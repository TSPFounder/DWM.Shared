// ToolDescriptor.cs / ToolAvailability.cs / ToolStatus.cs
// What DWMStudio knows about one external tool. DATA, not code -- adding FEMAP should be a
// row in a registry, not an enum member plus a boolean plus a ViewModel property plus a
// XAML slot.

using System;
using System.Collections.Generic;
using System.Linq;

namespace DWM.Shared.Tooling
{
    /// <summary>
    /// One licensed component of a tool -- a MATLAB toolbox, a FEMAP add-on -- with its
    /// version and, where it has one, the thing it cannot do.
    ///
    /// WHY THIS IS NOT JUST MORE TEXT ON THE TOOL. "Is MATLAB installed" is not the question
    /// anyone actually has; "can this machine solve that" is. The R2011a licence carries
    /// eleven toolboxes, and on 2026-08-05 this project's own recollected inventory was wrong
    /// by four of them -- which produced a confident and incorrect claim that two toolboxes
    /// the OOSEM process calls for were unlicensed. A remembered inventory is not an inventory.
    ///
    /// VERSIONS ARE PART OF THE RECORD, not decoration. Pinning the tool version per project
    /// is one of TOOLING.md's two carried-forward principles, and it was a version-less
    /// toolbox name that produced the wrong claim above.
    /// </summary>
    public sealed class ToolComponent
    {
        public string Name { get; init; } = string.Empty;

        /// <summary>As the tool reports it -- MATLAB's `ver` output, not a marketing name.</summary>
        public string Version { get; init; } = string.Empty;

        /// <summary>
        /// What this component cannot do. Same purpose as
        /// <see cref="ToolDescriptor.KnownLimitation"/> one level down, and the same
        /// justification: limits are what get assumed wrongly, not features.
        /// </summary>
        public string? KnownLimitation { get; init; }

        public override string ToString() =>
            KnownLimitation is null ? $"{Name} {Version}" : $"{Name} {Version} -- {KnownLimitation}";
    }

    public sealed class ToolDescriptor
    {
        /// <summary>Stable slug, e.g. "matlab", "mystran". Used in project files -- never renamed casually.</summary>
        public string Id { get; init; } = string.Empty;

        public string DisplayName { get; init; } = string.Empty;

        public ToolKind Kind { get; init; }

        /// <summary>
        /// COM ProgIDs, MOST SPECIFIC FIRST. The order is the whole point.
        ///
        /// A generic ProgID like "Matlab.Application" resolves to ONE CLSID -- whichever
        /// release registered last -- and an attach then searches the Running Object Table for
        /// exactly that CLSID. On a machine with R2011a and R2025b, the generic ProgID MISSES
        /// an open R2011a and launches R2025b instead. Keeping the versioned entries first, and
        /// letting a project pin one, is the only reliable way to reach a specific release.
        /// Learned the hard way: SCOPE.md 2026-08-03.
        /// </summary>
        public IReadOnlyList<string> ProgIds { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Candidate executable locations for <see cref="ToolKind.BatchExecutable"/>, in
        /// preference order. Probed with plain existence checks; the first hit wins.
        /// These are GUESSES at conventional install paths and are meant to be overridden --
        /// see <see cref="ToolRegistry.WithOverride"/>.
        /// </summary>
        public IReadOnlyList<string> ExecutableCandidates { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Install roots to search when no candidate path matched, e.g. C:\Mystran. Searched
        /// a few levels deep because installers disagree about whether the binary sits at the
        /// root, under bin/, or in a version-stamped subfolder -- and making the user hunt for
        /// their own install path is a poor substitute for looking.
        /// </summary>
        public IReadOnlyList<string> ExecutableSearchRoots { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Glob patterns to match under <see cref="ExecutableSearchRoots"/>, e.g. "mystran*.exe".
        ///
        /// PATTERNS RATHER THAN NAMES because a versioned filename is the norm, not the
        /// exception: MYSTRAN 19 ships as mystran-19.0.0-windows-x86_64.exe. Searching for an
        /// exact "mystran.exe" found nothing on a machine where MYSTRAN was plainly installed,
        /// and pinning the versioned name instead would just move the breakage to the next
        /// upgrade. Falls back to the candidate filenames when empty.
        /// </summary>
        public IReadOnlyList<string> ExecutableSearchPatterns { get; init; } = Array.Empty<string>();

        /// <summary>Localhost ping endpoint for <see cref="ToolKind.InteractiveHttp"/>.</summary>
        public string? HttpPingUrl { get; init; }

        /// <summary>
        /// File extensions this tool authors, lower-case with the dot. Used to offer "Edit"
        /// on an artifact by launching its owning tool, and to spot an artifact whose tool
        /// is not installed.
        /// </summary>
        public IReadOnlyList<string> ArtifactExtensions { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Extensions this tool PRODUCES as results rather than authors, e.g. MYSTRAN's .f06.
        /// Separated from ArtifactExtensions because a run's outputs are what freshness checks
        /// apply to, and its inputs are not.
        /// </summary>
        public IReadOnlyList<string> ResultExtensions { get; init; } = Array.Empty<string>();

        /// <summary>
        /// One line on what this tool cannot do, shown in the UI next to it. Present because
        /// every wrong assumption in this project so far has been about a tool's LIMITS rather
        /// than its features -- DATCOM knowing nothing about rotors, a generic ProgID not
        /// meaning "whichever is running", addpath succeeding on a folder with no code in it.
        /// </summary>
        public string? KnownLimitation { get; init; }

        /// <summary>
        /// Licensed components -- toolboxes, blocksets, add-ons -- version-stamped, with their
        /// own limits. Empty for a tool that is one indivisible thing, like MYSTRAN.
        /// </summary>
        public IReadOnlyList<ToolComponent> Components { get; init; } = Array.Empty<ToolComponent>();

        /// <summary>True if a component with this name is licensed. Case-insensitive.</summary>
        public bool HasComponent(string name) =>
            name is not null &&
            Components.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// The tool's own limitation plus every component limitation, one per line.
        ///
        /// Folded into a single string ON PURPOSE, because the UI already renders
        /// <see cref="KnownLimitation"/> and a brand-new field that nothing displays would
        /// repeat the run-history bug -- warnings collected for two builds into a control that
        /// never showed them. A limit nobody can read is not a limit.
        /// </summary>
        public string? AllLimitations
        {
            get
            {
                var lines = new List<string>();
                if (!string.IsNullOrWhiteSpace(KnownLimitation)) lines.Add(KnownLimitation!);
                foreach (var c in Components)
                    if (!string.IsNullOrWhiteSpace(c.KnownLimitation))
                        lines.Add($"{c.Name} {c.Version}: {c.KnownLimitation}");
                return lines.Count == 0 ? null : string.Join("\n", lines);
            }
        }

        public override string ToString() => $"{DisplayName} ({Id}, {Kind})";
    }

    /// <summary>
    /// How much is actually known about a tool right now. FOUND AND RUNNING ARE DIFFERENT
    /// THINGS and the difference is not pedantic: ToolStatusService's MATLAB dot has always
    /// meant "a COM server is registered", which is not running, not licensed, and not the
    /// release the project needs.
    /// </summary>
    public enum ToolAvailability
    {
        /// <summary>Not probed yet.</summary>
        Unknown,

        /// <summary>Probed, nothing there.</summary>
        NotFound,

        /// <summary>
        /// Installed and locatable -- an executable on disk, or a registered COM server.
        /// THE CEILING FOR A BATCH TOOL: MYSTRAN can never report more than this, because
        /// there is nothing to be connected to.
        /// </summary>
        Found,

        /// <summary>A process is up and answering.</summary>
        Running,

        /// <summary>A session is open against it and commands have succeeded.</summary>
        Connected
    }

    public sealed class ToolStatus
    {
        public string ToolId { get; init; } = string.Empty;
        public ToolAvailability Availability { get; init; }

        /// <summary>Which ProgID or executable path answered. Null when nothing did.</summary>
        public string? ResolvedVia { get; init; }

        /// <summary>Human-readable detail for the UI tooltip -- why not found, which version.</summary>
        public string? Detail { get; init; }

        public DateTime ProbedAtUtc { get; init; }

        public bool IsUsable =>
            Availability is ToolAvailability.Found
                         or ToolAvailability.Running
                         or ToolAvailability.Connected;

        public static ToolStatus NotFound(string toolId, string? detail = null) => new()
        {
            ToolId = toolId,
            Availability = ToolAvailability.NotFound,
            Detail = detail,
            ProbedAtUtc = DateTime.UtcNow
        };
    }
}
