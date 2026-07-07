// EconomyRepository.cs
// Read/write access to an economy.db seeded by EconomySeeder (economy_schema.sql). Wraps
// Microsoft.Data.Sqlite using the same connection/transaction pattern as EconomySeeder.cs
// for consistency within this folder.
//
// StoneLedger is APPEND-ONLY (see economy_schema.sql): AppendLedgerEntry is the only write
// path this repository exposes for it. There is deliberately no Update/Delete — corrections
// are new offsetting rows, written by calling AppendLedgerEntry again. This repository does
// not validate Amount/From/To itself; the schema's own CHECK constraints are the source of
// truth for those invariants (data layer, not business logic — see Day 6 for settlement).
//
// Requires NuGet package: Microsoft.Data.Sqlite (already referenced by DWM.Shared.csproj).

using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace DWM.Shared.Economy
{
    public sealed class EconomyRepository
    {
        private readonly string _connectionString;

        public EconomyRepository(string dbPath)
        {
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWrite
            }.ToString();
        }

        // ------------------------------------------------------------------
        // Reads
        // ------------------------------------------------------------------

        public IReadOnlyList<Community> GetCommunities()
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT CommunityId, Name, BiomeType, Description FROM Communities;";

            var results = new List<Community>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new Community(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
            return results;
        }

        public IReadOnlyList<Resource> GetResources()
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT ResourceId, Name, Unit, Category FROM Resources;";

            var results = new List<Resource>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new Resource(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
            return results;
        }

        public IReadOnlyList<CommunityResource> GetCommunityResources(string communityId)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT CommunityId, ResourceId, Role, Quantity
                FROM CommunityResources
                WHERE CommunityId = $communityId;";
            cmd.Parameters.AddWithValue("$communityId", communityId);

            var results = new List<CommunityResource>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new CommunityResource(
                    reader.GetString(0),
                    reader.GetString(1),
                    ParseRole(reader.GetString(2)),
                    reader.GetDouble(3)));
            }
            return results;
        }

        /// <summary>
        /// All StoneLedger rows, optionally filtered to those where <paramref name="communityId"/>
        /// is either the sender or the receiver. Pass null (the default) for the whole ledger.
        /// </summary>
        public IReadOnlyList<LedgerEntry> GetLedgerEntries(string? communityId = null)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = communityId is null
                ? @"SELECT TransactionId, Timestamp, FromCommunityId, ToCommunityId, Amount, ResourceId, Quantity, Memo
                    FROM StoneLedger;"
                : @"SELECT TransactionId, Timestamp, FromCommunityId, ToCommunityId, Amount, ResourceId, Quantity, Memo
                    FROM StoneLedger
                    WHERE FromCommunityId = $communityId OR ToCommunityId = $communityId;";
            if (communityId is not null)
                cmd.Parameters.AddWithValue("$communityId", communityId);

            var results = new List<LedgerEntry>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new LedgerEntry(
                    reader.GetString(0),
                    ParseTimestamp(reader.GetString(1)),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetDouble(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetDouble(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7)));
            }
            return results;
        }

        /// <summary>
        /// Sum of DollarVaultLedger.DeltaAmount — the current Dollar Vault balance. The
        /// COALESCE happens in SQL, so an empty ledger returns 0.0 rather than throwing or
        /// returning null: a genuinely empty vault ledger and a vault whose entries happen to
        /// net to zero are indistinguishable by design, per ECONOMY_SCHEMA_SPEC.md's SUM
        /// approach to deriving the balance.
        /// </summary>
        public double GetDollarVaultBalance()
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(SUM(DeltaAmount), 0) FROM DollarVaultLedger;";
            var result = cmd.ExecuteScalar();
            return Convert.ToDouble(result, CultureInfo.InvariantCulture);
        }

        // ------------------------------------------------------------------
        // Writes
        // ------------------------------------------------------------------

        /// <summary>
        /// Appends a new StoneLedger row and returns it as written (generated
        /// TransactionId and Timestamp included). INSERT ONLY — StoneLedger is append-only
        /// by design, so there is no corresponding Update or Delete method. A correction is a
        /// new call to this method with fromCommunityId/toCommunityId swapped.
        /// </summary>
        public LedgerEntry AppendLedgerEntry(
            string fromCommunityId,
            string toCommunityId,
            double amount,
            string? resourceId,
            double? quantity,
            string? memo)
        {
            var transactionId = Guid.NewGuid().ToString();
            var timestamp = DateTimeOffset.UtcNow;

            using var conn = OpenConnection();
            using var tx = conn.BeginTransaction();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    INSERT INTO StoneLedger
                        (TransactionId, Timestamp, FromCommunityId, ToCommunityId, Amount, ResourceId, Quantity, Memo)
                    VALUES
                        ($id, $ts, $from, $to, $amount, $resource, $qty, $memo);";
                cmd.Parameters.AddWithValue("$id", transactionId);
                cmd.Parameters.AddWithValue("$ts", timestamp.ToString("o"));
                cmd.Parameters.AddWithValue("$from", fromCommunityId);
                cmd.Parameters.AddWithValue("$to", toCommunityId);
                cmd.Parameters.AddWithValue("$amount", amount);
                cmd.Parameters.AddWithValue("$resource", (object?)resourceId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$qty", (object?)quantity ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$memo", (object?)memo ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();

            return new LedgerEntry(transactionId, timestamp, fromCommunityId, toCommunityId,
                amount, resourceId, quantity, memo);
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

        private static CommunityResourceRole ParseRole(string role) => role switch
        {
            "Produces" => CommunityResourceRole.Produces,
            "Needs" => CommunityResourceRole.Needs,
            _ => throw new InvalidOperationException($"Unknown CommunityResources.Role value: '{role}'.")
        };

        private static DateTimeOffset ParseTimestamp(string timestamp) =>
            DateTimeOffset.Parse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }
}
