// FP Enhancement: 2026-05-24 — optional interface for receiving notification when the counterparty rejects one of our messages via a session-level Reject (msgtype 3). Upstream has no equivalent callback in IApplication; opt-in by implementing this alongside IApplication and Session will dispatch via NotifyMessageRejected.
namespace QuickFix;

/// <summary>
/// Optional interface for receiving notification when the counterparty
/// rejects a message we sent, via a session-level Reject (msgtype 3).
/// This allows the application to take corrective action (e.g., updating
/// placement state) for outbound messages that were rejected.
/// </summary>
public interface IApplicationMessageRejection
{
    /// <summary>
    /// Called when a session-level Reject is received from the counterparty,
    /// indicating that a message we sent was rejected.
    /// </summary>
    /// <param name="rejectMessage">The inbound Reject message (msgtype 3) containing RefSeqNum, Text, etc.</param>
    /// <param name="originalMessage">The original sent message that was rejected, if retrievable from the message store; null otherwise</param>
    /// <param name="sessionId">The session on which the reject was received</param>
    /// <param name="reason">The reject reason text</param>
    void OnMessageRejected(Message rejectMessage, Message? originalMessage, SessionID sessionId, string reason);

    // FP Enhancement: 2026-08-10 — expose whether the referenced outbound message was successfully retransmitted while keeping existing four-argument implementers source-compatible through the default body.
    /// <summary>
    /// Called when a session-level Reject is received from the counterparty,
    /// indicating that a message we sent was rejected.
    /// </summary>
    /// <param name="rejectMessage">The inbound Reject message (msgtype 3) containing RefSeqNum, Text, etc.</param>
    /// <param name="originalMessage">The original sent message that was rejected, if retrievable from the message store; null otherwise</param>
    /// <param name="sessionId">The session on which the reject was received</param>
    /// <param name="reason">The reject reason text</param>
    /// <param name="referencedMessageWasResent">True when RefSeqNum is among the most recent 1,024 distinct successful retransmissions since the last lifecycle clear; false when it was never resent or has been evicted</param>
    void OnMessageRejected(
        Message rejectMessage,
        Message? originalMessage,
        SessionID sessionId,
        string reason,
        bool referencedMessageWasResent)
        => OnMessageRejected(rejectMessage, originalMessage, sessionId, reason);
}
