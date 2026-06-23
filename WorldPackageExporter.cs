// WorldPackageExporter.cs
// Path B export bridge: writes a DWM world package as a plain SQLite .db file
// that the UE side reads via its built-in SQLite module.
//
// The schema defined here is the single source of truth. The UE reader
// (DwmGameInstance::LoadDwmWorld) must read these exact table and column
// names. Column casing matches the DwmWorldPackageTypes.h structs: BlockId,
// Name, BlockType, etc.
//
// This first version writes a HARDCODED pendulum world (arm + bob) so the
// file -> UE load -> spawn chain can be proven before wiring up the real
// DWM.db read. Replace WriteHardcodedPendulum() with a DWM.db read later.
//
// Requires NuGet package: Microsoft.Data.Sqlite

using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace DWM.Shared
{
    /// <summary>
    /// Exports a DWM world to a self-contained SQLite package file that the
    /// Unreal runtime loads. Schema is plain SQLite — no USQLite asset needed.
    /// </summary>
    public sealed class WorldPackageExporter
    {
        // Schema version written into WorldInfo; bump when columns change.
        public const int SchemaVersion = 1;

        /// <summary>
        /// Write a hardcoded single-pendulum world package to the given path.
        /// Overwrites any existing file at that path.
        /// </summary>
        /// <param name="outputPath">Full path to the .db file to create,
        /// e.g. C:\DreamWorldMaker\Packages\DWM_WorldPackage_pendulum.db</param>
        /// <param name="worldId">World id to embed (also used by the launch URL).</param>
        public void WriteHardcodedPendulum(string outputPath, string worldId = "pendulum")
        {
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            // Fresh file every time — delete then recreate.
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
            SeedPendulum(conn, worldId);
        }

        // ------------------------------------------------------------------
        // Schema — the authoritative definition both sides agree on.
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
        // Hardcoded pendulum data.
        // ------------------------------------------------------------------
        private static void SeedPendulum(SqliteConnection conn, string worldId)
        {
            using var tx = conn.BeginTransaction();

            // World metadata
            Exec(conn, tx,
                "INSERT INTO WorldInfo (WorldId, Name, Description, SchemaVersion) VALUES ($id, $name, $desc, $ver);",
                ("$id", worldId),
                ("$name", "Tracer Pendulum"),
                ("$desc", "Single pendulum - the Phase 3 tracer bullet world"),
                ("$ver", SchemaVersion));

            // One block: the pendulum (arm + bob treated as a single swinging body)
            const string armBlockId = "block_arm";
            Exec(conn, tx,
                "INSERT INTO Blocks (BlockId, Name, BlockType) VALUES ($id, $name, $type);",
                ("$id", armBlockId),
                ("$name", "PendulumArm"),
                ("$type", "RigidBody"));

            // Parameters driving geometry + physics (SI units)
            InsertParam(conn, tx, armBlockId, "armLength",    1.0,    "m");
            InsertParam(conn, tx, armBlockId, "bobMass",      0.5,    "kg");
            InsertParam(conn, tx, armBlockId, "initialAngle", 0.5236, "rad");  // 30 degrees
            InsertParam(conn, tx, armBlockId, "gravity",      9.81,   "m/s2");

            // Asset binding — a real mesh stands in for the arm.
            // Using an engine basic shape so this works before importing registry assets.
            Exec(conn, tx,
                "INSERT INTO AssetBindings (BlockId, AssetPath, AssetType, Role) VALUES ($id, $path, $atype, $role);",
                ("$id", armBlockId),
                ("$path", "/Engine/BasicShapes/Cylinder.Cylinder"),
                ("$atype", "StaticMesh"),
                ("$role", "Visual"));

            // A few precomputed sim samples so UE has motion to play before
            // the real Simscape results are wired in. Small-angle approximation:
            // theta(t) = A * cos(omega * t), omega = sqrt(g / L).
            // A = 0.5236 rad, omega = sqrt(9.81/1.0) ~= 3.132 rad/s.
            double amplitude = 0.5236;
            double omega = Math.Sqrt(9.81 / 1.0);
            for (int i = 0; i <= 60; i++)          // ~2 seconds at 30 fps
            {
                double t = i / 30.0;
                double theta = amplitude * Math.Cos(omega * t);
                double thetaDot = -amplitude * omega * Math.Sin(omega * t);
                Exec(conn, tx,
                    "INSERT INTO SimSamples (BlockId, Time, Position, Velocity) VALUES ($id, $t, $p, $v);",
                    ("$id", armBlockId),
                    ("$t", t),
                    ("$p", theta),
                    ("$v", thetaDot));
            }

            tx.Commit();
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------
        private static void InsertParam(SqliteConnection conn, SqliteTransaction tx,
            string blockId, string name, double value, string unit)
        {
            Exec(conn, tx,
                "INSERT INTO Parameters (BlockId, Name, Value, Unit) VALUES ($b, $n, $v, $u);",
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
