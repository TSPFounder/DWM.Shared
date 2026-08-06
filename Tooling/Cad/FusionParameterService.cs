// FusionParameterService.cs
// Reading and driving a Fusion design's user parameters, and refusing the answers that cannot
// be true.
//
// WHY PARAMETERS ARE WORTH A SERVICE OF THEIR OWN
//
// Everything else the CAD stage does GENERATES geometry. This drives geometry that already
// exists: read what a design is parameterised on, change one number, let Fusion regenerate.
// That is the difference between "make a shape" and "explore a design", and the wind-turbine
// work needs the second one -- the blade script has a CONFIG dict precisely because somebody
// wanted to vary it.
//
// THE ROUTE IS LESS PROVEN THAN THE REST OF THIS STAGE, AND THAT IS SAID OUT LOUD.
//
// /scripts/execute has been driven against real Fusion many times. GET and PATCH on
// /documents/active/parameters have been driven ZERO times. They are read from
// FusionParameterCollection, which was written against DWM-Fusion-AddIn's contract -- but
// "the client believes this route exists" and "the route exists" are different claims, and
// this project has been caught by that distinction more than once. Every failure below names
// the fallback that IS proven: SetParameterOp through the `operations` command.
//
// THE UNIT TRAP, WHICH IS THE SAME ONE INERTIA HAD
//
// ICADParameter.Value is in FUSION'S INTERNAL UNITS -- centimetres and radians -- while
// Expression carries the units a human typed. A parameter shown in Fusion as "120 mm" reads
// back as Value = 12.0. Both numbers are real and they are not the same quantity. Nothing here
// converts, because nothing here has MEASURED the conversion the way `fusion revolve` measured
// inertia against a hand-computed solid. The label is honest; a factor would be a guess.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CAD;

namespace DWM.Shared.Tooling.Cad
{
    /// <summary>One user parameter, as DWM sees it.</summary>
    public sealed class FusionParameterReading
    {
        public string Name { get; init; } = string.Empty;

        /// <summary>What a human typed, units included: "120 mm", "width * 2".</summary>
        public string Expression { get; init; } = string.Empty;

        /// <summary>
        /// FUSION'S INTERNAL UNITS: centimetres for length, radians for angle.
        ///
        /// Named for what it is rather than "Value", because the shorter name is what invites
        /// somebody to feed it to a model expecting metres. A parameter displayed as 120 mm
        /// arrives here as 12.
        /// </summary>
        public double ValueInternal { get; init; }

        /// <summary>The unit Fusion reports, unverified against a real reply.</summary>
        public string Unit { get; init; } = string.Empty;

        public string Comment { get; init; } = string.Empty;

        public override string ToString() => $"{Name} = {Expression}  ({ValueInternal:G6} internal)";
    }

    public sealed class FusionParameterSet
    {
        public ToolRun Run { get; init; } = null!;

        public IReadOnlyList<FusionParameterReading> Parameters { get; init; } =
            Array.Empty<FusionParameterReading>();

        public bool Succeeded => Run.ProducedUsableOutput;

        /// <summary>Case-sensitive, because Fusion's parameter names are.</summary>
        public FusionParameterReading? this[string name] =>
            Parameters.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));
    }

    public sealed class FusionParameterService
    {
        private readonly Func<IFusionSession> _sessionFactory;
        private readonly FusionProtocol _protocol;

        public FusionParameterService(Func<IFusionSession> sessionFactory, FusionProtocol? protocol = null)
        {
            _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
            _protocol = protocol ?? new FusionProtocol();
        }

        /// <summary>
        /// Read every user parameter out of the active document.
        /// </summary>
        public async Task<FusionParameterSet> ReadAsync(
            string stageId = "cad-parameters", CancellationToken ct = default)
        {
            var startedUtc = DateTime.UtcNow;
            IFusionSession? session = null;

            try
            {
                session = _sessionFactory();

                if (!await session.PingAsync(ct).ConfigureAwait(false))
                    return Failed(stageId, startedUtc, Unreachable());

                var collection = await session.GetParametersAsync(ct).ConfigureAwait(false);
                if (collection is null)
                    return Failed(stageId, startedUtc, NoParameterSurface(session));

                var parameters = collection.Select(Read).ToList();

                // NO PARAMETERS AT ALL IS NOT A FAILURE, and that is a real difference from the
                // mass-properties read. A design with nothing parameterised is ordinary --
                // plenty of Fusion models are drawn rather than driven. Zero mass is a broken
                // read; zero parameters is a design decision somebody made.
                var warnings = new List<string>();
                if (parameters.Count == 0)
                    warnings.Add(
                        "The active document has no user parameters. That is legitimate -- a " +
                        "model can be drawn rather than driven -- but if one was expected, the " +
                        "add-in reads whatever document has FOCUS, not whatever this project " +
                        "names.");

                return new FusionParameterSet
                {
                    Run = ToolRun.Complete(
                        stageId, ToolRegistry.Fusion, startedUtc,
                        expectedOutputs: Array.Empty<string>(),
                        warnings: warnings,
                        resolvedVia: _protocol.BaseAddress.ToString()),
                    Parameters = parameters
                };
            }
            catch (Exception ex)
            {
                return Failed(stageId, startedUtc, Explain(ex));
            }
            finally
            {
                session?.Dispose();
            }
        }

        /// <summary>
        /// Set one parameter and CHECK THAT IT TOOK.
        ///
        /// The add-in answers with the updated parameter, and this compares what came back
        /// against what was asked for. That is not paranoia about this particular route -- it
        /// is the one discipline this project has had to relearn in every tool it has touched.
        /// MATLAB's Execute returns error text as an ordinary string, MYSTRAN exits 0 after a
        /// FATAL, FEMAP returns 0 rather than throwing, /scripts/execute answers HTTP 200 with
        /// success:false, and MCP hides a failure inside a successful response. A PATCH that
        /// returns the OLD value with a 200 would fit that family perfectly.
        ///
        /// A MISMATCH IS A WARNING, NOT A REFUSAL. Fusion legitimately rewrites an expression:
        /// "120" against a millimetre parameter comes back as "120 mm", and an expression
        /// referencing another parameter may be normalised. Refusing those would reject correct
        /// behaviour, so the caller is told what it asked for and what it got.
        /// </summary>
        public async Task<FusionParameterSet> SetAsync(
            string name, string expression,
            string stageId = "cad-parameters", CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("A parameter name is required.", nameof(name));
            if (string.IsNullOrWhiteSpace(expression))
                throw new ArgumentException("An expression is required.", nameof(expression));

            var startedUtc = DateTime.UtcNow;
            IFusionSession? session = null;

            try
            {
                session = _sessionFactory();

                if (!await session.PingAsync(ct).ConfigureAwait(false))
                    return Failed(stageId, startedUtc, Unreachable());

                var collection = await session.GetParametersAsync(ct).ConfigureAwait(false);
                if (collection is null)
                    return Failed(stageId, startedUtc, NoParameterSurface(session));

                // NAMED BEFORE IT IS SET. A typo'd name would otherwise be created rather than
                // rejected -- FusionParameterCollection.AddAsync delegates to SetAsync, so the
                // route is create-if-missing, and a design would silently gain a parameter that
                // drives nothing while the one meant to change did not.
                var existing = collection.FindByName(name);
                if (existing is null)
                    return Failed(stageId, startedUtc,
                        $"The active document has no parameter named '{name}'. Names are " +
                        "CASE-SENSITIVE.\n\n" +
                        "This is refused rather than created. The add-in's set route creates " +
                        "when missing, so a typo would add a parameter that drives nothing and " +
                        "leave the intended one unchanged -- a change that reports success and " +
                        "does nothing.\n\n" +
                        "Parameters in this document: " +
                        (collection.Any()
                            ? string.Join(", ", collection.Select(p => p.Name))
                            : "(none)"));

                var updated = await collection.SetAsync(name, expression, ct).ConfigureAwait(false);

                var warnings = new List<string>();
                if (!string.Equals(updated.Expression, expression, StringComparison.Ordinal))
                    warnings.Add(
                        $"Asked for '{name}' = \"{expression}\"; Fusion reports " +
                        $"\"{updated.Expression}\". Often benign -- Fusion normalises an " +
                        "expression and appends the parameter's unit -- but it is reported " +
                        "rather than assumed, because a route that echoed the OLD value would " +
                        "look identical to one that worked.");

                return new FusionParameterSet
                {
                    Run = ToolRun.Complete(
                        stageId, ToolRegistry.Fusion, startedUtc,
                        expectedOutputs: Array.Empty<string>(),
                        warnings: warnings,
                        resolvedVia: _protocol.BaseAddress.ToString()),
                    Parameters = new[] { Read(updated) }
                };
            }
            catch (Exception ex)
            {
                return Failed(stageId, startedUtc, Explain(ex));
            }
            finally
            {
                session?.Dispose();
            }
        }

        // ------------------------------------------------------------------
        private static FusionParameterReading Read(ICADParameter p) => new()
        {
            Name = p.Name,
            Expression = p.Expression,
            ValueInternal = p.Value,
            Unit = p.Unit,
            Comment = p.Comment
        };

        private string Unreachable() =>
            $"The Fusion add-in did not answer at {_protocol.BaseAddress}.\n\n" +
            "This cannot tell Fusion-not-running from add-in-not-loaded -- both refuse the " +
            "connection identically. Open Fusion, then Utilities > Scripts and Add-Ins > " +
            "Add-Ins.";

        private string NoParameterSurface(IFusionSession session) =>
            $"This transport ({session.GetType().Name}) has no parameter surface.\n\n" +
            "Parameters use the add-in's REST routes rather than the script executor, and not " +
            "every transport has them: the MCP route talks to Autodesk's server, whose tool " +
            "names are still unread.\n\n" +
            "The proven alternative is a SetParameterOp through the 'operations' command, " +
            "which goes through /scripts/execute -- more round trips, but that path has been " +
            "driven against real Fusion.";

        /// <summary>
        /// Turn a transport exception into something that names the likely cause.
        ///
        /// FusionParameterCollection throws InvalidOperationException with the HTTP status
        /// inside the message, so a route the add-in does not implement arrives as
        /// "HTTP 404" buried in prose. Contract v1's parameter routes are UNVERIFIED, and a
        /// 404 or 405 here means exactly that rather than anything about the document.
        /// </summary>
        private static string Explain(Exception ex)
        {
            var message = ex.Message ?? string.Empty;
            var missingRoute = message.Contains("404", StringComparison.Ordinal)
                            || message.Contains("405", StringComparison.Ordinal);

            if (!missingRoute) return message;

            return message + "\n\n" +
                   "A 404 or 405 here almost certainly means THE ADD-IN DOES NOT IMPLEMENT THIS " +
                   "ROUTE. The parameter routes were read from FusionLibrary's client rather " +
                   "than exercised against the add-in, so this is the first thing to doubt -- " +
                   "not the document, and not the parameter name.\n\n" +
                   "Use a SetParameterOp through the 'operations' command instead: it runs " +
                   "through /scripts/execute, which is proven.";
        }

        private static FusionParameterSet Failed(string stageId, DateTime startedUtc, string message) => new()
        {
            Run = ToolRun.Complete(stageId, ToolRegistry.Fusion, startedUtc,
                Array.Empty<string>(), failureMessage: message)
        };
    }
}
