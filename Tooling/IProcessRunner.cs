// IProcessRunner.cs / ProcessRequest.cs / ProcessOutcome.cs / ProcessRunner.cs
// Spawning an external executable, behind a seam.
//
// The seam exists for the same reason IMatlabSession does: the interesting behaviour is
// orchestration -- resolving the executable, choosing a working directory, deciding what
// counts as failure, checking output freshness -- and none of that needs a solver installed
// to test. Only the process spawn itself does, and it is deliberately the thinnest part.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace DWM.Shared.Tooling
{
    public sealed class ProcessRequest
    {
        public string ExecutablePath { get; init; } = string.Empty;
        public string Arguments { get; init; } = string.Empty;
        public string WorkingDirectory { get; init; } = string.Empty;

        /// <summary>
        /// Wall-clock ceiling. A solver given a bad deck can spin indefinitely, and a UI that
        /// waits forever is indistinguishable from one that has crashed.
        /// </summary>
        public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);
    }

    public sealed class ProcessOutcome
    {
        public int ExitCode { get; init; }
        public string StandardOutput { get; init; } = string.Empty;
        public string StandardError { get; init; } = string.Empty;
        public bool TimedOut { get; init; }
        public TimeSpan Duration { get; init; }
    }

    public interface IProcessRunner
    {
        ProcessOutcome Run(ProcessRequest request);
    }

    public sealed class ProcessRunner : IProcessRunner
    {
        public ProcessOutcome Run(ProcessRequest request)
        {
            if (request is null) throw new ArgumentNullException(nameof(request));

            var info = new ProcessStartInfo
            {
                FileName = request.ExecutablePath,
                Arguments = request.Arguments,
                WorkingDirectory = request.WorkingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var stopwatch = Stopwatch.StartNew();
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

            using var process = new Process { StartInfo = info };

            // ASYNCHRONOUS READS, NOT ReadToEnd ON BOTH STREAMS.
            //
            // Reading stdout to completion and then stderr deadlocks the moment the child
            // fills the stderr pipe buffer: it blocks writing, we block reading the other
            // stream, and neither side moves. A solver that prints warnings to stderr is
            // exactly the case that triggers it, and the symptom is an apparent hang with no
            // error -- which would be indistinguishable from the solver simply being slow.
            using var stdoutDone = new ManualResetEventSlim(false);
            using var stderrDone = new ManualResetEventSlim(false);

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) stdoutDone.Set(); else stdout.AppendLine(e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) stderrDone.Set(); else stderr.AppendLine(e.Data);
            };

            try
            {
                process.Start();
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                // "The system cannot find the file specified", almost always. Returned rather
                // than thrown because a missing solver is an ORDINARY outcome of asking a tool
                // to run, not an exceptional one -- and letting it propagate meant it surfaced
                // as an unhandled Win32Exception in the debugger instead of a message in the
                // window that asked for the run.
                return new ProcessOutcome
                {
                    ExitCode = -1,
                    StandardError =
                        $"Could not start '{request.ExecutablePath}': {ex.Message}",
                    Duration = stopwatch.Elapsed
                };
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var exited = process.WaitForExit((int)request.Timeout.TotalMilliseconds);
            if (!exited)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }

                return new ProcessOutcome
                {
                    ExitCode = -1,
                    TimedOut = true,
                    StandardOutput = stdout.ToString(),
                    StandardError = stderr.ToString(),
                    Duration = stopwatch.Elapsed
                };
            }

            // Give the redirected streams a moment to flush after exit -- WaitForExit(int) does
            // not guarantee the async readers have drained, and losing the last lines of an
            // .f06 summary would be losing exactly the lines that say what went wrong.
            stdoutDone.Wait(TimeSpan.FromSeconds(2));
            stderrDone.Wait(TimeSpan.FromSeconds(2));
            stopwatch.Stop();

            return new ProcessOutcome
            {
                ExitCode = process.ExitCode,
                StandardOutput = stdout.ToString(),
                StandardError = stderr.ToString(),
                Duration = stopwatch.Elapsed
            };
        }

        /// <summary>
        /// Where the executable actually is, or null.
        ///
        /// NULL MEANS NOT FOUND, and that promise is the point. An earlier version returned a
        /// bare filename unchecked on the theory that it might be on PATH -- so "resolved"
        /// could mean "guessed", ToolAvailability said Found when nothing had been found, and
        /// the failure arrived later as a Win32Exception from Process.Start. A tool that
        /// reports Found and then cannot start is worse than one that reports NotFound.
        ///
        /// Three passes, in order: rooted candidates, PATH for bare names, then a bounded
        /// search under the descriptor's search roots -- because installers vary on whether
        /// the binary sits at the root or under bin/, and a version-stamped subfolder is
        /// common enough to be worth looking for rather than making the user configure.
        /// </summary>
        public static string? ResolveExecutable(ToolDescriptor descriptor)
        {
            foreach (var candidate in descriptor.ExecutableCandidates)
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                if (Path.IsPathRooted(candidate) && File.Exists(candidate)) return candidate;
            }

            foreach (var candidate in descriptor.ExecutableCandidates)
            {
                if (string.IsNullOrWhiteSpace(candidate) || Path.IsPathRooted(candidate)) continue;

                var onPath = FindOnPath(candidate);
                if (onPath is not null) return onPath;
            }

            var patterns = descriptor.ExecutableSearchPatterns.Count > 0
                ? descriptor.ExecutableSearchPatterns
                : descriptor.ExecutableCandidates
                    .Select(Path.GetFileName)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Select(n => n!)
                    .ToList();

            foreach (var root in descriptor.ExecutableSearchRoots)
            {
                foreach (var pattern in patterns)
                {
                    var found = SearchUnder(root, pattern, depth: 3);
                    if (found is not null) return found;
                }
            }

            return null;
        }

        private static string? FindOnPath(string fileName)
        {
            var path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(path)) return null;

            foreach (var directory in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(directory)) continue;
                try
                {
                    var full = Path.Combine(directory.Trim(), fileName);
                    if (File.Exists(full)) return full;
                }
                catch (ArgumentException)
                {
                    // A malformed PATH entry is somebody else's problem and must not stop the
                    // search at the entry before the one that would have matched.
                }
            }
            return null;
        }

        /// <summary>
        /// Breadth-limited hunt under an install root. Depth-capped so a mistyped root pointing
        /// at a drive letter cannot turn tool detection into a full disk scan.
        /// </summary>
        private static string? SearchUnder(string root, string pattern, int depth)
        {
            if (depth < 0 || string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;

            try
            {
                // ORDERED, because EnumerateFiles gives no guaranteed order and an install with
                // two versions side by side would otherwise resolve to whichever the filesystem
                // happened to hand back first -- a different answer on different machines, or
                // on the same machine after a defrag.
                var match = Directory.EnumerateFiles(root, pattern)
                                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                                     .FirstOrDefault();
                if (match is not null) return match;

                foreach (var sub in Directory.EnumerateDirectories(root))
                {
                    var found = SearchUnder(sub, pattern, depth - 1);
                    if (found is not null) return found;
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }

            return null;
        }
    }
}
