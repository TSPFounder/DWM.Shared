// MystranRunner.cs
// Runs MYSTRAN on a Nastran deck and reads the results back.
//
// This is the first BATCH tool in the project, and it exists to prove the shape:
//
//     write a deck  ->  spawn a process  ->  read the artifacts it left on disk
//
// No COM, no port, nothing to attach to. The MATLAB stage's habits carry over exactly one
// step removed: there, MATLAB's Execute returned error text as an ordinary string and had to
// be interrogated with a sentinel; here the exit code comes back 0 after a FATAL, and the
// truth is in the .f06. Same lesson, different mechanism -- THE STATUS THE TOOL HANDS YOU IS
// NOT THE ANSWER.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DWM.Shared.Tooling.Fea
{
    public sealed class MystranRunResult
    {
        public ToolRun Run { get; init; } = null!;
        public ModalResults? Modal { get; init; }

        /// <summary>Full path of the .f06 print file, whether or not it parsed.</summary>
        public string? F06Path { get; init; }

        public string? ExecutableUsed { get; init; }
        public int ExitCode { get; init; }
        public bool TimedOut { get; init; }

        public bool Succeeded => Run.ProducedUsableOutput;
    }

    public sealed class MystranRunner
    {
        private readonly IProcessRunner _processRunner;
        private readonly ToolDescriptor _descriptor;

        public MystranRunner(ToolDescriptor? descriptor = null, IProcessRunner? processRunner = null)
        {
            _descriptor = descriptor ?? new ToolRegistry().Require(ToolRegistry.Mystran);
            _processRunner = processRunner ?? new ProcessRunner();
        }

        /// <param name="deckPath">Full path to the .bdf / .dat deck.</param>
        /// <param name="stageId">Pipeline stage this run belongs to, for the run record.</param>
        public MystranRunResult Run(string deckPath, string stageId = "fea-solve", TimeSpan? timeout = null)
        {
            if (string.IsNullOrWhiteSpace(deckPath))
                throw new ArgumentException("A deck path is required.", nameof(deckPath));

            if (!File.Exists(deckPath))
                return Failure(stageId, null, $"Deck not found: {deckPath}");

            var executable = ProcessRunner.ResolveExecutable(_descriptor);
            if (executable is null)
            {
                return Failure(stageId, null,
                    "MYSTRAN was not found. Looked for:\n  " +
                    string.Join("\n  ", _descriptor.ExecutableCandidates) +
                    "\n\nIf it is installed elsewhere, override the descriptor's " +
                    "ExecutableCandidates rather than moving the install -- the paths in the " +
                    "registry are defaults, not facts.");
            }

            // WORKING DIRECTORY IS THE DECK'S OWN FOLDER, and that is not cosmetic: MYSTRAN
            // writes .f06/.op2/.neu beside its input, so the working directory decides where
            // results land. The MATLAB stage learned the same thing the hard way when a
            // launched session started in a read-only install directory.
            var workingDirectory = Path.GetDirectoryName(Path.GetFullPath(deckPath))!;
            var deckName = Path.GetFileName(deckPath);
            var f06Path = Path.Combine(workingDirectory,
                Path.GetFileNameWithoutExtension(deckPath) + ".f06");

            var startedUtc = DateTime.UtcNow;

            var outcome = _processRunner.Run(new ProcessRequest
            {
                ExecutablePath = executable,
                Arguments = Quote(deckName),
                WorkingDirectory = workingDirectory,
                Timeout = timeout ?? TimeSpan.FromMinutes(10)
            });

            if (outcome.TimedOut)
            {
                return new MystranRunResult
                {
                    Run = ToolRun.Complete(stageId, ToolRegistry.Mystran, startedUtc,
                        new[] { f06Path },
                        failureMessage: $"MYSTRAN did not finish within {(timeout ?? TimeSpan.FromMinutes(10)).TotalMinutes:F0} " +
                                        "minutes and was terminated. A deck with an ill-posed constraint set can " +
                                        "spin without converging.",
                        resolvedVia: executable),
                    F06Path = f06Path,
                    ExecutableUsed = executable,
                    ExitCode = outcome.ExitCode,
                    TimedOut = true
                };
            }

            ModalResults? modal = null;
            string? failure = null;
            var warnings = new List<string>();

            if (!File.Exists(f06Path))
            {
                failure =
                    $"MYSTRAN exited with code {outcome.ExitCode} but wrote no print file.\n\n" +
                    $"  Expected: {f06Path}\n\n" +
                    "Console output follows:\n" + Trim(outcome.StandardOutput, outcome.StandardError);
            }
            else
            {
                modal = NastranF06Parser.Parse(File.ReadAllText(f06Path));

                // THE EXIT CODE IS CHECKED SECOND, AND ON PURPOSE. Solvers in this lineage
                // return 0 after writing FATAL and producing nothing, so trusting the status
                // yields a confident wrong answer. The print file is the authority.
                if (modal.HasFatal)
                {
                    failure =
                        "MYSTRAN reported a FATAL error. The exit code was " +
                        $"{outcome.ExitCode}, which in this solver family does NOT indicate failure.\n\n  " +
                        string.Join("\n  ", modal.FatalMessages.Take(5));
                }
                else if (modal.Modes.Count == 0)
                {
                    failure =
                        "MYSTRAN finished without a FATAL, but no eigenvalue table was found in " +
                        $"the .f06.\n\n  Print file: {f06Path}\n\n" +
                        "Either the deck ran a solution sequence that produces no modes, or the " +
                        "table layout differs from the one NastranF06Parser expects -- the parser " +
                        "has not yet been checked against output from this MYSTRAN build.";
                }

                foreach (var warning in modal.WarningMessages.Take(10))
                    warnings.Add(warning);
            }

            var run = ToolRun.Complete(
                stageId, ToolRegistry.Mystran, startedUtc,
                expectedOutputs: new[] { f06Path },
                warnings: warnings,
                failureMessage: failure,
                resolvedVia: executable);

            return new MystranRunResult
            {
                Run = run,
                Modal = modal,
                F06Path = f06Path,
                ExecutableUsed = executable,
                ExitCode = outcome.ExitCode
            };
        }

        private static MystranRunResult Failure(string stageId, string? executable, string message) => new()
        {
            Run = ToolRun.Complete(stageId, ToolRegistry.Mystran, DateTime.UtcNow,
                Array.Empty<string>(), failureMessage: message, resolvedVia: executable),
            ExecutableUsed = executable
        };

        private static string Quote(string value) =>
            value.Contains(' ') ? "\"" + value + "\"" : value;

        private static string Trim(string stdout, string stderr)
        {
            var combined = (stdout + "\n" + stderr).Trim();
            return combined.Length <= 2000 ? combined : combined[..2000] + "\n... (truncated)";
        }
    }
}
