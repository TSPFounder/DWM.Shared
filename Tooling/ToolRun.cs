// ToolRun.cs
// One execution of one stage, and the freshness check that makes its outputs trustworthy.
//
// WHY A RUN RECORD AND NOT A BOOLEAN
//
// WorldProject.MarkStageComplete sets IsComplete = true. That cannot express "ran, passed,
// but with three warnings" -- which is exactly what a turbine export produces when a channel
// is missing or the gust scenario was used. A green tick over a run with warnings is the
// same class of problem as a placeholder that looks like model output: correct-seeming, and
// silent about the one thing worth knowing.
//
// THE FRESHNESS CHECK IS THE POINT
//
// MatlabStageService refuses any CSV that predates the run it started, because an export
// that silently does nothing leaves yesterday's correctly-named files on disk and the
// package built from them is well-formed, plausible, and describes a run that never
// happened. That hazard is not specific to MATLAB. A MYSTRAN .f06 left from yesterday's
// deck is identical in kind -- it parses, it has eigenvalues, and nothing about it says the
// solver did not run today. So the check is generalised here rather than reimplemented per
// tool.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DWM.Shared.Tooling
{
    public enum ToolRunStatus
    {
        /// <summary>Started and not yet finished.</summary>
        Running,

        /// <summary>Finished, outputs verified fresh, nothing to report.</summary>
        Succeeded,

        /// <summary>
        /// Finished and usable, but something is worth reading. NOT the same as Succeeded, and
        /// deliberately not collapsed into it.
        /// </summary>
        SucceededWithWarnings,

        /// <summary>The tool reported an error, or an expected output never appeared.</summary>
        Failed,

        /// <summary>
        /// The tool reported success but its outputs predate the run. Distinct from Failed
        /// because it is the more dangerous case: everything LOOKS right, including the files.
        /// </summary>
        StaleOutputs
    }

    public sealed class ToolRun
    {
        public string StageId { get; init; } = string.Empty;
        public string ToolId { get; init; } = string.Empty;

        /// <summary>Which ProgID or executable actually ran. Pins the version for the record.</summary>
        public string? ResolvedVia { get; init; }

        public DateTime StartedUtc { get; init; }
        public TimeSpan Duration { get; init; }
        public ToolRunStatus Status { get; init; }

        public IReadOnlyList<string> Inputs { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> Outputs { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

        public string? FailureMessage { get; init; }
        public string? LogPath { get; init; }

        public bool HasWarnings => Warnings.Count > 0;

        public bool ProducedUsableOutput =>
            Status is ToolRunStatus.Succeeded or ToolRunStatus.SucceededWithWarnings;

        /// <summary>
        /// Which of <paramref name="expectedOutputs"/> are missing or older than the run.
        /// A file that exists but predates the run counts as MISSING, not present: it is the
        /// previous run's result wearing this run's name.
        /// </summary>
        /// <param name="tolerance">
        /// Slack for filesystem timestamp granularity, NOT for clock skew -- the tool runs on
        /// this machine. Keep it small; every second of tolerance is a second of staleness
        /// that can pass unnoticed.
        /// </param>
        public static IReadOnlyList<string> FindStaleOrMissing(
            IEnumerable<string> expectedOutputs,
            DateTime runStartedUtc,
            TimeSpan? tolerance = null)
        {
            var threshold = runStartedUtc - (tolerance ?? TimeSpan.FromSeconds(2));
            var bad = new List<string>();

            foreach (var path in expectedOutputs ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                if (!File.Exists(path)) { bad.Add(path); continue; }
                if (File.GetLastWriteTimeUtc(path) < threshold) bad.Add(path);
            }

            return bad;
        }

        /// <summary>
        /// Build the finished record, deriving the status from the evidence rather than taking
        /// the caller's word for it. A caller that believes it succeeded while its outputs are
        /// stale gets StaleOutputs, which is the situation this whole type exists to catch.
        /// </summary>
        public static ToolRun Complete(
            string stageId,
            string toolId,
            DateTime startedUtc,
            IEnumerable<string> expectedOutputs,
            IEnumerable<string>? warnings = null,
            string? failureMessage = null,
            string? resolvedVia = null,
            TimeSpan? tolerance = null)
        {
            var outputs = (expectedOutputs ?? Enumerable.Empty<string>()).ToList();
            var warningList = (warnings ?? Enumerable.Empty<string>()).ToList();
            var stale = FindStaleOrMissing(outputs, startedUtc, tolerance);

            ToolRunStatus status;
            if (failureMessage is not null) status = ToolRunStatus.Failed;
            else if (stale.Count > 0) status = ToolRunStatus.StaleOutputs;
            else if (warningList.Count > 0) status = ToolRunStatus.SucceededWithWarnings;
            else status = ToolRunStatus.Succeeded;

            if (status == ToolRunStatus.StaleOutputs)
            {
                warningList.Add(
                    "These outputs are missing or predate the run and were NOT produced by it: " +
                    string.Join(", ", stale.Select(Path.GetFileName)) +
                    ". A result file left from an earlier run parses exactly like a fresh one.");
            }

            return new ToolRun
            {
                StageId = stageId,
                ToolId = toolId,
                ResolvedVia = resolvedVia,
                StartedUtc = startedUtc,
                Duration = DateTime.UtcNow - startedUtc,
                Status = status,
                Outputs = outputs,
                Warnings = warningList,
                FailureMessage = failureMessage
            };
        }
    }
}
