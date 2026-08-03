// MatlabStageRequest.cs / MatlabStageResult.cs
// Inputs and outputs of one MATLAB pipeline-stage run.

using System;
using System.Collections.Generic;
using System.IO;

namespace DWM.Shared.Matlab
{
    public sealed class MatlabStageRequest
    {
        /// <summary>
        /// Folder holding wtRunSimulation.m and wtExportSimSamples.m. Added to MATLAB's path
        /// with ADDPATH rather than CD, so the user's current folder is left alone.
        /// </summary>
        public string TurbineCodeDirectory { get; init; } = string.Empty;

        /// <summary>Wind scenario. Ramp is the default and the right choice for export.</summary>
        public TurbineScenario Scenario { get; init; } = TurbineScenario.Ramp;

        /// <summary>
        /// Base CSV name, matching wtGui's "Base name" field. wtExportSimSamples inserts the
        /// channel suffix before the extension, so "wtSimSamples.csv" produces
        /// wtSimSamples_rotor.csv, _pitch, _yaw, _tower and _power as siblings.
        /// </summary>
        public string CsvBaseName { get; init; } = "wtSimSamples.csv";

        /// <summary>Where the CSVs are written. Defaults to TurbineCodeDirectory.</summary>
        public string? CsvOutputDirectory { get; init; }

        /// <summary>
        /// MATLAB's current folder for the run. Null means "decide by how the session was
        /// obtained", which is not the same thing in the two cases:
        ///
        ///   LAUNCHED session  -> cd to TurbineCodeDirectory. A MATLAB started by COM begins in
        ///                        ITS OWN INSTALL DIRECTORY, typically C:\Program Files\MATLAB\
        ///                        &lt;release&gt;, which is NOT WRITABLE. wtBuildModel saves
        ///                        wtTurbine3MW.mdl relative to the current folder, so the run
        ///                        dies with "Permission denied" on a path nobody chose.
        ///   ATTACHED session  -> leave it alone. It is the user's own MATLAB, already sitting
        ///                        where they put it, and moving someone's current folder out
        ///                        from under them is the side effect ADDPATH was chosen over CD
        ///                        to avoid in the first place.
        ///
        /// Setting this explicitly overrides both and always cds.
        /// </summary>
        public string? WorkingDirectory { get; init; }

        /// <summary>Export sample rate in Hz, matching wtGui's field. 30 is the default.</summary>
        public int SampleRateHz { get; init; } = 30;

        /// <summary>Full path of the world-package .db to write.</summary>
        public string OutputPackagePath { get; init; } = string.Empty;

        /// <summary>World id embedded in the package's WorldInfo row.</summary>
        public string WorldId { get; init; } = "turbine";

        /// <summary>
        /// When true, a missing pitch/yaw/tower/power CSV is an ERROR rather than a warning.
        /// Default false, which matches WriteTurbine: those four channels are optional there,
        /// and a rotor-only package is a legitimate thing to produce. Turn it on for a
        /// release build, where a channel quietly vanishing is the failure you would least
        /// like to discover from a screenshot.
        /// </summary>
        public bool RequireAllChannels { get; init; }

        /// <summary>
        /// Slack allowed when deciding whether a CSV predates this run. Covers filesystem
        /// timestamp granularity, NOT clock skew -- MATLAB and DWMStudio are the same machine.
        /// Keep it small: every second of slack is a second of staleness that can pass.
        /// </summary>
        public TimeSpan StaleFileTolerance { get; init; } = TimeSpan.FromSeconds(2);

        internal string ResolvedCsvDirectory =>
            string.IsNullOrWhiteSpace(CsvOutputDirectory) ? TurbineCodeDirectory : CsvOutputDirectory!;

        /// <summary>
        /// Absolute path of one channel's CSV, derived exactly as wtExportSimSamples.m derives
        /// it: FILEPARTS the base name, then [name '_' suffix ext]. If that convention ever
        /// changes in the .m file it must change here too -- the two are coupled by nothing
        /// but agreement, which is why the coupling is written down in both places.
        /// </summary>
        public string ChannelCsvPath(string channelSuffix)
        {
            var name = Path.GetFileNameWithoutExtension(CsvBaseName);
            var ext = Path.GetExtension(CsvBaseName);
            if (string.IsNullOrEmpty(ext)) ext = ".csv";
            return Path.Combine(ResolvedCsvDirectory, name + "_" + channelSuffix + ext);
        }
    }

    public sealed class MatlabStageResult
    {
        public TurbineScenario Scenario { get; init; }

        /// <summary>Path of the .db written.</summary>
        public string PackagePath { get; init; } = string.Empty;

        /// <summary>Channel suffixes whose CSV was found, fresh, and handed to WriteTurbine.</summary>
        public IReadOnlyList<string> ChannelsExported { get; init; } = Array.Empty<string>();

        /// <summary>Channel suffixes with no CSV. Empty on a complete run.</summary>
        public IReadOnlyList<string> ChannelsMissing { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Non-fatal notes worth showing the user -- a gust export, a missing optional channel,
        /// a MATLAB warning. NEVER let these go only to a log: every item here describes
        /// something that looks completely normal on screen.
        /// </summary>
        public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

        /// <summary>Whether the run used a MATLAB the user already had open.</summary>
        public bool AttachedToExistingMatlab { get; init; }

        public TimeSpan Duration { get; init; }

        public bool HasWarnings => Warnings.Count > 0;
    }
}
