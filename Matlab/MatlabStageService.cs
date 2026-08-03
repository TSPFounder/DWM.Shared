// MatlabStageService.cs
// The MATLAB stage of the DWM pipeline, end to end: run the R2011a turbine model for a
// chosen scenario, export its channels to CSV, and turn those into a world package .db.
//
//     wtRunSimulation  ->  wtExportSimSamples  ->  WorldPackageExporter.WriteTurbine  ->  .db
//
// Before this existed a human ran the first two in MATLAB and the third somewhere else,
// carrying file paths between them by hand. That gap is the whole reason this class exists;
// it contains no physics and no schema knowledge, only the handoff.
//
// THE FAILURE THIS CLASS IS REALLY BUILT AROUND
//
// A turbine that looks right is the project's characteristic hazard. WriteTurbine already
// refuses to fall back to a constant-rate placeholder without an explicit flag, for a reason
// written out at length there: a rotor turning at a steady rate looks EXACTLY like a rotor
// on real model output, so that particular failure is invisible at precisely the place
// anyone would check for it.
//
// Automating the chain adds a second way to reach the same bad outcome, and it is worse
// because no human is watching any step. If wtExportSimSamples throws -- or MATLAB errors
// and Execute quietly returns the error text as a string, which is what it does -- then
// yesterday's CSVs are still sitting on disk with the right names. WriteTurbine would open
// them, succeed, and produce a package that is well-formed, correct-looking, and describing
// a run that did not happen.
//
// So this class checks, in order:
//   1. MATLAB errors, via a MATLAB-side try/catch and a sentinel variable -- NOT by reading
//      Execute's return value, which does not indicate failure.
//   2. Every expected CSV EXISTS.
//   3. Every expected CSV was WRITTEN AFTER THIS RUN STARTED. This is the one that catches
//      the stale-file case, and it is the only check that would.
//
// Only then does WriteTurbine get called.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DWM.Shared.Matlab
{
    public sealed class MatlabStageService
    {
        /// <summary>
        /// The five channels wtExportSimSamples.m writes. ORDER MATTERS ONLY FOR REPORTING;
        /// "rotor" is first because it is the one channel that is mandatory -- WriteTurbine
        /// throws without it, and the other four are seeded opportunistically from siblings.
        /// </summary>
        public static readonly IReadOnlyList<string> ChannelSuffixes =
            new[] { "rotor", "pitch", "yaw", "tower", "power" };

        /// <summary>
        /// Sentinel holding the MATLAB-side error message, or '' when the command succeeded.
        /// Prefixed to avoid colliding with anything in the user's base workspace -- an
        /// attached session shares its workspace with whatever they were doing.
        /// </summary>
        private const string ErrorSentinel = "dwmStageErr";

        /// <summary>Variable the simulation output struct is parked in between the two commands.</summary>
        private const string OutputVariable = "dwmStageOut";

        private readonly Func<IMatlabSession> _sessionFactory;
        private readonly WorldPackageExporter _exporter;
        private readonly Func<DateTime> _utcNow;

        /// <param name="sessionFactory">
        /// Creates the session. A FACTORY rather than an instance because COM objects are
        /// thread-affine: RunAndExportAsync marshals the whole sequence onto one background
        /// thread, and the session must be created on that same thread to be used from it.
        /// </param>
        /// <param name="exporter">Defaults to a plain WorldPackageExporter.</param>
        /// <param name="utcNow">Clock seam; tests use it to simulate a stale file.</param>
        public MatlabStageService(
            Func<IMatlabSession> sessionFactory,
            WorldPackageExporter? exporter = null,
            Func<DateTime>? utcNow = null)
        {
            _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
            _exporter = exporter ?? new WorldPackageExporter();
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        /// <summary>
        /// Run the stage. BLOCKS for the full length of the simulation -- a 600 s ramp is tens
        /// of seconds of wall clock, and MATLAB's COM Execute is synchronous with no progress
        /// callback. Call <see cref="RunAndExportAsync"/> from UI code.
        /// </summary>
        public MatlabStageResult RunAndExport(MatlabStageRequest request)
        {
            if (request is null) throw new ArgumentNullException(nameof(request));
            Validate(request);

            var stopwatch = Stopwatch.StartNew();
            var warnings = new List<string>();

            if (request.Scenario == TurbineScenario.Gust)
            {
                warnings.Add(
                    "Scenario is GUST. The pitch controller holds rotor speed nearly constant " +
                    "through a gust -- that is what it is for -- so the exported rotor motion " +
                    "will look almost identical to a constant-rate placeholder. Use RAMP if the " +
                    "point of the export is to show the model doing something.");
            }

            // Take the reference time BEFORE anything runs. Every CSV must be newer than this
            // or it is left over from an earlier run. Taken before the session is even opened,
            // so MATLAB's own startup time cannot eat into the window.
            var runStartedUtc = _utcNow();

            using var session = _sessionFactory();

            // ADDPATH, not CD. The user's current folder is theirs; an attached session is one
            // they are working in. addpath is idempotent and does not move them.
            RunGuarded(session,
                $"addpath({MatlabLiteral(request.TurbineCodeDirectory)});",
                "adding the turbine folder to MATLAB's path");

            RunGuarded(session,
                $"{OutputVariable} = wtRunSimulation({MatlabLiteral(request.Scenario.ToMatlabToken())});",
                $"running the '{request.Scenario.ToMatlabToken()}' scenario");

            var csvBasePath = Path.Combine(request.ResolvedCsvDirectory, request.CsvBaseName);
            RunGuarded(session,
                $"wtExportSimSamples({OutputVariable}, {MatlabLiteral(csvBasePath)}, " +
                $"{request.SampleRateHz.ToString(CultureInfo.InvariantCulture)});",
                "exporting the channel CSVs");

            var (exported, missing) = InspectChannelFiles(request, runStartedUtc, warnings);

            // Rotor is not optional. Failing here rather than letting WriteTurbine throw gives
            // a message about THIS stage -- which MATLAB ran, which file was expected -- rather
            // than a FileNotFoundException from a layer that knows nothing about the run.
            if (!exported.Contains("rotor"))
            {
                throw new MatlabStageException(
                    "MATLAB reported no error, but the rotor CSV is missing or predates this run.\n\n" +
                    $"  Expected: {request.ChannelCsvPath("rotor")}\n" +
                    $"  Run started (UTC): {runStartedUtc:o}\n\n" +
                    "The rotor channel is the one WriteTurbine cannot do without. Check that " +
                    "wtExportSimSamples.m is on the path and that the output directory is writable.");
            }

            if (request.RequireAllChannels && missing.Count > 0)
            {
                throw new MatlabStageException(
                    "RequireAllChannels is set, and these channels produced no fresh CSV: " +
                    string.Join(", ", missing) + ".\n\n" +
                    "A missing channel is invisible downstream -- the package simply has fewer " +
                    "blocks, and nothing on screen says so.");
            }

            // WriteTurbine takes the ROTOR path and finds the siblings itself by substituting
            // on the last "_rotor". allowFallback stays at its default false: having just
            // verified real, fresh files exist, silently accepting a placeholder instead would
            // defeat every check above.
            _exporter.WriteTurbine(
                request.OutputPackagePath,
                request.WorldId,
                request.ChannelCsvPath("rotor"));

            stopwatch.Stop();

            return new MatlabStageResult
            {
                Scenario = request.Scenario,
                PackagePath = request.OutputPackagePath,
                ChannelsExported = exported,
                ChannelsMissing = missing,
                Warnings = warnings,
                AttachedToExistingMatlab = session.IsAttachedToExistingInstance,
                Duration = stopwatch.Elapsed
            };
        }

        /// <summary>
        /// <see cref="RunAndExport"/> on a background thread. The session is CREATED INSIDE the
        /// task so the COM object is born on, and stays on, the thread that uses it.
        /// </summary>
        public Task<MatlabStageResult> RunAndExportAsync(
            MatlabStageRequest request, CancellationToken cancellationToken = default)
        {
            // NOTE ON CANCELLATION: the token guards the QUEUE, not the run. Once MATLAB's
            // Execute is in flight there is no way to interrupt it from here -- COM automation
            // offers no cancellation and MATLAB will finish the simulation regardless.
            // Promising otherwise would be a lie told by a method signature.
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return RunAndExport(request);
            }, cancellationToken);
        }

        // ------------------------------------------------------------------
        private static void Validate(MatlabStageRequest r)
        {
            if (string.IsNullOrWhiteSpace(r.TurbineCodeDirectory))
                throw new MatlabStageException("TurbineCodeDirectory is required (the folder holding wtRunSimulation.m).");

            if (!Directory.Exists(r.TurbineCodeDirectory))
                throw new MatlabStageException(
                    $"TurbineCodeDirectory does not exist: {r.TurbineCodeDirectory}");

            if (string.IsNullOrWhiteSpace(r.OutputPackagePath))
                throw new MatlabStageException("OutputPackagePath is required (the .db to write).");

            if (string.IsNullOrWhiteSpace(r.CsvBaseName))
                throw new MatlabStageException("CsvBaseName is required (wtGui's 'Base name' field).");

            if (r.SampleRateHz <= 0)
                throw new MatlabStageException(
                    $"SampleRateHz must be positive (was {r.SampleRateHz}).");

            if (r.StaleFileTolerance < TimeSpan.Zero)
                throw new MatlabStageException("StaleFileTolerance cannot be negative.");

            if (!Directory.Exists(r.ResolvedCsvDirectory))
                throw new MatlabStageException(
                    $"CSV output directory does not exist: {r.ResolvedCsvDirectory}");
        }

        /// <summary>
        /// Run one command and raise a MATLAB-side error as a C# exception.
        ///
        /// THE WHOLE POINT: MATLAB's COM Execute returns error text as an ordinary string and
        /// does not throw, so checking its return value means string-sniffing for a prefix that
        /// differs between releases ('??? ' on R2011a, 'Error using ' on modern ones). Instead
        /// the command is wrapped in a MATLAB try/catch that assigns the message to a sentinel,
        /// and the sentinel is read back. That is release-independent and unambiguous.
        /// </summary>
        private static void RunGuarded(IMatlabSession session, string command, string whatItWasDoing)
        {
            session.Execute(BuildGuardedCommand(command));

            var error = session.GetCharArray(ErrorSentinel);
            if (!string.IsNullOrWhiteSpace(error))
            {
                throw new MatlabStageException(
                    $"MATLAB raised an error while {whatItWasDoing}.\n\n" +
                    $"  MATLAB said: {error.Trim()}\n\n" +
                    $"  Command was: {command}");
            }
        }

        /// <summary>
        /// Wrap a command so failure lands in the sentinel instead of the console.
        ///
        /// Emitted as ONE LINE with commas, because MATLAB's COM Execute takes a single
        /// command string and newline handling in it has never been worth relying on. R2011a
        /// accepts 'catch identifier' (R2007b) and MException.message, so this is within the
        /// release floor the MVP turbine sets.
        /// </summary>
        public static string BuildGuardedCommand(string command)
        {
            var trimmed = command.Trim();
            if (trimmed.EndsWith(";", StringComparison.Ordinal))
                trimmed = trimmed.Substring(0, trimmed.Length - 1);

            return $"{ErrorSentinel} = ''; try, {trimmed}; catch dwmStageME, " +
                   $"{ErrorSentinel} = dwmStageME.message; end";
        }

        /// <summary>
        /// A MATLAB single-quoted string literal.
        ///
        /// Backslashes need NO escaping -- MATLAB single-quoted strings are literal, which is
        /// what makes Windows paths safe to interpolate. Single quotes DO, by doubling. A path
        /// containing an apostrophe is rare but entirely legal on Windows, and getting this
        /// wrong would produce a MATLAB syntax error whose text points nowhere useful.
        /// </summary>
        public static string MatlabLiteral(string value)
            => "'" + (value ?? string.Empty).Replace("'", "''") + "'";

        /// <summary>
        /// Which channel CSVs exist AND postdate the run. A file that exists but predates the
        /// run is treated as MISSING, not present -- it is last time's data wearing this time's
        /// name, and it is the specific thing this stage must never hand downstream.
        /// </summary>
        private static (List<string> exported, List<string> missing) InspectChannelFiles(
            MatlabStageRequest request, DateTime runStartedUtc, List<string> warnings)
        {
            var exported = new List<string>();
            var missing = new List<string>();
            var threshold = runStartedUtc - request.StaleFileTolerance;

            foreach (var suffix in ChannelSuffixes)
            {
                var path = request.ChannelCsvPath(suffix);

                if (!File.Exists(path))
                {
                    missing.Add(suffix);
                    continue;
                }

                var writtenUtc = File.GetLastWriteTimeUtc(path);
                if (writtenUtc < threshold)
                {
                    missing.Add(suffix);
                    warnings.Add(
                        $"STALE: '{Path.GetFileName(path)}' exists but was last written " +
                        $"{writtenUtc:u}, before this run began ({runStartedUtc:u}). Treated as " +
                        "missing. This is left over from an earlier export -- it was NOT used.");
                    continue;
                }

                exported.Add(suffix);
            }

            foreach (var suffix in missing)
            {
                if (suffix == "rotor") continue;   // handled as a hard failure by the caller
                warnings.Add(
                    $"Channel '{suffix}' produced no fresh CSV, so the package has no " +
                    $"block for it. Nothing downstream will report this.");
            }

            return (exported, missing);
        }
    }
}
