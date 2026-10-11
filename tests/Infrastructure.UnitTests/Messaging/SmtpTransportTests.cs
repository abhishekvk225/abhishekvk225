using System.Diagnostics;
using Microsoft.Extensions.Options;
using MimeKit;
using NexaVerify.Domain.Messaging;
using NexaVerify.Infrastructure.Messaging;
using NexaVerify.TestSupport;

namespace NexaVerify.Infrastructure.UnitTests.Messaging;

/// <summary>The real MailKit client against an in-process SMTP server: TLS validation, authentication, reply classification and timeouts.</summary>
public sealed class SmtpTransportTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("nv-smtp-").FullName;
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var d in _disposables)
        {
            d.Dispose();
        }

        Directory.Delete(_directory, recursive: true);
    }

    private static OutgoingEmail Message(string to = "ada@example.com", string? toName = "Ada Lovelace", string subject = "Hello there", string? text = null) =>
        new(Guid.NewGuid(), to, toName, subject, text ?? "Plain body\r\nhttps://portal.example.com/x?token=abc", "<html><body><p>Html body</p></body></html>",
            "msg-" + Guid.NewGuid().ToString("N") + "@nexaverify.example", "corr-42", DateTime.UtcNow, EmailTemplate.PasswordReset);

    private async Task<FakeSmtpServer> StartAsync(Action<FakeSmtpServer>? configure = null)
    {
        var server = new FakeSmtpServer();
        configure?.Invoke(server);
        server.Start();
        _disposables.Add(new AsyncDisposeAdapter(server));
        await Task.CompletedTask;
        return server;
    }

    private static SmtpEmailTransport Transport(FakeSmtpServer server, Action<EmailOptions>? tweak = null)
    {
        var options = new EmailOptions
        {
            Provider = "Smtp",
            From = { Name = "NexaVerify", Address = "no-reply@example.com" },
            ReplyTo = "support@example.com",
            Smtp = { Host = "localhost", Port = server.Port, Security = SmtpSecurity.None, TimeoutSeconds = 5 },
        };
        tweak?.Invoke(options);
        return new SmtpEmailTransport(Options.Create(options));
    }

    private TestPkiHandle Pki(string name = "localhost")
    {
        var pki = FakeSmtpServer.TestPki.Create(name);
        _disposables.Add(pki);
        return new TestPkiHandle(pki, pki.WriteCaPem(_directory));
    }

    private sealed record TestPkiHandle(FakeSmtpServer.TestPki Pki, string CaFile);

    private sealed class AsyncDisposeAdapter(IAsyncDisposable inner) : IDisposable
    {
        public void Dispose() => inner.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task A_message_is_delivered_as_multipart_alternative_with_identifying_headers()
    {
        var server = await StartAsync();
        var message = Message();

        await Transport(server).SendAsync(message, default);

        var received = server.Messages.Single();
        received.MailFrom.ShouldBe("no-reply@example.com");
        received.Recipients.ShouldBe(["ada@example.com"]);
        var mime = MimeMessage.Load(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(received.Data)));
        mime.Subject.ShouldBe("Hello there");
        mime.From.Mailboxes.Single().Address.ShouldBe("no-reply@example.com");
        mime.From.Mailboxes.Single().Name.ShouldBe("NexaVerify");
        mime.To.Mailboxes.Single().Address.ShouldBe("ada@example.com");
        mime.ReplyTo.Mailboxes.Single().Address.ShouldBe("support@example.com");
        mime.MessageId.ShouldBe(message.MessageId);
        mime.Headers["X-NexaVerify-Correlation"].ShouldBe("corr-42");
        mime.Headers["Auto-Submitted"].ShouldBe("auto-generated");
        mime.Body!.ContentType.MimeType.ShouldBe("multipart/alternative");
        mime.TextBody!.ShouldContain("https://portal.example.com/x?token=abc");
        mime.HtmlBody!.ShouldContain("<p>Html body</p>");
    }

    [Fact]
    public async Task A_hostile_name_or_subject_cannot_add_headers_or_recipients()
    {
        var server = await StartAsync();

        await Transport(server).SendAsync(Message(toName: "Eve\r\nBcc: victim@example.com", subject: "Hi\r\nBcc: victim@example.com\r\nX-Evil: 1"), default);

        var received = server.Messages.Single();
        received.Recipients.ShouldBe(["ada@example.com"]);
        var headerBlock = received.Data[..received.Data.IndexOf("\r\n\r\n", StringComparison.Ordinal)];
        headerBlock.Split("\r\n").Count(l => l.StartsWith("Bcc:", StringComparison.OrdinalIgnoreCase)).ShouldBe(0);
        headerBlock.Split("\r\n").Count(l => l.StartsWith("X-Evil:", StringComparison.OrdinalIgnoreCase)).ShouldBe(0);
        headerBlock.Split("\r\n").Count(l => l.StartsWith("Subject:", StringComparison.OrdinalIgnoreCase)).ShouldBe(1);
    }

    [Fact]
    public async Task Starttls_with_a_certificate_signed_by_the_configured_private_ca_is_accepted()
    {
        var pki = Pki();
        var server = await StartAsync(s => s.Certificate = pki.Pki.Server);

        await Transport(server, o => { o.Smtp.Security = SmtpSecurity.StartTls; o.Smtp.CaCertificateFile = pki.CaFile; }).SendAsync(Message(), default);

        server.TlsEstablished.ShouldBeTrue();
        server.Messages.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Implicit_tls_with_a_trusted_private_ca_is_accepted()
    {
        var pki = Pki();
        var server = await StartAsync(s => { s.Certificate = pki.Pki.Server; s.ImplicitTls = true; });

        await Transport(server, o => { o.Smtp.Security = SmtpSecurity.SslOnConnect; o.Smtp.CaCertificateFile = pki.CaFile; }).SendAsync(Message(), default);

        server.TlsEstablished.ShouldBeTrue();
        server.Messages.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_certificate_from_an_untrusted_authority_is_rejected_and_nothing_is_sent()
    {
        var pki = Pki();
        var server = await StartAsync(s => s.Certificate = pki.Pki.Server);

        var ex = await Should.ThrowAsync<EmailSendException>(() => Transport(server, o => o.Smtp.Security = SmtpSecurity.StartTls).SendAsync(Message(), default));

        ex.Kind.ShouldBe(EmailFailureKind.Transient);
        ex.Message.ShouldContain("certificate");
        server.Messages.ShouldBeEmpty();
        server.Commands.ShouldNotContain(c => c.StartsWith("MAIL", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_certificate_signed_by_a_different_authority_than_the_configured_one_is_rejected()
    {
        var served = Pki();
        var trusted = Pki();
        var server = await StartAsync(s => s.Certificate = served.Pki.Server);

        var ex = await Should.ThrowAsync<EmailSendException>(
            () => Transport(server, o => { o.Smtp.Security = SmtpSecurity.StartTls; o.Smtp.CaCertificateFile = trusted.CaFile; }).SendAsync(Message(), default));

        ex.Kind.ShouldBe(EmailFailureKind.Transient);
        server.Messages.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_certificate_for_another_host_name_is_rejected_even_when_its_authority_is_trusted()
    {
        var pki = Pki("mail.other-host.example");
        var server = await StartAsync(s => s.Certificate = pki.Pki.Server);

        var ex = await Should.ThrowAsync<EmailSendException>(
            () => Transport(server, o => { o.Smtp.Security = SmtpSecurity.StartTls; o.Smtp.CaCertificateFile = pki.CaFile; }).SendAsync(Message(), default));

        ex.Kind.ShouldBe(EmailFailureKind.Transient);
        server.Messages.ShouldBeEmpty();
        server.Commands.ShouldNotContain(c => c.StartsWith("MAIL", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task When_starttls_is_required_but_not_offered_the_message_is_not_sent_in_clear_text()
    {
        var server = await StartAsync(); // no certificate: STARTTLS is not advertised

        await Should.ThrowAsync<EmailSendException>(() => Transport(server, o => o.Smtp.Security = SmtpSecurity.StartTls).SendAsync(Message(), default));

        server.Messages.ShouldBeEmpty();
        server.Commands.ShouldNotContain(c => c.StartsWith("MAIL", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Correct_credentials_authenticate_and_the_message_goes_through()
    {
        var pki = Pki();
        var server = await StartAsync(s => { s.Certificate = pki.Pki.Server; s.Credentials = ("mailer", "Sup3rSecretPassw0rd"); });

        await Transport(server, o =>
        {
            o.Smtp.Security = SmtpSecurity.StartTls;
            o.Smtp.CaCertificateFile = pki.CaFile;
            o.Smtp.Username = "mailer";
            o.Smtp.Password = "Sup3rSecretPassw0rd";
        }).SendAsync(Message(), default);

        server.Messages.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Wrong_credentials_fail_as_transient_without_echoing_them_and_nothing_is_sent()
    {
        var pki = Pki();
        var server = await StartAsync(s => { s.Certificate = pki.Pki.Server; s.Credentials = ("mailer", "Sup3rSecretPassw0rd"); });

        var ex = await Should.ThrowAsync<EmailSendException>(() => Transport(server, o =>
        {
            o.Smtp.Security = SmtpSecurity.StartTls;
            o.Smtp.CaCertificateFile = pki.CaFile;
            o.Smtp.Username = "mailer";
            o.Smtp.Password = "Wr0ngPassword!";
        }).SendAsync(Message(), default));

        ex.Kind.ShouldBe(EmailFailureKind.Transient);
        ex.Message.ShouldContain("authentication");
        ex.Message.ShouldNotContain("Wr0ngPassword!");
        ex.Message.ShouldNotContain("Sup3rSecretPassw0rd");
        server.Messages.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_5xx_refusal_of_the_recipient_is_a_permanent_recipient_failure()
    {
        var server = await StartAsync(s => s.RcptReply = _ => "550 5.1.1 <ada@example.com>: Recipient address rejected: User unknown");

        var ex = await Should.ThrowAsync<EmailSendException>(() => Transport(server).SendAsync(Message(), default));

        ex.Kind.ShouldBe(EmailFailureKind.PermanentRecipient);
        ex.IsPermanent.ShouldBeTrue();
        ex.Message.ShouldNotContain("ada@example.com"); // no address in the stored text
        server.Messages.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_4xx_reply_to_the_recipient_is_transient()
    {
        var server = await StartAsync(s => s.RcptReply = _ => "450 4.2.0 Mailbox temporarily unavailable, try again later");

        var ex = await Should.ThrowAsync<EmailSendException>(() => Transport(server).SendAsync(Message(), default));

        ex.Kind.ShouldBe(EmailFailureKind.Transient);
        ex.IsPermanent.ShouldBeFalse();
    }

    [Fact]
    public async Task A_5xx_after_the_data_is_permanent_and_a_4xx_is_transient()
    {
        var rejecting = await StartAsync(s => s.DataReply = () => "554 5.7.1 Message rejected as spam");
        var deferring = await StartAsync(s => s.DataReply = () => "451 4.3.0 Local error in processing");

        (await Should.ThrowAsync<EmailSendException>(() => Transport(rejecting).SendAsync(Message(), default))).Kind.ShouldBe(EmailFailureKind.Permanent);
        (await Should.ThrowAsync<EmailSendException>(() => Transport(deferring).SendAsync(Message(), default))).Kind.ShouldBe(EmailFailureKind.Transient);
    }

    [Fact]
    public async Task A_rejected_sender_is_permanent_but_not_a_recipient_problem()
    {
        var server = await StartAsync(s => s.MailReply = _ => "554 5.7.1 Sender address not verified");

        var ex = await Should.ThrowAsync<EmailSendException>(() => Transport(server).SendAsync(Message(), default));

        ex.Kind.ShouldBe(EmailFailureKind.Permanent);
    }

    [Fact]
    public async Task A_server_that_never_greets_times_out_within_the_configured_time_as_a_transient_failure()
    {
        var server = await StartAsync(s => s.Silent = true);
        var clock = Stopwatch.StartNew();

        var ex = await Should.ThrowAsync<EmailSendException>(() => Transport(server, o => o.Smtp.TimeoutSeconds = 1).SendAsync(Message(), default));

        ex.Kind.ShouldBe(EmailFailureKind.Transient);
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(8));
    }

    [Fact]
    public async Task A_closed_port_is_a_transient_failure()
    {
        var server = await StartAsync();
        var transport = Transport(server);
        await server.DisposeAsync();

        var ex = await Should.ThrowAsync<EmailSendException>(() => transport.SendAsync(Message(), default));

        ex.Kind.ShouldBe(EmailFailureKind.Transient);
    }

    [Fact]
    public async Task Shutdown_cancels_the_send_without_counting_it_as_a_server_failure()
    {
        var server = await StartAsync(s => s.Silent = true);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Should.ThrowAsync<OperationCanceledException>(() => Transport(server, o => o.Smtp.TimeoutSeconds = 30).SendAsync(Message(), cts.Token));
    }
}
