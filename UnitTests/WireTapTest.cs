using System;
using System.Collections.Generic;
using NUnit.Framework;
using QuickFix;
using QuickFix.Fields;
using QuickFix.Logger;
using QuickFix.Store;

namespace UnitTests;

/// <summary>
/// FP enhancement: tests for the <see cref="IFixWireTap"/> verbatim wire-frame capture seam.
/// The tap must fire once per inbound arrival (pre-parse, pre-redaction) and for every outbound
/// frame (pre-redaction, with transmitted disposition), and a null or throwing tap must never
/// disrupt FIX processing.
/// </summary>
[TestFixture]
public class WireTapTest
{
    private sealed record OutboundCapture(string Raw, bool Transmitted);

    private sealed class RecordingWireTap : IFixWireTap
    {
        public List<string> Inbound { get; } = new();
        public List<SeqNumType> Queued { get; } = new();
        public List<SeqNumType> ReplayPrepared { get; } = new();
        public List<SessionID> QueueCleared { get; } = new();
        public List<OutboundCapture> Outbound { get; } = new();

        public void OnInbound(SessionID sessionId, string rawFrame) => Inbound.Add(rawFrame);
        public void OnInboundQueued(SessionID sessionId, SeqNumType seqNum) => Queued.Add(seqNum);
        public void OnInboundReplayPrepare(SessionID sessionId, SeqNumType seqNum) => ReplayPrepared.Add(seqNum);
        public void OnInboundQueueCleared(SessionID sessionId) => QueueCleared.Add(sessionId);
        public void OnOutbound(SessionID sessionId, string rawFrame, bool transmitted) =>
            Outbound.Add(new OutboundCapture(rawFrame, transmitted));
    }

    private sealed class ThrowingWireTap : IFixWireTap
    {
        public void OnInbound(SessionID sessionId, string rawFrame) => throw new InvalidOperationException("boom");
        public void OnInboundQueued(SessionID sessionId, SeqNumType seqNum) => throw new InvalidOperationException("boom");
        public void OnInboundReplayPrepare(SessionID sessionId, SeqNumType seqNum) => throw new InvalidOperationException("boom");
        public void OnInboundQueueCleared(SessionID sessionId) => throw new InvalidOperationException("boom");
        public void OnOutbound(SessionID sessionId, string rawFrame, bool transmitted) => throw new InvalidOperationException("boom");
    }

    private SessionTestSupport.MockResponder _responder = new();
    private SessionTestSupport.MockApplication _application = new();
    private SessionID _sessionId = new("FIX.4.2", "SENDER", "TARGET");
    private SeqNumType _seqNum = 1;

    private Session BuildAcceptor(IFixWireTap? tap)
    {
        _responder = new SessionTestSupport.MockResponder();
        _application = new SessionTestSupport.MockApplication();
        _sessionId = new SessionID("FIX.4.2", "SENDER", "TARGET");
        _seqNum = 1;

        SettingsDictionary config = new();
        config.SetBool(SessionSettings.PERSIST_MESSAGES, false);
        config.SetString(SessionSettings.CONNECTION_TYPE, "acceptor");
        config.SetString(SessionSettings.START_TIME, "00:00:00");
        config.SetString(SessionSettings.END_TIME, "00:00:00");

        var logFactory = new LogFactoryAdapter(new NullLogFactory());

        var session = new Session(
            false, _application, new MemoryStoreFactory(), _sessionId,
            new DataDictionaryProvider(), new SessionSchedule(config), 0,
            logFactory, new DefaultMessageFactory(), "blah", tap);
        session.SetResponder(_responder);
        session.CheckLatency = false;
        return session;
    }

    private string SendLogonTo(Session session)
    {
        var msg = new QuickFix.FIX42.Logon();
        msg.Header.SetField(new TargetCompID(_sessionId.SenderCompID));
        msg.Header.SetField(new SenderCompID(_sessionId.TargetCompID));
        msg.Header.SetField(new MsgSeqNum(_seqNum++));
        msg.Header.SetField(new SendingTime(DateTime.UtcNow));
        msg.SetField(new HeartBtInt(1));
        string raw = msg.ConstructString();
        session.Next(raw);
        return raw;
    }

    private QuickFix.FIX42.NewOrderSingle CreateNos(SeqNumType n)
    {
        var order = new QuickFix.FIX42.NewOrderSingle(
            new ClOrdID("1"), new HandlInst(HandlInst.MANUAL_ORDER), new Symbol("IBM"),
            new Side(Side.BUY), new TransactTime(), new OrdType(OrdType.LIMIT));
        order.Header.SetField(new TargetCompID(_sessionId.SenderCompID));
        order.Header.SetField(new SenderCompID(_sessionId.TargetCompID));
        order.Header.SetField(new SendingTime(DateTime.UtcNow));
        order.Header.SetField(new MsgSeqNum(n));
        return order;
    }

    [Test]
    public void Inbound_tap_fires_once_per_arrival_with_verbatim_frame()
    {
        var tap = new RecordingWireTap();
        var session = BuildAcceptor(tap);

        string raw = SendLogonTo(session);

        Assert.That(tap.Inbound, Has.Count.EqualTo(1));
        Assert.That(tap.Inbound[0], Is.EqualTo(raw));
    }

    [Test]
    public void Outbound_tap_captures_every_sent_frame_verbatim_with_transmitted_true()
    {
        var tap = new RecordingWireTap();
        var session = BuildAcceptor(tap);

        SendLogonTo(session);

        // The acceptor answers the logon with exactly one outbound frame. Asserting the exact count
        // (not just non-empty) catches a regression that fires OnOutbound twice for a single send.
        Assert.That(tap.Outbound, Has.Count.EqualTo(1));
        Assert.That(tap.Outbound[0].Raw, Does.Contain("35=A"));
        Assert.That(tap.Outbound[0].Transmitted, Is.True, "M1: frames sent to a live responder must have Transmitted=true");
    }

    [Test]
    public void Outbound_tap_fires_when_responder_is_null_with_transmitted_false()
    {
        // A-F5 / M1: the capture seam must record every generated outbound frame, not only those
        // that reach the wire. When the responder is null (session disconnecting), Send returns
        // false and the frame is not transmitted — the tap must fire with transmitted=false so the
        // engine adapter can mark the capture row as phantom rather than missing.
        var tap = new RecordingWireTap();
        _application = new SessionTestSupport.MockApplication();
        _sessionId = new SessionID("FIX.4.2", "SENDER", "TARGET");
        SettingsDictionary config = new();
        config.SetBool(SessionSettings.PERSIST_MESSAGES, false);
        config.SetString(SessionSettings.CONNECTION_TYPE, "acceptor");
        config.SetString(SessionSettings.START_TIME, "00:00:00");
        config.SetString(SessionSettings.END_TIME, "00:00:00");
        var session = new Session(
            false, _application, new MemoryStoreFactory(), _sessionId,
            new DataDictionaryProvider(), new SessionSchedule(config), 0,
            new LogFactoryAdapter(new NullLogFactory()), new DefaultMessageFactory(), "blah", tap);
        // deliberate: no SetResponder — _responder remains null
        var result = session.Send("8=FIX.4.235=049=SENDER56=TARGET34=152=20260611-00:00:0010=000");
        Assert.That(result, Is.False, "Send must return false when responder is null");
        Assert.That(tap.Outbound, Has.Count.EqualTo(1), "tap must fire even though the frame was not transmitted");
        Assert.That(tap.Outbound[0].Transmitted, Is.False, "M1: disposition must be false for a frame that did not reach the wire");
    }

    [Test]
    public void Inbound_tap_captures_unredacted_frame()
    {
        var tap = new RecordingWireTap();
        var session = BuildAcceptor(tap);
        // Redaction is log-only and happens after the tap point, so the captured raw is always the
        // original. Redact the HeartBtInt tag (108) to prove the tap sidesteps the redaction path.
        session.RedactFieldsInLogs = [Tags.HeartBtInt];

        SendLogonTo(session);

        Assert.That(tap.Inbound[0], Does.Contain("108=1"));
        Assert.That(tap.Inbound[0], Does.Not.Contain("redacted"));
    }

    [Test]
    public void Slot_spill_queued_fires_and_replay_prepare_fires_for_queued_app_frame()
    {
        // §12.6 slot-spill: when an app frame arrives too-high, OnInboundQueued fires with the
        // frame's seqNum so the engine adapter can spill the correlator slot into its own
        // _captureIdQueue. When the gap is filled and NextQueued replays the frame,
        // OnInboundReplayPrepare fires with the same seqNum BEFORE NextMessage so the adapter
        // can restore the original CaptureId into the slot for FromApp to take.
        // Crucially: OnInbound does NOT re-fire on replay — one wire frame = one capture row.
        var tap = new RecordingWireTap();
        var session = BuildAcceptor(tap);
        SendLogonTo(session); // seq 1

        // seq 3 arrives too-high (session expects seq 2) — queued, ResendRequest sent
        string nos3 = CreateNos(3).ConstructString();
        session.Next(nos3);

        Assert.That(tap.Queued, Has.Count.EqualTo(1), "OnInboundQueued must fire when the frame is too-high");
        Assert.That(tap.Queued[0], Is.EqualTo((SeqNumType)3), "queued seqNum must be the too-high frame's seqNum");

        // seq 2 arrives — fills the gap, then NextQueued replays seq 3
        session.Next(CreateNos(2).ConstructString());

        Assert.That(tap.ReplayPrepared, Has.Count.EqualTo(1), "OnInboundReplayPrepare must fire before NextMessage on replay");
        Assert.That(tap.ReplayPrepared[0], Is.EqualTo((SeqNumType)3), "prepare seqNum must match the queued frame's seqNum");
        // Three wire arrivals (logon + nos3 + nos2), no replay re-tap — C5: one capture row per frame
        Assert.That(tap.Inbound, Has.Count.EqualTo(3), "OnInbound must fire exactly once per wire arrival, never on replay");
    }

    [Test]
    public void Slot_spill_replay_prepare_fires_for_logon_or_resend_in_queue()
    {
        // §12.6: the LOGON/RESEND skip branch in NextQueued increments the seq without calling
        // NextMessage — OnInboundReplayPrepare must still fire to discard any spilled slot rather
        // than leaking it into a later call.
        var tap = new RecordingWireTap();
        var session = BuildAcceptor(tap);
        SendLogonTo(session); // seq 1

        // A second LOGON at seq 3 (too-high) gets queued
        var logon2 = new QuickFix.FIX42.Logon();
        logon2.Header.SetField(new TargetCompID(_sessionId.SenderCompID));
        logon2.Header.SetField(new SenderCompID(_sessionId.TargetCompID));
        logon2.Header.SetField(new MsgSeqNum((SeqNumType)3));
        logon2.Header.SetField(new SendingTime(DateTime.UtcNow));
        logon2.SetField(new HeartBtInt(1));
        session.Next(logon2.ConstructString());

        Assert.That(tap.Queued, Has.Count.EqualTo(1), "OnInboundQueued fires for too-high LOGON");

        // Fill the gap: seq 2 NOS arrives — NextQueued hits the LOGON skip branch for seq 3
        session.Next(CreateNos(2).ConstructString());

        Assert.That(tap.ReplayPrepared, Has.Count.EqualTo(1),
            "OnInboundReplayPrepare must fire in the LOGON/RESEND skip branch to discard the spilled slot");
        Assert.That(tap.ReplayPrepared[0], Is.EqualTo((SeqNumType)3));
    }

    [Test]
    public void Inbound_queue_cleared_fires_on_session_disconnect()
    {
        // OnInboundQueueCleared fires when the session's inbound queue is cleared (on disconnect),
        // so the engine adapter can discard any spilled CaptureId slots that will never be replayed.
        var tap = new RecordingWireTap();
        var session = BuildAcceptor(tap);
        SendLogonTo(session);

        // Disconnect triggers Session.Disconnect which calls _state.ClearQueue
        session.Disconnect("test");

        Assert.That(tap.QueueCleared, Has.Count.EqualTo(1));
    }

    [Test]
    public void SessionFactory_threads_wiretap_into_created_session()
    {
        // The acceptor/initiator only ever build a Session via SessionFactory, so the tap must
        // ride through the factory ctor onto the Session it creates.
        var tap = new RecordingWireTap();
        _application = new SessionTestSupport.MockApplication();
        _responder = new SessionTestSupport.MockResponder();
        _sessionId = new SessionID("FIX.4.2", "SENDER", "TARGET");
        _seqNum = 1;

        SettingsDictionary config = new();
        config.SetBool(SessionSettings.USE_DATA_DICTIONARY, false);
        config.SetString(SessionSettings.CONNECTION_TYPE, "acceptor");
        config.SetString(SessionSettings.START_TIME, "00:00:00");
        config.SetString(SessionSettings.END_TIME, "00:00:00");
        config.SetBool(SessionSettings.PERSIST_MESSAGES, false);

        var factory = new SessionFactory(_application, new MemoryStoreFactory(), null, null, tap);
        var session = factory.Create(_sessionId, config);
        session.SetResponder(_responder);
        session.CheckLatency = false;

        string raw = SendLogonTo(session);

        Assert.That(tap.Inbound, Has.Count.EqualTo(1));
        Assert.That(tap.Inbound[0], Is.EqualTo(raw));
    }

    [Test]
    public void Null_wiretap_processes_logon_normally()
    {
        var session = BuildAcceptor(null);

        Assert.DoesNotThrow(() => SendLogonTo(session));
        Assert.That(_responder.GetCount(MsgType.LOGON), Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public void Throwing_wiretap_does_not_disrupt_processing()
    {
        var session = BuildAcceptor(new ThrowingWireTap());

        Assert.DoesNotThrow(() => SendLogonTo(session));
        Assert.That(_responder.GetCount(MsgType.LOGON), Is.GreaterThanOrEqualTo(1));
    }
}
