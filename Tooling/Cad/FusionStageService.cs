// FusionStageService.cs
// The CAD stage's orchestration: ask Fusion for what MATLAB needs, and refuse answers that
// cannot be true.
//
// WHY MASS PROPERTIES ARE THE FIRST JOB
//
// The CAD stage's real output is not a picture, it is the numbers a mechanism model runs on:
// mass, centre of mass and inertia per component. That is the CAD -> Simscape link the
// 2026-07-06 Fusion2Simscape entry was circling, and it is the only thing this stage produces
// that anything downstream consumes.
//
// THE TRAP THIS SERVICE EXISTS TO CATCH, ALREADY IN SCOPE.md
//
// Fusion's free tier allows 10 active documents. A design built from External Component
// References can exceed that, and the surplus components drop to Inactive (Read-Only) --
// at which point physicalProperties MAY SILENTLY RETURN 0 rather than raising. Zero mass, no
// error, straight into a Simulink model that will run and produce plausible nonsense.
//
// That is this project's signature failure in a new place, so it is refused here rather than
// reported: a component with no mass is treated as a FAILED READ, not as a light component.
// The recorded mitigation -- keep mechanism designs monolithic rather than externally
// referenced -- is in the message, because the fix is in the CAD file and not in this code.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DWM.Shared.Tooling.Cad
{
    public sealed class FusionMassProperties
    {
        public string ComponentName { get; init; } = string.Empty;

        /// <summary>Kilograms. Zero or negative is treated as a failed read, never as a fact.</summary>
        public double MassKg { get; init; }

        /// <summary>Centre of mass, metres, in the document's coordinate system.</summary>
        public double[] CentreOfMass { get; init; } = Array.Empty<double>();

        /// <summary>
        /// Moments of inertia as the add-in returned them, UNCONVERTED.
        ///
        /// Fusion's internal length unit is the centimetre, so these are believed to be
        /// kg*cm^2 -- but that has not been checked against a hand-computed solid, and
        /// silently applying a factor that might be wrong is worse than handing over raw
        /// numbers with a label. Nothing in this service makes a decision from them.
        /// </summary>
        public double[] Inertia { get; init; } = Array.Empty<double>();

        /// <summary>
        /// How many bodies the component holds, when the add-in says.
        ///
        /// LOAD-BEARING FOR THE ZERO-MASS CHECK. An assembly component that holds only
        /// sub-components has no bodies and therefore no mass of its own -- that is correct,
        /// not a fault. A component WITH bodies reporting zero mass is the Inactive
        /// (Read-Only) failure. Without this the two are the same number and refusing both
        /// would reject healthy models.
        ///
        /// Null when the add-in did not report it, which is treated as "assume it has
        /// bodies" -- the cautious direction, since the alternative is letting a real zero
        /// through on a missing field.
        /// </summary>
        public int? BodyCount { get; init; }

        /// <summary>
        /// Zero mass that cannot be explained by the component being empty.
        /// </summary>
        public bool IsSuspiciouslyMassless => MassKg <= 0 && (BodyCount is null or > 0);

        public override string ToString() => $"{ComponentName}: {MassKg:G6} kg";
    }

    public sealed class FusionStageResult
    {
        public ToolRun Run { get; init; } = null!;
        public IReadOnlyList<FusionMassProperties> Components { get; init; } =
            Array.Empty<FusionMassProperties>();
        public bool Succeeded => Run.ProducedUsableOutput;
    }

    public sealed class FusionStageService
    {
        private readonly Func<IFusionSession> _sessionFactory;
        private readonly FusionProtocol _protocol;

        public FusionStageService(Func<IFusionSession> sessionFactory, FusionProtocol? protocol = null)
        {
            _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
            _protocol = protocol ?? new FusionProtocol();
        }

        /// <summary>
        /// Read every component's mass properties out of the open Fusion document.
        ///
        /// Takes no document path, and that is not an oversight: nothing outside Fusion can
        /// make it open a file. The add-in operates on whatever is active, so the caller's job
        /// is to say which document was read, not to choose it. The name comes back in the run
        /// record for exactly that reason.
        /// </summary>
        public async Task<FusionStageResult> ReadMassPropertiesAsync(
            string stageId = "cad", CancellationToken ct = default)
        {
            var startedUtc = DateTime.UtcNow;
            var warnings = new List<string>();

            IFusionSession? session = null;
            try
            {
                session = _sessionFactory();

                if (!await session.PingAsync(ct).ConfigureAwait(false))
                {
                    return Failed(stageId, startedUtc,
                        $"The Fusion add-in did not answer at {_protocol.BaseAddress}.\n\n" +
                        "This cannot tell Fusion-not-running from add-in-not-loaded -- both " +
                        "refuse the connection identically. Open Fusion, then check " +
                        "Utilities > Scripts and Add-Ins > Add-Ins.");
                }

                var reply = await session
                    .InvokeAsync(_protocol.MassPropertiesCommand, ct: ct)
                    .ConfigureAwait(false);

                if (!reply.Ok)
                    return Failed(stageId, startedUtc, reply.Error ?? "Fusion refused the request.");

                if (reply.Json is null)
                    return Failed(stageId, startedUtc,
                        "The add-in answered but not with JSON. Body was:\n" + Trim(reply.RawBody));

                var components = Parse(reply.Json.Value, out var parseWarnings);
                warnings.AddRange(parseWarnings);

                if (components.Count == 0)
                    return Failed(stageId, startedUtc,
                        "Fusion returned no components. If a document is open, the add-in may be " +
                        "reading the wrong one -- it operates on the ACTIVE document, which is " +
                        "whatever has focus rather than whatever this project names.");

                // NOTHING WEIGHS ANYTHING. Caught on 2026-08-05 by a real read that this
                // service would otherwise have called a success: an empty design returns its
                // root component alone -- no bodies, so no mass, and the zero-mass guard below
                // correctly stays quiet because a bodiless component is legitimately massless.
                //
                // Every individual judgement was right and the conclusion was still useless.
                // A design where NOTHING has mass is not a light design; it is the wrong
                // document, and saying so is the difference between a caller retrying with the
                // right tab focused and a caller believing their rotor weighs nothing.
                if (components.All(c => c.MassKg <= 0))
                    return Failed(stageId, startedUtc,
                        $"Nothing in the active document has any mass. Read {components.Count} " +
                        $"component(s): {string.Join(", ", components.Select(c => c.ComponentName))}.\n\n" +
                        "This is almost always the WRONG DOCUMENT. The add-in reads whatever " +
                        "is ACTIVE in Fusion, not whatever this project names, and an empty " +
                        "design returns just its root component. Bring the intended document " +
                        "to the front and retry.\n\n" +
                        "Note that an unsaved document does not survive a Fusion restart.");

                // THE ZERO-MASS REFUSAL. See the file header: an Inactive (Read-Only)
                // component can report 0 rather than raising, and zero mass reaching a
                // Simulink model produces a simulation that runs and means nothing.
                var weightless = components.Where(c => c.IsSuspiciouslyMassless).ToList();
                if (weightless.Count > 0)
                    return Failed(stageId, startedUtc,
                        $"{weightless.Count} component(s) reported ZERO OR NEGATIVE MASS: " +
                        string.Join(", ", weightless.Select(c => c.ComponentName)) + ".\n\n" +
                        "This is refused rather than passed on. The usual cause is Fusion's " +
                        "10-active-document limit: a design built from External Component " +
                        "References pushes components to Inactive (Read-Only), where mass " +
                        "properties can come back as 0 WITHOUT an error.\n\n" +
                        "The fix is in the CAD file, not here -- keep the mechanism monolithic " +
                        "(internal components in one document) rather than externally " +
                        "referenced. A single file with many internal components counts as one " +
                        "of the ten.");

                return new FusionStageResult
                {
                    Run = ToolRun.Complete(
                        stageId, ToolRegistry.Fusion, startedUtc,
                        // NOTHING ON DISK. This reads numbers out of a live document; naming
                        // an expected output would make the freshness check fail every time
                        // and mean nothing on the occasions it passed.
                        expectedOutputs: Array.Empty<string>(),
                        warnings: warnings,
                        resolvedVia: _protocol.BaseAddress.ToString()),
                    Components = components
                };
            }
            catch (Exception ex)
            {
                return Failed(stageId, startedUtc, ex.Message);
            }
            finally
            {
                session?.Dispose();
            }
        }

        /// <summary>
        /// Reads the component list out of whatever shape the add-in sent.
        ///
        /// Tolerant on purpose: the field names are unverified, so a missing centre of mass
        /// is a warning rather than a failure. MASS IS NOT TOLERATED THAT WAY -- it is the one
        /// number downstream cannot do without, and a default of zero would be indistinguishable
        /// from the Inactive-component bug this service exists to catch.
        /// </summary>
        private static List<FusionMassProperties> Parse(JsonElement root, out List<string> warnings)
        {
            warnings = new List<string>();
            var list = new List<FusionMassProperties>();

            if (!root.TryGetProperty("components", out var components) ||
                components.ValueKind != JsonValueKind.Array)
            {
                warnings.Add(
                    "No 'components' array in the reply. The add-in's field names are " +
                    "unverified -- correct them with a FusionProtocol rather than editing " +
                    "the parser.");
                return list;
            }

            foreach (var item in components.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString() ?? "(unnamed)"
                    : "(unnamed)";

                if (!item.TryGetProperty("mass", out var m) || m.ValueKind != JsonValueKind.Number)
                {
                    // NOT defaulted to zero. Absent mass and zero mass are different problems
                    // and the second one is the dangerous one.
                    warnings.Add($"'{name}' has no numeric 'mass' field; skipped.");
                    continue;
                }

                list.Add(new FusionMassProperties
                {
                    ComponentName = name,
                    MassKg = m.GetDouble(),
                    CentreOfMass = Numbers(item, "centreOfMass", "centerOfMass", "com"),
                    Inertia = Numbers(item, "inertia", "momentsOfInertia"),
                    BodyCount = item.TryGetProperty("bodyCount", out var bc)
                                && bc.ValueKind == JsonValueKind.Number
                        ? bc.GetInt32()
                        : null
                });
            }

            return list;
        }

        private static double[] Numbers(JsonElement item, params string[] names)
        {
            foreach (var name in names)
            {
                if (item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array)
                    return v.EnumerateArray()
                        .Where(e => e.ValueKind == JsonValueKind.Number)
                        .Select(e => e.GetDouble())
                        .ToArray();
            }
            return Array.Empty<double>();
        }

        private static string Trim(string body) =>
            body.Length <= 400 ? body : body[..400] + "...";

        private static FusionStageResult Failed(string stageId, DateTime startedUtc, string message) => new()
        {
            Run = ToolRun.Complete(stageId, ToolRegistry.Fusion, startedUtc,
                Array.Empty<string>(), failureMessage: message)
        };
    }
}
