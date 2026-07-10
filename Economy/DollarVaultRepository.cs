// DollarVaultRepository.cs
// Read/write access to the per-community Dollar Vault tables
// (dollar_vault_percommunity_migration.sql) -- a SEPARATE mechanism from the original global
// DollarVaultLedger/DollarVaultConfig in economy_schema.sql, which this does not touch or
// read from.
//
// New repository rather than an extension of EconomyRepository -- same reasoning Day 8 used
// for TradeRequestRepository: EconomyRepository is already proven across 30 tests, and this
// is a genuinely separate table/concern even though it's thematically "Dollar Vault" like
// the existing GetDollarVaultBalance() -- the global and per-community vaults are different
// tables with different semantics, not one extended into the other. Composes
// EconomyRepository internally (for a future community-existence check, same pattern
// TradeRequestRepository already uses), rather than reimplementing or absorbing it.
//
// OUTSIDE-RESOURCES DATA GAP (flagging per this task's explicit instruction, not silently
// resolving it): the 10 seeded Resources (Timber, Wind-Power, Orchard Fruit, Wool, Grain,
// Water, Skilled Labor, Textiles, Manufactured Tools, Software Services) are all internally
// produced by the 5 communities -- none represent something purchased from OUTSIDE the
// network, which is what the Dollar Vault is supposed to fund. DebitVault below takes a
// plain string reason rather than a foreign-keyed resource specifically so real
// outside-purchase scenarios can be authored later (Day 13) without new schema -- this file
// does not invent placeholder "outside resource" names or lore.

using System;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace DWM.Shared.Economy
{
    public sealed class DollarVaultRepository
    {
        private readonly string _connectionString;

        public DollarVaultRepository(string dbPath)
        {
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWrite
            }.ToString();

            DollarVaultPerCommunityMigration.EnsureCreated(dbPath);
        }

        /// <summary>
        /// Sum of DeltaAmount for this community's vault ledger. Same COALESCE-in-SQL
        /// approach as EconomyRepository.GetDollarVaultBalance: a community with no ledger
        /// rows returns 0.0 rather than throwing or returning null.
        /// </summary>
        public double GetVaultBalance(string communityId)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(SUM(DeltaAmount), 0) FROM CommunityDollarVaultLedger WHERE CommunityId = $communityId;";
            cmd.Parameters.AddWithValue("$communityId", communityId);
            var result = cmd.ExecuteScalar();
            return Convert.ToDouble(result, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// This community's configured CascadingFailureThreshold. Every one of the 5 MVP
        /// communities gets a config row from this migration's seed data, so a missing row
        /// means communityId doesn't exist at all or the db predates this migration -- throws
        /// rather than silently returning a default, since a silent default threshold would
        /// be exactly the kind of invented-value problem this task's outside-resources note
        /// warns against.
        /// </summary>
        public double GetVaultThreshold(string communityId)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT CascadingFailureThreshold FROM CommunityDollarVaultConfig WHERE CommunityId = $communityId;";
            cmd.Parameters.AddWithValue("$communityId", communityId);
            var result = cmd.ExecuteScalar();
            if (result is null)
                throw new InvalidOperationException(
                    $"No CommunityDollarVaultConfig row for community '{communityId}' -- it either doesn't exist or this database predates the per-community vault migration.");
            return Convert.ToDouble(result, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// True if this community's current vault balance is at or below its own configured
        /// CascadingFailureThreshold (balance &lt;= threshold, not strictly balance &lt;= 0 --
        /// the threshold IS the configured trigger point, which is why it exists as a column
        /// at all rather than being a hardcoded zero check).
        /// </summary>
        public bool IsInCascadingFailure(string communityId) =>
            GetVaultBalance(communityId) <= GetVaultThreshold(communityId);

        /// <summary>
        /// Records an outside-purchase debit against a community's vault: an INSERT-only
        /// append to CommunityDollarVaultLedger with DeltaAmount = -amount (the ledger
        /// convention, same as the original DollarVaultLedger, is positive = inflow,
        /// negative = outflow; callers pass the positive amount being spent). Plain
        /// data-layer write, no business validation beyond a basic amount &gt; 0 sanity check
        /// -- unlike StoneLedger, CommunityDollarVaultLedger has no schema-level CHECK on
        /// DeltaAmount's sign (it's legitimately allowed to be positive for a future credit
        /// path), so nothing else guards against a "debit" of zero or negative amount
        /// actually crediting the vault, which the method name would make surprising.
        /// </summary>
        public CommunityDollarVaultEntry DebitVault(string communityId, double amount, string? reason)
        {
            if (amount <= 0)
                throw new ArgumentOutOfRangeException(nameof(amount), amount,
                    "DebitVault amount must be greater than 0 -- it represents a positive spend.");

            var entryId = Guid.NewGuid().ToString();
            var timestamp = DateTimeOffset.UtcNow;

            using var conn = OpenConnection();
            using var tx = conn.BeginTransaction();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    INSERT INTO CommunityDollarVaultLedger (EntryId, CommunityId, Timestamp, DeltaAmount, Reason)
                    VALUES ($id, $communityId, $ts, $delta, $reason);";
                cmd.Parameters.AddWithValue("$id", entryId);
                cmd.Parameters.AddWithValue("$communityId", communityId);
                cmd.Parameters.AddWithValue("$ts", timestamp.ToString("o"));
                cmd.Parameters.AddWithValue("$delta", -amount);
                cmd.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();

            return new CommunityDollarVaultEntry(entryId, communityId, timestamp, -amount, reason);
        }

        // ------------------------------------------------------------------
        private SqliteConnection OpenConnection()
        {
            var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA foreign_keys = ON;";
                pragma.ExecuteNonQuery();
            }
            return conn;
        }
    }
}
