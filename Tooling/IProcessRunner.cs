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

            process.Start();
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
        /// First candidate that exists on disk, or a bare filename if one is expected to be on
        /// PATH. Returns null when nothing matches.
        /// </summary>
        public static string? ResolveExecutable(ToolDescriptor descriptor)
        {
            foreach (var candidate in descriptor.ExecutableCandidates)
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;

                // A bare filename means "expect it on PATH" -- it cannot be existence-checked
                // here without reimplementing PATH lookup, so it is accepted as a last resort
                // and allowed to fail at spawn time with the OS's own message.
                if (!Path.IsPathRooted(candidate)) return candidate;
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }
    }
}
