// ToolWorkspaceModel.cs
// What one tool's tile and workspace window need to know, derived from the registry and the
// project's pipeline rather than hardcoded per tool.
//
// WHY THIS IS NOT IN THE WPF PROJECT
//
// WorldDetailView currently hand-writes one Border per tool with a hardcoded command behind
// it, and WorldDetailViewModel hand-writes one method per tool. Adding FEMAP that way means a
// fifth tile, a fifth command, a fifth stage accessor -- the same shape as
// `enum PipelineStage` and with the same result: the cost of a new tool is spread across four
// files and two languages, one of which cannot be compiled on the build agent.
//
// So the DECISION about what a tool can do lives here, where it is data and can be tested,
// and the WPF side renders whatever it is given.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DWM.Shared.Tooling
{
    /// <summary>The three verbs, and only three. See TOOLING.md.</summary>
    public enum ToolAction
    {
        /// <summary>Scaffold the artifact from a template.</summary>
        Create,

        /// <summary>
        /// Launch the native application on the artifact. DWMStudio does not edit these
        /// formats and should not try to -- editing belongs to whatever owns the format.
        /// </summary>
        Edit,

        /// <summary>Automate: a command sequence, or a batch process.</summary>
        Run
    }

    public sealed class ToolWorkspaceModel
    {
        public string ToolId { get; init; } = string.Empty;
        public string StageId { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;

        /// <summary>Hex accent, e.g. "#22D3EE". A string so no theme resource has to exist per tool.</summary>
        public string AccentColor { get; init; } = "#8B98A9";

        public ToolKind Kind { get; init; }
        public ToolAvailability Availability { get; init; }

        /// <summary>Absolute artifact path, or null when the stage has no artifact configured.</summary>
        public string? ArtifactPath { get; init; }

        /// <summary>
        /// The project's root folder. Carried because a tool can need somewhere to work
        /// without owning a single file: MATLAB's stage has no artifact -- its job is to open
        /// wtGui on the model folder, not to edit one document.
        /// </summary>
        public string ProjectRoot { get; init; } = string.Empty;

        /// <summary>
        /// The Run button's label. "Run" says nothing useful when the actual effect differs
        /// this much between tools -- MATLAB hands over to its own GUI, MYSTRAN solves a deck.
        /// </summary>
        public string RunLabel { get; init; } = "Run";

        public bool ArtifactExists { get; init; }

        /// <summary>Shown under the title. Never null -- a blank subtitle looks like a bug.</summary>
        public string Subtitle { get; init; } = string.Empty;

        /// <summary>The tool's own limits, surfaced in the workspace window.</summary>
        public string? KnownLimitation { get; init; }

        public IReadOnlyList<ToolRun> Runs { get; init; } = Array.Empty<ToolRun>();

        public ToolRun? LastRun => Runs.Count == 0 ? null : Runs[^1];

        /// <summary>Create only makes sense when there is a path and nothing is there yet.</summary>
        public bool CanCreate => ArtifactPath is not null && !ArtifactExists;

        /// <summary>
        /// Edit means "launch the owning application", so it needs both a file and somewhere
        /// to launch. A FileOnly tool has no application, which is the honest reason it cannot
        /// offer Edit rather than an oversight.
        /// </summary>
        public bool CanEdit =>
            ArtifactExists &&
            Kind != ToolKind.FileOnly &&
            Availability is not ToolAvailability.NotFound;

        /// <summary>
        /// Run is blocked only by a POSITIVE finding that the tool is absent.
        ///
        /// Not by Unknown, and that is the whole point. An interactive COM tool reports Unknown
        /// on purpose: checking whether a COM server is registered tells you almost nothing
        /// worth having -- not running, not licensed, and not the release the project needs --
        /// so the honest answer before trying is "I have not looked". Treating that as "cannot
        /// run" disabled the MATLAB button entirely and made clicking it do nothing, which is
        /// the exact failure the WhyNot tooltips exist to prevent.
        ///
        /// Attempting IS the probe for these tools. Attach or launch, then report what
        /// happened. Nor does Run require Running: for a batch solver "found on disk" is the
        /// ceiling, so requiring more would make MYSTRAN permanently unrunnable.
        /// </summary>
        public bool CanRun =>
            Kind != ToolKind.FileOnly &&
            Availability != ToolAvailability.NotFound;

        public bool Can(ToolAction action) => action switch
        {
            ToolAction.Create => CanCreate,
            ToolAction.Edit => CanEdit,
            ToolAction.Run => CanRun,
            _ => false
        };

        /// <summary>
        /// Why an action is unavailable, for a tooltip. A disabled button with no explanation
        /// is the same problem as the Create World button that silently did nothing.
        /// </summary>
        public string? WhyNot(ToolAction action)
        {
            if (Can(action)) return null;

            if (Kind == ToolKind.FileOnly)
                return $"{Title} has no automation -- this stage tracks files only.";

            if (Availability == ToolAvailability.NotFound)
                return $"{Title} was not found on this machine. The install paths in the tool " +
                       "registry are defaults, not facts -- override them if it is installed elsewhere.";

            return action switch
            {
                ToolAction.Create when ArtifactPath is null =>
                    "This stage has no artifact path configured, so there is nothing to create.",
                ToolAction.Create =>
                    $"{Path.GetFileName(ArtifactPath)} already exists. Use Edit, or delete it first.",
                ToolAction.Edit =>
                    $"No artifact yet at {ArtifactPath ?? "(no path configured)"}. Create it first.",
                _ => null
            };
        }
    }

    public static class ToolWorkspaceFactory
    {
        /// <summary>
        /// Accents by tool id. Hex rather than theme resource keys so adding a tool never
        /// requires touching a ResourceDictionary -- an unknown id gets the neutral grey and
        /// still renders, rather than throwing a missing-resource exception at runtime.
        /// </summary>
        private static readonly Dictionary<string, string> Accents = new(StringComparer.OrdinalIgnoreCase)
        {
            [ToolRegistry.UModel]  = "#A78BFA",
            [ToolRegistry.Fusion]  = "#60A5FA",
            [ToolRegistry.Matlab]  = "#22D3EE",
            [ToolRegistry.Unreal]  = "#4ADE80",
            [ToolRegistry.Femap]   = "#F472B6",
            [ToolRegistry.Mystran] = "#FB923C",
            [ToolRegistry.Datcom]  = "#FBBF24"
        };

        /// <summary>
        /// One workspace per pipeline stage that names a tool. Stages without a tool are
        /// skipped -- Co-Sim has none today and a tile offering nothing to do is worse than
        /// no tile.
        /// </summary>
        public static IReadOnlyList<ToolWorkspaceModel> Build(
            ProjectPipeline pipeline,
            ToolRegistry registry,
            string projectRoot,
            Func<string, ToolAvailability>? availability = null,
            Func<string, IReadOnlyList<ToolRun>>? runsForStage = null)
        {
            var models = new List<ToolWorkspaceModel>();

            foreach (var stage in pipeline.Stages)
            {
                if (stage.ToolId is null) continue;

                var tool = registry.Find(stage.ToolId);
                if (tool is null) continue;   // reported separately by UnknownToolStages

                var artifact = stage.ArtifactPath is null
                    ? null
                    : Path.Combine(projectRoot, stage.ArtifactPath);

                models.Add(new ToolWorkspaceModel
                {
                    ToolId = tool.Id,
                    StageId = stage.Id,
                    Title = $"{stage.Label} / {tool.DisplayName}",
                    AccentColor = Accents.TryGetValue(tool.Id, out var hex) ? hex : "#8B98A9",
                    Kind = tool.Kind,
                    Availability = availability?.Invoke(tool.Id) ?? ToolAvailability.Unknown,
                    ArtifactPath = artifact,
                    ArtifactExists = artifact is not null && File.Exists(artifact),
                    ProjectRoot = projectRoot,
                    RunLabel = RunLabelFor(tool.Id),
                    Subtitle = Describe(tool, stage),
                    KnownLimitation = tool.KnownLimitation,
                    Runs = runsForStage?.Invoke(stage.Id) ?? Array.Empty<ToolRun>()
                });
            }

            return models;
        }

        /// <summary>
        /// MATLAB/Simulink work is done IN MATLAB. wtGui already exists, runs under R2011a,
        /// picks the scenario, shows the six plots and the pass/fail panel, and exports the
        /// channel CSVs. Rebuilding any of that in WPF would be a worse version of a tool the
        /// project already owns, so this stage's job is to hand over cleanly and get out of
        /// the way. The label says so rather than claiming a generic "Run".
        /// </summary>
        private static string RunLabelFor(string toolId) => toolId switch
        {
            ToolRegistry.Matlab => "Open wtGui in MATLAB",
            ToolRegistry.Mystran => "Solve deck",
            ToolRegistry.Femap => "Load results in FEMAP",
            _ => "Run"
        };

        private static string Describe(ToolDescriptor tool, PipelineStageDefinition stage)
        {
            if (stage.ArtifactPath is not null) return stage.ArtifactPath;

            return tool.Kind switch
            {
                ToolKind.BatchExecutable => "Batch solver - writes results beside its input",
                ToolKind.InteractiveCom => "Driven over COM automation",
                ToolKind.InteractiveHttp => "Driven over a local HTTP add-in",
                _ => "No automation - files only"
            };
        }
    }
}
