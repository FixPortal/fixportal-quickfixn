using System;
using System.IO;
using System.Net;
using System.Threading;
using NUnit.Framework;
using QuickFix;
using QuickFix.Logger;
using QuickFix.Store;
using QuickFix.Transport;

namespace UnitTests;

[TestFixture]
public class SocketInitiatorLifecycleTests
{
    private sealed class RecordingStream : Stream
    {
        private readonly ManualResetEventSlim? _disposed;

        public int WriteCount;
        public bool IsDisposed;

        public RecordingStream(ManualResetEventSlim? disposed = null)
        {
            _disposed = disposed;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Interlocked.Increment(ref WriteCount);

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            _disposed?.Set();
            base.Dispose(disposing);
        }
    }

    private sealed class GatedSetupSocketInitiatorThread : SocketInitiatorThread
    {
        private readonly ManualResetEventSlim _setupEntered;
        private readonly ManualResetEventSlim _releaseSetup;
        private readonly Stream _stream;
        private Thread? _worker;

        public GatedSetupSocketInitiatorThread(
            SocketInitiator initiator,
            Session session,
            ManualResetEventSlim setupEntered,
            ManualResetEventSlim releaseSetup,
            Stream stream)
            : base(initiator, session, new IPEndPoint(IPAddress.Loopback, 1), new SocketSettings(),
                new LogFactoryAdapter(new NullLogFactory()))
        {
            _setupEntered = setupEntered;
            _releaseSetup = releaseSetup;
            _stream = stream;
        }

        public new void Start()
        {
            _worker = new Thread(state => SocketInitiator.SocketInitiatorThreadStart(state));
            _worker.Start(this);
        }

        public bool WaitForCompletion(int millisecondsTimeout) => _worker?.Join(millisecondsTimeout) ?? true;

        public new void Join()
        {
            Disconnect();
            if (_worker is not null && Environment.CurrentManagedThreadId != _worker.ManagedThreadId)
                _worker.Join(2000);
            _worker = null;
        }

        protected override Stream SetupStream()
        {
            _setupEntered.Set();
            if (!_releaseSetup.Wait(5000))
                throw new TimeoutException("test did not release connection setup");
            return _stream;
        }
    }

    private class GatedConnectionSocketInitiator : SocketInitiator
    {
        private readonly ManualResetEventSlim _setupEntered;
        private readonly ManualResetEventSlim _releaseSetup;
        private readonly Stream _stream;

        public GatedSetupSocketInitiatorThread? ConnectionThread { get; private set; }

        public GatedConnectionSocketInitiator(
            SessionSettings settings,
            ManualResetEventSlim setupEntered,
            ManualResetEventSlim releaseSetup,
            Stream stream)
            : base(new SessionTestSupport.MockApplication(), new MemoryStoreFactory(), settings,
                (ILogFactory?)new NullLogFactory())
        {
            _setupEntered = setupEntered;
            _releaseSetup = releaseSetup;
            _stream = stream;
        }

        protected override void DoConnect(Session session, SettingsDictionary settings)
        {
            ConnectionThread = new GatedSetupSocketInitiatorThread(
                this, session, _setupEntered, _releaseSetup, _stream);
            ConnectionThread.Start();
        }

        protected override void OnStop()
        {
            ConnectionThread?.Disconnect();
            base.OnStop();
        }
    }

    private sealed class GatedRemovalSocketInitiator : GatedConnectionSocketInitiator
    {
        private readonly ManualResetEventSlim _removeEntered;
        private readonly ManualResetEventSlim _releaseRemove;

        public GatedRemovalSocketInitiator(
            SessionSettings settings,
            ManualResetEventSlim setupEntered,
            ManualResetEventSlim releaseSetup,
            Stream stream,
            ManualResetEventSlim removeEntered,
            ManualResetEventSlim releaseRemove)
            : base(settings, setupEntered, releaseSetup, stream)
        {
            _removeEntered = removeEntered;
            _releaseRemove = releaseRemove;
        }

        protected override void OnRemove(SessionID sessionId)
        {
            _removeEntered.Set();
            _releaseRemove.Wait();
            base.OnRemove(sessionId);
        }
    }

    private sealed class GatedSocketInitiator : SocketInitiator
    {
        private readonly ManualResetEventSlim _workerEntered;
        private readonly ManualResetEventSlim _releaseWorker;
        private readonly ManualResetEventSlim _workerExited;
        private readonly ManualResetEventSlim _stopEntered;

        public GatedSocketInitiator(
            SessionSettings settings,
            ManualResetEventSlim workerEntered,
            ManualResetEventSlim releaseWorker,
            ManualResetEventSlim workerExited,
            ManualResetEventSlim stopEntered)
            : base(new SessionTestSupport.MockApplication(), new MemoryStoreFactory(), settings,
                (ILogFactory?)new NullLogFactory())
        {
            _workerEntered = workerEntered;
            _releaseWorker = releaseWorker;
            _workerExited = workerExited;
            _stopEntered = stopEntered;
        }

        protected override void OnStart()
        {
            _workerEntered.Set();
            _releaseWorker.Wait();
            try
            {
                base.OnStart();
            }
            finally
            {
                _workerExited.Set();
            }
        }

        protected override void OnStop()
        {
            _stopEntered.Set();
            base.OnStop();
        }

        public void RequestWorkerStop() => base.OnStop();
    }

    private static SessionSettings InitiatorSettings()
    {
        var session = new SettingsDictionary();
        session.SetString(SessionSettings.CONNECTION_TYPE, "initiator");
        session.SetString(SessionSettings.USE_DATA_DICTIONARY, "N");
        session.SetString(SessionSettings.START_TIME, "12:00:00");
        session.SetString(SessionSettings.END_TIME, "12:00:00");
        session.SetString(SessionSettings.HEARTBTINT, "30");
        session.SetString(SessionSettings.SOCKET_CONNECT_HOST, "127.0.0.1");
        session.SetString(SessionSettings.SOCKET_CONNECT_PORT, "65530");

        var settings = new SessionSettings();
        settings.Set(new SessionID("FIX.4.2", "INIT_SENDER", "INIT_TARGET"), session);
        return settings;
    }

    [Test]
    public void StopWhileConnectionSetupIsBlockedRejectsItsLateStream()
    {
        using var setupEntered = new ManualResetEventSlim();
        using var releaseSetup = new ManualResetEventSlim();
        var stream = new RecordingStream();
        using var initiator = new GatedConnectionSocketInitiator(
            InitiatorSettings(), setupEntered, releaseSetup, stream);

        try
        {
            initiator.Start();
            Assert.That(setupEntered.Wait(5000), Is.True, "connection setup should reach its gate");

            initiator.Stop(force: true);
            Assert.That(initiator.ConnectionThread!.Session.Disposed, Is.True,
                "stop should dispose the session before setup completes");

            releaseSetup.Set();
            initiator.ConnectionThread.Join();

            Assert.That(stream.IsDisposed, Is.True, "a stream completed after disconnect must be disposed");
            Assert.That(stream.WriteCount, Is.Zero, "a stopped session must not publish a late Logon");
        }
        finally
        {
            releaseSetup.Set();
            initiator.ConnectionThread?.Disconnect();
            initiator.ConnectionThread?.Join();
        }
    }

    [Test]
    public void RemoveWhileConnectionSetupIsBlockedRejectsItsLateActivation()
    {
        using var setupEntered = new ManualResetEventSlim();
        using var releaseSetup = new ManualResetEventSlim();
        using var removeEntered = new ManualResetEventSlim();
        using var releaseRemove = new ManualResetEventSlim();
        using var streamDisposed = new ManualResetEventSlim();
        var stream = new RecordingStream(streamDisposed);
        using var initiator = new GatedRemovalSocketInitiator(
            InitiatorSettings(), setupEntered, releaseSetup, stream, removeEntered, releaseRemove);
        Thread? remover = null;
        bool removed = false;

        try
        {
            initiator.Start();
            Assert.That(setupEntered.Wait(5000), Is.True, "connection setup should reach its gate");

            SessionID sessionId = initiator.ConnectionThread!.Session.SessionID;
            remover = new Thread(() => removed = initiator.RemoveSession(sessionId, terminateActiveSession: true));
            remover.Start();
            Assert.That(removeEntered.Wait(5000), Is.True,
                "removal should clear initiator state before transport cleanup starts");

            releaseSetup.Set();

            Assert.That(initiator.ConnectionThread.WaitForCompletion(5000), Is.True,
                "the connection worker should complete while transport removal remains paused");
            Assert.That(stream.WriteCount, Is.Zero, "a removed session must not publish a late Logon");
            initiator.ConnectionThread.Disconnect();
            Assert.That(streamDisposed.Wait(5000), Is.True, "completed worker stream should be released");

            releaseRemove.Set();
            Assert.That(remover.Join(5000), Is.True, "session removal should complete after cleanup is released");
            Assert.That(removed, Is.True);
        }
        finally
        {
            releaseSetup.Set();
            releaseRemove.Set();
            initiator.ConnectionThread?.Disconnect();
            initiator.ConnectionThread?.Join();
            remover?.Join(5000);
        }
    }

    [Test]
    public void StopBeforeWorkerLoopStartsDoesNotGetResetByWorker()
    {
        using var workerEntered = new ManualResetEventSlim();
        using var releaseWorker = new ManualResetEventSlim();
        using var workerExited = new ManualResetEventSlim();
        using var stopEntered = new ManualResetEventSlim();
        using var initiator = new GatedSocketInitiator(
            InitiatorSettings(), workerEntered, releaseWorker, workerExited, stopEntered);
        Thread? stopper = null;

        try
        {
            initiator.Start();
            Assert.That(workerEntered.Wait(5000), Is.True, "worker should reach the pre-loop gate");

            stopper = new Thread(() => initiator.Stop(force: true));
            stopper.Start();
            Assert.That(stopEntered.Wait(5000), Is.True, "stop should set shutdown before the worker loop starts");

            releaseWorker.Set();

            Assert.That(workerExited.Wait(5000), Is.True,
                "worker must not reset a shutdown requested before its loop starts");
            Assert.That(stopper.Join(5000), Is.True, "stop should complete after the worker exits");
        }
        finally
        {
            releaseWorker.Set();
            initiator.RequestWorkerStop();
            workerExited.Wait(5000);
            stopper?.Join(5000);
        }
    }
}
