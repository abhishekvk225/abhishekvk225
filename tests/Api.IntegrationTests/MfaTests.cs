using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NexaVerify.Api.IntegrationTests.Support;
using NexaVerify.Application.Identity;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Identity;
using NexaVerify.Domain.Identity;
using NexaVerify.TestSupport;

namespace NexaVerify.Api.IntegrationTests;

/// <summary>TOTP second factor: enrolment, two-step sign-in, replay and brute-force protection, recovery codes and administrator reset.</summary>
[Collection(SqlServerCollection.Name)]
public class MfaTests : IAsyncLifetime
{
    private const string Enroll = "/api/v1/auth/mfa/enroll";
    private const string Confirm = "/api/v1/auth/mfa/enroll/confirm";
    private const string Verify = "/api/v1/auth/mfa/verify";
    private const string Login = "/api/v1/auth/login";
    private const string Password = "Granite-Lantern-Voyage-77";

    private readonly SqlServerFixture _fixture;
    private AuthApp _app = null!;
    private LoginResponse _admin = null!;

    public MfaTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    protected virtual IReadOnlyDictionary<string, string>? AppSettings => null;

    public async Task InitializeAsync()
    {
        _app = await AuthApp.CreateAsync(_fixture, AppSettings);
        _admin = await _app.SuperAdminAsync();
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private static string Code(string base32, long stepOffset = 0) => Totp.Compute(Base32.Decode(base32)!, Totp.StepAt(DateTime.UtcNow) + stepOffset);

    private static string Code(string base32, long step, bool absolute) => Totp.Compute(Base32.Decode(base32)!, step);

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private async Task<T> ReadAsync<T>(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        response.StatusCode.ShouldBe(expected, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>(AuthApp.Json))!;
    }

    /// <summary>A staff member with the given role, signed in with a password only (no MFA yet).</summary>
    private async Task<(string Email, LoginResponse Session, Guid Id)> StaffAsync(string email, string role = SystemRoles.SuperAdmin)
    {
        const string temp = "Temporary-Passphrase-55";
        var created = await ReadAsync<PlatformUserDto>(
            await _app.PostAsync("/api/v1/admin/users", new CreatePlatformUserRequest(email, "Staff " + role, temp, [role]), _admin.AccessToken), HttpStatusCode.Created);
        var first = await _app.LoginAsync(email, temp);
        var changed = await ReadAsync<LoginResponse>(await _app.PostAsync("/api/v1/auth/change-password", new ChangePasswordRequest(temp, Password), first.AccessToken));
        return (email, changed, created.Id);
    }

    /// <summary>Enrols a signed-in user and returns (secret, recovery codes, the session that exists after enabling).</summary>
    private async Task<(string Secret, IReadOnlyList<string> Recovery, LoginResponse Session)> EnrolAsync(LoginResponse session)
    {
        var start = await ReadAsync<MfaEnrolmentDto>(await _app.PostAsync(Enroll, null, session.AccessToken));
        var enabled = await ReadAsync<MfaEnabledDto>(await _app.PostAsync(Confirm, new ConfirmMfaRequest(Code(start.SecretBase32)), session.AccessToken));
        return (start.SecretBase32, enabled.RecoveryCodes, enabled.Session);
    }

    private async Task<LoginResponse> ChallengeAsync(string email, string password = Password)
    {
        var step1 = await ReadAsync<LoginResponse>(await _app.PostAsync(Login, new LoginRequest(email, password)));
        step1.MfaRequired.ShouldBeTrue();
        return step1;
    }

    private Task<HttpResponseMessage> VerifyAsync(string challenge, string code) => _app.PostAsync(Verify, new VerifyMfaRequest(challenge, code));

    private async Task<long> LastUsedStepAsync(string email) =>
        (await _app.WithDbAsync(async db =>
        {
            var userId = (await db.Users.AsNoTracking().SingleAsync(u => u.NormalizedEmail == User.Normalize(email))).Id;
            return (await db.UserMfa.AsNoTracking().SingleAsync(m => m.UserId == userId)).LastUsedStep;
        }))!.Value;

    // ------------------------------------------------------------------ enrolment

    [Fact]
    public async Task Enrolment_shows_the_secret_once_stores_it_encrypted_and_enables_MFA_on_a_valid_code()
    {
        var start = await ReadAsync<MfaEnrolmentDto>(await _app.PostAsync(Enroll, null, _admin.AccessToken));

        start.SecretBase32.Length.ShouldBe(32); // 160 bits
        start.OtpauthUri.ShouldStartWith("otpauth://totp/");
        start.OtpauthUri.ShouldContain("secret=" + start.SecretBase32);
        start.OtpauthUri.ShouldContain("algorithm=SHA1");
        start.OtpauthUri.ShouldContain("digits=6");
        start.OtpauthUri.ShouldContain("period=30");
        start.OtpauthUri.ShouldContain(Uri.EscapeDataString(DatabaseBootstrap.SuperAdminEmail));
        var stored = await _app.WithDbAsync(db => db.UserMfa.AsNoTracking().SingleAsync());
        stored.IsConfirmed.ShouldBeFalse();
        var plain = Base32.Decode(start.SecretBase32)!;
        stored.SecretEnc.ShouldNotBe(plain);
        System.Text.Encoding.UTF8.GetString(stored.SecretEnc).ShouldNotContain(start.SecretBase32);
        stored.SecretEnc.Length.ShouldBe(12 + 16 + 20); // nonce + tag + secret: AES-GCM, not plaintext

        // not enabled yet: sign-in is still single-step, and a wrong code does not enable it
        (await _app.PostAsync(Confirm, new ConfirmMfaRequest("000000"), _admin.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _app.WithDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Id == stored.UserId))).TwoFactorEnabled.ShouldBeFalse();

        var enabled = await ReadAsync<MfaEnabledDto>(await _app.PostAsync(Confirm, new ConfirmMfaRequest(Code(start.SecretBase32)), _admin.AccessToken));

        enabled.RecoveryCodes.Count.ShouldBe(10);
        enabled.RecoveryCodes.ShouldAllBe(c => Regex.IsMatch(c, "^[A-Z2-9]{5}-[A-Z2-9]{5}$"));
        enabled.RecoveryCodes.Distinct().Count().ShouldBe(10);
        (await _app.GetAsync("/api/v1/auth/me", _admin.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized); // earlier sessions end
        (await _app.GetAsync("/api/v1/admin/roles", enabled.Session.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var hashes = await _app.WithDbAsync(db => db.MfaRecoveryCodes.AsNoTracking().Select(c => c.CodeHash).ToListAsync());
        hashes.Count.ShouldBe(10);
        hashes.ShouldAllBe(h => h.Length == 32);
        (await _app.WithDbAsync(db => db.AuditLogs.Where(a => a.Action == "auth.mfa_enabled").CountAsync())).ShouldBe(1);
        var audit = await _app.WithDbAsync(db => db.AuditLogs.AsNoTracking().Where(a => a.Action.StartsWith("auth.mfa")).Select(a => a.NewValuesJson + a.OldValuesJson).ToListAsync());
        audit.ShouldAllBe(v => v == null || !v.Contains(start.SecretBase32));
        var status = await ReadAsync<MfaStatusDto>(await _app.GetAsync("/api/v1/auth/mfa", enabled.Session.AccessToken));
        (status.Enabled, status.RecoveryCodesRemaining).ShouldBe((true, 10));
    }

    [Fact]
    public async Task Enrolling_again_while_enabled_is_refused_and_a_pending_setup_can_be_restarted()
    {
        var first = await ReadAsync<MfaEnrolmentDto>(await _app.PostAsync(Enroll, null, _admin.AccessToken));
        var second = await ReadAsync<MfaEnrolmentDto>(await _app.PostAsync(Enroll, null, _admin.AccessToken));
        second.SecretBase32.ShouldNotBe(first.SecretBase32);
        (await _app.PostAsync(Confirm, new ConfirmMfaRequest(Code(first.SecretBase32)), _admin.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest); // the old secret is gone

        var enabled = await ReadAsync<MfaEnabledDto>(await _app.PostAsync(Confirm, new ConfirmMfaRequest(Code(second.SecretBase32)), _admin.AccessToken));

        var again = await _app.PostAsync(Enroll, null, enabled.Session.AccessToken);
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await JsonOf(again)).GetProperty("code").GetString().ShouldBe("MFA_ALREADY_ENABLED");
    }

    [Fact]
    public async Task A_pending_setup_is_discarded_after_too_many_wrong_confirmation_codes()
    {
        var start = await ReadAsync<MfaEnrolmentDto>(await _app.PostAsync(Enroll, null, _admin.AccessToken));
        for (var i = 0; i < 5; i++)
        {
            (await _app.PostAsync(Confirm, new ConfirmMfaRequest("111111"), _admin.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        (await _app.PostAsync(Confirm, new ConfirmMfaRequest(Code(start.SecretBase32)), _admin.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await _app.WithDbAsync(db => db.UserMfa.CountAsync())).ShouldBe(0);
    }

    [Fact]
    public async Task The_enrolment_endpoints_need_a_signed_in_user_and_a_well_formed_code()
    {
        (await _app.PostAsync(Enroll, null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await _app.PostAsync(Confirm, new ConfirmMfaRequest("123456"))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await _app.GetAsync("/api/v1/auth/mfa")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await _app.PostAsync(Confirm, new ConfirmMfaRequest(""), _admin.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _app.PostAsync(Verify, new VerifyMfaRequest("", ""))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _app.PostAsync("/api/v1/auth/mfa/recovery-codes", new RegenerateRecoveryCodesRequest("12"), _admin.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // ------------------------------------------------------------------ two-step sign-in

    [Fact]
    public async Task Sign_in_becomes_two_step_and_the_challenge_alone_grants_nothing()
    {
        var (secret, _, _) = await EnrolAsync(_admin);

        var step1 = await ChallengeAsync(DatabaseBootstrap.SuperAdminEmail);

        step1.AccessToken.ShouldBeEmpty();
        step1.RefreshToken.ShouldBeEmpty();
        step1.MfaChallengeToken.ShouldNotBeNullOrEmpty();
        step1.MfaChallengeExpiresIn.ShouldBe(300);
        (await _app.GetAsync("/api/v1/admin/roles", step1.MfaChallengeToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await _app.WithDbAsync(db => db.MfaChallenges.AsNoTracking().ToListAsync())).ShouldHaveSingleItem().TokenHash.Length.ShouldBe(32); // only a hash is stored
        // a wrong password still gives the generic failure, before any challenge exists
        (await _app.PostAsync(Login, new LoginRequest(DatabaseBootstrap.SuperAdminEmail, "Wrong-Passphrase-1"))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var step = await LastUsedStepAsync(DatabaseBootstrap.SuperAdminEmail);
        var done = await ReadAsync<LoginResponse>(await VerifyAsync(step1.MfaChallengeToken!, Code(secret, step + 1, absolute: true)));

        done.AccessToken.ShouldNotBeEmpty();
        done.RefreshToken.ShouldNotBeEmpty();
        done.MfaRequired.ShouldBeFalse();
        (await _app.GetAsync("/api/v1/admin/roles", done.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _app.WithDbAsync(db => db.LoginHistory.CountAsync(h => h.Outcome == LoginOutcome.Success))).ShouldBeGreaterThan(0);
        (await _app.WithDbAsync(db => db.AuditLogs.CountAsync(a => a.Action == "auth.mfa_verified"))).ShouldBe(1);
    }

    [Fact]
    public async Task A_code_that_was_already_used_cannot_be_replayed_but_the_next_step_works()
    {
        var (secret, _, _) = await EnrolAsync(_admin);
        var used = await LastUsedStepAsync(DatabaseBootstrap.SuperAdminEmail);

        var replayed = await VerifyAsync((await ChallengeAsync(DatabaseBootstrap.SuperAdminEmail)).MfaChallengeToken!, Code(secret, used, absolute: true));
        replayed.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await JsonOf(replayed)).GetProperty("code").GetString().ShouldBe(ErrorCodes.MfaCodeInvalid);

        var older = await VerifyAsync((await ChallengeAsync(DatabaseBootstrap.SuperAdminEmail)).MfaChallengeToken!, Code(secret, used - 1, absolute: true));
        older.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var fresh = await VerifyAsync((await ChallengeAsync(DatabaseBootstrap.SuperAdminEmail)).MfaChallengeToken!, Code(secret, used + 1, absolute: true));
        fresh.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await LastUsedStepAsync(DatabaseBootstrap.SuperAdminEmail)).ShouldBe(used + 1);

        // and that newly used code is spent as well
        (await VerifyAsync((await ChallengeAsync(DatabaseBootstrap.SuperAdminEmail)).MfaChallengeToken!, Code(secret, used + 1, absolute: true)))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_challenge_is_single_use_and_expires()
    {
        var (secret, _, _) = await EnrolAsync(_admin);
        var used = await LastUsedStepAsync(DatabaseBootstrap.SuperAdminEmail);
        var ticket = (await ChallengeAsync(DatabaseBootstrap.SuperAdminEmail)).MfaChallengeToken!;
        (await VerifyAsync(ticket, Code(secret, used + 1, absolute: true))).StatusCode.ShouldBe(HttpStatusCode.OK);

        var reuse = await VerifyAsync(ticket, Code(secret, used + 1, absolute: true));
        reuse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await JsonOf(reuse)).GetProperty("code").GetString().ShouldBe(ErrorCodes.MfaChallengeInvalid);

        var expiring = (await ChallengeAsync(DatabaseBootstrap.SuperAdminEmail)).MfaChallengeToken!;
        await _app.WithDbAsync(async db =>
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE iam.MfaChallenges SET ExpiresAt = DATEADD(minute, -1, SYSUTCDATETIME()) WHERE ConsumedAt IS NULL");
            return true;
        });
        var late = await VerifyAsync(expiring, Code(secret, used + 2, absolute: true));
        late.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await JsonOf(late)).GetProperty("code").GetString().ShouldBe(ErrorCodes.MfaChallengeInvalid);
        (await VerifyAsync("not-a-real-challenge", "123456")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Five_wrong_codes_kill_the_challenge_even_for_the_right_code_and_are_recorded()
    {
        var (secret, _, _) = await EnrolAsync(_admin);
        var used = await LastUsedStepAsync(DatabaseBootstrap.SuperAdminEmail);
        var ticket = (await ChallengeAsync(DatabaseBootstrap.SuperAdminEmail)).MfaChallengeToken!;

        for (var i = 0; i < 5; i++)
        {
            (await VerifyAsync(ticket, "000000")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        var late = await VerifyAsync(ticket, Code(secret, used + 1, absolute: true));
        late.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await JsonOf(late)).GetProperty("code").GetString().ShouldBe(ErrorCodes.MfaChallengeInvalid);
        var challenge = await _app.WithDbAsync(db => db.MfaChallenges.AsNoTracking().SingleAsync());
        challenge.Attempts.ShouldBe(5);
        challenge.ConsumedAt.ShouldBeNull();
        var history = await _app.WithDbAsync(db => db.LoginHistory.AsNoTracking().Select(h => h.Outcome).ToListAsync());
        history.Count(o => o == LoginOutcome.MfaFailed).ShouldBeGreaterThanOrEqualTo(3);
        // the failed second factors count against the account: it is now locked for password sign-in too
        (await _app.PostAsync(Login, new LoginRequest(DatabaseBootstrap.SuperAdminEmail, Password))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await _app.WithDbAsync(db => db.AuditLogs.CountAsync(a => a.Action == "auth.mfa_challenge_exhausted"))).ShouldBe(1);
    }

    [Fact]
    public async Task Parallel_guesses_on_one_challenge_cannot_exceed_the_attempt_limit()
    {
        await EnrolAsync(_admin);
        var ticket = (await ChallengeAsync(DatabaseBootstrap.SuperAdminEmail)).MfaChallengeToken!;

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => VerifyAsync(ticket, (100000 + i).ToString())));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.Unauthorized);
        (await _app.WithDbAsync(db => db.MfaChallenges.AsNoTracking().SingleAsync())).Attempts.ShouldBeLessThanOrEqualTo(5);
    }

    [Fact]
    public async Task A_recovery_code_signs_in_once_and_is_consumed()
    {
        var (_, recovery, _) = await EnrolAsync(_admin);

        var ok = await VerifyAsync((await ChallengeAsync(DatabaseBootstrap.SuperAdminEmail)).MfaChallengeToken!, recovery[0].ToLowerInvariant().Replace("-", string.Empty));
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        var session = (await ok.Content.ReadFromJsonAsync<LoginResponse>(AuthApp.Json))!;

        var spent = await VerifyAsync((await ChallengeAsync(DatabaseBootstrap.SuperAdminEmail)).MfaChallengeToken!, recovery[0]);
        spent.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await VerifyAsync((await ChallengeAsync(DatabaseBootstrap.SuperAdminEmail)).MfaChallengeToken!, recovery[1])).StatusCode.ShouldBe(HttpStatusCode.OK);
        var status = await ReadAsync<MfaStatusDto>(await _app.GetAsync("/api/v1/auth/mfa", session.AccessToken));
        status.RecoveryCodesRemaining.ShouldBe(8);
        (await _app.WithDbAsync(db => db.AuditLogs.CountAsync(a => a.Action == "auth.mfa_recovery_code_used"))).ShouldBe(2);
    }

    [Fact]
    public async Task Regenerating_recovery_codes_needs_a_valid_code_and_retires_the_old_ones()
    {
        var (secret, recovery, session) = await EnrolAsync(_admin);
        var used = await LastUsedStepAsync(DatabaseBootstrap.SuperAdminEmail);
        const string Url = "/api/v1/auth/mfa/recovery-codes";

        (await _app.PostAsync(Url, new RegenerateRecoveryCodesRequest("000000"), session.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _app.PostAsync(Url, new RegenerateRecoveryCodesRequest(Code(secret, used, absolute: true)), session.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest); // replay
        var fresh = await ReadAsync<RecoveryCodesDto>(await _app.PostAsync(Url, new RegenerateRecoveryCodesRequest(Code(secret, used + 1, absolute: true)), session.AccessToken));

        fresh.RecoveryCodes.Count.ShouldBe(10);
        fresh.RecoveryCodes.Intersect(recovery).ShouldBeEmpty();
        (await VerifyAsync((await ChallengeAsync(DatabaseBootstrap.SuperAdminEmail)).MfaChallengeToken!, recovery[0])).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await VerifyAsync((await ChallengeAsync(DatabaseBootstrap.SuperAdminEmail)).MfaChallengeToken!, fresh.RecoveryCodes[0])).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _app.WithDbAsync(db => db.MfaRecoveryCodes.CountAsync())).ShouldBe(10);
    }

    // ------------------------------------------------------------------ reset by another administrator

    [Fact]
    public async Task Only_another_Super_Admin_can_reset_MFA_and_it_needs_a_reason_and_is_audited()
    {
        var (otherEmail, otherSession, otherId) = await StaffAsync("second.admin@nexaverify.test");
        var (secret, _, enabledSession) = await EnrolAsync(otherSession);
        var adminId = (await ReadAsync<MeResponse>(await _app.GetAsync("/api/v1/auth/me", _admin.AccessToken))).User.Id;
        var resetUrl = $"/api/v1/admin/users/{otherId}/mfa/reset";

        // a staff role without the reset permission
        (await _app.PostAsync("/api/v1/admin/roles", new CreateRoleRequest("Auditor", "Platform", null, [Permissions.Clients.Read]), _admin.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Created);
        var (_, auditorSession, _) = await StaffAsync("auditor.mfa@nexaverify.test", "Auditor");

        (await _app.PostAsync(resetUrl, new ResetMfaRequest("lost phone"))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await _app.PostAsync(resetUrl, new ResetMfaRequest("lost phone"), auditorSession.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _app.PostAsync($"/api/v1/admin/users/{adminId}/mfa/reset", new ResetMfaRequest("because"), _admin.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden); // yourself
        (await _app.PostAsync(resetUrl, new ResetMfaRequest(""), _admin.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _app.PostAsync($"/api/v1/admin/users/{Guid.NewGuid()}/mfa/reset", new ResetMfaRequest("x"), _admin.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _app.PostAsync($"/api/v1/admin/users/{adminId}/mfa/reset", new ResetMfaRequest("x"), enabledSession.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Conflict); // not enabled for that user

        (await _app.PostAsync(resetUrl, new ResetMfaRequest("Lost phone, identity verified by phone call"), _admin.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await _app.GetAsync("/api/v1/auth/me", enabledSession.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized); // their sessions end
        var plain = await _app.LoginAsync(otherEmail, Password);
        plain.MfaRequired.ShouldBeFalse();
        plain.AccessToken.ShouldNotBeEmpty();
        (await _app.WithDbAsync(db => db.UserMfa.CountAsync(m => m.UserId == otherId))).ShouldBe(0);
        (await _app.WithDbAsync(db => db.MfaRecoveryCodes.CountAsync(c => c.UserId == otherId))).ShouldBe(0);
        var entry = await _app.WithDbAsync(db => db.AuditLogs.AsNoTracking().SingleAsync(a => a.Action == "auth.mfa_reset"));
        entry.ActorId.ShouldBe(adminId);
        entry.NewValuesJson!.ShouldContain("Lost phone, identity verified by phone call");
        entry.NewValuesJson!.ShouldNotContain(secret);
        (await _app.PostAsync(resetUrl, new ResetMfaRequest("again"), _admin.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_client_user_s_MFA_is_reset_through_its_own_client_only()
    {
        var (client, clientAdmin) = await _app.OnboardClientAsync(_admin.AccessToken, "MFA1", "a@mfa1.test");
        var (other, _) = await _app.OnboardClientAsync(_admin.AccessToken, "MFA2", "a@mfa2.test");
        var adminUserId = (await ReadAsync<MeResponse>(await _app.GetAsync("/api/v1/auth/me", clientAdmin.AccessToken))).User.Id;
        var (_, _, enabledSession) = await EnrolAsync(clientAdmin); // client users can enrol too

        // a client admin cannot reset (platform permission), the wrong client id is a 404, the right one works
        (await _app.PostAsync($"/api/v1/admin/clients/{client.Id}/users/{adminUserId}/mfa/reset", new ResetMfaRequest("x"), enabledSession.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _app.PostAsync($"/api/v1/admin/clients/{other.Id}/users/{adminUserId}/mfa/reset", new ResetMfaRequest("x"), _admin.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _app.PostAsync($"/api/v1/admin/users/{adminUserId}/mfa/reset", new ResetMfaRequest("x"), _admin.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound); // not a staff account
        (await _app.PostAsync($"/api/v1/admin/clients/{client.Id}/users/{adminUserId}/mfa/reset", new ResetMfaRequest("Lost phone"), _admin.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await _app.LoginAsync("a@mfa1.test", AuthApp.StrongPassword)).MfaRequired.ShouldBeFalse();
        (await _app.WithDbAsync(db => db.AuditLogs.AsNoTracking().SingleAsync(a => a.Action == "auth.mfa_reset"))).ClientId.ShouldBe(client.Id);
    }

    [Fact]
    public async Task Client_users_can_enrol_and_sign_in_with_a_second_factor_too()
    {
        var (_, clientAdmin) = await _app.OnboardClientAsync(_admin.AccessToken, "MFA3", "a@mfa3.test");
        var (secret, _, _) = await EnrolAsync(clientAdmin);
        var used = await LastUsedStepAsync("a@mfa3.test");

        var step1 = await ChallengeAsync("a@mfa3.test", AuthApp.StrongPassword);
        var done = await ReadAsync<LoginResponse>(await VerifyAsync(step1.MfaChallengeToken!, Code(secret, used + 1, absolute: true)));

        done.User.IsPlatformUser.ShouldBeFalse();
        (await _app.GetAsync("/api/v1/client/profile", done.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ------------------------------------------------------------------ requirement (client setting)

    [Fact]
    public async Task A_client_that_requires_MFA_gets_tokens_that_only_open_the_enrolment_endpoints()
    {
        var (_, admin) = await _app.OnboardClientAsync(_admin.AccessToken, "MFA4", "a@mfa4.test");
        var set = await _app.PutAsync("/api/v1/client/settings/security", new UpdateSettingsRequestBody(new Dictionary<string, bool> { ["security.requireMfa"] = true }), admin.AccessToken);
        set.StatusCode.ShouldBe(HttpStatusCode.OK, await set.Content.ReadAsStringAsync());

        var session = await _app.LoginAsync("a@mfa4.test", AuthApp.StrongPassword);

        session.MfaEnrolmentRequired.ShouldBeTrue();
        new JwtSecurityTokenHandler().ReadJwtToken(session.AccessToken).Claims.ShouldContain(c => c.Type == NexaClaims.MfaEnrolmentRequired && c.Value == "true");
        (await _app.GetAsync("/api/v1/client/profile", session.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _app.GetAsync("/api/v1/client/users", session.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var me = await ReadAsync<MeResponse>(await _app.GetAsync("/api/v1/auth/me", session.AccessToken));
        (me.MfaEnabled, me.MfaEnrolmentRequired).ShouldBe((false, true));

        var (_, _, enabled) = await EnrolAsync(session);

        enabled.MfaEnrolmentRequired.ShouldBeFalse();
        (await _app.GetAsync("/api/v1/client/profile", enabled.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private sealed record UpdateSettingsRequestBody(Dictionary<string, bool> Values);
}

/// <summary>The platform default: Super Admins must enrol before they can do anything else (the same pattern as a forced password change).</summary>
[Collection(SqlServerCollection.Name)]
public class MfaEnforcementTests : IAsyncLifetime
{
    private readonly SqlServerFixture _fixture;
    private AuthApp _app = null!;

    public MfaEnforcementTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync() =>
        _app = await AuthApp.CreateAsync(_fixture, new Dictionary<string, string> { ["Mfa:RequiredPlatformRoles"] = "SuperAdmin" });

    public async Task DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task A_Super_Admin_without_MFA_gets_a_token_that_only_allows_enrolment()
    {
        var first = await _app.LoginAsync(DatabaseBootstrap.SuperAdminEmail, DatabaseBootstrap.SuperAdminPassword);
        var changed = await _app.PostAsync("/api/v1/auth/change-password", new ChangePasswordRequest(DatabaseBootstrap.SuperAdminPassword, AuthApp.StrongPassword), first.AccessToken);
        var session = (await changed.Content.ReadFromJsonAsync<LoginResponse>(AuthApp.Json))!;

        session.MfaEnrolmentRequired.ShouldBeTrue();
        session.MustChangePassword.ShouldBeFalse();
        var claims = new JwtSecurityTokenHandler().ReadJwtToken(session.AccessToken).Claims;
        claims.ShouldContain(c => c.Type == NexaClaims.MfaEnrolmentRequired && c.Value == "true");
        (await _app.GetAsync("/api/v1/admin/roles", session.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _app.GetAsync("/api/v1/admin/clients", session.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _app.PostAsync("/api/v1/admin/licensing/verify-ledger", null, session.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _app.GetAsync("/api/v1/auth/me", session.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var status = (await (await _app.GetAsync("/api/v1/auth/mfa", session.AccessToken)).Content.ReadFromJsonAsync<MfaStatusDto>(AuthApp.Json))!;
        (status.EnrolmentRequired, status.Enabled).ShouldBe((true, false));

        var start = (await (await _app.PostAsync("/api/v1/auth/mfa/enroll", null, session.AccessToken)).Content.ReadFromJsonAsync<MfaEnrolmentDto>(AuthApp.Json))!;
        var done = await _app.PostAsync("/api/v1/auth/mfa/enroll/confirm",
            new ConfirmMfaRequest(Totp.Compute(Base32.Decode(start.SecretBase32)!, Totp.StepAt(DateTime.UtcNow))), session.AccessToken);
        var enabled = (await done.Content.ReadFromJsonAsync<MfaEnabledDto>(AuthApp.Json))!;

        enabled.Session.MfaEnrolmentRequired.ShouldBeFalse();
        (await _app.GetAsync("/api/v1/admin/roles", enabled.Session.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Token_refresh_keeps_the_requirement_until_the_user_has_enrolled()
    {
        var first = await _app.LoginAsync(DatabaseBootstrap.SuperAdminEmail, DatabaseBootstrap.SuperAdminPassword);
        var changed = await _app.PostAsync("/api/v1/auth/change-password", new ChangePasswordRequest(DatabaseBootstrap.SuperAdminPassword, AuthApp.StrongPassword), first.AccessToken);
        var session = (await changed.Content.ReadFromJsonAsync<LoginResponse>(AuthApp.Json))!;

        var refreshed = (await (await _app.PostAsync("/api/v1/auth/refresh", new RefreshRequest(session.RefreshToken))).Content.ReadFromJsonAsync<LoginResponse>(AuthApp.Json))!;

        refreshed.MfaEnrolmentRequired.ShouldBeTrue();
        (await _app.GetAsync("/api/v1/admin/roles", refreshed.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
