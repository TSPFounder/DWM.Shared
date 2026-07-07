// Resource.cs
// Plain domain type mirroring a Resources row (economy_schema.sql). Not tied to SQLite.

namespace DWM.Shared.Economy
{
    public sealed record Resource(
        string ResourceId,
        string Name,
        string Unit,
        string? Category);
}
