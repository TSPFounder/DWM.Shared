// EconomySeeder.cs
// Writes the DWM economy ledger database: communities, resources, the Stone
// (LETS) ledger, and the Dollar Vault.
//
// This is the C# equivalent of running economy_schema.sql (DDL + seed) — see
// that file (in this folder) for the canonical schema and the design spec in
// ECONOMY_SCHEMA_SPEC.md. Table design and seed values here must stay in
// lockstep with economy_schema.sql; do not diverge them.
//
// Separate from WorldPackageExporter.cs's schema (WorldInfo/Blocks/Parameters/
// SimSamples), which is the engineering-mechanism package format, not the
// economy ledger. Recommended as a separate .db file — see ECONOMY_SCHEMA_SPEC.md.
//
// Requires NuGet package: Microsoft.Data.Sqlite

using System.IO;
using Microsoft.Data.Sqlite;

namespace DWM.Shared.Economy
{
    public sealed class EconomySeeder
    {
        /// <summary>
        /// Create the economy schema and write the MVP seed data (five
        /// communities, ten resources, CommunityResources links, and the
        /// Dollar Vault opening balance). The StoneLedger starts empty.
        /// </summary>
        /// <param name="outputPath">Full path to the .db file to create.</param>
        public void WriteEconomy(string outputPath)
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

            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA foreign_keys = ON;";
                pragma.ExecuteNonQuery();
            }

            CreateSchema(conn);
            SeedData(conn);
        }

        // ------------------------------------------------------------------
        private static void CreateSchema(SqliteConnection conn)
        {
            const string sql = @"
                CREATE TABLE Communities (
                    CommunityId  TEXT PRIMARY KEY NOT NULL,
                    Name         TEXT NOT NULL,
                    BiomeType    TEXT NOT NULL,
                    Description  TEXT
                );
                CREATE TABLE Resources (
                    ResourceId  TEXT PRIMARY KEY NOT NULL,
                    Name        TEXT NOT NULL,
                    Unit        TEXT NOT NULL,
                    Category    TEXT
                );
                CREATE TABLE CommunityResources (
                    CommunityId  TEXT NOT NULL REFERENCES Communities(CommunityId),
                    ResourceId   TEXT NOT NULL REFERENCES Resources(ResourceId),
                    Role         TEXT NOT NULL CHECK (Role IN ('Produces', 'Needs')),
                    Quantity     REAL NOT NULL DEFAULT 0,
                    PRIMARY KEY (CommunityId, ResourceId, Role)
                );
                CREATE TABLE StoneLedger (
                    TransactionId    TEXT PRIMARY KEY NOT NULL,
                    Timestamp        TEXT NOT NULL,
                    FromCommunityId  TEXT NOT NULL REFERENCES Communities(CommunityId),
                    ToCommunityId    TEXT NOT NULL REFERENCES Communities(CommunityId),
                    Amount           REAL NOT NULL CHECK (Amount > 0),
                    ResourceId       TEXT REFERENCES Resources(ResourceId),
                    Quantity         REAL,
                    Memo             TEXT,
                    CHECK (FromCommunityId <> ToCommunityId)
                );
                CREATE INDEX idx_stoneledger_from ON StoneLedger(FromCommunityId);
                CREATE INDEX idx_stoneledger_to   ON StoneLedger(ToCommunityId);
                CREATE TABLE DollarVaultLedger (
                    EntryId    TEXT PRIMARY KEY NOT NULL,
                    Timestamp  TEXT NOT NULL,
                    DeltaAmount REAL NOT NULL,
                    Reason     TEXT
                );
                CREATE TABLE DollarVaultConfig (
                    ConfigId                    INTEGER PRIMARY KEY CHECK (ConfigId = 1),
                    CascadingFailureThreshold   REAL NOT NULL
                );
            ";
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        // ------------------------------------------------------------------
        private static void SeedData(SqliteConnection conn)
        {
            using var tx = conn.BeginTransaction();

            InsertCommunity(conn, tx, "mountain", "Mountain", "Mountain",
                "Wind-power generation via the verified turbine mechanism.");
            InsertCommunity(conn, tx, "hillside", "Hillside", "Hillside",
                "Orchards and textile production on the slopes.");
            InsertCommunity(conn, tx, "valley", "Valley", "Valley",
                "Agricultural heartland; grain and water.");
            InsertCommunity(conn, tx, "suburb", "Suburb", "Suburb",
                "General labor and light manufacturing.");
            InsertCommunity(conn, tx, "city", "City", "City",
                "Manufacturing hub; tools and software services.");

            InsertResource(conn, tx, "timber", "Timber", "unit", "raw");
            InsertResource(conn, tx, "wind_power", "Wind-Power", "kWh", "power");
            InsertResource(conn, tx, "orchard_fruit", "Orchard Fruit", "unit", "raw");
            InsertResource(conn, tx, "wool", "Wool", "unit", "raw");
            InsertResource(conn, tx, "grain", "Grain", "unit", "raw");
            InsertResource(conn, tx, "water", "Water", "unit", "raw");
            InsertResource(conn, tx, "skilled_labor", "Skilled Labor", "hour", "labor");
            InsertResource(conn, tx, "textiles", "Textiles", "unit", "manufactured");
            InsertResource(conn, tx, "manufactured_tools", "Manufactured Tools", "unit", "manufactured");
            InsertResource(conn, tx, "software_services", "Software / Maintenance Services", "hour", "labor");

            // Mountain: Produces Timber, Wind-Power | Needs Grain, Manufactured Tools, Skilled Labor, Software Services
            InsertCommunityResource(conn, tx, "mountain", "timber", "Produces", 100);
            InsertCommunityResource(conn, tx, "mountain", "wind_power", "Produces", 100);
            InsertCommunityResource(conn, tx, "mountain", "grain", "Needs", 20);
            InsertCommunityResource(conn, tx, "mountain", "manufactured_tools", "Needs", 20);
            InsertCommunityResource(conn, tx, "mountain", "skilled_labor", "Needs", 10);
            InsertCommunityResource(conn, tx, "mountain", "software_services", "Needs", 10);

            // Hillside: Produces Orchard Fruit, Wool | Needs Timber, Wind-Power, Textiles
            InsertCommunityResource(conn, tx, "hillside", "orchard_fruit", "Produces", 100);
            InsertCommunityResource(conn, tx, "hillside", "wool", "Produces", 100);
            InsertCommunityResource(conn, tx, "hillside", "timber", "Needs", 20);
            InsertCommunityResource(conn, tx, "hillside", "wind_power", "Needs", 20);
            InsertCommunityResource(conn, tx, "hillside", "textiles", "Needs", 20);

            // Valley: Produces Grain, Water | Needs Wool, Manufactured Tools
            InsertCommunityResource(conn, tx, "valley", "grain", "Produces", 100);
            InsertCommunityResource(conn, tx, "valley", "water", "Produces", 100);
            InsertCommunityResource(conn, tx, "valley", "wool", "Needs", 20);
            InsertCommunityResource(conn, tx, "valley", "manufactured_tools", "Needs", 20);

            // Suburb: Produces Skilled Labor, Textiles | Needs Grain, Wind-Power
            InsertCommunityResource(conn, tx, "suburb", "skilled_labor", "Produces", 100);
            InsertCommunityResource(conn, tx, "suburb", "textiles", "Produces", 100);
            InsertCommunityResource(conn, tx, "suburb", "grain", "Needs", 20);
            InsertCommunityResource(conn, tx, "suburb", "wind_power", "Needs", 20);

            // City: Produces Manufactured Tools, Software Services | Needs Timber, Orchard Fruit, Water
            InsertCommunityResource(conn, tx, "city", "manufactured_tools", "Produces", 100);
            InsertCommunityResource(conn, tx, "city", "software_services", "Produces", 100);
            InsertCommunityResource(conn, tx, "city", "timber", "Needs", 20);
            InsertCommunityResource(conn, tx, "city", "orchard_fruit", "Needs", 20);
            InsertCommunityResource(conn, tx, "city", "water", "Needs", 20);

            // Dollar Vault: seed with a starting balance + failure threshold (placeholder values).
            Exec(conn, tx,
                "INSERT INTO DollarVaultConfig (ConfigId, CascadingFailureThreshold) VALUES (1, $t);",
                ("$t", 500));
            Exec(conn, tx,
                "INSERT INTO DollarVaultLedger (EntryId, Timestamp, DeltaAmount, Reason) VALUES ($id,$ts,$d,$r);",
                ("$id", "seed-001"), ("$ts", "2026-07-02T00:00:00Z"),
                ("$d", 5000), ("$r", "Initial Dollar Vault funding (MVP seed)"));

            // No StoneLedger seed rows — the ledger starts empty; the Day-27 demo trade
            // is the first real transaction, which is the point.

            tx.Commit();
        }

        // ------------------------------------------------------------------
        private static void InsertCommunity(SqliteConnection conn, SqliteTransaction tx,
            string id, string name, string biomeType, string description)
        {
            Exec(conn, tx,
                "INSERT INTO Communities (CommunityId, Name, BiomeType, Description) VALUES ($id,$n,$b,$d);",
                ("$id", id), ("$n", name), ("$b", biomeType), ("$d", description));
        }

        private static void InsertResource(SqliteConnection conn, SqliteTransaction tx,
            string id, string name, string unit, string category)
        {
            Exec(conn, tx,
                "INSERT INTO Resources (ResourceId, Name, Unit, Category) VALUES ($id,$n,$u,$c);",
                ("$id", id), ("$n", name), ("$u", unit), ("$c", category));
        }

        private static void InsertCommunityResource(SqliteConnection conn, SqliteTransaction tx,
            string communityId, string resourceId, string role, double quantity)
        {
            Exec(conn, tx,
                "INSERT INTO CommunityResources (CommunityId, ResourceId, Role, Quantity) VALUES ($c,$r,$role,$q);",
                ("$c", communityId), ("$r", resourceId), ("$role", role), ("$q", quantity));
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
