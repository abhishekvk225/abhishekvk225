using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexaVerify.Api.IntegrationTests.Support;
using NexaVerify.Application.Faces;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Faces;
using NexaVerify.Contracts.Identity;
using NexaVerify.Domain.Faces;
using NexaVerify.Domain.Licensing;
using NexaVerify.Infrastructure.Background;
using NexaVerify.TestSupport;

namespace NexaVerify.Api.IntegrationTests;

/// <summary>Face recognition end to end against real SQL Server: enrol / verify / identify, billing, isolation, erasure, hardening.</summary>
[Collection(SqlServerCollection.Name)]
public class FacesTests : IAsyncLifetime
{
    private readonly SqlServerFixture _fixture;
    private AuthApp _app = null!;
    private LoginResponse _platform = null!;

    public FacesTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _app = await AuthApp.CreateAsync(_fixture);
        _platform = await _app.SuperAdminAsync();
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private sealed record Tenant(Guid ClientId, string Token, Guid LicenseId);

    private async Task<Tenant> NewTenantAsync(string code, int credits = 100)
    {
        var (client, admin) = await _app.OnboardClientAsync(_platform.AccessToken, code, $"a@{code.ToLowerInvariant()}.test");
        var license = await _app.SeedLicenseAsync(client.Id, credits);
        return new Tenant(client.Id, admin.AccessToken, license);
    }

    private Task<HttpResponseMessage> EnrollAsync(Tenant t, string externalRef, byte[] image, string? key = null, string consent = "consent-001") =>
        _app.PostFormAsync("/api/v1/faces/enroll", image,
            new Dictionary<string, string> { ["externalRef"] = externalRef, ["displayName"] = "Person " + externalRef, ["consentReference"] = consent }, t.Token, key);

    private Task<HttpResponseMessage> VerifyAsync(Tenant t, string externalRef, byte[] image, string? key = null) =>
        _app.PostFormAsync("/api/v1/faces/verify", image, new Dictionary<string, string> { ["externalRef"] = externalRef }, t.Token, key);

    private Task<HttpResponseMessage> IdentifyAsync(Tenant t, byte[] image, string? key = null) =>
        _app.PostFormAsync("/api/v1/faces/identify", image, null, t.Token, key);

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>(AuthApp.Json);

    private static async Task<string?> CodeOf(HttpResponseMessage response) => (await JsonOf(response)).GetProperty("code").GetString();

    private Task<int> ConsumedAsync(Tenant t) =>
        _app.WithDbAsync(async db => (await db.Licenses.AsNoTracking().SingleAsync(l => l.Id == t.LicenseId)).ConsumedCredits);

    [Fact]
    public async Task A_person_can_be_enrolled_then_verified_and_a_stranger_is_rejected()
    {
        var t = await NewTenantAsync("F1");

        var enrolled = await EnrollAsync(t, "emp-1", TestImages.Person(1));
        enrolled.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await enrolled.Content.ReadFromJsonAsync<EnrollFaceResponse>(AuthApp.Json);
        body!.Outcome.ShouldBe("Enrolled");
        body.ProfileCreated.ShouldBeTrue();
        body.Credits.Charged.ShouldBe(1);

        var same = await (await VerifyAsync(t, "emp-1", TestImages.Person(1, variation: 3))).Content.ReadFromJsonAsync<VerifyFaceResponse>(AuthApp.Json);
        same!.Match.ShouldBeTrue();
        same.Score.ShouldBeGreaterThan(0.9m);
        same.Outcome.ShouldBe("Matched");

        var stranger = await (await VerifyAsync(t, "emp-1", TestImages.Person(2))).Content.ReadFromJsonAsync<VerifyFaceResponse>(AuthApp.Json);
        stranger!.Match.ShouldBeFalse();
        stranger.Score.ShouldBe(0m); // a near-miss score is never revealed (no hill-climbing oracle)
        stranger.Outcome.ShouldBe("NoMatch");

        // 1 (enrol) + 1 + 1 (a "no match" is a definitive, billable answer)
        (await ConsumedAsync(t)).ShouldBe(3);
    }

    [Fact]
    public async Task Identify_finds_the_right_person_among_many_and_reports_nobody_for_a_stranger()
    {
        var t = await NewTenantAsync("F2");
        foreach (var n in new[] { 10, 11, 12 })
        {
            (await EnrollAsync(t, "emp-" + n, TestImages.Person(n))).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var found = await (await IdentifyAsync(t, TestImages.Person(11, variation: 2))).Content.ReadFromJsonAsync<IdentifyFaceResponse>(AuthApp.Json);
        found!.Outcome.ShouldBe("Matched");
        found.Matches.Single().ExternalRef.ShouldBe("emp-11");
        found.Credits.Charged.ShouldBe(2);

        var none = await (await IdentifyAsync(t, TestImages.Person(99))).Content.ReadFromJsonAsync<IdentifyFaceResponse>(AuthApp.Json);
        none!.Outcome.ShouldBe("NoMatch");
        none.Matches.ShouldBeEmpty();
    }

    [Fact]
    public async Task Images_are_recognised_by_content_not_by_declared_type()
    {
        var t = await NewTenantAsync("F3");
        (await EnrollAsync(t, "emp-1", TestImages.PersonPng(3))).StatusCode.ShouldBe(HttpStatusCode.OK);

        var notAnImage = await _app.PostFormAsync("/api/v1/faces/detect", "<html>not an image</html>"u8.ToArray(), null, t.Token, imageType: "image/jpeg");
        notAnImage.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeOf(notAnImage)).ShouldBe(ErrorCodes.ImageUnsupportedType);

        var corrupt = await _app.PostFormAsync("/api/v1/faces/detect", [0xFF, 0xD8, 0xFF, 0x00, 1, 2, 3, 4, 5], null, t.Token);
        corrupt.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeOf(corrupt)).ShouldBe(ErrorCodes.ImageInvalid);

        var huge = new byte[(5 * 1024 * 1024) + 1];
        huge[0] = 0xFF;
        huge[1] = 0xD8;
        huge[2] = 0xFF;
        var tooLarge = await _app.PostFormAsync("/api/v1/faces/detect", huge, null, t.Token);
        tooLarge.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);

        var missing = await _app.PostFormAsync("/api/v1/faces/detect", null, null, t.Token);
        missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Unusable_images_are_reported_with_stable_codes_and_failed_enrolments_are_free()
    {
        var t = await NewTenantAsync("F4");

        var blank = await EnrollAsync(t, "x", TestImages.Blank());
        blank.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOf(blank)).ShouldBe(ErrorCodes.NoFaceDetected);

        var crowd = await EnrollAsync(t, "x", TestImages.TwoPeople());
        (await CodeOf(crowd)).ShouldBe(ErrorCodes.MultipleFaces);

        var dull = await EnrollAsync(t, "x", TestImages.Dull());
        (await CodeOf(dull)).ShouldBe(ErrorCodes.LowQualityImage);

        (await ConsumedAsync(t)).ShouldBe(0); // Enroll is charged only on success

        // ...but the attempts are on record, and nobody was registered
        var history = await JsonOf(await _app.GetAsync("/api/v1/faces/requests?outcome=NoFaceDetected", t.Token));
        history.GetProperty("totalCount").GetInt32().ShouldBe(1);
        (await JsonOf(await _app.GetAsync("/api/v1/faces/profiles", t.Token))).GetProperty("totalCount").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task Consent_is_required_and_duplicate_images_and_disabled_profiles_are_refused()
    {
        var t = await NewTenantAsync("F5");

        (await EnrollAsync(t, "emp-1", TestImages.Person(4), consent: "")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await EnrollAsync(t, "emp-1", TestImages.Person(4))).StatusCode.ShouldBe(HttpStatusCode.OK);

        var duplicate = await EnrollAsync(t, "emp-2", TestImages.Person(4));
        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOf(duplicate)).ShouldBe("DUPLICATE_TEMPLATE");

        var profile = (await JsonOf(await _app.GetAsync("/api/v1/faces/profiles", t.Token))).GetProperty("items")[0].GetProperty("id").GetGuid();
        (await _app.PutAsync($"/api/v1/faces/profiles/{profile}", new UpdateFaceProfileRequest(null, null, "Disabled", null), t.Token)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var disabled = await VerifyAsync(t, "emp-1", TestImages.Person(4, 1));
        disabled.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOf(disabled)).ShouldBe("PROFILE_DISABLED");
    }

    [Fact]
    public async Task Adding_a_face_to_an_existing_person_needs_the_consent_on_record()
    {
        var t = await NewTenantAsync("F21");
        (await EnrollAsync(t, "emp-1", TestImages.Person(300), consent: "consent-A")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var takeover = await EnrollAsync(t, "emp-1", TestImages.Person(301), consent: "something-else");
        takeover.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOf(takeover)).ShouldBe("CONSENT_MISMATCH");
        (await EnrollAsync(t, "emp-1", TestImages.Person(301), consent: "consent-A")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ConsumedAsync(t)).ShouldBe(2);
    }

    [Fact]
    public async Task Audit_entries_for_faces_never_carry_the_persons_reference()
    {
        var t = await NewTenantAsync("F22");
        await EnrollAsync(t, "secret-employee-id", TestImages.Person(310));
        var profile = (await JsonOf(await _app.GetAsync("/api/v1/faces/profiles", t.Token))).GetProperty("items")[0].GetProperty("id").GetGuid();
        await _app.DeleteAsync($"/api/v1/faces/profiles/{profile}", t.Token);

        var json = await _app.WithDbAsync(async db =>
            string.Join("|", await db.AuditLogs.AsNoTracking().Where(a => a.ClientId == t.ClientId && a.Action.StartsWith("face.")).Select(a => a.OldValuesJson + a.NewValuesJson).ToListAsync()));
        json.ShouldNotContain("secret-employee-id");
    }

    [Fact]
    public async Task A_person_keeps_at_most_five_templates()
    {
        var t = await NewTenantAsync("F6");
        for (var i = 0; i < 7; i++)
        {
            (await EnrollAsync(t, "emp-1", TestImages.Person(20 + i))).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var profile = (await JsonOf(await _app.GetAsync("/api/v1/faces/profiles", t.Token))).GetProperty("items")[0].GetProperty("id").GetGuid();
        var detail = await JsonOf(await _app.GetAsync($"/api/v1/faces/profiles/{profile}", t.Token));
        detail.GetProperty("templateCount").GetInt32().ShouldBe(5);
    }

    [Fact]
    public async Task Biometric_data_never_appears_in_responses()
    {
        var t = await NewTenantAsync("F7");
        await EnrollAsync(t, "emp-1", TestImages.Person(5));
        var profile = (await JsonOf(await _app.GetAsync("/api/v1/faces/profiles", t.Token))).GetProperty("items")[0].GetProperty("id").GetGuid();

        var raw = await (await _app.GetAsync($"/api/v1/faces/profiles/{profile}", t.Token)).Content.ReadAsStringAsync();
        raw.ShouldNotContain("embedding", Case.Insensitive);
        raw.ShouldContain("Person emp-1"); // the display name round-trips through client-key encryption

        // ...and at rest the name and embedding are ciphertext
        var stored = await _app.WithTenantDbAsync(t.ClientId, async db => (await db.FaceProfiles.AsNoTracking().SingleAsync()).DisplayNameEnc!);
        System.Text.Encoding.UTF8.GetString(stored).ShouldNotContain("Person");
    }

    [Fact]
    public async Task Tenants_cannot_see_or_match_each_others_faces_not_even_the_platform()
    {
        var a = await NewTenantAsync("F8A");
        var b = await NewTenantAsync("F8B");
        await EnrollAsync(a, "emp-1", TestImages.Person(6));

        // B cannot verify against A's person, nor identify them
        (await VerifyAsync(b, "emp-1", TestImages.Person(6))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var identify = await (await IdentifyAsync(b, TestImages.Person(6))).Content.ReadFromJsonAsync<IdentifyFaceResponse>(AuthApp.Json);
        identify!.Matches.ShouldBeEmpty();
        (await JsonOf(await _app.GetAsync("/api/v1/faces/profiles", b.Token))).GetProperty("totalCount").GetInt32().ShouldBe(0);

        // B may use the same external reference for a different person
        (await EnrollAsync(b, "emp-1", TestImages.Person(7))).StatusCode.ShouldBe(HttpStatusCode.OK);

        // the platform: no permission on the API, and no rows even with direct database access in platform scope
        (await _app.GetAsync("/api/v1/faces/profiles", _platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _app.WithDbAsync(db => db.FaceProfiles.CountAsync())).ShouldBe(0);
        (await _app.WithDbAsync(db => db.FaceTemplates.CountAsync())).ShouldBe(0);
        (await _app.WithDbAsync(db => db.RecognitionRequests.CountAsync())).ShouldBe(0);
    }

    [Fact]
    public async Task Without_a_usable_license_nothing_is_processed_or_stored()
    {
        var (client, admin) = await _app.OnboardClientAsync(_platform.AccessToken, "F9", "a@f9.test");
        var t = new Tenant(client.Id, admin.AccessToken, Guid.Empty);

        var none = await EnrollAsync(t, "emp-1", TestImages.Person(8));
        none.StatusCode.ShouldBe(HttpStatusCode.PaymentRequired);
        (await CodeOf(none)).ShouldBe(ErrorCodes.LicenseNotFound);

        // the gate runs before the image is even parsed: garbage gets the licence error, not an image error
        (await CodeOf(await _app.PostFormAsync("/api/v1/faces/identify", [1, 2, 3], null, t.Token))).ShouldBe(ErrorCodes.LicenseNotFound);

        await _app.SeedLicenseAsync(client.Id, 1);
        (await EnrollAsync(t, "emp-1", TestImages.Person(8))).StatusCode.ShouldBe(HttpStatusCode.OK);
        var empty = await VerifyAsync(t, "emp-1", TestImages.Person(8, 1));
        empty.StatusCode.ShouldBe(HttpStatusCode.PaymentRequired);
        (await CodeOf(empty)).ShouldBe(ErrorCodes.LicenseInsufficientBalance);
    }

    [Fact]
    public async Task A_result_is_withheld_when_a_parallel_request_took_the_last_credit()
    {
        var t = await NewTenantAsync("F10", credits: 2);
        (await EnrollAsync(t, "emp-1", TestImages.Person(30))).StatusCode.ShouldBe(HttpStatusCode.OK); // 1 credit left

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() => VerifyAsync(t, "emp-1", TestImages.Person(30, variation: i + 1)))));

        responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        responses.Where(r => r.StatusCode != HttpStatusCode.OK).ShouldAllBe(r => r.StatusCode == HttpStatusCode.PaymentRequired);
        (await ConsumedAsync(t)).ShouldBe(2);

        // only the paid request left a record: no free answers were delivered
        var verifies = await _app.WithTenantDbAsync(t.ClientId, db => db.RecognitionRequests.CountAsync(r => r.Operation == MeteredOperation.Verify));
        verifies.ShouldBe(1);
    }

    [Fact]
    public async Task An_idempotency_key_replays_the_same_request_but_cannot_be_reused_for_other_work()
    {
        var t = await NewTenantAsync("F11");
        await EnrollAsync(t, "emp-1", TestImages.Person(40));
        var before = await ConsumedAsync(t);

        var first = await (await VerifyAsync(t, "emp-1", TestImages.Person(40, 1), key: "abc")).Content.ReadFromJsonAsync<VerifyFaceResponse>(AuthApp.Json);
        var replay = await (await VerifyAsync(t, "emp-1", TestImages.Person(40, 1), key: "abc")).Content.ReadFromJsonAsync<VerifyFaceResponse>(AuthApp.Json);
        replay!.RequestId.ShouldBe(first!.RequestId);
        (await ConsumedAsync(t)).ShouldBe(before + 1); // charged once

        // the same key on a DIFFERENT image is new work and is billed (a key is not a free pass)
        var other = await (await VerifyAsync(t, "emp-1", TestImages.Person(40, 2), key: "abc")).Content.ReadFromJsonAsync<VerifyFaceResponse>(AuthApp.Json);
        other!.RequestId.ShouldNotBe(first.RequestId);
        (await ConsumedAsync(t)).ShouldBe(before + 2);

        // and the same key on a different operation is also separate
        var identify = await IdentifyAsync(t, TestImages.Person(40, 1), key: "abc");
        identify.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ConsumedAsync(t)).ShouldBe(before + 4); // identify costs 2
    }

    [Fact]
    public async Task An_idempotency_key_is_bound_to_the_request_target_not_just_the_image()
    {
        var t = await NewTenantAsync("F18");
        var photo = TestImages.Person(45);
        (await EnrollAsync(t, "emp-1", photo, key: "k1")).StatusCode.ShouldBe(HttpStatusCode.OK);
        await EnrollAsync(t, "emp-2", TestImages.Person(46));
        var consumed = await ConsumedAsync(t);

        // same key + same image, different target: must be a new, billed request - not a free replay of emp-1's result
        var verifyA = await VerifyAsync(t, "emp-1", TestImages.Person(45, 1), key: "k2");
        var verifyB = await VerifyAsync(t, "emp-2", TestImages.Person(45, 1), key: "k2");
        (await verifyA.Content.ReadFromJsonAsync<VerifyFaceResponse>(AuthApp.Json))!.Match.ShouldBeTrue();
        (await verifyB.Content.ReadFromJsonAsync<VerifyFaceResponse>(AuthApp.Json))!.Match.ShouldBeFalse();
        (await ConsumedAsync(t)).ShouldBe(consumed + 2);

        // an enrolment for a different person must not be swallowed by a replay either
        var other = TestImages.Person(47);
        (await EnrollAsync(t, "emp-3", other, key: "k3")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var again = await EnrollAsync(t, "emp-4", other, key: "k3");
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict); // duplicate image, not a replayed success for emp-3
        (await _app.WithTenantDbAsync(t.ClientId, db => db.FaceProfiles.AnyAsync(p => p.ExternalRef == "emp-4"))).ShouldBeFalse();
    }

    [Fact]
    public async Task Concurrent_enrolments_of_one_person_never_fail_with_a_server_error()
    {
        var t = await NewTenantAsync("F19");
        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Task.Run(() => EnrollAsync(t, "same", TestImages.Person(200 + i)))));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK || r.StatusCode == HttpStatusCode.Conflict);
        responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBeGreaterThanOrEqualTo(1);
        (await _app.WithTenantDbAsync(t.ClientId, db => db.FaceProfiles.CountAsync())).ShouldBe(1);
        // every successful enrolment was charged, every refused one was not
        (await ConsumedAsync(t)).ShouldBe(responses.Count(r => r.StatusCode == HttpStatusCode.OK));
    }

    [Fact]
    public async Task A_new_enrolment_is_found_at_once_even_when_the_index_was_warm()
    {
        var t = await NewTenantAsync("F20");
        await EnrollAsync(t, "first", TestImages.Person(210));
        (await IdentifyAsync(t, TestImages.Person(211))).StatusCode.ShouldBe(HttpStatusCode.OK); // warms the cache without the new person

        await EnrollAsync(t, "second", TestImages.Person(211));
        var found = await (await IdentifyAsync(t, TestImages.Person(211, 1))).Content.ReadFromJsonAsync<IdentifyFaceResponse>(AuthApp.Json);
        found!.Matches.Single().ExternalRef.ShouldBe("second");
    }

    [Fact]
    public async Task Concurrent_requests_with_one_idempotency_key_are_charged_once()
    {
        var t = await NewTenantAsync("F12");
        await EnrollAsync(t, "emp-1", TestImages.Person(41));
        var before = await ConsumedAsync(t);
        var image = TestImages.Person(41, 1);

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => VerifyAsync(t, "emp-1", image, key: "same"))));

        responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBeGreaterThanOrEqualTo(1);
        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK || r.StatusCode == HttpStatusCode.Conflict);
        (await ConsumedAsync(t)).ShouldBe(before + 1);
    }

    [Fact]
    public async Task Erasing_a_person_removes_them_from_matching_immediately_and_is_audited()
    {
        var t = await NewTenantAsync("F13");
        await EnrollAsync(t, "emp-1", TestImages.Person(50));
        var found = await (await IdentifyAsync(t, TestImages.Person(50, 1))).Content.ReadFromJsonAsync<IdentifyFaceResponse>(AuthApp.Json);
        found!.Matches.Count.ShouldBe(1); // warms the in-memory index
        var profile = found.Matches[0].ProfileId;

        (await _app.DeleteAsync($"/api/v1/faces/profiles/{profile}", t.Token)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var after = await (await IdentifyAsync(t, TestImages.Person(50, 2))).Content.ReadFromJsonAsync<IdentifyFaceResponse>(AuthApp.Json);
        after!.Matches.ShouldBeEmpty();
        (await VerifyAsync(t, "emp-1", TestImages.Person(50, 1))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _app.GetAsync($"/api/v1/faces/profiles/{profile}", t.Token)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _app.WithTenantDbAsync(t.ClientId, db => db.FaceTemplates.CountAsync())).ShouldBe(0);

        // history survives but no longer names the person
        var requests = await JsonOf(await _app.GetAsync("/api/v1/faces/requests?operation=Identify", t.Token));
        var firstId = requests.GetProperty("items").EnumerateArray().Last().GetProperty("id").GetGuid();
        var detail = await JsonOf(await _app.GetAsync($"/api/v1/faces/requests/{firstId}", t.Token));
        detail.GetProperty("candidates")[0].GetProperty("externalRef").ValueKind.ShouldBe(JsonValueKind.Null);

        var audited = await _app.WithDbAsync(db => db.AuditLogs.AnyAsync(a => a.Action == "face.profile_erased" && a.ClientId == t.ClientId));
        audited.ShouldBeTrue();
    }

    [Fact]
    public async Task The_retention_sweeper_erases_expired_profiles_inside_each_tenant()
    {
        var t = await NewTenantAsync("F14");
        var other = await NewTenantAsync("F14B");
        await EnrollAsync(t, "old", TestImages.Person(60));
        await EnrollAsync(t, "fresh", TestImages.Person(61));
        await EnrollAsync(other, "old", TestImages.Person(62));

        await _app.WithTenantDbAsync(t.ClientId, async db =>
        {
            var p = await db.FaceProfiles.SingleAsync(x => x.ExternalRef == "old");
            p.RetentionUntil = DateTime.UtcNow.AddMinutes(-5);
            await db.SaveChangesAsync();
            return true;
        });

        var sweeper = new FaceRetentionSweeper(_app.Factory.Services.GetRequiredService<IServiceScopeFactory>(), Microsoft.Extensions.Options.Options.Create(new FaceRetentionOptions()), TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<FaceRetentionSweeper>.Instance);
        (await sweeper.SweepOnceAsync(default)).ShouldBe(1);

        (await _app.WithTenantDbAsync(t.ClientId, db => db.FaceProfiles.Select(p => p.ExternalRef).ToListAsync())).ShouldBe(["fresh"]);
        (await _app.WithTenantDbAsync(other.ClientId, db => db.FaceProfiles.CountAsync())).ShouldBe(1);
    }

    [Fact]
    public async Task Permissions_limit_what_a_client_user_may_do()
    {
        var t = await NewTenantAsync("F15");
        await EnrollAsync(t, "emp-1", TestImages.Person(70));
        await _app.CreateClientUserAsync(t.ClientId, "user@f15.test", "ClientUser");
        var user = await _app.LoginAsync("user@f15.test", AuthApp.StrongPassword);
        var asUser = t with { Token = user.AccessToken };

        (await EnrollAsync(asUser, "emp-2", TestImages.Person(71))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await VerifyAsync(asUser, "emp-1", TestImages.Person(70, 1))).StatusCode.ShouldBe(HttpStatusCode.OK);
        var profile = (await JsonOf(await _app.GetAsync("/api/v1/faces/profiles", asUser.Token))).GetProperty("items")[0].GetProperty("id").GetGuid();
        (await _app.DeleteAsync($"/api/v1/faces/profiles/{profile}", asUser.Token)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _app.PostFormAsync("/api/v1/faces/detect", TestImages.Person(1), null, "")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Detect_is_free_and_leaves_no_record()
    {
        var t = await NewTenantAsync("F16");
        var response = await _app.PostFormAsync("/api/v1/faces/detect", TestImages.Person(80), null, t.Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<DetectFaceResponse>(AuthApp.Json);
        body!.FaceCount.ShouldBe(1);
        body.Credits.Charged.ShouldBe(0);
        (await ConsumedAsync(t)).ShouldBe(0);
        (await _app.WithTenantDbAsync(t.ClientId, db => db.RecognitionRequests.CountAsync())).ShouldBe(0);

        var balance = await _app.GetAsync("/api/v1/faces/balance", t.Token);
        (await JsonOf(balance)).GetProperty("remaining").GetInt32().ShouldBe(100);
    }

    [Fact]
    public async Task A_provider_outage_is_reported_as_unavailable_and_never_billed()
    {
        await using var app = await AuthApp.CreateAsync(_fixture, configure: services =>
        {
            var real = services.Single(d => d.ServiceType == typeof(IFaceEngine));
            services.Remove(real);
            services.AddSingleton<IFaceEngine, BrokenEngine>();
        });
        var platform = await app.SuperAdminAsync();
        var (client, admin) = await app.OnboardClientAsync(platform.AccessToken, "F17", "a@f17.test");
        await app.SeedLicenseAsync(client.Id, 10);

        var response = await app.PostFormAsync("/api/v1/faces/identify", TestImages.Person(90), null, admin.AccessToken);
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await CodeOf(response)).ShouldBe(ErrorCodes.FaceProviderUnavailable);
        (await app.WithDbAsync(async db => (await db.Licenses.AsNoTracking().SingleAsync()).ConsumedCredits)).ShouldBe(0);

        // recorded for diagnostics, but with no idempotency key so a retry reaches the engine again
        var rows = await app.WithTenantDbAsync(client.Id, db => db.RecognitionRequests.AsNoTracking().ToListAsync());
        rows.Single().Outcome.ShouldBe(RecognitionOutcome.ProviderError);
        rows.Single().IdempotencyKey.ShouldBeNull();
    }

    private sealed class BrokenEngine : IFaceEngine
    {
        public string Provider => "mock";

        public string ModelVersion => "mock-1";

        public int Dimensions => 256;

        public Task<IReadOnlyList<DetectedFace>> DetectAsync(PreparedImage image, CancellationToken cancellationToken) =>
            throw new FaceProviderException("down");

        public Task<float[]> ExtractAsync(PreparedImage image, DetectedFace face, CancellationToken cancellationToken) =>
            throw new FaceProviderException("down");

        public double Similarity(float[] a, float[] b) => 0;
    }
}
