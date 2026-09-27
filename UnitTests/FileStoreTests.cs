using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using System.Threading;
using QuickFix.Store;

namespace UnitTests;

[TestFixture]
public class FileStoreTests
{
    private FileStore? _store;
    private FileStoreFactory? _factory;

    private QuickFix.SessionSettings _settings = new();
    private QuickFix.SessionID _sessionId = new("unset", "unset", "unset");

    private string _storeDirectory = "unset";

    [SetUp]
    public void Setup()
    {
        _storeDirectory = Path.Combine(TestContext.CurrentContext.TestDirectory, "store");

        if (System.IO.Directory.Exists(_storeDirectory))
            System.IO.Directory.Delete(_storeDirectory, true);

        _sessionId = new QuickFix.SessionID("FIX.4.2", "SENDERCOMP", "TARGETCOMP");

        QuickFix.SettingsDictionary config = new QuickFix.SettingsDictionary();
        config.SetString(QuickFix.SessionSettings.CONNECTION_TYPE, "initiator");
        config.SetString(QuickFix.SessionSettings.FILE_STORE_PATH, _storeDirectory);

        _settings = new QuickFix.SessionSettings();
        _settings.Set(_sessionId, config);
        _factory = new FileStoreFactory(_settings);

        _store = (FileStore)_factory.Create(_sessionId);
    }

    void RebuildStore()
    {
        _store?.Dispose();
        _store = (FileStore)_factory!.Create(_sessionId);
    }


    [TearDown]
    public void Teardown()
    {
        _store!.Dispose();
        Directory.Delete(_storeDirectory, true);
    }

    [Test]
    public void TestPrefixForSessionWithSubsAndLoc()
    {
        QuickFix.SessionID sessionIDWithSubsAndLocation = new QuickFix.SessionID("FIX.4.2", "SENDERCOMP", "SENDERSUB", "SENDERLOC", "TARGETCOMP", "TARGETSUB", "TARGETLOC");
        Assert.That(FileStore.Prefix(sessionIDWithSubsAndLocation), Is.EqualTo("FIX.4.2-SENDERCOMP_SENDERSUB_SENDERLOC-TARGETCOMP_TARGETSUB_TARGETLOC"));

        QuickFix.SessionID sessionIDWithSubsNoLocation = new QuickFix.SessionID("FIX.4.2", "SENDERCOMP", "SENDERSUB", "TARGETCOMP", "TARGETSUB");
        Assert.That(FileStore.Prefix(sessionIDWithSubsNoLocation), Is.EqualTo("FIX.4.2-SENDERCOMP_SENDERSUB-TARGETCOMP_TARGETSUB"));
    }

    [Test]
    public void GenerateFileNamesTest()
    {
        Assert.That(System.IO.File.Exists(Path.Combine(_storeDirectory, "FIX.4.2-SENDERCOMP-TARGETCOMP.seqnums")));
        Assert.That(System.IO.File.Exists(Path.Combine(_storeDirectory, "FIX.4.2-SENDERCOMP-TARGETCOMP.body")));
        Assert.That(System.IO.File.Exists(Path.Combine(_storeDirectory, "FIX.4.2-SENDERCOMP-TARGETCOMP.header")));
        Assert.That(System.IO.File.Exists(Path.Combine(_storeDirectory, "FIX.4.2-SENDERCOMP-TARGETCOMP.session")));
    }

    [Test]
    public void NextSenderMsgSeqNumTest()
    {
        Assert.That(_store!.NextSenderMsgSeqNum, Is.EqualTo(1));
        _store.NextSenderMsgSeqNum = 5;
        Assert.That(_store.NextSenderMsgSeqNum, Is.EqualTo(5));
        RebuildStore();
        Assert.That(_store.NextSenderMsgSeqNum, Is.EqualTo(5));
    }

    [Test]
    public void IncNextSenderMsgSeqNumTest()
    {
        _store!.IncrNextSenderMsgSeqNum();
        Assert.That(_store.NextSenderMsgSeqNum, Is.EqualTo(2));
        RebuildStore();
        Assert.That(_store.NextSenderMsgSeqNum, Is.EqualTo(2));
    }

    [Test]
    public void NextTargetMsgSeqNumTest()
    {
        Assert.That(_store!.NextTargetMsgSeqNum, Is.EqualTo(1));
        _store.NextTargetMsgSeqNum = 6;
        Assert.That(_store.NextTargetMsgSeqNum, Is.EqualTo(6));
        RebuildStore();
        Assert.That(_store.NextTargetMsgSeqNum, Is.EqualTo(6));
    }

    [Test]
    public void IncNextTargetMsgSeqNumTest()
    {
        _store!.IncrNextTargetMsgSeqNum();
        Assert.That(_store.NextTargetMsgSeqNum, Is.EqualTo(2));
        RebuildStore();
        Assert.That(_store.NextTargetMsgSeqNum, Is.EqualTo(2));
    }

    /// Using UInt64 seqnums per FIX Trading Community Continuous Markets Working Group recommendations.
    [Test]
    public void TestSeqNumLimitsForContinuousMarkets()
    {
        // Given the next seqnums are UInt64.MaxValue - 1
        _store!.NextSenderMsgSeqNum = System.UInt64.MaxValue - 1;
        _store.NextTargetMsgSeqNum = _store.NextSenderMsgSeqNum;

        // When the next seqnums are incremented
        _store.IncrNextSenderMsgSeqNum();
        _store.IncrNextTargetMsgSeqNum();

        // Then the next seqnums should be UInt64.MaxValue
        Assert.That(_store.NextSenderMsgSeqNum, Is.EqualTo(System.UInt64.MaxValue));
        Assert.That(_store.NextTargetMsgSeqNum, Is.EqualTo(System.UInt64.MaxValue));

        // When the store is reloaded from files
        RebuildStore();

        // Then the next seqnums should still be UInt64.MaxValue
        Assert.That(_store.NextSenderMsgSeqNum, Is.EqualTo(System.UInt64.MaxValue));
        Assert.That(_store.NextTargetMsgSeqNum, Is.EqualTo(System.UInt64.MaxValue));

        // When the next seqnums are incremented again
        _store.IncrNextSenderMsgSeqNum();
        _store.IncrNextTargetMsgSeqNum();

        // Then the next seqnums should overflow to zero
        Assert.That(_store.NextSenderMsgSeqNum, Is.EqualTo(0));
        Assert.That(_store.NextTargetMsgSeqNum, Is.EqualTo(0));

        // When the store is reloaded from files
        RebuildStore();

        // Then the next seqnums should still be zero
        Assert.That(_store.NextSenderMsgSeqNum, Is.EqualTo(0));
        Assert.That(_store.NextTargetMsgSeqNum, Is.EqualTo(0));
    }

    [Test]
    public void ResetTest()
    {
        // seq nums reset
        _store!.NextTargetMsgSeqNum = 5;
        _store.NextSenderMsgSeqNum = 4;
        _store.Reset();
        Assert.That(_store.NextTargetMsgSeqNum, Is.EqualTo(1));
        Assert.That(_store.NextSenderMsgSeqNum, Is.EqualTo(1));

        // Check that messages do not persist after reset
        _store.Set(1, "dude");
        _store.Set(2, "pude");
        _store.Set(3, "ok");
        _store.Set(4, "ohai");

        _store.Reset();

        var msgs = new List<string>();
        _store.Get(2, 3, msgs);
        Assert.That(msgs,Is.Empty);
    }

    [Test]
    public void CreationTimeTest()
    {
        DateTime d1 = _store!.CreationTime!.Value;
        RebuildStore();
        DateTime d2 = _store.CreationTime.Value;
        Util.UtcDateTimeSerializerTests.AssertHackyDateTimeEquality(d1, d2);

        Thread.Sleep(1000);
        _store.Reset();
        DateTime d3 = _store.CreationTime.Value;
        Assert.That(DateTimeOffset.Compare(d1, d3), Is.EqualTo(-1)); // e.g. d1 is earlier than d3
    }


    [Test]
    public void GetTest()
    {
        _store!.Set(1, "dude");
        _store.Set(2, "pude");
        _store.Set(3, "ok");
        _store.Set(4, "ohai");

        var msgs = new List<string>();
        _store.Get(2, 3, msgs);
        var expected = new List<string>() { "pude", "ok" };

        Assert.That(msgs, Is.EqualTo(expected));

        RebuildStore();

        msgs = new List<string>();
        _store.Get(2, 3, msgs);

        Assert.That(msgs, Is.EqualTo(expected));
    }

    [Test]
    public void SetAndIncrNextSenderMsgSeqNumTest()
    {
        IMessageStore messageStore = _store ?? throw new InvalidProgramException();

        messageStore.SetAndIncrNextSenderMsgSeqNum(1, "dude");
        messageStore.SetAndIncrNextSenderMsgSeqNum(2, "pude");
        messageStore.SetAndIncrNextSenderMsgSeqNum(3, "ok");
        messageStore.SetAndIncrNextSenderMsgSeqNum(4, "ohai");

        var msgs = new List<string>();
        _store.Get(2, 3, msgs);
        var expected = new List<string>() { "pude", "ok" };

        Assert.That(msgs, Is.EqualTo(expected));
        Assert.That(_store.NextSenderMsgSeqNum, Is.EqualTo(5));

        RebuildStore();

        msgs = new List<string>();
        _store.Get(2, 3, msgs);

        Assert.That(msgs, Is.EqualTo(expected));
        Assert.That(_store.NextSenderMsgSeqNum, Is.EqualTo(5));
    }

    private sealed class SimulatedCrash : Exception;

    /// <summary>
    /// C1: SetAndIncrNextSenderMsgSeqNum makes three durable writes (body, sequence numbers,
    /// header). Interrupt after each of the first two, reopen the files, and check the store
    /// is coherent: no message is recorded at the number the next send will use (it would be
    /// replaced), and every recorded message reads back intact.
    /// </summary>
    [TestCase(1)]
    [TestCase(2)]
    public void SetAndIncr_interrupted_between_durable_writes_recovers_a_coherent_store(int writesBeforeCrash)
    {
        IMessageStore store = _store!;
        store.SetAndIncrNextSenderMsgSeqNum(1, "first");

        int writes = 0;
        _store!.AfterDurableWrite = () =>
        {
            if (++writes == writesBeforeCrash)
                throw new SimulatedCrash();
        };
        Assert.Throws<SimulatedCrash>(() => store.SetAndIncrNextSenderMsgSeqNum(2, "second"));
        _store.AfterDurableWrite = null;

        RebuildStore();

        SeqNumType next = _store.NextSenderMsgSeqNum;
        var atNext = new List<string>();
        _store.Get(next, next, atNext);
        Assert.That(atNext, Is.Empty, $"a message is recorded at NextSenderMsgSeqNum {next}; the next send would replace it");

        var recorded = new List<string>();
        Assert.DoesNotThrow(() => _store.Get(1, next - 1, recorded));
        Assert.That(recorded[0], Is.EqualTo("first"));
        Assert.That(recorded, Is.SubsetOf(new[] { "first", "second" }));
    }

    /// <summary>
    /// C1: a header entry whose body bytes never reached disk (a torn write) is dropped on
    /// load, so a resend over that range reads the intact messages instead of failing.
    /// </summary>
    [Test]
    public void Header_entry_past_the_end_of_the_body_is_ignored_on_load()
    {
        IMessageStore store = _store!;
        store.SetAndIncrNextSenderMsgSeqNum(1, "first");
        store.SetAndIncrNextSenderMsgSeqNum(2, "second");
        _store!.Dispose();

        string headerPath = Path.Combine(_storeDirectory, FileStore.Prefix(_sessionId) + ".header");
        long bodyLength = new FileInfo(Path.Combine(_storeDirectory, FileStore.Prefix(_sessionId) + ".body")).Length;
        File.AppendAllText(headerPath, $"3,{bodyLength},50{Environment.NewLine}");

        _store = (FileStore)_factory!.Create(_sessionId);

        var msgs = new List<string>();
        Assert.DoesNotThrow(() => _store.Get(1, 3, msgs));
        Assert.That(msgs, Is.EqualTo(new List<string> { "first", "second" }));
    }

    /// <summary>
    /// C1: recovery must not second-guess the sequence file. An operator rewind of
    /// NextSenderMsgSeqNum below messages already recorded survives a reopen.
    /// </summary>
    [Test]
    public void Rewound_sender_sequence_survives_reopen()
    {
        IMessageStore store = _store!;
        store.SetAndIncrNextSenderMsgSeqNum(1, "first");
        store.SetAndIncrNextSenderMsgSeqNum(2, "second");
        store.SetAndIncrNextSenderMsgSeqNum(3, "third");

        store.NextSenderMsgSeqNum = 2;
        RebuildStore();

        Assert.That(_store!.NextSenderMsgSeqNum, Is.EqualTo(2));
    }

    /// <summary>
    /// C1 (composition review Q5): a header write that fails while the process lives must
    /// not reach disk later. The session never sent that number and gap-fills it; if the
    /// failed line surfaced on a later flush or close, a resend after restart would replay
    /// the unsent message as PossDup.
    /// The fault is a real OS byte-range lock on the header file, which only blocks another
    /// handle's writes on Windows (advisory on Linux), so this runs on Windows only.
    /// </summary>
    [Test]
    [Platform("Win")]
    public void Failed_header_write_does_not_reach_disk_later()
    {
        IMessageStore store = _store!;
        store.SetAndIncrNextSenderMsgSeqNum(1, "first");

        string headerPath = Path.Combine(_storeDirectory, FileStore.Prefix(_sessionId) + ".header");
        using (var locker = new FileStream(headerPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            locker.Lock(0, 1_000_000);
            Assert.Throws<IOException>(() => store.SetAndIncrNextSenderMsgSeqNum(2, "second"));
            locker.Unlock(0, 1_000_000);
        }

        RebuildStore();

        var msgs = new List<string>();
        _store!.Get(1, 2, msgs);
        Assert.That(msgs, Is.EqualTo(new List<string> { "first" }));
    }
}
