// FusionMcpSession.cs
// The second way into Fusion: an MCP server, instead of the DwmBridge add-in.
//
// WHY TWO TRANSPORTS RATHER THAN A CHOICE BETWEEN THEM
//
// Autodesk ships a Fusion connector for Claude Desktop, which is an MCP server talking to
// Fusion on the same machine. A DWM user who already has it working should not have to
// install an add-in as well; a user who does not have it, or who wants DWMStudio running
// unattended with no Claude in the loop, needs the bridge. Neither is a subset of the other:
//
//   BRIDGE   Our code, our command set, runs headless, needs the add-in installed.
//   MCP      Autodesk's code, their command set, no add-in, but a server process to launch.
//
// Both sit behind IFusionSession, so FusionStageService -- including its refusal to believe
// a zero mass -- is identical either way. That interface is the whole point of having built
// the client half first.
//
// WHAT IS UNVERIFIED HERE, STATED PLAINLY
//
// The tool NAMES Autodesk's server exposes have not been read, and neither has the command
// that launches it standalone. Both are DATA in FusionMcpOptions for the same reason
// FemapApiNames was: being wrong should cost one object, not a rebuild. It is also entirely
// possible that the server is only launchable by Claude Desktop, in which case this transport
// is unusable and the bridge is the answer -- that is a question for the machine that has it.
//
// THE MCP TRAP, WHICH IS THIS PROJECT'S OLDEST BUG IN A NEW COAT
//
// A tool call that FAILS does not come back as a JSON-RPC error. It comes back as a perfectly
// ordinary JSON-RPC SUCCESS whose result carries `isError: true`. A client that checks only
// the protocol layer sees every failure as a success -- exactly like FEMAP returning 0 without
// throwing, MYSTRAN exiting 0 after a FATAL, and an HTTP 200 carrying {"ok": false}. Four
// tools, four different places to hide the verdict. Both layers are checked below.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DWM.Shared.Tooling.Cad
{
    /// <summary>
    /// One JSON-RPC round trip. An interface so the MCP logic is testable without spawning
    /// anything -- the framing and the process are separable, and only one of them is
    /// interesting.
    /// </summary>
    public interface IJsonRpcChannel : IDisposable
    {
        Task<JsonElement> SendAsync(string method, object? parameters, CancellationToken ct);

        /// <summary>Fire-and-forget. MCP requires `notifications/initialized` after handshake.</summary>
        Task NotifyAsync(string method, object? parameters, CancellationToken ct);
    }

    /// <summary>
    /// How to reach Autodesk's Fusion MCP server, and what its tools are called.
    ///
    /// EVERY VALUE IS A GUESS. Nothing here has been checked against the server, because the
    /// machine that has it is not the machine this was written on.
    /// </summary>
    public sealed class FusionMcpOptions
    {
        /// <summary>Executable that speaks MCP on stdio. Empty means "not configured".</summary>
        public string ServerCommand { get; init; } = string.Empty;

        public IReadOnlyList<string> ServerArguments { get; init; } = Array.Empty<string>();

        /// <summary>
        /// DWM's command names mapped onto the server's tool names.
        ///
        /// The keys are ours (see FusionProtocol) and stay put; the values are Autodesk's and
        /// will be wrong until someone reads `tools/list` from a running server. That listing
        /// is exactly what <see cref="FusionMcpSession.ListToolsAsync"/> exists to fetch.
        /// </summary>
        public IReadOnlyDictionary<string, string> ToolNames { get; init; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["build"] = "run_script",
                ["massProperties"] = "get_physical_properties",
                ["export"] = "export_design"
            };

        public string ProtocolVersion { get; init; } = "2024-11-05";
        public string ClientName { get; init; } = "DWMStudio";
        public string ClientVersion { get; init; } = "1.0.0";
    }

    public sealed class FusionMcpSession : IFusionSession
    {
        private readonly IJsonRpcChannel _channel;
        private readonly FusionMcpOptions _options;
        private bool _initialised;

        public FusionMcpSession(IJsonRpcChannel channel, FusionMcpOptions? options = null)
        {
            _channel = channel ?? throw new ArgumentNullException(nameof(channel));
            _options = options ?? new FusionMcpOptions();
        }

        /// <summary>
        /// MCP has no ping. Reaching the server at all is the liveness signal, so the handshake
        /// stands in for one -- which is honest, because a server that will not initialise is
        /// no more usable than one that is not there.
        /// </summary>
        public async Task<bool> PingAsync(CancellationToken ct = default)
        {
            try
            {
                await EnsureInitialisedAsync(ct).ConfigureAwait(false);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>The server's actual tool names. The only way to correct ToolNames honestly.</summary>
        public async Task<IReadOnlyList<string>> ListToolsAsync(CancellationToken ct = default)
        {
            await EnsureInitialisedAsync(ct).ConfigureAwait(false);
            var result = await _channel.SendAsync("tools/list", new { }, ct).ConfigureAwait(false);

            if (!result.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();

            return tools.EnumerateArray()
                .Where(t => t.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                .Select(t => t.GetProperty("name").GetString()!)
                .ToList();
        }

        public async Task<FusionResponse> InvokeAsync(string command, object? payload = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(command))
                throw new ArgumentException("A command is required.", nameof(command));

            if (!_options.ToolNames.TryGetValue(command, out var toolName))
            {
                return new FusionResponse
                {
                    Ok = false,
                    Error = $"No MCP tool is mapped to '{command}'. Mapped: " +
                            string.Join(", ", _options.ToolNames.Keys) + ".\n\n" +
                            "Run ListToolsAsync against the server and correct " +
                            "FusionMcpOptions.ToolNames -- the names here are guesses."
                };
            }

            try
            {
                await EnsureInitialisedAsync(ct).ConfigureAwait(false);

                var result = await _channel.SendAsync("tools/call", new
                {
                    name = toolName,
                    arguments = payload ?? new { }
                }, ct).ConfigureAwait(false);

                var text = ExtractText(result);

                // THE FAILURE FLAG LIVES IN THE RESULT, NOT IN THE PROTOCOL. A failed tool
                // call is a successful JSON-RPC response carrying isError. Checking only the
                // transport would read every failure as a success.
                var isError = result.TryGetProperty("isError", out var err) &&
                              err.ValueKind == JsonValueKind.True;

                JsonElement? json = null;
                try { json = JsonDocument.Parse(text).RootElement.Clone(); }
                catch (JsonException) { /* prose, not JSON; RawBody carries it */ }

                return new FusionResponse
                {
                    Ok = !isError,
                    Json = json,
                    RawBody = text,
                    Error = isError
                        ? (string.IsNullOrWhiteSpace(text)
                            ? $"The MCP tool '{toolName}' reported an error without saying why."
                            : text)
                        : null
                };
            }
            catch (Exception ex)
            {
                return new FusionResponse
                {
                    Ok = false,
                    Error = $"The Fusion MCP server did not answer '{toolName}': {ex.Message}\n\n" +
                            "If this server is only launchable by Claude Desktop, use the " +
                            "DwmBridge add-in instead -- DWMStudio needs a transport it can " +
                            "start itself."
                };
            }
        }

        private async Task EnsureInitialisedAsync(CancellationToken ct)
        {
            if (_initialised) return;

            await _channel.SendAsync("initialize", new
            {
                protocolVersion = _options.ProtocolVersion,
                capabilities = new { },
                clientInfo = new { name = _options.ClientName, version = _options.ClientVersion }
            }, ct).ConfigureAwait(false);

            // REQUIRED BY THE SPEC, and easy to omit because nothing answers it. A server that
            // never receives it may refuse every subsequent call, which then looks like a
            // broken tool name rather than a missing handshake step.
            await _channel.NotifyAsync("notifications/initialized", new { }, ct)
                .ConfigureAwait(false);

            _initialised = true;
        }

        /// <summary>
        /// Flatten MCP's content blocks into one string.
        ///
        /// A tool result is a LIST of typed blocks, not a payload. Text blocks are joined;
        /// anything else (images, embedded resources) is skipped rather than stringified into
        /// noise the parser would then fail on.
        /// </summary>
        private static string ExtractText(JsonElement result)
        {
            if (!result.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.Array)
            {
                return result.ValueKind == JsonValueKind.Undefined ? string.Empty : result.ToString();
            }

            var parts = content.EnumerateArray()
                .Where(b => b.TryGetProperty("type", out var t) && t.GetString() == "text")
                .Where(b => b.TryGetProperty("text", out var x) && x.ValueKind == JsonValueKind.String)
                .Select(b => b.GetProperty("text").GetString()!)
                .ToList();

            return string.Join("\n", parts);
        }

        public void Dispose() => _channel.Dispose();
    }

    /// <summary>Which way into Fusion a project uses.</summary>
    public enum FusionTransport
    {
        /// <summary>The DwmBridge add-in over localhost HTTP. Runs unattended.</summary>
        Bridge,

        /// <summary>Autodesk's Fusion MCP server. No add-in, but a server to launch.</summary>
        Mcp
    }

    public static class FusionSessionFactory
    {
        /// <summary>
        /// Build the session a project has asked for.
        ///
        /// PER PROJECT, NOT PER INSTALL. One user may have Autodesk's connector working and
        /// another may not, and the same DWMStudio has to serve both. The choice belongs
        /// beside the other per-project tool settings -- the same argument that put a MATLAB
        /// ProgID in WorldProjectRecord.ToolVersions rather than in a constant.
        /// </summary>
        public static Func<IFusionSession> For(
            FusionTransport transport,
            FusionProtocol? protocol = null,
            FusionMcpOptions? mcp = null,
            Func<FusionMcpOptions, IJsonRpcChannel>? channelFactory = null)
        {
            switch (transport)
            {
                case FusionTransport.Bridge:
                    return () => new FusionHttpSession(protocol);

                case FusionTransport.Mcp:
                    var options = mcp ?? new FusionMcpOptions();
                    if (channelFactory is null && string.IsNullOrWhiteSpace(options.ServerCommand))
                    {
                        throw new InvalidOperationException(
                            "The MCP transport needs either a ServerCommand to launch or a " +
                            "channel factory. Neither was supplied.\n\n" +
                            "If Autodesk's Fusion server cannot be started outside Claude " +
                            "Desktop, use FusionTransport.Bridge -- DWMStudio has to be able " +
                            "to start its own transport.");
                    }
                    return () => new FusionMcpSession(
                        channelFactory is null
                            ? new StdioJsonRpcChannel(options)
                            : channelFactory(options),
                        options);

                default:
                    throw new ArgumentOutOfRangeException(nameof(transport));
            }
        }
    }

    /// <summary>
    /// JSON-RPC over a child process's stdin/stdout, which is how a local MCP server runs.
    ///
    /// UNTESTED AGAINST A REAL SERVER. The framing is newline-delimited JSON, one object per
    /// line, which is what MCP's stdio transport specifies -- but no Autodesk server has
    /// answered it here. Kept deliberately thin: everything worth testing lives in
    /// FusionMcpSession, behind IJsonRpcChannel.
    /// </summary>
    public sealed class StdioJsonRpcChannel : IJsonRpcChannel
    {
        private readonly Process _process;
        private readonly SemaphoreSlim _turn = new(1, 1);
        private int _nextId = 1;

        public StdioJsonRpcChannel(FusionMcpOptions options)
        {
            var info = new ProcessStartInfo
            {
                FileName = options.ServerCommand,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in options.ServerArguments) info.ArgumentList.Add(arg);

            _process = Process.Start(info)
                       ?? throw new InvalidOperationException(
                           $"Could not start the MCP server '{options.ServerCommand}'.");
        }

        public async Task<JsonElement> SendAsync(string method, object? parameters, CancellationToken ct)
        {
            // ONE CALL AT A TIME. Responses are correlated by id, but reading them in order
            // from a single pipe means an interleaved second request would be matched to the
            // first reply. A semaphore is cheaper than a correlation table for a client that
            // makes one call at a time anyway.
            await _turn.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var id = _nextId++;
                await WriteAsync(new { jsonrpc = "2.0", id, method, @params = parameters }, ct)
                    .ConfigureAwait(false);

                while (true)
                {
                    var line = await _process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);
                    if (line is null)
                        throw new InvalidOperationException(
                            "The MCP server closed its output. Stderr:\n" +
                            await _process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false));

                    if (string.IsNullOrWhiteSpace(line)) continue;

                    JsonElement message;
                    try { message = JsonDocument.Parse(line).RootElement.Clone(); }
                    catch (JsonException) { continue; }   // servers do log to stdout sometimes

                    // Skip notifications and anything answering a different request.
                    if (!message.TryGetProperty("id", out var mid) ||
                        mid.ValueKind != JsonValueKind.Number ||
                        mid.GetInt32() != id) continue;

                    if (message.TryGetProperty("error", out var error))
                        throw new InvalidOperationException("MCP error: " + error.ToString());

                    return message.TryGetProperty("result", out var result)
                        ? result.Clone()
                        : default;
                }
            }
            finally
            {
                _turn.Release();
            }
        }

        public Task NotifyAsync(string method, object? parameters, CancellationToken ct) =>
            WriteAsync(new { jsonrpc = "2.0", method, @params = parameters }, ct);

        private async Task WriteAsync(object message, CancellationToken ct)
        {
            var json = JsonSerializer.Serialize(message);
            await _process.StandardInput.WriteLineAsync(json.AsMemory(), ct).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
        }

        public void Dispose()
        {
            try
            {
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            }
            catch (Exception) { }
            _process.Dispose();
            _turn.Dispose();
        }
    }
}
