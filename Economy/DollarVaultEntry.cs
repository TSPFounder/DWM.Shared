// DollarVaultEntry.cs
// Plain domain type mirroring a DollarVaultLedger row (economy_schema.sql). Not tied to SQLite.

using System;

namespace DWM.Shared.Economy
{
    public sealed record DollarVaultEntry(
        string EntryId,
        DateTimeOffset Timestamp,
        double DeltaAmount,
        string? Reason);
}
