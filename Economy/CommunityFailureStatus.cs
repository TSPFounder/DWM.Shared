// CommunityFailureStatus.cs
// Plain result type returned by CommunityFailureStateService.GetFailureState -- carries the
// State enum plus the raw balance/threshold it was computed from, so UE-side code (and tests)
// can read the number behind the classification without a second round-trip.

namespace DWM.Shared.Economy
{
    public sealed record CommunityFailureStatus(
        string CommunityId,
        CommunityFailureState State,
        double VaultBalance,
        double VaultThreshold);
}
