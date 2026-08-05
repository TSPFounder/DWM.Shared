// FusionSession.cs
// Fusion 360 over a localhost HTTP add-in.
//
// WHY THIS TOOL IS DIFFERENT FROM EVERY OTHER ONE IN THE REGISTRY
//
// MATLAB, FEMAP and UModel all ship a COM automation surface. The work there was finding the
// right calls in something that already existed. FUSION SHIPS NO SUCH SURFACE: its API is
// Python running INSIDE Fusion, reachable only from Fusion's own Scripts and Add-Ins. Nothing
// outside the process can reach in.
//
// So DWM has to supply the server as well as the client. `InteractiveHttp` and port 18750 in
// the registry are not describing something that exists -- they describe an add-in that must
// be written, installed under
// %APPDATA%\Autodesk\Autodesk Fusion 360\API\AddIns\, and set to run on startup.
//
// THE ADD-IN ALREADY EXISTS, AND THIS IS PINNED TO IT.
//
// TSPFounder/DWM-Fusion-AddIn v0.2.0, installed at
// %APPDATA%\Autodesk\Autodesk Fusion 360\API\AddIns\DWM_FusionAddIn\. It listens on
// 127.0.0.1:18750 and marshals every request onto Fusion's main thread with a custom event,
// because the Fusion API is not thread-safe and an HTTP handler is on the wrong thread.
//
// This file was first written against a GUESSED protocol, and a second bridge was written to
// serve it. Both were wrong to exist: the add-in was already installed, already on that port,
// and already correct about the threading. Two servers on 18750 would have meant one silently
// failing to bind and intermittent "nothing answered" that reads like the marshalling bug.
// The duplicate is deleted; the contract below is read from the add-in's source.
//
// Still DATA rather than literals, for a reason that has not changed: the add-in is versioned,
// its contract is labelled v1, and v2 should cost one object here rather than an edit through
// this file.
//
// WHAT CONTRACT v1 DOES NOT HAVE: any mass-properties route. That rides on
// /scripts/execute instead -- send Python, read what it printed. See FusionScripts.
//
// ONE THING THE TRANSPORT CANNOT TELL YOU, stated here because it will look like a bug.
// A closed Fusion and a running Fusion without the add-in loaded are INDISTINGUISHABLE from
// out here -- both are a refused connection on 18750. The failure message says so rather than
// picking one and sounding certain.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DWM.Shared.Tooling.Cad
{
    /// <summary>One reply from the add-in. Never throws for a Fusion-side error; carries it.</summary>
    public sealed class FusionResponse
    {
        public bool Ok { get; init; }

        /// <summary>Parsed body when the add-in returned JSON, null otherwise.</summary>
        public JsonElement? Json { get; init; }

        /// <summary>Body as received. Kept whole because a truncated error is a wasted trip.</summary>
        public string RawBody { get; init; } = string.Empty;

        /// <summary>Fusion-side error text, or the transport failure when the call never landed.</summary>
        public string? Error { get; init; }

        public int? HttpStatus { get; init; }

        public override string ToString() => Ok ? $"ok: {RawBody}" : $"failed: {Error}";
    }

    /// <summary>One command mapped onto DWM_FusionAddIn's contract v1.</summary>
    public sealed class FusionCommand
    {
        /// <summary>Route, relative to the base address. No leading slash.</summary>
        public string Path { get; init; } = string.Empty;

        /// <summary>Turns the caller's payload into the body this route expects.</summary>
        public Func<object?, object?> BuildBody { get; init; } = payload => payload ?? new { };

        /// <summary>
        /// Pulls the actual result out of the reply.
        ///
        /// NEEDED BECAUSE /scripts/execute NESTS IT. That route answers
        /// {"success": true, "output": "&lt;whatever the script printed&gt;"}, so a script
        /// that prints JSON has its payload inside a STRING one level down. Reading the
        /// envelope as the result would hand back a body with no components in it and no
        /// indication why.
        /// </summary>
        public Func<JsonElement, JsonElement?> ReadResult { get; init; } = body => body;
    }

    /// <summary>
    /// How a command becomes an HTTP request, against DWM_FusionAddIn contract v1.
    ///
    /// PINNED FROM THE ADD-IN'S SOURCE on 2026-08-05, not guessed: TSPFounder/DWM-Fusion-AddIn
    /// v0.2.0. Still data rather than literals, because the add-in is versioned and its
    /// contract will move.
    ///
    /// THE ENVELOPE IS "success", NOT "ok". An earlier draft of this file assumed "ok" -- a
    /// perfectly reasonable guess that would have read every failure as a success, because a
    /// missing flag is treated as "worked". That is the whole reason these are data.
    /// </summary>
    public sealed class FusionProtocol
    {
        /// <summary>Where the add-in listens. Matches PORT in DWM_FusionAddIn.py.</summary>
        public Uri BaseAddress { get; init; } = new("http://127.0.0.1:18750/");

        /// <summary>Liveness. Answered without touching the Fusion API.</summary>
        public string PingPath { get; init; } = "ping";

        /// <summary>
        /// The add-in's own success flag, which is NOT the HTTP status.
        ///
        /// It answers 200 with {"success": false} for a script that raised -- an ordinary
        /// Python handler catching its own exception. Reading only the transport would call
        /// that a success, which is this project's oldest bug in its fifth costume.
        /// </summary>
        public Func<JsonElement, bool> ReadOk { get; init; } = json =>
            !json.TryGetProperty("success", out var ok) || ok.ValueKind != JsonValueKind.False;

        public Func<JsonElement, string?> ReadError { get; init; } = json =>
            json.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String
                ? e.GetString()
                : null;

        public IReadOnlyDictionary<string, FusionCommand> Commands { get; init; } =
            new Dictionary<string, FusionCommand>(StringComparer.OrdinalIgnoreCase)
            {
                // NO NATIVE MASS-PROPERTIES ROUTE EXISTS in contract v1, so this rides on
                // /scripts/execute: send Python that prints JSON, read it back out of
                // "output". Exactly the shape MatlabStageService uses -- generate a guarded
                // command, run it in the tool, read a value back -- because the constraint is
                // the same one: the tool will not hand you a typed result, so you ask it to
                // print one.
                ["massProperties"] = new FusionCommand
                {
                    Path = "scripts/execute",
                    BuildBody = _ => new { source = FusionScripts.MassProperties },
                    ReadResult = ReadPrintedJson
                },

                ["build"] = new FusionCommand
                {
                    Path = "scripts/execute",
                    BuildBody = payload => new { source = FusionScripts.BuildRotor(payload) },
                    ReadResult = ReadPrintedJson
                },

                ["export"] = new FusionCommand
                {
                    Path = "documents/active/export"
                    // Body passes straight through: the route wants {format, outputPath}.
                },

                ["save"] = new FusionCommand { Path = "documents/active/save" },
                ["newDocument"] = new FusionCommand { Path = "documents" },
                ["openDocument"] = new FusionCommand { Path = "documents/open" }
            };

        /// <summary>
        /// Parse the JSON a script printed, out of the "output" string.
        ///
        /// Returns null when it is not JSON -- a traceback, or a script that printed nothing.
        /// Null is honest: the caller then reports the raw body rather than a parse crash.
        /// </summary>
        private static JsonElement? ReadPrintedJson(JsonElement body)
        {
            if (!body.TryGetProperty("output", out var output) ||
                output.ValueKind != JsonValueKind.String)
                return null;

            var text = output.GetString();
            if (string.IsNullOrWhiteSpace(text)) return null;

            try { return JsonDocument.Parse(text).RootElement.Clone(); }
            catch (JsonException) { return null; }
        }
    }

    public interface IFusionSession : IDisposable
    {
        /// <summary>True when the add-in answered. Does not prove Fusion has a document open.</summary>
        Task<bool> PingAsync(CancellationToken ct = default);

        Task<FusionResponse> InvokeAsync(string command, object? payload = null,
            CancellationToken ct = default);
    }

    public sealed class FusionHttpSession : IFusionSession
    {
        private readonly HttpClient _http;
        private readonly bool _ownsClient;
        private readonly FusionProtocol _protocol;

        public FusionProtocol Protocol => _protocol;

        /// <param name="timeout">
        /// Short by default. A Fusion operation that has not answered in thirty seconds is
        /// far more likely to be a add-in deadlocked on the wrong thread than a slow model --
        /// see the threading note below.
        /// </param>
        public FusionHttpSession(FusionProtocol? protocol = null, HttpClient? http = null,
            TimeSpan? timeout = null)
        {
            _protocol = protocol ?? new FusionProtocol();

            if (http is not null)
            {
                _http = http;
                _ownsClient = false;
            }
            else
            {
                _http = new HttpClient { BaseAddress = _protocol.BaseAddress };
                _ownsClient = true;
            }

            if (_ownsClient) _http.Timeout = timeout ?? TimeSpan.FromSeconds(30);
        }

        public async Task<bool> PingAsync(CancellationToken ct = default)
        {
            try
            {
                using var reply = await _http.GetAsync(_protocol.PingPath, ct).ConfigureAwait(false);
                return reply.IsSuccessStatusCode;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return false;
            }
        }

        public async Task<FusionResponse> InvokeAsync(string command, object? payload = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(command))
                throw new ArgumentException("A command is required.", nameof(command));

            if (!_protocol.Commands.TryGetValue(command, out var spec))
            {
                return new FusionResponse
                {
                    Ok = false,
                    Error = $"No route is mapped to '{command}'. Mapped: " +
                            string.Join(", ", _protocol.Commands.Keys) + "."
                };
            }

            try
            {
                using var reply = await _http
                    .PostAsJsonAsync(spec.Path, spec.BuildBody(payload), ct)
                    .ConfigureAwait(false);

                var body = await reply.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                JsonElement? envelope = null;
                try { envelope = JsonDocument.Parse(body).RootElement.Clone(); }
                catch (JsonException) { /* not JSON; RawBody still carries it */ }

                // BOTH LAYERS CHECKED. The HTTP status says the request was routed; the
                // add-in's own "success" flag says whether Fusion did the thing. A 200
                // carrying {"success": false} is an ordinary Python handler catching its own
                // exception, and /scripts/execute answers exactly that way for a script that
                // raised.
                var addInOk = envelope is null || _protocol.ReadOk(envelope.Value);
                var addInError = envelope is null ? null : _protocol.ReadError(envelope.Value);

                // Unwrapped AFTER the flags are read, because the envelope carries the verdict
                // and the payload carries the answer -- reading either one for both loses
                // something.
                var result = envelope is null ? null : spec.ReadResult(envelope.Value);

                return new FusionResponse
                {
                    Ok = reply.IsSuccessStatusCode && addInOk,
                    Json = result,
                    RawBody = body,
                    HttpStatus = (int)reply.StatusCode,
                    Error = reply.IsSuccessStatusCode
                        ? (addInOk ? null : addInError ?? "The add-in reported failure without saying why.")
                        : $"HTTP {(int)reply.StatusCode} from the Fusion add-in. {addInError ?? body}"
                };
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return new FusionResponse
                {
                    Ok = false,
                    Error = Unreachable(command, spec.Path, ex)
                };
            }
        }

        /// <summary>
        /// The message for a call that never landed.
        ///
        /// IT REFUSES TO GUESS BETWEEN THE TWO CAUSES, because from out here they are the same
        /// event: a closed Fusion and a running Fusion without the add-in loaded both refuse
        /// the connection identically. Naming one would be a coin toss printed as a diagnosis,
        /// and the registry already records this as the tool's known limitation.
        /// </summary>
        private string Unreachable(string command, string path, Exception ex)
        {
            var timedOut = ex is TaskCanceledException;

            return timedOut
                ? $"Fusion did not answer '{command}' within the timeout.\n\n" +
                  "The usual cause is the add-in calling the Fusion API from its HTTP thread. " +
                  "That API is NOT thread-safe: work has to be marshalled onto Fusion's main " +
                  "thread with a custom event, and an add-in that skips this hangs or crashes " +
                  "intermittently rather than failing cleanly."
                : $"Nothing answered at {_protocol.BaseAddress}{path}.\n\n" +
                  "This CANNOT distinguish between Fusion not running and Fusion running " +
                  "without the DWM add-in loaded -- both refuse the connection the same way. " +
                  "Check Fusion is open, then Utilities > Scripts and Add-Ins > Add-Ins that " +
                  "the bridge is running.";
        }

        public void Dispose()
        {
            if (_ownsClient) _http.Dispose();
        }
    }

    public sealed class FusionSessionException : Exception
    {
        public FusionSessionException(string message) : base(message) { }
        public FusionSessionException(string message, Exception inner) : base(message, inner) { }
    }
}
