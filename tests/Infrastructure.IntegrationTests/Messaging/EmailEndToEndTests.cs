using Microsoft.Extensions.Options;
using MimeKit;
using NexaVerify.Application.Messaging;
using NexaVerify.Domain.Messaging;
using NexaVerify.Infrastructure.Messaging;
using NexaVerify.TestSupport;

namespace NexaVerify.Infrastructure.IntegrationTests.Messaging;

/// <summary>The whole chain: staged in the database, claimed by the dispatcher, rendered content delivered by the real MailKit client to an SMTP server over TLS.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class EmailEndToEndTests : IDisposable
{
    private readonly SqlServerFixture _fixture;
    private readonly string _directory = Directory.CreateTempSubdirectory("nv-e2e-").FullName;

    public EmailEndToEndTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static SmtpEmailTransport Transport(FakeSmtpServer server, string caFile) => new(Options.Create(new EmailOptions
    {
        Provider = "Smtp",
        From = { Name = "NexaVerify", Address = "no-reply@example.com" },
        Smtp = { Host = "localhost", Port = server.Port, Security = SmtpSecurity.StartTls, CaCertificateFile = caFile, Username = "mailer", Password = "S3cret-Passw0rd", TimeoutSeconds = 5 },
    }));

    [Fact]
    public async Task A_verification_mail_travels_from_the_outbox_to_the_smtp_server_and_its_token_is_erased_afterwards()
    {
        using var pki = FakeSmtpServer.TestPki.Create();
        await using var server = new FakeSmtpServer { Certificate = pki.Server, Credentials = ("mailer", "S3cret-Passw0rd") };
        server.Start();
        await using var rig = await EmailRig.CreateAsync(_fixture, transport: Transport(server, pki.WriteCaPem(_directory)));
        var request = new EmailRequest(EmailTemplate.VerifyEmail, "ada@example.com", "Ada Lovelace")
            .Text("name", "Ada Lovelace").Text("company", "Acme <Ltd>").Number("trialCredits", 40).Number("trialDays", 10).Number("hours", 24)
            .Link("verify", "verify-email", ("email", "ada@example.com"), ("token", "tok-123_ABC"));
        var id = (await rig.EnqueueAsync(request))[0];

        (await rig.Dispatcher.DispatchDueAsync(default)).ShouldBe(1);

        var received = server.Messages.Single();
        received.Recipients.ShouldBe(["ada@example.com"]);
        server.TlsEstablished.ShouldBeTrue();
        var mime = MimeMessage.Load(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(received.Data)));
        mime.Subject.ShouldBe("Confirm your NexaVerify account");
        mime.TextBody!.ShouldContain("https://portal.example.com/verify-email?email=ada%40example.com&token=tok-123_ABC");
        mime.HtmlBody!.ShouldContain("Acme &lt;Ltd&gt;");
        mime.Headers["X-NexaVerify-Correlation"].ShouldNotBeNullOrWhiteSpace();
        var stored = await rig.GetAsync(id);
        mime.MessageId.ShouldBe(stored.MessageId);
        stored.Status.ShouldBe(EmailStatus.Sent);
        stored.TextBody.ShouldBeNull(); // the one-time link is gone from the table
        stored.HtmlBody.ShouldBeNull();
        rig.Logs.Lines.ShouldNotContain(l => l.Message.Contains("tok-123_ABC", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_server_that_refuses_the_mailbox_ends_the_message_and_suppresses_the_address()
    {
        using var pki = FakeSmtpServer.TestPki.Create();
        await using var server = new FakeSmtpServer
        {
            Certificate = pki.Server,
            Credentials = ("mailer", "S3cret-Passw0rd"),
            RcptReply = _ => "550 5.1.1 <nobody@example.com>: Recipient address rejected: User unknown",
        };
        server.Start();
        await using var rig = await EmailRig.CreateAsync(_fixture, transport: Transport(server, pki.WriteCaPem(_directory)));
        var id = (await rig.EnqueueAsync(EmailRig.Changed("nobody@example.com")))[0];

        await rig.Dispatcher.DispatchDueAsync(default);

        var mail = await rig.GetAsync(id);
        mail.Status.ShouldBe(EmailStatus.Failed);
        mail.LastError!.ShouldNotContain("nobody@example.com");
        (await rig.ScalarAsync<string>("SELECT Address FROM tenancy.EmailSuppressions")).ShouldBe("nobody@example.com");

        var second = (await rig.EnqueueAsync(EmailRig.Changed("nobody@example.com")))[0];
        var connectionsBefore = server.Connections;
        await rig.Dispatcher.DispatchDueAsync(default);
        (await rig.GetAsync(second)).Status.ShouldBe(EmailStatus.Failed);
        server.Connections.ShouldBe(connectionsBefore); // not even a connection was made
    }

    [Fact]
    public async Task A_certificate_the_client_does_not_trust_keeps_the_mail_queued_for_retry_instead_of_sending_it_in_the_clear()
    {
        using var pki = FakeSmtpServer.TestPki.Create();
        using var other = FakeSmtpServer.TestPki.Create();
        await using var server = new FakeSmtpServer { Certificate = pki.Server, Credentials = ("mailer", "S3cret-Passw0rd") };
        server.Start();
        await using var rig = await EmailRig.CreateAsync(_fixture, transport: Transport(server, other.WriteCaPem(_directory)));
        var id = (await rig.EnqueueAsync(EmailRig.Changed("ada@example.com")))[0];

        await rig.Dispatcher.DispatchDueAsync(default);

        var mail = await rig.GetAsync(id);
        mail.Status.ShouldBe(EmailStatus.Pending);
        mail.LastError!.ShouldContain("certificate");
        mail.TextBody.ShouldNotBeNull();
        server.Messages.ShouldBeEmpty();
    }
}
