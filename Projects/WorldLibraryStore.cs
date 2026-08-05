// WorldLibraryStore.cs / WorldProjectRecord.cs
// Persistence for the world library: %APPDATA%\DWMStudio\worlds.json.
//
// Until now DWMStudio's New World wizard ended with `await Task.Delay(200)` where a save
// should have been, so a created world lived in an ObservableCollection and vanished on
// restart. The dashboard's two sample worlds were hardcoded in a constructor.
//
// THREE THINGS THIS DOES THAT A NAIVE FILE WRITE WOULD NOT
//
// 1. WRITES ATOMICALLY. A crash or a full disk partway through serialising would otherwise
//    leave a truncated file, and since this one file IS the library, that is not "one world
//    is damaged" -- it is all of them, permanently. Serialise to a temp file first, then
//    replace, so an interrupted save leaves the previous good file untouched.
//
// 2. SURVIVES A CORRUPT FILE. Throwing on load would make DWMStudio refuse to start, which
//    is a worse outcome than a lost library and harder to diagnose. The bad file is moved
//    aside with a timestamp and the app opens empty, saying so. Nothing is deleted -- a file
//    that failed to parse may still be readable by a human, and it is the only copy.
//
// 3. ACTUALLY READS ITS SchemaVersion. The world-package format writes a SchemaVersion in
//    three places and reads it in none, which SCOPE.md's fragility audit calls item 2. That
//    is a mistake worth not repeating in a new format on the same day it was written down:
//    a file from a FUTURE version is refused rather than parsed with unknown fields quietly
//    dropped, because silently discarding a field is how a save turns into data loss.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DWM.Shared.Projects
{
    public sealed class StageStatusRecord
    {
        public string StageId { get; set; } = string.Empty;
        public string Status { get; set; } = "Pending";
        public bool IsComplete { get; set; }
    }

    public sealed class WorldProjectRecord
    {
        public string WorldId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Version { get; set; } = "1.0";

        public string? FusionDocumentPath { get; set; }
        public string? SimulinkModelPath { get; set; }

        /// <summary>
        /// The Nastran deck the FEA stages share. Persisted per project because it is
        /// normally an absolute path outside the project root, so it cannot be derived.
        /// </summary>
        public string? FeaDeckPath { get; set; }

        /// <summary>
        /// Which release of a tool this project expects, keyed by ToolRegistry id --
        /// e.g. "matlab" -> "Matlab.Application.7.12".
        ///
        /// Recorded per project because the MVP turbine model runs under R2011a and will
        /// silently run under R2025b if nobody says otherwise. That cost four rounds of
        /// debugging on 2026-08-03, and "which MATLAB was this built with" is exactly the
        /// question a saved project should be able to answer.
        /// </summary>
        public Dictionary<string, string> ToolVersions { get; set; } = new();

        public int RequirementCount { get; set; }
        public int ActorCount { get; set; }
        public int UseCaseCount { get; set; }
        public int UserStoryCount { get; set; }

        public DateTimeOffset LastModifiedOn { get; set; }

        public List<StageStatusRecord> Stages { get; set; } = new();
    }

    public sealed class WorldLibraryFile
    {
        public int SchemaVersion { get; set; }
        public DateTimeOffset SavedAtUtc { get; set; }
        public List<WorldProjectRecord> Worlds { get; set; } = new();
    }

    public sealed class WorldLibraryLoadResult
    {
        public IReadOnlyList<WorldProjectRecord> Worlds { get; init; } = Array.Empty<WorldProjectRecord>();

        /// <summary>No file yet. Ordinary on first run, and NOT an error.</summary>
        public bool WasMissing { get; init; }

        /// <summary>The file existed and could not be used. See BackupPath and Message.</summary>
        public bool Recovered { get; init; }

        /// <summary>Where the unusable file was moved. Never null when Recovered is true.</summary>
        public string? BackupPath { get; init; }

        /// <summary>Worth showing the user when Recovered -- silence here means silent data loss.</summary>
        public string? Message { get; init; }
    }

    public sealed class WorldLibraryStore
    {
        public const int CurrentSchemaVersion = 1;

        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public string Path { get; }

        public WorldLibraryStore(string? path = null)
        {
            Path = string.IsNullOrWhiteSpace(path) ? DefaultPath() : path!;
        }

        /// <summary>%APPDATA%\DWMStudio\worlds.json (and the XDG equivalent elsewhere).</summary>
        public static string DefaultPath() => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DWMStudio", "worlds.json");

        public WorldLibraryLoadResult Load()
        {
            if (!File.Exists(Path))
                return new WorldLibraryLoadResult { WasMissing = true };

            string text;
            try
            {
                text = File.ReadAllText(Path);
            }
            catch (IOException ex)
            {
                // Locked or unreadable. Do NOT move it aside -- a file we cannot read is a file
                // we cannot safely conclude anything about, and the copy on disk may be fine.
                return new WorldLibraryLoadResult
                {
                    Recovered = false,
                    Message = $"Could not read the world library at {Path}: {ex.Message}. " +
                              "Nothing was changed; the file is left exactly as it is."
                };
            }

            WorldLibraryFile? file;
            try
            {
                file = JsonSerializer.Deserialize<WorldLibraryFile>(text, Options);
            }
            catch (JsonException ex)
            {
                return Quarantine($"The world library at {Path} is not valid JSON ({ex.Message}).");
            }

            if (file is null)
                return Quarantine($"The world library at {Path} deserialised to nothing.");

            if (file.SchemaVersion > CurrentSchemaVersion)
            {
                // Refuse rather than parse. A newer file may carry fields this build does not
                // know about, and loading it would drop them -- then the next save would write
                // the reduced version back over the original. That is not a read failure, it is
                // data loss with a successful-looking save in front of it.
                return Quarantine(
                    $"The world library at {Path} was written by a newer version of DWMStudio " +
                    $"(schema {file.SchemaVersion}, this build understands {CurrentSchemaVersion}). " +
                    "It has NOT been loaded, because loading it would silently discard whatever " +
                    "the newer version added and the next save would overwrite the original.");
            }

            return new WorldLibraryLoadResult { Worlds = file.Worlds ?? new List<WorldProjectRecord>() };
        }

        public void Save(IEnumerable<WorldProjectRecord> worlds)
        {
            if (worlds is null) throw new ArgumentNullException(nameof(worlds));

            var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var payload = new WorldLibraryFile
            {
                SchemaVersion = CurrentSchemaVersion,
                SavedAtUtc = DateTimeOffset.UtcNow,
                Worlds = new List<WorldProjectRecord>(worlds)
            };

            // TEMP FILE THEN REPLACE. Writing straight to Path would mean a crash or a full
            // disk mid-write truncates the only copy of the entire library.
            var temp = Path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(payload, Options));

            if (File.Exists(Path)) File.Replace(temp, Path, destinationBackupFileName: null);
            else File.Move(temp, Path);
        }

        private WorldLibraryLoadResult Quarantine(string reason)
        {
            var backup = $"{Path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
            try
            {
                File.Move(Path, backup);
            }
            catch (IOException ex)
            {
                return new WorldLibraryLoadResult
                {
                    Recovered = false,
                    Message = reason + $" It could not be moved aside either ({ex.Message}), so " +
                                       "it has been left in place and nothing was loaded."
                };
            }

            return new WorldLibraryLoadResult
            {
                Recovered = true,
                BackupPath = backup,
                Message = reason + $" It has been moved to {backup} and DWMStudio has started " +
                                   "with an empty library. NOTHING WAS DELETED -- that file is " +
                                   "the only copy and may still be readable by hand."
            };
        }
    }
}
