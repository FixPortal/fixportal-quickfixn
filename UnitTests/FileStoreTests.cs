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
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
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

    /// <summary>
    /// C1 (PR #98 composition review, Q5 residue): a crash or short write can leave a torn
    /// last header line. Cut inside its size digits it would parse as a valid, truncated entry,
    /// and the next append would be glued onto it, losing that entry after the next restart.
    /// Load must drop the torn tail so the file ends on a line boundary.
    /// </summary>
    [Test]
    public void Torn_trailing_header_line_is_dropped_on_load()
    {
        IMessageStore store = _store!;
        store.SetAndIncrNextSenderMsgSeqNum(1, "first");
        store.SetAndIncrNextSenderMsgSeqNum(2, "second");
        _store!.Dispose();

        string headerPath = Path.Combine(_storeDirectory, FileStore.Prefix(_sessionId) + ".header");
        File.AppendAllText(headerPath, "3,0,1");

        _store = (FileStore)_factory!.Create(_sessionId);
        _store.SetAndIncrNextSenderMsgSeqNum(3, "third");
        RebuildStore();

        var msgs = new List<string>();
        _store!.Get(1, 3, msgs);
        Assert.That(msgs, Is.EqualTo(new List<string> { "first", "second", "third" }));
    }

    /// <summary>Lands only the first <paramref name="landed"/> bytes of an armed write, then
    /// fails: a short write followed by ENOSPC, or (landed &gt;= count) a write that completed
    /// but still reported failure.</summary>
    private sealed class ShortWriteStream(Stream inner, Func<bool> armed, int landed = 3) : Stream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (!armed())
            {
                inner.Write(buffer, offset, count);
                return;
            }
            inner.Write(buffer, offset, Math.Min(landed, count));
            inner.Flush();
            throw new IOException("simulated short write (ENOSPC)");
        }

        public override void Flush() => inner.Flush();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
    }

    /// <summary>
    /// C1 (PR #98 composition review, Q5 residue): a header write that lands partially and
    /// then fails, with the process still running, must not leave a torn prefix for the next
    /// append to be glued onto. The failed number stays unrecorded and the next message survives
    /// a restart.
    /// </summary>
    [Test]
    public void Short_header_write_leaves_no_torn_line_for_the_next_append()
    {
        IMessageStore store = _store!;
        store.SetAndIncrNextSenderMsgSeqNum(1, "first");

        bool armed = false;
        _store!.HeaderStreamDecorator = s => new ShortWriteStream(s, () => armed);
        store.Refresh();

        armed = true;
        Assert.Throws<IOException>(() => store.SetAndIncrNextSenderMsgSeqNum(2, "second"));
        armed = false;
        store.SetAndIncrNextSenderMsgSeqNum(3, "third");

        _store.HeaderStreamDecorator = null;
        RebuildStore();

        var msgs = new List<string>();
        _store!.Get(1, 3, msgs);
        Assert.That(msgs, Is.EqualTo(new List<string> { "first", "third" }));
    }

    /// <summary>
    /// C1: a header write that lands the whole line and still reports failure must not leave
    /// that line behind. The session treats the number as unsent and gap-fills it; a surviving
    /// complete line would replay the unsent message as PossDup after restart.
    /// </summary>
    [Test]
    public void Header_write_that_completes_but_reports_failure_leaves_no_entry()
    {
        IMessageStore store = _store!;
        store.SetAndIncrNextSenderMsgSeqNum(1, "first");

        bool armed = false;
        _store!.HeaderStreamDecorator = s => new ShortWriteStream(s, () => armed, landed: int.MaxValue);
        store.Refresh();

        armed = true;
        Assert.Throws<IOException>(() => store.SetAndIncrNextSenderMsgSeqNum(2, "second"));
        armed = false;

        _store.HeaderStreamDecorator = null;
        RebuildStore();

        var msgs = new List<string>();
        _store!.Get(1, 2, msgs);
        Assert.That(msgs, Is.EqualTo(new List<string> { "first" }));
    }

    /// <summary>
    /// C1: if the repair after a failed header write itself fails, the original failure is
    /// kept, the next write still throws (nothing is sent on a header that is not back on a
    /// line boundary) while retrying the repair, and the store recovers once it succeeds.
    /// </summary>
    [Test]
    public void Failed_header_repair_keeps_the_cause_fails_closed_and_recovers()
    {
        IMessageStore store = _store!;
        store.SetAndIncrNextSenderMsgSeqNum(1, "first");

        bool armed = false;
        bool failReopen = false;
        _store!.HeaderStreamDecorator = s =>
        {
            if (failReopen)
            {
                s.Dispose();
                throw new IOException("simulated reopen failure");
            }
            return new ShortWriteStream(s, () => armed);
        };
        store.Refresh();

        armed = true;
        failReopen = true;
        var failure = Assert.Throws<IOException>(() => store.SetAndIncrNextSenderMsgSeqNum(2, "second"));
        Assert.That(failure!.InnerException, Is.TypeOf<AggregateException>());
        Assert.That(System.Linq.Enumerable.Select(((AggregateException)failure.InnerException!).InnerExceptions, e => e.Message),
            Is.EqualTo(new[] { "simulated short write (ENOSPC)", "simulated reopen failure" }));

        armed = false;
        failReopen = false;
        Assert.Catch<Exception>(() => store.SetAndIncrNextSenderMsgSeqNum(3, "third"));
        store.SetAndIncrNextSenderMsgSeqNum(4, "fourth");

        _store.HeaderStreamDecorator = null;
        RebuildStore();

        var msgs = new List<string>();
        _store!.Get(1, 4, msgs);
        Assert.That(msgs, Is.EqualTo(new List<string> { "first", "fourth" }));
    }

    /// <summary>
    /// C1: Reset() deletes the header; if reopening it then fails before the file is created,
    /// the next write's repair must recreate it rather than fail on the missing file forever.
    /// </summary>
    [Test]
    public void Header_repair_recovers_when_reset_left_no_header_file()
    {
        IMessageStore store = _store!;
        store.SetAndIncrNextSenderMsgSeqNum(1, "first");

        string headerPath = Path.Combine(_storeDirectory, FileStore.Prefix(_sessionId) + ".header");
        bool failOpen = false;
        _store!.HeaderStreamDecorator = s =>
        {
            if (!failOpen)
                return s;
            s.Dispose();
            File.Delete(headerPath); // the open failed before the file existed
            throw new IOException("simulated header open failure");
        };

        failOpen = true;
        Assert.Throws<IOException>(() => store.Reset());
        failOpen = false;

        Assert.Catch<Exception>(() => store.SetAndIncrNextSenderMsgSeqNum(1, "a"));
        store.SetAndIncrNextSenderMsgSeqNum(2, "b");

        _store.HeaderStreamDecorator = null;
        RebuildStore();

        var msgs = new List<string>();
        _store!.Get(1, 2, msgs);
        Assert.That(msgs, Is.EqualTo(new List<string> { "b" }));
    }

    /// <summary>
    /// C1: if opening the header fails after its FileStream exists, that handle must be
    /// closed. A leaked write handle refuses the repair's own open (on Windows), so every
    /// later write would fail instead of the store recovering on the next one.
    /// </summary>
    [Test]
    public void Failed_header_open_does_not_leak_a_handle_that_blocks_recovery()
    {
        IMessageStore store = _store!;
        store.SetAndIncrNextSenderMsgSeqNum(1, "first");

        bool armed = false;
        bool failOpen = false;
        _store!.HeaderStreamDecorator = s =>
        {
            if (failOpen)
                throw new IOException("simulated failure after the header stream was opened");
            return new ShortWriteStream(s, () => armed);
        };
        store.Refresh();

        armed = true;
        failOpen = true;
        Assert.Throws<IOException>(() => store.SetAndIncrNextSenderMsgSeqNum(2, "second"));
        armed = false;
        failOpen = false;

        Assert.Catch<Exception>(() => store.SetAndIncrNextSenderMsgSeqNum(3, "third"));
        Assert.DoesNotThrow(() => store.SetAndIncrNextSenderMsgSeqNum(4, "fourth"));
    }

    /// <summary>
    /// A constructor that fails after opening some of the store's files must close them, so
    /// the store can be opened again in-process instead of being refused by leaked handles.
    /// The failure is real: a directory where the header file should be, so the header open
    /// throws after .seqnums, its shadow and .body are already open.
    /// </summary>
    [Test]
    public void Failed_construction_releases_the_store_files()
    {
        _store!.NextSenderMsgSeqNum = 5;
        _store.Dispose();

        string headerPath = Path.Combine(_storeDirectory, FileStore.Prefix(_sessionId) + ".header");
        File.Delete(headerPath);
        Directory.CreateDirectory(headerPath);
        Assert.Catch<Exception>(() => _factory!.Create(_sessionId));
        Directory.Delete(headerPath);

        _store = (FileStore)_factory!.Create(_sessionId);
        Assert.That(_store.NextSenderMsgSeqNum, Is.EqualTo(5));
    }

    /// <summary>
    /// A .seqnums write that fails with the process alive leaves .seqnums possibly torn and the
    /// shadow as its only witness. The next update must repair .seqnums before overwriting
    /// that witness: if the new shadow write then tears, the torn .seqnums must not load.
    /// </summary>
    [Test]
    public void Failed_seqnums_write_is_repaired_before_the_witness_is_overwritten()
    {
        _store!.NextTargetMsgSeqNum = 50;
        _store.NextSenderMsgSeqNum = 99;

        bool tearShadow = false;
        // 50 bytes reach the new target digits (byte 42), so the tear really changes the witness.
        _store.SeqNumsShadowStreamDecorator = s => new ShortWriteStream(s, () => tearShadow, landed: 50);
        _store.Refresh();

        // 99 -> 100: shadow written, then the .seqnums write fails.
        _store.AfterSeqNumsShadowWrite = () => throw new IOException("simulated .seqnums write failure");
        Assert.Throws<IOException>(() => _store.NextSenderMsgSeqNum = 100);
        _store.AfterSeqNumsShadowWrite = null;

        // Next update: its shadow write tears.
        tearShadow = true;
        Assert.Throws<IOException>(() => _store.NextTargetMsgSeqNum = 51);
        tearShadow = false;
        _store.SeqNumsShadowStreamDecorator = null;

        RebuildStore();
        Assert.That(_store!.NextSenderMsgSeqNum, Is.EqualTo(100));
    }

    /// <summary>
    /// The repair a load decides on must survive an Open() that fails before writing it: the
    /// next update still has to repair .seqnums under the old shadow before overwriting it.
    /// </summary>
    [Test]
    public void Repair_survives_a_refresh_that_fails_before_writing_it()
    {
        _store!.NextTargetMsgSeqNum = 50;
        _store.NextSenderMsgSeqNum = 99;

        bool tearShadow = false;
        _store.SeqNumsShadowStreamDecorator = s => new ShortWriteStream(s, () => tearShadow, landed: 50);
        _store.Refresh();

        // 99 -> 100: shadow written, then the .seqnums write fails.
        _store.AfterSeqNumsShadowWrite = () => throw new IOException("simulated .seqnums write failure");
        Assert.Throws<IOException>(() => _store.NextSenderMsgSeqNum = 100);
        _store.AfterSeqNumsShadowWrite = null;

        // A Refresh that fails after load, before its repair of .seqnums.
        _store.HeaderStreamDecorator = _ => throw new IOException("simulated header open failure");
        Assert.Throws<IOException>(() => _store.Refresh());
        _store.HeaderStreamDecorator = null;

        // Next update: its shadow write tears.
        tearShadow = true;
        Assert.Catch<Exception>(() => _store.NextTargetMsgSeqNum = 51);
        tearShadow = false;
        _store.SeqNumsShadowStreamDecorator = null;

        RebuildStore();
        Assert.That(_store!.NextSenderMsgSeqNum, Is.EqualTo(100));
    }

    private string SeqNumsPath => Path.Combine(_storeDirectory, FileStore.Prefix(_sessionId) + ".seqnums");
    private string ShadowPath => SeqNumsPath + ".shadow";

    /// <summary>
    /// A partial in-place overwrite of .seqnums leaves the new value's leading digits in front
    /// of the old value's trailing ones (99 -> 100 torn to 199). The record still parses, so it
    /// must be recovered from the checksummed shadow rather than loaded.
    /// </summary>
    private sealed class SimulatedSeqNumsCrash : Exception;

    private static string SeqNumsRecord(ulong sender, ulong target) =>
        sender.ToString("D20") + " : " + target.ToString("D20") + "  ";

    /// <summary>Update the sender number and "die" after the shadow write, before .seqnums.</summary>
    private void CrashInsideSeqNumsUpdate(ulong sender)
    {
        _store!.AfterSeqNumsShadowWrite = () => throw new SimulatedSeqNumsCrash();
        Assert.Throws<SimulatedSeqNumsCrash>(() => _store.NextSenderMsgSeqNum = sender);
        _store.AfterSeqNumsShadowWrite = null;
        _store.Dispose();
    }

    [Test]
    public void Torn_seqnums_record_is_recovered_from_the_shadow()
    {
        _store!.NextTargetMsgSeqNum = 50;
        _store.NextSenderMsgSeqNum = 99;
        CrashInsideSeqNumsUpdate(100);

        // The 99 -> 100 overwrite torn after 18 bytes: new leading digits, old trailing ones.
        File.WriteAllText(SeqNumsPath, SeqNumsRecord(199, 50));

        _store = (FileStore)_factory!.Create(_sessionId);
        Assert.That(_store.NextSenderMsgSeqNum, Is.EqualTo(100));
        Assert.That(_store.NextTargetMsgSeqNum, Is.EqualTo(50));
    }

    /// <summary>
    /// When the shadow is used at load, .seqnums is rewritten at once. Otherwise an
    /// interruption of the next shadow write would lose the only witness and the torn record
    /// would load.
    /// </summary>
    [Test]
    public void Recovery_from_the_shadow_rewrites_seqnums()
    {
        _store!.NextTargetMsgSeqNum = 50;
        _store.NextSenderMsgSeqNum = 99;
        CrashInsideSeqNumsUpdate(100);
        File.WriteAllText(SeqNumsPath, SeqNumsRecord(199, 50));

        _store = (FileStore)_factory!.Create(_sessionId);
        _store.Dispose();

        Assert.That(File.ReadAllText(SeqNumsPath), Is.EqualTo(SeqNumsRecord(100, 50)));
    }

    /// <summary>
    /// The process can die after .seqnums is written but before the shadow is retired. Load
    /// must retire it, or a later rollback of .seqnums to exactly the previous record (the
    /// shape of an overwrite interrupted before its first byte) would be overridden.
    /// </summary>
    [Test]
    public void Shadow_left_by_a_crash_after_the_seqnums_write_is_retired_at_load()
    {
        _store!.NextTargetMsgSeqNum = 50;
        _store.NextSenderMsgSeqNum = 99;
        CrashInsideSeqNumsUpdate(100);
        File.WriteAllText(SeqNumsPath, SeqNumsRecord(100, 50)); // the .seqnums write completed

        _store = (FileStore)_factory!.Create(_sessionId);
        Assert.That(_store.NextSenderMsgSeqNum, Is.EqualTo(100));
        _store.Dispose();

        File.WriteAllText(SeqNumsPath, SeqNumsRecord(99, 50)); // operator rolls back

        _store = (FileStore)_factory!.Create(_sessionId);
        Assert.That(_store.NextSenderMsgSeqNum, Is.EqualTo(99));
    }

    /// <summary>
    /// The recovery at load must repair .seqnums while the shadow still stands as its witness;
    /// rewriting the shadow first would expose the only witness to a torn write.
    /// </summary>
    [Test]
    public void Recovery_at_load_does_not_rewrite_the_shadow()
    {
        _store!.NextTargetMsgSeqNum = 50;
        _store.NextSenderMsgSeqNum = 99;

        // Die after the shadow for 99 -> 100 is written; .seqnums still holds 99/50.
        _store.AfterSeqNumsShadowWrite = () => throw new SimulatedSeqNumsCrash();
        Assert.Throws<SimulatedSeqNumsCrash>(() => _store.NextSenderMsgSeqNum = 100);

        // Reload with the seam still armed: the repair must not write the shadow again.
        Assert.DoesNotThrow(() => _store.Refresh());
        _store.AfterSeqNumsShadowWrite = null;
        Assert.That(_store.NextSenderMsgSeqNum, Is.EqualTo(100));
        _store.Dispose();
        Assert.That(File.ReadAllText(SeqNumsPath), Is.EqualTo(SeqNumsRecord(100, 50)));
        Assert.That(new FileInfo(ShadowPath).Length, Is.EqualTo(0));
    }

    /// <summary>
    /// .seqnums rewritten by someone other than this store (an engine that predates the
    /// shadow, or an operator's deliberate edit) must win; otherwise sequence numbers the
    /// other writer already used would be reused.
    /// </summary>
    [Test]
    public void Seqnums_written_by_another_writer_wins()
    {
        _store!.NextSenderMsgSeqNum = 100;
        _store.NextTargetMsgSeqNum = 50;
        _store.Dispose();

        File.WriteAllText(SeqNumsPath, SeqNumsRecord(150, 60));

        _store = (FileStore)_factory!.Create(_sessionId);
        Assert.That(_store.NextSenderMsgSeqNum, Is.EqualTo(150));
        Assert.That(_store.NextTargetMsgSeqNum, Is.EqualTo(60));
    }

    /// <summary>
    /// An edit made after a completed update can have exactly the shape of a torn overwrite
    /// (99 -> 100 completed, then 199 written by hand). Content alone cannot tell them apart,
    /// so no shadow may survive a completed update.
    /// </summary>
    [Test]
    public void Edit_shaped_like_a_torn_overwrite_wins_after_a_completed_update()
    {
        _store!.NextTargetMsgSeqNum = 50;
        _store.NextSenderMsgSeqNum = 99;
        _store.NextSenderMsgSeqNum = 100;
        _store.Dispose();

        File.WriteAllText(SeqNumsPath, SeqNumsRecord(199, 50));

        _store = (FileStore)_factory!.Create(_sessionId);
        Assert.That(_store.NextSenderMsgSeqNum, Is.EqualTo(199));
    }

    /// <summary>
    /// A torn shadow means the interruption came while it was being written, before .seqnums
    /// was touched, so .seqnums still holds the previous complete record.
    /// </summary>
    [Test]
    public void Torn_shadow_falls_back_to_the_seqnums_record()
    {
        _store!.NextTargetMsgSeqNum = 50;
        CrashInsideSeqNumsUpdate(100);

        // The shadow's new record torn to 109, which would otherwise pass as a partial
        // overwrite of 1/50. Only the checksum can reject it.
        string shadow = File.ReadAllText(ShadowPath);
        File.WriteAllText(ShadowPath, shadow.Substring(0, 19) + "9" + shadow.Substring(20));

        _store = (FileStore)_factory!.Create(_sessionId);
        Assert.That(_store.NextSenderMsgSeqNum, Is.EqualTo(1));
        Assert.That(_store.NextTargetMsgSeqNum, Is.EqualTo(50));
    }

    /// <summary>
    /// A store written before the shadow existed has only .seqnums; it still loads.
    /// </summary>
    [Test]
    public void Seqnums_without_a_shadow_still_load()
    {
        _store!.NextSenderMsgSeqNum = 7;
        _store.NextTargetMsgSeqNum = 9;
        _store.Dispose();
        File.Delete(ShadowPath);

        _store = (FileStore)_factory!.Create(_sessionId);
        Assert.That(_store.NextSenderMsgSeqNum, Is.EqualTo(7));
        Assert.That(_store.NextTargetMsgSeqNum, Is.EqualTo(9));
    }

    /// <summary>
    /// Reset must not leave a stale shadow that would win over the reset sequence numbers.
    /// </summary>
    [Test]
    public void Reset_leaves_no_stale_shadow()
    {
        _store!.NextSenderMsgSeqNum = 100;
        _store.NextTargetMsgSeqNum = 50;

        _store.Reset();
        RebuildStore();

        Assert.That(_store!.NextSenderMsgSeqNum, Is.EqualTo(1));
        Assert.That(_store.NextTargetMsgSeqNum, Is.EqualTo(1));
    }
}
