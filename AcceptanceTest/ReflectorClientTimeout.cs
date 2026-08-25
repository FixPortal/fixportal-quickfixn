using NUnit.Framework;
using System;
using System.Net;
using System.Net.Sockets;

namespace AcceptanceTest;

/// <summary>
/// Covers the read-timeout path in <see cref="ReflectorClient"/>. Before it existed, an
/// engine that went quiet blocked <see cref="ReflectorClient.Expect"/> forever, so the run
/// died as an opaque test-host abort naming no expectation. These pin the replacement:
/// a bounded wait that fails the one test and quotes what it was waiting for.
/// </summary>
[TestFixture]
public class ReflectorClientTimeout
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(200);

    /// <summary>A listener that accepts a connection and then never sends anything.</summary>
    private static Socket StartSilentListener(out IPEndPoint endPoint)
    {
        Socket listener = new(SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        endPoint = (IPEndPoint)listener.LocalEndPoint!;
        return listener;
    }

    [Test]
    public void Expect_RemoteSendsNothing_FailsNamingTheExpectedMessage()
    {
        using Socket listener = StartSilentListener(out IPEndPoint endPoint);
        using ReflectorClient client = new(endPoint, ShortTimeout);

        client.InitiateConnect();
        using Socket accepted = listener.Accept();

        AssertionException ex = Assert.Throws<AssertionException>(
            () => client.Expect("8=FIX.4.2\u000135=1\u0001112=TEST\u0001"))!;

        Assert.That(ex.Message, Does.Contain("Timed out"));
        Assert.That(ex.Message, Does.Contain("8=FIX.4.2|35=1|112=TEST|"));
    }

    [Test]
    public void ExpectDisconnect_RemoteStaysConnected_FailsNamingTheDisconnect()
    {
        using Socket listener = StartSilentListener(out IPEndPoint endPoint);
        using ReflectorClient client = new(endPoint, ShortTimeout);

        client.InitiateConnect();
        using Socket accepted = listener.Accept();

        AssertionException ex = Assert.Throws<AssertionException>(() => client.ExpectDisconnect())!;

        Assert.That(ex.Message, Does.Contain("the remote host to disconnect"));
    }

    /// <summary>
    /// The ceiling has to clear the longest legitimate idle in the suite — a disconnect at
    /// ~2.4x the largest HeartBtInt in use (30s). A default trimmed below that would abort
    /// healthy definitions, which is exactly how the blame-hang flag misfired.
    /// </summary>
    [Test]
    public void DefaultReceiveTimeout_ClearsTheLongestLegitimateIdle()
    {
        Assert.That(
            ReflectorClient.DefaultReceiveTimeout,
            Is.GreaterThan(TimeSpan.FromSeconds(2.4 * 30)));
    }
}
