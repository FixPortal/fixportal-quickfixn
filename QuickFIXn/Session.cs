using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.Logging;
using QuickFix.Fields;
using QuickFix.Fields.Converters;
using QuickFix.Logger;
using QuickFix.Store;
using QuickFix.Util;

namespace QuickFix;

/// <summary>
/// The Session is the primary FIX abstraction for message communication.
/// It performs sequencing and error recovery and represents a communication
/// channel to a counterparty. Sessions are independent of specific communication
/// layer connections. A Session is defined as starting with message sequence number
/// of 1 and ending when the session is reset. The Session could span many sequential
/// connections (it cannot operate on multiple connections simultaneously).
/// </summary>
public class Session : IDisposable
{
    private static readonly Dictionary<SessionID, Session> Sessions = new();
    private static readonly HashSet<string> AdminMsgTypes = ["0", "A", "1", "2", "3", "4", "5"];

    private readonly object _sync = new();
    private IResponder? _responder;
    private readonly SessionSchedule _schedule;
    private readonly SessionState _state;
    private readonly IMessageFactory _msgFactory;
    private readonly bool _appDoesEarlyIntercept;
    // FP Enhancement: 2026-05-24 — cache whether the application opts into the rejection-notification callback (see IApplicationMessageRejection / NotifyMessageRejected).
    private readonly bool _appHandlesRejection;
    // FP Enhancement: verbatim wire-frame tap for the engine Tier-2 capture seam (null when not wired). See IFixWireTap.
    private readonly IFixWireTap? _wireTap;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(bool Outbound, ulong Seq), (int LogId, DateTime CaptureTime)> _resentTracker = new();
    // FP Enhancement: 2026-08-10 — retain successful outbound retransmissions so session Reject callbacks can distinguish replayed frames from first transmissions.
    private readonly object _resentOutboundHistorySync = new();
    private readonly HashSet<SeqNumType> _resentOutboundHistory = [];
    private readonly Queue<SeqNumType> _resentOutboundHistoryOrder = [];

    private const LogLevel MessagesLogLevel = LogLevel.Information;

    // FP Enhancement: 2026-08-10 — bound resend history per session so long-lived connections cannot grow it without limit.
    /// <summary>
    /// Maximum number of successfully retransmitted outbound sequence numbers retained per session.
    /// The 1,024-entry FIFO bound prevents long-lived sessions from growing without limit.
    /// </summary>
    internal const int ResentOutboundHistoryCapacity = 1024;

    #region Properties

    // state
    public IMessageStore MessageStore => _state.MessageStore;
    public ILogger Log => _state.Log;
    public bool IsInitiator => _state.IsInitiator;
    public bool IsAcceptor => !_state.IsInitiator;
    public bool IsEnabled => _state.IsEnabled;
    public bool IsSessionTime => _schedule.IsSessionTime(DateTime.UtcNow);
    public bool IsLoggedOn => ReceivedLogon && SentLogon;
    public bool SentLogon => _state.SentLogon;
    public bool ReceivedLogon => _state.ReceivedLogon;

    public bool IsNewSession
    {
        get
        {
            DateTime? creationTime = _state.CreationTime;
            return creationTime.HasValue == false
                || _schedule.IsNewSession(creationTime.Value, DateTime.UtcNow);
        }
    }

    /// <summary>
    /// Session setting for heartbeat interval (in seconds)
    /// </summary>
    public int HeartBtInt => _state.HeartBtInt;

    /// <summary>
    /// Session setting for enabling message latency checks
    /// </summary>
    public bool CheckLatency { get; set; }

    /// <summary>
    /// Session setting for maximum message latency (in seconds)
    /// </summary>
    public int MaxLatency { get; set; }

    /// <summary>
    /// Send a logout if counterparty times out and does not heartbeat
    /// in response to a TestRequeset. Defaults to false
    /// </summary>
    public bool SendLogoutBeforeTimeoutDisconnect { get; set; }

    /// <summary>
    /// Gets or sets the next expected outgoing sequence number
    /// </summary>
    public SeqNumType NextSenderMsgSeqNum
    {
        get => _state.NextSenderMsgSeqNum;
        set => _state.NextSenderMsgSeqNum = value;
    }

    /// <summary>
    /// Gets or sets the next expected incoming sequence number
    /// </summary>
    public SeqNumType NextTargetMsgSeqNum
    {
        get => _state.NextTargetMsgSeqNum;
        set => _state.NextTargetMsgSeqNum = value;
    }

    /// <summary>
    /// Logon timeout in seconds
    /// </summary>
    public int LogonTimeout
    {
        get => _state.LogonTimeout;
        set => _state.LogonTimeout = value;
    }

    /// <summary>
    /// Logout timeout in seconds
    /// </summary>
    public int LogoutTimeout
    {
        get => _state.LogoutTimeout;
        set => _state.LogoutTimeout = value;
    }

    // unsynchronized properties
    /// <summary>
    /// Whether to persist messages or not. Setting to false forces quickfix
    /// to always send GapFills instead of resending messages.
    /// </summary>
    public bool PersistMessages { get; set; }

    /// <summary>
    /// Determines if session state should be restored from persistance
    /// layer when logging on. Useful for creating hot failover sessions.
    /// </summary>
    public bool RefreshOnLogon { get; set; }

    /// <summary>
    /// Reset sequence numbers on logon request
    /// </summary>
    public bool ResetOnLogon { get; set; }

    /// <summary>
    /// Reset sequence numbers to 1 after a normal logout
    /// </summary>
    public bool ResetOnLogout { get; set; }

    /// <summary>
    /// Reset sequence numbers to 1 after abnormal termination
    /// </summary>
    public bool ResetOnDisconnect { get; set; }

    /// <summary>
    /// Whether to send redundant resend requests
    /// </summary>
    public bool SendRedundantResendRequests { get; set; }

    /// <summary>
    /// Whether to resend session level rejects (msg type '3') when servicing a resend request
    /// </summary>
    public bool ResendSessionLevelRejects { get; set; }

    /// <summary>
    /// Whether to validate length and checksum of messages
    /// </summary>
    public bool ValidateLengthAndChecksum { get; set; }

    /// <summary>
    /// Whether to validates Comp IDs for each message
    /// </summary>
    public bool CheckCompID { get; set; }

    /// <summary>
    /// Gets or sets the time stamp precision.
    /// </summary>
    /// <value>
    /// The time stamp precision.
    /// </value>
    public TimeStampPrecision TimeStampPrecision
    {
        get;
        set;
    }

    /// <summary>
    /// Adds the last message sequence number processed in the header (tag 369)
    /// </summary>
    public bool EnableLastMsgSeqNumProcessed { get; set; }

    /// <summary>
    /// Ignores resend requests marked poss dup
    /// </summary>
    public bool IgnorePossDupResendRequests { get; set; }

    /// <summary>
    /// Sets a maximum number of messages to request in a resend request.
    /// </summary>
    public SeqNumType MaxMessagesInResendRequest { get; set; }

    /// <summary>
    /// This is the FIX field value, e.g. "6" for FIX44
    /// </summary>
    public ApplVerID? TargetDefaultApplVerId { get; set; }

    /// <summary>
    /// This is the FIX field value, e.g. "6" for FIX44
    /// </summary>
    public string SenderDefaultApplVerID { get; set; }

    public SessionID SessionID { get; set; }
    public IApplication Application { get; }
    public DataDictionaryProvider DataDictionaryProvider { get; }
    public DataDictionary.DataDictionary SessionDataDictionary { get; }
    public DataDictionary.DataDictionary ApplicationDataDictionary { get; }

    /// <summary>
    /// Returns whether the Session has a Responder. This method is synchronized
    /// </summary>
    public bool HasResponder { get { Thread.MemoryBarrier(); return _responder is not null; } }

    /// <summary>
    /// Returns whether the Sessions will allow ResetSequence messages sent as
    /// part of a resend request (PossDup=Y) to omit the OrigSendingTime
    /// </summary>
    public bool RequiresOrigSendingTime { get; set; }

    /// <summary>
    /// True if session is waiting for ResendRequest content.
    /// If the RR's EndSeqNo is 0 aka infinite, then this becomes
    /// false after the first resent message is received.
    /// Else it remains true until EndSeqNo is received.
    /// </summary>
    internal bool IsResendRequested => _state.IsResendRequested();

    public bool CmeEnhancedResend { get; set; }

    public int[] RedactFieldsInLogs { get; set; } = [];
    public string RedactionLogText { get; set; } = "<redacted>";

    #endregion

    internal Session(
        bool isInitiator,
        IApplication app,
        IMessageStoreFactory storeFactory,
        SessionID sessId,
        DataDictionaryProvider dataDictProvider,
        SessionSchedule sessionSchedule,
        int heartBtInt,
        IQuickFixLoggerFactory loggerFactory,
        IMessageFactory msgFactory,
        string senderDefaultApplVerId,
        IFixWireTap? wireTap = null)
    {
        _schedule = sessionSchedule;
        _msgFactory = msgFactory;
        _appDoesEarlyIntercept = app is IApplicationExt;
        _appHandlesRejection = app is IApplicationMessageRejection;
        _wireTap = wireTap;

        Application = app;
        SessionID = sessId;
        DataDictionaryProvider = new DataDictionaryProvider(dataDictProvider);
        SenderDefaultApplVerID = senderDefaultApplVerId;

        SessionDataDictionary = DataDictionaryProvider.GetSessionDataDictionary(SessionID.BeginString);
        ApplicationDataDictionary = SessionID.IsFIXT
            ? DataDictionaryProvider.GetApplicationDataDictionary(SenderDefaultApplVerID)
            : SessionDataDictionary;

        ILogger logger = loggerFactory.CreateSessionLogger(sessId);

        _state = new SessionState(isInitiator, logger, heartBtInt, storeFactory.Create(sessId));

        // Configuration defaults.
        // Will be overridden by the SessionFactory with values in the user's configuration.
        PersistMessages = true;
        ResetOnDisconnect = false;
        SendRedundantResendRequests = false;
        ResendSessionLevelRejects = false;
        ValidateLengthAndChecksum = true;
        CheckCompID = true;
        TimeStampPrecision = TimeStampPrecision.Millisecond;
        EnableLastMsgSeqNumProcessed = false;
        MaxMessagesInResendRequest = 0;
        SendLogoutBeforeTimeoutDisconnect = false;
        IgnorePossDupResendRequests = false;
        RequiresOrigSendingTime = true;
        CheckLatency = true;
        MaxLatency = 120;

        if (!IsSessionTime)
            Reset("Out of SessionTime (Session construction)");
        else if (IsNewSession)
            Reset("New session");

        lock (Sessions)
        {
            Sessions[SessionID] = this;
        }

        Application.OnCreate(SessionID);
        Log.Log(LogLevel.Information, "Created session");
    }

    #region Static Methods

    /// <summary>
    /// Looks up a Session by its SessionID
    /// </summary>
    /// <param name="sessionId">the SessionID of the Session</param>
    /// <returns>the Session if found, else returns null</returns>
    public static Session? LookupSession(SessionID sessionId)
    {
        lock (Sessions) {
            if (Sessions.TryGetValue(sessionId, out Session? result))
                return result;
        }

        return null;
    }

    /// <summary>
    /// Looks up a Session by its SessionID
    /// </summary>
    /// <param name="sessionId">the SessionID of the Session</param>
    /// <returns>the true if Session exists, false otherwise</returns>
    public static bool DoesSessionExist(SessionID sessionId)
    {
        return LookupSession(sessionId) is not null;
    }

    /// <summary>
    /// Sends a message to the session specified by the provider session ID.
    /// </summary>
    /// <param name="message">FIX message</param>
    /// <param name="sessionId">target SessionID</param>
    /// <returns>true if send was successful, false otherwise</returns>
    public static bool SendToTarget(Message message, SessionID sessionId)
    {
        message.SetSessionID(sessionId);
        Session? session = Session.LookupSession(sessionId);
        if (session is null)
            throw new SessionNotFound(sessionId);
        return session.Send(message);
    }

    /// <summary>
    /// Send to session indicated by header fields in message
    /// </summary>
    /// <param name="message"></param>
    /// <returns></returns>
    public static bool SendToTarget(Message message)
    {
        return SendToTarget(message, message.GetSessionID(message));
    }

    #endregion

    /// <summary>
    /// Sends a message via the session indicated by the header fields
    /// </summary>
    /// <param name="message">message to send</param>
    /// <returns>true if was sent successfully</returns>
    public virtual bool Send(Message message)
    {
        message.Header.RemoveField(Fields.Tags.PossDupFlag);
        message.Header.RemoveField(Fields.Tags.OrigSendingTime);
        return SendRaw(message);
    }

    // FP Enhancement: 2026-05-24 — overloads of SendToTarget / Send that let callers opt out of stripping PossDupFlag / OrigSendingTime; useful when retransmitting a message that should retain its original dup-flag semantics (e.g. CME-style resend flows). The flag-less originals stay unchanged.
    public static bool SendToTarget(Message message, SessionID sessionID, bool removeDupeFlag = true, bool removeOriginalSendingTime = true)
    {
        message.SetSessionID(sessionID);
        Session? session = Session.LookupSession(sessionID);
        if (null == session)
            throw new SessionNotFound(sessionID);
        return session.Send(message, removeDupeFlag, removeOriginalSendingTime);
    }

    public virtual bool Send(Message message, bool removeDupeFlag, bool removeOriginalSendingTime = true)
    {
        if (removeDupeFlag) message.Header.RemoveField(Fields.Tags.PossDupFlag);
        if (removeOriginalSendingTime) message.Header.RemoveField(Fields.Tags.OrigSendingTime);
        return SendRaw(message, 0);
    }

    /// <summary>
    /// Sends a message
    /// </summary>
    /// <param name="message"></param>
    /// <returns></returns>
    public bool Send(string message)
    {
        lock (_sync)
        {
            // A-F5 / M1: tap fires for every generated outbound frame, including phantom sends
            // (when _responder is null or Send returns false). The transmitted flag tells the
            // engine adapter whether the frame reached the wire, so it can mark the capture row
            // accordingly rather than relying solely on the reconciliation arm.
            if (_responder is null)
            {
                TapOutbound(message, transmitted: false);
                return false;
            }

            if (Log.IsEnabled(MessagesLogLevel))
            {
                using (Log.BeginScope(new Dictionary<string, object>
                       {
                           { "MessageType", Message.GetMsgType(message) }
                       }))
                {
                    Log.Log(MessagesLogLevel, LogEventIds.OutgoingMessage, "{Message}",
                        LogAssist.RedactSensitiveFields(message, RedactFieldsInLogs, RedactionLogText));
                }
            }

            bool transmitted = _responder.Send(message);
            TapOutbound(message, transmitted);
            return transmitted;
        }
    }

    /// <summary>
    /// Sets some internal state variables to enable the session.
    /// </summary>
    public void Logon()
    {
        _state.IsEnabled = true;
        _state.LogoutReason = "";
    }

    /// <summary>
    /// Sets some internal state variables to disable the session.
    /// Users will be disconnected on next cycle.
    /// (This function is for actively initiating a logout,
    /// it is NOT for processing a logout request from the counterparty.)
    /// </summary>
    public void Logout(string reason = "")
    {
        _state.IsEnabled = false;
        _state.LogoutReason = reason;
    }

    // FP Enhancement: 2026-08-16 — read-only views of the two logout signals, for
    // applications that classify why a session went down. LastDisconnectReason is why
    // the socket was torn down; LogoutReason is non-empty only when Logout(reason) was
    // called, i.e. the session was deliberately taken out of service.
    public string LastDisconnectReason => _state.LastDisconnectReason;

    public string LogoutReason => _state.LogoutReason;

    /// <summary>
    /// Logs out from session and closes the network connection
    /// </summary>
    /// <param name="reason"></param>
    public void Disconnect(string reason)
    {
        lock (_sync)
        {
            if (_responder is not null)
            {
                Log.Log(LogLevel.Information, "Session {SessionID} disconnecting: {Reason}", SessionID, reason);
                _responder.Disconnect();
                _responder = null;
            }
            else
            {
                Log.Log(LogLevel.Information, "Session {SessionID} already disconnected: {Reason}",
                    SessionID, reason);
            }

            if (_state.ReceivedLogon || _state.SentLogon)
            {
                _state.ReceivedLogon = false;
                _state.SentLogon = false;
                // FP Enhancement: 2026-08-16 — surface the disconnect reason to the
                // application so it can distinguish an intentional logout from a drop.
                // Stamped before the callback; LogoutReason is not cleared until below,
                // so both signals are readable from inside OnLogout.
                _state.LastDisconnectReason = reason;
                Application.OnLogout(SessionID);
            }

            _state.SentLogout = false;
            _state.ReceivedReset = false;
            _state.SentReset = false;
            _state.ClearQueue();
            TapInboundQueueCleared();
            // Drop the LogExtended resend-correlation cache; its seqnum keys are recycled next session.
            _resentTracker.Clear();
            // FP Enhancement: 2026-08-10 — drop resend history when sequence numbers may be recycled after disconnect.
            ClearResentOutboundHistory();
            _state.LogoutReason = "";
            if (ResetOnDisconnect)
                _state.Reset("ResetOnDisconnect");
            _state.ResetResendRange();
        }
    }

    /// <summary>
    /// There's no message to process, but check the session state to see if there's anything to do
    /// (e.g. send heartbeat, logout at end of session, etc)
    /// </summary>
    public void Next()
    {
        if (!HasResponder)
            return;

        if (!IsSessionTime)
        {
            if(IsInitiator)
                Reset("Out of SessionTime (Session.Next())");
            else
                Reset("Out of SessionTime (Session.Next())", "Message received outside of session time");
            return;
        }

        if (IsNewSession)
            _state.Reset("New session (detected in Next())");

        if (!IsEnabled)
        {
            if (!IsLoggedOn)
                return;

            if (!_state.SentLogout)
            {
                Log.Log(LogLevel.Information, "Initiated logout request");
                GenerateLogout(_state.LogoutReason);
            }
        }

        if (!_state.ReceivedLogon)
        {
            if (_state.ShouldSendLogon && IsTimeToGenerateLogon())
            {
                if (GenerateLogon())
                    Log.Log(LogLevel.Information, "Initiated logon request");
                else
                    Log.Log(LogLevel.Error, "Error during logon request initiation");

            }
            else if (_state.SentLogon && _state.LogonTimedOut())
            {
                Disconnect("Timed out waiting for logon response");
            }
            return;
        }

        if (0 == _state.HeartBtInt)
            return;

        if (_state.LogoutTimedOut())
            Disconnect("Timed out waiting for logout response");

        if (_state.WithinHeartbeat())
            return;

        if (_state.TimedOut())
        {
            if (SendLogoutBeforeTimeoutDisconnect)
                GenerateLogout();
            Disconnect("Timed out waiting for heartbeat");
        }
        else
        {
            if (_state.NeedTestRequest())
            {
                GenerateTestRequest("TEST");
                _state.TestRequestCounter += 1;
                Log.Log(LogLevel.Information, "Sent test request TEST");
            }
            else if (_state.NeedHeartbeat())
            {
                GenerateHeartbeat();
            }
        }
    }

    /// <summary>
    /// Process a message (in string form) from the counterparty
    /// </summary>
    /// <param name="msgStr"></param>
    public void Next(string msgStr)
    {
        // FP Enhancement: capture the verbatim inbound frame once per wire arrival, before parsing
        // and before any field redaction (queued-message replay re-enters via NextMessage, not
        // here, so this fires exactly once per arrival). See IFixWireTap.
        TapInbound(msgStr);
        NextMessage(msgStr);
        _state.LastProcessedMessageWasQueued = false;
        NextQueued();
    }

    // FP Enhancement: invoke the capture wire-tap. A throwing tap must never disrupt FIX
    // processing, so the call is guarded; only the error text is logged (never the frame body).
    private void TapInbound(string rawFrame)
    {
        if (_wireTap is null)
            return;
        try
        {
            _wireTap.OnInbound(SessionID, rawFrame);
        }
        catch (Exception e)
        {
            Log.Log(LogLevel.Warning, "FIX wire-tap OnInbound threw and was suppressed: {Error}", e.Message);
        }
    }

    private void TapInboundQueued(SeqNumType seqNum)
    {
        if (_wireTap is null)
            return;
        try
        {
            _wireTap.OnInboundQueued(SessionID, seqNum);
        }
        catch (Exception e)
        {
            Log.Log(LogLevel.Warning, "FIX wire-tap OnInboundQueued threw and was suppressed: {Error}", e.Message);
        }
    }

    private void TapInboundReplayPrepare(SeqNumType seqNum)
    {
        if (_wireTap is null)
            return;
        try
        {
            _wireTap.OnInboundReplayPrepare(SessionID, seqNum);
        }
        catch (Exception e)
        {
            Log.Log(LogLevel.Warning, "FIX wire-tap OnInboundReplayPrepare threw and was suppressed: {Error}", e.Message);
        }
    }

    private void TapInboundQueueCleared()
    {
        if (_wireTap is null)
            return;
        try
        {
            _wireTap.OnInboundQueueCleared(SessionID);
        }
        catch (Exception e)
        {
            Log.Log(LogLevel.Warning, "FIX wire-tap OnInboundQueueCleared threw and was suppressed: {Error}", e.Message);
        }
    }

    private void TapOutbound(string rawFrame, bool transmitted)
    {
        if (_wireTap is null)
            return;
        try
        {
            _wireTap.OnOutbound(SessionID, rawFrame, transmitted);
        }
        catch (Exception e)
        {
            Log.Log(LogLevel.Warning, "FIX wire-tap OnOutbound threw and was suppressed: {Error}", e.Message);
        }
    }

    /// <summary>
    /// Process a message (in string form) from the counterparty
    /// </summary>
    /// <param name="msgStr"></param>
    private void NextMessage(string msgStr)
    {
        try
        {
            if (Log.IsEnabled(MessagesLogLevel))
            {
                using (Log.BeginScope(new Dictionary<string, object>
                       {
                           { "MessageType", Message.GetMsgType(msgStr) }
                       }))
                {
                    Log.Log(MessagesLogLevel, LogEventIds.IncomingMessage, "{Message}",
                        LogAssist.RedactSensitiveFields(msgStr, RedactFieldsInLogs, RedactionLogText));
                }
            }
        } catch (Exception ex)
        {
            // FP Enhancement: this catch fires precisely when RedactSensitiveFields threw, so msgStr is the
            // raw, un-redacted frame and may carry sensitive tags (e.g. Password(554)). Logging any of its
            // bytes here would defeat redaction on the one path that bypasses it — emit metadata only.
            Log.Log(LogLevel.Warning, ex, "Failed to format/redact incoming message ({Length} bytes) — frame omitted to avoid leaking unredacted content.",
                msgStr.Length);
        }

        MessageBuilder msgBuilder = new MessageBuilder(
                msgStr,
                SenderDefaultApplVerID,
                ValidateLengthAndChecksum,
                SessionDataDictionary,
                ApplicationDataDictionary,
                _msgFactory);

        Next(msgBuilder);
    }

    /// <summary>
    /// Process a message from the counterparty.
    /// </summary>
    /// <param name="msgBuilder"></param>
    internal void Next(MessageBuilder msgBuilder)
    {
        if (!IsSessionTime)
        {
            Reset("Out of SessionTime (Session.Next(message))", "Message received outside of session time");
            return;
        }

        if (IsNewSession)
            _state.Reset("New session (detected in Next(Message))");

        Message? message = null; // declared outside of try-block so that catch-blocks can use it

        try
        {
            message = msgBuilder.Build();

            if (_appDoesEarlyIntercept)
                ((IApplicationExt)Application).FromEarlyIntercept(message, SessionID);

            string msgType = msgBuilder.MsgType.Value;
            string beginString = msgBuilder.BeginString;

            if (!beginString.Equals(SessionID.BeginString))
                throw new UnsupportedVersion(beginString);


            if (MsgType.LOGON.Equals(msgType)) {
                TargetDefaultApplVerId = SessionID.IsFIXT
                    ? new ApplVerID(message.GetString(Fields.Tags.DefaultApplVerID))
                    : Message.GetApplVerID(beginString);
            }

            if (SessionID.IsFIXT && !Message.IsAdminMsgType(msgType))
            {
                DataDictionary.DataDictionary.Validate(message, SessionDataDictionary, ApplicationDataDictionary, beginString, msgType);
            }
            else
            {
                DataDictionary.DataDictionary.Validate(message, SessionDataDictionary, SessionDataDictionary, beginString, msgType);
            }

            if (MsgType.LOGON.Equals(msgType))
                NextLogon(message);
            else if (MsgType.LOGOUT.Equals(msgType))
                NextLogout(message);
            // FP Enhancement: 2026-05-24 — tolerate a Logout (msgtype 5) arriving before logon completes; FIX spec permits it.
            // This is handled by routing MsgType.LOGOUT to NextLogout above regardless of IsLoggedOn.
            else if (!IsLoggedOn)
                Disconnect($"Received msg type '{msgType}' when not logged on");
            else if (MsgType.HEARTBEAT.Equals(msgType))
                NextHeartbeat(message);
            else if (MsgType.TEST_REQUEST.Equals(msgType))
                NextTestRequest(message);
            else if (MsgType.SEQUENCE_RESET.Equals(msgType))
                NextSequenceReset(message);
            else if (MsgType.RESEND_REQUEST.Equals(msgType))
                NextResendRequest(message);
            else
            {
                if (!Verify(message))
                    return;
                _state.IncrNextTargetMsgSeqNum();
            }

            // FP Enhancement: 2026-05-24 — emit incoming-message XML/JSON via Log.OnIncomingAndOutgoing when the SessionID has a tracked LogId (see LogExtended below).
            LogExtended(message, msgType, isOutbound: false);

        }
        catch (InvalidMessage e)
        {
            Log.Log(LogLevel.Information, "{Message}", e.Message);

            try
            {
                if (MsgType.LOGON.Equals(msgBuilder.MsgType.Value))
                    Disconnect("Logon message is not valid");
            }
            catch (MessageParseError)
            { }

            throw;
        }
        catch (TagException e)
        {
            if (e.InnerException is not null)
                Log.Log(LogLevel.Error, "{Message}", e.InnerException.Message);

            if (!_state.ReceivedLogon)
            {
                Disconnect("Logon message has invalid tag format/value: " + e.Message);
            }
            else
            {
                // FP Enhancement: 2026-06-08 — pass the offending field value (e.Value, e.g. from IncorrectTagValue) into the reject so it lands in the structured reject-log line.
                GenerateReject(msgBuilder, e.sessionRejectReason, e.Field, e.Value);
            }
        }
        catch (UnsupportedVersion uvx)
        {
            if (MsgType.LOGOUT.Equals(msgBuilder.MsgType.Value))
            {
                NextLogout(message!);
            }
            else
            {
                Log.Log(LogLevel.Error, uvx, "{Message}", uvx.ToString());
                GenerateLogout(uvx.Message);
                _state.IncrNextTargetMsgSeqNum();
            }
        }
        catch (MessageFactoryNotFound mfnf)
        {
            if (MsgType.LOGOUT.Equals(msgBuilder.MsgType.Value))
            {
                NextLogout(message!);
            }
            else
            {
                // We shouldn't send that question to the counterparty, but local devs should see it
                Log.Log(LogLevel.Error, mfnf,
                    "{Message} (Did you forget a message package dependency?)", mfnf.ToString());
                GenerateLogout(mfnf.Message);
                _state.IncrNextTargetMsgSeqNum();
            }
        }
        catch (UnsupportedMessageType e)
        {
            Log.Log(LogLevel.Error, e, "Unsupported message type: {Message}", e.Message);
            GenerateBusinessMessageReject(message!, Fields.BusinessRejectReason.UNKNOWN_MESSAGE_TYPE, 0);
        }
        catch (FieldNotFoundException e)
        {
            Log.Log(LogLevel.Warning, e, "Rejecting invalid message, field not found: {Message}", e.Message);
            if (string.CompareOrdinal(SessionID.BeginString, FixValues.BeginString.FIX42) >= 0 && message!.IsApp())
            {
                GenerateBusinessMessageReject(message, Fields.BusinessRejectReason.CONDITIONALLY_REQUIRED_FIELD_MISSING, e.Field);
            }
            else
            {
                if (MsgType.LOGON.Equals(msgBuilder.MsgType.Value))
                {
                    Log.Log(LogLevel.Error, "Required field missing from logon");
                    Disconnect("Required field missing from logon");
                }
                else
                    GenerateReject(msgBuilder, new QuickFix.FixValues.SessionRejectReason(SessionRejectReason.REQUIRED_TAG_MISSING, "Required Tag Missing"), e.Field);
            }
        }
        catch (RejectLogon e)
        {
            GenerateLogout(e.Message);
            Disconnect(e.ToString());
        }

        Next();
    }

    protected void NextLogon(Message logon)
    {
        _state.ReceivedReset = logon.IsSetField(ResetSeqNumFlag.TAG) && logon.GetBoolean(ResetSeqNumFlag.TAG);

        if (_state.ReceivedReset)
        {
            Log.Log(LogLevel.Information, "Sequence numbers reset due to ResetSeqNumFlag=Y");
            if (!_state.SentReset)
            {
                _state.Reset("Reset requested by counterparty");
                _resentTracker.Clear();
                // FP Enhancement: 2026-08-10 — drop resend history before the peer reuses sequence numbers.
                ClearResentOutboundHistory();
            }
        }

        if (IsAcceptor && ResetOnLogon)
        {
            _state.Reset("ResetOnLogon");
            _resentTracker.Clear();
            // FP Enhancement: 2026-08-10 — drop resend history before acceptor logon reset reuses sequence numbers.
            ClearResentOutboundHistory();
        }
        if (RefreshOnLogon)
            Refresh();

        if (!Verify(logon, false, true))
            return;

        if (!IsGoodTime(logon))
        {
            Log.Log(LogLevel.Error, "Logon has bad sending time");
            Disconnect("bad sending time");
            return;
        }

        _state.ReceivedLogon = true;
        Log.Log(LogLevel.Information, "Received logon");
        if (IsAcceptor)
        {
            int heartBtInt = logon.GetInt(Fields.Tags.HeartBtInt);
            _state.HeartBtInt = heartBtInt;
            GenerateLogon(logon);
            Log.Log(LogLevel.Information, "Responding to logon request; heartbeat is {HeartBtInt} seconds",
                heartBtInt);
        }

        _state.SentReset = false;
        _state.ReceivedReset = false;

        SeqNumType msgSeqNum = logon.Header.GetULong(Fields.Tags.MsgSeqNum);
        if (IsTargetTooHigh(msgSeqNum) && !_state.ReceivedReset)
        {
            DoTargetTooHigh(logon, msgSeqNum);
        }
        else
        {
            _state.IncrNextTargetMsgSeqNum();
        }

        if (IsLoggedOn)
            Application.OnLogon(SessionID);
    }

    protected void NextTestRequest(Message testRequest)
    {
        if (!Verify(testRequest))
            return;
        GenerateHeartbeat(testRequest);
        _state.IncrNextTargetMsgSeqNum();
    }

    protected void NextResendRequest(Message resendReq)
    {
        if (!Verify(resendReq, false, false))
            return;
        try {
            SeqNumType msgSeqNum;
            if (!(IgnorePossDupResendRequests && resendReq.Header.IsSetField(Tags.PossDupFlag)))
            {
                SeqNumType begSeqNo = resendReq.GetULong(Fields.Tags.BeginSeqNo);
                SeqNumType endSeqNo = resendReq.GetULong(Fields.Tags.EndSeqNo);
                Log.Log(LogLevel.Information, "Got resend request from {BeginSeqNo} to {EndSeqNo}",
                    begSeqNo, endSeqNo);

                if (endSeqNo == 999999 || endSeqNo == 0)
                {
                    endSeqNo = _state.NextSenderMsgSeqNum - 1;
                }

                if (!PersistMessages)
                {
                    endSeqNo++;
                    SeqNumType next = _state.NextSenderMsgSeqNum;
                    if (endSeqNo > next)
                        endSeqNo = next;
                    GenerateSequenceReset(resendReq, begSeqNo, endSeqNo);
                    msgSeqNum = resendReq.Header.GetULong(Tags.MsgSeqNum);
                    if (!IsTargetTooHigh(msgSeqNum) && !IsTargetTooLow(msgSeqNum))
                    {
                        _state.IncrNextTargetMsgSeqNum();
                    }
                    return;
                }

                List<string> messages = new List<string>();
                _state.Get(begSeqNo, endSeqNo, messages);
                SeqNumType current = begSeqNo;
                SeqNumType begin = 0;
                foreach (string msgStr in messages)
                {
                    Message msg = new Message();
                    msg.FromString(msgStr, true, SessionDataDictionary, ApplicationDataDictionary, _msgFactory, ignoreBody: false);
                    msgSeqNum = msg.Header.GetULong(Tags.MsgSeqNum);

                    if (current != msgSeqNum && begin == 0)
                    {
                        begin = current;
                    }

                    if (IsAdminMessage(msg) && !(ResendSessionLevelRejects && msg.Header.GetString(Tags.MsgType) == MsgType.REJECT))
                    {
                        if (begin == 0)
                        {
                            begin = msgSeqNum;
                        }
                    }
                    else
                    {

                        InitializeResendFields(msg);
                        if(!ResendApproved(msg, SessionID))
                        {
                            continue;
                        }

                        if (begin != 0)
                        {
                            GenerateSequenceReset(resendReq, begin, msgSeqNum);
                        }
                        // FP Enhancement: 2026-08-10 — retain the session lock across successful replay send and history recording so a lifecycle clear cannot be followed by a stale record.
                        lock (_sync)
                        {
                            if (Send(msg.ConstructString()))
                                RecordResentOutbound(msgSeqNum);
                        }
                        // FP Enhancement: 2026-06-08 — mirror the outbound LogExtended hook for replayed messages. The resend path sends via Send(string), bypassing SendRaw where LogExtended is invoked, so tracked resent application messages were missing from the extended XML/JSON audit stream. (LogExtended itself skips admin message types.)
                        LogExtended(msg, msg.Header.GetString(Tags.MsgType), isOutbound: true);
                        begin = 0;
                    }
                    current = msgSeqNum + 1;
                }

                SeqNumType nextSeqNum = _state.NextSenderMsgSeqNum;
                if (++endSeqNo > nextSeqNum)
                {
                    endSeqNo = nextSeqNum;
                }

                if (begin == 0)
                {
                    begin = current;
                }

                if (endSeqNo > begin)
                {
                    GenerateSequenceReset(resendReq, begin, endSeqNo);
                }
            }
            msgSeqNum = resendReq.Header.GetULong(Tags.MsgSeqNum);
            if (!IsTargetTooHigh(msgSeqNum) && !IsTargetTooLow(msgSeqNum))
            {
                _state.IncrNextTargetMsgSeqNum();
            }

        }
        catch (Exception e)
        {
            Log.Log(LogLevel.Error, e, "ERROR during resend request {Message}", e.Message);
        }
    }

    private bool ResendApproved(Message msg, SessionID sessionId)
    {
        try
        {
            Application.ToApp(msg, sessionId);
        }
        catch (DoNotSend)
        {
            return false;
        }

        return true;
    }

    protected void NextLogout(Message logout)
    {
        if (!Verify(logout, false, false))
            return;

        string disconnectReason;

        if (_state.SentLogout)
        {
            // We initiated the logout, and this is the response.
            disconnectReason = "Received logout response";
            Log.Log(LogLevel.Information, "{Message}", disconnectReason);

            _state.IncrNextTargetMsgSeqNum();
            if (ResetOnLogout) {
                _state.Reset("ResetOnLogout");
            }
        }
        else
        {
            // Counterparty is initiating the logout
            disconnectReason = "Received logout request";
            Log.Log(LogLevel.Information, "{Message}", disconnectReason);
            GenerateLogout(logout);
            Log.Log(LogLevel.Information, "Sending logout response");


            _state.IncrNextTargetMsgSeqNum();
            if(ResetOnLogout)
                _state.Reset("ResetOnLogout");
            else if (CmeEnhancedResend && logout.IsSetField(789) && logout.GetInt(789) == 1) {
                // Reset, but preserve target seqnum
                SeqNumType n = _state.NextTargetMsgSeqNum;
                _state.Reset("Session reset because CME Logout has tag 789=1");
                NextTargetMsgSeqNum = n;
            }
        }

        Disconnect(disconnectReason);
    }

    protected void NextHeartbeat(Message heartbeat)
    {
        if (!Verify(heartbeat))
            return;
        _state.IncrNextTargetMsgSeqNum();
    }

    // using this variable is hacky, but I didn't come up with a better solution
    private bool _didHandleCmeEnhancedResendGapFill = false;

    protected void NextSequenceReset(Message sequenceReset)
    {
        _didHandleCmeEnhancedResendGapFill = false;

        bool isGapFill = false;
        if (sequenceReset.IsSetField(Tags.GapFillFlag))
            isGapFill = sequenceReset.GetBoolean(Tags.GapFillFlag);

        SeqNumType newSeqNo = sequenceReset.GetULong(Tags.NewSeqNo);
        Log.Log(LogLevel.Information, "Received SequenceRequest FROM: {NextTargetMsgSeqNum} TO: {NewSeqNo}",
            _state.NextTargetMsgSeqNum, newSeqNo);

        if (newSeqNo < _state.NextTargetMsgSeqNum)
        {
            GenerateReject(sequenceReset, FixValues.SessionRejectReason.VALUE_IS_INCORRECT);
            return;
        }

        // This may possibly set _didHandleCmeEnhancedResendGapFill=true if CmeEnhancedResend mode enabled
        if (!Verify(sequenceReset, isGapFill, isGapFill))
            return;

        if (_didHandleCmeEnhancedResendGapFill)
        {
            _didHandleCmeEnhancedResendGapFill = false;
            // Return now, and don't proceed to set NextTargetMsgSeqNum in the next block!
            return;
        }

        if (newSeqNo > _state.NextTargetMsgSeqNum)
        {
            _state.NextTargetMsgSeqNum = newSeqNo;
        }
    }

    private void HandleCmeEnhancedResendGapFill(Message seqReset)
    {
        ResendRange range = _state.GetResendRange();
        SeqNumType newSeqNo = seqReset.GetULong(Tags.NewSeqNo);

        if (range.ChunkEndSeqNo == ResendRange.NOT_SET || range.ChunkEndSeqNo == range.EndSeqNo)
        {
            // We're either in a non-chunk situation or we're in the final chunk.
            if (newSeqNo > range.ActualEndSeqNo)
            {
                Log.Log(LogLevel.Information,
                    "ResendRequest for messages FROM: {BeginSeqNo} TO: {EndSeqNo} has been satisfied (by a gap fill).",
                    range.BeginSeqNo, range.EndSeqNo);
                _state.ResetResendRange();
            }
        }
        else // we're in a non-final chunk
        {
            SeqNumType max = range.ChunkEndSeqNo + 1;
            if (newSeqNo > max)
            {
                Log.Log(LogLevel.Information,
                    "The SequenceReset's NewSeqNo ({NewSeqNo}) is higher than my request ({BeginSeqNo}-{ChunkEndSeqNo}).  Per CME's expected behavior, I'll use {max}.",
                    newSeqNo, range.BeginSeqNo, range.ChunkEndSeqNo, max);
                newSeqNo = max;
            }
            if (newSeqNo == max)
            {
                Log.Log(LogLevel.Information,
                    "Chunked ResendRequest for messages FROM: {BeginSeqNo} TO: {ChunkEndSeqNo} has been satisfied (by a gap fill).",
                    range.BeginSeqNo, range.ChunkEndSeqNo);
                SeqNumType newStart = max;
                SeqNumType newChunkEnd = Math.Min(range.EndSeqNo, newStart + MaxMessagesInResendRequest); // TODO we can +1 this

                Message resendRequest = CreateResendRequest(seqReset.Header.GetString(Tags.BeginString),
                    newStart, newChunkEnd);
                if (EnableLastMsgSeqNumProcessed)
                    resendRequest.Header.SetField(new LastMsgSeqNumProcessed(seqReset.Header.GetULong(Tags.MsgSeqNum)));

                if (SendRaw(resendRequest))
                    Log.Log(LogLevel.Information, "Sent ResendRequest FROM: {NewStart} TO: {NewChunkEnd}", newStart, newChunkEnd);
                else
                    Log.Log(LogLevel.Information, "Error sending ResendRequest ({NewStart}, {NewChunkEnd})", newStart, newChunkEnd);

                range.UpdateChunk(newStart, newChunkEnd);
            }
            else if (newSeqNo >= range.BeginSeqNo)
            {
                range.MarkAsStarted();
            }
        }

        if(newSeqNo > _state.NextTargetMsgSeqNum)
            _state.NextTargetMsgSeqNum = newSeqNo;

        _didHandleCmeEnhancedResendGapFill = true;
    }

    public bool Verify(Message msg, bool checkTooHigh = true, bool checkTooLow = true)
    {
        string msgType;

        try
        {
            msgType = msg.Header.GetString(Fields.Tags.MsgType);
            string senderCompId = msg.Header.GetString(Fields.Tags.SenderCompID);
            string targetCompId = msg.Header.GetString(Fields.Tags.TargetCompID);

            if (!IsCorrectCompId(senderCompId, targetCompId))
            {
                GenerateReject(msg, FixValues.SessionRejectReason.COMPID_PROBLEM);
                GenerateLogout();
                return false;
            }

            if (checkTooHigh || checkTooLow)
            {
                SeqNumType msgSeqNum = msg.Header.GetULong(Fields.Tags.MsgSeqNum);

                if (checkTooHigh && IsTargetTooHigh(msgSeqNum))
                {
                    DoTargetTooHigh(msg, msgSeqNum);
                    return false;
                }
                if (checkTooLow && IsTargetTooLow(msgSeqNum))
                {
                    // FP Enhancement: 2026-06-08 — guard GapFillFlag (tag 123, optional) with IsSetField before GetBoolean. A reset-mode SequenceReset legitimately omits tag 123; the unguarded read threw FieldNotFoundException, which Verify's catch-all turned into a session disconnect. (Upstream v1.14.1 bug; reported upstream.)
                    if (_state.LastProcessedMessageWasQueued
                        && msg.Header.GetString(35) == MsgType.SEQUENCE_RESET
                        && msg.IsSetField(Tags.GapFillFlag)
                        && msg.GetBoolean(Tags.GapFillFlag))
                    {
                        Log.Log(LogLevel.Warning,
                            "SequenceReset-GapFill 34={MsgSeqNum} is too low (expected {NextTargetMsgSeqNum}), but in this case I'm going to obey it anyway",
                            msgSeqNum, _state.NextTargetMsgSeqNum);
                        // This is an uncommon situation, see #309
                    }
                    else
                    {
                        DoTargetTooLow(msg, msgSeqNum);
                        return false;
                    }
                }

                if (IsResendRequested)
                {
                    ResendRange range = _state.GetResendRange();

                    // FP Enhancement: 2026-06-08 — guard GapFillFlag (tag 123, optional) with IsSetField before GetBoolean; a reset-mode SequenceReset during an active resend omits tag 123 and the unguarded read disconnected the session via Verify's catch-all. (Upstream v1.14.1 bug; reported upstream.)
                    if (CmeEnhancedResend && msgType == MsgType.SEQUENCE_RESET && msg.IsSetField(Tags.GapFillFlag) && msg.GetBoolean(Tags.GapFillFlag))
                    {
                        HandleCmeEnhancedResendGapFill(msg);
                    }
                    else if (msgSeqNum >= range.ActualEndSeqNo)
                    {
                        Log.Log(LogLevel.Information,
                            "ResendRequest for messages FROM: {BeginSeqNo} TO: {EndSeqNo} has been satisfied.",
                            range.BeginSeqNo, range.EndSeqNo);
                        _state.ResetResendRange();
                    }
                    else if (msgSeqNum >= range.ChunkEndSeqNo)
                    {
                        Log.Log(LogLevel.Information,
                            "Chunked ResendRequest for messages FROM: {BeginSeqNo} TO: {ChunkEndSeqNo} has been satisfied",
                            range.BeginSeqNo, range.ChunkEndSeqNo);
                        SeqNumType newStart = range.ChunkEndSeqNo + 1;
                        SeqNumType newChunkEndSeqNo = Math.Min(range.EndSeqNo, range.ChunkEndSeqNo + MaxMessagesInResendRequest); // TODO we can +1 this

                        Message resendRequest = CreateResendRequest(msg.Header.GetString(Tags.BeginString),
                            newStart, newChunkEndSeqNo);
                        if (EnableLastMsgSeqNumProcessed)
                            resendRequest.Header.SetField(new LastMsgSeqNumProcessed(msgSeqNum));

                        if (SendRaw(resendRequest))
                        {
                            Log.Log(LogLevel.Information,
                                "Sent ResendRequest FROM: {NewStart} TO: {NewChunkEndSeqNo}",
                                newStart, newChunkEndSeqNo);
                        }
                        else
                        {
                            Log.Log(LogLevel.Information,
                                "Error sending ResendRequest ({NewStart}, {NewChunkEndSeqNo})",
                                newStart, newChunkEndSeqNo);
                        }

                        range.UpdateChunk(newStart, newChunkEndSeqNo);
                    }
                    else if (msgSeqNum >= range.BeginSeqNo)
                    {
                        range.MarkAsStarted();
                    }
                }
            }

            if (!IsGoodTime(msg))
            {
                Log.Log(LogLevel.Error, "Sending time accuracy problem");
                GenerateReject(msg, FixValues.SessionRejectReason.SENDING_TIME_ACCURACY_PROBLEM);
                GenerateLogout();
                return false;
            }
        }
        catch (Exception e)
        {
            Log.Log(LogLevel.Error, e, "Verify failed: {Message}", e.Message);
            Disconnect("Verify failed: " + e.Message);
            return false;
        }

        _state.LastReceivedTimeDT = DateTime.UtcNow;
        _state.TestRequestCounter = 0;

        if (Message.IsAdminMsgType(msgType))
        {
            Application.FromAdmin(msg, SessionID);

            // FP Enhancement: 2026-05-24 — dispatch IApplicationMessageRejection.OnMessageRejected after FromAdmin for inbound session-level Reject messages.
            if (msgType == MsgType.REJECT)
            {
                var reason = msg.IsSetField(Tags.Text) ? msg.GetString(Tags.Text) : "Session-level reject received";
                NotifyMessageRejected(msg, reason);
            }
        }
        else
            Application.FromApp(msg, SessionID);

        return true;
    }

    public void SetResponder(IResponder responder)
    {
        if (!IsSessionTime)
            Reset("Out of SessionTime (Session.SetResponder)");

        lock (_sync)
        {
            _responder = responder;
        }
    }

    public void Refresh()
    {
        _state.Refresh();
    }

    /// <summary>
    /// Send a logout, disconnect, and reset session state
    /// </summary>
    /// <param name="loggedReason">message to log</param>
    /// <param name="logoutMessage">value to put in the Logout message's Text field (ignored if null/empty string)</param>
    public void Reset(string loggedReason, string? logoutMessage = null)
    {
        if(IsLoggedOn)
            GenerateLogout(logoutMessage);
        Disconnect("Resetting...");
        _state.Reset(loggedReason);
    }

    private void InitializeResendFields(Message message)
    {
        FieldMap header = message.Header;
        DateTime sendingTime = header.GetDateTime(Fields.Tags.SendingTime);
        InsertOrigSendingTime(header, sendingTime);
        header.SetField(new Fields.PossDupFlag(true));
        InsertSendingTime(header);
    }

    protected bool ShouldSendReset()
    {
        return string.CompareOrdinal(SessionID.BeginString, FixValues.BeginString.FIX41) >= 0
            && (ResetOnLogon || ResetOnLogout || ResetOnDisconnect)
            && _state.NextSenderMsgSeqNum == 1
            && _state.NextTargetMsgSeqNum == 1;
    }

    protected bool IsCorrectCompId(string senderCompId, string targetCompId)
    {
        if (!CheckCompID)
            return true;
        return SessionID.SenderCompID.Equals(targetCompId)
            && SessionID.TargetCompID.Equals(senderCompId);
    }

    /// TODO - this fn always returns true-- should it ever be false?
    protected bool IsTimeToGenerateLogon()
    {
        return true;
    }

    protected bool IsTargetTooHigh(SeqNumType msgSeqNum)
    {
        return msgSeqNum > _state.NextTargetMsgSeqNum;
    }

    protected bool IsTargetTooLow(SeqNumType msgSeqNum)
    {
        return msgSeqNum < _state.NextTargetMsgSeqNum;
    }

    protected void DoTargetTooHigh(Message msg, SeqNumType msgSeqNum)
    {
        string beginString = msg.Header.GetString(Fields.Tags.BeginString);

        Log.Log(LogLevel.Warning, "MsgSeqNum too high, expecting {NextSeqNum} but received {MsgSeqNum}", _state.NextTargetMsgSeqNum, msgSeqNum);
        _state.Queue(msgSeqNum, msg);
        // §12.6 slot-spill: notify the engine adapter that this frame is now queued so it can
        // spill the correlator's pending CaptureId slot into a keyed store keyed on seqNum.
        TapInboundQueued(msgSeqNum);

        if (IsResendRequested)
        {
            ResendRange range = _state.GetResendRange();

            if (CmeEnhancedResend && !range.IsResendStarted)
            {
                return; // don't send another ResendRequest
            }

            if (!SendRedundantResendRequests && msgSeqNum >= range.BeginSeqNo)
            {
                Log.Log(LogLevel.Information,
                    "Already sent ResendRequest FROM: {BeginSeqNo} TO: {EndSeqNo}. Not sending another",
                    range.BeginSeqNo, range.EndSeqNo);
                return;
            }
        }

        GenerateResendRequest(beginString, msgSeqNum);
    }

    protected void DoTargetTooLow(Message msg, SeqNumType msgSeqNum)
    {
        bool possDupFlag = false;
        if (msg.Header.IsSetField(Fields.Tags.PossDupFlag))
            possDupFlag = msg.Header.GetBoolean(Fields.Tags.PossDupFlag);

        if (!possDupFlag)
        {
            string err = "MsgSeqNum too low, expecting " + _state.NextTargetMsgSeqNum + " but received " + msgSeqNum;
            GenerateLogout(err);
            throw new QuickFIXException(err);
        }

        DoPossDup(msg);
    }

    /// <summary>
    /// Validates a message where PossDupFlag=Y
    /// </summary>
    /// <param name="msg"></param>
    protected void DoPossDup(Message msg)
    {
        // If config RequiresOrigSendingTime=N, then tolerate SequenceReset messages that lack OrigSendingTime (issue #102).
        // (This field doesn't really make sense in this message, so some parties omit it, even though spec requires it.)
        string msgType = msg.Header.GetString(Fields.Tags.MsgType);
        if (msgType == Fields.MsgType.SEQUENCE_RESET && RequiresOrigSendingTime == false)
            return;

        // Reject if messages don't have OrigSendingTime set
        if (!msg.Header.IsSetField(Fields.Tags.OrigSendingTime))
        {
            GenerateReject(msg, FixValues.SessionRejectReason.REQUIRED_TAG_MISSING, Fields.Tags.OrigSendingTime);
            return;
        }

        // Ensure sendingTime is later than OrigSendingTime, else reject and logout
        DateTime origSendingTime = msg.Header.GetDateTime(Fields.Tags.OrigSendingTime);
        DateTime sendingTime = msg.Header.GetDateTime(Fields.Tags.SendingTime);
        System.TimeSpan tmSpan = origSendingTime - sendingTime;

        if (tmSpan.TotalSeconds > 0)
        {
            GenerateReject(msg, FixValues.SessionRejectReason.SENDING_TIME_ACCURACY_PROBLEM);
            GenerateLogout();
        }
    }

    protected void GenerateBusinessMessageReject(Message message, int err, int field)
    {
        string msgType = message.Header.GetString(Tags.MsgType);
        SeqNumType msgSeqNum = message.Header.GetULong(Tags.MsgSeqNum);
        string reason = FixValues.BusinessRejectReason.RejText[err];
        Message reject;
        if (string.CompareOrdinal(SessionID.BeginString, FixValues.BeginString.FIX42) >= 0)
        {
            reject = _msgFactory.Create(SessionID.BeginString, MsgType.BUSINESS_MESSAGE_REJECT);
            reject.SetField(new RefMsgType(msgType));
            reject.SetField(new BusinessRejectReason(err));
        }
        else
        {
            reject = _msgFactory.Create(SessionID.BeginString, MsgType.REJECT);
            char[] reasonArray = reason.ToLower().ToCharArray();
            reasonArray[0] = char.ToUpper(reasonArray[0]);
            reason = new string(reasonArray);
        }
        InitializeHeader(reject);
        reject.SetField(new RefSeqNum(msgSeqNum));
        _state.IncrNextTargetMsgSeqNum();


        reject.SetField(new Text(reason));
        Log.Log(LogLevel.Information, "Reject sent for Message: {MsgSeqNum} Reason: {Reason}", msgSeqNum, reason);
        SendRaw(reject);
    }

    private Message CreateResendRequest(string beginString, SeqNumType startSeqNum, SeqNumType endSeqNum)
    {
        Message resendRequest = _msgFactory.Create(beginString, MsgType.RESEND_REQUEST);
        resendRequest.SetField(new BeginSeqNo(startSeqNum));
        resendRequest.SetField(new EndSeqNo(endSeqNum));
        InitializeHeader(resendRequest);
        return resendRequest;
    }

    internal void GenerateResendRequest(string beginString, SeqNumType msgSeqNum)
    {
        SeqNumType beginSeqNum = _state.NextTargetMsgSeqNum;
        SeqNumType endRangeSeqNum = msgSeqNum - 1;
        SeqNumType endChunkSeqNum;
        if (MaxMessagesInResendRequest > 0)
        {
            endChunkSeqNum = Math.Min(endRangeSeqNum, beginSeqNum + MaxMessagesInResendRequest - 1);
        }
        else
        {
            if (string.CompareOrdinal(beginString, FixValues.BeginString.FIX42) >= 0)
                endRangeSeqNum = 0;
            else if (string.CompareOrdinal(beginString, FixValues.BeginString.FIX41) <= 0)
                endRangeSeqNum = 999999;
            endChunkSeqNum = endRangeSeqNum;
        }

        Message resendRequest = CreateResendRequest(beginString, beginSeqNum, endChunkSeqNum);
        if (EnableLastMsgSeqNumProcessed)
            resendRequest.Header.SetField(new LastMsgSeqNumProcessed(msgSeqNum));

        if (SendRaw(resendRequest))
        {
            Log.Log(LogLevel.Information, "Sent ResendRequest FROM: {BeginSeqNum} TO: {EndChunkSeqNum}",
                beginSeqNum, endChunkSeqNum);
            _state.SetResendRange(beginSeqNum, endRangeSeqNum, msgSeqNum, resendRequest,
                endChunkSeqNum==0 ? ResendRange.NOT_SET : endChunkSeqNum);
            return;
        }

        Log.Log(LogLevel.Error, "Error sending ResendRequest ({BeginSeqNum} ,{EndChunkSeqNum})",
            beginSeqNum, endChunkSeqNum);
    }

    /// <summary>
    /// Create and send a logon
    /// </summary>
    /// <returns>true of logon was successfully sent</returns>
    internal bool GenerateLogon()
    {
        Message logon = _msgFactory.Create(SessionID.BeginString, Fields.MsgType.LOGON);
        logon.SetField(new Fields.EncryptMethod(0));
        logon.SetField(new Fields.HeartBtInt(_state.HeartBtInt));

        if (SessionID.IsFIXT)
            logon.SetField(new Fields.DefaultApplVerID(SenderDefaultApplVerID));
        if (RefreshOnLogon)
            Refresh();
        if (ResetOnLogon)
            _state.Reset("ResetOnLogon");
        if (ShouldSendReset())
            logon.SetField(new Fields.ResetSeqNumFlag(true));

        InitializeHeader(logon);
        _state.LastReceivedTimeDT = DateTime.UtcNow;
        _state.TestRequestCounter = 0;
        _state.SentLogon = SendRaw(logon);
        return _state.SentLogon;
    }

    protected bool GenerateLogon(Message otherLogon)
    {
        Message logon = _msgFactory.Create(SessionID.BeginString, Fields.MsgType.LOGON);
        logon.SetField(new Fields.EncryptMethod(0));
        logon.SetField(new Fields.HeartBtInt(otherLogon.GetInt(Tags.HeartBtInt)));

        if (SessionID.IsFIXT)
            logon.SetField(new Fields.DefaultApplVerID(SenderDefaultApplVerID));
        if (EnableLastMsgSeqNumProcessed)
            logon.Header.SetField(new Fields.LastMsgSeqNumProcessed(otherLogon.Header.GetULong(Tags.MsgSeqNum)));

        InitializeHeader(logon);
        _state.SentLogon = SendRaw(logon);
        return _state.SentLogon;
    }

    public void GenerateTestRequest(string id)
    {
        Message testRequest = _msgFactory.Create(SessionID.BeginString, Fields.MsgType.TEST_REQUEST);
        InitializeHeader(testRequest);
        testRequest.SetField(new Fields.TestReqID(id));
        SendRaw(testRequest);
    }

    /// <summary>
    /// Send a basic Logout message
    /// </summary>
    /// <returns></returns>
    public void GenerateLogout()
    {
        ImplGenerateLogout();
    }

    /// <summary>
    /// Send a Logout message
    /// </summary>
    /// <param name="text">written into the Text field</param>
    /// <returns></returns>
    private void GenerateLogout(string? text)
    {
        ImplGenerateLogout(text: text);
    }

    /// <summary>
    /// Send a Logout message
    /// </summary>
    /// <param name="other">used to fill MsgSeqNum field, if configuration requires it</param>
    /// <returns></returns>
    private void GenerateLogout(Message other)
    {
        ImplGenerateLogout(other: other);
    }

    /// <summary>
    /// Common implementation for variant GenerateLogout() function interfaces
    /// </summary>
    /// <param name="other">used to fill MsgSeqNum field, if configuration requires it; ignored if null</param>
    /// <param name="text">written into the Text field; ignored if empty/null</param>
    /// <returns></returns>
    private void ImplGenerateLogout(Message? other = null, string? text = null)
    {
        Message logout = _msgFactory.Create(SessionID.BeginString, Fields.MsgType.LOGOUT);
        InitializeHeader(logout);
        if (!string.IsNullOrEmpty(text))
            logout.SetField(new Fields.Text(text));
        if (other is not null && EnableLastMsgSeqNumProcessed)
        {
            try
            {
                logout.Header.SetField(new Fields.LastMsgSeqNumProcessed(other.Header.GetULong(Tags.MsgSeqNum)));
            }
            catch (FieldNotFoundException e)
            {
                Log.Log(LogLevel.Error, e, "Error: No message sequence number: {Other}", other);
            }
        }
        _state.SentLogout = SendRaw(logout);
    }

    public void GenerateHeartbeat()
    {
        Message heartbeat = _msgFactory.Create(SessionID.BeginString, Fields.MsgType.HEARTBEAT);
        InitializeHeader(heartbeat);
        SendRaw(heartbeat);
    }

    public void GenerateHeartbeat(Message testRequest)
    {
        Message heartbeat = _msgFactory.Create(SessionID.BeginString, Fields.MsgType.HEARTBEAT);
        InitializeHeader(heartbeat);
        try
        {
            heartbeat.SetField(new Fields.TestReqID(testRequest.GetString(Fields.Tags.TestReqID)));
            if (EnableLastMsgSeqNumProcessed)
            {
                heartbeat.Header.SetField(new Fields.LastMsgSeqNumProcessed(testRequest.Header.GetULong(Tags.MsgSeqNum)));
            }
        }
        catch (FieldNotFoundException)
        { }
        SendRaw(heartbeat);
    }

    // FP Enhancement: 2026-06-08 — thread the offending field value through the MessageBuilder bridge so the value carried by TagException reaches GenerateReject's reject-log event; the producer side was previously unwired (value always defaulted to empty).
    internal void GenerateReject(MessageBuilder msgBuilder, FixValues.SessionRejectReason reason, int field, string value = "")
    {
        GenerateReject(msgBuilder.RejectableMessage(), reason, field, value);
    }

    // FP Enhancement: 2026-05-24 — optional `value` parameter carries the offending field value into the reject log line; backward-compatible (defaults to empty).
    public void GenerateReject(Message message, FixValues.SessionRejectReason reason, int field = 0, string value = "")
    {
        string beginString = SessionID.BeginString;

        Message reject = _msgFactory.Create(beginString, Fields.MsgType.REJECT);
        reject.ReverseRoute(message.Header);
        InitializeHeader(reject);

        string msgType = message.Header.IsSetField(Fields.Tags.MsgType)
            ? message.Header.GetString(Fields.Tags.MsgType) : "";

        SeqNumType msgSeqNum = 0;
        if (message.Header.IsSetField(Fields.Tags.MsgSeqNum))
        {
            try
            {
                msgSeqNum = message.Header.GetULong(Fields.Tags.MsgSeqNum);
                reject.SetField(new Fields.RefSeqNum(msgSeqNum));
            }
            catch (Exception ex)
            {
                Log.Log(LogLevel.Error, ex, "Exception while setting RefSeqNum: {Exception}", ex);
            }
        }

        if (string.CompareOrdinal(beginString, FixValues.BeginString.FIX42) >= 0)
        {
            if (msgType.Length > 0)
                reject.SetField(new Fields.RefMsgType(msgType));
            if ((FixValues.BeginString.FIX42.Equals(beginString) && reason.Value <= FixValues.SessionRejectReason.INVALID_MSGTYPE.Value)
                || string.CompareOrdinal(beginString, FixValues.BeginString.FIX42) > 0)
            {
                reject.SetField(new Fields.SessionRejectReason(reason.Value));
            }
        }
        if (!MsgType.LOGON.Equals(msgType)
          && !MsgType.SEQUENCE_RESET.Equals(msgType)
          && msgSeqNum == _state.NextTargetMsgSeqNum)
        {
            _state.IncrNextTargetMsgSeqNum();
        }

        if (0 != field || FixValues.SessionRejectReason.INVALID_TAG_NUMBER.Equals(reason))
        {
            if (FixValues.SessionRejectReason.INVALID_MSGTYPE.Equals(reason))
            {
                if (string.CompareOrdinal(SessionID.BeginString, FixValues.BeginString.FIX43) >= 0)
                    PopulateRejectReason(reject, reason.Description);
                else
                    PopulateSessionRejectReason(reject, field, reason.Description, false);
            }
            else
                PopulateSessionRejectReason(reject, field, reason.Description, true);

            // FP Enhancement: 2026-05-24 — structured rejection log event (see Log.OnRejectionEvent / LoggerExtensions).
            Log.OnRejectionEvent(LogAssist.RedactSensitiveFields(message.RawMessage, RedactFieldsInLogs, RedactionLogText), $"Message {msgSeqNum} Rejected: {reason.Description} (Field={field}, Value={value})");
            Log.Log(LogLevel.Warning, "Message {MsgSeqNum} Rejected: {Reason} (Field={Field})", msgSeqNum, reason.Description, field);
        }
        else
        {
            PopulateRejectReason(reject, reason.Description);
            // FP Enhancement: 2026-05-24 — structured rejection log event for the no-field branch (see above).
            Log.OnRejectionEvent(LogAssist.RedactSensitiveFields(message.RawMessage, RedactFieldsInLogs, RedactionLogText), $"Message {msgSeqNum} Rejected: {reason.Value}");
            Log.Log(LogLevel.Error, "Message {MsgSeqNum} Rejected: {Reason}", msgSeqNum, reason.Value);
        }

        if (!_state.ReceivedLogon)
            throw new QuickFIXException("Tried to send a reject while not logged on");

        SendRaw(reject);
    }

    protected void PopulateSessionRejectReason(Message reject, int field, string text, bool includeFieldInfo)
    {
        if (string.CompareOrdinal(SessionID.BeginString, FixValues.BeginString.FIX42) >= 0)
        {
            reject.SetField(new Fields.RefTagID(field));
            reject.SetField(new Fields.Text(text));
        }
        else
        {
            if (includeFieldInfo)
                reject.SetField(new Fields.Text(text + " (" + field + ")"));
            else
                reject.SetField(new Fields.Text(text));
        }
    }

    protected void PopulateRejectReason(Message reject, string text)
    {
        reject.SetField(new Fields.Text(text));
    }

    // FP Enhancement: 2026-05-24 — fan an inbound session-level Reject out to IApplicationMessageRejection.OnMessageRejected, attaching the original outbound message looked up via RefSeqNum. Exceptions inside the callback are caught and logged so a buggy handler can't tear down the session.
    private void NotifyMessageRejected(Message rejectMessage, string reason)
    {
        if (!_appHandlesRejection)
            return;

        try
        {
            Message? originalMessage = null;
            bool referencedMessageWasResent = false;

            if (rejectMessage.IsSetField(Tags.RefSeqNum))
            {
                // FP Enhancement: 2026-06-08 — read RefSeqNum as ulong (GetULong) rather than narrowing a 64-bit sequence number through 32-bit int, which truncated above int.MaxValue.
                SeqNumType refSeqNum = rejectMessage.GetULong(Tags.RefSeqNum);
                // FP Enhancement: 2026-08-10 — surface whether RefSeqNum identifies a successful outbound retransmission without changing original-message reconstruction.
                referencedMessageWasResent = WasResentOutbound(refSeqNum);
                var messages = new List<string>();
                _state.Get(refSeqNum, refSeqNum, messages);

                if (messages.Count > 0)
                {
                    originalMessage = new Message();
                    originalMessage.FromString(messages[0], false, SessionDataDictionary, ApplicationDataDictionary, _msgFactory, ignoreBody: false);
                }
            }

            // FP Enhancement: 2026-08-10 — pass the explicit resend signal through the source-compatible rejection callback overload.
            ((IApplicationMessageRejection)Application).OnMessageRejected(
                rejectMessage, originalMessage, SessionID, reason, referencedMessageWasResent);
        }
        catch (Exception ex)
        {
            Log.Log(LogLevel.Error, ex, "Error in OnMessageRejected callback: {Message}", ex.Message);
        }
    }

    // FP Enhancement: 2026-08-10 — serialize bounded FIFO history operations behind the session lock, then the history lock, so lifecycle clears cannot race resend recording.
    private void RecordResentOutbound(SeqNumType msgSeqNum)
    {
        lock (_sync)
        {
            lock (_resentOutboundHistorySync)
            {
                if (!_resentOutboundHistory.Add(msgSeqNum))
                    return;

                _resentOutboundHistoryOrder.Enqueue(msgSeqNum);
                if (_resentOutboundHistoryOrder.Count > ResentOutboundHistoryCapacity)
                    _resentOutboundHistory.Remove(_resentOutboundHistoryOrder.Dequeue());
            }
        }
    }

    private bool WasResentOutbound(SeqNumType msgSeqNum)
    {
        lock (_sync)
        {
            lock (_resentOutboundHistorySync)
                return _resentOutboundHistory.Contains(msgSeqNum);
        }
    }

    private void ClearResentOutboundHistory()
    {
        lock (_sync)
        {
            lock (_resentOutboundHistorySync)
            {
                _resentOutboundHistory.Clear();
                _resentOutboundHistoryOrder.Clear();
            }
        }
    }

    // FP Enhancement: 2026-05-24 — emit XML + JSON renderings of tracked messages via Log.OnIncomingAndOutgoing. Tracked messages are those whose rendered string is present in SessionID.SessionLogIdentifiers (populated by application code).
    // FP Enhancement: 2026-06-08 — render the message string once and do a single atomic TryGetValue. The prior ContainsKey-then-indexer pair was a TOCTOU on the ConcurrentDictionary: if application code removed the entry between the two calls, the indexer threw KeyNotFoundException onto the send/receive path.
    private void LogExtended(Message message, string msgType, bool isOutbound)
    {
        if (Message.IsAdminMsgType(msgType)) return;
        if (SessionID.SessionLogIdentifiers.IsEmpty) return;

        (int LogId, DateTime CaptureTime) tracked = default;
        bool found = false;
        ulong seqNum = 0;

        if (message.Header.IsSetField(Tags.MsgSeqNum))
        {
            seqNum = message.Header.GetULong(Tags.MsgSeqNum);
            if (_resentTracker.TryGetValue((isOutbound, seqNum), out tracked))
            {
                found = true;
            }
        }

        string? rendered = null;
        if (!found)
        {
            rendered = message.ToString();
            if (rendered != null && SessionID.SessionLogIdentifiers.TryGetValue(rendered, out tracked))
            {
                found = true;
                if (seqNum != 0)
                {
                    _resentTracker[(isOutbound, seqNum)] = tracked;
                }
            }
        }

        if (found)
        {
            rendered ??= message.ToString();
            Log.OnIncomingAndOutgoing((tracked.LogId, rendered, message.ToXML(), message.ToJSON()));
        }
    }

    protected void InitializeHeader(Message m, SeqNumType msgSeqNum = 0)
    {
        _state.LastSentTimeDT = DateTime.UtcNow;
        m.Header.SetField(new Fields.BeginString(SessionID.BeginString));
        m.Header.SetField(new Fields.SenderCompID(SessionID.SenderCompID));
        if (SessionID.IsSet(SessionID.SenderSubID))
            m.Header.SetField(new Fields.SenderSubID(SessionID.SenderSubID));
        if (SessionID.IsSet(SessionID.SenderLocationID))
            m.Header.SetField(new Fields.SenderLocationID(SessionID.SenderLocationID));
        m.Header.SetField(new Fields.TargetCompID(SessionID.TargetCompID));
        if (SessionID.IsSet(SessionID.TargetSubID))
            m.Header.SetField(new Fields.TargetSubID(SessionID.TargetSubID));
        if (SessionID.IsSet(SessionID.TargetLocationID))
            m.Header.SetField(new Fields.TargetLocationID(SessionID.TargetLocationID));

        if (msgSeqNum > 0)
            m.Header.SetField(new Fields.MsgSeqNum(msgSeqNum));
        else
            m.Header.SetField(new Fields.MsgSeqNum(_state.NextSenderMsgSeqNum));

        if (EnableLastMsgSeqNumProcessed && !m.Header.IsSetField(Tags.LastMsgSeqNumProcessed))
            m.Header.SetField(new LastMsgSeqNumProcessed(NextTargetMsgSeqNum - 1));

        InsertSendingTime(m.Header);
    }

    private bool IsFix42OrAbove() {
        return SessionID.BeginString == FixValues.BeginString.FIXT11
            || string.CompareOrdinal(SessionID.BeginString, FixValues.BeginString.FIX42) >= 0;
    }

    protected void InsertSendingTime(FieldMap header)
    {
        header.SetField(new Fields.SendingTime(
            DateTime.UtcNow, IsFix42OrAbove() ? TimeStampPrecision : TimeStampPrecision.Second ) );
    }

    protected void Persist(Message message, string messageString)
    {
        if (PersistMessages)
        {
            SeqNumType msgSeqNum = message.Header.GetULong(Fields.Tags.MsgSeqNum);
            _state.SetAndIncrNextSenderMsgSeqNum(msgSeqNum, messageString);
        }
        else
        {
            _state.IncrNextSenderMsgSeqNum();
        }
    }

    protected bool IsGoodTime(Message msg)
    {
        if (!CheckLatency)
            return true;

        var sendingTime = msg.Header.GetDateTime(Fields.Tags.SendingTime);
        TimeSpan tmSpan = DateTime.UtcNow - sendingTime;
        return Math.Abs(tmSpan.TotalSeconds) <= MaxLatency;
    }

    private void GenerateSequenceReset(Message receivedMessage, SeqNumType beginSeqNo, SeqNumType endSeqNo)
    {
        string beginString = SessionID.BeginString;
        Message sequenceReset = _msgFactory.Create(beginString, Fields.MsgType.SEQUENCE_RESET);
        InitializeHeader(sequenceReset);
        SeqNumType newSeqNo = endSeqNo;
        sequenceReset.Header.SetField(new PossDupFlag(true));
        InsertOrigSendingTime(sequenceReset.Header, sequenceReset.Header.GetDateTime(Tags.SendingTime));

        sequenceReset.Header.SetField(new MsgSeqNum(beginSeqNo));
        sequenceReset.SetField(new NewSeqNo(newSeqNo));
        sequenceReset.SetField(new GapFillFlag(true));
        if (EnableLastMsgSeqNumProcessed)
        {
            try
            {
                sequenceReset.Header.SetField(new Fields.LastMsgSeqNumProcessed(receivedMessage.Header.GetULong(Tags.MsgSeqNum)));
            }
            catch (FieldNotFoundException e)
            {
                Log.Log(LogLevel.Error, e, "Error: Received message without MsgSeqNum: {ReceivedMessage}",
                    receivedMessage);
            }
        }
        SendRaw(sequenceReset, beginSeqNo);
        Log.Log(LogLevel.Information, "Sent SequenceReset TO: {NewSeqNo}", newSeqNo);
    }

    protected void InsertOrigSendingTime(FieldMap header, DateTime sendingTime)
    {
        header.SetField(new OrigSendingTime(
            sendingTime, IsFix42OrAbove() ? TimeStampPrecision : TimeStampPrecision.Second ) );
    }

    protected void NextQueued()
    {
        while (NextQueued(_state.MessageStore.NextTargetMsgSeqNum))
        {
            // continue
        }
    }

    protected bool NextQueued(SeqNumType num)
    {
        Message? msg = _state.Dequeue(num);

        if (msg is not null)
        {
            Log.Log(LogLevel.Information, "Processing queued message: {Num}", num);

            string msgType = msg.Header.GetString(Tags.MsgType);
            if (msgType.Equals(MsgType.LOGON) || msgType.Equals(MsgType.RESEND_REQUEST))
            {
                // §12.6: notify the adapter to discard the spilled slot for this seqNum — the
                // LOGON/RESEND skip branch does not call NextMessage, so FromApp will never take it.
                TapInboundReplayPrepare(num);
                _state.IncrNextTargetMsgSeqNum();
            }
            else
            {
                // §12.6 slot-spill restore: notify the adapter to move the original CaptureId from
                // its keyed store back into the correlator slot BEFORE NextMessage, so FromApp reads
                // the correct id for this replayed frame without a second OnInbound call (no double
                // capture row). Replaces the earlier A-F1 re-tap approach.
                TapInboundReplayPrepare(num);
                var reconstructed = msg.ConstructString();
                // Set the queued flag BEFORE NextMessage so the replayed message's own Verify sees it
                // (the #309 too-low SequenceReset-GapFill "obey anyway" case at Verify).
                _state.LastProcessedMessageWasQueued = true;
                NextMessage(reconstructed);
            }
            return true;
        }
        return false;
    }

    private static bool IsAdminMessage(Message msg)
    {
        var msgType = msg.Header.GetString(Fields.Tags.MsgType);
        return AdminMsgTypes.Contains(msgType);
    }

    /// <summary>
    /// Update the header as needed and send the message
    /// </summary>
    /// <param name="message"></param>
    /// <param name="seqNum">if non-zero, set this seqno in the message (else leave existing value alone)</param>
    /// <returns></returns>
    protected bool SendRaw(Message message, SeqNumType seqNum = 0UL)
    {
        lock (_sync)
        {
            string msgType = message.Header.GetString(Fields.Tags.MsgType);

            InitializeHeader(message, seqNum);

            if (Message.IsAdminMsgType(msgType))
            {
                try
                {
                    Application.ToAdmin(message, SessionID);
                }
                catch (DoNotSend)
                {
                    return false;
                }

                if (MsgType.LOGON.Equals(msgType) && !_state.ReceivedReset)
                {
                    Fields.ResetSeqNumFlag resetSeqNumFlag = new Fields.ResetSeqNumFlag(false);
                    if (message.IsSetField(resetSeqNumFlag))
                        message.GetField(resetSeqNumFlag);
                    if (resetSeqNumFlag.Value)
                    {
                        _state.Reset("ResetSeqNumFlag");
                        message.Header.SetField(new Fields.MsgSeqNum(_state.NextSenderMsgSeqNum));
                    }
                    _state.SentReset = resetSeqNumFlag.Value;
                }
            }
            else
            {
                try
                {
                    Application.ToApp(message, SessionID);
                }
                catch (DoNotSend)
                {
                    return false;
                }
            }

            string messageString = message.ConstructString();
            if (0UL == seqNum)
                Persist(message, messageString);

            // FP Enhancement: 2026-05-24 — mirror the incoming LogExtended hook for outbound messages so tracked sends are emitted via Log.OnIncomingAndOutgoing.
            var result = Send(messageString);
            LogExtended(message, msgType, isOutbound: true);
            return result;
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public bool Disposed { get; private set; } = false;

    protected virtual void Dispose(bool disposing)
    {
        if (Disposed)
            return;
        if (disposing)
        {
            _state.Dispose();
            lock (Sessions)
            {
                Sessions.Remove(SessionID);
            }
        }
        Disposed = true;
    }

    ~Session() => Dispose(false);
}
