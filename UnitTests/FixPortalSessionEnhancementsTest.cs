using System;
using System.Collections.Generic;
using NUnit.Framework;
using QuickFix;
using QuickFix.Fields;
using QuickFix.Logger;
using QuickFix.Store;

namespace UnitTests;

// Coverage for FixPortal session-level enhancements that previously had no tests:
//  - IApplicationMessageRejection.OnMessageRejected dispatch on inbound 35=3 (Session.cs:1235-1239,1696-1722)
//  - Logout (35=5) tolerated before logon completes (Session.cs:684-688)
// Each test is revert-sensitive: it fails if the FP enhancement is removed.

[TestFixture]
public class FixPortalRejectionCallbackTest
{
    private sealed class RejectionCapturingApplication : IApplication, IApplicationMessageRejection
    {
        public int Calls;
        public Message? Reject;
        public Message? Original;
        public string? Reason;
        public bool? ReferencedMessageWasResent;
        public List<bool> ResentSignals = new();

        public void OnMessageRejected(Message rejectMessage, Message? originalMessage, SessionID sessionId, string reason)
            => Capture(rejectMessage, originalMessage, reason, false);

        public void OnMessageRejected(
            Message rejectMessage,
            Message? originalMessage,
            SessionID sessionId,
            string reason,
            bool referencedMessageWasResent)
            => Capture(rejectMessage, originalMessage, reason, referencedMessageWasResent);

        private void Capture(Message rejectMessage, Message? originalMessage, string reason, bool referencedMessageWasResent)
        {
            Calls++;
            Reject = rejectMessage;
            Original = originalMessage;
            Reason = reason;
            ReferencedMessageWasResent = referencedMessageWasResent;
            ResentSignals.Add(referencedMessageWasResent);
        }

        public void ToAdmin(Message message, SessionID sessionId) { }
        public void FromAdmin(Message message, SessionID sessionId) { }
        public void ToApp(Message message, SessionID sessionId) { }
        public void FromApp(Message message, SessionID sessionId) { }
        public void OnCreate(SessionID sessionId) { }
        public void OnLogout(SessionID sessionId) { }
        public void OnLogon(SessionID sessionId) { }
    }

    private RejectionCapturingApplication _app = new();
    private SessionTestSupport.MockResponder _responder = new();
    private SessionID _sessionId = new("FIX.4.2", "RJ_SENDER", "RJ_TARGET");
    private Session _session = null!;
    private SeqNumType _seq = 1;

    [TearDown]
    public void TearDown() => _session?.Dispose();

    [SetUp]
    public void Setup()
    {
        _app = new RejectionCapturingApplication();
        _responder = new SessionTestSupport.MockResponder();
        _sessionId = new SessionID("FIX.4.2", "RJ_SENDER", "RJ_TARGET");

        var config = new SettingsDictionary();
        config.SetString(SessionSettings.CONNECTION_TYPE, "acceptor");
        config.SetString(SessionSettings.START_TIME, "00:00:00");
        config.SetString(SessionSettings.END_TIME, "00:00:00");

        var logFactory = new LogFactoryAdapter(new NullLogFactory());
        _session = new Session(false, _app, new MemoryStoreFactory(), _sessionId,
            new DataDictionaryProvider(), new SessionSchedule(config), 0, logFactory,
            new DefaultMessageFactory(), "blah");
        _session.SetResponder(_responder);
        _session.PersistMessages = true;
        _session.CheckLatency = false;
        _seq = 1;

        // Bring the session up so admin processing (and the reject dispatch) runs.
        var logon = new QuickFix.FIX42.Logon();
        StampInbound(logon);
        logon.SetField(new HeartBtInt(1));
        _session.Next(logon.ConstructString());
    }

    private void StampInbound(Message msg, SeqNumType? sequenceNumber = null)
    {
        msg.Header.SetField(new TargetCompID(_sessionId.SenderCompID));
        msg.Header.SetField(new SenderCompID(_sessionId.TargetCompID));
        var sequence = sequenceNumber ?? _seq;
        msg.Header.SetField(new MsgSeqNum(sequence));
        msg.Header.SetField(new SendingTime(DateTime.UtcNow));
        _seq = sequence + 1;
    }

    private SeqNumType SendPersistedOrder(string clOrdId)
    {
        var order = new QuickFix.FIX42.NewOrderSingle(
            new ClOrdID(clOrdId), new HandlInst(HandlInst.MANUAL_ORDER), new Symbol("IBM"),
            new Side(Side.BUY), new TransactTime(), new OrdType(OrdType.MARKET));

        Assert.That(_session.Send(order), Is.True, "the original outbound application message should be sent");
        return order.Header.GetULong(Tags.MsgSeqNum);
    }

    private void RequestResend(SeqNumType sequenceNumber)
    {
        var resendRequest = new QuickFix.FIX42.ResendRequest(
            new BeginSeqNo(sequenceNumber), new EndSeqNo(sequenceNumber));
        StampInbound(resendRequest);
        _session.Next(resendRequest.ConstructString());
    }

    private void ReceiveReject(SeqNumType referencedSequenceNumber)
    {
        var reject = new QuickFix.FIX42.Reject(new RefSeqNum(referencedSequenceNumber));
        StampInbound(reject);
        _session.Next(reject.ConstructString());
    }

    [Test]
    public void InboundReject_DispatchesOnMessageRejected_WithReason()
    {
        Assume.That(_session.IsLoggedOn, Is.True, "session should be logged on after the setup logon");

        var reject = new QuickFix.FIX42.Reject(new RefSeqNum(1));
        reject.SetField(new Text("Bad message, rejected by counterparty"));
        StampInbound(reject);
        _session.Next(reject.ConstructString());

        Assert.That(_app.Calls, Is.EqualTo(1), "OnMessageRejected should fire exactly once for an inbound 35=3");
        Assert.That(_app.Reason, Is.EqualTo("Bad message, rejected by counterparty"));
        Assert.That(_app.Reject, Is.Not.Null);
        Assert.That(_app.Reject!.Header.GetString(Tags.MsgType), Is.EqualTo(MsgType.REJECT));
    }

    [Test]
    public void InboundReject_WithoutText_DispatchesDefaultReason()
    {
        var reject = new QuickFix.FIX42.Reject(new RefSeqNum(1));
        StampInbound(reject);
        _session.Next(reject.ConstructString());

        Assert.That(_app.Calls, Is.EqualTo(1));
        Assert.That(_app.Reason, Is.EqualTo("Session-level reject received"));
    }

    [Test]
    public void InboundReject_OfResentOutboundFrame_IsMarkedResent()
    {
        var sequenceNumber = SendPersistedOrder("RESENT-1");
        RequestResend(sequenceNumber);
        ReceiveReject(sequenceNumber);

        Assert.That(_app.ReferencedMessageWasResent, Is.True);
        Assert.That(_app.Original, Is.Not.Null);
        Assert.That(_app.Original!.Header.IsSetField(Tags.PossDupFlag), Is.False,
            "the reconstructed original must remain the pre-resend stored frame");
    }

    [Test]
    public void InboundReject_OfFirstTransmission_IsNotMarkedResent()
    {
        var sequenceNumber = SendPersistedOrder("FIRST-TRANSMISSION");
        ReceiveReject(sequenceNumber);

        Assert.That(_app.ReferencedMessageWasResent, Is.False);
    }

    [Test]
    public void ResentOutboundHistory_EvictsTheOldestSequenceNumber()
    {
        SeqNumType oldestSequenceNumber = 0;
        SeqNumType newestSequenceNumber = 0;

        for (var i = 0; i <= Session.ResentOutboundHistoryCapacity; i++)
        {
            var sequenceNumber = SendPersistedOrder($"FIFO-{i}");
            RequestResend(sequenceNumber);

            if (i == 0)
                oldestSequenceNumber = sequenceNumber;
            newestSequenceNumber = sequenceNumber;
        }

        ReceiveReject(oldestSequenceNumber);
        ReceiveReject(newestSequenceNumber);

        Assert.That(_app.ResentSignals[^2], Is.False);
        Assert.That(_app.ResentSignals[^1], Is.True);
    }

    [Test]
    public void ResentOutboundHistory_IsClearedOnDisconnect()
    {
        var sequenceNumber = SendPersistedOrder("DISCONNECT");
        RequestResend(sequenceNumber);

        _session.Disconnect("test recyclable sequence boundary");
        _session.SetResponder(_responder);
        var logon = new QuickFix.FIX42.Logon();
        StampInbound(logon);
        logon.SetField(new HeartBtInt(1));
        _session.Next(logon.ConstructString());
        ReceiveReject(sequenceNumber);

        Assert.That(_app.ReferencedMessageWasResent, Is.False);
    }

    [Test]
    public void ResentOutboundHistory_IsClearedWhenPeerResetsSequenceNumbers()
    {
        var sequenceNumber = SendPersistedOrder("PEER-RESET");
        RequestResend(sequenceNumber);

        var resetLogon = new QuickFix.FIX42.Logon();
        resetLogon.SetField(new HeartBtInt(1));
        resetLogon.SetField(new ResetSeqNumFlag(true));
        StampInbound(resetLogon, 1);
        _session.Next(resetLogon.ConstructString());
        ReceiveReject(sequenceNumber);

        Assert.That(_app.ReferencedMessageWasResent, Is.False);
    }

    [Test]
    public void ResentOutboundHistory_IsClearedWhenAcceptorResetsOnLogon()
    {
        var sequenceNumber = SendPersistedOrder("ACCEPTOR-RESET");
        RequestResend(sequenceNumber);

        _session.ResetOnLogon = true;
        var logon = new QuickFix.FIX42.Logon();
        logon.SetField(new HeartBtInt(1));
        StampInbound(logon, 1);
        _session.Next(logon.ConstructString());
        ReceiveReject(sequenceNumber);

        Assert.That(_app.ReferencedMessageWasResent, Is.False);
    }

    [Test]
    public void ResentOutboundHistory_DoesNotRecordFailedResends()
    {
        var sequenceNumber = SendPersistedOrder("FAILED-RESEND");
        _session.SetResponder(new FailingResponder());
        RequestResend(sequenceNumber);
        _session.SetResponder(_responder);
        ReceiveReject(sequenceNumber);

        Assert.That(_app.ReferencedMessageWasResent, Is.False);
    }

    private sealed class FailingResponder : IResponder
    {
        public bool Send(string message) => false;
        public void Disconnect() { }
    }

    private sealed class LegacyRejectionCapturingApplication : IApplication, IApplicationMessageRejection
    {
        public int Calls;
        public string? Reason;

        public void OnMessageRejected(Message rejectMessage, Message? originalMessage, SessionID sessionId, string reason)
        {
            Calls++;
            Reason = reason;
        }

        public void ToAdmin(Message message, SessionID sessionId) { }
        public void FromAdmin(Message message, SessionID sessionId) { }
        public void ToApp(Message message, SessionID sessionId) { }
        public void FromApp(Message message, SessionID sessionId) { }
        public void OnCreate(SessionID sessionId) { }
        public void OnLogout(SessionID sessionId) { }
        public void OnLogon(SessionID sessionId) { }
    }

    private sealed class LockYieldingResponder : IResponder, IDisposable
    {
        private readonly object _sessionSync;

        public LockYieldingResponder(object sessionSync) => _sessionSync = sessionSync;

        public System.Threading.ManualResetEventSlim SendStarted { get; } = new(false);
        public System.Threading.ManualResetEventSlim SyncReleased { get; } = new(false);
        public System.Threading.ManualResetEventSlim AllowSendReturn { get; } = new(false);
        public bool OuterSessionLockRetained { get; private set; }

        public bool Send(string message)
        {
            SendStarted.Set();
            System.Threading.Monitor.Exit(_sessionSync);
            OuterSessionLockRetained = System.Threading.Monitor.IsEntered(_sessionSync);
            SyncReleased.Set();
            Assert.That(AllowSendReturn.Wait(TimeSpan.FromSeconds(10)), Is.True,
                "the test must release the blocked resend");
            System.Threading.Monitor.Enter(_sessionSync);
            return true;
        }

        public void Disconnect() { }

        public void Dispose()
        {
            SendStarted.Dispose();
            SyncReleased.Dispose();
            AllowSendReturn.Dispose();
        }
    }

    private static object GetSessionSync(Session session)
        => typeof(Session).GetField("_sync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(session)!;

    [Test]
    public void FiveArgumentCallback_DefaultBody_DelegatesToLegacyImplementation()
    {
        var legacyApplication = new LegacyRejectionCapturingApplication();
        IApplicationMessageRejection callback = legacyApplication;

        callback.OnMessageRejected(new Message(), null, _sessionId, "legacy callback", referencedMessageWasResent: true);

        Assert.That(legacyApplication.Calls, Is.EqualTo(1));
        Assert.That(legacyApplication.Reason, Is.EqualTo("legacy callback"));
    }

    [Test]
    public void ResentOutboundHistory_DisconnectAfterBlockedResend_ClearsRecordedSequenceNumber()
    {
        var sequenceNumber = SendPersistedOrder("RESEND-DISCONNECT-RACE");
        using var responder = new LockYieldingResponder(GetSessionSync(_session));
        using var disconnectStarted = new System.Threading.ManualResetEventSlim(false);
        _session.SetResponder(responder);

        var resendTask = System.Threading.Tasks.Task.Run(() => RequestResend(sequenceNumber));
        Assert.That(responder.SendStarted.Wait(TimeSpan.FromSeconds(10)), Is.True,
            "the resend must reach the responder");

        var disconnectTask = System.Threading.Tasks.Task.Run(() =>
        {
            disconnectStarted.Set();
            _session.Disconnect("concurrent resend lifecycle boundary");
        });
        Assert.That(disconnectStarted.Wait(TimeSpan.FromSeconds(10)), Is.True,
            "the disconnect task must start");

        Assert.That(responder.SyncReleased.Wait(TimeSpan.FromSeconds(10)), Is.True,
            "the responder must expose the recursive lock state");
        responder.AllowSendReturn.Set();
        Assert.That(resendTask.Wait(TimeSpan.FromSeconds(10)), Is.True,
            "the resend task must complete");
        Assert.That(disconnectTask.Wait(TimeSpan.FromSeconds(10)), Is.True,
            "the disconnect task must complete");

        Assert.That(responder.OuterSessionLockRetained, Is.True,
            "resend send and record must retain the session lock as one lifecycle operation");

        _session.SetResponder(_responder);
        var logon = new QuickFix.FIX42.Logon();
        logon.SetField(new HeartBtInt(1));
        StampInbound(logon);
        _session.Next(logon.ConstructString());
        ReceiveReject(sequenceNumber);

        Assert.That(_app.ReferencedMessageWasResent, Is.False);
    }
}

[TestFixture]
public class FixPortalLogoutBeforeLogonTest : SessionTestBase
{
    [SetUp]
    public void Setup() => BaseSetup();

    [Test]
    public void Logout_ArrivingBeforeLogon_IsAnsweredNotAbruptlyDropped()
    {
        // _session is a fresh acceptor, never logged on. Upstream would hit the
        // "received msgtype when not logged on" abrupt Disconnect; the FP enhancement
        // (Session.cs:684-688) routes a 35=5 to NextLogout, which answers with a Logout.
        var logout = new QuickFix.FIX42.Logout();
        logout.Header.SetField(new TargetCompID(_sessionId.SenderCompID));
        logout.Header.SetField(new SenderCompID(_sessionId.TargetCompID));
        logout.Header.SetField(new MsgSeqNum(1));
        logout.Header.SetField(new SendingTime(DateTime.UtcNow));
        _session!.Next(logout.ConstructString());

        Assert.That(SENT_LOGOUT(), Is.True, "a logout-before-logon should be answered with a Logout, not silently dropped");
    }
}

// FP Enhancement: 2026-08-16 — the application must be able to tell an intentional
// logout from an unexpected drop. Session.Disconnect(reason) knows which it is; before
// this enhancement it discarded the reason and IApplication.OnLogout saw nothing.
// Both tests are revert-sensitive: remove the stamp and they fail.
[TestFixture]
public class FixPortalDisconnectReasonTest
{
    private sealed class ReasonCapturingApplication : IApplication
    {
        public Session? Session;
        public int LogoutCalls;
        public string? SeenDisconnectReason;
        public string? SeenLogoutReason;

        public void OnLogout(SessionID sessionId)
        {
            LogoutCalls++;
            SeenDisconnectReason = Session?.LastDisconnectReason;
            SeenLogoutReason = Session?.LogoutReason;
        }

        public void ToAdmin(Message message, SessionID sessionId) { }
        public void FromAdmin(Message message, SessionID sessionId) { }
        public void ToApp(Message message, SessionID sessionId) { }
        public void FromApp(Message message, SessionID sessionId) { }
        public void OnCreate(SessionID sessionId) { }
        public void OnLogon(SessionID sessionId) { }
    }

    private ReasonCapturingApplication _app = new();
    private SessionTestSupport.MockResponder _responder = new();
    private SessionID _sessionId = new("FIX.4.2", "DR_SENDER", "DR_TARGET");
    private Session _session = null!;

    [TearDown]
    public void TearDown() => _session?.Dispose();

    [SetUp]
    public void Setup()
    {
        _app = new ReasonCapturingApplication();
        _responder = new SessionTestSupport.MockResponder();
        _sessionId = new SessionID("FIX.4.2", "DR_SENDER", "DR_TARGET");

        var config = new SettingsDictionary();
        config.SetString(SessionSettings.CONNECTION_TYPE, "acceptor");
        config.SetString(SessionSettings.START_TIME, "00:00:00");
        config.SetString(SessionSettings.END_TIME, "00:00:00");

        var logFactory = new LogFactoryAdapter(new NullLogFactory());
        _session = new Session(false, _app, new MemoryStoreFactory(), _sessionId,
            new DataDictionaryProvider(), new SessionSchedule(config), 0, logFactory,
            new DefaultMessageFactory(), "blah");
        _app.Session = _session;
        _session.SetResponder(_responder);
        _session.PersistMessages = true;
        _session.CheckLatency = false;

        // Bring the session up so Disconnect actually dispatches OnLogout
        // (Session.cs guards it on ReceivedLogon || SentLogon).
        var logon = new QuickFix.FIX42.Logon();
        logon.Header.SetField(new TargetCompID(_sessionId.SenderCompID));
        logon.Header.SetField(new SenderCompID(_sessionId.TargetCompID));
        logon.Header.SetField(new MsgSeqNum(1));
        logon.Header.SetField(new SendingTime(DateTime.UtcNow));
        logon.SetField(new HeartBtInt(1));
        _session.Next(logon.ConstructString());
    }

    [Test]
    public void Disconnect_ExposesItsReasonToTheApplication()
    {
        Assert.That(_session.IsLoggedOn, Is.True, "session should be logged on after the setup logon");

        _session.Disconnect("Timed out waiting for heartbeat");

        Assert.That(_app.LogoutCalls, Is.EqualTo(1), "OnLogout should fire once for a logged-on session");
        Assert.That(_app.SeenDisconnectReason, Is.EqualTo("Timed out waiting for heartbeat"),
            "the application must see the engine's own disconnect reason during OnLogout");
    }

    [Test]
    public void OperatorLogoutReason_IsStillVisibleDuringOnLogout()
    {
        Assert.That(_session.IsLoggedOn, Is.True, "session should be logged on after the setup logon");

        _session.Logout("taken out of service by operator");
        _session.Disconnect("Logout request");

        Assert.That(_app.SeenLogoutReason, Is.EqualTo("taken out of service by operator"),
            "LogoutReason is cleared only after OnLogout, so it must still be readable inside it");
    }

    // FP Enhancement: 2026-08-16 — documents a known gap, not a fix: Logout() called with
    // its default argument ("") is indistinguishable from Logout() never having been
    // called at all — LogoutReason reads "" during OnLogout either way. Deliberate
    // operator intent is not tracked independently of the reason string; this is
    // upstream's pre-existing `Logout(string reason = "")` signature and is out of scope
    // to change here. This test exists so a future change to that behaviour is caught.
    [Test]
    public void Logout_CalledWithDefaultArgument_LeavesLogoutReasonEmptyDuringOnLogout()
    {
        Assert.That(_session.IsLoggedOn, Is.True, "session should be logged on after the setup logon");

        _session.Logout();
        _session.Disconnect("Logout request");

        Assert.That(_app.SeenLogoutReason, Is.EqualTo(""),
            "Logout() with its default argument is currently indistinguishable from Logout() never being called");
    }

    // FP Enhancement: 2026-08-16 — LastDisconnectReason must not leak across a reconnect;
    // a diagnostics reader on a freshly logged-on session must not see the previous
    // incarnation's disconnect reason. Revert-sensitive: without clearing it in
    // SetResponder(), this fails with the stale "first drop" reason still present.
    // SetResponder is the real reconnect entry point — it's what SocketReader
    // (acceptor) and SocketInitiatorThread (initiator) both call on every fresh
    // connection, before any inbound message is processed. Session.Logon() is NOT
    // called on reconnect — it only runs once at engine start, so it is deliberately
    // not exercised here.
    [Test]
    public void SecondLogon_ClearsThePreviousDisconnectReason()
    {
        Assert.That(_session.IsLoggedOn, Is.True, "session should be logged on after the setup logon");

        _session.Disconnect("first drop");
        _session.SetResponder(_responder);

        var logon = new QuickFix.FIX42.Logon();
        logon.Header.SetField(new TargetCompID(_sessionId.SenderCompID));
        logon.Header.SetField(new SenderCompID(_sessionId.TargetCompID));
        logon.Header.SetField(new MsgSeqNum(2));
        logon.Header.SetField(new SendingTime(DateTime.UtcNow));
        logon.SetField(new HeartBtInt(1));
        _session.Next(logon.ConstructString());

        Assert.That(_session.IsLoggedOn, Is.True, "session should be logged on again after the second logon");
        Assert.That(_session.LastDisconnectReason, Is.EqualTo(""),
            "a freshly logged-on session must not report the previous incarnation's disconnect reason");
    }

    // FP Enhancement: 2026-08-16 — a second disconnect must report its own reason, not the
    // first one. This does not depend on the Logon-clear fix above: the stamp in Disconnect
    // is unconditional on every call, so this test stays green whether or not LastDisconnectReason
    // is cleared on logon in between — it only pins that the SECOND disconnect's reason wins.
    [Test]
    public void SecondDisconnect_ReportsItsOwnReasonNotTheFirst()
    {
        Assert.That(_session.IsLoggedOn, Is.True, "session should be logged on after the setup logon");

        _session.Disconnect("A");
        _session.SetResponder(_responder);

        var logon = new QuickFix.FIX42.Logon();
        logon.Header.SetField(new TargetCompID(_sessionId.SenderCompID));
        logon.Header.SetField(new SenderCompID(_sessionId.TargetCompID));
        logon.Header.SetField(new MsgSeqNum(2));
        logon.Header.SetField(new SendingTime(DateTime.UtcNow));
        logon.SetField(new HeartBtInt(1));
        _session.Next(logon.ConstructString());
        Assert.That(_session.IsLoggedOn, Is.True, "session should be logged on again after the second logon");

        _session.Disconnect("B");

        Assert.That(_app.LogoutCalls, Is.EqualTo(2), "OnLogout should fire once per logged-on disconnect");
        Assert.That(_app.SeenDisconnectReason, Is.EqualTo("B"),
            "the second disconnect's reason must overwrite the first, not persist it");
    }
}
