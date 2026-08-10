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
        config.SetBool(SessionSettings.PERSIST_MESSAGES, true);
        config.SetString(SessionSettings.CONNECTION_TYPE, "acceptor");
        config.SetString(SessionSettings.START_TIME, "00:00:00");
        config.SetString(SessionSettings.END_TIME, "00:00:00");

        var logFactory = new LogFactoryAdapter(new NullLogFactory());
        _session = new Session(false, _app, new MemoryStoreFactory(), _sessionId,
            new DataDictionaryProvider(), new SessionSchedule(config), 0, logFactory,
            new DefaultMessageFactory(), "blah");
        _session.SetResponder(_responder);
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
