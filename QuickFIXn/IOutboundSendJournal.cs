namespace QuickFix;

// FP Enhancement: 2026-09-01 — neutral durable pre-send journal boundary for outbound FIX frames.
public readonly record struct OutboundSendJournalToken(string Value);

/// <summary>
/// What became of an outbound frame after its durable intent was recorded.
/// </summary>
// FP Enhancement: 2026-09-14 — a send that THREW is neither transmitted nor not-transmitted,
// and the original boolean could say only one of those two things. It therefore said nothing:
// the prepared row stayed unresolved, and a journal whose publishable prefix stops at the first
// unfinalised ordinal stalled that session's stream until an external recovery pass noticed.
public enum OutboundSendDisposition
{
    /// <summary>The frame definitely did not reach the transport.</summary>
    NotTransmitted = 0,

    /// <summary>The transport accepted the frame.</summary>
    Transmitted = 1,

    /// <summary>
    /// The send threw. Whether bytes reached the counterparty is unknowable from here, so this
    /// is the honest answer and must not be collapsed into either definite value. It is an
    /// audit outcome requiring reconciliation — never an instruction to resend, which is how
    /// an uncertain send becomes a duplicate order at the venue.
    /// </summary>
    Unknown = 2,
}

/// <summary>
/// Records the durable intent and resulting disposition of an outbound FIX frame.
/// </summary>
public interface IOutboundSendJournal
{
    OutboundSendJournalToken Prepare(SessionID sessionId, string rawFrame);

    void RecordOutcome(OutboundSendJournalToken token, bool transmitted);

    /// <summary>
    /// Records the disposition of a frame. Override this to receive
    /// <see cref="OutboundSendDisposition.Unknown"/>; the default forwards only the two
    /// definite states to <see cref="RecordOutcome(OutboundSendJournalToken, bool)"/>.
    /// </summary>
    // A DEFAULT, not an abstract member: every existing implementer keeps compiling. The
    // default deliberately DROPS Unknown rather than mapping it to `false`. An implementer
    // written against the boolean contract cannot represent uncertainty, and reporting `false`
    // would assert the frame never reached the venue — the one claim that lets a downstream
    // consumer resend it. Dropping preserves the pre-existing unresolved row, which that
    // implementer's own recovery path already handles.
    void RecordOutcome(OutboundSendJournalToken token, OutboundSendDisposition disposition)
    {
        if (disposition == OutboundSendDisposition.Unknown)
            return;

        RecordOutcome(token, disposition == OutboundSendDisposition.Transmitted);
    }
}
