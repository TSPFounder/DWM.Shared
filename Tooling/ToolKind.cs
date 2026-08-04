// ToolKind.cs
// The THREE SHAPES an external engineering tool comes in. This distinction is the reason
// this namespace exists.
//
// ToolStatusService models exactly one shape -- a long-lived process that advertises itself
// over COM or localhost HTTP -- as four hardcoded booleans. That was fine while the tools
// were MATLAB, Fusion, UModel and Unreal. It stops being fine the moment MYSTRAN arrives:
// a batch solver has no COM server, no port, and no notion of "connected". It cannot be
// represented in that model AT ALL -- not badly, just not at all.
//
// See SCOPE.md's Architectural Fragility Audit, item 4, which predicted exactly this.

namespace DWM.Shared.Tooling
{
    public enum ToolKind
    {
        /// <summary>
        /// A long-lived application driven through COM automation: MATLAB, FEMAP, UModel.
        /// Can be attached to when already running, or launched. Versioned ProgIDs matter --
        /// see ToolDescriptor.ProgIds and SCOPE.md 2026-08-03.
        /// </summary>
        InteractiveCom,

        /// <summary>
        /// A long-lived application reached over localhost HTTP, because it hosts an add-in
        /// rather than a COM server: Fusion 360 on 127.0.0.1:18750.
        /// </summary>
        InteractiveHttp,

        /// <summary>
        /// A one-shot process: write an input file, run an executable, read the artifacts it
        /// leaves behind. MYSTRAN and Digital DATCOM. THERE IS NOTHING TO CONNECT TO, so the
        /// most such a tool can ever report is "an executable exists at this path" -- which is
        /// why <see cref="ToolAvailability"/> distinguishes Found from Running.
        /// </summary>
        BatchExecutable,

        /// <summary>
        /// No automation at all; the stage produces or consumes files and a human does the
        /// work. Kept as a first-class option so a tool without an API is representable
        /// honestly rather than by pretending it has one.
        /// </summary>
        FileOnly
    }
}
