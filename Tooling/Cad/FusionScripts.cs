// FusionScripts.cs
// Python that DWMStudio sends to the add-in's /scripts/execute route.
//
// WHY GENERATED SCRIPTS RATHER THAN ENDPOINTS
//
// Contract v1 has no mass-properties route, and adding one means changing an add-in that is
// installed, versioned and working. /scripts/execute already runs arbitrary Python on
// Fusion's main thread and returns whatever it printed, so the capability is reachable today
// without touching the add-in at all.
//
// THIS IS THE MATLAB STAGE'S PATTERN, ARRIVED AT FROM THE SAME CONSTRAINT. There,
// MatlabStageService wraps a command in a MATLAB-side try/catch and reads a sentinel variable
// back, because Execute returns error text as an ordinary string. Here the tool will not hand
// back a typed result either, so it is asked to print one. Both are "the tool has no return
// channel, so build one out of what it does have".
//
// WHICH MEANS THE SAME DISCIPLINE APPLIES. A script that raises is caught by the add-in and
// answered as {"success": false, "error": traceback} -- but a script that SUCCEEDS while
// printing nothing useful looks identical to one that worked. So every script here prints
// JSON on exactly one line, and prints nothing else.

using System;
using System.Text.Json;

namespace DWM.Shared.Tooling.Cad
{
    public static class FusionScripts
    {
        /// <summary>
        /// Print every component's mass properties as one line of JSON.
        ///
        /// THE LIST IS FLAT AND IT AGGREGATES. design.allComponents returns the assembly root
        /// AND its children, and the root's mass ALREADY CONTAINS the children's -- measured
        /// 2026-08-06, where the root reported 3,275,458.7247 kg with ZERO bodies of its own
        /// and the three blades reported 1,091,819.5749 kg each. Summing this list
        /// double-counts. FusionStageService warns about it; nothing here can prevent it.
        ///
        /// bodyCount IS PART OF THE CONTRACT, not a nicety -- but not for the reason first
        /// written here. It does NOT mean "no bodies, therefore no mass": the read above has
        /// bodyCount 0 alongside three thousand tonnes. It means a component with no bodies
        /// AND no mass holds nothing anywhere beneath it, which is a legitimate empty
        /// component; a component WITH bodies reporting zero mass is Fusion's Inactive
        /// (Read-Only) failure, where physicalProperties returns 0 without raising.
        ///
        /// Centre of mass is converted from Fusion's internal CENTIMETRES to metres here, and
        /// inertia from kg*cm^2 to kg*m^2 (x 1e-4). The inertia unit was previously labelled
        /// UNVERIFIED rather than converted; the 2026-08-06 read settles it by radius of
        /// gyration -- sqrt(I/m) = 1973, which is 19.73 m read as centimetres and 1973 m read
        /// as metres. The blade spans 1.5 to 60 m with its centre of mass at 15.98 m, so the
        /// centimetre reading lands where it must and the metre reading is 33x the blade.
        /// </summary>
        public const string MassProperties = @"
import json, adsk.core, adsk.fusion

def run(context):
    app = adsk.core.Application.get()
    design = adsk.fusion.Design.cast(app.activeProduct)
    if design is None:
        print(json.dumps({'error': 'No active Fusion design.'}))
        return

    out = []
    for i in range(design.allComponents.count):
        comp = design.allComponents.item(i)
        try:
            props = comp.getPhysicalProperties(
                adsk.fusion.CalculationAccuracy.HighCalculationAccuracy)
        except Exception:
            props = comp.physicalProperties

        entry = {
            'name': comp.name,
            'bodyCount': comp.bRepBodies.count,
            'mass': float(props.mass),
        }

        com = props.centerOfMass
        if com is not None:
            entry['centreOfMass'] = [com.x / 100.0, com.y / 100.0, com.z / 100.0]

        ok, xx, yy, zz, xy, yz, xz = props.getXYZMomentsOfInertia()
        if ok:
            entry['inertia'] = [v * 1e-4 for v in (xx, yy, zz, xy, yz, xz)]
            entry['inertiaUnits'] = 'kg*m^2'

        out.append(entry)

    print(json.dumps({'components': out}))
";

        /// <summary>
        /// Build the rotor by calling WindTurbineBlade.build_rotor.
        ///
        /// CALLS build_rotor, NOT run(). run() creates a document and ends in a modal
        /// ui.messageBox -- and a modal dialog inside /scripts/execute holds Fusion's main
        /// thread, which is the one thread the add-in needs to answer anything. The request
        /// would time out and every later request would queue behind the dialog.
        ///
        /// Reuses the active design when there is one. Fusion's free tier caps active
        /// documents at 10, and past that cap components drop to Inactive (Read-Only) where
        /// mass properties return 0 without raising -- so a build loop that opened a document
        /// each time would manufacture the failure the mass-properties script exists to expose.
        /// </summary>
        /// <param name="configOverrides">
        /// Merged over the script's CONFIG. Keys the script does not define are reported
        /// rather than ignored: a typo'd override that silently did nothing would leave the
        /// caller believing it had changed the blade.
        /// </param>
        public static string BuildRotor(object? configOverrides)
        {
            var json = configOverrides is null
                ? "{}"
                : JsonSerializer.Serialize(configOverrides);

            return @"
import json, sys, os, adsk.core, adsk.fusion

SEARCH = [
    os.environ.get('DWM_FUSION_SCRIPTS', ''),
    r'C:\DreamWorldMaker\Repos\DWM_Dev\Models\Fusion\MVP_WindTurbine',
]

def run(context):
    app = adsk.core.Application.get()

    wtb = None
    tried = []
    for path in SEARCH:
        if not path:
            continue
        full = os.path.abspath(path)
        tried.append(full)
        if os.path.isfile(os.path.join(full, 'WindTurbineBlade.py')):
            if full not in sys.path:
                sys.path.insert(0, full)
            import WindTurbineBlade as _wtb
            wtb = _wtb
            break

    if wtb is None:
        print(json.dumps({'error': 'WindTurbineBlade.py not found. Looked in: ' + '; '.join(tried)}))
        return

    if not hasattr(wtb, 'build_rotor'):
        print(json.dumps({'error':
            'This WindTurbineBlade.py has no build_rotor(design, cfg, log). '
            'It needs the refactored script, where run() is split into the interactive '
            'wrapper and build_rotor.'}))
        return

    design = adsk.fusion.Design.cast(app.activeProduct)
    if design is None:
        app.documents.add(adsk.core.DocumentTypes.FusionDesignDocumentType)
        design = adsk.fusion.Design.cast(app.activeProduct)

    cfg = dict(wtb.CONFIG)
    overrides = " + json + @"
    unknown = [k for k in overrides if k not in cfg]
    cfg.update(overrides)

    log = []
    if unknown:
        log.append('Ignored unknown config key(s): ' + ', '.join(sorted(unknown)))

    wtb.build_rotor(design, cfg, log)

    print(json.dumps({
        'log': log,
        'document': app.activeDocument.name,
        'componentCount': design.allComponents.count,
    }))
";
        }
    }
}
