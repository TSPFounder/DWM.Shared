// TradeRequest.cs
// Plain domain type mirroring a TradeRequests row (trade_requests_migration.sql). Not tied
// to SQLite. Unlike LedgerEntry/StoneLedger, TradeRequests is NOT append-only by design —
// Status/ResolvedAt are meant to change as a request moves Proposed -> Settled/Cancelled.

using System;

namespace DWM.Shared.Economy
{
    public enum TradeRequestStatus
    {
        Proposed,
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
