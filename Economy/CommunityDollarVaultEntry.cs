// CommunityDollarVaultEntry.cs
// Plain domain type mirroring a CommunityDollarVaultLedger row
// (dollar_vault_percommunity_migration.sql). Not tied to SQLite.

using System;

namespace DWM.Shared.Economy
{
    public sealed record CommunityDollarVaultEntry(
        string EntryId,
        string CommunityId,
        DateTimeOffset Timestamp,
        double DeltaAmount,
        string? Reason);
}
