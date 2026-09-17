using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using QuickFix;
using QuickFix.Fields;
using QuickFix.Logger;
using QuickFix.Store;

namespace UnitTests;

[TestFixture]
public class OutboundSendJournalTest
{
    private const string RawHeartbeat = "8=FIX.4.2\u00019=5\u000135=0\u000110=000\u0001";

    private sealed class RecordingJournal : IOutboundSendJournal
    {
        public List<string> Calls { get; } = [];
        public List<string> PreparedFrames { get; } = [];
        public List<OutboundSendJournalToken> PreparedTokens { get; } = [];
        public List<OutboundSendJournalToken> OutcomeTokens { get; } = [];
        public bool FailPrepare { get; init; }
        public bool FailOutcome { get; init; }

        public OutboundSendJournalToken Prepare(SessionID sessionId, string rawFrame)
        {
            Calls.Add("prepare");
            PreparedFrames.Add(rawFrame);
            if (FailPrepare)
                throw new InvalidOperationException("prepare failed");

            var token = new OutboundSendJournalToken($"token-{PreparedTokens.Count + 1}");
            PreparedTokens.Add(token);
            return token;
        }

        public void RecordOutcome(OutboundSendJournalToken token, bool transmitted) =>
            RecordOutcome(
                token,
                transmitted ? OutboundSendDisposition.Transmitted : OutboundSendDisposition.NotTransmitted);

        // Opted into the three-state overload, so an uncertain send is recorded as such.
        public void RecordOutcome(OutboundSendJournalToken token, OutboundSendDisposition disposition)
        {
            Calls.Add(disposition switch
            {
                OutboundSendDisposition.Transmitted => "outcome:True",
                OutboundSendDisposition.NotTransmitted => "outcome:False",
                _ => "outcome:Unknown",
            });
            OutcomeTokens.Add(token);
            if (FailOutcome)
                throw new InvalidOperationException("outcome failed");
        }

        public void Clear()
        {
            Calls.Clear();
            PreparedFrames.Clear();
            PreparedTokens.Clear();
            OutcomeTokens.Clear();
        }
    }

    /// <summary>
    /// An implementer written against the original boolean-only contract, which has not
    /// overridden the disposition overload. Proves the interface default cannot invent a
    /// definite answer on its behalf.
    /// </summary>
    private sealed class LegacyBooleanJournal : IOutboundSendJournal
    {
        public List<string> Calls { get; } = [];

        public OutboundSendJournalToken Prepare(SessionID sessionId, string rawFrame)
        {
            Calls.Add("prepare");
            return new OutboundSendJournalToken("legacy-token");
        }

        public void RecordOutcome(OutboundSendJournalToken token, bool transmitted) =>
            Calls.Add($"outcome:{transmitted}");
    }

    private sealed class CountingResponder : IResponder
    {
        private readonly bool _result;
        private readonly bool _throwOnSend;

        public CountingResponder(bool result = true, bool throwOnSend = false)
        {
            _result = result;
            _throwOnSend = throwOnSend;
        }

        public int SendCount { get; private set; }

        public bool Send(string message)
        {
            SendCount++;
            if (_throwOnSend)
                throw new InvalidOperationException("responder failed");
            return _result;
        }

        public void Disconnect() { }
    }

    /// <summary>
    /// A logger that claims to be enabled and then throws from the scope the outgoing-message log
    /// block opens. That block sits between Prepare and the responder's own try, so it is the
    /// window in which an escape used to leave a prepared row with no outcome at all.
    /// </summary>
    private sealed class ThrowingSessionLoggerFactory : IQuickFixLoggerFactory
    {
        private sealed class ThrowingLogger : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull =>
                throw new InvalidOperationException("log scope failed");

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            { }
        }

        public ILogger CreateSessionLogger(SessionID sessionId) => new ThrowingLogger();

        public ILogger CreateNonSessionLogger<T>() => new ThrowingLogger();
    }

    /// <summary>Records what the wire tap was told about outbound frames.</summary>
    private sealed class RecordingWireTap : IFixWireTap
    {
        public List<(string RawFrame, bool Transmitted)> Outbound { get; } = [];

        public void OnInbound(SessionID sessionId, string rawFrame) { }

        public void OnInboundQueued(SessionID sessionId, SeqNumType seqNum) { }

        public void OnOutbound(SessionID sessionId, string rawFrame, bool transmitted) =>
            Outbound.Add((rawFrame, transmitted));
    }

    private static Session BuildSession(IOutboundSendJournal journal, IResponder? responder = null, bool persistMessages = false, IQuickFixLoggerFactory? loggerFactory = null, IFixWireTap? wireTap = null)
    {
        var sessionId = new SessionID("FIX.4.2", "SENDER", "TARGET");
        var settings = new SettingsDictionary();
        settings.SetBool(SessionSettings.PERSIST_MESSAGES, persistMessages);
        settings.SetString(SessionSettings.CONNECTION_TYPE, "acceptor");
        settings.SetString(SessionSettings.START_TIME, "00:00:00");
        settings.SetString(SessionSettings.END_TIME, "00:00:00");

        var session = new Session(
            false, new SessionTestSupport.MockApplication(), new MemoryStoreFactory(), sessionId,
            new DataDictionaryProvider(), new SessionSchedule(settings), 0,
            loggerFactory ?? new LogFactoryAdapter(new NullLogFactory()), new DefaultMessageFactory(), "blah",
            wireTap: wireTap,
            outboundSendJournal: journal);
        if (responder is not null)
            session.SetResponder(responder);
        session.CheckLatency = false;
        return session;
    }

    private static Message CreateOrder()
    {
        return new QuickFix.FIX42.NewOrderSingle(
            new ClOrdID("1"), new HandlInst(HandlInst.MANUAL_ORDER), new Symbol("IBM"),
            new Side(Side.BUY), new TransactTime(), new OrdType(OrdType.LIMIT));
    }

    private static Message CreateInboundResendRequest(SeqNumType sequenceNumber, SeqNumType beginSequenceNumber, SeqNumType endSequenceNumber)
    {
        var request = new QuickFix.FIX42.ResendRequest(new BeginSeqNo(beginSequenceNumber), new EndSeqNo(endSequenceNumber));
        request.Header.SetField(new TargetCompID("SENDER"));
        request.Header.SetField(new SenderCompID("TARGET"));
        request.Header.SetField(new MsgSeqNum(sequenceNumber));
        request.Header.SetField(new SendingTime(DateTime.UtcNow));
        return request;
    }

    private static void SendInboundLogon(Session session)
    {
        var logon = new QuickFix.FIX42.Logon();
        logon.Header.SetField(new TargetCompID("SENDER"));
        logon.Header.SetField(new SenderCompID("TARGET"));
        logon.Header.SetField(new MsgSeqNum(1));
        logon.Header.SetField(new SendingTime(DateTime.UtcNow));
        logon.SetField(new HeartBtInt(1));
        session.Next(logon.ConstructString());
    }

    /// <summary>
    /// Prepare has already consumed a MsgSeqNum and written a prepared row by the time the
    /// outgoing-message log block runs, so an escape from that block must still record an outcome.
    /// Recording nothing stalls the session's publishable prefix until the 60s sweep and loses the
    /// frame's audit row, because the journal holds the only copy of the body for the capture seam.
    ///
    /// NotTransmitted, not Unknown. The log block sits before the responder call, which is the only
    /// route to the transport, so an escape there PROVES the bytes never left — the assertion below
    /// that the responder was never entered is the same fact the disposition records. Unknown would
    /// be both false and harmful: it maps to a SendThrew recovery reason for a send that never ran,
    /// and it withholds the one claim that licenses resending a frame whose MsgSeqNum is spent.
    /// </summary>
    [Test]
    public void Escape_before_the_send_records_an_unsent_outcome()
    {
        var journal = new RecordingJournal();
        var responder = new CountingResponder();
        using var session = BuildSession(journal, responder, loggerFactory: new ThrowingSessionLoggerFactory());

        Assert.That(() => session.Send(RawHeartbeat), Throws.InvalidOperationException);
        Assert.Multiple(() =>
        {
            Assert.That(journal.Calls, Is.EqualTo(new[] { "prepare", "outcome:False" }));
            Assert.That(responder.SendCount, Is.Zero);
            Assert.That(journal.OutcomeTokens, Is.EqualTo(journal.PreparedTokens));
        });
    }

    /// <summary>
    /// The escape must tap as well as record. The engine's journal captures the frame only for an
    /// Unknown outcome, on the reasoning that a definite outcome means QuickFIX/n already reached
    /// its own wire tap — which is true everywhere except this path, because the escape jumps over
    /// the tap call. Recording a definite outcome without tapping also finalises the emission row
    /// and so puts it beyond the recovery sweep's capture, leaving the frame with no audit row at
    /// all: the exact loss this whole block exists to prevent.
    /// </summary>
    [Test]
    public void Escape_before_the_send_still_taps_the_frame()
    {
        var journal = new RecordingJournal();
        var tap = new RecordingWireTap();
        using var session = BuildSession(
            journal, new CountingResponder(), loggerFactory: new ThrowingSessionLoggerFactory(), wireTap: tap);

        Assert.That(() => session.Send(RawHeartbeat), Throws.InvalidOperationException);
        Assert.That(tap.Outbound, Is.EqualTo(new[] { (RawHeartbeat, false) }));
    }

    /// <summary>
    /// The counterpart to the test above, pinning the boundary between the two dispositions. Once
    /// the responder has been entered the bytes may or may not have reached the counterparty, so
    /// the escape value becomes Unknown — and stays Unknown for anything added after the send.
    /// </summary>
    [Test]
    public void Escape_after_the_send_records_an_unknown_outcome()
    {
        var journal = new RecordingJournal();
        var responder = new CountingResponder(throwOnSend: true);
        using var session = BuildSession(journal, responder);

        Assert.That(() => session.Send(RawHeartbeat), Throws.InvalidOperationException);
        Assert.Multiple(() =>
        {
            Assert.That(journal.Calls, Is.EqualTo(new[] { "prepare", "outcome:Unknown" }));
            Assert.That(responder.SendCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void Prepare_failure_prevents_responder_send()
    {
        var journal = new RecordingJournal { FailPrepare = true };
        var responder = new CountingResponder();
        using var session = BuildSession(journal, responder);

        Assert.That(() => session.Send(RawHeartbeat), Throws.InvalidOperationException);
        Assert.That(responder.SendCount, Is.Zero);
        Assert.That(journal.Calls, Is.EqualTo(new[] { "prepare" }));
    }

    [Test]
    public void Outcome_failure_does_not_change_successful_send_result()
    {
        var journal = new RecordingJournal { FailOutcome = true };
        var responder = new CountingResponder();
        using var session = BuildSession(journal, responder);

        Assert.That(session.Send(RawHeartbeat), Is.True);
        Assert.That(responder.SendCount, Is.EqualTo(1));
        Assert.That(journal.Calls, Is.EqualTo(new[] { "prepare", "outcome:True" }));
    }

    [Test]
    public void Null_responder_records_unsent_outcome()
    {
        var journal = new RecordingJournal();
        using var session = BuildSession(journal);

        Assert.That(session.Send(RawHeartbeat), Is.False);
        Assert.That(journal.Calls, Is.EqualTo(new[] { "prepare", "outcome:False" }));
    }

    [Test]
    public void False_responder_result_records_unsent_outcome()
    {
        var journal = new RecordingJournal();
        var responder = new CountingResponder(result: false);
        using var session = BuildSession(journal, responder);

        Assert.That(session.Send(RawHeartbeat), Is.False);
        Assert.That(responder.SendCount, Is.EqualTo(1));
        Assert.That(journal.Calls, Is.EqualTo(new[] { "prepare", "outcome:False" }));
    }

    [Test]
    public void Responder_failure_records_an_unknown_outcome()
    {
        // A throwing responder may or may not have put bytes on the wire, so neither `true`
        // nor `false` is honest. This used to record NOTHING, leaving the prepared row
        // unresolved forever — and a journal whose publishable prefix stops at the first
        // unfinalised ordinal then stalls that session's stream until an external recovery
        // pass notices. Unknown is the outcome; the exception still propagates to the caller.
        var journal = new RecordingJournal();
        var responder = new CountingResponder(throwOnSend: true);
        using var session = BuildSession(journal, responder);

        Assert.That(() => session.Send(RawHeartbeat), Throws.InvalidOperationException);
        Assert.That(responder.SendCount, Is.EqualTo(1));
        Assert.That(journal.Calls, Is.EqualTo(new[] { "prepare", "outcome:Unknown" }));
        Assert.That(journal.OutcomeTokens, Is.EqualTo(journal.PreparedTokens));
    }

    [Test]
    public void Legacy_boolean_only_journal_is_not_told_a_throwing_send_failed()
    {
        // An implementer that never opted into the disposition overload cannot express
        // Unknown. Degrading it to `false` would assert the frame never reached the venue —
        // the claim that turns an uncertain send into a duplicate order downstream. The
        // default leaves the row unresolved, exactly as before this change.
        var journal = new LegacyBooleanJournal();
        var responder = new CountingResponder(throwOnSend: true);
        using var session = BuildSession(journal, responder);

        Assert.That(() => session.Send(RawHeartbeat), Throws.InvalidOperationException);
        Assert.That(journal.Calls, Is.EqualTo(new[] { "prepare" }));
    }

    [Test]
    public void Outcome_failure_on_a_throwing_send_does_not_mask_the_send_exception()
    {
        // The journal's own failure must not replace the responder's. RecordJournalOutcome
        // already suppresses its exceptions; this pins that the suppression holds on the
        // newly-added exceptional path too, so the caller still sees why the send failed.
        var journal = new RecordingJournal { FailOutcome = true };
        var responder = new CountingResponder(throwOnSend: true);
        using var session = BuildSession(journal, responder);

        Assert.That(
            () => session.Send(RawHeartbeat),
            Throws.InvalidOperationException.With.Message.EqualTo("responder failed"));
        Assert.That(journal.Calls, Is.EqualTo(new[] { "prepare", "outcome:Unknown" }));
    }

    [Test]
    public void Raw_send_prepares_a_distinct_token_for_each_frame()
    {
        var journal = new RecordingJournal();
        using var session = BuildSession(journal, new CountingResponder());

        session.Send(RawHeartbeat);
        session.Send(RawHeartbeat);

        Assert.That(journal.PreparedTokens, Has.Count.EqualTo(2));
        Assert.That(journal.PreparedTokens[0], Is.Not.EqualTo(journal.PreparedTokens[1]));
        Assert.That(journal.OutcomeTokens, Is.EqualTo(journal.PreparedTokens));
    }

    [Test]
    public void Application_send_is_journaled_at_the_raw_send_boundary()
    {
        var journal = new RecordingJournal();
        using var session = BuildSession(journal, new CountingResponder());

        Assert.That(session.Send(CreateOrder()), Is.True);
        Assert.That(journal.Calls, Is.EqualTo(new[] { "prepare", "outcome:True" }));
        Assert.That(journal.PreparedFrames[0], Does.Contain("35=D"));
    }

    [Test]
    public void Admin_send_is_journaled_at_the_raw_send_boundary()
    {
        var journal = new RecordingJournal();
        using var session = BuildSession(journal, new CountingResponder());

        session.GenerateHeartbeat();

        Assert.That(journal.Calls, Is.EqualTo(new[] { "prepare", "outcome:True" }));
        Assert.That(journal.PreparedFrames[0], Does.Contain("35=0"));
    }

    [Test]
    public void Resend_is_journaled_at_the_raw_send_boundary()
    {
        var journal = new RecordingJournal();
        using var session = BuildSession(journal, new CountingResponder(), persistMessages: true);
        SendInboundLogon(session);
        session.Send(CreateOrder());
        journal.Clear();

        session.Next(CreateInboundResendRequest(2, 1, 2).ConstructString());

        Assert.That(journal.PreparedFrames, Has.Exactly(1).Contains("35=D"));
        Assert.That(journal.Calls.Count(call => call == "outcome:True"), Is.EqualTo(journal.PreparedFrames.Count));
    }

    [Test]
    public void Generated_gap_fill_is_journaled_at_the_raw_send_boundary()
    {
        var journal = new RecordingJournal();
        using var session = BuildSession(journal, new CountingResponder());
        SendInboundLogon(session);
        journal.Clear();

        session.Next(CreateInboundResendRequest(2, 1, 1).ConstructString());

        Assert.That(journal.PreparedFrames, Has.Exactly(1).Contains("35=4").And.Contains("123=Y"));
        Assert.That(journal.Calls, Is.EqualTo(new[] { "prepare", "outcome:True" }));
    }

    [Test]
    public void Session_factory_threads_journal_into_created_session()
    {
        var journal = new RecordingJournal();
        var settings = new SettingsDictionary();
        settings.SetBool(SessionSettings.USE_DATA_DICTIONARY, false);
        settings.SetBool(SessionSettings.PERSIST_MESSAGES, false);
        settings.SetString(SessionSettings.CONNECTION_TYPE, "acceptor");
        settings.SetString(SessionSettings.START_TIME, "00:00:00");
        settings.SetString(SessionSettings.END_TIME, "00:00:00");
        var factory = new SessionFactory(new SessionTestSupport.MockApplication(), new MemoryStoreFactory(),
            outboundSendJournal: journal);
        using var session = factory.Create(new SessionID("FIX.4.2", "FACTORY_SENDER", "FACTORY_TARGET"), settings);
        session.SetResponder(new CountingResponder());

        Assert.That(session.Send(RawHeartbeat), Is.True);
        Assert.That(journal.Calls, Is.EqualTo(new[] { "prepare", "outcome:True" }));
    }
}
