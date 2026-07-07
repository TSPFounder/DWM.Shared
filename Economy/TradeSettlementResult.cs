// TradeSettlementResult.cs
// Structured outcome of TradeSettlementService.SettleTrade — a rejected trade is a normal,
// expected return value (a bad UI input), not an exception. Only genuinely exceptional
// failures (e.g. the database being unreachable) should throw.

namespace DWM.Shared.Economy
{
    public enum TradeRejectionReason
    {
        NonPositiveAmount,
        SelfTrade,
        UnknownFromCommunity,
        UnknownToCommunity,
        UnknownResource,
        NonPositiveQuantity
    }

    public sealed class TradeSettlementResult
    {
        public bool Success { get; }
        public LedgerEntry? LedgerEntry { get; }
        public TradeRejectionReason? RejectionReason { get; }
        public string? Message { get; }

        private TradeSettlementResult(bool success, LedgerEntry? ledgerEntry,
            TradeRejectionReason? rejectionReason, string? message)
        {
            Success = success;
            LedgerEntry = ledgerEntry;
            RejectionReason = rejectionReason;
            Message = message;
        }

        public static TradeSettlementResult Succeeded(LedgerEntry ledgerEntry) =>
            new(true, ledgerEntry, null, null);

        public static TradeSettlementResult Rejected(TradeRejectionReason reason, string message) =>
            new(false, null, reason, message);
    }
}
