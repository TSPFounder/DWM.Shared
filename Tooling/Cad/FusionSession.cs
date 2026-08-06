// FusionSession.cs
// The shape of a Fusion conversation, independent of who carries it.
//
// WHY THIS TOOL IS DIFFERENT FROM EVERY OTHER ONE IN THE REGISTRY
//
// MATLAB, FEMAP and UModel all ship a COM automation surface. The work there was finding the
// right calls in something that already existed. FUSION SHIPS NO SUCH SURFACE: its API is
// Python running INSIDE Fusion, reachable only from Fusion's own Scripts and Add-Ins. Nothing
// outside the process can reach in.
//
// So DWM supplies the server as well as the client -- and it already did, twice over.
//
// WHAT USED TO BE IN THIS FILE, AND WHY IT IS NOT
//
// FusionHttpSession: an HTTP client posting {source} to /scripts/execute on 18750 and reading
// {success, output, error} back. FusionLibrary's FusionPythonHttpRunner is that same client,
// written earlier, against the same add-in. Keeping both would mean one of them going stale
// the day contract v1 becomes v2. It is deleted; FusionRunnerSession adapts theirs.
//
// That is the second duplicate on this one tool -- a whole DwmBridge add-in was written for a
// port that already had DWM-Fusion-AddIn v0.2.0 listening on it. Two servers on 18750 would
// have meant one silently failing to bind and intermittent "nothing answered" that reads like
// the marshalling bug. Same cause both times: building before looking.
//
// THE ADD-IN THIS TALKS TO, PINNED FROM ITS SOURCE
//
// TSPFounder/DWM-Fusion-AddIn v0.2.0, installed at
// %APPDATA%\Autodesk\Autodesk Fusion 360\API\AddIns\DWM_FusionAddIn\. It listens on
// 127.0.0.1:18750 and marshals every request onto Fusion's main thread with a custom event,
// because the Fusion API is not thread-safe and an HTTP handler is on the wrong thread.
//
// THE ENVELOPE IS "success", NOT "ok". An earlier draft assumed "ok" -- a perfectly reasonable
// guess that would have read every failure as a success, because a missing flag is treated as
// "worked". Reading the add-in's source is what settled it, and FusionPythonHttpRunner had it
// right already.
//
// ONE THING THE TRANSPORT CANNOT TELL YOU, stated here because it will look like a bug.
// A closed Fusion and a running Fusion without the add-in loaded are INDISTINGUISHABLE from
// out here -- both are a refused connection on 18750.

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DWM.Shared.Tooling.Cad
{
    /// <summary>One reply from Fusion. Never throws for a Fusion-side error; carries it.</summary>
    public sealed class FusionResponse
    {
        public bool Ok { get; init; }

        /// <summary>Parsed body when the script printed JSON, null otherwise.</summary>
        public JsonElement? Json { get; init; }

        /// <summary>Body as received. Kept whole because a truncated error is a wasted trip.</summary>
        public string RawBody { get; init; } = string.Empty;

        /// <summary>Fusion-side error text, or the transport failure when the call never landed.</summary>
        public string? Error { get; init; }

        public int? HttpStatus { get; init; }

        public override string ToString() => Ok ? $"ok: {RawBody}" : $"failed: {Error}";
    }

    /// <summary>
    /// The command names DWM uses, and where the add-in listens.
    ///
    /// STILL DATA RATHER THAN LITERALS, for a reason that has not changed: the add-in is
    /// versioned, its contract is labelled v1, and v2 should cost one object here rather than
    /// an edit through several files.
    ///
    /// WHAT LEFT THIS TYPE. Routes, request bodies and envelope parsing used to live here,
    /// because DWM had its own client. FusionLibrary's runner owns all three now, so what
    /// remains is the vocabulary: which commands exist, and what they are called.
    /// </summary>
    public sealed class FusionProtocol
    {
        /// <summary>Where the add-in listens. Matches PORT in DWM_FusionAddIn.py.</summary>
        public Uri BaseAddress { get; init; } = new("http://127.0.0.1:18750/");

        /// <summary>
        /// Reads every component's mass, centre of mass and inertia.
        ///
        /// CONTRACT v1 HAS NO MASS-PROPERTIES ROUTE, so this rides on /scripts/execute: send
        /// Python that prints JSON, read it back. Exactly the shape MatlabStageService uses,
        /// because the constraint is the same one -- the tool will not hand you a typed result,
        /// so you ask it to print one.
        /// </summary>
        public string MassPropertiesCommand { get; init; } = "massProperties";

        /// <summary>Builds the rotor by calling WindTurbineBlade.build_rotor.</summary>
        public string BuildCommand { get; init; } = "build";

        /// <summary>Exports the active design. Payload is a FusionExportRequest.</summary>
        public string ExportCommand { get; init; } = "export";

        /// <summary>
        /// Runs an arbitrary CadOperationSequence -- sketches, extrudes, revolves, exports.
        ///
        /// THIS IS THE ONE THAT GENERALISES. The other three name a script somebody wrote;
        /// this one names the IR, so a new feature is a new operation type in CAD_Library and
        /// an emitter case in FusionLibrary, not a new command here.
        /// </summary>
        public string OperationsCommand { get; init; } = "operations";

        public IEnumerable<string> CommandNames
        {
            get
            {
                yield return MassPropertiesCommand;
                yield return BuildCommand;
                yield return ExportCommand;
                yield return OperationsCommand;
            }
        }
    }

    public interface IFusionSession : IDisposable
    {
        /// <summary>True when Fusion answered. Does not prove it has the right document open.</summary>
        Task<bool> PingAsync(CancellationToken ct = default);

        Task<FusionResponse> InvokeAsync(string command, object? payload = null,
            CancellationToken ct = default);
    }

    public sealed class FusionSessionException : Exception
    {
        public FusionSessionException(string message) : base(message) { }
        public FusionSessionException(string message, Exception inner) : base(message, inner) { }
    }
}
