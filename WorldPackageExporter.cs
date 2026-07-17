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
