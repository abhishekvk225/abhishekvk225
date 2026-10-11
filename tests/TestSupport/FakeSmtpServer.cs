using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace NexaVerify.TestSupport;

/// <summary>One message the fake server accepted: the envelope and the raw DATA (headers and body as sent, CRLF line ends).</summary>
public sealed record SmtpReceived(string MailFrom, IReadOnlyList<string> Recipients, string Data);

/// <summary>
/// A tiny in-process SMTP server for testing the real MailKit client: EHLO, STARTTLS (or implicit TLS), AUTH PLAIN/LOGIN, MAIL/RCPT/DATA with
/// scriptable replies, and a "never answers" mode for timeouts. It speaks just enough of RFC 5321 to be a faithful peer; it stores nothing but
/// what it was sent.
/// </summary>
public sealed class FakeSmtpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _connections = [];
    private readonly object _lock = new();
    private Task? _accept;

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Presented after STARTTLS (or at once with <see cref="ImplicitTls"/>). Null = the server does not offer STARTTLS.</summary>
    public X509Certificate2? Certificate { get; set; }

    public bool ImplicitTls { get; set; }

    /// <summary>When set, AUTH is offered and only this user/password pair is accepted.</summary>
    public (string User, string Password)? Credentials { get; set; }

    /// <summary>Accept the connection but never send the greeting (the client must time out).</summary>
    public bool Silent { get; set; }

    /// <summary>Reply to RCPT TO for an address; null = "250 OK".</summary>
    public Func<string, string?>? RcptReply { get; set; }

    /// <summary>Reply to MAIL FROM; null = "250 OK".</summary>
    public Func<string, string?>? MailReply { get; set; }

    /// <summary>Reply after the message data; null = "250 OK queued".</summary>
    public Func<string?>? DataReply { get; set; }

    public List<SmtpReceived> Messages { get; } = [];

    /// <summary>Every command line the server saw (AUTH credentials excluded), in order.</summary>
    public List<string> Commands { get; } = [];

    public bool TlsEstablished { get; private set; }

    public int Connections { get; private set; }

    public void Start()
    {
        _listener.Start();
        _accept = Task.Run(AcceptAsync);
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                lock (_lock)
                {
                    Connections++;
                    _connections.Add(Task.Run(() => HandleAsync(client)));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using var _ = client;
        try
        {
            Stream stream = client.GetStream();
            if (ImplicitTls)
            {
                stream = await UpgradeAsync(stream);
            }

            if (Silent)
            {
                await Task.Delay(Timeout.Infinite, _stop.Token);
                return;
            }

            var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
            var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("220 fake.smtp ESMTP ready");

            var from = string.Empty;
            var rcpts = new List<string>();
            while (await reader.ReadLineAsync(_stop.Token) is { } line)
            {
                var upper = line.ToUpperInvariant();
                if (!upper.StartsWith("AUTH ", StringComparison.Ordinal) && !IsContinuation(upper))
                {
                    lock (_lock)
                    {
                        Commands.Add(line);
                    }
                }

                if (upper.StartsWith("EHLO", StringComparison.Ordinal) || upper.StartsWith("HELO", StringComparison.Ordinal))
                {
                    await writer.WriteLineAsync("250-fake.smtp greets you");
                    if (Certificate is not null && !TlsEstablished && !ImplicitTls)
                    {
                        await writer.WriteLineAsync("250-STARTTLS");
                    }

                    if (Credentials is not null)
                    {
                        await writer.WriteLineAsync("250-AUTH PLAIN LOGIN");
                    }

                    await writer.WriteLineAsync("250 8BITMIME");
                }
                else if (upper == "STARTTLS")
                {
                    await writer.WriteLineAsync("220 ready to start TLS");
                    stream = await UpgradeAsync(stream);
                    reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
                    writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
                }
                else if (upper.StartsWith("AUTH PLAIN", StringComparison.Ordinal))
                {
                    var payload = line.Length > 11 ? line[11..].Trim() : null;
                    if (string.IsNullOrEmpty(payload))
                    {
                        await writer.WriteLineAsync("334 ");
                        payload = await reader.ReadLineAsync(_stop.Token);
                    }

                    var parts = Encoding.UTF8.GetString(Convert.FromBase64String(payload ?? string.Empty)).Split('\0');
                    await writer.WriteLineAsync(parts.Length == 3 && Credentials is { } c && parts[1] == c.User && parts[2] == c.Password
                        ? "235 2.7.0 authenticated"
                        : "535 5.7.8 authentication credentials invalid");
                }
                else if (upper.StartsWith("AUTH LOGIN", StringComparison.Ordinal))
                {
                    await writer.WriteLineAsync("334 VXNlcm5hbWU6");
                    var user = Encoding.UTF8.GetString(Convert.FromBase64String(await reader.ReadLineAsync(_stop.Token) ?? string.Empty));
                    await writer.WriteLineAsync("334 UGFzc3dvcmQ6");
                    var password = Encoding.UTF8.GetString(Convert.FromBase64String(await reader.ReadLineAsync(_stop.Token) ?? string.Empty));
                    await writer.WriteLineAsync(Credentials is { } c && user == c.User && password == c.Password ? "235 2.7.0 authenticated" : "535 5.7.8 authentication credentials invalid");
                }
                else if (upper.StartsWith("MAIL FROM:", StringComparison.Ordinal))
                {
                    from = Address(line);
                    rcpts.Clear();
                    await writer.WriteLineAsync(MailReply?.Invoke(from) ?? "250 2.1.0 OK");
                }
                else if (upper.StartsWith("RCPT TO:", StringComparison.Ordinal))
                {
                    var address = Address(line);
                    var reply = RcptReply?.Invoke(address) ?? "250 2.1.5 OK";
                    if (reply.StartsWith('2'))
                    {
                        rcpts.Add(address);
                    }

                    await writer.WriteLineAsync(reply);
                }
                else if (upper == "DATA")
                {
                    await writer.WriteLineAsync("354 end data with <CR><LF>.<CR><LF>");
                    var data = new StringBuilder();
                    while (await reader.ReadLineAsync(_stop.Token) is { } dataLine && dataLine != ".")
                    {
                        data.Append(dataLine.StartsWith("..", StringComparison.Ordinal) ? dataLine[1..] : dataLine).Append("\r\n");
                    }

                    var reply = DataReply?.Invoke() ?? "250 2.0.0 OK queued";
                    if (reply.StartsWith('2'))
                    {
                        lock (_lock)
                        {
                            Messages.Add(new SmtpReceived(from, [.. rcpts], data.ToString()));
                        }
                    }

                    await writer.WriteLineAsync(reply);
                }
                else if (upper == "RSET" || upper == "NOOP")
                {
                    await writer.WriteLineAsync("250 OK");
                }
                else if (upper == "QUIT")
                {
                    await writer.WriteLineAsync("221 bye");
                    return;
                }
                else if (!IsContinuation(upper))
                {
                    await writer.WriteLineAsync("502 5.5.2 command not recognised");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or AuthenticationException or ObjectDisposedException or SocketException or FormatException)
        {
            // a client that disconnects (e.g. because it rejected our certificate) is normal in these tests
        }
    }

    private static bool IsContinuation(string upper) => upper.Length > 0 && !upper.Contains(' ', StringComparison.Ordinal) && upper.All(c => char.IsAsciiLetterOrDigit(c) || c is '=' or '+' or '/');

    private async Task<Stream> UpgradeAsync(Stream inner)
    {
        var ssl = new SslStream(inner, leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = Certificate ?? throw new InvalidOperationException("No certificate configured."),
            ClientCertificateRequired = false,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        }, _stop.Token);
        TlsEstablished = true;
        return ssl;
    }

    private static string Address(string line)
    {
        var open = line.IndexOf('<', StringComparison.Ordinal);
        var close = line.IndexOf('>', StringComparison.Ordinal);
        return open >= 0 && close > open ? line[(open + 1)..close] : line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim();
    }

    private bool _disposed;

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _stop.CancelAsync();
        _listener.Stop();
        Task[] pending;
        lock (_lock)
        {
            pending = [.. _connections];
        }

        try
        {
            await Task.WhenAll(pending.Append(_accept ?? Task.CompletedTask)).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
        }

        _stop.Dispose();
    }

    /// <summary>A throw-away certificate authority and a server certificate it signed (DNS localhost + 127.0.0.1) for testing TLS validation.</summary>
    public sealed class TestPki : IDisposable
    {
        private TestPki(X509Certificate2 ca, X509Certificate2 server)
        {
            Ca = ca;
            Server = server;
        }

        public X509Certificate2 Ca { get; }

        public X509Certificate2 Server { get; }

        public static TestPki Create(string serverName = "localhost", DateTimeOffset? notAfter = null)
        {
            using var caKey = RSA.Create(2048);
            var caRequest = new CertificateRequest("CN=NexaVerify Test CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            using var caCert = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

            using var serverKey = RSA.Create(2048);
            var request = new CertificateRequest($"CN={serverName}", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName(serverName);
            if (serverName == "localhost")
            {
                san.AddIpAddress(IPAddress.Loopback);
            }

            request.CertificateExtensions.Add(san.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
            var serial = RandomNumberGenerator.GetBytes(8);
            using var signed = request.Create(caCert, DateTimeOffset.UtcNow.AddDays(-1), notAfter ?? DateTimeOffset.UtcNow.AddDays(10), serial);
            var server = X509CertificateLoader.LoadPkcs12(signed.CopyWithPrivateKey(serverKey).Export(X509ContentType.Pfx), null);
            return new TestPki(X509CertificateLoader.LoadCertificate(caCert.Export(X509ContentType.Cert)), server);
        }

        public string WriteCaPem(string directory)
        {
            var path = Path.Combine(directory, "ca-" + Guid.NewGuid().ToString("N") + ".pem");
            File.WriteAllText(path, Ca.ExportCertificatePem());
            return path;
        }

        public void Dispose()
        {
            Ca.Dispose();
            Server.Dispose();
        }
    }
}
