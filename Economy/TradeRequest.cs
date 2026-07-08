// TradeRequest.cs
// Plain domain type mirroring a TradeRequests row (trade_requests_migration.sql). Not tied
// to SQLite. Unlike LedgerEntry/StoneLedger, TradeRequests is NOT append-only by design —
// Status/ResolvedAt are meant to change as a request moves Proposed -> Settling -> Settled,
// or Proposed -> Cancelled.

using System;

namespace DWM.Shared.Economy
{
    public enum TradeRequestStatus
    {
        Proposed,

        // Transient: set only by SettleProposedTrade's compare-and-swap claim, immediately
        // before the StoneLedger write. Never a default/initial value, never set directly by
        // ProposeTrade. See trade_requests_migration.sql for why this status exists.
        Settling,

        Settled,
        Cancelled
    }

    public sealed record TradeRequest(
        string RequestId,
        string FromCommunityId,
        string ToCommunityId,
        double Amount,
        string? ResourceId,
        double? Quantity,
        string? Memo,
        TradeRequestStatus Status,
        DateTimeOffset CreatedAt,
        DateTimeOffset? ResolvedAt);
}
