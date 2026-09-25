using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using NUnit.Framework;
using QuickFix;
using QuickFix.Logger;
using QuickFix.Transport;

namespace UnitTests;

/// <summary>
/// SslStreamFactory.CreateClientStreamAndAuthenticate/CreateServerStreamAndAuthenticate
/// (QuickFIXn/Transport/SslStreamFactory.cs:34-112) wire real SslStream client/server callbacks and
/// authenticate. The existing SslStreamFactoryTest only calls VerifyRemoteCertificate directly; it
/// never proves the callbacks are actually invoked during a handshake, nor exercises a hostname
/// mismatch. This drives real client/server SslStream handshakes over a loopback TCP socket pair.
/// </summary>
[TestFixture]
public class SslStreamFactoryHandshakeTest
{
    private const string HostName = "127.0.0.1";
    private string _dir = "unset";

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(TestContext.CurrentContext.TestDirectory, "ssl-handshake-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, true);
    }

    private static X509Certificate2 CreateCaCertificate()
    {
        using RSA rsa = RSA.Create();
        var request = new CertificateRequest("CN=Test CA", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(2));
    }

    private static X509Certificate2 CreateLeafCertificate(
        X509Certificate2 issuer, string commonName, string enhancedKeyUsageOid, bool includeLoopbackSan)
    {
        using RSA rsa = RSA.Create();
        var request = new CertificateRequest($"CN={commonName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid(enhancedKeyUsageOid) }, false));

        if (includeLoopbackSan)
        {
            var sanBuilder = new SubjectAlternativeNameBuilder();
            sanBuilder.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(sanBuilder.Build());
        }

        byte[] serial = Guid.NewGuid().ToByteArray();
        using X509Certificate2 signed = request.Create(issuer, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1), serial);
        return signed.CopyWithPrivateKey(rsa);
    }

    private string SavePfx(X509Certificate2 cert, string fileName, string password)
    {
        string path = Path.Combine(_dir, fileName);
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx, password));
        return path;
    }

    private string SaveCer(X509Certificate2 cert, string fileName)
    {
        string path = Path.Combine(_dir, fileName);
        File.WriteAllBytes(path, cert.Export(X509ContentType.Cert));
        return path;
    }

    private static SocketSettings BuildSettings(
        string certPfxPath, string certPassword, string caCerPath, string serverCommonName, bool requireClientCert)
    {
        return new SocketSettings
        {
            CertificatePath = certPfxPath,
            CertificatePassword = certPassword,
            CACertificatePath = caCerPath,
            ServerCommonName = serverCommonName,
            ValidateCertificates = true,
            CheckCertificateRevocation = false,
            RequireClientCertificate = requireClientCert,
        };
    }

    /// <summary>
    /// Runs a client/server handshake concurrently over a real loopback TCP connection. The
    /// underlying sockets are kept open for the whole handshake (success or failure) and only
    /// disposed once both sides have finished, so a slow/failing side never gets its stream
    /// yanked out from under it by an eager `using`.
    /// </summary>
    private static async Task<(Task<Stream> client, Task<Stream> server)> RunHandshakeAsync(
        SocketSettings clientSettings, SocketSettings serverSettings)
    {
        using TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync();
        using TcpClient client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using TcpClient server = await acceptTask;

        Task<Stream> clientAuth = Task.Run(() => new SslStreamFactory(
            clientSettings, NullQuickFixLoggerFactory.Instance).CreateClientStreamAndAuthenticate(client.GetStream()));
        Task<Stream> serverAuth = Task.Run(() => new SslStreamFactory(
            serverSettings, NullQuickFixLoggerFactory.Instance).CreateServerStreamAndAuthenticate(server.GetStream()));

        await Task.WhenAll(clientAuth, serverAuth).ContinueWith(_ => { });

        return (clientAuth, serverAuth);
    }

    [Test]
    public async Task ValidCertificatesAuthenticateSuccessfully()
    {
        X509Certificate2 ca = CreateCaCertificate();
        string caCerPath = SaveCer(ca, "ca.cer");

        using X509Certificate2 serverCert = CreateLeafCertificate(
            ca, HostName, SslStreamFactory.SERVER_AUTHENTICATION_OID, includeLoopbackSan: true);
        string serverPfxPath = SavePfx(serverCert, "server.pfx", "pw1");

        using X509Certificate2 clientCert = CreateLeafCertificate(
            ca, "client", SslStreamFactory.CLIENT_AUTHENTICATION_OID, includeLoopbackSan: false);
        string clientPfxPath = SavePfx(clientCert, "client.pfx", "pw2");

        SocketSettings clientSettings = BuildSettings(clientPfxPath, "pw2", caCerPath, HostName, requireClientCert: true);
        SocketSettings serverSettings = BuildSettings(serverPfxPath, "pw1", caCerPath, HostName, requireClientCert: true);

        var (clientTask, serverTask) = await RunHandshakeAsync(clientSettings, serverSettings);
        await using Stream clientStream = await clientTask;
        await using Stream serverStream = await serverTask;

        Assert.That(clientStream, Is.InstanceOf<SslStream>());
        Assert.That(serverStream, Is.InstanceOf<SslStream>());
        Assert.That(((SslStream)clientStream).IsAuthenticated, Is.True);
        Assert.That(((SslStream)serverStream).IsAuthenticated, Is.True);
    }

    [Test]
    public async Task UntrustedServerCertificateFailsHandshake()
    {
        X509Certificate2 ca = CreateCaCertificate();
        string caCerPath = SaveCer(ca, "ca.cer");

        // A DIFFERENT, unrelated CA signs the server certificate: the client's trust anchor never
        // matches, so chain validation must fail.
        X509Certificate2 untrustedCa = CreateCaCertificate();
        using X509Certificate2 serverCert = CreateLeafCertificate(
            untrustedCa, HostName, SslStreamFactory.SERVER_AUTHENTICATION_OID, includeLoopbackSan: true);
        string serverPfxPath = SavePfx(serverCert, "server.pfx", "pw1");

        using X509Certificate2 clientCert = CreateLeafCertificate(
            ca, "client", SslStreamFactory.CLIENT_AUTHENTICATION_OID, includeLoopbackSan: false);
        string clientPfxPath = SavePfx(clientCert, "client.pfx", "pw2");

        SocketSettings clientSettings = BuildSettings(clientPfxPath, "pw2", caCerPath, HostName, requireClientCert: false);
        SocketSettings serverSettings = BuildSettings(serverPfxPath, "pw1", caCerPath, HostName, requireClientCert: false);

        var (clientTask, serverTask) = await RunHandshakeAsync(clientSettings, serverSettings);

        Assert.ThrowsAsync<AuthenticationException>(async () => await clientTask);
        Assert.CatchAsync(async () => await serverTask);
    }

    [Test]
    public async Task HostnameMismatchFailsHandshake()
    {
        X509Certificate2 ca = CreateCaCertificate();
        string caCerPath = SaveCer(ca, "ca.cer");

        // The server certificate's SAN only covers the loopback address, not this bogus name, so
        // the client's hostname check (against SSL_SERVERNAME) must reject it.
        using X509Certificate2 serverCert = CreateLeafCertificate(
            ca, HostName, SslStreamFactory.SERVER_AUTHENTICATION_OID, includeLoopbackSan: true);
        string serverPfxPath = SavePfx(serverCert, "server.pfx", "pw1");

        using X509Certificate2 clientCert = CreateLeafCertificate(
            ca, "client", SslStreamFactory.CLIENT_AUTHENTICATION_OID, includeLoopbackSan: false);
        string clientPfxPath = SavePfx(clientCert, "client.pfx", "pw2");

        SocketSettings clientSettings = BuildSettings(
            clientPfxPath, "pw2", caCerPath, "totally-not-127.0.0.1.example.invalid", requireClientCert: false);
        SocketSettings serverSettings = BuildSettings(serverPfxPath, "pw1", caCerPath, HostName, requireClientCert: false);

        var (clientTask, serverTask) = await RunHandshakeAsync(clientSettings, serverSettings);

        Assert.ThrowsAsync<AuthenticationException>(async () => await clientTask);
        Assert.CatchAsync(async () => await serverTask);
    }
}
