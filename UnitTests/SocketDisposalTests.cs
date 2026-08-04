using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using QuickFix;
using QuickFix.Logger;
using QuickFix.Store;

namespace UnitTests;

[TestFixture]
public class SocketDisposalTests
{
    private TcpListener? _listener;
    private TcpClient? _client;
    private TcpClient? _acceptedClient;

    private static int FreeTcpPort()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [SetUp]
    public void Setup()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _client = new TcpClient();
        var connectTask = _client.ConnectAsync(IPAddress.Loopback, port);
        _acceptedClient = _listener.AcceptTcpClient();
        connectTask.Wait();
    }

    [TearDown]
    public void TearDown()
    {
        _client?.Close();
        _acceptedClient?.Close();
        _listener?.Stop();
    }

    private SessionSchedule CreateSchedule()
    {
        var settings = new SettingsDictionary();
        settings.SetString(SessionSettings.START_TIME, "00:00:00");
        settings.SetString(SessionSettings.END_TIME, "00:00:00");
        return new SessionSchedule(settings);
    }

    [Test]
    public void SocketInitiatorThread_IdempotentDisconnect_DoesNotThrow()
    {
        var session = new Session(
            false, new SessionTestSupport.MockApplication(), new MemoryStoreFactory(),
            new SessionID("FIX.4.2", "SENDER", "TARGET"), new DataDictionaryProvider(),
            CreateSchedule(), 0, new LogFactoryAdapter(new NullLogFactory()),
            new DefaultMessageFactory(), "blah");

        var settings = new SocketSettings();

        var initiatorThread = new SocketInitiatorThread(
            null!, session, new IPEndPoint(IPAddress.Loopback, 1234), settings, new LogFactoryAdapter(new NullLogFactory()));

        // Call Disconnect without even connecting (or after connecting)
        Assert.DoesNotThrow(() => initiatorThread.Disconnect());
        // Call it again to prove idempotency
        Assert.DoesNotThrow(() => initiatorThread.Disconnect());
    }

    [Test]
    public void SocketReader_IdempotentDispose_DoesNotThrow()
    {
        var settings = new SocketSettings();
        var responder = new ClientHandlerThread(_acceptedClient!, 1, settings, null, new LogFactoryAdapter(new NullLogFactory()));
        
        // Retrieve the reader via reflection or we can just dispose the responder (which disposes the reader)
        // Since ClientHandlerThread.Dispose() disposes the reader, let's call it twice.
        Assert.DoesNotThrow(() => responder.Dispose());
        Assert.DoesNotThrow(() => responder.Dispose());
    }

    [Test]
    public void SocketInitiatorThread_WedgedStream_DisconnectDoesNotThrow()
    {
        var session = new Session(
            false, new SessionTestSupport.MockApplication(), new MemoryStoreFactory(),
            new SessionID("FIX.4.2", "SENDER", "TARGET"), new DataDictionaryProvider(),
            CreateSchedule(), 0, new LogFactoryAdapter(new NullLogFactory()),
            new DefaultMessageFactory(), "blah");

        var settings = new SocketSettings();

        // We subclass SocketInitiatorThread to inject our accepted loopback client stream
        var initiatorThread = new TestSocketInitiatorThread(
            null!, session, _acceptedClient!, settings, new LogFactoryAdapter(new NullLogFactory()));

        initiatorThread.Connect();

        // Start a read which will wait forever because we do not send any data from _client
        var readTask = Task.Run(() => initiatorThread.Read());

        // Give it a brief moment to enter ReadAsync and block in the Wait call
        Thread.Sleep(100);

        // Disconnect should cancel the read and dispose resources gracefully without throwing
        Assert.DoesNotThrow(() => initiatorThread.Disconnect());

        // Wait for the read task to unwind
        readTask.Wait(1000);
    }

    [Test]
    public void SocketReader_WedgedStream_DisposeDoesNotThrow()
    {
        var settings = new SocketSettings();
        var responder = new ClientHandlerThread(_acceptedClient!, 1, settings, null, new LogFactoryAdapter(new NullLogFactory()));

        // Start a read which will wait forever because we do not send any data from _client
        var readTask = Task.Run(() => responder.Start());

        // Give it a brief moment to start reading
        Thread.Sleep(100);

        // Disposing the responder shuts down the SocketReader and the thread, and must not throw
        Assert.DoesNotThrow(() => responder.Dispose());

        // Wait for the thread/task to unwind
        readTask.Wait(1000);
    }

    [Test]
    public void HttpServer_DisposeAfterStart_DoesNotAbortItsThread()
    {
        using var server = new HttpServer($"http://localhost:{FreeTcpPort()}/", new SessionSettings());
        server.Start();

        Assert.DoesNotThrow(server.Dispose);
    }

    private class TestSocketInitiatorThread : SocketInitiatorThread
    {
        private readonly TcpClient _tcpClient;

        public TestSocketInitiatorThread(
            QuickFix.Transport.SocketInitiator initiator,
            Session session,
            TcpClient tcpClient,
            SocketSettings settings,
            IQuickFixLoggerFactory loggerFactory)
            : base(initiator, session, (IPEndPoint)tcpClient.Client.RemoteEndPoint!, settings, loggerFactory)
        {
            _tcpClient = tcpClient;
        }

        protected override Stream SetupStream()
        {
            return _tcpClient.GetStream();
        }
    }
}
