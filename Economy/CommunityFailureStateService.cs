// CommunityFailureStateService.cs
// Day 11: exposes the failure_state trigger UE-side code will read.
//
// SCOPE DECISION on "unmet dependency" (flagging per this task's explicit instruction, not
// silently resolving it): the original schedule describes the failure trigger as "vault<=0
// WITH unmet dependency" -- a compound condition. There is no live inventory/stock tracking
// anywhere in this codebase -- CommunityResource.Quantity (economy_schema.sql) is a static
// SEED value (a starting-stock or need-target number set once by EconomySeeder), not something
// that updates as trades happen. Literally computing "does this community currently lack a
// needed resource" would require building real inventory tracking, which is out of scope here.
//
// A lightweight proxy was considered -- e.g. checking whether a community's Needs-role
// CommunityResource rows have ever been the target of a settled StoneLedger-linked trade --
// but this was NOT built: it would require joining CommunityResource(Role='Needs') against
// StoneLedger/TradeRequests by ResourceId, and a community that simply hasn't needed to trade
// recently (network is healthy) would read identically to one that's been unable to get a
// resource it truly lacks -- the proxy doesn't actually distinguish the two, so it adds real
// query complexity for a signal that isn't reliable. Not worth it for an MVP trigger.
//
// DECISION: "unmet dependency" is treated as narrative framing, not a separate technical
// condition. The failure trigger below IS Day 10's IsInCascadingFailure check (vault balance
// <= threshold), full stop. Real per-resource scarcity tracking is a good post-MVP depth
// feature, not a Day 11 requirement.
//
// Composes DollarVaultRepository (Day 10) rather than editing it, same layering reasoning
// Day 8/10 already used: DollarVaultRepository is proven behavior, and this is a distinct
// concern (classifying/exposing a state) layered on top of it, not a change to vault
// read/write logic itself.

namespace DWM.Shared.Economy
{
    public sealed class CommunityFailureStateService
    {
        private readonly DollarVaultRepository _vault;

        public CommunityFailureStateService(string dbPath)
        {
            _vault = new DollarVaultRepository(dbPath);
        }

        /// <summary>
        /// The failure_state trigger: Healthy if this community's vault balance is above its
        /// configured CascadingFailureThreshold, CascadingFailure if at or below it (matches
        /// Day 10's IsInCascadingFailure &lt;= semantics exactly -- see the file-level comment
        /// for why this is the whole trigger, not one half of a compound condition).
        /// </summary>
        public CommunityFailureStatus GetFailureState(string communityId)
        {
            var balance = _vault.GetVaultBalance(communityId);
            var threshold = _vault.GetVaultThreshold(communityId);
            var state = balance <= threshold
                ? CommunityFailureState.CascadingFailure
                : CommunityFailureState.Healthy;

            return new CommunityFailureStatus(communityId, state, balance, threshold);
        }
    }
}
