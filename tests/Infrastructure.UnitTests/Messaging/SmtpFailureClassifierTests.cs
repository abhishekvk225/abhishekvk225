using System.Net.Sockets;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using NexaVerify.Infrastructure.Messaging;

namespace NexaVerify.Infrastructure.UnitTests.Messaging;

public class SmtpFailureClassifierTests
{
    private static readonly EmailOptions Options = new() { Smtp = { Username = "mailer-user", Password = "Sup3rSecretPassw0rd" } };

    private static EmailSendException Classify(Exception ex) => SmtpFailureClassifier.Classify(ex, Options);

    private static SmtpCommandException Command(SmtpErrorCode code, int status, string text) =>
        new(code, (SmtpStatusCode)status, new MailboxAddress(null, "who@example.com"), text);

    [Theory]
    [InlineData(421, "4.4.2 Service not available")]
    [InlineData(450, "4.2.0 Mailbox busy")]
    [InlineData(451, "4.3.0 Local error")]
    [InlineData(452, "4.2.2 Insufficient storage")]
    public void A_4xx_reply_is_transient_whatever_command_it_answers(int status, string text)
    {
        foreach (var code in new[] { SmtpErrorCode.RecipientNotAccepted, SmtpErrorCode.MessageNotAccepted, SmtpErrorCode.SenderNotAccepted, SmtpErrorCode.UnexpectedStatusCode })
        {
            Classify(Command(code, status, text)).Kind.ShouldBe(EmailFailureKind.Transient);
        }
    }

    [Theory]
    [InlineData(550, "5.1.1 The email account that you tried to reach does not exist")]
    [InlineData(551, "User not local")]
    [InlineData(553, "5.1.3 Mailbox name not allowed")]
    [InlineData(554, "5.1.2 Domain does not exist")]
    public void A_5xx_that_refuses_the_recipient_is_permanent_and_suppresses_the_address(int status, string text) =>
        Classify(Command(SmtpErrorCode.RecipientNotAccepted, status, text)).Kind.ShouldBe(EmailFailureKind.PermanentRecipient);

    [Fact]
    public void A_policy_refusal_of_a_recipient_is_permanent_but_does_not_suppress_the_address() =>
        Classify(Command(SmtpErrorCode.RecipientNotAccepted, 550, "5.7.1 Relay access denied")).Kind.ShouldBe(EmailFailureKind.Permanent);

    [Fact]
    public void A_mailbox_full_answer_dressed_as_5xx_is_treated_as_transient() =>
        Classify(Command(SmtpErrorCode.RecipientNotAccepted, 552, "5.2.2 Mailbox full")).Kind.ShouldBe(EmailFailureKind.Transient);

    [Theory]
    [InlineData(SmtpErrorCode.SenderNotAccepted, 554, "Sender address rejected: not verified")]
    [InlineData(SmtpErrorCode.MessageNotAccepted, 554, "Message rejected as spam")]
    [InlineData(SmtpErrorCode.UnexpectedStatusCode, 503, "Bad sequence of commands")]
    public void Other_5xx_replies_are_permanent_without_suppression(SmtpErrorCode code, int status, string text) =>
        Classify(Command(code, status, text)).Kind.ShouldBe(EmailFailureKind.Permanent);

    [Fact]
    public void Authentication_and_tls_problems_are_transient_so_fixing_the_configuration_lets_mail_flow()
    {
        Classify(new AuthenticationException("535 5.7.8 bad credentials")).Kind.ShouldBe(EmailFailureKind.Transient);
        Classify(new SslHandshakeException("The remote certificate is invalid", new IOException("tls"))).Kind.ShouldBe(EmailFailureKind.Transient);
    }

    [Fact]
    public void Network_failures_and_timeouts_are_transient()
    {
        Classify(new IOException("connection reset")).Kind.ShouldBe(EmailFailureKind.Transient);
        Classify(new SocketException()).Kind.ShouldBe(EmailFailureKind.Transient);
        Classify(new TimeoutException()).Kind.ShouldBe(EmailFailureKind.Transient);
        Classify(new OperationCanceledException()).Kind.ShouldBe(EmailFailureKind.Transient);
        Classify(new SmtpProtocolException("bad greeting")).Kind.ShouldBe(EmailFailureKind.Transient);
        Classify(new ServiceNotConnectedException()).Kind.ShouldBe(EmailFailureKind.Transient);
        Classify(new InvalidOperationException("boom")).Kind.ShouldBe(EmailFailureKind.Transient);
    }

    [Fact]
    public void A_message_that_cannot_be_built_is_permanent() =>
        Classify(new FormatException("bad header")).Kind.ShouldBe(EmailFailureKind.Permanent);

    [Fact]
    public void The_stored_text_is_one_line_bounded_and_free_of_credentials()
    {
        var exception = Command(SmtpErrorCode.UnexpectedStatusCode, 535, "5.7.8 auth failed for mailer-user using Sup3rSecretPassw0rd\r\nsecond line " + new string('x', 1000));

        var classified = Classify(exception);

        classified.Message.ShouldNotContain("Sup3rSecretPassw0rd");
        classified.Message.ShouldNotContain("mailer-user");
        classified.Message.ShouldNotContain("\n");
        classified.Message.Length.ShouldBeLessThanOrEqualTo(300);
        classified.InnerException.ShouldBe(exception);
    }

    [Fact]
    public void Authentication_failures_name_the_settings_but_never_echo_them() =>
        Classify(new AuthenticationException("bad Sup3rSecretPassw0rd")).Message.ShouldNotContain("Sup3rSecretPassw0rd");
}
