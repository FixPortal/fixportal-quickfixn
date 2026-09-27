using NUnit.Framework;
using QuickFix;
using System;
using System.IO;
using System.Linq;
using System.Net;
using QuickFix.Logger;
using QuickFix.Store;

namespace AcceptanceTest;

public abstract class TestBase
{
    private int _port;
    private ThreadedSocketAcceptor _acceptor;

    protected abstract SessionSettings Settings { get; }

    [OneTimeSetUp]
    public void Setup()
    {
        SessionSettings settings = Settings;

        _port = settings.Get().GetInt(SessionSettings.SOCKET_ACCEPT_PORT);
        FailIfPortIsTaken(_port);
        var testApp = new ATApplication();
        var storeFactory = new MemoryStoreFactory();

        ILogFactory? logFactory = settings.Get().Has("Verbose") && settings.Get().GetBool("Verbose")
            ? new FileLogFactory(settings)
            : new NullLogFactory();

        _acceptor = new ThreadedSocketAcceptor(testApp, storeFactory, settings, logFactory);

        _acceptor.Start();
    }

    /// <summary>
    /// FP Enhancement: 2026-09-27 — refuse to start when anything already listens on the
    /// acceptance port. The acceptor binds 0.0.0.0 and that bind succeeds even when another
    /// process (typically a Docker port publish) holds 127.0.0.1 on the same port; the runner
    /// dials 127.0.0.1, so Windows routes every test to the other listener and the whole
    /// fixture fails as "Socket was shut down by remote host" after long timeouts. Observed:
    /// a container publishing 127.0.0.1:5002 failed all 56 Fix41 tests; on a free port they
    /// passed 56/56.
    /// </summary>
    private static void FailIfPortIsTaken(int port)
    {
        IPEndPoint[] taken = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpListeners()
            .Where(ep => ep.Port == port)
            .ToArray();
        if (taken.Length > 0)
            throw new InvalidOperationException(
                $"Acceptance port {port} is already in use ({string.Join(", ", taken.Select(ep => ep.ToString()))}). " +
                "Stop whatever listens there (often a Docker container publishing the port) before running AcceptanceTest.");
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        _acceptor?.Dispose();
    }

    protected void RunTest(string definitionPath)
    {
        System.Threading.Thread.Sleep(100);
        using Runner runner = new(new IPEndPoint(IPAddress.Loopback, _port));
        using StreamReader sr = new(definitionPath);
        runner.Run(sr);
    }
}
