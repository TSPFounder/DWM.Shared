// FusionRunnerSession.cs
// IFusionSession over FusionLibrary, instead of over a second HTTP client of our own.
//
// WHY THIS FILE REPLACED ONE THAT ALREADY WORKED
//
// FusionHttpSession posted {source: ...} to /scripts/execute on 127.0.0.1:18750 and read
// {success, output, error} back. FusionLibrary's FusionPythonHttpRunner does exactly that,
// and had done since before this project's Fusion work started. Two clients for one contract
// is one client too many: when the add-in moves to v2, one of them gets updated.
//
// This is the second time on this tool. A whole DwmBridge add-in was written for a port that
// already had one. Both times the cause was the same -- building before looking -- and both
// times the answer is to delete ours and keep theirs.
//
// WHAT THIS ADAPTER IS FOR, GIVEN THAT
//
// Not transport. FusionLibrary owns that. This layer holds the things DWM learned the hard way
// and FusionLibrary has no reason to know:
//
//   THE DIAGNOSIS. FusionPythonHttpRunner lets HttpRequestException and TaskCanceledException
//   escape. Bare, they say "connection refused" and "the operation was canceled", which is
//   true and useless. A refused connection cannot distinguish a closed Fusion from a running
//   Fusion without the add-in, and a timeout is almost always a modal dialog holding the main
//   thread. Those messages are kept verbatim from the client this replaces.
//
//   THE COMMAND SET. FusionStageService asks for "massProperties"; something has to turn that
//   into Python. Scripts DWM already owns go through as they are; anything expressible in the
//   IR goes through the generator, which is what makes sketches, extrudes and revolves
//   reachable from DWMStudio at all.
//
// WHAT IT DELIBERATELY DOES NOT DO is judge the answer. Refusing a zero mass, spotting an
// aggregate root, naming the wrong document -- all of that is FusionStageService's, and it
// works the same over MCP, which has no runner and no add-in.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CAD;              // ICADScriptFactory lives here, not in CAD.Scripting
using CAD.Scripting;
using Fusion.Application;
using Fusion.Scripting;

namespace DWM.Shared.Tooling.Cad
{
    /// <summary>What the "export" command needs. Mirrors the IR's ExportOp.</summary>
    public sealed class FusionExportRequest
    {
        public ExportFormat Format { get; init; } = ExportFormat.Step;

        /// <summary>Path ON THE MACHINE RUNNING FUSION, which is not necessarily this one.</summary>
        public string OutputPath { get; init; } = string.Empty;
    }

    public sealed class FusionRunnerSession : IFusionSession
    {
        private readonly FusionProtocol _protocol;
        private readonly ICADScriptRunner _runner;
        private readonly ICADScriptFactory _factory;
        private readonly Func<CancellationToken, Task<bool>> _ping;
        private readonly List<IDisposable> _owned = new();

        /// <summary>
        /// Null when the caller injected its own ping, which is what the tests do.
        ///
        /// Parameters need the REST surface rather than the script runner, so a session built
        /// for testing the script mapping has nothing to read them with. That is reported by
        /// GetParametersAsync rather than papered over: a fake that silently returned an empty
        /// parameter list would look exactly like a real document with no parameters.
        /// </summary>
        private readonly FusionApplication? _app;

        /// <summary>
        /// Connects to the add-in named by the protocol's base address.
        /// </summary>
        /// <param name="runner">
        /// Injectable so the mapping below can be tested without Fusion. Left null, a
        /// FusionPythonHttpRunner is built and owned.
        /// </param>
        public FusionRunnerSession(
            FusionProtocol? protocol = null,
            ICADScriptRunner? runner = null,
            ICADScriptFactory? factory = null,
            Func<CancellationToken, Task<bool>>? ping = null)
        {
            _protocol = protocol ?? new FusionProtocol();
            _factory = factory ?? new FusionPythonScriptFactory();

            // TRAILING SLASH MATTERS AND IS REMOVED. FusionLibrary builds its routes with
            // new Uri(base, "/scripts/execute"), where a leading slash on the relative part
            // discards any path on the base -- so a base of ".../api/" would silently lose
            // "api". Passing the authority alone keeps the two agreeing about what the base is.
            var baseUrl = _protocol.BaseAddress.GetLeftPart(UriPartial.Authority);

            if (runner is null)
            {
                var owned = new FusionPythonHttpRunner(baseUrl);
                _owned.Add(owned);
                _runner = owned;
            }
            else
            {
                _runner = runner;
            }

            if (ping is null)
            {
                // FusionApplication's ping is NOT a socket check: the add-in routes /ping
                // through the main thread and reads app.version, so it answers only when
                // Fusion is genuinely responsive. That is a stronger signal than a connect
                // test -- and it is also why it HANGS rather than failing fast while a modal
                // dialog is open.
                _app = new FusionApplication(baseUrl);
                _owned.Add(_app);
                _ping = _app.PingAsync;
            }
            else
            {
                _ping = ping;
            }
        }

        /// <summary>
        /// The active document's user parameters, over the add-in's REST surface.
        ///
        /// NOT THROUGH THE SCRIPT RUNNER, and that is the point. Everything else this session
        /// does is Python generated, sent, and executed; parameters have their own routes
        /// (GET /documents/active/parameters, PATCH .../{name}) that hand back typed values
        /// with no round trip through printed JSON.
        ///
        /// WHICH ALSO MEANS THEY ARE LESS PROVEN. /scripts/execute has been exercised against
        /// Fusion repeatedly; these routes have not been exercised at all. FusionParameterService
        /// is where that shows up as a message rather than as a stack trace.
        ///
        /// Returns null when this session has no REST client, which happens only when a caller
        /// injected its own ping.
        /// </summary>
        public async Task<ICADParameterCollection?> GetParametersAsync(CancellationToken ct = default)
        {
            if (_app is null) return null;

            var document = await _app.GetActiveDocumentAsync(ct).ConfigureAwait(false);
            return document.Parameters;
        }

        public async Task<bool> PingAsync(CancellationToken ct = default)
        {
            try
            {
                return await _ping(ct).ConfigureAwait(false);
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

            GeneratedPackage package;
            try
            {
                package = BuildPackage(command, payload);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                // Reported, not thrown. A caller asking for a command that does not exist is
                // in the same position as one whose script failed in Fusion, and gets the
                // answer the same way.
                return new FusionResponse { Ok = false, Error = ex.Message };
            }

            try
            {
                var result = await _runner.ExecuteAsync(package, ct).ConfigureAwait(false);

                // ScriptResult.Output IS WHAT THE SCRIPT PRINTED, already lifted out of the
                // {"success":..., "output":"..."} envelope by the runner. So the printed JSON
                // is parsed straight from it -- the nesting that made this delicate is handled
                // one layer down now.
                return new FusionResponse
                {
                    Ok = result.Success,
                    Json = TryParse(result.Output),
                    RawBody = result.Output ?? result.Error ?? string.Empty,
                    Error = result.Success
                        ? null
                        : result.Error ?? result.Output
                          ?? "The add-in reported failure without saying why."
                };
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return new FusionResponse { Ok = false, Error = Unreachable(command, ex) };
            }
        }

        /// <summary>
        /// Turns a DWM command into Python.
        ///
        /// TWO ROUTES ON PURPOSE. Scripts DWM already owns -- the mass-properties reader, the
        /// rotor build -- are complete modules and go through verbatim; rewriting them as IR
        /// would be a translation with nothing to gain. Everything else is built from the IR,
        /// which is what makes a sketch or a revolve reachable without a new hand-written
        /// script each time.
        /// </summary>
        private GeneratedPackage BuildPackage(string command, object? payload)
        {
            if (Is(command, _protocol.MassPropertiesCommand))
                return Verbatim("DWM_MassProperties", FusionScripts.MassProperties);

            if (Is(command, _protocol.BuildCommand))
                return Verbatim("DWM_BuildRotor", FusionScripts.BuildRotor(payload));

            if (Is(command, _protocol.OperationsCommand))
            {
                if (payload is not CadOperationSequence ops)
                    throw new ArgumentException(
                        $"'{command}' needs a CadOperationSequence as its payload; got " +
                        $"{payload?.GetType().Name ?? "null"}. Build one with " +
                        "new CadOperationSequence().Add(new CreateSketchOp { ... }).");

                return _factory.CreateScript(ops, new ScriptMetadata
                {
                    Name = "DWM_Operations",
                    Description = $"{ops.Operations.Count} operation(s) from DWMStudio"
                });
            }

            if (Is(command, _protocol.ExportCommand))
            {
                if (payload is not FusionExportRequest export)
                    throw new ArgumentException(
                        $"'{command}' needs a FusionExportRequest as its payload; got " +
                        $"{payload?.GetType().Name ?? "null"}.");

                if (string.IsNullOrWhiteSpace(export.OutputPath))
                    throw new ArgumentException(
                        "An export needs an OutputPath, on the machine running Fusion.");

                // THROUGH THE IR RATHER THAN THE /documents/active/export ROUTE. Both work;
                // this one keeps every write going through a single emitter, so an export that
                // follows a build is one script and one round trip instead of two.
                var ops = new CadOperationSequence().Add(new ExportOp
                {
                    Format = export.Format,
                    OutputPath = export.OutputPath,
                    Comment = "Export requested by DWMStudio"
                });

                return _factory.CreateScript(ops, new ScriptMetadata
                {
                    Name = "DWM_Export",
                    Description = $"Export to {export.Format}"
                });
            }

            throw new NotSupportedException(
                $"No command named '{command}'. Known: " +
                string.Join(", ", _protocol.CommandNames) + ".");
        }

        private static bool Is(string command, string name) =>
            string.Equals(command, name, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Wraps source DWM already owns as a package, with no generation in between.
        /// </summary>
        private static GeneratedPackage Verbatim(string name, string source)
        {
            var entry = name + ".py";
            return new GeneratedPackage
            {
                Language = ScriptLanguage.Python,
                Kind = ScriptKind.Script,
                Metadata = new ScriptMetadata { Name = name, Description = "DWM script" },
                EntryFile = entry,
                Files = new Dictionary<string, string> { [entry] = source }
            };
        }

        private static JsonElement? TryParse(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            try { return JsonDocument.Parse(text).RootElement.Clone(); }
            catch (JsonException) { return null; }
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
                ? $"Fusion accepted the connection but did not answer '{command}'.\n\n" +
                  "THE USUAL CAUSE IS A MODAL DIALOG. Fusion does not pump events while one is " +
                  "open, so the add-in's custom event never fires and every request queues " +
                  "behind it. Observed 2026-08-05: the SCRIPTS AND ADD-INS DIALOG ITSELF does " +
                  "this -- the window used to start the add-in blocks the add-in. So does " +
                  "WindTurbineBlade's own \"Rotor assembly built\" message box, which is why " +
                  "the build command calls build_rotor rather than run().\n\n" +
                  "Close any open Fusion dialog and retry. A connection that is REFUSED rather " +
                  "than hanging is a different fault: the add-in is not running at all."
                : $"Nothing answered at {_protocol.BaseAddress}.\n\n" +
                  "This CANNOT distinguish between Fusion not running and Fusion running " +
                  "without the DWM add-in loaded -- both refuse the connection the same way. " +
                  "Check Fusion is open, then Utilities > Scripts and Add-Ins > Add-Ins that " +
                  "the bridge is running.";
        }

        public void Dispose()
        {
            foreach (var d in _owned) d.Dispose();
            _owned.Clear();
        }
    }
}
