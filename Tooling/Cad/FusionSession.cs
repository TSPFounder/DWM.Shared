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
// THE PROTOCOL HERE IS UNVERIFIED, AND IS THEREFORE DATA.
//
// Every path, field name and command below is a GUESS. FusionProtocol exists so correcting
// them is one object at a call site rather than an edit through this file -- the same shape
// FemapApiNames took, for the same reason, and it was right: the FEMAP names were wrong on
// first contact and cost one line to fix.
//
// WHAT THE EXISTING FUSION PYTHON ACTUALLY IS (read 2026-08-05)
//
// WindTurbineBlade.py is a GENERATIVE SCRIPT, not a server. It builds the rotor from the BOM
// and is run by hand from Fusion's Scripts and Add-Ins dialog. There is no HTTP surface to
// talk to, so nothing below is wrong so much as PREMATURE -- the bridge add-in still has to
// be written, and its job is to invoke that script rather than to reimplement it.
//
// It is well built for that: the adsk imports are guarded, so the pure-geometry half runs
// under an ordinary interpreter and verify_blade_geometry.py exercises 26 checks against it
// with no Fusion at all. That is the same split as DWM.Shared against DWMStudio, arrived at
// independently.
//
// THREE THINGS IN THAT SCRIPT BLOCK AUTOMATION, and they are why the default command below
// is "build" rather than something finer-grained:
//
//   1. It ends in ui.messageBox(...), which is MODAL. Any caller that is not a human clicking
//      OK waits forever. This is the likeliest cause of the timeout message further down.
//   2. It calls documents.add() on every run, so each build leaves another open document.
//      Fusion's free tier caps active documents at 10 -- see the note in FusionStageService
//      about what happens to mass properties past that point. A build loop reaches it.
//   3. Its log is assembled for a message box rather than returned, so a bridge has nothing
//      to hand back but "it did not throw".
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

    /// <summary>
    /// How a command becomes an HTTP request.
    ///
    /// DATA, NOT LITERALS, because none of it has been checked against the add-in that will
    /// answer it. The default assumes the plainest possible shape -- POST the payload as JSON
    /// to /<c>command</c> -- which is a guess, not a specification.
    /// </summary>
    public sealed class FusionProtocol
    {
        /// <summary>Where the add-in listens. Must match what the Python binds.</summary>
        public Uri BaseAddress { get; init; } = new("http://127.0.0.1:18750/");

        /// <summary>Liveness endpoint. Answered without touching the Fusion API.</summary>
        public string PingPath { get; init; } = "ping";

        /// <summary>Builds the request path for a command. Default: the command is the path.</summary>
        public Func<string, string> PathFor { get; init; } = command => command;

        /// <summary>
        /// Reads the add-in's own success flag out of a JSON reply.
        ///
        /// SEPARATE FROM THE HTTP STATUS ON PURPOSE. A Python handler that catches an
        /// exception and returns 200 with {"ok": false} is the normal shape, and treating
        /// 200 as success would be this project's oldest mistake in a new place -- the status
        /// living somewhere other than where a caller would naturally look.
        /// </summary>
        public Func<JsonElement, bool> ReadOk { get; init; } = json =>
            !json.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.False;

        /// <summary>Reads the add-in's error text out of a JSON reply, when it has one.</summary>
        public Func<JsonElement, string?> ReadError { get; init; } = json =>
            json.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String
                ? e.GetString()
                : null;

        /// <summary>
        /// Command names. Guesses. The add-in decides these, not this file.
        ///
        /// "build" is first because the existing Python is a GENERATIVE script: the unit of
        /// work for this project is "construct the rotor from CONFIG", not "query whatever
        /// document happens to be open". Mass properties are what comes after a build, not
        /// instead of one.
        /// </summary>
        public string BuildCommand { get; init; } = "build";
        public string MassPropertiesCommand { get; init; } = "massProperties";
        public string ExportCommand { get; init; } = "export";
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

            var path = _protocol.PathFor(command);

            try
            {
                using var reply = await _http
                    .PostAsJsonAsync(path, payload ?? new { }, ct)
                    .ConfigureAwait(false);

                var body = await reply.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                JsonElement? json = null;
                try { json = JsonDocument.Parse(body).RootElement.Clone(); }
                catch (JsonException) { /* not JSON; RawBody still carries it */ }

                // BOTH LAYERS CHECKED. The HTTP status says the request was handled; the
                // add-in's own flag says whether Fusion did the thing. A 200 carrying
                // {"ok": false} is an ordinary Python handler catching its own exception.
                var addInOk = json is null || _protocol.ReadOk(json.Value);
                var addInError = json is null ? null : _protocol.ReadError(json.Value);

                return new FusionResponse
                {
                    Ok = reply.IsSuccessStatusCode && addInOk,
                    Json = json,
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
                    Error = Unreachable(command, ex)
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
        private string Unreachable(string command, Exception ex)
        {
            var timedOut = ex is TaskCanceledException;

            return timedOut
                ? $"Fusion did not answer '{command}' within the timeout.\n\n" +
                  "The usual cause is the add-in calling the Fusion API from its HTTP thread. " +
                  "That API is NOT thread-safe: work has to be marshalled onto Fusion's main " +
                  "thread with a custom event, and an add-in that skips this hangs or crashes " +
                  "intermittently rather than failing cleanly."
                : $"Nothing answered at {_protocol.BaseAddress}{_protocol.PathFor(command)}.\n\n" +
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
