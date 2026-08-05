// ProjectPipeline.cs / PipelineStageDefinition.cs
// A project's stages as an ORDERED LIST OF DATA, replacing DWMStudio's
// `enum PipelineStage { SysML, Cad, Matlab, CoSim, Runtime }`.
//
// WHY THE ENUM HAS TO GO
//
// WorldProject indexes its stage list by casting the enum -- Stages[(int)stage] -- and
// WorldDetailViewModel exposes five hardcoded properties feeding five hardcoded XAML slots.
// So the shape asserts that EVERY engineering system passes through exactly those five
// stages, exactly once, in that order, and that a stage is binary complete/incomplete.
//
// That was already false before FEMAP and MYSTRAN arrived: the MYSTRAN tower modal deck had
// nowhere to live, and neither would a DATCOM sweep or a wind-tunnel dataset. Adding one
// stage costs an enum change PLUS a list-index invariant PLUS a ViewModel PLUS XAML.
// SCOPE.md's fragility audit calls this item 3 and rates it the cheapest thing to fix and
// the most definitionally wrong for a system meant to support any engineering discipline.
//
// MIGRATION NOTE: this does NOT delete WorldProject.PipelineStage. The WPF project cannot be
// built or tested on the Linux agent, so ripping out the enum blind would be reckless. This
// is the replacement model, tested; wiring WorldProject to it is a separate change made
// where it can be compiled.

using System;
using System.Collections.Generic;
using System.Linq;

namespace DWM.Shared.Tooling
{
    /// <summary>One step of a project's pipeline. A stage NAMES a tool; it does not embed one.</summary>
    public sealed class PipelineStageDefinition
    {
        /// <summary>Stable slug within the project, e.g. "fea-modal".</summary>
        public string Id { get; init; } = string.Empty;

        public string Label { get; init; } = string.Empty;

        /// <summary>A <see cref="ToolRegistry"/> id, or null for a stage with no tool behind it.</summary>
        public string? ToolId { get; init; }

        /// <summary>
        /// The file this stage authors, relative to the project root. Null when the stage
        /// produces nothing durable.
        /// </summary>
        public string? ArtifactPath { get; init; }

        /// <summary>
        /// An optional stage does not hold up a pipeline being considered complete. Present
        /// because the previous model had no way to say "this project has no CAD step" other
        /// than leaving a slot permanently unticked.
        /// </summary>
        public bool IsOptional { get; init; }

        public override string ToString() => $"{Label} [{Id}]" + (ToolId is null ? "" : $" -> {ToolId}");
    }

    public sealed class ProjectPipeline
    {
        private readonly List<PipelineStageDefinition> _stages = new();

        public IReadOnlyList<PipelineStageDefinition> Stages => _stages;

        public ProjectPipeline() { }

        public ProjectPipeline(IEnumerable<PipelineStageDefinition> stages)
        {
            foreach (var stage in stages) Add(stage);
        }

        /// <summary>
        /// The five stages DWMStudio has always had, expressed as data. Exists so switching to
        /// this model changes nothing visible on day one -- a migration nobody notices is the
        /// only kind worth attempting on a UI that cannot be built on the test agent.
        /// </summary>
        public static ProjectPipeline Default() => new(new[]
        {
            new PipelineStageDefinition { Id = "sysml",   Label = "SysML",   ToolId = ToolRegistry.UModel },
            new PipelineStageDefinition { Id = "cad",     Label = "CAD",     ToolId = ToolRegistry.Fusion },
            new PipelineStageDefinition { Id = "matlab",  Label = "MATLAB",  ToolId = ToolRegistry.Matlab },
            new PipelineStageDefinition { Id = "cosim",   Label = "Co-Sim",  ToolId = null },
            new PipelineStageDefinition { Id = "runtime", Label = "Runtime", ToolId = ToolRegistry.Unreal }
        });

        /// <summary>
        /// The default plus the FEA pair, as a worked example of the thing the enum could not
        /// express. FEMAP meshes and writes the deck, MYSTRAN solves it, FEMAP reads it back --
        /// so structural work is two stages sharing one artifact chain, not one box to tick.
        /// </summary>
        /// <param name="deckPath">
        /// The Nastran deck both FEA stages work on. FEMAP writes it, MYSTRAN reads it, and
        /// FEMAP reads the results back -- ONE FILE IS THE HANDOFF, which is why both stages
        /// name the same path rather than each having its own.
        ///
        /// May be ABSOLUTE, and usually is: a deck normally lives with the FEA work rather
        /// than under the Simulink model that happens to be the project root. Path.Combine
        /// returns a rooted second argument unchanged, so no special case is needed here.
        /// Null falls back to "fea/model.bdf" relative to the project root.
        /// </param>
        public static ProjectPipeline WithStructuralAnalysis(string? deckPath = null)
        {
            var deck = string.IsNullOrWhiteSpace(deckPath) ? "fea/model.bdf" : deckPath!;

            var pipeline = Default();
            pipeline.InsertAfter("matlab", new PipelineStageDefinition
            {
                Id = "fea-mesh", Label = "FEA Mesh", ToolId = ToolRegistry.Femap,
                ArtifactPath = deck, IsOptional = true
            });
            pipeline.InsertAfter("fea-mesh", new PipelineStageDefinition
            {
                Id = "fea-solve", Label = "FEA Solve", ToolId = ToolRegistry.Mystran,
                ArtifactPath = deck, IsOptional = true
            });
            return pipeline;
        }

        public PipelineStageDefinition? Find(string stageId) =>
            _stages.FirstOrDefault(s => string.Equals(s.Id, stageId, StringComparison.OrdinalIgnoreCase));

        public void Add(PipelineStageDefinition stage)
        {
            if (stage is null) throw new ArgumentNullException(nameof(stage));
            if (string.IsNullOrWhiteSpace(stage.Id))
                throw new ArgumentException("A stage needs an Id.", nameof(stage));
            if (Find(stage.Id) is not null)
                throw new ArgumentException(
                    $"Stage id '{stage.Id}' is already in this pipeline. Ids address stages in " +
                    "saved projects and run history, so duplicates would make both ambiguous.",
                    nameof(stage));

            _stages.Add(stage);
        }

        public void InsertAfter(string existingStageId, PipelineStageDefinition stage)
        {
            var index = IndexOf(existingStageId);
            if (index < 0)
                throw new ArgumentException(
                    $"No stage '{existingStageId}' to insert after. Present: " +
                    string.Join(", ", _stages.Select(s => s.Id)), nameof(existingStageId));

            Add(stage);                       // validates first
            _stages.RemoveAt(_stages.Count - 1);
            _stages.Insert(index + 1, stage);
        }

        public bool Remove(string stageId)
        {
            var index = IndexOf(stageId);
            if (index < 0) return false;
            _stages.RemoveAt(index);
            return true;
        }

        /// <summary>Move a stage to a new position. Order is data now, not an enum's declaration order.</summary>
        public void MoveTo(string stageId, int newIndex)
        {
            var index = IndexOf(stageId);
            if (index < 0) throw new ArgumentException($"No stage '{stageId}'.", nameof(stageId));
            if (newIndex < 0 || newIndex >= _stages.Count)
                throw new ArgumentOutOfRangeException(nameof(newIndex),
                    $"Index {newIndex} is outside 0..{_stages.Count - 1}.");

            var stage = _stages[index];
            _stages.RemoveAt(index);
            _stages.Insert(newIndex, stage);
        }

        public int IndexOf(string stageId) =>
            _stages.FindIndex(s => string.Equals(s.Id, stageId, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Stage ids naming a tool the registry does not have. Worth surfacing rather than
        /// discovering when a Run button does nothing -- a project file can outlive a tool id.
        /// </summary>
        public IReadOnlyList<string> UnknownToolStages(ToolRegistry registry) =>
            _stages.Where(s => s.ToolId is not null && registry.Find(s.ToolId) is null)
                   .Select(s => s.Id)
                   .ToList();
    }
}
