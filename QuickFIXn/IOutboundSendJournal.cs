namespace QuickFix;

// FP Enhancement: 2026-09-01 — neutral durable pre-send journal boundary for outbound FIX frames.
public readonly record struct OutboundSendJournalToken(string Value);

/// <summary>
/// Records the durable intent and resulting disposition of an outbound FIX frame.
/// </summary>
public interface IOutboundSendJournal
{
    OutboundSendJournalToken Prepare(SessionID sessionId, string rawFrame);
    void RecordOutcome(OutboundSendJournalToken token, bool transmitted);
}
