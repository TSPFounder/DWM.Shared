// FemapPostProcessor.cs
// Load a solved MYSTRAN run into FEMAP for post-processing.
//
// THIS AUTOMATES A WORKFLOW THAT WAS PROVEN BY HAND FIRST, on 2026-08-04, and the order
// mattered. Driven blind, the first attempt would have hit FEMAP's Import dialog defaulting
// to "Femap Neutral" with a blank Nastran flavour -- which reads a Nastran deck, reports
// "Database Update Completed. No Errors.", and produces nothing. Through a COM API that
// would have been a silent no-op with a success return. Watching it happen in the UI took
// two minutes.
//
// WHY TWO IMPORTS AND NOT ONE
//
// MYSTRAN's .op2 for this run holds six OUGV1 eigenvector blocks and NO GEOM datablocks. It
// carries results without geometry, so there is nothing for the results to attach to until
// the model exists. FEMAP reads the deck to build the mesh, then reads the .op2 onto it, and
// the two agree because node and element ids come from the same file.
//
// A .neu would collapse this to one step by carrying model and results together, but MYSTRAN
// did not write one for this deck. Worth revisiting when FEMAP is the thing authoring the
// deck, at which point the model is already open and only the results need importing.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DWM.Shared.Tooling.Fea
{
    public sealed class FemapPostProcessResult
    {
        public ToolRun Run { get; init; } = null!;
        public bool AttachedToExistingFemap { get; init; }
        public string? DeckPath { get; init; }
        public string? ResultsPath { get; init; }
        public bool Succeeded => Run.ProducedUsableOutput;
    }

    public sealed class FemapPostProcessor
    {
        private readonly Func<IFemapSession> _sessionFactory;
        private readonly FemapApiNames _api;
        private readonly bool _startNewModel;

        /// <param name="api">
        /// Override when a method name turns out wrong for this FEMAP. The names have not been
        /// checked against 10.2's reference, and they are data precisely so being wrong costs a
        /// caller one line rather than a rebuild.
        /// </param>
        /// <param name="startNewModel">
        /// Clear FEMAP to an empty model before importing.
        ///
        /// DEFAULTS TO FALSE, and the reason is asymmetric damage. Leaving it off means a
        /// repeat load collides with itself -- "Overwriting existing Property 101", twelve
        /// output sets where six belong -- which is confusing but loses nothing. Turning it on
        /// by default would discard whatever the user had open in FEMAP, which might be an
        /// afternoon's meshing this code knows nothing about. A confusing results tree is
        /// recoverable in one File > New; somebody else's unsaved model is not.
        /// </param>
        public FemapPostProcessor(
            Func<IFemapSession> sessionFactory,
            FemapApiNames? api = null,
            bool startNewModel = false)
        {
            _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
            _api = api ?? new FemapApiNames();
            _startNewModel = startNewModel;
        }

        /// <param name="deckPath">The Nastran deck MYSTRAN solved. Read first, to build the mesh.</param>
        /// <param name="resultsPath">
        /// The .op2. When null, derived from the deck's stem -- MYSTRAN writes results beside
        /// their input, which is also why the solve runs in the deck's own folder.
        /// </param>
        public FemapPostProcessResult Load(
            string deckPath, string? resultsPath = null, string stageId = "fea-solve")
        {
            if (string.IsNullOrWhiteSpace(deckPath))
                throw new ArgumentException("A deck path is required.", nameof(deckPath));

            var startedUtc = DateTime.UtcNow;
            var warnings = new List<string>();

            resultsPath ??= Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(deckPath))!,
                Path.GetFileNameWithoutExtension(deckPath) + ".OP2");

            if (!File.Exists(deckPath))
                return Failed(stageId, startedUtc, deckPath, resultsPath,
                    $"Deck not found: {deckPath}");

            if (!File.Exists(resultsPath))
                return Failed(stageId, startedUtc, deckPath, resultsPath,
                    $"No results file at {resultsPath}.\n\n" +
                    "Solve the deck first -- FEMAP can display the mesh without it, but there " +
                    "would be nothing to post-process.");

            var attached = false;
            var acceptedVia = FemapComSession.DefaultProgId;

            try
            {
                using var session = _sessionFactory();
                attached = session.IsAttachedToExistingInstance;

                // FEMAP IS LEFT OPEN. The point of this is that the user ends up looking at
                // their mode shapes; a session that closed on the way out would be the MATLAB
                // hand-off bug again, one tool along.
                session.Detach();

                // Visibility is cosmetic; a FEMAP that will not show itself still imported.
                try { session.Invoke(_api.SetVisible, true); } catch (Exception) { }

                if (_startNewModel)
                {
                    var cleared = TryShapes(session, _api.NewModel, string.Empty, "start a new model");
                    warnings.Add($"FEMAP was cleared to an empty model first ({cleared}).");
                }

                // Order is not optional: results have nowhere to land until the model exists.
                var modelShape = TryShapes(session, _api.ReadNastranModel, deckPath, "read the model");
                var resultsShape = TryShapes(session, _api.ReadNastranResults, resultsPath, "read the results");

                // Nudge the tree into repainting. Every one of these is best-effort: the
                // import has already succeeded by the time we get here, and a refresh that
                // does not happen costs one click on the PostProcessing tab.
                foreach (var refresh in _api.RefreshUi)
                {
                    try { session.Invoke(refresh); } catch (Exception) { }
                }

                // RECORDED AS ResolvedVia, NOT AS A WARNING, and the distinction cost a test
                // to notice. As a warning it fired on EVERY successful load, so every run came
                // back SucceededWithWarnings -- and a channel that triggers every single time
                // carries no information. That is the run-history bug inverted: there, warnings
                // existed and could not be read; here they could be read and meant nothing.
                // Both end with nobody looking.
                //
                // ResolvedVia is where this belonged. The field answers "how did this actually
                // get done", which is precisely what an accepted call shape is, and the run
                // history already renders it.
                acceptedVia = $"{FemapComSession.DefaultProgId} via {modelShape} + {resultsShape}";

                if (!attached)
                    warnings.Add(
                        "FEMAP was launched by this command rather than attached to. It has been " +
                        "detached and will stay open, but a FEMAP you start yourself is the " +
                        "steadier path.");
            }
            catch (Exception ex)
            {
                return Failed(stageId, startedUtc, deckPath, resultsPath, ex.Message);
            }

            return new FemapPostProcessResult
            {
                Run = ToolRun.Complete(
                    stageId, ToolRegistry.Femap, startedUtc,
                    // NO EXPECTED OUTPUTS. This produces nothing on disk -- it puts a model on
                    // someone's screen. Naming a file here would make the freshness check fail
                    // every time and mean nothing when it passed.
                    expectedOutputs: Array.Empty<string>(),
                    warnings: warnings,
                    resolvedVia: acceptedVia),
                AttachedToExistingFemap = attached,
                DeckPath = deckPath,
                ResultsPath = resultsPath
            };
        }


        /// <summary>
        /// Try each candidate call shape until one succeeds; returns the signature that worked.
        ///
        /// A COM type mismatch is cheap to provoke and tells you nothing dangerous -- FEMAP
        /// either coerces the arguments or refuses them, and refusing changes no state. So
        /// trying is a legitimate probe here in a way it would not be for, say, a solver that
        /// might half-write a file.
        /// </summary>
        private static string TryShapes(
            IFemapSession session, IReadOnlyList<FemapCallShape> shapes, string path, string what)
        {
            var attempts = new List<string>();

            foreach (var shape in shapes)
            {
                try
                {
                    // THE RETURN VALUE IS REPORTED, NOT IGNORED. FEMAP's API signals failure
                    // with a status code rather than an exception, so a call that "worked"
                    // may have declined to do anything -- the same shape as MATLAB's Execute
                    // returning error text as a string and MYSTRAN exiting 0 after a FATAL.
                    // The convention is not documented here, so the code is surfaced rather
                    // than interpreted: a wrong guess about which value means success would
                    // be worse than showing the number and letting a human judge it.
                    var status = session.Invoke(shape.Method, shape.Args(path));

                    // THE RETURN CODE IS NOW CHECKED, not merely reported. Until FEMAP's
                    // success value was known this could only be shown to a human, and three
                    // runs reported success while leaving Out: 0 because a refused call and an
                    // accepted one are indistinguishable when nobody reads the number.
                    if (!IsSuccess(status))
                    {
                        attempts.Add($"{shape.Signature} -> returned {status}, not {FemapApiNames.Success}");
                        continue;
                    }

                    return $"{shape.Signature} returned {status ?? "(null)"}";
                }
                catch (Exception ex)
                {
                    attempts.Add($"{shape.Signature} -> {FirstLine(ex.Message)}");
                }
            }

            throw new FemapSessionException(
                $"Could not {what}. Every candidate call shape was refused:\n  " +
                string.Join("\n  ", attempts) + "\n\n" +
                "The right signature is in FEMAP's API reference under the install " +
                "(C:\\FEMAPv102). Pass a corrected FemapApiNames -- the shapes are data, so " +
                "this needs no rebuild.");
        }


        /// <summary>
        /// FEMAP returns -1 for success. A value that will not convert to a number is accepted
        /// rather than rejected -- not every API member returns a status, and refusing an
        /// unreadable one would turn a working call into a failure on a technicality.
        /// </summary>
        private static bool IsSuccess(object? status)
        {
            if (status is null) return true;
            try { return Convert.ToInt64(status) == FemapApiNames.Success; }
            catch (Exception) { return true; }
        }

        private static string FirstLine(string message)
        {
            var index = message.IndexOf('\n');
            return index < 0 ? message : message[..index];
        }

        private static FemapPostProcessResult Failed(
            string stageId, DateTime startedUtc, string deck, string results, string message) => new()
        {
            Run = ToolRun.Complete(stageId, ToolRegistry.Femap, startedUtc,
                Array.Empty<string>(), failureMessage: message),
            DeckPath = deck,
            ResultsPath = results
        };
    }
}
