// TradeSettlementService.cs
// Validates a proposed trade, then commits it as a single StoneLedger row via
// EconomyRepository.AppendLedgerEntry. One validate-then-commit call (SettleTrade) — not a
// separate propose/pending/commit workflow; that's more state than the MVP trade panel
// (fill in seller/resource/quantity, press Confirm) needs.
//
// DOMAIN RULE: this is a LETS mutual-credit system (ECONOMY_SCHEMA_SPEC.md / the Primer).
// Stones are minted at the moment of exchange, so a community's ledger balance going
// negative is normal and expected — that IS mutual credit; it is not an error condition.
// This service deliberately does NOT check resulting balances and never will for that
// reason. The only checks here are structural sanity (positive amount, no self-trade, real
// community/resource ids) — the same things economy_schema.sql's own CHECK/FOREIGN KEY
// constraints enforce. This service pre-checks them so a bad UI input fails with a clear,
// typed reason instead of a raw SqliteException reaching player-facing code.

using System;
using System.Linq;

namespace DWM.Shared.Economy
{
    public sealed class TradeSettlementService
    {
        private readonly EconomyRepository _repository;

        public TradeSettlementService(EconomyRepository repository)
        {
            _repository = repository;
        }

        public TradeSettlementResult SettleTrade(
            string fromCommunityId,
            string toCommunityId,
            double amount,
            string? resourceId,
            double? quantity,
            string? memo)
        {
            if (amount <= 0)
                return TradeSettlementResult.Rejected(TradeRejectionReason.NonPositiveAmount,
                    $"Amount must be greater than 0 (was {amount}).");

            if (fromCommunityId == toCommunityId)
                return TradeSettlementResult.Rejected(TradeRejectionReason.SelfTrade,
                    $"A community cannot trade with itself ('{fromCommunityId}').");

            var communities = _repository.GetCommunities();

            if (!communities.Any(c => c.CommunityId == fromCommunityId))
                return TradeSettlementResult.Rejected(TradeRejectionReason.UnknownFromCommunity,
                    $"No community with id '{fromCommunityId}' exists.");

            if (!communities.Any(c => c.CommunityId == toCommunityId))
                return TradeSettlementResult.Rejected(TradeRejectionReason.UnknownToCommunity,
                    $"No community with id '{toCommunityId}' exists.");

            if (resourceId is not null)
            {
                var resourceExists = _repository.GetResources().Any(r => r.ResourceId == resourceId);
                if (!resourceExists)
                    return TradeSettlementResult.Rejected(TradeRejectionReason.UnknownResource,
                        $"No resource with id '{resourceId}' exists.");

                if (quantity is not null && quantity <= 0)
                    return TradeSettlementResult.Rejected(TradeRejectionReason.NonPositiveQuantity,
                        $"Quantity must be greater than 0 when provided (was {quantity}).");
            }

            // AppendLedgerEntry already wraps its INSERT in its own transaction — no
            // additional transaction wrapping here would just be a no-op nested scope.
            var ledgerEntry = _repository.AppendLedgerEntry(
                fromCommunityId, toCommunityId, amount, resourceId, quantity, memo);

            return TradeSettlementResult.Succeeded(ledgerEntry);
        }
    }
}
