using System;
using System.Collections.Generic;
using System.Linq;
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

        public void RecordOutcome(OutboundSendJournalToken token, bool transmitted)
        {
            Calls.Add($"outcome:{transmitted}");
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

    private static Session BuildSession(IOutboundSendJournal journal, IResponder? responder = null, bool persistMessages = false)
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
            new LogFactoryAdapter(new NullLogFactory()), new DefaultMessageFactory(), "blah",
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
    public void Responder_failure_leaves_prepared_frame_unresolved()
    {
        var journal = new RecordingJournal();
        var responder = new CountingResponder(throwOnSend: true);
        using var session = BuildSession(journal, responder);

        Assert.That(() => session.Send(RawHeartbeat), Throws.InvalidOperationException);
        Assert.That(responder.SendCount, Is.EqualTo(1));
        Assert.That(journal.Calls, Is.EqualTo(new[] { "prepare" }));
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
