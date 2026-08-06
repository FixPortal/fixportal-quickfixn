using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
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
            _releaseSetup.Wait();
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

    private sealed class NoConnectionSocketInitiator : SocketInitiator
    {
        public NoConnectionSocketInitiator(SessionSettings settings, IMessageStoreFactory storeFactory)
            : base(new SessionTestSupport.MockApplication(), storeFactory, settings,
                (ILogFactory?)new NullLogFactory())
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

        public void RequestWorkerStop() => base.OnStop();
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

            Assert.That(initiator.AddSession(sessionId, sessionSettings), Is.False,
                "same-ID re-add must wait for old session disposal");

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
        TcpClient? replacementPeer = null;

        try
        {
            initiator.Start();
            Assert.That(oldSetupEntered.Wait(5000), Is.True, "old connection setup should reach its gate");

            Assert.That(initiator.RemoveSession(sessionId, terminateActiveSession: true), Is.True);
            Assert.That(initiator.OldThread!.WaitForCompletion(0), Is.False,
                "old setup should outlive bounded transport cleanup");
            Assert.That(initiator.AddSession(sessionId, sessionSettings), Is.True);

            Task<TcpClient> acceptReplacement = listener.AcceptTcpClientAsync();
            Assert.That(acceptReplacement.Wait(5000), Is.True,
                "replacement connection should reach its TLS setup gate");
            replacementPeer = acceptReplacement.Result;
            replacementPeer.ReceiveTimeout = 5000;

            releaseOldSetup.Set();

            Assert.That(initiator.OldThread.WaitForCompletion(5000), Is.True,
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
            initiator.OldThread?.Disconnect();
            initiator.OldThread?.Join();
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
}
