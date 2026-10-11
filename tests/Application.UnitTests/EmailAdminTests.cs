using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Auditing;
using NexaVerify.Application.Common;
using NexaVerify.Application.Identity;
using NexaVerify.Application.Messaging;
using NexaVerify.Application.Persistence;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Identity;
using NexaVerify.Contracts.Messaging;
using NexaVerify.Domain.Identity;
using NexaVerify.Domain.Messaging;

namespace NexaVerify.Application.UnitTests;

public class EmailAdminTests
{
    private static readonly DateTime Now = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(Now);
    }

    private sealed class FakeRepository : IEmailOutboxRepository
    {
        public List<OutboundEmail> Mails { get; } = [];

        public List<EmailSuppression> Suppressions { get; } = [];

        public EmailOutboxFilter? LastFilter { get; private set; }

        public Task<(IReadOnlyList<EmailOutboxRow> Items, int Total)> ListAsync(EmailOutboxFilter filter, int skip, int take, CancellationToken cancellationToken)
        {
            LastFilter = filter;
            IReadOnlyList<EmailOutboxRow> rows = [.. Mails.Skip(skip).Take(take).Select(EmailOutboxRow.From)];
            return Task.FromResult((rows, Mails.Count));
        }

        public Task<OutboundEmail?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Mails.FirstOrDefault(m => m.Id == id));

        public Task<(IReadOnlyList<EmailSuppression> Items, int Total)> ListSuppressionsAsync(int skip, int take, CancellationToken cancellationToken) =>
            Task.FromResult(((IReadOnlyList<EmailSuppression>)[.. Suppressions.Skip(skip).Take(take)], Suppressions.Count));

        public Task<EmailSuppression?> GetSuppressionAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Suppressions.FirstOrDefault(s => s.Id == id));

        public void Remove(EmailSuppression suppression) => Suppressions.Remove(suppression);
    }

    private sealed class FakeSender : IEmailSender
    {
        public List<EmailRequest> Requests { get; } = [];

        public bool Accept { get; set; } = true;

        public Task<Guid?> SendAsync(EmailRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult<Guid?>(Accept ? Guid.CreateVersion7() : null);
        }
    }

    private sealed class FakePermissions : IPermissionResolver
    {
        public HashSet<string> Granted { get; } = [];

        public Task<IReadOnlySet<string>> GetPermissionsAsync(IEnumerable<string> roleNames, CancellationToken cancellationToken) => Task.FromResult<IReadOnlySet<string>>(Granted);

        public void Invalidate()
        {
        }
    }

    private sealed class FakeUser : ICurrentUser
    {
        public bool IsAuthenticated => true;

        public ActorType ActorType => ActorType.User;

        public Guid? ActorId { get; } = Guid.NewGuid();

        public Guid? ClientId => null;

        public bool IsPlatformUser => true;

        public IReadOnlyCollection<string> Roles => ["SuperAdmin"];
    }

    private sealed class FakeAudit : IAuditService
    {
        public List<AuditEntry> Entries { get; } = [];

        public void Record(AuditEntry entry) => Entries.Add(entry);
    }

    private sealed class FakeUnitOfWork : IUnitOfWork
    {
        public int Saves { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            Saves++;
            return Task.FromResult(1);
        }

        public Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken = default) => action(cancellationToken);

        public void ClearTracked()
        {
        }
    }

    private sealed class Rig
    {
        public FakeRepository Repository { get; } = new();

        public FakeSender Sender { get; } = new();

        public FakePermissions Permissions { get; } = new();

        public FakeAudit Audit { get; } = new();

        public FakeUnitOfWork UnitOfWork { get; } = new();

        public EmailAdminService Service => new(Repository, Sender, Permissions, new FakeUser(), Audit, UnitOfWork, new FixedTime(), NullLogger<EmailAdminService>.Instance);
    }

    private static OutboundEmail Mail(string to = "jane.doe@example.com", bool failed = false)
    {
        var mail = OutboundEmail.Queue(to, null, "Subject", "text", "<p>html</p>", EmailTemplate.PasswordReset, "corr", "id@x", false, Now);
        if (failed)
        {
            mail.Fail("550 refused");
        }

        return mail;
    }

    [Theory]
    [InlineData("jane.doe@example.com", "j***@example.com")]
    [InlineData("a@b.co", "a***@b.co")]
    [InlineData("weird", "***")]
    [InlineData("@x.com", "***")]
    public void Addresses_are_masked_to_their_first_letter_and_domain(string address, string expected) =>
        EmailAddressMasker.Mask(address).ShouldBe(expected);

    [Fact]
    public async Task Callers_without_email_read_see_masked_recipients()
    {
        var rig = new Rig();
        rig.Repository.Mails.Add(Mail());

        var page = (await rig.Service.ListOutboxAsync(new EmailOutboxQuery(), default)).Value;

        page.Items.Single().To.ShouldBe("j***@example.com");
        page.Items.Single().Subject.ShouldBe("Subject");
    }

    [Fact]
    public async Task Callers_with_email_read_see_the_full_recipient()
    {
        var rig = new Rig();
        rig.Permissions.Granted.Add(Permissions.Email.Read);
        rig.Repository.Mails.Add(Mail());

        (await rig.Service.ListOutboxAsync(new EmailOutboxQuery(), default)).Value.Items.Single().To.ShouldBe("jane.doe@example.com");
    }

    [Fact]
    public async Task Filters_are_parsed_and_the_recipient_filter_is_lower_cased()
    {
        var rig = new Rig();

        var result = await rig.Service.ListOutboxAsync(new EmailOutboxQuery { Status = "failed", Template = "passwordreset", Recipient = " Jane@Example.com ", PageSize = 5000 }, default);

        result.IsSuccess.ShouldBeTrue();
        rig.Repository.LastFilter!.Status.ShouldBe(EmailStatus.Failed);
        rig.Repository.LastFilter.Template.ShouldBe(EmailTemplate.PasswordReset);
        rig.Repository.LastFilter.Recipient.ShouldBe("jane@example.com");
        result.Value.PageSize.ShouldBe(PageRequest.MaxPageSize);
    }

    [Theory]
    [InlineData("Bogus", null)]
    [InlineData("42", null)]
    [InlineData(null, "NotATemplate")]
    public async Task Unknown_filter_values_are_a_validation_error(string? status, string? template)
    {
        var result = await new Rig().Service.ListOutboxAsync(new EmailOutboxQuery { Status = status, Template = template }, default);

        result.Error!.Type.ShouldBe(ErrorType.Validation);
        result.Error.FieldErrors!.Keys.Single().ShouldBe(status is not null ? "status" : "template");
    }

    [Fact]
    public async Task An_inverted_date_range_is_a_validation_error()
    {
        var result = await new Rig().Service.ListOutboxAsync(new EmailOutboxQuery { From = Now, To = Now.AddDays(-1) }, default);

        result.Error!.Type.ShouldBe(ErrorType.Validation);
    }

    [Fact]
    public async Task Retrying_a_failed_message_requeues_it_audits_without_the_full_address_and_saves()
    {
        var rig = new Rig();
        var mail = Mail(failed: true);
        rig.Repository.Mails.Add(mail);

        var result = await rig.Service.RetryAsync(mail.Id, default);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Status.ShouldBe("Pending");
        rig.UnitOfWork.Saves.ShouldBe(1);
        var audit = rig.Audit.Entries.Single();
        audit.Action.ShouldBe(EmailAuditActions.Retried);
        System.Text.Json.JsonSerializer.Serialize(audit.NewValues).ShouldNotContain("jane.doe@example.com");
    }

    [Fact]
    public async Task Retrying_a_message_in_another_state_or_a_missing_one_fails_cleanly()
    {
        var rig = new Rig();
        var pending = Mail();
        rig.Repository.Mails.Add(pending);

        (await rig.Service.RetryAsync(pending.Id, default)).Error!.Type.ShouldBe(ErrorType.Conflict);
        (await rig.Service.RetryAsync(Guid.NewGuid(), default)).Error!.Type.ShouldBe(ErrorType.NotFound);
        rig.UnitOfWork.Saves.ShouldBe(0);
    }

    [Fact]
    public async Task Removing_a_suppression_deletes_it_and_audits_it()
    {
        var rig = new Rig();
        var suppression = EmailSuppression.Create("jane.doe@example.com", "550", null, Now);
        rig.Repository.Suppressions.Add(suppression);

        (await rig.Service.RemoveSuppressionAsync(suppression.Id, default)).IsSuccess.ShouldBeTrue();

        rig.Repository.Suppressions.ShouldBeEmpty();
        rig.Audit.Entries.Single().Action.ShouldBe(EmailAuditActions.SuppressionRemoved);
        rig.UnitOfWork.Saves.ShouldBe(1);
        (await rig.Service.RemoveSuppressionAsync(suppression.Id, default)).Error!.Type.ShouldBe(ErrorType.NotFound);
    }

    [Fact]
    public async Task A_test_email_is_queued_audited_and_saved()
    {
        var rig = new Rig();

        var result = await rig.Service.SendTestAsync(new SendTestEmailRequest(" me@example.com "), default);

        result.IsSuccess.ShouldBeTrue();
        var request = rig.Sender.Requests.Single();
        request.Template.ShouldBe(EmailTemplate.AdminTest);
        request.To.ShouldBe("me@example.com");
        rig.Audit.Entries.Single().Action.ShouldBe(EmailAuditActions.TestSent);
        System.Text.Json.JsonSerializer.Serialize(rig.Audit.Entries.Single().NewValues).ShouldNotContain("me@example.com");
        rig.UnitOfWork.Saves.ShouldBe(1);
    }

    [Fact]
    public async Task A_test_email_to_an_unusable_address_is_a_validation_error_and_saves_nothing()
    {
        var rig = new Rig();
        rig.Sender.Accept = false;

        var result = await rig.Service.SendTestAsync(new SendTestEmailRequest("x"), default);

        result.Error!.Type.ShouldBe(ErrorType.Validation);
        rig.UnitOfWork.Saves.ShouldBe(0);
    }

    [Theory]
    [InlineData("me@example.com", true)]
    [InlineData("", false)]
    [InlineData("not-an-address", false)]
    [InlineData("me@example.com\r\nBcc: x@y.z", false)]
    [InlineData("me @example.com", false)]
    public void The_test_request_validator_accepts_only_plain_addresses(string to, bool valid) =>
        new SendTestEmailRequestValidator().Validate(new SendTestEmailRequest(to)).IsValid.ShouldBe(valid);

    [Fact]
    public void The_new_permissions_are_in_the_catalogue_and_super_admin_holds_them_but_client_roles_do_not()
    {
        Permissions.All.Select(p => p.Key).ShouldContain("email.read");
        Permissions.All.Select(p => p.Key).ShouldContain("email.manage");
        SystemRoles.PermissionsFor(SystemRoles.SuperAdmin).ShouldContain("email.read");
        SystemRoles.PermissionsFor(SystemRoles.SuperAdmin).ShouldContain("email.manage");
        SystemRoles.PermissionsFor(SystemRoles.ClientAdmin).ShouldNotContain("email.read");
        SystemRoles.PermissionsFor(SystemRoles.ClientAdmin).ShouldNotContain("email.manage");
        SystemRoles.PermissionsFor(SystemRoles.ClientUser).ShouldNotContain("email.manage");
    }

    // ---- the reset link keeps its shape ----

    [Fact]
    public void The_reset_template_is_split_into_a_path_and_query_with_the_placeholders_filled_in()
    {
        var (path, query) = PasswordResetService.LinkShape("https://portal.example.com/reset-password?email={email}&token={token}", "a+b@example.com", "tok_en-1");

        path.ShouldBe("reset-password");
        query.ShouldBe([("email", "a+b@example.com"), ("token", "tok_en-1")]);
    }

    [Fact]
    public void Placeholders_in_the_path_are_filled_too()
    {
        var (path, query) = PasswordResetService.LinkShape("https://portal.example.com/app/reset/{token}?e={email}&x=1", "a@b.test", "tok");

        path.ShouldBe("app/reset/tok");
        query.ShouldBe([("e", "a@b.test"), ("x", "1")]);
    }

    [Fact]
    public async Task The_reset_and_invitation_mails_use_the_right_template_and_carry_the_token_only_in_the_link()
    {
        var sender = new FakeSender();
        var service = new PasswordResetService(
            null!, null!, sender, Options.Create(new AuthOptions { PasswordResetMinutes = 30, InvitationHours = 72, PasswordResetUrlTemplate = "https://portal.example.com/reset-password?email={email}&token={token}" }),
            new FixedTime());
        var user = User.Create("ada@example.com", "Ada Lovelace", "hash", Guid.NewGuid(), isPlatformUser: false, mustChangePassword: false);

        await service.SendAsync(user, "RAWTOKEN", ResetEmailKind.Reset, default);
        await service.SendAsync(user, "RAWTOKEN", ResetEmailKind.Invitation, default);

        sender.Requests[0].Template.ShouldBe(EmailTemplate.PasswordReset);
        sender.Requests[1].Template.ShouldBe(EmailTemplate.UserInvite);
        foreach (var request in sender.Requests)
        {
            request.To.ShouldBe("ada@example.com");
            request.Links["action"].Path.ShouldBe("reset-password");
            request.Links["action"].Query.Select(q => q.Value).ShouldBe(["ada@example.com", "RAWTOKEN"]);
            request.Values.Values.Select(v => v.Value).ShouldNotContain("RAWTOKEN");
        }
    }
}
