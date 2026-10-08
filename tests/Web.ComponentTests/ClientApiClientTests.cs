using System.Net;
using System.Text;
using NexaVerify.Contracts.Api;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Dashboards;
using NexaVerify.Contracts.Faces;
using NexaVerify.Contracts.Tenancy;

namespace NexaVerify.Web.ComponentTests;

public class ClientApiClientTests
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01];

    private static async Task<(BffHarness Harness, FacesApiClient Client)> NewAsync()
    {
        var h = new BffHarness();
        await h.SignInAsync();
        return (h, new FacesApiClient(h.Gateway));
    }

    [Fact]
    public async Task Enrolling_sends_a_multipart_form_with_the_image_the_fields_and_an_idempotency_key()
    {
        var (h, client) = await NewAsync();
        h.Api.Respond = _ => ScriptedApi.Json(HttpStatusCode.OK,
            "{\"profileId\":\"00000000-0000-0000-0000-000000000001\",\"templateId\":\"00000000-0000-0000-0000-000000000002\",\"profileCreated\":true,\"quality\":0.93,\"outcome\":\"Enrolled\",\"requestId\":\"00000000-0000-0000-0000-000000000003\",\"credits\":{\"charged\":1,\"remaining\":99}}");

        var result = await client.EnrollAsync(new EnrollFaceRequest("EMP-1001", "Ada", null, "FORM-42"), new NexaVerify.Web.Components.CapturedImage(Jpeg, "image/jpeg", "ada.jpg"), "idem-123");

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        result.Value.Credits.Remaining.ShouldBe(99);
        var seen = h.Api.Seen.Single();
        seen.Method.ShouldBe(HttpMethod.Post);
        seen.Path.ShouldBe("/api/v1/faces/enroll");
        seen.Headers["Idempotency-Key"].ShouldBe("idem-123");
        seen.ContentType.ShouldStartWith("multipart/form-data");
        seen.Authorization.ShouldStartWith("Bearer ", Case.Sensitive, "the token is attached on the server, never supplied by the page");
        seen.Body!.ShouldContain("name=ExternalRef");
        seen.Body!.ShouldContain("EMP-1001");
        seen.Body!.ShouldContain("name=ConsentReference");
        seen.Body!.ShouldContain("name=image");
        seen.Body!.ShouldContain("filename=upload.jpg");
        seen.Body!.ShouldNotContain("ada.jpg", Case.Insensitive, "the original file name (it may be a person's name) is not sent");
        seen.Body!.ShouldNotContain("name=Metadata", Case.Sensitive, "empty optional fields are not sent");
        IndexOf(seen.BodyBytes!, Jpeg).ShouldBeGreaterThan(0, "the image bytes travel unchanged");
    }

    [Fact]
    public async Task Verify_and_identify_use_their_own_routes_and_always_carry_the_key()
    {
        var (h, client) = await NewAsync();
        h.Api.Respond = _ => ScriptedApi.Json(HttpStatusCode.OK, "{\"match\":true,\"score\":0.9,\"threshold\":0.6,\"outcome\":\"Matched\",\"profileId\":\"00000000-0000-0000-0000-000000000001\",\"requestId\":\"00000000-0000-0000-0000-000000000003\",\"credits\":{\"charged\":1,\"remaining\":8}}");
        var image = new NexaVerify.Web.Components.CapturedImage(Jpeg, "image/jpeg", "v.jpg");

        (await client.VerifyAsync(new VerifyFaceRequest(null, "EMP-1"), image, "k1")).IsSuccess.ShouldBeTrue();
        h.Api.Respond = _ => ScriptedApi.Json(HttpStatusCode.OK, "{\"matches\":[],\"bestScore\":null,\"threshold\":0.6,\"outcome\":\"NoMatch\",\"requestId\":\"00000000-0000-0000-0000-000000000003\",\"credits\":{\"charged\":2,\"remaining\":6}}");
        var identify = await client.IdentifyAsync(new IdentifyFaceRequest(3), image, "k2");

        identify.Value.Matches.ShouldBeEmpty();
        h.Api.Seen.Select(s => s.Path).ShouldBe(["/api/v1/faces/verify", "/api/v1/faces/identify"]);
        h.Api.Seen.Select(s => s.Headers["Idempotency-Key"]).ShouldBe(["k1", "k2"]);
        h.Api.Seen[1].Body!.ShouldContain("name=TopK");
    }

    [Fact]
    public async Task A_rejected_photo_is_mapped_to_a_friendly_error_with_the_reference()
    {
        var (h, client) = await NewAsync();
        h.Api.Respond = _ => ScriptedApi.Json(HttpStatusCode.UnprocessableEntity,
            "{\"code\":\"NO_FACE_DETECTED\",\"detail\":\"We could not find a face in the photo.\",\"correlationId\":\"corr-face\"}");

        var result = await client.VerifyAsync(new VerifyFaceRequest(null, "EMP-1"), new NexaVerify.Web.Components.CapturedImage(Jpeg, "image/jpeg", "v.jpg"), "k");

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe("NO_FACE_DETECTED");
        result.Error.Message.ShouldBe("We could not find a face in the photo.");
        result.Error.CorrelationId.ShouldBe("corr-face");
    }

    [Fact]
    public async Task An_upload_is_replayed_with_its_body_and_key_after_a_token_refresh()
    {
        var h = new BffHarness();
        await h.SignInAsync();
        var faces = new FacesApiClient(h.Gateway);
        h.Api.Respond = r => r.Path.Contains("faces/verify", StringComparison.Ordinal) && h.Api.Seen.Count == 1
            ? ScriptedApi.Json(HttpStatusCode.Unauthorized, "{\"code\":\"TOKEN_EXPIRED\"}")
            : ScriptedApi.Json(HttpStatusCode.OK, "{\"match\":false,\"score\":0.1,\"threshold\":0.6,\"outcome\":\"NoMatch\",\"profileId\":\"00000000-0000-0000-0000-000000000001\",\"requestId\":\"00000000-0000-0000-0000-000000000003\",\"credits\":{\"charged\":1,\"remaining\":4}}");

        var result = await faces.VerifyAsync(new VerifyFaceRequest(null, "E"), new NexaVerify.Web.Components.CapturedImage(Jpeg, "image/jpeg", "v.jpg"), "same-key");

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        h.Api.Seen.Count(s => s.Path.EndsWith("faces/verify", StringComparison.Ordinal)).ShouldBe(2);
        h.Api.Seen.Where(s => s.Path.EndsWith("faces/verify", StringComparison.Ordinal)).ShouldAllBe(s => s.Headers["Idempotency-Key"] == "same-key" && IndexOf(s.BodyBytes!, Jpeg) > 0);
    }

    [Fact]
    public async Task Profile_listing_and_erasing_use_the_documented_routes()
    {
        var (h, client) = await NewAsync();
        h.Api.Respond = r => r.Method == HttpMethod.Delete
            ? new HttpResponseMessage(HttpStatusCode.NoContent)
            : ScriptedApi.Json(HttpStatusCode.OK, "{\"items\":[],\"page\":2,\"pageSize\":10,\"totalCount\":0}");

        await client.ListProfilesAsync(new FaceProfileListQuery { Page = 2, PageSize = 10, Search = "ada & co", Status = "Active" });
        var id = Guid.NewGuid();
        (await client.EraseProfileAsync(id)).IsSuccess.ShouldBeTrue();

        h.Api.Seen[0].Path.ShouldBe("/api/v1/faces/profiles?page=2&pageSize=10&search=ada%20%26%20co&status=Active");
        h.Api.Seen[1].Method.ShouldBe(HttpMethod.Delete);
        h.Api.Seen[1].Path.ShouldBe($"/api/v1/faces/profiles/{id}");
    }

    [Fact]
    public async Task Api_key_calls_send_the_documented_bodies_and_never_a_client_id()
    {
        var h = new BffHarness();
        await h.SignInAsync();
        var keys = new ApiKeysApiClient(h.Gateway);
        var id = Guid.NewGuid();
        h.Api.Respond = _ => ScriptedApi.Json(HttpStatusCode.OK,
            "{\"key\":{\"id\":\"00000000-0000-0000-0000-000000000001\",\"name\":\"k\",\"prefix\":\"nv_live_ab\",\"scopes\":[],\"status\":\"Active\",\"effectiveStatus\":\"Active\",\"allowedIps\":[],\"createdAt\":\"2026-01-01T00:00:00Z\",\"rowVersion\":\"x\"},\"rawKey\":\"nv_live_X\"}");

        await keys.CreateAsync(new CreateApiKeyRequest("Front desk", ["faces.verify"], null, 30, ["203.0.113.0/24"]));
        await keys.RegenerateAsync(id, 90);
        await keys.RevokeAsync(id, "leaked");

        h.Api.Seen[0].Path.ShouldBe("/api/v1/client/api-keys");
        h.Api.Seen[0].Body.ShouldBe("{\"name\":\"Front desk\",\"scopes\":[\"faces.verify\"],\"expiresAt\":null,\"rateLimitPerMinute\":30,\"allowedIps\":[\"203.0.113.0/24\"]}");
        h.Api.Seen[1].Path.ShouldBe($"/api/v1/client/api-keys/{id}/regenerate");
        h.Api.Seen[1].Body.ShouldBe("{\"graceMinutes\":90}");
        h.Api.Seen[2].Body.ShouldBe("{\"reason\":\"leaked\"}");
        h.Api.Seen.ShouldAllBe(s => !s.Path.Contains("clientId", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Log_filters_notifications_and_webhook_retries_use_the_documented_routes()
    {
        var h = new BffHarness();
        await h.SignInAsync();
        h.Api.Respond = _ => ScriptedApi.Json(HttpStatusCode.OK, "{\"items\":[],\"page\":1,\"pageSize\":25,\"totalCount\":0}");
        var keyId = Guid.NewGuid();
        await new ApiKeysApiClient(h.Gateway).GetLogsAsync(new ApiLogQuery { StatusClass = "ClientError", ApiKeyId = keyId, From = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc) });
        h.Api.Respond = _ => ScriptedApi.Json(HttpStatusCode.OK, "{\"unreadCount\":2,\"notifications\":{\"items\":[],\"page\":1,\"pageSize\":5,\"totalCount\":0}}");
        var feed = await new NotificationsApiClient(h.Gateway).ListAsync(new NotificationListQuery { PageSize = 5, UnreadOnly = true });
        h.Api.Respond = _ => ScriptedApi.Json(HttpStatusCode.OK,
            "{\"id\":12,\"eventId\":\"00000000-0000-0000-0000-000000000001\",\"eventType\":\"x\",\"status\":\"Pending\",\"attempts\":0,\"nextAttemptAt\":\"2026-01-01T00:00:00Z\",\"createdAt\":\"2026-01-01T00:00:00Z\"}");
        var hook = Guid.NewGuid();
        await new WebhooksApiClient(h.Gateway).RetryAsync(hook, 12);

        h.Api.Seen[0].Path.ShouldBe($"/api/v1/client/api-logs?page=1&pageSize=25&apiKeyId={keyId}&statusClass=ClientError&from=2026-01-02T00%3A00%3A00.0000000Z");
        feed.Value.UnreadCount.ShouldBe(2);
        h.Api.Seen[1].Path.ShouldBe("/api/v1/client/notifications?page=1&pageSize=5&unreadOnly=true");
        h.Api.Seen[2].Method.ShouldBe(HttpMethod.Post);
        h.Api.Seen[2].Path.ShouldBe($"/api/v1/client/webhooks/{hook}/deliveries/12/retry");
    }

    [Fact]
    public async Task Settings_are_saved_through_the_area_route_the_permission_belongs_to()
    {
        var h = new BffHarness();
        await h.SignInAsync();
        h.Api.Respond = _ => ScriptedApi.Json(HttpStatusCode.OK, "[]");
        var values = new Dictionary<string, System.Text.Json.JsonElement> { ["integration.allowedIps"] = System.Text.Json.JsonSerializer.SerializeToElement(new[] { "203.0.113.7" }) };

        await new ClientAccountApiClient(h.Gateway).UpdateSettingsAsync("security", new UpdateSettingsRequest(values));

        h.Api.Seen.Single().Method.ShouldBe(HttpMethod.Put);
        h.Api.Seen.Single().Path.ShouldBe("/api/v1/client/settings/security");
        h.Api.Seen.Single().Body.ShouldBe("{\"values\":{\"integration.allowedIps\":[\"203.0.113.7\"]}}");
    }

    [Theory]
    [InlineData("Recognition", "recognition")]
    [InlineData("Notifications", "notifications")]
    [InlineData("Integration", "security")]
    [InlineData("Security", "security")]
    public void Setting_groups_map_to_the_matching_area(string group, string route) => SettingGroups.For(group)!.Route.ShouldBe(route);

    [Fact]
    public void Platform_managed_groups_have_no_edit_area() => SettingGroups.For("Limits").ShouldBeNull();

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                return i;
            }
        }

        return -1;
    }
}
