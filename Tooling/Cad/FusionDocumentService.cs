// FusionDocumentService.cs
// Creating and opening Fusion documents, and saying what that costs.
//
// WHAT THIS RETIRES
//
// "Nothing outside Fusion can make it open a file" appears in several comments in this
// codebase, including FusionStageService's own summary. It is TRUE OF THE SCRIPT PATH and
// FALSE of the add-in, which has had POST /documents and POST /documents/open all along. The
// claim was made from what /scripts/execute could do and generalised to the whole transport
// without checking the rest of the contract -- the same error shape as reading a tool's exit
// code and calling it a verdict.
//
// THE TWO THINGS THAT MAKE THIS DIFFERENT FROM EVERY OTHER COMMAND
//
// 1. IT CHANGES WHAT "ACTIVE" MEANS. Every other command operates on whatever document has
//    focus. Creating or opening one retargets massProperties, revolve and export -- silently,
//    and after this call returns. Useful, and a foot-gun; both are stated on every call.
//
// 2. IT SPENDS A DOCUMENT SLOT. Fusion's free tier allows TEN active documents. Past that,
//    components drop to Inactive (Read-Only) where physicalProperties returns 0 WITHOUT
//    raising -- which is the exact failure FusionStageService exists to refuse. A build loop
//    that creates a document each pass manufactures it, and WindTurbineBlade.run() calling
//    documents.add() every run was already recorded as walking into precisely this.
//
//    Nothing here can count the open documents: contract v1 has no route that lists them. So
//    this warns on every create rather than pretending to know. A warning that fires every
//    time is usually useless -- that was the FEMAP lesson -- but the alternative here is
//    silence about a limit that turns into zeroed mass properties three commands later.
//
// THE ROUTES ARE UNVERIFIED, like the parameter ones. Read from FusionLibrary's client, never
// exercised against the add-in. And a create depends on TWO of them, because FusionApplication
// hydrates the new document through /documents/active/parameters.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CAD;

namespace DWM.Shared.Tooling.Cad
{
    /// <summary>What came back from a create or an open.</summary>
    public sealed class FusionDocumentResult
    {
        public ToolRun Run { get; init; } = null!;

        public string DocumentName { get; init; } = string.Empty;

        /// <summary>"active" in contract v1, which always operates on the focused document.</summary>
        public string DocumentId { get; init; } = string.Empty;

        /// <summary>Parameters the new document arrived with. Empty is normal for a create.</summary>
        public IReadOnlyList<FusionParameterReading> Parameters { get; init; } =
            Array.Empty<FusionParameterReading>();

        public bool Succeeded => Run.ProducedUsableOutput;
    }

    public sealed class FusionDocumentService
    {
        /// <summary>
        /// Fusion's free-tier limit on ACTIVE documents. Not enforced here -- nothing can count
        /// them -- but named so the warning quotes a number rather than a rumour.
        /// </summary>
        public const int FreeTierActiveDocumentLimit = 10;

        private readonly Func<IFusionSession> _sessionFactory;
        private readonly FusionProtocol _protocol;

        public FusionDocumentService(Func<IFusionSession> sessionFactory, FusionProtocol? protocol = null)
        {
            _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
            _protocol = protocol ?? new FusionProtocol();
        }

        /// <summary>Create a new design and make it active.</summary>
        public Task<FusionDocumentResult> CreateAsync(
            string name, string stageId = "cad-document", CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("A document name is required.", nameof(name));

            return RunAsync(
                stageId, ct,
                (session, token) => session.CreateDocumentAsync(name, token),
                verb: "create",
                subject: name,
                extraWarnings: new[]
                {
                    $"A NEW ACTIVE DOCUMENT WAS CREATED. Fusion's free tier allows " +
                    $"{FreeTierActiveDocumentLimit} active documents; past that, components drop " +
                    "to Inactive (Read-Only), where mass properties come back as 0 WITHOUT an " +
                    "error. Nothing here can count them -- contract v1 has no route that lists " +
                    "open documents -- so this fires every time rather than only when it matters. " +
                    "Close documents you are finished with."
                });
        }

        /// <summary>
        /// Open a document by path on the machine running Fusion.
        /// </summary>
        public Task<FusionDocumentResult> OpenAsync(
            string path, string stageId = "cad-document", CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("A document path is required.", nameof(path));

            var warnings = new List<string>();

            // CHECKED LOCALLY, WARNED NOT REFUSED. Fusion resolves this path, and Fusion is not
            // necessarily on this machine -- the add-in is reached over HTTP. So a path that is
            // absent here may be perfectly good there, and refusing would break the remote case
            // to catch a typo. Saying "this machine cannot see it" costs nothing and catches the
            // typo anyway, which is the common case.
            if (!File.Exists(path))
                warnings.Add(
                    $"'{path}' does not exist ON THIS MACHINE. That is not necessarily wrong -- " +
                    "Fusion resolves the path, and the add-in is reached over HTTP, so Fusion " +
                    "may be elsewhere. But if it is the same machine, this is a typo and the " +
                    "add-in is about to say so less clearly.");

            return RunAsync(
                stageId, ct,
                (session, token) => session.OpenDocumentAsync(path, token),
                verb: "open",
                subject: path,
                extraWarnings: warnings.ToArray());
        }

        // ------------------------------------------------------------------
        private async Task<FusionDocumentResult> RunAsync(
            string stageId,
            CancellationToken ct,
            Func<IFusionSession, CancellationToken, Task<ICADDocument?>> action,
            string verb,
            string subject,
            string[] extraWarnings)
        {
            var startedUtc = DateTime.UtcNow;
            IFusionSession? session = null;

            try
            {
                session = _sessionFactory();

                if (!await session.PingAsync(ct).ConfigureAwait(false))
                    return Failed(stageId, startedUtc,
                        $"The Fusion add-in did not answer at {_protocol.BaseAddress}.\n\n" +
                        "This cannot tell Fusion-not-running from add-in-not-loaded -- both " +
                        "refuse the connection identically.");

                var document = await action(session, ct).ConfigureAwait(false);

                if (document is null)
                    return Failed(stageId, startedUtc,
                        $"This transport ({session.GetType().Name}) cannot {verb} documents.\n\n" +
                        "Document routes belong to the add-in's REST surface, not to the script " +
                        "executor, and the MCP route has nothing mapped to them.\n\n" +
                        "A CreateDocumentOp or OpenDocumentOp through the 'operations' command " +
                        "is the alternative -- but note it IMPORTS rather than opens, so it " +
                        "spends a document slot either way.");

                var warnings = new List<string>(extraWarnings)
                {
                    $"THE ACTIVE DOCUMENT IS NOW '{document.Name}'. Every other command reads " +
                    "whatever has focus, so massProperties, revolve and export all target this " +
                    "from here on."
                };

                return new FusionDocumentResult
                {
                    Run = ToolRun.Complete(
                        stageId, ToolRegistry.Fusion, startedUtc,
                        expectedOutputs: Array.Empty<string>(),
                        warnings: warnings,
                        resolvedVia: _protocol.BaseAddress.ToString()),
                    DocumentName = document.Name,
                    DocumentId = document.Id,
                    Parameters = document.Parameters
                        .Select(p => new FusionParameterReading
                        {
                            Name = p.Name,
                            Expression = p.Expression,
                            ValueInternal = p.Value,
                            Unit = p.Unit,
                            Comment = p.Comment
                        })
                        .ToList()
                };
            }
            catch (Exception ex)
            {
                return Failed(stageId, startedUtc, Explain(ex, verb, subject));
            }
            finally
            {
                session?.Dispose();
            }
        }

        /// <summary>
        /// Name the route that is probably missing, which is not the one that was asked for.
        ///
        /// FusionApplication hydrates the document it returns by GETting
        /// /documents/active/parameters, so a create touches TWO routes and a 404 is ambiguous
        /// between them. Blaming /documents alone would send the next hour to the wrong place.
        /// </summary>
        private static string Explain(Exception ex, string verb, string subject)
        {
            var message = ex.Message ?? string.Empty;
            var missingRoute = message.Contains("404", StringComparison.Ordinal)
                            || message.Contains("405", StringComparison.Ordinal);

            if (!missingRoute) return $"Could not {verb} '{subject}'.\n\n{message}";

            return $"Could not {verb} '{subject}'.\n\n{message}\n\n" +
                   "A 404 or 405 means A ROUTE IS MISSING, AND THERE ARE TWO CANDIDATES. The " +
                   "document is created or opened by one route, then hydrated by a GET on " +
                   "/documents/active/parameters -- so the document may well exist in Fusion " +
                   "even though this call failed. LOOK AT FUSION before retrying.\n\n" +
                   "These routes were read from FusionLibrary's client rather than exercised " +
                   "against the add-in, unlike /scripts/execute, which is proven.";
        }

        private static FusionDocumentResult Failed(string stageId, DateTime startedUtc, string message) => new()
        {
            Run = ToolRun.Complete(stageId, ToolRegistry.Fusion, startedUtc,
                Array.Empty<string>(), failureMessage: message)
        };
    }
}
