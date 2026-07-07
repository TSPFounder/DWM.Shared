// CommunityResource.cs
// Plain domain type mirroring a CommunityResources row (economy_schema.sql). Not tied to SQLite.

namespace DWM.Shared.Economy
{
    public enum CommunityResourceRole
    {
        Produces,
        Needs
    }

    public sealed record CommunityResource(
        string CommunityId,
        string ResourceId,
        CommunityResourceRole Role,
        double Quantity);
}
