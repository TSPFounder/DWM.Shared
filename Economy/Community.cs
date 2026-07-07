// Community.cs
// Plain domain type mirroring a Communities row (economy_schema.sql). Not tied to SQLite.

namespace DWM.Shared.Economy
{
    public sealed record Community(
        string CommunityId,
        string Name,
        string BiomeType,
        string? Description);
}
