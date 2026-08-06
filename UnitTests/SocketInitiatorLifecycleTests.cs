using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using QuickFix;
using QuickFix.Logger;
using QuickFix.Store;
using QuickFix.Transport;

namespace UnitTests;

[TestFixture]
[NonParallelizable]
public class SocketInitiatorLifecycleTests
{
    private sealed class SettingsReadingApplication(SessionSettings settings) : IApplication
    {
        public ManualResetEventSlim ToAdminCalled { get; } = new();
        public ManualResetEventSlim LoggedOn { get; } = new();
        public string? ObservedHmacSecret { get; private set; }

        public void ToAdmin(Message message, SessionID sessionId)
        {
            ObservedHmacSecret = settings.Get(sessionId).GetString("FPSimHmacSecret");
            ToAdminCalled.Set();
        }

        public void FromAdmin(Message message, SessionID sessionId) { }
        public void ToApp(Message message, SessionID sessionId) { }
        public void FromApp(Message message, SessionID sessionId) { }
        public void OnCreate(SessionID sessionId) { }
        public void OnLogout(SessionID sessionId) { }
        public void OnLogon(SessionID sessionId) => LoggedOn.Set();
    }

    private sealed class GatedHeartbeatMessageFactory : IMessageFactory
    {
        private readonly IMessageFactory _inner = new DefaultMessageFactory();
        private readonly ManualResetEventSlim _heartbeatCreationEntered;
        private readonly ManualResetEventSlim _releaseHeartbeatCreation;

        public GatedHeartbeatMessageFactory(
            ManualResetEventSlim heartbeatCreationEntered,
            ManualResetEventSlim releaseHeartbeatCreation)
        {
            _heartbeatCreationEntered = heartbeatCreationEntered;
            _releaseHeartbeatCreation = releaseHeartbeatCreation;
        }

        public ICollection<string> GetSupportedBeginStrings() => _inner.GetSupportedBeginStrings();

        public Message Create(string beginString, string msgType)
        {
            if (msgType == QuickFix.Fields.MsgType.HEARTBEAT)
            {
                _heartbeatCreationEntered.Set();
                _releaseHeartbeatCreation.Wait();
            }

            return _inner.Create(beginString, msgType);
        }

        public Message Create(string beginString, QuickFix.Fields.ApplVerID applVerId, string msgType) =>
            _inner.Create(beginString, applVerId, msgType);

        public Group? Create(string beginString, string msgType, int groupCounterTag) =>
            _inner.Create(beginString, msgType, groupCounterTag);
    }

    private sealed class GatedConnectionTypeComparer : IEqualityComparer<string>
    {
        private readonly ManualResetEventSlim _admissionEntered;
        private readonly ManualResetEventSlim _releaseAdmission;
        private readonly SessionSettings _settings;
        private readonly SessionID _sessionId;
        private int _addThreadId;
        private int _admissionGated;

        public GatedConnectionTypeComparer(
            SessionSettings settings,
            SessionID sessionId,
            ManualResetEventSlim admissionEntered,
            ManualResetEventSlim releaseAdmission)
        {
            _settings = settings;
            _sessionId = sessionId;
            _admissionEntered = admissionEntered;
            _releaseAdmission = releaseAdmission;
        }

        public void GateAdmissionOnCurrentThread()
        {
            _addThreadId = Environment.CurrentManagedThreadId;
            _admissionGated = 0;
        }

        public bool Equals(string? x, string? y) => StringComparer.Ordinal.Equals(x, y);

        public int GetHashCode(string key)
        {
            if (Environment.CurrentManagedThreadId == Volatile.Read(ref _addThreadId)
                && key == "CONNECTIONTYPE"
                && _settings.Has(_sessionId)
                && Interlocked.Exchange(ref _admissionGated, 1) == 0)
            {
                _admissionEntered.Set();
                _releaseAdmission.Wait();
            }

            return StringComparer.Ordinal.GetHashCode(key);
        }
    }

    private static void SetComparer(SettingsDictionary settings, IEqualityComparer<string> comparer)
    {
        FieldInfo dataField = typeof(SettingsDictionary).GetField(
            "_data", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("SettingsDictionary._data was not found");
        var data = (Dictionary<string, string>)dataField.GetValue(settings)!;
        dataField.SetValue(settings, new Dictionary<string, string>(data, comparer));
    }

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
            _releaseSetup.Wait();
            return _stream;
        }
    }

    private sealed class FailingSetupSocketInitiatorThread : SocketInitiatorThread
    {
        public FailingSetupSocketInitiatorThread(
            SocketInitiator initiator,
            Session session,
            IQuickFixLoggerFactory loggerFactory)
            : base(initiator, session, new IPEndPoint(IPAddress.Loopback, 1), new SocketSettings(), loggerFactory)
        {
        }

        protected override Stream SetupStream() => throw new IOException("setup failed");
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

    private sealed class GatedSettingsLifetimeSocketInitiator : SocketInitiator
    {
        private readonly ManualResetEventSlim _removeEntered;
        private readonly ManualResetEventSlim _releaseRemove;

        public GatedSettingsLifetimeSocketInitiator(
            IApplication application,
            SessionSettings settings,
            ManualResetEventSlim removeEntered,
            ManualResetEventSlim releaseRemove)
            : base(application, new MemoryStoreFactory(), settings, (ILogFactory?)new NullLogFactory())
        {
            _removeEntered = removeEntered;
            _releaseRemove = releaseRemove;
        }

        protected override void DoConnect(Session session, SettingsDictionary settings) { }

        protected override void OnRemove(SessionID sessionId)
        {
            _removeEntered.Set();
            _releaseRemove.Wait();
            base.OnRemove(sessionId);
        }
    }

    private sealed class AbaSocketInitiator : SocketInitiator
    {
        private readonly ManualResetEventSlim _oldSetupEntered;
        private readonly ManualResetEventSlim _releaseOldSetup;
        private readonly Stream _oldStream;
        private int _connectionAttempts;

        public GatedSetupSocketInitiatorThread? OldThread { get; private set; }

        public AbaSocketInitiator(
            SessionSettings settings,
            ManualResetEventSlim oldSetupEntered,
            ManualResetEventSlim releaseOldSetup,
            Stream oldStream)
            : base(new SessionTestSupport.MockApplication(), new MemoryStoreFactory(), settings,
                (ILogFactory?)new NullLogFactory())
        {
            _oldSetupEntered = oldSetupEntered;
            _releaseOldSetup = releaseOldSetup;
            _oldStream = oldStream;
        }

        protected override void DoConnect(Session session, SettingsDictionary settings)
        {
            if (Interlocked.Increment(ref _connectionAttempts) == 1)
            {
                SetPending(session.SessionID);
                OldThread = new GatedSetupSocketInitiatorThread(
                    this, session, _oldSetupEntered, _releaseOldSetup, _oldStream);
                OldThread.Start();
                return;
            }

            base.DoConnect(session, settings);
        }

        protected override void OnRemove(SessionID sessionId)
        {
            OldThread?.WaitForCompletion(2000);
            base.OnRemove(sessionId);
        }

        protected override void OnStop()
        {
            OldThread?.Disconnect();
            base.OnStop();
        }
    }

    private sealed class GatedDisposeStore : MemoryStore
    {
        private readonly ManualResetEventSlim _disposeEntered;
        private readonly ManualResetEventSlim _releaseDispose;

        public GatedDisposeStore(ManualResetEventSlim disposeEntered, ManualResetEventSlim releaseDispose)
        {
            _disposeEntered = disposeEntered;
            _releaseDispose = releaseDispose;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _disposeEntered.Set();
                _releaseDispose.Wait();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class GatedFirstStoreFactory : IMessageStoreFactory
    {
        private readonly ManualResetEventSlim _disposeEntered;
        private readonly ManualResetEventSlim _releaseDispose;
        private int _created;

        public GatedFirstStoreFactory(
            ManualResetEventSlim disposeEntered,
            ManualResetEventSlim releaseDispose)
        {
            _disposeEntered = disposeEntered;
            _releaseDispose = releaseDispose;
        }

        public IMessageStore Create(SessionID sessionId)
        {
            return Interlocked.Increment(ref _created) == 1
                ? new GatedDisposeStore(_disposeEntered, _releaseDispose)
                : new MemoryStore();
        }
    }

    private sealed class ThrowingDisposeStore : MemoryStore
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                throw new InvalidOperationException("dispose failed");
            base.Dispose(disposing);
        }
    }

    private sealed class ThrowingFirstStoreFactory : IMessageStoreFactory
    {
        private int _created;

        public IMessageStore Create(SessionID sessionId) =>
            Interlocked.Increment(ref _created) == 1 ? new ThrowingDisposeStore() : new MemoryStore();
    }

    private sealed class ThrowingConnectionFailureLog : ILog
    {
        public void Clear() { }
        public void OnIncoming(string msg) { }
        public void OnOutgoing(string msg) { }
        public void OnEvent(string message)
        {
            if (message.StartsWith("Connection failed:", StringComparison.Ordinal))
                throw new InvalidOperationException("logging failed");
        }
        public void Dispose() { }
    }

    private sealed class ThrowingConnectionFailureLogFactory : ILogFactory
    {
        public ILog Create(SessionID sessionId) => new ThrowingConnectionFailureLog();
        public ILog CreateNonSessionLog() => new NullLog();
    }

    private sealed class NoConnectionSocketInitiator : SocketInitiator
    {
        public NoConnectionSocketInitiator(
            SessionSettings settings,
            IMessageStoreFactory storeFactory,
            ILogFactory? logFactory = null)
            : base(new SessionTestSupport.MockApplication(), storeFactory, settings,
                logFactory ?? new NullLogFactory())
        {
        }

        protected override void DoConnect(Session session, SettingsDictionary settings)
        {
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

        protected override void DoConnect(Session session, SettingsDictionary settings)
        {
        }

        public void RequestWorkerStop() => base.OnStop();
    }

    private sealed class BlockedSchedulingSocketInitiator : SocketInitiator
    {
        private readonly ManualResetEventSlim _connectEntered;
        private readonly ManualResetEventSlim _releaseConnect;

        public BlockedSchedulingSocketInitiator(
            SessionSettings settings,
            ManualResetEventSlim connectEntered,
            ManualResetEventSlim releaseConnect)
            : base(new SessionTestSupport.MockApplication(), new MemoryStoreFactory(), settings,
                (ILogFactory?)new NullLogFactory())
        {
            _connectEntered = connectEntered;
            _releaseConnect = releaseConnect;
        }

        protected override void DoConnect(Session session, SettingsDictionary settings)
        {
            _connectEntered.Set();
            _releaseConnect.Wait();
        }

        public void RequestWorkerStop() => base.OnStop();
    }

    private sealed class GatedMissingRemovalSocketInitiator : SocketInitiator
    {
        private readonly SessionID _gatedSessionId;
        private readonly ManualResetEventSlim _removeEntered;
        private readonly ManualResetEventSlim _releaseRemove;

        public GatedMissingRemovalSocketInitiator(
            SessionSettings settings,
            SessionID gatedSessionId,
            ManualResetEventSlim removeEntered,
            ManualResetEventSlim releaseRemove)
            : base(new SessionTestSupport.MockApplication(), new MemoryStoreFactory(), settings,
                (ILogFactory?)new NullLogFactory())
        {
            _gatedSessionId = gatedSessionId;
            _removeEntered = removeEntered;
            _releaseRemove = releaseRemove;
        }

        protected override void DoConnect(Session session, SettingsDictionary settings)
        {
            Interlocked.Increment(ref ConnectionAttempts);
        }

        public int ConnectionAttempts;
        public void ConnectForTest(SessionID sessionId) => Connect(sessionId);

        protected override void OnRemove(SessionID sessionId)
        {
            if (sessionId.Equals(_gatedSessionId))
            {
                _removeEntered.Set();
                _releaseRemove.Wait();
            }
            base.OnRemove(sessionId);
        }
    }

    private sealed class ConcurrentStopSocketInitiator : SocketInitiator
    {
        private readonly ManualResetEventSlim _configureEntered;
        private readonly ManualResetEventSlim _releaseConfigure;
        private readonly ManualResetEventSlim _firstStopPaused;
        private readonly ManualResetEventSlim _releaseFirstStop;
        private int _configureCalls;
        private int _stopCalls;

        public ConcurrentStopSocketInitiator(
            SessionSettings settings,
            ManualResetEventSlim configureEntered,
            ManualResetEventSlim releaseConfigure,
            ManualResetEventSlim firstStopPaused,
            ManualResetEventSlim releaseFirstStop)
            : base(new SessionTestSupport.MockApplication(), new MemoryStoreFactory(), settings,
                (ILogFactory?)new NullLogFactory())
        {
            _configureEntered = configureEntered;
            _releaseConfigure = releaseConfigure;
            _firstStopPaused = firstStopPaused;
            _releaseFirstStop = releaseFirstStop;
        }

        public ManualResetEventSlim SecondStopEntered { get; } = new();
        public int WorkerStarts;
        public int FirstStopCompleted;
        public int SecondStopEnteredBeforeFirstCompleted;

        protected override void OnConfigure(SessionSettings settings)
        {
            if (Interlocked.Increment(ref _configureCalls) == 1)
            {
                _configureEntered.Set();
                _releaseConfigure.Wait();
            }
            base.OnConfigure(settings);
        }

        protected override void OnStart() => Interlocked.Increment(ref WorkerStarts);

        protected override void OnStop()
        {
            int stopCall = Interlocked.Increment(ref _stopCalls);
            base.OnStop();
            if (stopCall == 1)
            {
                _firstStopPaused.Set();
                _releaseFirstStop.Wait();
                Interlocked.Exchange(ref FirstStopCompleted, 1);
            }
            else
            {
                if (Volatile.Read(ref FirstStopCompleted) == 0)
                    Interlocked.Exchange(ref SecondStopEnteredBeforeFirstCompleted, 1);
                SecondStopEntered.Set();
            }
        }

        protected override void DoConnect(Session session, SettingsDictionary settings)
        {
        }
    }

    private static SessionSettings InitiatorSettings(int port = 65530, bool useSsl = false)
    {
        var session = new SettingsDictionary();
        session.SetString(SessionSettings.CONNECTION_TYPE, "initiator");
        session.SetString(SessionSettings.USE_DATA_DICTIONARY, "N");
        session.SetString(SessionSettings.START_TIME, "12:00:00");
        session.SetString(SessionSettings.END_TIME, "12:00:00");
        session.SetString(SessionSettings.HEARTBTINT, "30");
        session.SetString(SessionSettings.SOCKET_CONNECT_HOST, "127.0.0.1");
        session.SetLong(SessionSettings.SOCKET_CONNECT_PORT, port);
        if (useSsl)
        {
            session.SetBool(SessionSettings.SSL_ENABLE, true);
            session.SetString(SessionSettings.SSL_SERVERNAME, "localhost");
            session.SetBool(SessionSettings.SSL_VALIDATE_CERTIFICATES, false);
            session.SetBool(SessionSettings.SSL_CHECK_CERTIFICATE_REVOCATION, false);
            session.SetBool(SessionSettings.SOCKET_IGNORE_PROXY, true);
        }

        var settings = new SessionSettings();
        settings.Set(new SessionID("FIX.4.2", "INIT_SENDER", "INIT_TARGET"), session);
        return settings;
    }

    private static X509Certificate2 CreateServerCertificate()
    {
        using RSA rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 generated = request.CreateSelfSigned(
            new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2040, 1, 1, 0, 0, 0, TimeSpan.Zero));
        return X509CertificateLoader.LoadPkcs12(
            generated.Export(X509ContentType.Pfx), password: null, X509KeyStorageFlags.Exportable);
    }

    private static void SendToInitiator(NetworkStream stream, Message message, int sequenceNumber)
    {
        message.Header.SetField(new QuickFix.Fields.SenderCompID("INIT_TARGET"));
        message.Header.SetField(new QuickFix.Fields.TargetCompID("INIT_SENDER"));
        message.Header.SetField(new QuickFix.Fields.MsgSeqNum((ulong)sequenceNumber));
        message.Header.SetField(new QuickFix.Fields.SendingTime(DateTime.UtcNow));
        byte[] bytes = Encoding.ASCII.GetBytes(message.ConstructString());
        stream.Write(bytes);
    }

    [Test]
    public void StopWhileConnectionSetupIsBlockedRejectsItsLateStream()
    {
        using var setupEntered = new ManualResetEventSlim();
        using var releaseSetup = new ManualResetEventSlim();
        var stream = new RecordingStream();
        using var initiator = new GatedConnectionSocketInitiator(
            InitiatorSettings(), setupEntered, releaseSetup, stream);
        GatedSetupSocketInitiatorThread? connectionThread = null;

        try
        {
            initiator.Start();
            Assert.That(setupEntered.Wait(5000), Is.True, "connection setup should reach its gate");
            connectionThread = initiator.ConnectionThread
                ?? throw new InvalidOperationException("Connection thread was not created");

            initiator.Stop(force: true);
            Assert.That(connectionThread.Session.Disposed, Is.True,
                "stop should dispose the session before setup completes");

            releaseSetup.Set();
            connectionThread.Join();

            Assert.That(stream.IsDisposed, Is.True, "a stream completed after disconnect must be disposed");
            Assert.That(stream.WriteCount, Is.Zero, "a stopped session must not publish a late Logon");
        }
        finally
        {
            releaseSetup.Set();
            connectionThread?.Disconnect();
            connectionThread?.Join();
        }
    }

    [Test]
    public void RemoveWhileConnectionSetupIsBlockedReservesIdAndRejectsLateActivation()
    {
        using var setupEntered = new ManualResetEventSlim();
        using var releaseSetup = new ManualResetEventSlim();
        using var removeEntered = new ManualResetEventSlim();
        using var releaseRemove = new ManualResetEventSlim();
        using var streamDisposed = new ManualResetEventSlim();
        var stream = new RecordingStream(streamDisposed);
        SessionSettings settings = InitiatorSettings();
        var sessionId = new SessionID("FIX.4.2", "INIT_SENDER", "INIT_TARGET");
        SettingsDictionary sessionSettings = settings.Get(sessionId);
        using var initiator = new GatedRemovalSocketInitiator(
            settings, setupEntered, releaseSetup, stream, removeEntered, releaseRemove);
        Thread? remover = null;
        bool removed = false;

        try
        {
            initiator.Start();
            Assert.That(setupEntered.Wait(5000), Is.True, "connection setup should reach its gate");
            GatedSetupSocketInitiatorThread connectionThread = initiator.ConnectionThread!;

            remover = new Thread(() => removed = initiator.RemoveSession(sessionId, terminateActiveSession: true));
            remover.Start();
            Assert.That(removeEntered.Wait(5000), Is.True,
                "removal should clear initiator state before transport cleanup starts");
            Assert.That(initiator.AddSession(sessionId, sessionSettings), Is.False,
                "same-ID re-add must wait for transport cleanup");

            releaseSetup.Set();

            Assert.That(connectionThread.WaitForCompletion(5000), Is.True,
                "the connection worker should complete while transport removal remains paused");
            Assert.That(stream.WriteCount, Is.Zero, "a removed session must not publish a late Logon");
            connectionThread.Disconnect();
            Assert.That(streamDisposed.Wait(5000), Is.True, "completed worker stream should be released");

            releaseRemove.Set();
            Assert.That(remover.Join(5000), Is.True, "session removal should complete after cleanup is released");
            Assert.That(removed, Is.True);
            Assert.That(initiator.AddSession(sessionId, sessionSettings), Is.True,
                "same-ID re-add should succeed after removal returns");
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
    public void RemoveSessionReservesIdUntilOldSessionDisposeCompletes()
    {
        using var disposeEntered = new ManualResetEventSlim();
        using var releaseDispose = new ManualResetEventSlim();
        SessionSettings settings = InitiatorSettings();
        var sessionId = new SessionID("FIX.4.2", "INIT_SENDER", "INIT_TARGET");
        SettingsDictionary sessionSettings = settings.Get(sessionId);
        var storeFactory = new GatedFirstStoreFactory(disposeEntered, releaseDispose);
        using var initiator = new NoConnectionSocketInitiator(settings, storeFactory);
        Thread? remover = null;
        bool removed = false;

        try
        {
            initiator.Start();
            remover = new Thread(() => removed = initiator.RemoveSession(sessionId, terminateActiveSession: true));
            remover.Start();
            Assert.That(disposeEntered.Wait(5000), Is.True,
                "old session disposal should begin after transport cleanup");

            Assert.That(initiator.RemoveSession(sessionId, terminateActiveSession: true), Is.True,
                "a duplicate removal should be idempotent while disposal is in progress");
            Assert.That(initiator.AddSession(sessionId, sessionSettings), Is.False,
                "duplicate removal must not release the same-ID reservation before old disposal");

            releaseDispose.Set();
            Assert.That(remover.Join(5000), Is.True, "session removal should complete after disposal is released");
            Assert.That(removed, Is.True);
            Assert.That(initiator.AddSession(sessionId, sessionSettings), Is.True,
                "same-ID re-add should succeed after removal returns");
            Assert.That(Session.LookupSession(sessionId), Is.Not.Null,
                "replacement should remain registered after old session disposal");
        }
        finally
        {
            releaseDispose.Set();
            remover?.Join(5000);
        }
    }

    [Test]
    public void RemoveSessionRetainsOldSettingsUntilTransportCleanupCompletes()
    {
        using var removeEntered = new ManualResetEventSlim();
        using var releaseRemove = new ManualResetEventSlim();
        SessionSettings settings = InitiatorSettings();
        var sessionId = new SessionID("FIX.4.2", "INIT_SENDER", "INIT_TARGET");
        settings.Get(sessionId).SetString("FPSimHmacSecret", "generation-secret");
        var application = new SettingsReadingApplication(settings);
        using var initiator = new GatedSettingsLifetimeSocketInitiator(
            application, settings, removeEntered, releaseRemove);
        Thread? remover = null;

        try
        {
            initiator.Start();
            remover = new Thread(() => initiator.RemoveSession(sessionId, terminateActiveSession: true));
            remover.Start();
            Assert.That(removeEntered.Wait(5000), Is.True,
                "removal should reach transport cleanup before the old generation's final callback");

            Assert.That(() => application.ToAdmin(new Message(), sessionId), Throws.Nothing,
                "an old-generation ToAdmin can still run until transport cleanup quiesces");
            Assert.That(application.ObservedHmacSecret, Is.EqualTo("generation-secret"));

            releaseRemove.Set();
            Assert.That(remover.Join(5000), Is.True);
            Assert.That(settings.Has(sessionId), Is.False,
                "the old generation's settings should be detached after transport cleanup");
        }
        finally
        {
            releaseRemove.Set();
            remover?.Join(5000);
        }
    }

    [Test]
    public void ReaderExitCompletionRunsWhenConnectionFailureLoggingThrows()
    {
        SessionSettings settings = InitiatorSettings();
        var sessionId = new SessionID("FIX.4.2", "INIT_SENDER", "INIT_TARGET");
        var logFactory = new ThrowingConnectionFailureLogFactory();
        using var initiator = new NoConnectionSocketInitiator(settings, new MemoryStoreFactory(), logFactory);
        initiator.Start();
        Session session = Session.LookupSession(sessionId)!;
        var thread = new FailingSetupSocketInitiatorThread(
            initiator, session, new LogFactoryAdapter(logFactory));
        int completionCalls = 0;
        Assert.That(thread.TryRunWhenExited(() =>
        {
            Interlocked.Increment(ref completionCalls);
            throw new ApplicationException("completion failed");
        }), Is.True);

        Assert.That(
            () => SocketInitiator.SocketInitiatorThreadStart(thread),
            Throws.Nothing,
            "a reader-thread cleanup failure must not escape and terminate the host process");
        Assert.That(Volatile.Read(ref completionCalls), Is.EqualTo(1),
            "reader-exit completion must run even when connection-failure logging throws");
    }

    [Test]
    public void JoinUsesStableWorkerReferenceWhenReaderSignalsExit()
    {
        SessionSettings settings = InitiatorSettings();
        var sessionId = new SessionID("FIX.4.2", "INIT_SENDER", "INIT_TARGET");
        using var initiator = new NoConnectionSocketInitiator(settings, new MemoryStoreFactory());
        initiator.Start();
        var thread = new SocketInitiatorThread(
            initiator,
            Session.LookupSession(sessionId)!,
            new IPEndPoint(IPAddress.Loopback, 1),
            new SocketSettings(),
            new LogFactoryAdapter(new NullLogFactory()));
        using var releaseWorker = new ManualResetEventSlim();
        var worker = new Thread(releaseWorker.Wait);
        worker.Start();
        typeof(SocketInitiatorThread).GetField("_thread", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(thread, worker);
        var readCompletion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(SocketInitiatorThread).GetField("_currentReadTask", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(thread, readCompletion.Task);
        Exception? joinFailure = null;
        var joiner = new Thread(() =>
        {
            try { thread.Join(); }
            catch (Exception ex) { joinFailure = ex; }
        });

        try
        {
            joiner.Start();
            Assert.That(SpinWait.SpinUntil(
                () => joiner.ThreadState.HasFlag(ThreadState.WaitSleepJoin), 5000), Is.True);
            thread.SignalExited();
            readCompletion.SetResult(0);
            releaseWorker.Set();

            Assert.That(joiner.Join(5000), Is.True);
            Assert.That(joinFailure, Is.Null);
        }
        finally
        {
            readCompletion.TrySetResult(0);
            releaseWorker.Set();
            joiner.Join(5000);
            worker.Join(5000);
        }
    }

    [TestCase(false, TestName = "RemoveSessionRetainsReservationUntilTimedOutReaderActuallyExits")]
    [TestCase(true, TestName = "RemoveSessionReleasesReservationWhenDeferredDisposalThrows")]
    public void RemoveSessionDefersCompletionUntilTimedOutReaderActuallyExits(bool disposalThrows)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var heartbeatCreationEntered = new ManualResetEventSlim();
        using var releaseHeartbeatCreation = new ManualResetEventSlim();
        SessionSettings settings = InitiatorSettings(port);
        var sessionId = new SessionID("FIX.4.2", "INIT_SENDER", "INIT_TARGET");
        SettingsDictionary sessionSettings = settings.Get(sessionId);
        sessionSettings.SetString("FPSimHmacSecret", "generation-secret");
        var application = new SettingsReadingApplication(settings);
        var messageFactory = new GatedHeartbeatMessageFactory(
            heartbeatCreationEntered, releaseHeartbeatCreation);
        IMessageStoreFactory storeFactory = disposalThrows
            ? new ThrowingFirstStoreFactory()
            : new MemoryStoreFactory();
        using var initiator = new SocketInitiator(
            application, storeFactory, settings, (ILogFactory?)new NullLogFactory(), messageFactory);
        TcpClient? peer = null;
        Thread? remover = null;
        bool removed = false;

        try
        {
            Task<TcpClient> accept = listener.AcceptTcpClientAsync();
            initiator.Start();
            Assert.That(accept.Wait(5000), Is.True, "the old generation should establish its socket");
            peer = accept.Result;
            NetworkStream peerStream = peer.GetStream();
            SendToInitiator(
                peerStream,
                new QuickFix.FIX42.Logon(
                    new QuickFix.Fields.EncryptMethod(0), new QuickFix.Fields.HeartBtInt(30)),
                sequenceNumber: 1);
            Assert.That(application.LoggedOn.Wait(5000), Is.True,
                "the old generation should complete logon before the gated callback");
            SendToInitiator(
                peerStream,
                new QuickFix.FIX42.TestRequest(new QuickFix.Fields.TestReqID("quiescence")),
                sequenceNumber: 2);
            Assert.That(heartbeatCreationEntered.Wait(5000), Is.True,
                "the old reader should pause after activation and before heartbeat ToAdmin");

            remover = new Thread(() => removed = initiator.RemoveSession(sessionId, terminateActiveSession: true));
            remover.Start();
            Assert.That(remover.Join(5000), Is.True,
                "removal should retain its established bounded-return behavior when the reader does not exit");
            Assert.That(removed, Is.True);
            Assert.That(settings.Has(sessionId), Is.True,
                "old-generation settings must remain available until its reader actually exits");
            Assert.That(initiator.AddSession(sessionId, sessionSettings), Is.False,
                "replacement admission must remain reserved while an old-generation admin callback can still run");

            application.ToAdminCalled.Reset();
            releaseHeartbeatCreation.Set();
            Assert.That(application.ToAdminCalled.Wait(5000), Is.True,
                "the old-generation callback should finish with its retained settings");
            Assert.That(application.ObservedHmacSecret, Is.EqualTo("generation-secret"));
            Assert.That(SpinWait.SpinUntil(() => !settings.Has(sessionId), 5000), Is.True,
                "actual reader exit should detach the old settings and release removal");
            Assert.That(initiator.AddSession(sessionId, sessionSettings), Is.True,
                "replacement admission should succeed after actual reader exit");
            Assert.That(Session.LookupSession(sessionId), Is.Not.Null);
        }
        finally
        {
            releaseHeartbeatCreation.Set();
            remover?.Join(5000);
            peer?.Dispose();
            listener.Stop();
        }
    }

    [Test]
    public void RemoveSessionReleasesSettingsAndReservationWhenDisposalThrows()
    {
        SessionSettings settings = InitiatorSettings();
        var sessionId = new SessionID("FIX.4.2", "INIT_SENDER", "INIT_TARGET");
        SettingsDictionary sessionSettings = settings.Get(sessionId);
        using var initiator = new NoConnectionSocketInitiator(settings, new ThrowingFirstStoreFactory());

        initiator.Start();

        Assert.That(
            () => initiator.RemoveSession(sessionId, terminateActiveSession: true),
            Throws.TypeOf<InvalidOperationException>().With.Message.EqualTo("dispose failed"));
        Assert.That(settings.Has(sessionId), Is.False,
            "failed disposal must still detach settings owned by the removed generation");
        Assert.That(initiator.AddSession(sessionId, sessionSettings), Is.True,
            "failed disposal must still release the same-ID removal reservation");
        Assert.That(Session.LookupSession(sessionId), Is.Not.Null);
    }

    [Test]
    public void RemovedGenerationCannotActivateOrPreventReplacementActivation()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var oldSetupEntered = new ManualResetEventSlim();
        using var releaseOldSetup = new ManualResetEventSlim();
        var oldStream = new RecordingStream();
        SessionSettings settings = InitiatorSettings(port, useSsl: true);
        var sessionId = new SessionID("FIX.4.2", "INIT_SENDER", "INIT_TARGET");
        SettingsDictionary sessionSettings = settings.Get(sessionId);
        using var initiator = new AbaSocketInitiator(settings, oldSetupEntered, releaseOldSetup, oldStream);
        GatedSetupSocketInitiatorThread? oldThread = null;
        TcpClient? replacementPeer = null;

        try
        {
            initiator.Start();
            Assert.That(oldSetupEntered.Wait(5000), Is.True, "old connection setup should reach its gate");
            oldThread = initiator.OldThread
                ?? throw new InvalidOperationException("Old connection thread was not created");

            Assert.That(initiator.RemoveSession(sessionId, terminateActiveSession: true), Is.True);
            Assert.That(oldThread.WaitForCompletion(0), Is.False,
                "old setup should outlive bounded transport cleanup");
            Assert.That(initiator.AddSession(sessionId, sessionSettings), Is.True);

            Task<TcpClient> acceptReplacement = listener.AcceptTcpClientAsync();
            Assert.That(acceptReplacement.Wait(5000), Is.True,
                "replacement connection should reach its TLS setup gate");
            replacementPeer = acceptReplacement.Result;
            replacementPeer.ReceiveTimeout = 5000;

            releaseOldSetup.Set();

            Assert.That(oldThread.WaitForCompletion(5000), Is.True,
                "old connection worker should complete after setup is released");
            Assert.That(oldStream.WriteCount, Is.Zero,
                "an old session instance must not activate against replacement pending state");

            using X509Certificate2 certificate = CreateServerCertificate();
            using var replacementStream = new SslStream(replacementPeer.GetStream());
            replacementStream.ReadTimeout = 5000;
            replacementStream.AuthenticateAsServer(certificate);
            byte[] logon = new byte[4096];
            string replacementMessage;
            try
            {
                int bytesRead = replacementStream.Read(logon);
                replacementMessage = Encoding.ASCII.GetString(logon, 0, bytesRead);
            }
            catch (IOException)
            {
                replacementMessage = string.Empty;
            }

            Assert.That(replacementMessage, Does.Contain("\u000135=A\u0001"),
                "replacement should publish its Logon after activation");
        }
        finally
        {
            releaseOldSetup.Set();
            oldThread?.Disconnect();
            oldThread?.Join();
            replacementPeer?.Dispose();
            listener.Stop();
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

    [Test]
    public void ShutdownSignalDoesNotWaitForBlockedConnectScheduling()
    {
        using var connectEntered = new ManualResetEventSlim();
        using var releaseConnect = new ManualResetEventSlim();
        using var stopCompleted = new ManualResetEventSlim();
        using var initiator = new BlockedSchedulingSocketInitiator(
            InitiatorSettings(), connectEntered, releaseConnect);
        Thread? stopper = null;

        try
        {
            initiator.Start();
            Assert.That(connectEntered.Wait(5000), Is.True,
                "the worker should enter connection scheduling");

            stopper = new Thread(() =>
            {
                initiator.RequestWorkerStop();
                stopCompleted.Set();
            });
            stopper.Start();

            Assert.That(stopCompleted.Wait(5000), Is.True,
                "shutdown signalling must not wait for synchronous connection setup");
        }
        finally
        {
            releaseConnect.Set();
            stopper?.Join(5000);
        }
    }

    [Test]
    public void MissingRemovalCleanupCannotDeleteAcceptedReplacementSettings()
    {
        using var removeEntered = new ManualResetEventSlim();
        using var releaseRemove = new ManualResetEventSlim();
        using var admissionEntered = new ManualResetEventSlim();
        using var releaseAdmission = new ManualResetEventSlim();
        SessionSettings settings = InitiatorSettings();
        var replacementId = new SessionID("FIX.4.2", "REPLACEMENT_SENDER", "REPLACEMENT_TARGET");
        var initiator = new GatedMissingRemovalSocketInitiator(
            settings, replacementId, removeEntered, releaseRemove);
        SettingsDictionary replacementSettings = InitiatorSettings().Get(
            new SessionID("FIX.4.2", "INIT_SENDER", "INIT_TARGET"));
        var connectionTypeComparer = new GatedConnectionTypeComparer(
            settings, replacementId, admissionEntered, releaseAdmission);
        SetComparer(replacementSettings, connectionTypeComparer);
        Thread? remover = null;
        Thread? adder = null;
        bool added = false;

        try
        {
            remover = new Thread(() => initiator.RemoveSession(replacementId, terminateActiveSession: true));
            remover.Start();
            Assert.That(removeEntered.Wait(5000), Is.True,
                "missing-session removal should reserve the ID before replacement admission");

            adder = new Thread(() =>
            {
                connectionTypeComparer.GateAdmissionOnCurrentThread();
                added = initiator.AddSession(replacementId, replacementSettings);
            });
            adder.Start();
            Assert.That(admissionEntered.Wait(5000), Is.True,
                "replacement settings should be inserted before admission checks the removal reservation");
            Assert.That(settings.Has(replacementId), Is.True);

            releaseRemove.Set();
            Assert.That(remover.Join(5000), Is.True,
                "missing-session cleanup should finish before replacement admission resumes");
            releaseAdmission.Set();
            Assert.That(adder.Join(5000), Is.True);

            Assert.That(added, Is.True);
            Assert.That(settings.Has(replacementId), Is.True,
                "accepted replacement settings must not be deleted by stale removal cleanup");
            Assert.DoesNotThrow(() => initiator.ConnectForTest(replacementId));
            Assert.That(Volatile.Read(ref initiator.ConnectionAttempts), Is.EqualTo(1));
        }
        finally
        {
            releaseRemove.Set();
            releaseAdmission.Set();
            remover?.Join(5000);
            adder?.Join(5000);
            initiator.Dispose();
        }
    }

    [Test]
    public void ConcurrentStopsAreSerializedBeforeRestart()
    {
        using var configureEntered = new ManualResetEventSlim();
        using var releaseConfigure = new ManualResetEventSlim();
        using var firstStopPaused = new ManualResetEventSlim();
        using var releaseFirstStop = new ManualResetEventSlim();
        using var firstStopAttempted = new ManualResetEventSlim();
        using var secondStopAttempted = new ManualResetEventSlim();
        using var initiator = new ConcurrentStopSocketInitiator(
            InitiatorSettings(), configureEntered, releaseConfigure, firstStopPaused, releaseFirstStop);
        Thread? starter = null;
        Thread? firstStopper = null;
        Thread? secondStopper = null;

        try
        {
            starter = new Thread(initiator.Start);
            starter.Start();
            Assert.That(configureEntered.Wait(5000), Is.True,
                "start should hold its lifecycle boundary while configuring");

            firstStopper = new Thread(() =>
            {
                firstStopAttempted.Set();
                initiator.Stop(force: true);
            });
            secondStopper = new Thread(() =>
            {
                secondStopAttempted.Set();
                initiator.Stop(force: true);
            });
            firstStopper.Start();
            secondStopper.Start();
            Assert.That(firstStopAttempted.Wait(5000), Is.True);
            Assert.That(secondStopAttempted.Wait(5000), Is.True);

            releaseConfigure.Set();
            Assert.That(starter.Join(5000), Is.True);
            Assert.That(firstStopPaused.Wait(5000), Is.True,
                "the first stop should pause before completing its lifecycle");

            initiator.Start();
            Assert.That(Volatile.Read(ref initiator.WorkerStarts), Is.EqualTo(1),
                "restart must be refused while any stop lifecycle is still active");

            releaseFirstStop.Set();
            Assert.That(firstStopper.Join(5000), Is.True);
            Assert.That(secondStopper.Join(5000), Is.True);
            Assert.That(Volatile.Read(ref initiator.SecondStopEnteredBeforeFirstCompleted), Is.Zero,
                "a concurrent stop must not enter lifecycle cleanup before the first stop completes");

            initiator.Start();
            Assert.That(SpinWait.SpinUntil(
                    () => Volatile.Read(ref initiator.WorkerStarts) == 2, 5000),
                Is.True, "restart should launch a replacement worker");
            Assert.That(initiator.GetSessionIDs(), Is.Not.Empty,
                "the completed stop must not clear the restarted generation");
        }
        finally
        {
            releaseConfigure.Set();
            releaseFirstStop.Set();
            starter?.Join(5000);
            firstStopper?.Join(5000);
            secondStopper?.Join(5000);
        }
    }
}
