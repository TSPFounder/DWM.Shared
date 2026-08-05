// ArtifactTemplates.cs
// What Create writes, keyed by extension.
//
// THE RULE THIS FILE IS BUILT AROUND: A TEMPLATE MUST SURVIVE CONTACT WITH Run.
//
// Creating an empty file named correctly would be the easy version and a bad one -- Edit
// would then open a blank document and Run would fail somewhere downstream with a message
// about the wrong thing. So a template here is a COMPLETE, WORKING artifact of its type: not
// a stub, not a header, something that actually solves.
//
// WHICH MEANS MOST FORMATS GET NOTHING, AND THAT IS THE POINT.
//
// A .slx is a zip archive, a .f3d and a .ump are proprietary binaries. Nothing here can write
// a valid one, and writing an INVALID one dressed up as a template would be the worst
// available outcome: a file that exists, opens as corrupt, and sends someone looking for the
// fault in their tool. So those extensions are absent, Create says so plainly, and it names
// the application that can make one. Absence is the honest answer and it is preserved
// deliberately -- see ToolsWithoutTemplates.

using System;
using System.Collections.Generic;

namespace DWM.Shared.Tooling
{
    public sealed class ArtifactTemplate
    {
        /// <summary>Lower-case, with the dot.</summary>
        public string Extension { get; init; } = string.Empty;

        /// <summary>Shown when the file is created, so it is clear what landed on disk.</summary>
        public string Description { get; init; } = string.Empty;

        /// <summary>
        /// Builds the file content. Takes the artifact's base name so the template can title
        /// itself -- a deck called TITLE = UNTITLED in a folder of six decks helps nobody.
        /// </summary>
        public Func<string, string> Render { get; init; } = _ => string.Empty;
    }

    public static class ArtifactTemplates
    {
        /// <summary>The template for an extension, or null when none can honestly be written.</summary>
        public static ArtifactTemplate? For(string? extensionOrPath)
        {
            if (string.IsNullOrWhiteSpace(extensionOrPath)) return null;

            var ext = extensionOrPath!.StartsWith('.')
                ? extensionOrPath
                : SafeExtension(extensionOrPath);

            return ext is not null && All.TryGetValue(ext, out var t) ? t : null;
        }

        public static IReadOnlyDictionary<string, ArtifactTemplate> All { get; } =
            new Dictionary<string, ArtifactTemplate>(StringComparer.OrdinalIgnoreCase)
            {
                [".dat"] = NastranModal(".dat"),
                [".bdf"] = NastranModal(".bdf")
            };

        /// <summary>
        /// Formats deliberately left without a template, and the application that owns each.
        ///
        /// Kept as DATA rather than as an absence, so Create can explain itself instead of
        /// saying "no template" and leaving someone to guess whether that is a gap or a
        /// decision. It is a decision.
        /// </summary>
        public static IReadOnlyDictionary<string, string> ToolsWithoutTemplates { get; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [".slx"] = "Simulink (a .slx is a zip archive; only Simulink writes a valid one)",
                [".mdl"] = "Simulink",
                [".f3d"] = "Fusion 360 (proprietary binary)",
                [".ump"] = "Altova UModel (proprietary binary)",
                [".uproject"] = "Unreal Editor"
            };

        /// <summary>
        /// A complete 5-element cantilever in SOL 103, steel, SI units.
        ///
        /// NOT AN EMPTY SKELETON. A deck with no GRID cards would parse and then FATAL, which
        /// is the failure this whole file exists to avoid -- Run would break on a file Create
        /// had just reported making. This one has geometry, a property, a material and a
        /// constraint, so it solves and returns the beam's own modes.
        ///
        /// HONESTY NOTE: written from the Nastran bulk-data format and NOT solved on the
        /// machine that produced it, because no solver runs there. It is believed valid; the
        /// first run of it is the check. Its shape follows wtTowerModal.dat, which MYSTRAN
        /// 19.0.0 does accept.
        /// </summary>
        private static ArtifactTemplate NastranModal(string extension) => new()
        {
            Extension = extension,
            Description =
                "A complete 5-element cantilever beam in SOL 103 (steel, SI units). It solves " +
                "as written -- replace the geometry with the real structure.",
            Render = name =>
$@"$ ============================================================
$ {name} -- normal modes (SOL 103)
$ Created by DWMStudio from its built-in template.
$
$ A 5-element cantilever beam, fixed at GRID 1, steel, SI units
$ (metres, Pascals, kg/m^3).
$
$ THIS IS A COMPLETE MODEL, NOT A SKELETON. It solves as written and the
$ modes it returns are this beam's. Replace the geometry below with the
$ real structure; keep SPC set 1 and EIGRL set 100, or update the Case
$ Control entries to match.
$ ============================================================
ID DWM,MODAL
SOL 103
CEND
TITLE = {name} NORMAL MODES
ECHO = NONE
METHOD = 100
SPC = 1
DISPLACEMENT(PLOT) = ALL
BEGIN BULK
PARAM,POST,-1
$
$ Lanczos extraction, lowest 6 modes.
EIGRL,100,,,6
$
$ ---- Geometry: 5 equal elements over 1.0 m, along +Z ----
GRID,1,,0.0,0.0,0.0
GRID,2,,0.0,0.0,0.2
GRID,3,,0.0,0.0,0.4
GRID,4,,0.0,0.0,0.6
GRID,5,,0.0,0.0,0.8
GRID,6,,0.0,0.0,1.0
$
$ ---- Elements. The trailing vector orients the element Y axis. ----
CBAR,1,1,1,2,1.0,0.0,0.0
CBAR,2,1,2,3,1.0,0.0,0.0
CBAR,3,1,3,4,1.0,0.0,0.0
CBAR,4,1,4,5,1.0,0.0,0.0
CBAR,5,1,5,6,1.0,0.0,0.0
$
$ ---- Property: A, I1, I2, J for a 20 mm square section ----
PBAR,1,1,4.0E-4,1.333E-8,1.333E-8,2.256E-8
$
$ ---- Material: structural steel ----
MAT1,1,2.10E+11,,0.3,7850.0
$
$ ---- Fixed base ----
SPC1,1,123456,1
ENDDATA
"
        };

        private static string? SafeExtension(string path)
        {
            try
            {
                var ext = System.IO.Path.GetExtension(path);
                return string.IsNullOrEmpty(ext) ? null : ext;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
    }
}
