// DollarVaultPerCommunityMigration.cs
// Applies the additive per-community Dollar Vault tables
// (dollar_vault_percommunity_migration.sql, this folder) to an existing economy.db.
// Idempotent (CREATE TABLE/INDEX IF NOT EXISTS + INSERT OR IGNORE for seed data) -- safe to
// call every time DollarVaultRepository is constructed. Does not touch economy_schema.sql,
// DollarVaultLedger, or DollarVaultConfig; this is a separate, additive migration layered on
// top of a database those already created.

using Microsoft.Data.Sqlite;

namespace DWM.Shared.Economy
{
    public static class DollarVaultPerCommunityMigration
    {
        // Kept in lockstep with dollar_vault_percommunity_migration.sql in this folder --
        // same convention TradeRequestsMigration.cs already uses for its own .sql file.
        private const string Sql = @"
            CREATE TABLE IF NOT EXISTS CommunityDollarVaultLedger (
                EntryId      TEXT PRIMARY KEY NOT NULL,
                CommunityId  TEXT NOT NULL REFERENCES Communities(CommunityId),
                Timestamp    TEXT NOT NULL,
                DeltaAmount  REAL NOT NULL,
                Reason       TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_communitydollarvaultledger_community ON CommunityDollarVaultLedger(CommunityId);

            CREATE TABLE IF NOT EXISTS CommunityDollarVaultConfig (
                CommunityId                 TEXT PRIMARY KEY NOT NULL REFERENCES Communities(CommunityId),
                CascadingFailureThreshold   REAL NOT NULL
            );

            INSERT OR IGNORE INTO CommunityDollarVaultConfig (CommunityId, CascadingFailureThreshold) VALUES
                ('mountain', 500),
                ('hillside', 500),
                ('valley',   500),
                ('suburb',   500),
                ('city',     500);

            INSERT OR IGNORE INTO CommunityDollarVaultLedger (EntryId, CommunityId, Timestamp, DeltaAmount, Reason) VALUES
                ('seed-vault-mountain', 'mountain', '2026-07-02T00:00:00Z', 5000, 'Initial per-community Dollar Vault funding (MVP placeholder, mirrors original global seed)'),
                ('seed-vault-hillside', 'hillside', '2026-07-02T00:00:00Z', 5000, 'Initial per-community Dollar Vault funding (MVP placeholder, mirrors original global seed)'),
                ('seed-vault-valley',   'valley',   '2026-07-02T00:00:00Z', 5000, 'Initial per-community Dollar Vault funding (MVP placeholder, mirrors original global seed)'),
                ('seed-vault-suburb',   'suburb',   '2026-07-02T00:00:00Z', 5000, 'Initial per-community Dollar Vault funding (MVP placeholder, mirrors original global seed)'),
                ('seed-vault-city',     'city',     '2026-07-02T00:00:00Z', 5000, 'Initial per-community Dollar Vault funding (MVP placeholder, mirrors original global seed)');
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
