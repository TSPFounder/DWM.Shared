// FusionMcpHttpChannel.cs
// MCP over streamable HTTP, which is what Autodesk's Fusion server actually speaks.
//
// WHY THIS REPLACES THE STDIO CHANNEL AS THE DEFAULT
//
// The first MCP channel written for this project assumed stdio -- launch the server as a
// child process and talk over its pipes. That is how many local MCP servers run, and it was
// a reasonable assumption. It was also wrong here, and the evidence was sitting in Fusion's
// own Text Commands panel the whole time:
//
//     MCP - http://127.0.0.1:65517/mcp
//
// Confirmed by netstat on 2026-08-05: 127.0.0.1:65517 LISTENING. The server is ALREADY
// RUNNING and reachable over HTTP, which is better than the assumption in every way --
// DWMStudio launches nothing, owns no child process, and cannot leave one orphaned.
//
// TWO THINGS ABOUT STREAMABLE HTTP THAT WILL BITE IF SKIPPED
//
// 1. THE SESSION ID. The server may return an Mcp-Session-Id header on initialize, and every
//    later request has to carry it back. Drop it and the server answers 400 or 404 on the
//    second call -- which reads as a broken method name rather than a lost session.
//
// 2. THE REPLY MAY BE SSE, NOT JSON. A streamable-HTTP server is free to answer either
//    application/json or text/event-stream for the same request. An SSE body carries the
//    JSON-RPC message inside `data:` lines, so a client that parses the body as JSON gets a
//    parse error on a perfectly good response. Both shapes are handled below.
//
// Neither of these has been exercised against Autodesk's server yet. They are implemented
// from the transport spec, and the session header in particular is the sort of thing that
// works in testing -- one call -- and fails on the second.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DWM.Shared.Tooling.Cad
{
    public sealed class HttpJsonRpcChannel : IJsonRpcChannel
    {
        private readonly HttpClient _http;
        private readonly bool _ownsClient;
        private readonly Uri _endpoint;
        private readonly SemaphoreSlim _turn = new(1, 1);
        private int _nextId = 1;

        /// <summary>
        /// Returned by the server on initialize and echoed on every later request. Null until
        /// the server asks for one -- not every server uses sessions, and inventing a header
        /// it did not ask for is its own way to get a 400.
        /// </summary>
        public string? SessionId { get; private set; }

        public HttpJsonRpcChannel(Uri endpoint, HttpClient? http = null, TimeSpan? timeout = null)
        {
            _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));

            if (http is not null)
            {
                _http = http;
                _ownsClient = false;
            }
            else
            {
                _http = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(120) };
                _ownsClient = true;
            }
        }

        public async Task<JsonElement> SendAsync(string method, object? parameters, CancellationToken ct)
        {
            // ONE CALL AT A TIME, matching the stdio channel. Ids correlate replies, but a
            // client that only ever has one request outstanding does not need a correlation
            // table, and a semaphore cannot get the bookkeeping wrong.
            await _turn.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var id = _nextId++;
                using var reply = await PostAsync(
                    new { jsonrpc = "2.0", id, method, @params = parameters }, ct)
                    .ConfigureAwait(false);

                CaptureSessionId(reply);

                var body = await reply.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                if (!reply.IsSuccessStatusCode)
                    throw new InvalidOperationException(
                        $"HTTP {(int)reply.StatusCode} from the MCP server for '{method}'. {Trim(body)}");

                var message = ParseMessage(body, reply.Content.Headers.ContentType?.MediaType);

                if (message.TryGetProperty("error", out var error))
                    throw new InvalidOperationException("MCP error: " + error.ToString());

                return message.TryGetProperty("result", out var result) ? result.Clone() : default;
            }
            finally
            {
                _turn.Release();
            }
        }

        public async Task NotifyAsync(string method, object? parameters, CancellationToken ct)
        {
            // No id, so no reply is expected. The response is still read far enough to pick up
            // a session id, because a server is entitled to establish one here.
            using var reply = await PostAsync(
                new { jsonrpc = "2.0", method, @params = parameters }, ct).ConfigureAwait(false);
            CaptureSessionId(reply);
        }

        private async Task<HttpResponseMessage> PostAsync(object message, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(message), Encoding.UTF8, "application/json")
            };

            // BOTH ACCEPTED, because the server chooses. Advertising only application/json can
            // get a 406 from a server that intended to stream.
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            if (SessionId is not null)
                request.Headers.TryAddWithoutValidation("Mcp-Session-Id", SessionId);

            return await _http.SendAsync(request, ct).ConfigureAwait(false);
        }

        private void CaptureSessionId(HttpResponseMessage reply)
        {
            if (reply.Headers.TryGetValues("Mcp-Session-Id", out var values))
            {
                var id = values.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(id)) SessionId = id;
            }
        }

        /// <summary>
        /// Read one JSON-RPC message out of either a JSON body or an SSE stream.
        ///
        /// SSE frames look like `event: message` then `data: {...}`, blank-line separated, and
        /// a single logical message may be split across several data lines which are joined
        /// with newlines. The LAST parseable data payload wins: a server may send progress
        /// notifications before the real reply, and the reply is what was asked for.
        /// </summary>
        internal static JsonElement ParseMessage(string body, string? mediaType)
        {
            var looksLikeSse = (mediaType?.Contains("event-stream", StringComparison.OrdinalIgnoreCase) ?? false)
                               || body.StartsWith("event:", StringComparison.Ordinal)
                               || body.StartsWith("data:", StringComparison.Ordinal);

            if (!looksLikeSse)
                return JsonDocument.Parse(body).RootElement.Clone();

            JsonElement? last = null;
            var buffer = new List<string>();

            void Flush()
            {
                if (buffer.Count == 0) return;
                var payload = string.Join("\n", buffer);
                buffer.Clear();
                try
                {
                    var element = JsonDocument.Parse(payload).RootElement.Clone();
                    // Keep only messages that answer a request. A notification has no id and
                    // no result, and letting one overwrite the reply would return nothing.
                    if (element.TryGetProperty("result", out _) || element.TryGetProperty("error", out _))
                        last = element;
                }
                catch (JsonException) { /* keep-alive or comment; not a message */ }
            }

            foreach (var raw in body.Split('\n'))
            {
                var line = raw.TrimEnd('\r');

                if (line.Length == 0) { Flush(); continue; }
                if (line.StartsWith(":", StringComparison.Ordinal)) continue;   // comment/heartbeat

                if (line.StartsWith("data:", StringComparison.Ordinal))
                    buffer.Add(line[5..].TrimStart());
            }
            Flush();

            return last ?? throw new InvalidOperationException(
                "The MCP server sent an event stream with no JSON-RPC reply in it:\n" + Trim(body));
        }

        private static string Trim(string s) => s.Length <= 400 ? s : s[..400] + "...";

        public void Dispose()
        {
            if (_ownsClient) _http.Dispose();
            _turn.Dispose();
        }
    }
}
