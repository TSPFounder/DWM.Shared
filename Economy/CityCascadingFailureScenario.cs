// CityCascadingFailureScenario.cs
// Day 11 Task 2: a deterministic, repeatable demo/test fixture that drives City's Dollar
// Vault into Cascading Failure via a scripted sequence of outside-purchase debits (not one
// lump sum -- reads like a real sequence of events for demo purposes). City starts at the
// Day 10 seeded $5000 balance with a $500 threshold; this sequence totals $4600, leaving
// $400 <= $500, which is Cascading Failure under CommunityFailureStateService's trigger.
//
// Placeholder purchase descriptions -- same "outside purchase" framing DollarVaultRepository
// already uses, not new invented resource/lore names (see that file's OUTSIDE-RESOURCES note).

using System.Collections.Generic;

namespace DWM.Shared.Economy
{
    public static class CityCascadingFailureScenario
    {
        private const string CommunityId = "city";

        public static readonly IReadOnlyList<(double Amount, string Reason)> Debits = new[]
        {
            (2000.0, "Outside purchase: bulk lumber order"),
            (1600.0, "Outside purchase: contracted skilled labor from outside the network"),
            (1000.0, "Outside purchase: emergency water infrastructure repair"),
        };

        /// <summary>
        /// Runs the scripted debit sequence against City's vault and returns its resulting
        /// failure state. Deterministic and repeatable: run against a fresh database, it
        /// produces the same final state (CascadingFailure, balance 400) every time.
        /// </summary>
        public static CommunityFailureStatus Run(string dbPath)
        {
            var vault = new DollarVaultRepository(dbPath);
            foreach (var (amount, reason) in Debits)
            {
                vault.DebitVault(CommunityId, amount, reason);
            }

            return new CommunityFailureStateService(dbPath).GetFailureState(CommunityId);
        }
    }
}
