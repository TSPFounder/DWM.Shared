// GoldenEconomyScenario.cs
// Day 13: the canonical MVP demo starting scenario -- calibration, not new schema. Seeds a
// fresh economy.db via the existing, unmodified EconomySeeder (base 5 communities / 10
// resources / 24 CommunityResources / StoneLedger empty), then layers calibrated Dollar
// Vault starting balances on top via DollarVaultRepository.DebitVault (Day 10's existing
// write path -- no new repository methods added).
//
// StoneLedger DECISION: stays EMPTY. This is the original EconomySeeder design ("the Day-27
// demo trade is the first real transaction, which is the point -- it should be visibly 'the
// first'") and this task's own instruction was to keep it that way absent a strong reason to
// reverse it. No strong reason surfaced, so it isn't reversed here.
//
// CITY IS DELIBERATELY LEFT UNTOUCHED at its Day 10 seeded $5000/$500. Task instruction was
// to reuse Day 11's CityCascadingFailureScenario "as your starting point, don't recalculate
// from scratch" -- that script (and its whole test suite) assumes City starts at the Day 10
// seed value and reaches exactly $400/CascadingFailure after 3 debits totaling $4600. Pre-
// debiting City here would silently change that already-tested arithmetic. So the golden
// scenario's starting state keeps City Healthy at the pristine seed balance, and City's
// descent into Cascading Failure is the LIVE, in-demo action (run CityCascadingFailureScenario
// against a copy of the golden .db during the take), not something baked into the snapshot.
//
// THRESHOLDS ARE NOT VARIED PER COMMUNITY: DollarVaultRepository (Day 10) exposes no write
// path for CascadingFailureThreshold, only balance (via DebitVault). Adding one would mean
// editing a Day 10 file, which this task's guardrail forbids. So every community keeps the
// Day 10 seeded $500 threshold; calibration is expressed entirely through each community's
// starting BALANCE (its distance above that shared threshold), which is sufficient to make
// the communities feel distinct without touching guarded files.
//
// The four non-City debits below are invented, reasonable outside-purchase descriptions (not
// new resource/lore entities) -- this closes Day 10's deferred "sample memo strings" gap using
// the generic string-reason mechanism DebitVault already has, exactly as this task asked.

using System.Collections.Generic;

namespace DWM.Shared.Economy
{
    public static class GoldenEconomyScenario
    {
        /// <summary>
        /// Calibrated starting-balance debits applied on top of the Day 10 seed ($5000 each).
        /// City is intentionally absent -- see the file-level comment.
        /// </summary>
        public static readonly IReadOnlyList<(string CommunityId, double Amount, string Reason)> CalibrationDebits = new[]
        {
            ("mountain", 800.0, "Specialized cold-weather turbine bearing import"),
            ("hillside", 600.0, "Orchard drip-irrigation parts, sourced outside the network"),
            ("valley",   400.0, "Emergency well-pump motor replacement"),
            ("suburb",  1000.0, "Contracted structural inspection services"),
        };

        /// <summary>
        /// Resulting starting balances after calibration, for reference/tests
        /// (5000 - the debit above; City is the untouched 5000 seed value).
        /// </summary>
        public static readonly IReadOnlyDictionary<string, double> StartingBalances = new Dictionary<string, double>
        {
            ["mountain"] = 4200.0,
            ["hillside"] = 4400.0,
            ["valley"]   = 4600.0,
            ["suburb"]   = 4000.0,
            ["city"]     = 5000.0,
        };

        /// <summary>
        /// Seeds a fresh economy.db at <paramref name="economyDbPath"/> with the canonical
        /// golden demo scenario: the unmodified Day 5 base seed (via EconomySeeder), plus the
        /// calibration debits above. Delete-and-recreate, same as EconomySeeder.WriteEconomy
        /// and WorldPackageExporter -- safe to call repeatedly to regenerate the scenario.
        /// </summary>
        public static void Seed(string economyDbPath)
        {
            new EconomySeeder().WriteEconomy(economyDbPath);

            var vault = new DollarVaultRepository(economyDbPath);
            foreach (var (communityId, amount, reason) in CalibrationDebits)
            {
                vault.DebitVault(communityId, amount, reason);
            }
        }
    }
}
