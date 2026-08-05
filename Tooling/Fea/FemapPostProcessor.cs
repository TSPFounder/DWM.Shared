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

        /// <param name="api">
        /// Override when a method name turns out wrong for this FEMAP. The names have not been
        /// checked against 10.2's reference, and they are data precisely so being wrong costs a
        /// caller one line rather than a rebuild.
        /// </param>
        public FemapPostProcessor(Func<IFemapSession> sessionFactory, FemapApiNames? api = null)
        {
            _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
            _api = api ?? new FemapApiNames();
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

            try
            {
                using var session = _sessionFactory();
                attached = session.IsAttachedToExistingInstance;

                // FEMAP IS LEFT OPEN. The point of this is that the user ends up looking at
                // their mode shapes; a session that closed on the way out would be the MATLAB
                // hand-off bug again, one tool along.
                session.Detach();

                session.Invoke(_api.SetVisible, true);

                // Order is not optional: results have nowhere to land until the model exists.
                session.Invoke(_api.ReadNastranModel, deckPath);
                session.Invoke(_api.ReadNastranResults, 1, resultsPath);

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
                    resolvedVia: FemapComSession.DefaultProgId),
                AttachedToExistingFemap = attached,
                DeckPath = deckPath,
                ResultsPath = resultsPath
            };
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
