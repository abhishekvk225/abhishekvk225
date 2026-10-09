using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NexaVerify.Api.IntegrationTests.Support;
using NexaVerify.Application.Common;
using NexaVerify.Application.Licensing;
using NexaVerify.Application.Public;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Identity;
using NexaVerify.Contracts.Licensing;
using NexaVerify.Contracts.Public;
using NexaVerify.Domain.Common;
using NexaVerify.Domain.Identity;
using NexaVerify.Domain.Tenancy;
using NexaVerify.Infrastructure.Background;
using NexaVerify.TestSupport;

namespace NexaVerify.Api.IntegrationTests;

/// <summary>Public self-service sign-up end to end: the real API on a migrated SQL Server database, anonymous callers only.</summary>
[Collection(SqlServerCollection.Name)]
public class PublicSignupTests : IAsyncLifetime
{
    private const string Password = "Correct-Horse-Battery-9";

    private readonly SqlServerFixture _fixture;
    private AuthApp _app = null!;

    public PublicSignupTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    /// <summary>Generous limits and no response padding unless a test says otherwise.</summary>
    private static Dictionary<string, string> Fast(params (string Key, string Value)[] extra)
    {
        var settings = new Dictionary<string, string>
        {
            ["Portal:PublicBaseUrl"] = "https://portal.test",
            ["Signup:MinimumResponseMilliseconds"] = "0",
            ["Signup:MaxSignupsPerIpPerHour"] = "1000",
            ["Signup:MaxSignupsPerEmailPerDay"] = "1000",
            ["Signup:MaxContactsPerIpPerHour"] = "1000",
            ["Signup:TrialCredits"] = "40",
            ["Signup:TrialDays"] = "10",
        };
        foreach (var (key, value) in extra)
        {
            settings[key] = value;
        }

        return settings;
    }

    private async Task StartAsync(Dictionary<string, string>? settings = null, Action<IServiceCollection>? configure = null) =>
        _app = await AuthApp.CreateAsync(_fixture, settings ?? Fast(), configure);

    private static object SignupBody(string email = "ada@acme.test", string company = "Acme Ltd", string password = Password, bool terms = true, string? website = null, string? captcha = null) =>
        new { companyName = company, fullName = "Ada Lovelace", email, password, acceptTerms = terms, captchaToken = captcha, website };

    private Task<HttpResponseMessage> SignupAsync(string email = "ada@acme.test", string company = "Acme Ltd", string password = Password, bool terms = true, string? website = null, string? captcha = null) =>
        _app.PostAsync("/api/v1/public/signup", SignupBody(email, company, password, terms, website, captcha));

    private Task<HttpResponseMessage> VerifyAsync(string email, string token) =>
        _app.PostAsync("/api/v1/public/signup/verify", new { email, token });

    private (string Email, string Token) LinkOf(int mailIndex) => AuthApp.LinkFrom(_app.Emails.Sent[mailIndex]);

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    /// <summary>Signs up and verifies; returns the verified address.</summary>
    private async Task<string> OnboardAsync(string email, string company)
    {
        var before = _app.Emails.Sent.Count;
        (await SignupAsync(email, company)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var (mailEmail, token) = LinkOf(before);
        (await VerifyAsync(mailEmail, token)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        return email;
    }

    // ---------------------------------------------------------------- plans and config

    [Fact]
    public async Task Public_plans_are_anonymous_cacheable_and_show_the_configured_trial_values()
    {
        await StartAsync();

        var response = await _app.GetAsync("/api/v1/public/plans");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var cache = response.Headers.CacheControl!;
        cache.Public.ShouldBeTrue();
        cache.MaxAge.ShouldBe(TimeSpan.FromSeconds(60));
        var plans = (await response.Content.ReadFromJsonAsync<List<PublicPlanDto>>(AuthApp.Json))!;
        var trial = plans.Single(); // only the trial plan is published by the seed
        trial.IsTrial.ShouldBeTrue();
        trial.Name.ShouldBe("Free trial");
        trial.Credits.ShouldBe(40);
        trial.ValidityDays.ShouldBe(10);
        trial.Highlights.ShouldNotBeEmpty();
        trial.DisplayPrice.ShouldBeNull();

        var raw = await response.Content.ReadAsStringAsync();
        foreach (var field in new[] { "id", "name", "description", "credits", "validityDays", "highlights", "isTrial" })
        {
            JsonDocument.Parse(raw).RootElement[0].TryGetProperty(field, out _).ShouldBeTrue(field);
        }

        raw.ShouldNotContain("rateLimit"); // plan internals are not published
        raw.ShouldNotContain("\"code\""); // internal plan codes are not published
    }

    [Fact]
    public async Task Admins_publish_hide_and_describe_plans_and_old_clients_do_not_clear_the_public_fields()
    {
        await StartAsync();
        var admin = await _app.SuperAdminAsync();
        var plans = (await (await _app.GetAsync("/api/v1/admin/plans", admin.AccessToken)).Content.ReadFromJsonAsync<List<PlanDto>>(AuthApp.Json))!;
        var trial = plans.Single(p => p.Code == "TRIAL");
        trial.IsPublic.ShouldBeTrue();
        trial.IsTrial.ShouldBeTrue();
        var starter = plans.Single(p => p.Code == "STARTER");
        starter.IsPublic.ShouldBeFalse();

        // publish Starter with a price text, highlights and an order
        var save = new SavePlanRequest("STARTER", starter.Name, "For small teams", starter.DefaultCredits, starter.DefaultDurationDays, starter.RateLimitPerMinute,
            starter.DailyQuota, starter.MaxFaceProfiles, starter.MaxApiKeys, starter.MaxUsers, true, true, false, 5, "From 49 / month", ["10,000 credits", "Email support"]);
        (await _app.PutAsync($"/api/v1/admin/plans/{starter.Id}", save, admin.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var visible = (await (await _app.GetAsync("/api/v1/public/plans")).Content.ReadFromJsonAsync<List<PublicPlanDto>>(AuthApp.Json))!;
        visible.Select(p => p.Name).ShouldBe(["Free trial", starter.Name]);
        var published = visible[1];
        published.DisplayPrice.ShouldBe("From 49 / month");
        published.Highlights.ShouldBe(["10,000 credits", "Email support"]);
        published.Credits.ShouldBe(starter.DefaultCredits);
        published.IsTrial.ShouldBeFalse();

        // an older admin client that does not know the new fields keeps everything as it was
        var legacy = new SavePlanRequest("STARTER", starter.Name, "Edited description", starter.DefaultCredits, starter.DefaultDurationDays, starter.RateLimitPerMinute,
            starter.DailyQuota, starter.MaxFaceProfiles, starter.MaxApiKeys, starter.MaxUsers, true);
        var updated = (await (await _app.PutAsync($"/api/v1/admin/plans/{starter.Id}", legacy, admin.AccessToken)).Content.ReadFromJsonAsync<PlanDto>(AuthApp.Json))!;
        updated.IsPublic.ShouldBeTrue();
        updated.DisplayPrice.ShouldBe("From 49 / month");
        updated.Highlights.ShouldBe(["10,000 credits", "Email support"]);

        // hiding and deactivating take plans off the site
        (await _app.PutAsync($"/api/v1/admin/plans/{starter.Id}", save with { IsPublic = false, DisplayPrice = string.Empty, Highlights = [] }, admin.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _app.PutAsync($"/api/v1/admin/plans/{trial.Id}", legacy with { Code = "TRIAL", Name = trial.Name, IsActive = false }, admin.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await (await _app.GetAsync("/api/v1/public/plans")).Content.ReadFromJsonAsync<List<PublicPlanDto>>(AuthApp.Json))!.ShouldBeEmpty();

        // the new fields are validated
        var tooMany = save with { Highlights = Enumerable.Range(0, 11).Select(i => "h" + i).ToList() };
        (await _app.PutAsync($"/api/v1/admin/plans/{starter.Id}", tooMany, admin.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Public_config_reports_the_switches_and_never_the_secret()
    {
        await StartAsync(Fast(("Captcha:Provider", "turnstile"), ("Captcha:SiteKey", "0xSITE"), ("Captcha:SecretKey", "0xTOP-SECRET")));

        var response = await _app.GetAsync("/api/v1/public/config");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var config = (await response.Content.ReadFromJsonAsync<PublicConfigDto>(AuthApp.Json))!;
        config.ShouldBe(new PublicConfigDto(true, 40, 10, new CaptchaConfigDto("turnstile", "0xSITE")));
        (await response.Content.ReadAsStringAsync()).ShouldNotContain("0xTOP-SECRET");

        var raw = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        raw.GetProperty("captcha").GetProperty("provider").GetString().ShouldBe("turnstile");
        raw.GetProperty("signupEnabled").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Public_config_defaults_to_no_captcha()
    {
        await StartAsync();

        var config = (await (await _app.GetAsync("/api/v1/public/config")).Content.ReadFromJsonAsync<PublicConfigDto>(AuthApp.Json))!;

        config.Captcha.ShouldBe(new CaptchaConfigDto("none", null));
    }

    // ---------------------------------------------------------------- sign-up and verification

    [Fact]
    public async Task Signing_up_and_verifying_creates_a_working_client_with_an_admin_and_a_trial_license()
    {
        await StartAsync();

        var signup = await SignupAsync("  Ada@Acme.TEST ", "Acme Ltd");

        signup.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await signup.Content.ReadAsStringAsync()).ShouldBe("{}");
        var mail = _app.Emails.Sent.Single();
        mail.To.ShouldBe("ada@acme.test");
        mail.Subject.ShouldBe("Confirm your NexaVerify account");
        mail.Body.ShouldContain("Hello Ada Lovelace");
        mail.Body.ShouldContain("40 credits for 10 days");
        mail.Body.ShouldContain("24 hours");
        mail.Body.ShouldNotContain(Password);
        var link = new Uri(mail.Body.Split('\n').Single(l => l.StartsWith("https://", StringComparison.Ordinal)));
        link.GetLeftPart(UriPartial.Path).ShouldBe("https://portal.test/verify-email");
        var (email, token) = AuthApp.LinkFrom(mail);
        email.ShouldBe("ada@acme.test");

        // nothing exists yet: no account, no client
        (await _app.PostAsync("/api/v1/auth/login", new LoginRequest("ada@acme.test", Password))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await _app.WithDbAsync(db => db.Clients.CountAsync(c => !c.IsSystem))).ShouldBe(0);

        var verify = await VerifyAsync(email, token);
        verify.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // the person can sign in to the portal with the password they chose, with no forced change
        var login = await _app.LoginAsync("ada@acme.test", Password);
        login.MustChangePassword.ShouldBeFalse();
        login.User.Roles.ShouldBe([SystemRoles.ClientAdmin]);
        var me = (await (await _app.GetAsync("/api/v1/auth/me", login.AccessToken)).Content.ReadFromJsonAsync<MeResponse>(AuthApp.Json))!;
        me.Client!.Status.ShouldBe("Active");
        me.Client.Code.ShouldBe("ACME-LTD");
        me.Client.Name.ShouldBe("Acme Ltd");
        me.Permissions.ShouldContain(Permissions.ApiKeys.Manage);

        // a trial license with the configured credits and period, backed by a ledger grant
        var summary = (await (await _app.GetAsync("/api/v1/client/licenses/summary", login.AccessToken)).Content.ReadFromJsonAsync<LicenseSummaryDto>(AuthApp.Json))!;
        var license = summary.Licenses.Single();
        license.Name.ShouldBe("Free trial");
        license.TotalCredits.ShouldBe(40);
        license.RemainingCredits.ShouldBe(40);
        license.EffectiveStatus.ShouldBe("Active");
        license.ExpiresAt.ShouldBeInRange(DateTime.UtcNow.AddDays(9), DateTime.UtcNow.AddDays(10).AddMinutes(1));
        var ledger = await _app.WithDbAsync(db => db.LicenseTransactions.Where(t => t.LicenseId == license.Id).ToListAsync());
        ledger.Single().Credits.ShouldBe(40);
        ledger.Single().Type.ToString().ShouldBe("Grant");
        var planId = await _app.WithDbAsync(db => db.Licenses.Where(l => l.Id == license.Id).Select(l => l.PlanId).SingleAsync());
        planId.ShouldBe(await _app.WithDbAsync(db => db.Plans.Where(p => p.IsTrial).Select(p => p.Id).SingleAsync()));
        (await _app.GetAsync($"/api/v1/admin/licenses/{license.Id}/verify-ledger", (await _app.SuperAdminAsync()).AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Verification_is_audited_with_the_public_signup_source_and_without_secrets()
    {
        await StartAsync();
        await OnboardAsync("ada@acme.test", "Acme Ltd");

        var audit = await _app.WithDbAsync(db => db.AuditLogs.Where(a => a.Action == "signup.requested" || a.Action == "signup.verified" || a.Action == "client.created").ToListAsync());

        audit.Select(a => a.Action).ShouldBe(["signup.requested", "client.created", "signup.verified"], ignoreOrder: true);
        audit.Single(a => a.Action == "signup.verified").ActorType.ToString().ShouldBe("System");
        audit.Single(a => a.Action == "signup.verified").NewValuesJson!.ShouldContain("public-signup");
        audit.Single(a => a.Action == "client.created").NewValuesJson!.ShouldContain("public-signup");
        foreach (var row in audit)
        {
            (row.NewValuesJson ?? string.Empty).ShouldNotContain(Password);
            (row.NewValuesJson ?? string.Empty).ShouldNotContain("hash", Case.Insensitive);
            (row.NewValuesJson ?? string.Empty).ShouldNotContain("token", Case.Insensitive);
        }
    }

    [Fact]
    public async Task Only_hashes_are_stored_and_the_consumed_row_loses_its_password_hash()
    {
        await StartAsync();
        (await SignupAsync()).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var (email, token) = LinkOf(0);

        var pending = await _app.WithDbAsync(db => db.PendingSignups.SingleAsync());
        pending.TokenHash.Length.ShouldBe(32);
        pending.TokenHash.ShouldBe(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
        pending.PasswordHash.ShouldNotContain(Password);
        pending.PasswordHash.ShouldNotBeEmpty();
        pending.ExpiresAt.ShouldBeInRange(DateTime.UtcNow.AddHours(23.9), DateTime.UtcNow.AddHours(24.1));

        (await VerifyAsync(email, token)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var consumed = await _app.WithDbAsync(db => db.PendingSignups.SingleAsync());
        consumed.ConsumedAt.ShouldNotBeNull();
        consumed.PasswordHash.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_second_company_with_the_same_name_gets_a_distinct_code_and_a_separate_tenant()
    {
        await StartAsync();
        await OnboardAsync("one@acme.test", "Acme Ltd");
        await OnboardAsync("two@acme.test", "Acme Ltd");

        var codes = await _app.WithDbAsync(db => db.Clients.Where(c => !c.IsSystem).OrderBy(c => c.CreatedAt).Select(c => c.Code).ToListAsync());

        codes.Count.ShouldBe(2);
        codes[0].ShouldBe("ACME-LTD");
        codes[1].ShouldStartWith("ACME-LTD-");
        codes[1].Length.ShouldBeLessThanOrEqualTo(30);
    }

    [Fact]
    public async Task A_new_clients_admin_sees_only_its_own_tenant()
    {
        await StartAsync();
        await OnboardAsync("a@alpha.test", "Alpha");
        await OnboardAsync("b@beta.test", "Beta");
        var a = await _app.LoginAsync("a@alpha.test", Password);
        var b = await _app.LoginAsync("b@beta.test", Password);
        a.User.ClientId.ShouldNotBe(b.User.ClientId);

        var licenseOfB = (await (await _app.GetAsync("/api/v1/client/licenses/summary", b.AccessToken)).Content.ReadFromJsonAsync<LicenseSummaryDto>(AuthApp.Json))!.Licenses.Single();
        var summaryA = (await (await _app.GetAsync("/api/v1/client/licenses/summary", a.AccessToken)).Content.ReadFromJsonAsync<LicenseSummaryDto>(AuthApp.Json))!;

        summaryA.Licenses.Single().Id.ShouldNotBe(licenseOfB.Id);
        (await _app.GetAsync($"/api/v1/client/licenses/{licenseOfB.Id}", a.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _app.GetAsync("/api/v1/admin/clients", a.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _app.GetAsync("/api/v1/admin/contact-requests", a.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _app.WithTenantDbAsync(a.User.ClientId!.Value, db => db.Licenses.CountAsync())).ShouldBe(1);
    }

    // ---------------------------------------------------------------- enumeration neutrality

    [Fact]
    public async Task New_pending_and_registered_addresses_get_identical_responses()
    {
        await StartAsync();
        await OnboardAsync("known@acme.test", "Known Co");
        var before = _app.Emails.Sent.Count;

        var fresh = await SignupAsync("fresh@acme.test");
        var pending = await SignupAsync("fresh@acme.test"); // second time: already pending
        var registered = await SignupAsync("known@acme.test");
        var upperCase = await SignupAsync("KNOWN@ACME.TEST");

        foreach (var response in new[] { fresh, pending, registered, upperCase })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
            (await response.Content.ReadAsStringAsync()).ShouldBe("{}");
            response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
            response.Headers.Select(h => h.Key).Except(["X-Correlation-Id"]).Order().ShouldBe(fresh.Headers.Select(h => h.Key).Except(["X-Correlation-Id"]).Order());
        }

        // only the email differs
        var mails = _app.Emails.Sent.Skip(before).ToList();
        mails.Select(m => m.Subject).ShouldBe(
            ["Confirm your NexaVerify account", "Confirm your NexaVerify account", "You already have a NexaVerify account", "You already have a NexaVerify account"]);
        var existing = mails[2];
        existing.To.ShouldBe("known@acme.test");
        existing.Body.ShouldContain("https://portal.test/login");
        existing.Body.ShouldContain("https://portal.test/forgot-password");
        existing.Body.ShouldNotContain("token=");
        existing.Body.ShouldNotContain("verify-email");
        (await _app.WithDbAsync(db => db.PendingSignups.CountAsync(p => p.NormalizedEmail == User.Normalize("known@acme.test") && p.ConsumedAt == null))).ShouldBe(0);
    }

    [Fact]
    public async Task Response_time_does_not_depend_on_whether_the_address_is_known()
    {
        await StartAsync(Fast(("Signup:MinimumResponseMilliseconds", "400")));
        await OnboardAsync("known@acme.test", "Known Co");

        async Task<TimeSpan> TimeAsync(Func<Task<HttpResponseMessage>> call)
        {
            var timer = Stopwatch.StartNew();
            (await call()).StatusCode.ShouldBe(HttpStatusCode.Accepted);
            return timer.Elapsed;
        }

        var fresh = await TimeAsync(() => SignupAsync("fresh@acme.test"));
        var known = await TimeAsync(() => SignupAsync("known@acme.test"));
        var honeypot = await TimeAsync(() => SignupAsync("bot@acme.test", website: "http://spam.test"));

        foreach (var elapsed in new[] { fresh, known, honeypot })
        {
            elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(380));
        }
    }

    [Fact]
    public async Task A_repeated_signup_replaces_the_pending_one_and_only_the_latest_link_works()
    {
        await StartAsync();
        (await SignupAsync(company: "First Name")).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await SignupAsync(company: "Second Name")).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var first = LinkOf(0);
        var second = LinkOf(1);

        (await _app.WithDbAsync(db => db.PendingSignups.CountAsync(p => p.ConsumedAt == null))).ShouldBe(1);
        (await VerifyAsync(first.Email, first.Token)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await VerifyAsync(second.Email, second.Token)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await _app.WithDbAsync(db => db.Clients.Where(c => !c.IsSystem).Select(c => c.Name).SingleAsync())).ShouldBe("Second Name");
    }

    [Fact]
    public async Task Every_verification_failure_is_the_same_generic_400()
    {
        await StartAsync();
        (await SignupAsync()).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var (email, token) = LinkOf(0);

        var wrongToken = await VerifyAsync(email, token + "x");
        var unknownEmail = await VerifyAsync("nobody@acme.test", token);
        var otherUsersToken = await VerifyAsync("other@acme.test", "AAAA");

        var bodies = new List<string>();
        foreach (var response in new[] { wrongToken, unknownEmail, otherUsersToken })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
            bodies.Add(NormalizeCorrelation(await response.Content.ReadAsStringAsync()));
        }

        bodies.Distinct().Count().ShouldBe(1);
        bodies[0].ShouldContain("invalid or has expired");
        (await _app.WithDbAsync(db => db.Clients.CountAsync(c => !c.IsSystem))).ShouldBe(0);
        (await VerifyAsync(email, token)).StatusCode.ShouldBe(HttpStatusCode.NoContent); // failed guesses did not burn the real link
    }

    private static string NormalizeCorrelation(string json) =>
        System.Text.RegularExpressions.Regex.Replace(json, "\"correlationId\":\"[^\"]*\"", "\"correlationId\":\"x\"");

    [Fact]
    public async Task An_expired_link_is_refused_and_a_used_link_cannot_be_replayed()
    {
        await StartAsync();
        (await SignupAsync("old@acme.test")).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await SignupAsync("used@acme.test")).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var old = LinkOf(0);
        var used = LinkOf(1);
        await _app.WithDbAsync(async db => await db.Database.ExecuteSqlRawAsync("UPDATE iam.PendingSignups SET ExpiresAt = DATEADD(second, -1, SYSUTCDATETIME()) WHERE NormalizedEmail = 'OLD@ACME.TEST'"));

        (await VerifyAsync(old.Email, old.Token)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await VerifyAsync(used.Email, used.Token)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await VerifyAsync(used.Email, used.Token)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await _app.WithDbAsync(db => db.Clients.CountAsync(c => !c.IsSystem))).ShouldBe(1);
        (await _app.WithDbAsync(db => db.Users.CountAsync(u => u.NormalizedEmail == "OLD@ACME.TEST"))).ShouldBe(0);
    }

    [Fact]
    public async Task Concurrent_verifications_of_one_link_create_exactly_one_account()
    {
        await StartAsync();
        (await SignupAsync()).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var (email, token) = LinkOf(0);

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => VerifyAsync(email, token)));

        responses.Count(r => r.StatusCode == HttpStatusCode.NoContent).ShouldBe(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.BadRequest).ShouldBe(9);
        (await _app.WithDbAsync(db => db.Clients.CountAsync(c => !c.IsSystem))).ShouldBe(1);
        (await _app.WithDbAsync(db => db.Users.CountAsync(u => u.NormalizedEmail == "ADA@ACME.TEST"))).ShouldBe(1);
        (await _app.WithDbAsync(db => db.Licenses.CountAsync())).ShouldBe(1);
        (await _app.WithDbAsync(db => db.LicenseTransactions.CountAsync())).ShouldBe(1);
        (await _app.WithDbAsync(db => db.AuditLogs.CountAsync(a => a.Action == "signup.verified"))).ShouldBe(1);
    }

    private sealed class FailingLicenseService : ILicenseService
    {
        private readonly LicenseService _inner;
        private readonly Func<bool> _fail;

        public FailingLicenseService(LicenseService inner, Func<bool> fail)
        {
            _inner = inner;
            _fail = fail;
        }

        public Task<Result<LicenseDto>> CreateAsync(Guid clientId, CreateLicenseRequest request, CancellationToken cancellationToken) =>
            _fail() ? Task.FromResult<Result<LicenseDto>>(Error.Failure("TEST_FAILURE", "boom")) : _inner.CreateAsync(clientId, request, cancellationToken);

        public Task<Result<PagedResult<LicenseListItemDto>>> ListAsync(LicenseListQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<LicenseDto>> GetAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<LicenseDto>> UpdateAsync(Guid id, UpdateLicenseRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<LicenseDto>> ActivateAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<LicenseDto>> DeactivateAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<LicenseDto>> SuspendAsync(Guid id, LicenseReasonRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<LicenseDto>> RevokeAsync(Guid id, LicenseReasonRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<LicenseDto>> RenewAsync(Guid id, RenewLicenseRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<AdjustOutcome>> AdjustAsync(Guid id, AdjustLicenseRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<LicenseTransactionDto>> RefundAsync(long transactionId, RefundRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<PagedResult<LicenseTransactionDto>>> GetTransactionsAsync(Guid id, PageRequest page, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<LedgerVerificationDto>> VerifyLedgerAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_failure_while_creating_the_account_rolls_everything_back_and_keeps_the_link_usable()
    {
        var fail = true;
        await StartAsync(configure: services =>
            services.AddScoped<ILicenseService>(sp => new FailingLicenseService(sp.GetRequiredService<LicenseService>(), () => fail)));
        (await SignupAsync()).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var (email, token) = LinkOf(0);

        (await VerifyAsync(email, token)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await _app.WithDbAsync(db => db.Clients.CountAsync(c => !c.IsSystem))).ShouldBe(0);
        (await _app.WithDbAsync(db => db.Users.CountAsync(u => u.NormalizedEmail == "ADA@ACME.TEST"))).ShouldBe(0);
        (await _app.WithDbAsync(db => db.ClientKeys.CountAsync())).ShouldBe(0);
        (await _app.WithDbAsync(db => db.PendingSignups.SingleAsync())).ConsumedAt.ShouldBeNull();

        fail = false;
        (await VerifyAsync(email, token)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await _app.WithDbAsync(db => db.Clients.CountAsync(c => !c.IsSystem))).ShouldBe(1);
    }

    [Fact]
    public async Task A_link_is_refused_when_the_address_became_a_user_in_the_meantime()
    {
        await StartAsync();
        (await SignupAsync()).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var (email, token) = LinkOf(0);
        var admin = await _app.SuperAdminAsync();
        (await _app.PostAsync("/api/v1/admin/clients", AuthApp.NewClientRequest("TAKEN", "ada@acme.test"), admin.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Created);

        (await VerifyAsync(email, token)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await _app.WithDbAsync(db => db.PendingSignups.SingleAsync())).ConsumedAt.ShouldBeNull();
        (await _app.WithDbAsync(db => db.Clients.CountAsync(c => !c.IsSystem))).ShouldBe(1);
    }

    // ---------------------------------------------------------------- resend

    private Task<HttpResponseMessage> ResendAsync(string email) => _app.PostAsync("/api/v1/public/signup/resend", new { email });

    [Fact]
    public async Task Resending_issues_a_fresh_link_and_invalidates_the_old_one()
    {
        await StartAsync();
        (await SignupAsync()).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var first = LinkOf(0);

        var resend = await ResendAsync("  ADA@acme.test ");

        resend.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await resend.Content.ReadAsStringAsync()).ShouldBe("{}");
        _app.Emails.Sent.Count.ShouldBe(2);
        _app.Emails.Sent[1].Subject.ShouldBe("Confirm your NexaVerify account");
        _app.Emails.Sent[1].To.ShouldBe("ada@acme.test");
        var second = LinkOf(1);
        second.Token.ShouldNotBe(first.Token);
        (await VerifyAsync(first.Email, first.Token)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await VerifyAsync(second.Email, second.Token)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await _app.LoginAsync("ada@acme.test", Password)).User.Roles.ShouldBe([SystemRoles.ClientAdmin]); // the original password still works
    }

    [Fact]
    public async Task Resend_is_neutral_for_unknown_registered_and_exhausted_addresses()
    {
        await StartAsync();
        await OnboardAsync("known@acme.test", "Known Co");
        (await SignupAsync("pending@acme.test")).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var before = _app.Emails.Sent.Count;

        var responses = new List<HttpResponseMessage>
        {
            await ResendAsync("nobody@acme.test"),
            await ResendAsync("known@acme.test"),
            await ResendAsync("pending@acme.test"),
        };
        for (var i = 0; i < 4; i++)
        {
            responses.Add(await ResendAsync("pending@acme.test")); // beyond the 3 allowed
        }

        foreach (var response in responses)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
            (await response.Content.ReadAsStringAsync()).ShouldBe("{}");
        }

        _app.Emails.Sent.Count.ShouldBe(before + 3); // only the pending address, and only 3 times
        _app.Emails.Sent.Skip(before).ShouldAllBe(m => m.To == "pending@acme.test");
        (await _app.WithDbAsync(db => db.PendingSignups.Where(p => p.ConsumedAt == null).Select(p => p.ResendCount).SingleAsync())).ShouldBe(3);
    }

    [Fact]
    public async Task Resend_does_not_revive_an_expired_signup_and_is_validated_and_rate_limited()
    {
        await StartAsync(Fast(("Signup:MaxSignupsPerIpPerHour", "4")));
        (await SignupAsync("old@acme.test")).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await _app.WithDbAsync(async db => await db.Database.ExecuteSqlRawAsync("UPDATE iam.PendingSignups SET ExpiresAt = DATEADD(second, -1, SYSUTCDATETIME())"));

        (await ResendAsync("old@acme.test")).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        _app.Emails.Sent.Count.ShouldBe(1);

        (await _app.PostAsync("/api/v1/public/signup/resend", new { email = "nope" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await ResendAsync("a@acme.test")).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await ResendAsync("b@acme.test")).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await ResendAsync("c@acme.test")).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests); // shares the sign-up IP budget
    }

    // ---------------------------------------------------------------- validation and abuse controls

    [Theory]
    [InlineData("password", "Password123", "password")]
    [InlineData("short", "short", "password")]
    [InlineData("terms", "", "acceptTerms")]
    [InlineData("email", "not-an-email", "email")]
    [InlineData("disposable", "x@mailinator.com", "email")]
    [InlineData("company", "", "companyName")]
    public async Task Invalid_input_is_a_400_problem_with_the_field_named(string kind, string value, string field)
    {
        await StartAsync();
        var body = kind switch
        {
            "password" => SignupBody(password: value),
            "short" => SignupBody(password: value),
            "terms" => SignupBody(terms: false),
            "email" => SignupBody(email: value),
            "disposable" => SignupBody(email: value),
            _ => SignupBody(company: value),
        };

        var response = await _app.PostAsync("/api/v1/public/signup", body);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var json = await BodyAsync(response);
        json.GetProperty("code").GetString().ShouldBe("VALIDATION_FAILED");
        json.GetProperty("errors").TryGetProperty(field, out _).ShouldBeTrue(field);
        _app.Emails.Sent.ShouldBeEmpty();
        (await _app.WithDbAsync(db => db.PendingSignups.CountAsync())).ShouldBe(0);
    }

    [Fact]
    public async Task The_password_policy_is_the_same_as_everywhere_else()
    {
        await StartAsync();

        var response = await SignupAsync(email: "lovelace@acme.test", password: "lovelace-secret-1");
        var errors = (await BodyAsync(response)).GetProperty("errors").GetProperty("password").EnumerateArray().Select(e => e.GetString()).ToList();

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        errors.ShouldContain("Password must not contain your email name.");
    }

    [Fact]
    public async Task A_filled_honeypot_is_accepted_and_silently_dropped()
    {
        await StartAsync();

        var response = await SignupAsync(website: "https://spam.test");

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await response.Content.ReadAsStringAsync()).ShouldBe("{}");
        _app.Emails.Sent.ShouldBeEmpty();
        (await _app.WithDbAsync(db => db.PendingSignups.CountAsync())).ShouldBe(0);
    }

    [Fact]
    public async Task Sign_ups_per_address_are_limited_per_day_without_telling_the_caller()
    {
        await StartAsync(Fast(("Signup:MaxSignupsPerEmailPerDay", "3")));

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            statuses.Add((await SignupAsync("ada@acme.test")).StatusCode);
        }

        statuses.ShouldAllBe(s => s == HttpStatusCode.Accepted);
        _app.Emails.Sent.Count.ShouldBe(3); // the 4th and 5th were dropped
        (await SignupAsync("other@acme.test")).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        _app.Emails.Sent.Count.ShouldBe(4);
    }

    [Fact]
    public async Task Sign_ups_per_ip_are_limited_per_hour_with_a_429_problem()
    {
        await StartAsync(Fast(("Signup:MaxSignupsPerIpPerHour", "5")));

        for (var i = 0; i < 5; i++)
        {
            (await SignupAsync($"user{i}@acme.test")).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        var limited = await SignupAsync("user6@acme.test");

        limited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await BodyAsync(limited)).GetProperty("code").GetString().ShouldBe("RATE_LIMITED");
        _app.Emails.Sent.Count.ShouldBe(5);
        (await VerifyAsync("user0@acme.test", "nope")).StatusCode.ShouldBe(HttpStatusCode.BadRequest); // other endpoints are not part of that budget
    }

    [Fact]
    public async Task Sign_up_can_be_switched_off()
    {
        await StartAsync(Fast(("Signup:Enabled", "false")));

        var response = await SignupAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await BodyAsync(response)).GetProperty("code").GetString().ShouldBe("SIGNUP_DISABLED");
        _app.Emails.Sent.ShouldBeEmpty();
        (await (await _app.GetAsync("/api/v1/public/config")).Content.ReadFromJsonAsync<PublicConfigDto>(AuthApp.Json))!.SignupEnabled.ShouldBeFalse();
    }

    private sealed class RecordingCaptcha : ICaptchaVerifier
    {
        public List<string?> Tokens { get; } = [];

        public bool Accept { get; set; }

        public Task<bool> VerifyAsync(string? token, string? remoteIp, CancellationToken cancellationToken)
        {
            Tokens.Add(token);
            return Task.FromResult(Accept && token == "good");
        }
    }

    [Fact]
    public async Task A_failed_captcha_stops_the_signup_and_a_good_one_lets_it_through()
    {
        var captcha = new RecordingCaptcha { Accept = true };
        await StartAsync(configure: services => services.AddSingleton<ICaptchaVerifier>(captcha));

        var bad = await SignupAsync(captcha: "bad");
        var missing = await SignupAsync();
        var good = await SignupAsync(captcha: "good");

        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await BodyAsync(bad)).GetProperty("code").GetString().ShouldBe("CAPTCHA_FAILED");
        missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        good.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        captcha.Tokens.ShouldBe(["bad", null, "good"]);
        _app.Emails.Sent.Count.ShouldBe(1);
    }

    [Fact]
    public async Task When_the_captcha_provider_is_unreachable_the_signup_fails_closed()
    {
        // the real verifier with Turnstile on and an address nothing listens on
        await StartAsync(Fast(("Captcha:Provider", "turnstile"), ("Captcha:SiteKey", "s"), ("Captcha:SecretKey", "k"),
            ("Captcha:VerifyUrl", "https://127.0.0.1:1/siteverify"), ("Captcha:TimeoutSeconds", "2")));

        var response = await SignupAsync(captcha: "anything");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await BodyAsync(response)).GetProperty("code").GetString().ShouldBe("CAPTCHA_FAILED");
        _app.Emails.Sent.ShouldBeEmpty();
    }

    [Fact]
    public void Turnstile_without_its_keys_refuses_to_start()
    {
        using var factory = new ApiFactory { Settings = new Dictionary<string, string> { ["Captcha:Provider"] = "turnstile" } };

        Should.Throw<OptionsValidationException>(() => factory.CreateClient()).Message.ShouldContain("Captcha:SiteKey");
    }

    [Theory]
    [InlineData("Portal:PublicBaseUrl", "http://portal.example.com", "Portal:PublicBaseUrl")]
    [InlineData("Signup:TrialCredits", "0", "TrialCredits")]
    [InlineData("Signup:TrialDays", "400", "TrialDays")]
    [InlineData("Captcha:Provider", "recaptcha", "Captcha:Provider")]
    public void Bad_options_refuse_to_start(string key, string value, string expected)
    {
        using var factory = new ApiFactory { Settings = new Dictionary<string, string> { [key] = value } };

        Should.Throw<OptionsValidationException>(() => factory.CreateClient()).Message.ShouldContain(expected);
    }

    [Fact]
    public void The_public_post_endpoints_cap_the_request_body_size_and_rate_limit_by_ip()
    {
        foreach (var name in new[] { "Signup", "Verify", "Contact" })
        {
            var action = typeof(NexaVerify.Api.Controllers.Public.PublicController).GetMethod(name)!;
            action.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute), false).ShouldNotBeEmpty(name);
            action.GetCustomAttributes(typeof(Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute), false).ShouldNotBeEmpty(name);
        }

        typeof(NexaVerify.Api.Controllers.Public.PublicController).IsDefined(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), false).ShouldBeTrue();
    }

    [Fact]
    public async Task Other_endpoints_still_require_authentication()
    {
        await StartAsync();

        (await _app.GetAsync("/api/v1/admin/contact-requests")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await _app.GetAsync("/api/v1/admin/plans")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await _app.GetAsync("/api/v1/client/licenses/summary")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // ---------------------------------------------------------------- contact

    [Fact]
    public async Task A_contact_message_is_stored_notified_and_readable_by_staff()
    {
        await StartAsync(Fast(("Signup:ContactNotifyEmail", "sales@nexa.test")));

        var response = await _app.PostAsync("/api/v1/public/contact", new { name = "Grace Hopper", email = "grace@navy.test", company = "US Navy", message = "I would like a demo.\nThanks!" });

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await response.Content.ReadAsStringAsync()).ShouldBe("{}");
        var mail = _app.Emails.Sent.Single();
        mail.To.ShouldBe("sales@nexa.test");
        mail.Subject.ShouldBe("New contact request from Grace Hopper");
        mail.Body.ShouldContain("grace@navy.test");
        mail.Body.ShouldContain("US Navy");
        mail.Body.ShouldContain("I would like a demo.");

        var admin = await _app.SuperAdminAsync();
        var page = (await (await _app.GetAsync("/api/v1/admin/contact-requests", admin.AccessToken)).Content.ReadFromJsonAsync<PagedResult<ContactRequestDto>>(AuthApp.Json))!;
        page.TotalCount.ShouldBe(1);
        page.Items.Single().ShouldSatisfyAllConditions(
            c => c.Name.ShouldBe("Grace Hopper"),
            c => c.Email.ShouldBe("grace@navy.test"),
            c => c.Company.ShouldBe("US Navy"),
            c => c.Message.ShouldBe("I would like a demo.\nThanks!"));
    }

    [Fact]
    public async Task Without_a_notify_address_the_message_is_only_stored()
    {
        await StartAsync();

        (await _app.PostAsync("/api/v1/public/contact", new { name = "Ada", email = "ada@acme.test", message = "Hi" })).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        _app.Emails.Sent.ShouldBeEmpty();
        (await _app.WithDbAsync(db => db.ContactRequests.CountAsync())).ShouldBe(1);
    }

    [Fact]
    public async Task Contact_input_is_validated_the_honeypot_dropped_and_the_ip_limited()
    {
        await StartAsync(Fast(("Signup:MaxContactsPerIpPerHour", "3"), ("Signup:ContactNotifyEmail", "sales@nexa.test")));

        var invalid = await _app.PostAsync("/api/v1/public/contact", new { name = "", email = "bad", message = "" });
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var errors = (await BodyAsync(invalid)).GetProperty("errors");
        foreach (var field in new[] { "name", "email", "message" })
        {
            errors.TryGetProperty(field, out _).ShouldBeTrue(field);
        }

        var bot = await _app.PostAsync("/api/v1/public/contact", new { name = "Bot", email = "bot@spam.test", message = "Buy now", website = "https://spam.test" });
        bot.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await _app.WithDbAsync(db => db.ContactRequests.CountAsync())).ShouldBe(0);
        _app.Emails.Sent.ShouldBeEmpty();

        for (var i = 0; i < 3; i++)
        {
            (await _app.PostAsync("/api/v1/public/contact", new { name = "Ada", email = "ada@acme.test", message = "Hi " + i })).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        var limited = await _app.PostAsync("/api/v1/public/contact", new { name = "Ada", email = "ada@acme.test", message = "Too many" });
        limited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await _app.WithDbAsync(db => db.ContactRequests.CountAsync())).ShouldBe(3);
    }

    [Fact]
    public async Task Contact_mail_headers_cannot_be_injected_through_the_name()
    {
        await StartAsync(Fast(("Signup:ContactNotifyEmail", "sales@nexa.test")));

        await _app.PostAsync("/api/v1/public/contact", new { name = "Eve\r\nBcc: victim@x.test", email = "eve@x.test", message = "Hi" });

        _app.Emails.Sent.Single().Subject.ShouldNotContain("\n");
        _app.Emails.Sent.Single().Subject.ShouldNotContain("\r");
    }

    [Fact]
    public async Task The_contact_list_needs_the_clients_read_permission()
    {
        await StartAsync();
        var admin = await _app.SuperAdminAsync();
        for (var i = 0; i < 3; i++)
        {
            (await _app.PostAsync("/api/v1/public/contact", new { name = "N" + i, email = "n@x.test", message = "m" + i })).StatusCode.ShouldBe(HttpStatusCode.Accepted);
            await Task.Delay(20);
        }

        (await _app.GetAsync("/api/v1/admin/contact-requests")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var page = (await (await _app.GetAsync("/api/v1/admin/contact-requests?page=1&pageSize=2", admin.AccessToken)).Content.ReadFromJsonAsync<PagedResult<ContactRequestDto>>(AuthApp.Json))!;
        page.Items.Select(c => c.Name).ShouldBe(["N2", "N1"]); // newest first
        page.TotalCount.ShouldBe(3);
    }

    // ---------------------------------------------------------------- retention

    [Fact]
    public async Task Retention_removes_stale_pending_signups_and_old_contact_requests_only()
    {
        await StartAsync();
        (await SignupAsync("live@acme.test")).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await SignupAsync("expired@acme.test")).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await SignupAsync("justexpired@acme.test")).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await OnboardAsync("done@acme.test", "Done Co");
        (await _app.PostAsync("/api/v1/public/contact", new { name = "New", email = "n@x.test", message = "recent" })).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await _app.PostAsync("/api/v1/public/contact", new { name = "Old", email = "o@x.test", message = "ancient" })).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await _app.WithDbAsync(async db =>
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE iam.PendingSignups SET ExpiresAt = DATEADD(day, -2, SYSUTCDATETIME()) WHERE NormalizedEmail = 'EXPIRED@ACME.TEST'");
            await db.Database.ExecuteSqlRawAsync("UPDATE iam.PendingSignups SET ExpiresAt = DATEADD(hour, -2, SYSUTCDATETIME()) WHERE NormalizedEmail = 'JUSTEXPIRED@ACME.TEST'");
            await db.Database.ExecuteSqlRawAsync("UPDATE iam.PendingSignups SET ConsumedAt = DATEADD(day, -8, SYSUTCDATETIME()) WHERE NormalizedEmail = 'DONE@ACME.TEST'");
            await db.Database.ExecuteSqlRawAsync("UPDATE tenancy.ContactRequests SET CreatedAt = DATEADD(day, -181, SYSUTCDATETIME()) WHERE Name = 'Old'");
            return 0;
        });

        var (signups, contacts) = await _app.Factory.Services.GetRequiredService<PublicDataPurger>().PurgeOnceAsync(default);

        signups.ShouldBe(2);
        contacts.ShouldBe(1);
        (await _app.WithDbAsync(db => db.PendingSignups.Select(p => p.NormalizedEmail).OrderBy(e => e).ToListAsync())).ShouldBe(["JUSTEXPIRED@ACME.TEST", "LIVE@ACME.TEST"]);
        (await _app.WithDbAsync(db => db.ContactRequests.Select(c => c.Name).ToListAsync())).ShouldBe(["New"]);
    }

    // ---------------------------------------------------------------- the public endpoints stay out of browsers' reach

    [Fact]
    public async Task Public_endpoints_add_no_cors_allowance()
    {
        await StartAsync();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/public/signup") { Content = JsonContent.Create(SignupBody()) };
        request.Headers.Add("Origin", "https://evil.test");

        var response = await _app.SendAsync(request, null);

        response.Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();
    }
}
