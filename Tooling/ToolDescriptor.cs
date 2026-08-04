// ToolDescriptor.cs / ToolAvailability.cs / ToolStatus.cs
// What DWMStudio knows about one external tool. DATA, not code -- adding FEMAP should be a
// row in a registry, not an enum member plus a boolean plus a ViewModel property plus a
// XAML slot.

using System;
using System.Collections.Generic;

namespace DWM.Shared.Tooling
{
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
