// TradeRequestResult.cs
// Structured outcome of TradeRequestRepository's Propose/Settle/Cancel operations — a
// rejected/failed operation is a normal return value, not an exception.
//
// TradeRequestFailureReason intentionally duplicates the six structural-validation reason
// names already defined by TradeRejectionReason (TradeSettlementResult.cs) rather than
// reusing that type directly, plus two lifecycle-only reasons. This keeps TradeSettlementService
// and TradeSettlementResult.cs completely untouched, per this task's guardrail — the
// existing atomic SettleTrade path is not to be modified or coupled to this new lifecycle
// path.

namespace DWM.Shared.Economy
{
    public enum TradeRequestFailureReason
    {
        // Structural — same semantics as TradeRejectionReason's equivalents.
        NonPositiveAmount,
        SelfTrade,
        UnknownFromCommunity,
        UnknownToCommunity,
        UnknownResource,
        NonPositiveQuantity,

        // Lifecycle-only.
        RequestNotFound,
        RequestNotProposed
    }

    public sealed class TradeRequestResult
    {
        public bool Success { get; }
        public TradeRequest? Request { get; }
        public LedgerEntry? LedgerEntry { get; }
        public TradeRequestFailureReason? FailureReason { get; }
        public string? Message { get; }

        private TradeRequestResult(bool success, TradeRequest? request, LedgerEntry? ledgerEntry,
            TradeRequestFailureReason? failureReason, string? message)
        {
            Success = success;
            Request = request;
            LedgerEntry = ledgerEntry;
            FailureReason = failureReason;
            Message = message;
        }

        public static TradeRequestResult Succeeded(TradeRequest request, LedgerEntry? ledgerEntry = null) =>
            new(true, request, ledgerEntry, null, null);

        public static TradeRequestResult Rejected(TradeRequestFailureReason reason, string message) =>
            new(false, null, null, reason, message);
    }
}
