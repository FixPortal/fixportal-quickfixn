using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using QuickFix;
using QuickFix.Logger;
using QuickFix.Transport;

namespace UnitTests;

/// <summary>
/// StreamFactory.CreateClientStream tunnels through a proxy by issuing an HTTP CONNECT and
/// substring-searching the reply for "200" (QuickFIXn/Transport/StreamFactory.cs:51-60). A local
/// TCP listener stands in for the proxy so the response text is fully controlled: a real success
/// status, a real failure status, and a failure status whose BODY merely contains "200".
/// </summary>
[TestFixture]
public class StreamFactoryProxyConnectTest
{
    private IWebProxy? _originalProxy;

    [SetUp]
    public void SetUp() => _originalProxy = WebRequest.DefaultWebProxy;

    [TearDown]
    public void TearDown() => WebRequest.DefaultWebProxy = _originalProxy;

    private static (TcpListener listener, int port) StartLocalProxy()
    {
        TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return (listener, ((IPEndPoint)listener.LocalEndpoint).Port);
    }

    private static async Task RespondOnceAsync(TcpListener listener, string response)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync();
        using NetworkStream stream = client.GetStream();

        byte[] requestBuffer = new byte[1024];
        int read = await stream.ReadAsync(requestBuffer.AsMemory(0, requestBuffer.Length));
        string request = Encoding.ASCII.GetString(requestBuffer, 0, read);
        Assert.That(request, Does.StartWith("CONNECT "));

        byte[] responseBytes = Encoding.ASCII.GetBytes(response);
        await stream.WriteAsync(responseBytes);
    }

    private static SocketSettings ProxiedSettings() =>
        new SocketSettings { SocketIgnoreProxy = false, ServerCommonName = "dest.example.com" };

    [Test]
    public async Task SuccessfulConnectStatusEstablishesTunnel()
    {
        var (listener, port) = StartLocalProxy();
        WebRequest.DefaultWebProxy = new WebProxy($"http://127.0.0.1:{port}");
        try
        {
            Task proxyTask = RespondOnceAsync(listener, "HTTP/1.1 200 Connection Established\r\n\r\n");

            using var stream = StreamFactory.CreateClientStream(
                new IPEndPoint(IPAddress.Loopback, 65000),
                ProxiedSettings(),
                NullQuickFixLoggerFactory.Instance,
                CancellationToken.None);

            Assert.That(stream, Is.Not.Null);
            await proxyTask;
        }
        finally
        {
            listener.Stop();
        }
    }

    [Test]
    public async Task Non200SuccessStatusEstablishesTunnel()
    {
        // RFC 9110: any 2xx response to CONNECT is a successful tunnel establishment, not
        // only 200. Some proxies legitimately answer 201/204.
        var (listener, port) = StartLocalProxy();
        WebRequest.DefaultWebProxy = new WebProxy($"http://127.0.0.1:{port}");
        try
        {
            Task proxyTask = RespondOnceAsync(listener, "HTTP/1.1 204 No Content\r\n\r\n");

            using var stream = StreamFactory.CreateClientStream(
                new IPEndPoint(IPAddress.Loopback, 65000),
                ProxiedSettings(),
                NullQuickFixLoggerFactory.Instance,
                CancellationToken.None);

            Assert.That(stream, Is.Not.Null);
            await proxyTask;
        }
        finally
        {
            listener.Stop();
        }
    }

    [Test]
    public async Task NonSuccessStatusIsRejected()
    {
        var (listener, port) = StartLocalProxy();
        WebRequest.DefaultWebProxy = new WebProxy($"http://127.0.0.1:{port}");
        try
        {
            Task proxyTask = RespondOnceAsync(listener, "HTTP/1.1 407 Proxy Authentication Required\r\n\r\n");

            Assert.Throws<ApplicationException>(() => StreamFactory.CreateClientStream(
                new IPEndPoint(IPAddress.Loopback, 65000),
                ProxiedSettings(),
                NullQuickFixLoggerFactory.Instance,
                CancellationToken.None));
            await proxyTask;
        }
        finally
        {
            listener.Stop();
        }
    }

    [Test]
    public async Task NonSuccessStatusWithMisleading200InBodyIsRejected()
    {
        // The response body must not fool the "200" substring search: the status line fails but
        // the body text (falsely) mentions "200".
        var (listener, port) = StartLocalProxy();
        WebRequest.DefaultWebProxy = new WebProxy($"http://127.0.0.1:{port}");
        try
        {
            Task proxyTask = RespondOnceAsync(
                listener,
                "HTTP/1.1 502 Bad Gateway\r\n\r\nUpstream returned code 200 earlier today\r\n");

            Assert.Throws<ApplicationException>(() => StreamFactory.CreateClientStream(
                new IPEndPoint(IPAddress.Loopback, 65000),
                ProxiedSettings(),
                NullQuickFixLoggerFactory.Instance,
                CancellationToken.None));
            await proxyTask;
        }
        finally
        {
            listener.Stop();
        }
    }
}
