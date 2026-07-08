// TradeRequestsMigration.cs
// Applies the additive TradeRequests table (trade_requests_migration.sql, this folder) to an
// existing economy.db. Idempotent (CREATE TABLE/INDEX IF NOT EXISTS) — safe to call every
// time TradeRequestRepository is constructed. Does not touch economy_schema.sql or
// EconomySeeder.cs; this is a separate, additive migration layered on top of a database those
// already created and seeded.

using Microsoft.Data.Sqlite;

namespace DWM.Shared.Economy
{
    public static class TradeRequestsMigration
    {
        // Kept in lockstep with trade_requests_migration.sql in this folder — same
        // convention EconomySeeder.cs already uses for economy_schema.sql.
        private const string Sql = @"
            CREATE TABLE IF NOT EXISTS TradeRequests (
                RequestId        TEXT PRIMARY KEY NOT NULL,
                FromCommunityId  TEXT NOT NULL REFERENCES Communities(CommunityId),
                ToCommunityId    TEXT NOT NULL REFERENCES Communities(CommunityId),
                Amount           REAL NOT NULL,
                ResourceId       TEXT REFERENCES Resources(ResourceId),
                Quantity         REAL,
                Memo             TEXT,
                Status           TEXT NOT NULL CHECK (Status IN ('Proposed', 'Settled', 'Cancelled')) DEFAULT 'Proposed',
                CreatedAt        TEXT NOT NULL,
                ResolvedAt       TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_traderequests_status ON TradeRequests(Status);
        ";

        public static void EnsureCreated(string dbPath)
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWrite
            }.ToString();

            using var conn = new SqliteConnection(connectionString);
            conn.Open();

            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA foreign_keys = ON;";
                pragma.ExecuteNonQuery();
            }

            using var cmd = conn.CreateCommand();
            cmd.CommandText = Sql;
            cmd.ExecuteNonQuery();
        }
    }
}
