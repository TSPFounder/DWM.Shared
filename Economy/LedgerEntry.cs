// LedgerEntry.cs
// Plain domain type mirroring a StoneLedger row (economy_schema.sql). Not tied to SQLite.
// StoneLedger is append-only — there is deliberately no mutable state or Id setter here
// beyond what the row already recorded; see EconomyRepository.AppendLedgerEntry.

using System;

namespace DWM.Shared.Economy
{
    public sealed record LedgerEntry(
        string TransactionId,
        DateTimeOffset Timestamp,
        string FromCommunityId,
        string ToCommunityId,
        double Amount,
        string? ResourceId,
        double? Quantity,
        string? Memo);
}
