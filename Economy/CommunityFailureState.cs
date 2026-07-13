// CommunityFailureState.cs
// The two states a community's Dollar Vault can be in, per Day 10's IsInCascadingFailure
// check (vault balance <= configured threshold). See CommunityFailureStateService.

namespace DWM.Shared.Economy
{
    public enum CommunityFailureState
    {
        Healthy,
        CascadingFailure
    }
}
