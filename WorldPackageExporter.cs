// WorldPackageExporter.cs
// Path B export bridge: writes a DWM world package as a plain SQLite .db file
// that the UE side reads via its built-in SQLite module.
//
// This version reads real simulation results from a CSV produced by the
// MATLAB Simscape Multibody pendulum (run_pendulum_sim.m), replacing the
// previous hardcoded small-angle approximation. If the CSV is missing, it
// falls back to the analytic small-angle curve so the pipeline still runs.
//
// Requires NuGet package: Microsoft.Data.Sqlite

using System;
using System.Globalization;
using System.IO;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using DWM.Shared.Economy;

namespace DWM.Shared
{
    public sealed class WorldPackageExporter
    {
        public const int SchemaVersion = 1;
        public const int EconomySchemaVersion = 1;

        /// <summary>
        /// Write a single-pendulum world package.
        /// </summary>
        /// <param name="outputPath">Full path to the .db file to create.</param>
        /// <param name="worldId">World id to embed.</param>
        /// <param name="simResultsCsv">
        /// Optional path to a CSV of Simscape results (columns: Time,Position,Velocity).
        /// If null or missing, falls back to the analytic small-angle curve.
        /// </param>
        public void WritePendulum(string outputPath, string worldId = "pendulum",
                                  string simResultsCsv = null)
        {
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            if (File.Exists(outputPath))
                File.Delete(outputPath);

            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = outputPath,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString();

            using var conn = new SqliteConnection(connectionString);
            conn.Open();

            CreateSchema(conn);
            SeedPendulum(conn, worldId, simResultsCsv);
        }

        // Kept for backward compatibility with existing callers.
        public void WriteHardcodedPendulum(string outputPath, string worldId = "pendulum")
            => WritePendulum(outputPath, worldId, null);

        /// <summary>
        /// Write a wind-turbine world package: the Mountain turbine's rotor motion, sourced
        /// from the R2011a Simulink model (see SCOPE.md 2026-08-02).
        /// </summary>
        /// <param name="outputPath">Full path to the .db file to create.</param>
        /// <param name="worldId">World id to embed.</param>
        /// <param name="simResultsCsv">
        /// Path to the CSV written by wtExportSimSamples.m. Columns are Time,Position,Velocity
        /// -- the same three the pendulum uses, which is why this reuses LoadSamplesFromCsv
        /// unchanged. For the turbine they carry time (s), ROTOR AZIMUTH (rad, UNWRAPPED), and
        /// rotor angular velocity (rad/s).
        /// </param>
        /// <param name="allowFallback">
        /// Opt in to the constant-rate placeholder when the CSV is missing. Defaults to FALSE,
        /// which is a deliberate difference from WritePendulum -- see the note below.
        /// </param>
        /// <remarks>
        /// WHY THIS THROWS WHERE WritePendulum SILENTLY FALLS BACK
        ///
        /// WritePendulum degrades to GenerateSmallAngleFallback() without complaint. That was
        /// right for a tracer bullet whose job was to prove the pipeline moved bytes end to end.
        /// It is the wrong default here, for a reason specific to what a turbine looks like.
        ///
        /// A pendulum on the analytic curve still visibly swings, and anyone who knows the
        /// project can tell roughly what they are looking at. A turbine on a constant-rate curve
        /// looks EXACTLY like a turbine on real model output -- a rotor going round at a steady
        /// speed is a rotor going round at a steady speed. The failure is invisible at precisely
        /// the place someone would check for it.
        ///
        /// That matters because the MVP's engineering-rigor claim now rests on this data being
        /// model output. SCOPE.md 2026-08-02 is explicit that demo materials may say "real
        /// engineering model" and may not say "Simscape" or "CAD-verified physics". Shipping the
        /// placeholder by accident and describing it as model output would make that claim false
        /// with nothing on screen to give it away. So the fallback exists, but it cannot be
        /// reached without asking for it by name.
        /// </remarks>
        public void WriteTurbine(string outputPath, string worldId = "turbine",
                                 string simResultsCsv = null, bool allowFallback = false)
        {
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            if (File.Exists(outputPath))
                File.Delete(outputPath);

            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = outputPath,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString();

            using var conn = new SqliteConnection(connectionString);
            conn.Open();

            CreateSchema(conn);
            SeedTurbine(conn, worldId, simResultsCsv, allowFallback);
        }

        /// <summary>
        /// Day 12: exports a SNAPSHOT of the CURRENT economy state (Communities, Resources,
        /// CommunityResources, all StoneLedger entries, plus a derived per-community Dollar
        /// Vault balance/threshold and CommunityFailureStateService failure status) into its
        /// own world-package .db -- a sibling to WritePendulum, not an extension of it.
        ///
        /// WHY A SIBLING METHOD, NOT A MERGE INTO WritePendulum'S FILE: SCOPE.md's 2026-07-02
        /// entry already establishes economy.db and pendulum.db as deliberately separate files
        /// ("different consumers, different change rates"). WritePendulum deletes-and-recreates
        /// its ENTIRE output file on every call; if this snapshot wrote into that same file it
        /// would either wipe the pendulum data or have to special-case around it. Writing to
        /// its own output path preserves that same separation one level up, for the exported
        /// package as much as for the live authoring databases -- and lets each export run
        /// independently (re-exporting the economy snapshot doesn't require re-running the
        /// pendulum export, and vice versa).
        ///
        /// <paramref name="economyDbPath"/> is opened READ-ONLY via the existing Day 5-11
        /// repositories/services (EconomyRepository, DollarVaultRepository via
        /// CommunityFailureStateService) -- this method never writes to economy.db itself.
        /// </summary>
        /// <param name="outputPath">Full path to the snapshot .db file to create (a separate
        /// file from whatever WritePendulum writes to -- do not point both at the same path).</param>
        /// <param name="economyDbPath">Path to the live, already-seeded economy.db to read from.</param>
        /// <param name="worldId">World id to embed in the snapshot's WorldInfo row.</param>
        /// <summary>
        /// Day 13: same as <see cref="WriteEconomySnapshot(string, string, string)"/>, but
        /// with no economyDbPath supplied -- exports the CANONICAL GOLDEN DEMO SCENARIO
        /// (GoldenEconomyScenario.Seed) instead of an existing hand-prepared database.
        ///
        /// Mirrors WritePendulum's simResultsCsv=null fallback pattern: when the caller
        /// doesn't have (or doesn't want to point at) a specific source database, this
        /// generates the canonical data deterministically from code -- GenerateSmallAngleFallback()
        /// for the pendulum, GoldenEconomyScenario.Seed() here -- rather than requiring an
        /// external prepared file. This is the "produce a demo world package" default path
        /// Task 2 asked for.
        /// </summary>
        public void WriteGoldenEconomySnapshot(string outputPath, string worldId = "economy")
        {
            var tempEconomyDbPath = Path.Combine(Path.GetTempPath(), $"dwm_golden_economy_{Guid.NewGuid():N}.db");
            try
            {
                GoldenEconomyScenario.Seed(tempEconomyDbPath);
                WriteEconomySnapshot(outputPath, tempEconomyDbPath, worldId);
            }
            finally
            {
                // EconomyRepository and CommunityFailureStateService open short-lived
                // connections while copying the seeded scenario.  Microsoft.Data.Sqlite
                // returns those handles to its pool when disposed, which can still keep
                // the temporary database locked on Windows.  Release the pool before
                // deleting this per-export temporary file.
                SqliteConnection.ClearAllPools();
                if (File.Exists(tempEconomyDbPath))
                    File.Delete(tempEconomyDbPath);
            }
        }

        public void WriteEconomySnapshot(string outputPath, string economyDbPath, string worldId = "economy")
        {
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            if (File.Exists(outputPath))
                File.Delete(outputPath);

            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = outputPath,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString();

            using (var conn = new SqliteConnection(connectionString))
            {
                conn.Open();
                CreateEconomySchema(conn);
                SeedEconomySnapshot(conn, worldId, economyDbPath);
            } // conn disposed here -- releases the write handle before this method returns
              // (Task 4b: DWMStudio must not hold the .db handle after export completes).
        }

        // ------------------------------------------------------------------
        private static void CreateEconomySchema(SqliteConnection conn)
        {
            const string sql = @"
                CREATE TABLE WorldInfo (
                    WorldId        TEXT PRIMARY KEY NOT NULL,
                    Name           TEXT,
                    Description    TEXT,
                    SchemaVersion  INTEGER,
                    ExportedAtUtc  TEXT
                );
                CREATE TABLE Communities (
                    CommunityId  TEXT PRIMARY KEY NOT NULL,
                    Name         TEXT,
                    BiomeType    TEXT,
                    Description  TEXT
                );
                CREATE TABLE Resources (
                    ResourceId  TEXT PRIMARY KEY NOT NULL,
                    Name        TEXT,
                    Unit        TEXT,
                    Category    TEXT
                );
                CREATE TABLE CommunityResources (
                    CommunityId  TEXT NOT NULL,
                    ResourceId   TEXT NOT NULL,
                    Role         TEXT NOT NULL,
                    Quantity     REAL NOT NULL,
                    PRIMARY KEY (CommunityId, ResourceId, Role)
                );
                CREATE TABLE StoneLedger (
                    TransactionId    TEXT PRIMARY KEY NOT NULL,
                    Timestamp        TEXT NOT NULL,
                    FromCommunityId  TEXT NOT NULL,
                    ToCommunityId    TEXT NOT NULL,
                    Amount           REAL NOT NULL,
                    ResourceId       TEXT,
                    Quantity         REAL,
                    Memo             TEXT
                );
                CREATE TABLE CommunityDollarVault (
                    CommunityId  TEXT PRIMARY KEY NOT NULL,
                    Balance      REAL NOT NULL,
                    Threshold    REAL NOT NULL
                );
                CREATE TABLE CommunityFailureStatus (
                    CommunityId  TEXT PRIMARY KEY NOT NULL,
                    State        TEXT NOT NULL
                );
            ";
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        // ------------------------------------------------------------------
        private static void SeedEconomySnapshot(SqliteConnection conn, string worldId, string economyDbPath)
        {
            var economy = new EconomyRepository(economyDbPath);
            var failureState = new CommunityFailureStateService(economyDbPath);

            var communities = economy.GetCommunities();
            var resources = economy.GetResources();
            var ledgerEntries = economy.GetLedgerEntries();

            using var tx = conn.BeginTransaction();

            Exec(conn, tx,
                "INSERT INTO WorldInfo (WorldId, Name, Description, SchemaVersion, ExportedAtUtc) VALUES ($id,$n,$d,$v,$ts);",
                ("$id", worldId), ("$n", "DWM Economy Snapshot"),
                ("$d", "Current-state export of the Stone ledger economy for UE to read"),
                ("$v", EconomySchemaVersion), ("$ts", DateTimeOffset.UtcNow.ToString("o")));

            foreach (var c in communities)
            {
                Exec(conn, tx,
                    "INSERT INTO Communities (CommunityId, Name, BiomeType, Description) VALUES ($id,$n,$b,$d);",
                    ("$id", c.CommunityId), ("$n", c.Name), ("$b", c.BiomeType),
                    ("$d", (object?)c.Description ?? DBNull.Value));
            }

            foreach (var r in resources)
            {
                Exec(conn, tx,
                    "INSERT INTO Resources (ResourceId, Name, Unit, Category) VALUES ($id,$n,$u,$c);",
                    ("$id", r.ResourceId), ("$n", r.Name), ("$u", r.Unit),
                    ("$c", (object?)r.Category ?? DBNull.Value));
            }

            foreach (var c in communities)
            {
                foreach (var cr in economy.GetCommunityResources(c.CommunityId))
                {
                    Exec(conn, tx,
                        "INSERT INTO CommunityResources (CommunityId, ResourceId, Role, Quantity) VALUES ($cid,$rid,$role,$qty);",
                        ("$cid", cr.CommunityId), ("$rid", cr.ResourceId), ("$role", cr.Role.ToString()),
                        ("$qty", cr.Quantity));
                }
            }

            foreach (var entry in ledgerEntries)
            {
                Exec(conn, tx,
                    @"INSERT INTO StoneLedger
                        (TransactionId, Timestamp, FromCommunityId, ToCommunityId, Amount, ResourceId, Quantity, Memo)
                      VALUES ($id,$ts,$from,$to,$amount,$resource,$qty,$memo);",
                    ("$id", entry.TransactionId), ("$ts", entry.Timestamp.ToString("o")),
                    ("$from", entry.FromCommunityId), ("$to", entry.ToCommunityId), ("$amount", entry.Amount),
                    ("$resource", (object?)entry.ResourceId ?? DBNull.Value),
                    ("$qty", (object?)entry.Quantity ?? DBNull.Value),
                    ("$memo", (object?)entry.Memo ?? DBNull.Value));
            }

            foreach (var c in communities)
            {
                var status = failureState.GetFailureState(c.CommunityId);

                Exec(conn, tx,
                    "INSERT INTO CommunityDollarVault (CommunityId, Balance, Threshold) VALUES ($id,$bal,$thr);",
                    ("$id", c.CommunityId), ("$bal", status.VaultBalance), ("$thr", status.VaultThreshold));

                Exec(conn, tx,
                    "INSERT INTO CommunityFailureStatus (CommunityId, State) VALUES ($id,$state);",
                    ("$id", c.CommunityId), ("$state", status.State.ToString()));
            }

            tx.Commit();
            Console.WriteLine(
                $"[DWM] Wrote economy snapshot: {communities.Count} communities, {resources.Count} resources, " +
                $"{ledgerEntries.Count} StoneLedger entries, {communities.Count} vault/failure-status rows.");
        }

        // ------------------------------------------------------------------
        private static void CreateSchema(SqliteConnection conn)
        {
            const string sql = @"
                CREATE TABLE WorldInfo (
                    WorldId        TEXT PRIMARY KEY NOT NULL,
                    Name           TEXT,
                    Description    TEXT,
                    SchemaVersion  INTEGER
                );
                CREATE TABLE Blocks (
                    BlockId    TEXT PRIMARY KEY NOT NULL,
                    Name       TEXT,
                    BlockType  TEXT
                );
                CREATE TABLE Parameters (
                    BlockId  TEXT NOT NULL,
                    Name     TEXT NOT NULL,
                    Value    REAL,
                    Unit     TEXT,
                    PRIMARY KEY (BlockId, Name)
                );
                CREATE TABLE AssetBindings (
                    BlockId    TEXT NOT NULL,
                    AssetPath  TEXT NOT NULL,
                    AssetType  TEXT,
                    Role       TEXT,
                    PRIMARY KEY (BlockId, AssetPath, Role)
                );
                CREATE TABLE SimSamples (
                    BlockId   TEXT NOT NULL,
                    Time      REAL NOT NULL,
                    Position  REAL,
                    Velocity  REAL,
                    PRIMARY KEY (BlockId, Time)
                );
            ";
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        // ------------------------------------------------------------------
        private static void SeedPendulum(SqliteConnection conn, string worldId,
                                         string simResultsCsv)
        {
            using var tx = conn.BeginTransaction();

            Exec(conn, tx,
                "INSERT INTO WorldInfo (WorldId, Name, Description, SchemaVersion) VALUES ($id,$n,$d,$v);",
                ("$id", worldId), ("$n", "Tracer Pendulum"),
                ("$d", "Single pendulum driven by Simscape Multibody physics"),
                ("$v", SchemaVersion));

            const string armBlockId = "block_arm";
            Exec(conn, tx,
                "INSERT INTO Blocks (BlockId, Name, BlockType) VALUES ($id,$n,$t);",
                ("$id", armBlockId), ("$n", "PendulumArm"), ("$t", "RigidBody"));

            InsertParam(conn, tx, armBlockId, "armLength",    1.0,    "m");
            InsertParam(conn, tx, armBlockId, "bobMass",      3.14,   "kg");   // from CAD
            InsertParam(conn, tx, armBlockId, "initialAngle", 0.5236, "rad");
            InsertParam(conn, tx, armBlockId, "gravity",      9.81,   "m/s2");

            Exec(conn, tx,
                "INSERT INTO AssetBindings (BlockId, AssetPath, AssetType, Role) VALUES ($id,$p,$at,$r);",
                ("$id", armBlockId),
                ("$p", "/Engine/BasicShapes/Cylinder.Cylinder"),
                ("$at", "StaticMesh"), ("$r", "Visual"));

            // --- Sim samples: from CSV if available, else analytic fallback ---
            var samples = LoadSamplesFromCsv(simResultsCsv);
            string source;
            if (samples != null && samples.Count > 0)
            {
                source = "Simscape CSV";
            }
            else
            {
                samples = GenerateSmallAngleFallback();
                source = "analytic small-angle fallback";
            }

            foreach (var s in samples)
            {
                Exec(conn, tx,
                    "INSERT INTO SimSamples (BlockId, Time, Position, Velocity) VALUES ($id,$t,$p,$v);",
                    ("$id", armBlockId), ("$t", s.Time), ("$p", s.Position), ("$v", s.Velocity));
            }

            tx.Commit();
            Console.WriteLine($"[DWM] Wrote {samples.Count} sim samples ({source}).");
        }

        // ------------------------------------------------------------------
        private static void SeedTurbine(SqliteConnection conn, string worldId,
                                        string simResultsCsv, bool allowFallback)
        {
            // Load BEFORE opening the transaction: if the CSV is missing and fallback was not
            // requested, fail without having created a half-populated database on disk.
            var samples = LoadSamplesFromCsv(simResultsCsv);
            string source;

            if (samples != null && samples.Count > 0)
            {
                source = "R2011a Simulink model (wtExportSimSamples.m)";
            }
            else if (allowFallback)
            {
                samples = GenerateConstantRotationFallback();
                source = "CONSTANT-RATE PLACEHOLDER -- NOT MODEL OUTPUT";
                Console.WriteLine(
                    "[DWM] ***********************************************************\n" +
                    "[DWM] WARNING: turbine package built from the constant-rate\n" +
                    "[DWM] placeholder, NOT from the Simulink model. The rotor will\n" +
                    "[DWM] look completely normal on screen, so nothing downstream\n" +
                    "[DWM] will reveal this. Do NOT describe this build as simulation\n" +
                    "[DWM] output in any demo or campaign material.\n" +
                    "[DWM] ***********************************************************");
            }
            else
            {
                throw new FileNotFoundException(
                    "WriteTurbine found no simulation results, and allowFallback is false.\n\n" +
                    $"  Looked for: {simResultsCsv ?? "(null -- no path was passed)"}\n\n" +
                    "Produce it in MATLAB with:\n" +
                    "    out = wtRunSimulation();\n" +
                    "    wtExportSimSamples(out, 'wtSimSamples.csv');\n\n" +
                    "Pass allowFallback: true ONLY if you deliberately want the constant-rate " +
                    "placeholder. It is not model output and is indistinguishable on screen " +
                    "from data that is.",
                    simResultsCsv ?? "(none)");
            }

            using var tx = conn.BeginTransaction();

            Exec(conn, tx,
                "INSERT INTO WorldInfo (WorldId, Name, Description, SchemaVersion) VALUES ($id,$n,$d,$v);",
                ("$id", worldId), ("$n", "Mountain Wind Turbine"),
                ("$d", "Rotor motion from the R2011a Simulink turbine model (wtTurbine3MW). " +
                       "Lumped-parameter engineering model -- NOT Simscape Multibody, NOT " +
                       "CAD-linked multibody dynamics. See SCOPE.md 2026-08-02."),
                ("$v", SchemaVersion));

            const string rotorBlockId = "block_rotor";
            Exec(conn, tx,
                "INSERT INTO Blocks (BlockId, Name, BlockType) VALUES ($id,$n,$t);",
                ("$id", rotorBlockId), ("$n", "TurbineRotor"), ("$t", "RigidBody"));

            // DESCRIPTIVE METADATA ONLY -- none of this drives the motion, which comes entirely
            // from SimSamples below. It travels with the package so the UE side and anyone
            // inspecting the .db can see what machine produced the curve.
            //
            // KEEP IN SYNC WITH wtParameters.m. These are duplicated here rather than read from
            // the model because the C# layer has no MATLAB dependency and is not getting one for
            // four numbers -- but duplication means they can drift, and drift here is silent.
            InsertParam(conn, tx, rotorBlockId, "bladeLength",   58.5,    "m");     // BOM 1100
            InsertParam(conn, tx, rotorBlockId, "bladeCount",     3.0,    "count");
            InsertParam(conn, tx, rotorBlockId, "ratedPower",     3.0e6,  "W");     // wtTurbine3MW
            InsertParam(conn, tx, rotorBlockId, "gearboxRatio", 104.3,    "-");     // BOM 2300

            // !!! PLACEHOLDER ASSET PATH -- MUST BE REPLACED BEFORE THIS RENDERS ANYTHING !!!
            // The Mountain turbine mesh was placed on Day 21; its real content path is not
            // recorded in any document this exporter can see. A wrong path here binds nothing
            // and the rotor simply will not appear, with no error -- so treat a turbine that
            // does not show up as this line first, before suspecting the data.
            Exec(conn, tx,
                "INSERT INTO AssetBindings (BlockId, AssetPath, AssetType, Role) VALUES ($id,$p,$at,$r);",
                ("$id", rotorBlockId),
                ("$p", "REPLACE_ME/WindTurbineRotor"),
                ("$at", "StaticMesh"), ("$r", "Visual"));

            foreach (var s in samples)
            {
                Exec(conn, tx,
                    "INSERT INTO SimSamples (BlockId, Time, Position, Velocity) VALUES ($id,$t,$p,$v);",
                    ("$id", rotorBlockId), ("$t", s.Time), ("$p", s.Position), ("$v", s.Velocity));
            }

            // ----------------------------------------------------------------
            // ADDITIONAL CHANNELS: the turbine has four moving parts, not one.
            //
            // SimSamples is keyed on (BlockId, Time), so a mechanism with several
            // moving parts is expressed as several BLOCKS rather than as extra
            // columns. The pendulum tracer used a single block because a pendulum has
            // a single moving part; this is the same schema being used as designed,
            // and it needs NO migration.
            //
            // Discovery is by convention: wtExportSimSamples.m writes sibling files
            // named <base>_rotor.csv, <base>_pitch.csv and so on, so given the rotor
            // path the rest are found by substitution. A caller who passes a path
            // without "_rotor" gets rotor-only behaviour, which is what every
            // existing caller and test does.
            var extraTotal = SeedTurbineChannel(conn, tx, simResultsCsv, "pitch",
                "block_pitch", "BladePitch",  "RigidBody", "REPLACE_ME/WindTurbineBlade");
            extraTotal += SeedTurbineChannel(conn, tx, simResultsCsv, "yaw",
                "block_yaw",   "Nacelle",     "RigidBody", "REPLACE_ME/WindTurbineNacelle");
            extraTotal += SeedTurbineChannel(conn, tx, simResultsCsv, "tower",
                "block_tower", "Tower",       "RigidBody", "REPLACE_ME/WindTurbineTower");

            // NOT KINEMATIC, and deliberately so. BlockType 'Signal' marks a block
            // whose Position and Velocity are two plain channel slots rather than an
            // angle and a rate -- here electrical power (W) and wind speed (m/s),
            // which belong on a HUD rather than on a mesh. Written down here so the
            // reuse is documented rather than guessed at, and given no AssetBinding
            // because there is nothing to bind it to.
            extraTotal += SeedTurbineChannel(conn, tx, simResultsCsv, "power",
                "block_power", "PowerOutput", "Signal", null);

            tx.Commit();

            var lastAzimuth = samples[samples.Count - 1].Position;
            Console.WriteLine(
                $"[DWM] Wrote {samples.Count} turbine rotor samples ({source}). " +
                $"Azimuth spans {lastAzimuth:F2} rad " +
                $"({lastAzimuth / (2 * Math.PI):F1} revolutions, UNWRAPPED -- " +
                "the UE actor takes the modulus at read time).");
            if (extraTotal > 0)
                Console.WriteLine($"[DWM] Plus {extraTotal} samples across the pitch/yaw/tower/power channels.");
        }

        /// <summary>
        /// Seed one additional turbine channel from a sibling CSV, if that file exists.
        /// Returns the number of samples written (0 when the file is absent).
        /// </summary>
        /// <remarks>
        /// YAW IS AN ERROR ANGLE, NOT AN ABSOLUTE HEADING. The model logs the angle
        /// between where the nacelle points and where the wind comes from, so driving
        /// a nacelle's world rotation straight from it will look wrong. Absolute
        /// heading needs wind direction, which the model does not currently log --
        /// adding it means a 13th channel on the LogMux in wtBuildModel. Until then
        /// the yaw block is diagnostic rather than animation input.
        /// </remarks>
        private static int SeedTurbineChannel(SqliteConnection conn, SqliteTransaction tx,
            string rotorCsvPath, string suffix, string blockId, string name,
            string blockType, string assetPath)
        {
            if (string.IsNullOrEmpty(rotorCsvPath)) return 0;

            // Only substitute on the final "_rotor" so a directory called e.g.
            // "rotor_studies" upstream in the path cannot be rewritten by accident.
            int at = rotorCsvPath.LastIndexOf("_rotor", StringComparison.Ordinal);
            if (at < 0) return 0;
            var path = rotorCsvPath.Substring(0, at) + "_" + suffix + rotorCsvPath.Substring(at + "_rotor".Length);

            var samples = LoadSamplesFromCsv(path);
            if (samples == null || samples.Count == 0) return 0;

            Exec(conn, tx,
                "INSERT INTO Blocks (BlockId, Name, BlockType) VALUES ($id,$n,$t);",
                ("$id", blockId), ("$n", name), ("$t", blockType));

            if (!string.IsNullOrEmpty(assetPath))
            {
                Exec(conn, tx,
                    "INSERT INTO AssetBindings (BlockId, AssetPath, AssetType, Role) VALUES ($id,$p,$at,$r);",
                    ("$id", blockId), ("$p", assetPath), ("$at", "StaticMesh"), ("$r", "Visual"));
            }

            foreach (var s in samples)
            {
                Exec(conn, tx,
                    "INSERT INTO SimSamples (BlockId, Time, Position, Velocity) VALUES ($id,$t,$p,$v);",
                    ("$id", blockId), ("$t", s.Time), ("$p", s.Position), ("$v", s.Velocity));
            }
            return samples.Count;
        }

        /// <summary>
        /// Constant-rate rotor placeholder. Reachable only via WriteTurbine's explicit
        /// allowFallback flag -- see the remarks on that method for why it is not the default.
        /// </summary>
        private static List<Sample> GenerateConstantRotationFallback()
        {
            // 12.5 rpm, mid-range for a 3 MW machine. Deliberately CONSTANT: this makes no
            // attempt to imitate the model, because a placeholder that imitated the model well
            // would be harder to notice, which is the opposite of what a placeholder should be.
            var list = new List<Sample>();
            double omega = 12.5 * 2.0 * Math.PI / 60.0;   // rad/s
            for (int i = 0; i <= 300; i++)
            {
                double t = i / 30.0;
                list.Add(new Sample(t, omega * t, omega));
            }
            return list;
        }

        // ------------------------------------------------------------------
        private readonly struct Sample
        {
            public readonly double Time, Position, Velocity;
            public Sample(double t, double p, double v) { Time = t; Position = p; Velocity = v; }
        }

        private static List<Sample> LoadSamplesFromCsv(string csvPath)
        {
            if (string.IsNullOrEmpty(csvPath) || !File.Exists(csvPath))
                return null;

            var result = new List<Sample>();
            var lines = File.ReadAllLines(csvPath);

            // Expect header: Time,Position,Velocity
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.Length == 0) continue;

                // Skip header row (non-numeric first field)
                var parts = line.Split(',');
                if (parts.Length < 3) continue;
                if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double t))
                    continue; // header or bad row

                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double p);
                double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double v);
                result.Add(new Sample(t, p, v));
            }
            return result;
        }

        private static List<Sample> GenerateSmallAngleFallback()
        {
            // theta(t) = A cos(omega t), omega = sqrt(g/L)
            var list = new List<Sample>();
            double A = 0.5236, omega = Math.Sqrt(9.81 / 1.0);
            for (int i = 0; i <= 60; i++)
            {
                double t = i / 30.0;
                double theta = A * Math.Cos(omega * t);
                double thetaDot = -A * omega * Math.Sin(omega * t);
                list.Add(new Sample(t, theta, thetaDot));
            }
            return list;
        }

        // ------------------------------------------------------------------
        private static void InsertParam(SqliteConnection conn, SqliteTransaction tx,
            string blockId, string name, double value, string unit)
        {
            Exec(conn, tx,
                "INSERT INTO Parameters (BlockId, Name, Value, Unit) VALUES ($b,$n,$v,$u);",
                ("$b", blockId), ("$n", name), ("$v", value), ("$u", unit));
        }

        private static void Exec(SqliteConnection conn, SqliteTransaction tx,
            string sql, params (string name, object value)[] parameters)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            foreach (var (name, value) in parameters)
                cmd.Parameters.AddWithValue(name, value);
            cmd.ExecuteNonQuery();
        }
    }
}
