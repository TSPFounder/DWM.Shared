// TradeRequestRepository.cs
// Read/write access to the TradeRequests table (trade_requests_migration.sql) — trade
// lifecycle (Proposed -> Settled/Cancelled), layered on top of EconomyRepository and
// alongside TradeSettlementService, not merged into either. See this task's PR description
// for the "why a separate repository, not an extension of EconomyRepository" reasoning.
//
// StoneLedger stays append-only: a TradeRequests row only produces a StoneLedger row once
// SettleProposedTrade actually settles it. A Cancelled or still-Proposed request never
// touches StoneLedger.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace DWM.Shared.Economy
{
    public sealed class TradeRequestRepository
    {
        private readonly string _connectionString;
        private readonly EconomyRepository _economyRepository;

        public TradeRequestRepository(string dbPath)
        {
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWrite
            }.ToString();
            _economyRepository = new EconomyRepository(dbPath);

            TradeRequestsMigration.EnsureCreated(dbPath);
        }

        // ------------------------------------------------------------------
        // Reads

        public TradeRequest? GetTradeRequest(string requestId)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT RequestId, FromCommunityId, ToCommunityId, Amount, ResourceId, Quantity, Memo, Status, CreatedAt, ResolvedAt
                FROM TradeRequests
                WHERE RequestId = $id;";
            cmd.Parameters.AddWithValue("$id", requestId);

            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadTradeRequest(reader) : null;
        }

        /// <summary>
        /// All TradeRequests rows, optionally filtered to a single status. Pass null (the
        /// default) for every request regardless of status. Used by the CLI's "list" command
        /// with status=Proposed to show pending trades.
        /// </summary>
        public IReadOnlyList<TradeRequest> GetTradeRequests(TradeRequestStatus? status = null)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = status is null
                ? @"SELECT RequestId, FromCommunityId, ToCommunityId, Amount, ResourceId, Quantity, Memo, Status, CreatedAt, ResolvedAt
                    FROM TradeRequests;"
                : @"SELECT RequestId, FromCommunityId, ToCommunityId, Amount, ResourceId, Quantity, Memo, Status, CreatedAt, ResolvedAt
                    FROM TradeRequests
                    WHERE Status = $status;";
            if (status is not null)
                cmd.Parameters.AddWithValue("$status", status.Value.ToString());

            var results = new List<TradeRequest>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                results.Add(ReadTradeRequest(reader));
            return results;
        }

        // ------------------------------------------------------------------
        // Writes

        /// <summary>
        /// Validates and records a trade request as Proposed. Does not touch StoneLedger.
        /// Uses the same structural validation TradeSettlementService.SettleTrade already
        /// applies (positive amount, no self-trade, real community/resource ids) — a
        /// proposal that fails these checks is rejected the same clean way a direct
        /// SettleTrade call would reject it, rather than recording a request that could
        /// never settle.
        /// </summary>
        public TradeRequestResult ProposeTrade(
            string fromCommunityId,
            string toCommunityId,
            double amount,
            string? resourceId,
            double? quantity,
            string? memo)
        {
            var failure = ValidateStructure(fromCommunityId, toCommunityId, amount, resourceId, quantity);
            if (failure is not null)
                return TradeRequestResult.Rejected(failure.Value.Reason, failure.Value.Message);

            var requestId = Guid.NewGuid().ToString();
            var createdAt = DateTimeOffset.UtcNow;

            using var conn = OpenConnection();
            using var tx = conn.BeginTransaction();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    INSERT INTO TradeRequests
                        (RequestId, FromCommunityId, ToCommunityId, Amount, ResourceId, Quantity, Memo, Status, CreatedAt, ResolvedAt)
                    VALUES
                        ($id, $from, $to, $amount, $resource, $qty, $memo, $status, $createdAt, NULL);";
                cmd.Parameters.AddWithValue("$id", requestId);
                cmd.Parameters.AddWithValue("$from", fromCommunityId);
                cmd.Parameters.AddWithValue("$to", toCommunityId);
                cmd.Parameters.AddWithValue("$amount", amount);
                cmd.Parameters.AddWithValue("$resource", (object?)resourceId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$qty", (object?)quantity ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$memo", (object?)memo ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$status", nameof(TradeRequestStatus.Proposed));
                cmd.Parameters.AddWithValue("$createdAt", createdAt.ToString("o"));
                cmd.ExecuteNonQuery();
            }
            tx.Commit();

            var request = new TradeRequest(requestId, fromCommunityId, toCommunityId, amount,
                resourceId, quantity, memo, TradeRequestStatus.Proposed, createdAt, null);
            return TradeRequestResult.Succeeded(request);
        }

        /// <summary>
        /// Re-validates a Proposed request against CURRENT community/resource state — not
        /// just trusting what was true at propose time, since that state can drift — and, if
        /// still valid, atomically claims it (see TryClaimForSettlement) before committing it
        /// as a single StoneLedger row via EconomyRepository.AppendLedgerEntry, then marking
        /// the request Settled. If validation now fails, the request is left completely
        /// untouched (still Proposed, no StoneLedger row written, no claim attempted). Fails
        /// cleanly (no exception, no double-commit, no resurrecting a cancelled trade) if the
        /// request doesn't exist, isn't currently Proposed, or loses the compare-and-swap
        /// claim to a concurrent caller (or a stuck prior attempt — see Settling in
        /// TradeRequestStatus).
        /// </summary>
        public TradeRequestResult SettleProposedTrade(string requestId)
        {
            var existing = GetTradeRequest(requestId);
            if (existing is null)
                return TradeRequestResult.Rejected(TradeRequestFailureReason.RequestNotFound,
                    $"No trade request with id '{requestId}' exists.");

            // Fast-path rejection on a (possibly stale) read — avoids a wasted structural
            // validation query in the common case. This is NOT what prevents the race the
            // compare-and-swap below guards against; it's purely an optimization.
            if (existing.Status != TradeRequestStatus.Proposed)
                return TradeRequestResult.Rejected(TradeRequestFailureReason.RequestNotProposed,
                    $"Trade request '{requestId}' is already {existing.Status}, not Proposed — it cannot be settled again.");

            var failure = ValidateStructure(existing.FromCommunityId, existing.ToCommunityId,
                existing.Amount, existing.ResourceId, existing.Quantity);
            if (failure is not null)
                return TradeRequestResult.Rejected(failure.Value.Reason, failure.Value.Message);

            // Compare-and-swap: atomically claim this request for settlement by flipping it
            // to the transient Settling status ONLY IF it is still Proposed right now. This
            // is the actual race guard, not the status check above. It closes the gap where a
            // crash between the ledger write and the Settled update would otherwise leave the
            // request looking Proposed, letting a naive retry pass the check above and
            // double-append to StoneLedger. If this doesn't affect exactly one row, something
            // else already claimed (or resolved) this request since the read above — reject
            // cleanly rather than proceeding to the ledger write.
            if (!TryClaimForSettlement(requestId))
            {
                var current = GetTradeRequest(requestId);
                return TradeRequestResult.Rejected(TradeRequestFailureReason.RequestNotProposed,
                    $"Trade request '{requestId}' could not be claimed for settlement — it is currently " +
                    $"{(current?.Status.ToString() ?? "unknown")}, not Proposed. Another operation may " +
                    "already be settling it, or it was already resolved.");
            }

            // Ledger write first, Settled update second: if the process dies here, the
            // request is stuck at Settling (not Proposed), so a retry is correctly rejected
            // by the compare-and-swap above instead of silently re-appending to StoneLedger.
            var ledgerEntry = _economyRepository.AppendLedgerEntry(
                existing.FromCommunityId, existing.ToCommunityId, existing.Amount,
                existing.ResourceId, existing.Quantity, existing.Memo);

            var resolvedAt = DateTimeOffset.UtcNow;
            UpdateStatus(requestId, TradeRequestStatus.Settled, resolvedAt);

            var updated = existing with { Status = TradeRequestStatus.Settled, ResolvedAt = resolvedAt };
            return TradeRequestResult.Succeeded(updated, ledgerEntry);
        }

        /// <summary>
        /// Atomically flips a request from Proposed to the transient Settling status via
        /// UPDATE ... WHERE Status = 'Proposed', and reports whether this call won that
        /// claim. SQLite serializes concurrent writers to the same database file, so this
        /// UPDATE is a safe compare-and-swap even across separate connections/processes:
        /// exactly one concurrent caller can ever see rowsAffected == 1 for a given
        /// RequestId.
        /// </summary>
        private bool TryClaimForSettlement(string requestId)
        {
            using var conn = OpenConnection();
            using var tx = conn.BeginTransaction();
            int rowsAffected;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    UPDATE TradeRequests
                    SET Status = $settling
                    WHERE RequestId = $id AND Status = $proposed;";
                cmd.Parameters.AddWithValue("$settling", nameof(TradeRequestStatus.Settling));
                cmd.Parameters.AddWithValue("$id", requestId);
                cmd.Parameters.AddWithValue("$proposed", nameof(TradeRequestStatus.Proposed));
                rowsAffected = cmd.ExecuteNonQuery();
            }
            tx.Commit();
            return rowsAffected == 1;
        }

        /// <summary>
        /// Marks a Proposed request Cancelled. Never touches StoneLedger. Fails cleanly if
        /// the request doesn't exist or isn't currently Proposed (mirrors
        /// SettleProposedTrade's not-found/wrong-status handling, so an already-settled or
        /// already-cancelled request can't be cancelled out from under a settlement either).
        /// </summary>
        public TradeRequestResult CancelProposedTrade(string requestId)
        {
            var existing = GetTradeRequest(requestId);
            if (existing is null)
                return TradeRequestResult.Rejected(TradeRequestFailureReason.RequestNotFound,
                    $"No trade request with id '{requestId}' exists.");

            if (existing.Status != TradeRequestStatus.Proposed)
                return TradeRequestResult.Rejected(TradeRequestFailureReason.RequestNotProposed,
                    $"Trade request '{requestId}' is already {existing.Status}, not Proposed — it cannot be cancelled.");

            var resolvedAt = DateTimeOffset.UtcNow;
            UpdateStatus(requestId, TradeRequestStatus.Cancelled, resolvedAt);

            var updated = existing with { Status = TradeRequestStatus.Cancelled, ResolvedAt = resolvedAt };
            return TradeRequestResult.Succeeded(updated);
        }

        // ------------------------------------------------------------------
        private (TradeRequestFailureReason Reason, string Message)? ValidateStructure(
            string fromCommunityId, string toCommunityId, double amount,
            string? resourceId, double? quantity)
        {
            if (amount <= 0)
                return (TradeRequestFailureReason.NonPositiveAmount,
                    $"Amount must be greater than 0 (was {amount}).");

            if (fromCommunityId == toCommunityId)
                return (TradeRequestFailureReason.SelfTrade,
                    $"A community cannot trade with itself ('{fromCommunityId}').");

            var communities = _economyRepository.GetCommunities();

            if (!communities.Any(c => c.CommunityId == fromCommunityId))
                return (TradeRequestFailureReason.UnknownFromCommunity,
                    $"No community with id '{fromCommunityId}' exists.");

            if (!communities.Any(c => c.CommunityId == toCommunityId))
                return (TradeRequestFailureReason.UnknownToCommunity,
                    $"No community with id '{toCommunityId}' exists.");

            if (resourceId is not null)
            {
                if (!_economyRepository.GetResources().Any(r => r.ResourceId == resourceId))
                    return (TradeRequestFailureReason.UnknownResource,
                        $"No resource with id '{resourceId}' exists.");

                if (quantity is not null && quantity <= 0)
                    return (TradeRequestFailureReason.NonPositiveQuantity,
                        $"Quantity must be greater than 0 when provided (was {quantity}).");
            }

            return null;
        }

        private void UpdateStatus(string requestId, TradeRequestStatus status, DateTimeOffset resolvedAt)
        {
            using var conn = OpenConnection();
            using var tx = conn.BeginTransaction();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    UPDATE TradeRequests
                    SET Status = $status, ResolvedAt = $resolvedAt
                    WHERE RequestId = $id;";
                cmd.Parameters.AddWithValue("$status", status.ToString());
                cmd.Parameters.AddWithValue("$resolvedAt", resolvedAt.ToString("o"));
                cmd.Parameters.AddWithValue("$id", requestId);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }

        private static TradeRequest ReadTradeRequest(SqliteDataReader reader) => new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetDouble(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetDouble(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            ParseStatus(reader.GetString(7)),
            ParseTimestamp(reader.GetString(8)),
            reader.IsDBNull(9) ? null : ParseTimestamp(reader.GetString(9)));

        private static TradeRequestStatus ParseStatus(string status) => status switch
        {
            "Proposed" => TradeRequestStatus.Proposed,
            "Settling" => TradeRequestStatus.Settling,
            "Settled" => TradeRequestStatus.Settled,
            "Cancelled" => TradeRequestStatus.Cancelled,
            _ => throw new InvalidOperationException($"Unknown TradeRequests.Status value: '{status}'.")
        };

        private static DateTimeOffset ParseTimestamp(string timestamp) =>
            DateTimeOffset.Parse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

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
