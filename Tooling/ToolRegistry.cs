// ToolRegistry.cs
// The tools DWMStudio knows about, as data. Adding one is a row here, not a schema change.
//
// WHAT IS VERIFIED HERE AND WHAT IS NOT, as of 2026-08-03:
//
//   VERIFIED  MATLAB's ProgIDs, from `reg query HKCR /f "matlab.application" /k` on the
//             machine -- it returned Matlab.Application.7.12 and Matlab.Application.25.2,
//             which is exactly the two-release situation the ordering below exists for.
//   VERIFIED  The FEMAP and MYSTRAN install DIRECTORIES (C:\FEMAPv102, C:\Mystran).
//   GUESSED   Every executable FILENAME, FEMAP's ProgID, and all of Fusion's and Unreal's
//             paths.
//
// The distinction matters because a wrong guess here fails as "tool not installed", which
// reads like a fact and is not one. WithOverride exists so correcting it is a settings
// change rather than a rebuild.

using System;
using System.Collections.Generic;
using System.Linq;

namespace DWM.Shared.Tooling
{
    public sealed class ToolRegistry
    {
        public const string Matlab = "matlab";
        public const string Fusion = "fusion";
        public const string UModel = "umodel";
        public const string Unreal = "unreal";
        public const string Femap = "femap";
        public const string Mystran = "mystran";
        public const string Datcom = "datcom";

        private readonly Dictionary<string, ToolDescriptor> _tools;

        public ToolRegistry(IEnumerable<ToolDescriptor>? tools = null)
        {
            _tools = (tools ?? BuiltIn()).ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        }

        public IReadOnlyCollection<ToolDescriptor> All => _tools.Values;

        public ToolDescriptor? Find(string toolId) =>
            toolId is not null && _tools.TryGetValue(toolId, out var t) ? t : null;

        public ToolDescriptor Require(string toolId) =>
            Find(toolId) ?? throw new KeyNotFoundException(
                $"No tool registered with id '{toolId}'. Known ids: " +
                string.Join(", ", _tools.Keys.OrderBy(k => k)));

        /// <summary>
        /// A copy with one descriptor replaced. How a wrong default install path gets fixed
        /// without a rebuild -- the registry is data, so a settings file can carry overrides.
        /// </summary>
        public ToolRegistry WithOverride(ToolDescriptor replacement)
        {
            if (replacement is null) throw new ArgumentNullException(nameof(replacement));
            var next = new Dictionary<string, ToolDescriptor>(_tools, StringComparer.OrdinalIgnoreCase)
            {
                [replacement.Id] = replacement
            };
            return new ToolRegistry(next.Values);
        }

        /// <summary>Tools that can author the given file extension, e.g. ".bdf" -> FEMAP.</summary>
        public IReadOnlyList<ToolDescriptor> ToolsForExtension(string extension)
        {
            if (string.IsNullOrWhiteSpace(extension)) return Array.Empty<ToolDescriptor>();
            var ext = extension.StartsWith('.') ? extension : "." + extension;
            return _tools.Values
                .Where(t => t.ArtifactExtensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        // ------------------------------------------------------------------
        public static IReadOnlyList<ToolDescriptor> BuiltIn() => new List<ToolDescriptor>
        {
            new()
            {
                Id = Matlab,
                DisplayName = "MATLAB / Simulink",
                Kind = ToolKind.InteractiveCom,
                // VERSIONED FIRST. The MVP turbine model runs under R2011a (SCOPE.md
                // 2026-08-02) and the generic entry reaches whichever release registered last.
                ProgIds = new[] { "Matlab.Application.7.12", "Matlab.Application.25.2", "Matlab.Application" },
                // For LAUNCHING a session the user keeps, rather than automating one. A MATLAB
                // started through COM is owned by its client and exits when the last reference
                // is released, so a hand-off has to start the executable instead. R2011a first,
                // matching the ProgID order and for the same reason.
                ExecutableCandidates = new[]
                {
                    @"C:\Program Files\MATLAB\R2011a\bin\matlab.exe",
                    @"C:\Program Files\MATLAB\R2025b\bin\matlab.exe",
                    "matlab.exe"
                },
                ArtifactExtensions = new[] { ".m", ".mdl", ".slx", ".mat" },
                ResultExtensions = new[] { ".csv" },
                KnownLimitation =
                    "The generic ProgID resolves to ONE release. Pin a versioned ProgID per " +
                    "project or an attach will miss the release you have open.",

                // THE R2011a LICENCE, FROM `ver`, NOT FROM MEMORY. Licence 639293.
                //
                // Recorded 2026-08-05 after this project's own remembered inventory turned out
                // to be wrong by four products -- it named six toolboxes where eleven are
                // installed, and on the strength of that omission a confident claim was made
                // that Simulink Control Design and Simulink Design Optimization were NOT
                // licensed and would block OOSEM phase H. Both are licensed. The full list
                // lives here so the next such question is answered by reading rather than
                // recalling.
                Components = new[]
                {
                    new ToolComponent { Name = "Simulink",               Version = "7.7" },
                    new ToolComponent { Name = "Aerospace Blockset",     Version = "3.7" },
                    // A distinct product from the Blockset, and one of the four the earlier
                    // inventory missed by treating "Aerospace" as a single item.
                    new ToolComponent { Name = "Aerospace Toolbox",      Version = "2.7" },
                    new ToolComponent { Name = "Control System Toolbox", Version = "9.1" },
                    new ToolComponent { Name = "Fixed-Point Toolbox",    Version = "3.3" },
                    new ToolComponent { Name = "Optimization Toolbox",   Version = "6.0" },
                    new ToolComponent
                    {
                        Name = "Partial Differential Equation Toolbox",
                        Version = "1.0.18",
                        // CONFIRMED 2026-08-05, and the reason ToolComponent carries a limit of
                        // its own. The R2011a pdetool line is 2-D only; 3-D geometry arrived in
                        // far later releases. It does not announce itself -- a 2-D solve of a
                        // 3-D problem returns plausible numbers rather than an error, which is
                        // this project's most-repeated failure shape.
                        KnownLimitation =
                            "2-D ONLY. No 3-D geometry in this release, and a 2-D solve of a " +
                            "3-D problem returns plausible numbers rather than an error. " +
                            "Structural work goes to MYSTRAN/FEMAP, which are 3-D."
                    },
                    new ToolComponent { Name = "Simulink Control Design",      Version = "3.3" },
                    new ToolComponent { Name = "Simulink Design Optimization", Version = "1.2.1" },
                    new ToolComponent { Name = "Simulink Verification and Validation", Version = "3.1" },
                    new ToolComponent { Name = "Symbolic Math Toolbox",        Version = "5.6" }
                }
            },
            new()
            {
                Id = Fusion,
                DisplayName = "Fusion 360",
                Kind = ToolKind.InteractiveHttp,
                HttpPingUrl = "http://127.0.0.1:18750/ping",
                ArtifactExtensions = new[] { ".f3d", ".f3z" },
                ResultExtensions = new[] { ".step", ".stp", ".stl" },
                KnownLimitation =
                    "Reachable only while the DWM add-in is loaded; a running Fusion without " +
                    "the add-in looks identical to no Fusion at all."
            },
            new()
            {
                Id = UModel,
                DisplayName = "Altova UModel",
                Kind = ToolKind.InteractiveCom,
                ProgIds = new[] { "UModel.Application" },
                ArtifactExtensions = new[] { ".ump" }
            },
            new()
            {
                Id = Unreal,
                DisplayName = "Unreal Engine 5.3",
                Kind = ToolKind.BatchExecutable,
                ExecutableCandidates = new[]
                {
                    @"C:\Program Files\Epic Games\UE_5.3\Engine\Binaries\Win64\UnrealEditor.exe"
                },
                ArtifactExtensions = new[] { ".uproject" },
                KnownLimitation =
                    "Launch only. The Studio starts the packaged build and records that it " +
                    "ran; it cannot drive what happens inside, or read anything back. Live " +
                    "control would need the editor's Python Remote Execution plugin enabled."
            },
            new()
            {
                Id = Femap,
                DisplayName = "Siemens FEMAP 10.2",
                Kind = ToolKind.InteractiveCom,
                ProgIds = new[] { "femap.model" },
                // Install directory confirmed 2026-08-03; the executable NAME is still a guess.
                // Carried even though FEMAP is COM-driven, because a path is what "Edit" needs
                // to launch it, and because an install whose COM server is not registered would
                // otherwise report NotFound while plainly being installed.
                ExecutableCandidates = new[] { @"C:\FEMAPv102\femap.exe" },
                ExecutableSearchRoots = new[] { @"C:\FEMAPv102" },
                ExecutableSearchPatterns = new[] { "femap*.exe" },
                ArtifactExtensions = new[] { ".modfem", ".neu" },
                ResultExtensions = new[] { ".bdf", ".dat" },
                KnownLimitation =
                    "Pre- and post-processor only -- IT DOES NOT SOLVE. Pair it with MYSTRAN: " +
                    "FEMAP meshes and writes the deck, MYSTRAN solves, FEMAP reads the results."
            },
            new()
            {
                Id = Mystran,
                DisplayName = "MYSTRAN",
                Kind = ToolKind.BatchExecutable,
                // Install directory confirmed 2026-08-03. The executable name and whether it
                // sits in a bin/ subfolder are still guesses, hence several candidates.
                // CONFIRMED 2026-08-03: the installed binary is version-stamped,
                // C:\Mystran\mystran-19.0.0-windows-x86_64.exe. The exact path is listed first
                // because it is a cheap direct hit, and the PATTERN below is what actually
                // matters -- pinning this filename alone would break on the next release.
                ExecutableCandidates = new[]
                {
                    @"C:\Mystran\mystran-19.0.0-windows-x86_64.exe",
                    @"C:\Mystran\mystran.exe",
                    "mystran.exe"          // on PATH
                },
                ExecutableSearchPatterns = new[] { "mystran*.exe" },
                // Confirmed install root, 2026-08-03. Searched because none of the exact
                // paths above matched on the machine that has it installed there.
                ExecutableSearchRoots = new[] { @"C:\Mystran", @"C:\MYSTRAN" },
                ArtifactExtensions = new[] { ".bdf", ".dat", ".nas" },
                ResultExtensions = new[] { ".f06", ".op2", ".neu" },
                KnownLimitation =
                    "No API of any kind. Status can never be better than 'an executable exists " +
                    "here'. Results are read from the .f06/.op2 it leaves on disk, which means " +
                    "a stale result file is indistinguishable from a fresh one without checking " +
                    "timestamps -- the same hazard as a stale sim CSV."
            },
            new()
            {
                Id = Datcom,
                DisplayName = "Digital DATCOM",
                Kind = ToolKind.BatchExecutable,
                ExecutableCandidates = new[] { "datcom.exe" },
                ArtifactExtensions = new[] { ".dcm", ".inp" },
                ResultExtensions = new[] { ".out" },
                KnownLimitation =
                    "Fixed-wing aerodynamics only. IT KNOWS NOTHING ABOUT ROTORS, PROPELLERS OR " +
                    "POWERED LIFT, so it cannot answer a tiltrotor's hover or transition."
            }
        };
    }
}
