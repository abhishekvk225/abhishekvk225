using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexaVerify.Api.IntegrationTests.Support;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Identity;
using NexaVerify.Contracts.Messaging;
using NexaVerify.Domain.Messaging;
using NexaVerify.TestSupport;

namespace NexaVerify.Api.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public class EmailAdminTests : IAsyncLifetime
{
    private const string Outbox = "/api/v1/admin/email/outbox";
    private const string Suppressions = "/api/v1/admin/email/suppressions";
    private const string Test = "/api/v1/admin/email/test";
    private const string StaffPassword = "Granite-Lantern-Voyage-77";

    private readonly SqlServerFixture _fixture;
    private AuthApp _app = null!;
    private LoginResponse _admin = null!;

    public EmailAdminTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _app = await AuthApp.CreateAsync(_fixture, new Dictionary<string, string> { ["RateLimiting:EmailTestPerMinute"] = "3", ["Portal:PublicBaseUrl"] = "https://portal.test" });
        _admin = await _app.SuperAdminAsync();
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private async Task<LoginResponse> StaffAsync(string email, params string[] permissions)
    {
        var role = "Mail " + Guid.NewGuid().ToString("N")[..8];
        (await _app.PostAsync("/api/v1/admin/roles", new CreateRoleRequest(role, "Platform", null, permissions), _admin.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Created);
        const string temp = "Temporary-Passphrase-55";
        (await _app.PostAsync("/api/v1/admin/users", new CreatePlatformUserRequest(email, "Staff", temp, [role]), _admin.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Created);
        var first = await _app.LoginAsync(email, temp);
        var changed = await _app.PostAsync("/api/v1/auth/change-password", new ChangePasswordRequest(temp, StaffPassword), first.AccessToken);
        return (await changed.Content.ReadFromJsonAsync<LoginResponse>(AuthApp.Json))!;
    }

    private async Task<Guid> SeedAsync(string to, EmailStatus status = EmailStatus.Sent, EmailTemplate template = EmailTemplate.PasswordChanged, DateTime? createdAt = null)
    {
        var now = createdAt ?? DateTime.UtcNow;
        var mail = OutboundEmail.Queue(to, null, "Seeded " + template, "text body", "<p>html body</p>", template, "corr-" + Guid.NewGuid().ToString("N")[..8], "id" + Guid.NewGuid().ToString("N") + "@x", false, now);
        switch (status)
        {
            case EmailStatus.Sent:
                typeof(OutboundEmail).GetProperty(nameof(OutboundEmail.Status))!.SetValue(mail, EmailStatus.Sending);
                mail.MarkSent(now);
                break;
            case EmailStatus.Failed:
                mail.Fail("SMTP 550 refused");
                break;
            case EmailStatus.Abandoned:
                typeof(OutboundEmail).GetProperty(nameof(OutboundEmail.Attempts))!.SetValue(mail, 6);
                mail.RecordFailure(permanent: false, "timeouts", now, 6, TimeSpan.Zero);
                break;
        }

        await _app.WithDbAsync(async db =>
        {
            db.OutboundEmails.Add(mail);
            await db.SaveChangesAsync();
            return true;
        });
        return mail.Id;
    }

    private async Task<PagedResult<EmailOutboxItemDto>> ListAsync(string query, string token)
    {
        var response = await _app.GetAsync(Outbox + query, token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<PagedResult<EmailOutboxItemDto>>(AuthApp.Json))!;
    }

    // ---------------------------------------------------------------- authentication and authorisation

    public static TheoryData<string, string> Endpoints => new()
    {
        { "GET", Outbox },
        { "POST", Outbox + "/00000000-0000-0000-0000-000000000001/retry" },
        { "GET", Suppressions },
        { "DELETE", Suppressions + "/00000000-0000-0000-0000-000000000001" },
        { "POST", Test },
    };

    private static HttpRequestMessage Request(string method, string path) =>
        new(new HttpMethod(method), path) { Content = method == "POST" ? JsonContent.Create(new SendTestEmailRequest("someone@example.com")) : null };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Every_email_admin_endpoint_requires_a_signed_in_user(string method, string path) =>
        (await _app.SendAsync(Request(method, path), token: null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Client_users_cannot_use_any_email_admin_endpoint(string method, string path)
    {
        var (_, clientAdmin) = await _app.OnboardClientAsync(_admin.AccessToken, "MAILX", "owner@mailx.test");

        (await _app.SendAsync(Request(method, path), clientAdmin.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Platform_staff_without_the_permissions_are_refused()
    {
        var nobody = await StaffAsync("nobody@nexaverify.test", Permissions.Licenses.Read);

        foreach (var (method, path) in new[] { ("GET", Outbox), ("POST", Outbox + "/" + Guid.NewGuid() + "/retry"), ("GET", Suppressions), ("DELETE", Suppressions + "/" + Guid.NewGuid()), ("POST", Test) })
        {
            (await _app.SendAsync(Request(method, path), nobody.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, path);
        }
    }

    [Fact]
    public async Task Each_permission_unlocks_exactly_its_endpoints()
    {
        var viewer = await StaffAsync("viewer@nexaverify.test", Permissions.System.Configure);
        var reader = await StaffAsync("reader@nexaverify.test", Permissions.Email.Read);
        var manager = await StaffAsync("manager@nexaverify.test", Permissions.Email.Manage);
        var id = await SeedAsync("someone@example.com", EmailStatus.Failed);

        // system.configure: the (masked) outbox only
        (await _app.GetAsync(Outbox, viewer.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _app.GetAsync(Suppressions, viewer.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _app.PostAsync($"{Outbox}/{id}/retry", null, viewer.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // email.read: the suppression list, not the outbox list and not changes
        (await _app.GetAsync(Suppressions, reader.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _app.GetAsync(Outbox, reader.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _app.PostAsync(Test, new SendTestEmailRequest("x@example.com"), reader.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // email.manage: changes
        (await _app.PostAsync($"{Outbox}/{id}/retry", null, manager.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _app.PostAsync(Test, new SendTestEmailRequest("x@example.com"), manager.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await _app.GetAsync(Suppressions, manager.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // ---------------------------------------------------------------- listing

    [Fact]
    public async Task The_outbox_lists_newest_first_with_full_addresses_for_email_readers_and_never_the_bodies()
    {
        await SeedAsync("first.person@example.com", createdAt: DateTime.UtcNow.AddMinutes(-10));
        await SeedAsync("second.person@example.com", createdAt: DateTime.UtcNow.AddMinutes(-5));

        var page = await ListAsync("", _admin.AccessToken);

        page.Items.Select(i => i.To).ShouldBe(["second.person@example.com", "first.person@example.com"]);
        page.TotalCount.ShouldBe(2);
        var raw = await (await _app.GetAsync(Outbox, _admin.AccessToken)).Content.ReadAsStringAsync();
        raw.ShouldNotContain("text body");
        raw.ShouldNotContain("html body");
        raw.ShouldNotContain("textBody", Case.Insensitive);
    }

    [Fact]
    public async Task Recipient_addresses_are_masked_for_staff_without_email_read()
    {
        await SeedAsync("jane.doe@example.com");
        var viewer = await StaffAsync("viewer2@nexaverify.test", Permissions.System.Configure);

        var page = await ListAsync("", viewer.AccessToken);

        page.Items.Single().To.ShouldBe("j***@example.com");
        (await (await _app.GetAsync(Outbox, viewer.AccessToken)).Content.ReadAsStringAsync()).ShouldNotContain("jane.doe");
    }

    [Fact]
    public async Task The_outbox_filters_by_status_template_recipient_and_dates_and_pages()
    {
        await SeedAsync("a@example.com", EmailStatus.Sent, EmailTemplate.PaymentReceipt, DateTime.UtcNow.AddDays(-3));
        await SeedAsync("b@example.com", EmailStatus.Failed, EmailTemplate.PasswordReset, DateTime.UtcNow.AddDays(-2));
        await SeedAsync("bob@example.com", EmailStatus.Abandoned, EmailTemplate.PasswordReset, DateTime.UtcNow.AddDays(-1));
        await SeedAsync("c@example.com", EmailStatus.Sent, EmailTemplate.PasswordReset, DateTime.UtcNow);

        (await ListAsync("?status=Failed", _admin.AccessToken)).Items.Select(i => i.To).ShouldBe(["b@example.com"]);
        (await ListAsync("?status=abandoned", _admin.AccessToken)).Items.Select(i => i.To).ShouldBe(["bob@example.com"]);
        (await ListAsync("?template=PasswordReset", _admin.AccessToken)).TotalCount.ShouldBe(3);
        (await ListAsync("?template=PasswordReset&status=Sent", _admin.AccessToken)).Items.Select(i => i.To).ShouldBe(["c@example.com"]);
        (await ListAsync("?recipient=b@example.com", _admin.AccessToken)).Items.Select(i => i.To).ShouldBe(["b@example.com"]);
        (await ListAsync("?recipient=B", _admin.AccessToken)).Items.Select(i => i.To).ShouldBe(["bob@example.com", "b@example.com"]);
        (await ListAsync($"?from={Uri.EscapeDataString(DateTime.UtcNow.AddDays(-2.5).ToString("o"))}&to={Uri.EscapeDataString(DateTime.UtcNow.AddHours(-12).ToString("o"))}", _admin.AccessToken))
            .Items.Select(i => i.To).ShouldBe(["bob@example.com", "b@example.com"]);
        var paged = await ListAsync("?pageSize=2&page=2", _admin.AccessToken);
        paged.Items.Count.ShouldBe(2);
        paged.TotalCount.ShouldBe(4);
        (await ListAsync("?pageSize=100000", _admin.AccessToken)).PageSize.ShouldBe(PageRequest.MaxPageSize);
    }

    [Theory]
    [InlineData("?status=Bogus")]
    [InlineData("?template=Nope")]
    [InlineData("?status=Sent&template=Nope")]
    public async Task Unknown_filter_values_are_a_validation_problem(string query)
    {
        var response = await _app.GetAsync(Outbox + query, _admin.AccessToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("code").GetString().ShouldBe(ErrorCodes.ValidationFailed);
    }

    // ---------------------------------------------------------------- retry

    [Theory]
    [InlineData(EmailStatus.Failed)]
    [InlineData(EmailStatus.Abandoned)]
    public async Task A_failed_or_abandoned_message_can_be_retried_and_the_retry_is_audited(EmailStatus status)
    {
        var id = await SeedAsync("retry.me@example.com", status);

        var response = await _app.PostAsync($"{Outbox}/{id}/retry", null, _admin.AccessToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var item = (await response.Content.ReadFromJsonAsync<EmailOutboxItemDto>(AuthApp.Json))!;
        item.Status.ShouldBe("Pending");
        item.Attempts.ShouldBe(0);
        item.To.ShouldBe("retry.me@example.com"); // the super admin holds email.read
        var audit = await _app.WithDbAsync(db => db.AuditLogs.Where(a => a.Action == "email.retried").ToListAsync());
        audit.Count.ShouldBe(1);
        audit[0].EntityId.ShouldBe(id.ToString());
        (audit[0].NewValuesJson ?? string.Empty).ShouldNotContain("retry.me@example.com");
    }

    [Fact]
    public async Task Retrying_a_message_in_another_state_is_a_conflict_and_an_unknown_one_is_not_found()
    {
        var sent = await SeedAsync("sent@example.com", EmailStatus.Sent);

        (await _app.PostAsync($"{Outbox}/{sent}/retry", null, _admin.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await _app.PostAsync($"{Outbox}/{Guid.NewGuid()}/retry", null, _admin.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_message_whose_content_was_already_erased_cannot_be_retried()
    {
        var id = await SeedAsync("erased@example.com", EmailStatus.Failed);
        await _app.WithDbAsync(async db =>
        {
            var mail = await db.OutboundEmails.SingleAsync(e => e.Id == id);
            mail.PurgeBodies(DateTime.UtcNow);
            await db.SaveChangesAsync();
            return true;
        });

        var response = await _app.PostAsync($"{Outbox}/{id}/retry", null, _admin.AccessToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().ShouldBe("EMAIL_BODY_PURGED");
    }

    // ---------------------------------------------------------------- suppression list

    private async Task<Guid> SuppressAsync(string address)
    {
        var suppression = EmailSuppression.Create(address, "SMTP 550 5.1.1 user unknown", null, DateTime.UtcNow);
        await _app.WithDbAsync(async db =>
        {
            db.EmailSuppressions.Add(suppression);
            await db.SaveChangesAsync();
            return true;
        });
        return suppression.Id;
    }

    [Fact]
    public async Task The_suppression_list_is_listed_and_an_entry_can_be_cleared_with_an_audit_trail()
    {
        var keep = await SuppressAsync("keep@example.com");
        var clear = await SuppressAsync("clear@example.com");

        var list = (await (await _app.GetAsync(Suppressions, _admin.AccessToken)).Content.ReadFromJsonAsync<PagedResult<EmailSuppressionDto>>(AuthApp.Json))!;
        list.TotalCount.ShouldBe(2);
        list.Items.Select(s => s.Address).ShouldBe(["clear@example.com", "keep@example.com"], ignoreOrder: true);

        (await _app.DeleteAsync($"{Suppressions}/{clear}", _admin.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await _app.WithDbAsync(db => db.EmailSuppressions.Select(s => s.Id).ToListAsync())).ShouldBe([keep]);
        (await _app.WithDbAsync(db => db.AuditLogs.CountAsync(a => a.Action == "email.suppression_removed"))).ShouldBe(1);
        (await _app.DeleteAsync($"{Suppressions}/{clear}", _admin.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ---------------------------------------------------------------- test email

    [Fact]
    public async Task A_test_email_is_queued_with_the_admin_test_template_and_audited_without_the_address()
    {
        var response = await _app.PostAsync(Test, new SendTestEmailRequest("  Tester@Example.com "), _admin.AccessToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var queued = (await response.Content.ReadFromJsonAsync<SendTestEmailResponse>(AuthApp.Json))!;
        var mail = _app.Emails.Sent.Single();
        mail.Id.ShouldBe(queued.Id);
        mail.To.ShouldBe("Tester@Example.com");
        mail.Template.ShouldBe("AdminTest");
        mail.Status.ShouldBe("Pending");
        mail.Subject.ShouldBe("NexaVerify test email");
        mail.Body.ShouldContain("test message");
        var audit = await _app.WithDbAsync(db => db.AuditLogs.SingleAsync(a => a.Action == "email.test_sent"));
        (audit.NewValuesJson ?? string.Empty).ShouldNotContain("Tester@Example.com");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-address")]
    [InlineData("a@b.com\r\nBcc: x@y.com")]
    public async Task A_test_email_needs_a_plain_address(string to)
    {
        var response = await _app.PostAsync(Test, new SendTestEmailRequest(to), _admin.AccessToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        _app.Emails.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task Test_emails_are_rate_limited_per_administrator()
    {
        for (var i = 0; i < 3; i++)
        {
            (await _app.PostAsync(Test, new SendTestEmailRequest($"t{i}@example.com"), _admin.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        var limited = await _app.PostAsync(Test, new SendTestEmailRequest("t9@example.com"), _admin.AccessToken);

        limited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        limited.Headers.Contains("Retry-After").ShouldBeTrue();
        _app.Emails.Sent.Count.ShouldBe(3);
    }

    // ---------------------------------------------------------------- the flows that send mail

    [Fact]
    public async Task Forgot_password_queues_exactly_one_reset_mail_whose_link_works_and_the_reset_queues_a_change_notice()
    {
        await _app.PostAsync("/api/v1/auth/forgot-password", new ForgotPasswordRequest(DatabaseBootstrap.SuperAdminEmail));

        var reset = _app.Emails.Sent.Single();
        reset.To.ShouldBe(DatabaseBootstrap.SuperAdminEmail);
        reset.Template.ShouldBe("PasswordReset");
        reset.Status.ShouldBe("Pending");
        reset.Html.ShouldContain("<!DOCTYPE html>");
        var (email, token) = AuthApp.LinkFrom(reset);
        reset.Html.ShouldContain(token); // the same link in the HTML part
        reset.MessageId.ShouldNotBeNullOrWhiteSpace();
        reset.CorrelationId.ShouldNotBeNullOrWhiteSpace();

        (await _app.PostAsync("/api/v1/auth/reset-password", new ResetPasswordRequest(email, token, "A-Brand-New-Passphrase-77"))).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        _app.Emails.Sent.Count.ShouldBe(2);
        var notice = _app.Emails.Sent[1];
        notice.Template.ShouldBe("PasswordChanged");
        notice.To.ShouldBe(DatabaseBootstrap.SuperAdminEmail);
        notice.Body.ShouldContain("password reset");
        notice.Body.ShouldNotContain(token);
    }

    [Fact]
    public async Task Changing_the_password_while_signed_in_queues_a_change_notice()
    {
        var before = _app.Emails.Sent.Count;

        var changed = await _app.PostAsync("/api/v1/auth/change-password", new ChangePasswordRequest(AuthApp.StrongPassword, "Another-Strong-Passphrase-31"), _admin.AccessToken);

        changed.StatusCode.ShouldBe(HttpStatusCode.OK);
        var notice = _app.Emails.Sent.Skip(before).Single();
        notice.Template.ShouldBe("PasswordChanged");
        notice.Body.ShouldContain("account settings");
    }

    [Fact]
    public async Task A_rejected_change_queues_no_mail()
    {
        var before = _app.Emails.Sent.Count;

        var changed = await _app.PostAsync("/api/v1/auth/change-password", new ChangePasswordRequest("wrong-current-password", "Another-Strong-Passphrase-31"), _admin.AccessToken);

        changed.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        _app.Emails.Sent.Count.ShouldBe(before);
    }

    [Fact]
    public async Task Inviting_a_client_user_queues_one_invitation_with_a_working_link()
    {
        var (_, clientAdmin) = await _app.OnboardClientAsync(_admin.AccessToken, "INVT", "owner@invt.test");
        var before = _app.Emails.Sent.Count;

        var created = await _app.PostAsync("/api/v1/client/users", new { email = "newbie@invt.test", fullName = "New Bie", role = SystemRoles.ClientUser, jobTitle = (string?)null }, clientAdmin.AccessToken);

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var invite = _app.Emails.Sent.Skip(before).Single();
        invite.Template.ShouldBe("UserInvite");
        invite.To.ShouldBe("newbie@invt.test");
        var (email, token) = AuthApp.LinkFrom(invite);
        (await _app.PostAsync("/api/v1/auth/reset-password", new ResetPasswordRequest(email, token, AuthApp.StrongPassword))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await _app.LoginAsync("newbie@invt.test", AuthApp.StrongPassword)).User.Email.ShouldBe("newbie@invt.test");
        _app.Emails.Sent.Skip(before).Count(m => m.Template == "PasswordChanged").ShouldBe(0); // choosing a first password is setup, not a change to warn about
    }

    // ---------------------------------------------------------------- startup guard

    [Fact]
    public async Task Production_refuses_to_start_with_the_logging_provider_unless_it_is_allowed_on_purpose()
    {
        var connection = await _fixture.CreateDatabaseAsync();
        await using var refused = new ApiFactory
        {
            ConnectionString = connection,
            Environment = "Production",
            UseTestAuth = false,
            Settings = new Dictionary<string, string>
            {
                ["Email:AllowLoggingProvider"] = "false",
                ["Portal:PublicBaseUrl"] = "https://portal.test",
                ["AllowedHosts"] = "api.example.com",
                ["Hosting:RedirectToHttps"] = "false",
            },
        };

        var ex = Should.Throw<Exception>(() => refused.CreateClient());

        Flatten(ex).ShouldContain("Logging email provider");
    }

    [Fact]
    public async Task Production_starts_with_a_complete_smtp_configuration()
    {
        var connection = await _fixture.CreateDatabaseAsync();
        await DatabaseBootstrap.MigrateAsync(connection);
        await using var factory = new ApiFactory
        {
            ConnectionString = connection,
            Environment = "Production",
            UseTestAuth = false,
            Settings = new Dictionary<string, string>
            {
                ["AllowedHosts"] = "localhost",
                ["Hosting:RedirectToHttps"] = "false",
                ["Email:AllowLoggingProvider"] = "false",
                ["Email:Provider"] = "Smtp",
                ["Email:Smtp:Host"] = "smtp.invalid",
                ["Email:Smtp:Username"] = "user",
                ["Email:Smtp:Password"] = "pass",
                ["Portal:PublicBaseUrl"] = "https://portal.test",
            },
        };

        (await factory.CreateClient().GetAsync("/health/live")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static string Flatten(Exception ex) => string.Join(" | ", Chain(ex).Select(e => e.Message));

    private static IEnumerable<Exception> Chain(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            yield return ex;
            if (ex is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions.SelectMany(Chain))
                {
                    yield return inner;
                }
            }
        }
    }
}
