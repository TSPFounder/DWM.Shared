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

namespace DWM.Shared
{
    public sealed class WorldPackageExporter
    {
        public const int SchemaVersion = 1;

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
