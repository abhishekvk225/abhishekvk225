using NexaVerify.Domain.Common;
using NexaVerify.Domain.Messaging;

namespace NexaVerify.Domain.UnitTests;

public class OutboundEmailTests
{
    private static readonly DateTime Now = new(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);

    private static OutboundEmail Mail(bool oneTimeToken = false, string to = "Ada@Example.COM") =>
        OutboundEmail.Queue(to, " Ada ", "Subject", "text", "<p>html</p>", EmailTemplate.PasswordReset, "corr", "id@nexaverify.example", oneTimeToken, Now);

    /// <summary>Simulates a dispatcher claim (the SQL does this in production).</summary>
    private static void Claim(OutboundEmail mail) =>
        typeof(OutboundEmail).GetProperty(nameof(OutboundEmail.Status))!.SetValue(mail, EmailStatus.Sending);

    private static void SetAttempts(OutboundEmail mail, int attempts) =>
        typeof(OutboundEmail).GetProperty(nameof(OutboundEmail.Attempts))!.SetValue(mail, attempts);

    [Fact]
    public void A_new_message_is_pending_and_due_immediately_with_a_time_ordered_id()
    {
        var mail = Mail();

        mail.Status.ShouldBe(EmailStatus.Pending);
        mail.NextAttemptAt.ShouldBe(Now);
        mail.CreatedAt.ShouldBe(Now);
        mail.Attempts.ShouldBe(0);
        mail.ToDomain.ShouldBe("example.com");
        mail.ToName.ShouldBe("Ada");
        mail.Id.Version.ShouldBe(7);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-at-sign")]
    [InlineData("@example.com")]
    [InlineData("ada@")]
    [InlineData("ada@example.com\r\nBcc: x@y.z")]
    public void An_unusable_recipient_is_refused(string to) =>
        Should.Throw<DomainException>(() => Mail(to: to)).Code.ShouldBe("EMAIL_RECIPIENT_INVALID");

    [Fact]
    public void Sending_a_message_with_a_one_time_token_erases_its_bodies_at_once()
    {
        var mail = Mail(oneTimeToken: true);
        Claim(mail);

        mail.MarkSent(Now.AddSeconds(1));

        mail.Status.ShouldBe(EmailStatus.Sent);
        mail.SentAt.ShouldBe(Now.AddSeconds(1));
        mail.TextBody.ShouldBeNull();
        mail.HtmlBody.ShouldBeNull();
        mail.BodiesPurgedAt.ShouldBe(Now.AddSeconds(1));
        mail.HasBodies.ShouldBeFalse();
    }

    [Fact]
    public void Other_messages_keep_their_bodies_after_sending_until_the_retention_purge()
    {
        var mail = Mail(oneTimeToken: false);
        Claim(mail);

        mail.MarkSent(Now);

        mail.TextBody.ShouldBe("text");
        mail.HtmlBody.ShouldBe("<p>html</p>");
        mail.BodiesPurgedAt.ShouldBeNull();
    }

    [Fact]
    public void A_transient_failure_schedules_the_retry_after_the_delay()
    {
        var mail = Mail();
        Claim(mail);
        SetAttempts(mail, 1);

        mail.RecordFailure(permanent: false, "timeout", Now, maxAttempts: 3, retryDelay: TimeSpan.FromMinutes(5));

        mail.Status.ShouldBe(EmailStatus.Pending);
        mail.NextAttemptAt.ShouldBe(Now.AddMinutes(5));
        mail.LastError.ShouldBe("timeout");
        mail.LeaseToken.ShouldBeNull();
    }

    [Fact]
    public void A_transient_failure_on_the_last_attempt_abandons_the_message()
    {
        var mail = Mail();
        Claim(mail);
        SetAttempts(mail, 3);

        mail.RecordFailure(permanent: false, "timeout", Now, maxAttempts: 3, retryDelay: TimeSpan.FromMinutes(5));

        mail.Status.ShouldBe(EmailStatus.Abandoned);
    }

    [Fact]
    public void A_permanent_failure_ends_the_message_at_once_even_on_the_first_attempt()
    {
        var mail = Mail();
        Claim(mail);
        SetAttempts(mail, 1);

        mail.RecordFailure(permanent: true, "550 no such user", Now, maxAttempts: 6, retryDelay: TimeSpan.FromMinutes(5));

        mail.Status.ShouldBe(EmailStatus.Failed);
    }

    [Fact]
    public void The_stored_error_is_bounded()
    {
        var mail = Mail();
        Claim(mail);

        mail.RecordFailure(permanent: true, new string('e', 1000), Now, 6, TimeSpan.Zero);

        mail.LastError!.Length.ShouldBe(OutboundEmail.ErrorMaxLength);
    }

    [Fact]
    public void Releasing_a_claim_does_not_count_the_attempt()
    {
        var mail = Mail();
        Claim(mail);
        SetAttempts(mail, 2);

        mail.Release(Now.AddSeconds(5));

        mail.Status.ShouldBe(EmailStatus.Pending);
        mail.Attempts.ShouldBe(1);
        mail.NextAttemptAt.ShouldBe(Now.AddSeconds(5));
    }

    [Theory]
    [InlineData(EmailStatus.Failed)]
    [InlineData(EmailStatus.Abandoned)]
    public void An_administrator_can_retry_a_failed_or_abandoned_message_with_a_fresh_set_of_attempts(EmailStatus from)
    {
        var mail = Mail();
        Claim(mail);
        SetAttempts(mail, 6);
        mail.RecordFailure(permanent: from == EmailStatus.Failed, "x", Now, 6, TimeSpan.Zero);
        mail.Status.ShouldBe(from);

        mail.Retry(Now.AddHours(1));

        mail.Status.ShouldBe(EmailStatus.Pending);
        mail.Attempts.ShouldBe(0);
        mail.NextAttemptAt.ShouldBe(Now.AddHours(1));
        mail.LastError.ShouldBeNull();
    }

    [Theory]
    [InlineData(EmailStatus.Pending)]
    [InlineData(EmailStatus.Sending)]
    [InlineData(EmailStatus.Sent)]
    public void Only_failed_or_abandoned_messages_can_be_retried(EmailStatus status)
    {
        var mail = Mail();
        if (status == EmailStatus.Sent)
        {
            Claim(mail);
            mail.MarkSent(Now);
        }
        else if (status == EmailStatus.Sending)
        {
            Claim(mail);
        }

        Should.Throw<DomainException>(() => mail.Retry(Now)).Code.ShouldBe("EMAIL_NOT_RETRYABLE");
    }

    [Fact]
    public void A_message_whose_content_was_erased_cannot_be_retried()
    {
        var mail = Mail(oneTimeToken: true);
        mail.Fail("x");
        mail.PurgeBodies(Now);

        Should.Throw<DomainException>(() => mail.Retry(Now)).Code.ShouldBe("EMAIL_BODY_PURGED");
    }

    [Fact]
    public void A_suppression_normalises_the_address_and_bounds_the_reason()
    {
        var suppression = EmailSuppression.Create("  Ada@Example.COM ", new string('r', 1000), Guid.NewGuid(), Now);

        suppression.Address.ShouldBe("ada@example.com");
        suppression.Reason.Length.ShouldBe(EmailSuppression.ReasonMaxLength);
        suppression.Id.Version.ShouldBe(7);
    }
}
