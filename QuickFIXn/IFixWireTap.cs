namespace QuickFix;

/// <summary>
/// FP enhancement: a verbatim wire-frame tap for the engine Tier-2 capture seam. A
/// <see cref="Session"/> invokes this (when one is supplied) at the two wire chokepoints:
/// <list type="bullet">
/// <item><see cref="OnInbound"/> at the top of <c>Session.Next(string)</c> — once per wire
/// arrival, before the frame is parsed and before any field redaction, so the raw is complete
/// even for malformed frames and for sessions that redact fields in their logs.</item>
/// <item><see cref="OnOutbound"/> at the point of transmission in <c>Session.Send(string)</c> —
/// the single outbound chokepoint every admin / app / resend / gap-fill frame funnels through,
/// with the transmitted disposition (M1: whether the frame actually reached the wire).</item>
/// </list>
/// Two additional hooks support the §12.6 slot-spill CaptureId-bridge mechanism, allowing the
/// engine adapter to preserve a frame's CaptureId across a gap-fill queue drain without
/// re-tapping (which would create a second capture row for the same FIX frame):
/// <list type="bullet">
/// <item><see cref="OnInboundQueued"/> fires when a too-high frame is stored in the inbound
/// queue so the adapter can spill the correlator's pending slot into its own keyed store.</item>
/// <item><see cref="OnInboundReplayPrepare"/> fires in <c>NextQueued</c> immediately before the
/// replayed frame is passed to <c>NextMessage</c> (and for the LOGON/RESEND skip branch too) so
/// the adapter can restore the original CaptureId into the correlator slot for <c>FromApp</c>
/// to take — or discard it in the skip-branch case.</item>
/// </list>
/// <see cref="OnInboundQueueCleared"/> fires when the session resets its inbound queue (on
/// disconnect) so the adapter can purge any orphaned spilled entries.
/// <para>
/// The implementation is called on the FIX session thread and <b>must never block</b>; the
/// session additionally guards every call so a throwing tap can never disrupt FIX processing.
/// The tap is purely observational — it never mutates the frame or the session.
/// </para>
/// </summary>
public interface IFixWireTap
{
    /// <summary>Called once per inbound wire arrival with the verbatim, pre-parse frame.</summary>
    /// <param name="sessionId">The session the frame arrived on.</param>
    /// <param name="rawFrame">The exact wire bytes, SOH-delimited, before parsing or redaction.</param>
    void OnInbound(SessionID sessionId, string rawFrame);

    /// <summary>Called when a too-high inbound frame is stored in the session's inbound queue,
    /// immediately after the queue insert, with the frame's <c>MsgSeqNum</c>. Allows the engine
    /// adapter to spill the correlator's pending CaptureId slot into its own keyed store so the
    /// id survives the gap-fill drain without re-tapping the frame on replay (§12.6).</summary>
    /// <param name="sessionId">The session the frame arrived on.</param>
    /// <param name="seqNum">The too-high <c>MsgSeqNum</c> of the queued frame.</param>
    void OnInboundQueued(SessionID sessionId, SeqNumType seqNum) { }

    /// <summary>Called in <c>NextQueued</c> immediately before a queued frame is replayed via
    /// <c>NextMessage</c>, and also in the LOGON/RESEND skip branch (which does not call
    /// <c>NextMessage</c>), with the frame's <c>MsgSeqNum</c>. In the replay case the adapter
    /// restores the original CaptureId from its keyed store into the correlator slot so
    /// <c>FromApp</c> reads the correct id. In the skip-branch case the adapter discards the
    /// spilled entry to prevent a slot leak (§12.6).</summary>
    /// <param name="sessionId">The session the frame belongs to.</param>
    /// <param name="seqNum">The <c>MsgSeqNum</c> of the frame about to be replayed or skipped.</param>
    void OnInboundReplayPrepare(SessionID sessionId, SeqNumType seqNum) { }

    /// <summary>Called when the session's inbound message queue is cleared (on disconnect /
    /// session reset). Allows the engine adapter to purge any spilled CaptureId entries for
    /// this session that will never be replayed.</summary>
    /// <param name="sessionId">The session whose queue was cleared.</param>
    void OnInboundQueueCleared(SessionID sessionId) { }

    /// <summary>Called for every outbound frame with the verbatim, pre-redaction body and the
    /// transmitted disposition (M1). Fires inside the <c>_sync</c> lock after the responder call
    /// (or after the null-responder check) so the disposition is definitive at call time.</summary>
    /// <param name="sessionId">The session the frame is sent on.</param>
    /// <param name="rawFrame">The exact wire bytes, SOH-delimited, before redaction.</param>
    /// <param name="transmitted"><see langword="true"/> if <c>_responder.Send</c> returned
    /// <see langword="true"/>; <see langword="false"/> if the responder was null or returned
    /// <see langword="false"/> (frame generated but not transmitted — phantom send).</param>
    void OnOutbound(SessionID sessionId, string rawFrame, bool transmitted);
}
