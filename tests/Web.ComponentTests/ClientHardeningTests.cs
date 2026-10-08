using Microsoft.JSInterop;
using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using NexaVerify.Contracts.Dashboards;
using NexaVerify.Web.Security;
using NotificationBell = NexaVerify.Web.Components.NotificationBell;
using NotificationsPage = NexaVerify.Web.Pages.Client.Notifications;

namespace NexaVerify.Web.ComponentTests;

/// <summary>M8b-01: a background poll must not keep an unattended session alive.</summary>
public class PassiveCallTests
{
    private sealed record Payload(int Value);

    [Fact]
    public async Task A_passive_call_does_not_slide_the_idle_window_but_a_normal_one_does()
    {
        var h = new BffHarness();
        await h.SignInAsync();
        h.Api.Respond = _ => ScriptedApi.Json(HttpStatusCode.OK, "{\"value\":1}");
        h.Clock.Advance(TimeSpan.FromMinutes(20));
        var seenBefore = (await h.Store.GetAsync("sid-1"))!.LastSeenAt;

        await h.Gateway.GetAsync<Payload>("client/notifications", default, new ApiCallOptions { Passive = true });
        (await h.Store.GetAsync("sid-1"))!.LastSeenAt.ShouldBe(seenBefore, "polling is not activity");

        await h.Gateway.GetAsync<Payload>("client/dashboard");
        (await h.Store.GetAsync("sid-1"))!.LastSeenAt.ShouldBe(h.Clock.GetUtcNow(), "a call the user made is activity");
    }

    [Fact]
    public async Task A_tab_that_only_polls_is_signed_out_when_the_idle_time_is_up()
    {
        var h = new BffHarness(apiOptions: new ApiClientOptions { TimeoutSeconds = 30 });
        await h.Store.SaveAsync(SessionFixtures.NewSession(h.Clock, accessLifetimeSeconds: 86_400));
        h.Api.Respond = _ => ScriptedApi.Json(HttpStatusCode.OK, "{\"value\":1}");

        for (var minute = 0; minute < 31; minute++)
        {
            h.Clock.Advance(TimeSpan.FromMinutes(1));
            var result = await h.Gateway.GetAsync<Payload>("client/notifications", default, new ApiCallOptions { Passive = true });
            if (minute < 29)
            {
                result.IsSuccess.ShouldBeTrue($"minute {minute}");
            }
        }

        (await h.Gateway.GetAsync<Payload>("client/notifications", default, new ApiCallOptions { Passive = true })).Error!.Code.ShouldBe("SESSION_EXPIRED");
        (await h.Store.GetAsync("sid-1")).ShouldBeNull();
    }

    [Fact]
    public async Task An_upload_body_is_rebuilt_for_the_retry_instead_of_being_copied()
    {
        var h = new BffHarness();
        await h.SignInAsync();
        h.Api.Respond = r => r.Authorization == "Bearer access-2" ? ScriptedApi.Json(HttpStatusCode.OK, "{\"value\":1}") : ScriptedApi.Json(HttpStatusCode.Unauthorized, "{}");
        var built = 0;
        HttpContent Build()
        {
            built++;
            return new ByteArrayContent([1, 2, 3, 4]) { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream") } };
        }

        var result = await h.Gateway.SendAsync<Payload>(HttpMethod.Post, "faces/verify", (Func<HttpContent>)Build);

        result.IsSuccess.ShouldBeTrue();
        built.ShouldBe(2, "once for the first attempt, once for the retry after the token refresh");
        h.Api.Seen.Count.ShouldBe(2);
        h.Api.Seen[1].BodyBytes.ShouldBe(h.Api.Seen[0].BodyBytes);
    }
}

public class NotificationBellBehaviourTests : ClientPageTestBase
{
    [Fact]
    public async Task Polls_are_background_calls_and_the_first_load_is_not()
    {
        SignInAsClientAdmin();
        var cut = Render<NotificationBell>();
        cut.WaitForAssertion(() => Notifications.Calls.ShouldBe(1));
        Notifications.BackgroundCalls.ShouldBe(0);

        Clock.Advance(TimeSpan.FromSeconds(60));

        cut.WaitForAssertion(() => Notifications.Calls.ShouldBe(2));
        Notifications.BackgroundCalls.ShouldBe(1);
        await ((IAsyncDisposable)cut.Instance).DisposeAsync();
    }

    [Fact]
    public async Task The_loop_ends_for_good_when_the_session_is_over()
    {
        SignInAsClientAdmin();
        var cut = Render<NotificationBell>();
        cut.WaitForAssertion(() => Notifications.Calls.ShouldBe(1));
        Notifications.Override = _ => Ok.Fail<NotificationFeedDto>("SESSION_EXPIRED", "Your session has ended. Please sign in again.", null, 401);

        Clock.Advance(TimeSpan.FromSeconds(60));
        await cut.Instance.PollLoop!.WaitAsync(TimeSpan.FromSeconds(10)); // completes when the loop returns

        Notifications.Calls.ShouldBe(2);
        Clock.Advance(TimeSpan.FromMinutes(10));
        Notifications.Calls.ShouldBe(2, "no more calls once the session is gone");
        await ((IAsyncDisposable)cut.Instance).DisposeAsync();
    }

    [Fact]
    public async Task An_unexpected_failure_in_one_poll_does_not_end_polling_or_break_disposal()
    {
        SignInAsClientAdmin();
        var cut = Render<NotificationBell>();
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Notifications, 3 unread"));
        var calls = 0;
        Notifications.Override = q =>
        {
            calls++;
            if (calls == 1)
            {
                throw new InvalidOperationException("boom: connection string leaked");
            }

            return Task.FromResult(ApiResult<NotificationFeedDto>.Ok(new NotificationFeedDto(9, new NexaVerify.Contracts.Common.PagedResult<NotificationDto>([], 1, 5, 0))));
        };

        Clock.Advance(TimeSpan.FromSeconds(60));
        cut.WaitForAssertion(() => calls.ShouldBe(1));
        Clock.Advance(TimeSpan.FromSeconds(60));

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Notifications, 9 unread"));
        await Should.NotThrowAsync(async () => await ((IAsyncDisposable)cut.Instance).DisposeAsync());
    }

    [Fact]
    public async Task Disposing_while_a_poll_is_failing_never_throws_into_circuit_teardown()
    {
        SignInAsClientAdmin();
        var cut = Render<NotificationBell>();
        cut.WaitForAssertion(() => Notifications.Calls.ShouldBe(1));
        Notifications.Override = _ => throw new InvalidOperationException("down");
        Clock.Advance(TimeSpan.FromSeconds(60));
        cut.WaitForAssertion(() => Notifications.Calls.ShouldBe(2));

        await Should.NotThrowAsync(async () => await ((IAsyncDisposable)cut.Instance).DisposeAsync());
    }

    [Fact]
    public void Marking_as_read_also_reloads_the_bells_list_not_just_its_badge()
    {
        SignInAsClientAdmin();
        var bell = Render<NotificationBell>();
        bell.WaitForAssertion(() => Notifications.Calls.ShouldBe(1));
        var page = Render<NotificationsPage>();
        page.WaitForAssertion(() => page.FindAll("table [data-testid=mark-read]").Count.ShouldBe(1));
        var before = Notifications.Calls;
        var backgroundBefore = Notifications.BackgroundCalls;

        page.Find("[data-testid=mark-read]").Click();

        bell.WaitForAssertion(() => Notifications.Calls.ShouldBeGreaterThanOrEqualTo(before + 2), TimeSpan.FromSeconds(5)); // the page's own reload plus the bell's
        Notifications.BackgroundCalls.ShouldBe(backgroundBefore, "reloading after a click is a user action, not a poll");
    }
}

public class FaceCaptureStreamingTests : ClientPageTestBase
{
    private sealed class FakeFrame(byte[] data, long? length = null) : IJSStreamReference
    {
        public int Opens { get; private set; }

        public long Length { get; } = length ?? data.Length;

        public ValueTask<Stream> OpenReadStreamAsync(long maxAllowedSize = 512000, CancellationToken cancellationToken = default)
        {
            Opens++;
            if (Length > maxAllowedSize)
            {
                throw new InvalidOperationException("over the cap");
            }

            return new ValueTask<Stream>(new MemoryStream(data));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private (IRenderedComponent<FaceCapture> Cut, List<CapturedImage> Captured) StartCamera(IJSStreamReference? frame)
    {
        var module = JSInterop.SetupModule("./js/face-capture.js");
        module.Setup<string>("start", _ => true).SetResult(string.Empty);
        module.Setup<IJSStreamReference?>("captureStream", _ => true).SetResult(frame);
        module.SetupVoid("stop", _ => true).SetVoidResult();
        Render<MudPopoverProvider>();
        var captured = new List<CapturedImage>();
        var cut = Render<FaceCapture>(p => p.Add(x => x.OnCaptured, EventCallback.Factory.Create<CapturedImage>(this, captured.Add)));
        cut.Find("[data-testid=camera-start]").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=camera-capture]").Count.ShouldBe(1));
        return (cut, captured);
    }

    private static byte[] FakeJpeg(int length) => new byte[length].Select((_, i) => i < 3 ? (byte)(i == 0 ? 0xFF : i == 1 ? 0xD8 : 0xFF) : (byte)7).ToArray();

    [Fact]
    public void A_frame_inside_the_cap_is_read_as_a_stream_and_offered_for_use()
    {
        var frame = new FakeFrame(FakeJpeg(200_000));
        var (cut, captured) = StartCamera(frame);

        cut.Find("[data-testid=camera-capture]").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=capture-use]").Count.ShouldBe(1));
        cut.FindAll("[data-testid=capture-preview]").Count.ShouldBe(1, "a small photo gets a preview");
        cut.Find("[data-testid=capture-use]").Click();

        frame.Opens.ShouldBe(1);
        captured.Single().Data.Length.ShouldBe(200_000);
        captured.Single().ContentType.ShouldBe("image/jpeg");
        captured.Single().FileName.ShouldBe("upload.jpg", "no device-chosen or person-named file name");
    }

    [Fact]
    public void A_frame_over_the_cap_is_refused_before_a_single_byte_is_read()
    {
        var frame = new FakeFrame([], length: FaceLimits.MaxImageBytes + 1);
        var (cut, captured) = StartCamera(frame);

        cut.Find("[data-testid=camera-capture]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=camera-problem]").TextContent.ShouldContain("too large"));
        frame.Opens.ShouldBe(0, "the size is checked up front");
        captured.ShouldBeEmpty();
        cut.FindAll("[data-testid=capture-use]").ShouldBeEmpty();
    }

    [Fact]
    public void A_frame_exactly_at_the_cap_is_accepted_and_a_big_one_gets_no_base64_preview()
    {
        var frame = new FakeFrame(FakeJpeg((int)FaceLimits.MaxImageBytes));
        var (cut, _) = StartCamera(frame);

        cut.Find("[data-testid=camera-capture]").Click();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=capture-use]").Count.ShouldBe(1));
        cut.FindAll("[data-testid=capture-preview]").ShouldBeEmpty("no multi-megabyte data URI pushed through the circuit");
        cut.Find("[data-testid=capture-ready]").TextContent.ShouldContain("5.0 MB");
        cut.Markup.ShouldNotContain("data:image");
    }

    [Fact]
    public void A_missing_or_empty_frame_is_a_friendly_message()
    {
        var (cut, _) = StartCamera(null);
        cut.Find("[data-testid=camera-capture]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=camera-problem]").TextContent.ShouldContain("could not take the photo"));
    }

    [Fact]
    public void An_empty_frame_is_a_friendly_message()
    {
        var (cut, _) = StartCamera(new FakeFrame([]));
        cut.Find("[data-testid=camera-capture]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=camera-problem]").TextContent.ShouldContain("could not take the photo"));
    }

    [Fact]
    public void An_uploaded_photo_larger_than_a_megabyte_has_no_preview_string_either()
    {
        Render<MudPopoverProvider>();
        var captured = new List<CapturedImage>();
        var cut = Render<FaceCapture>(p => p.Add(x => x.OnCaptured, EventCallback.Factory.Create<CapturedImage>(this, captured.Add)));

        cut.FindComponent<Microsoft.AspNetCore.Components.Forms.InputFile>().UploadFiles(
            Bunit.InputFileContent.CreateFromBinary(Jpeg(1_500_000), "Ada Lovelace passport.jpg", null, "image/jpeg"));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=upload-ready]").Count.ShouldBe(1));
        cut.FindAll("img.nv-preview").ShouldBeEmpty();
        cut.Find("[data-testid=capture-use]").Click();

        captured.Single().FileName.ShouldBe("upload.jpg", "the original file name is not forwarded");
    }

    [Fact]
    public void Uploads_over_the_cap_are_refused_with_the_shared_wording()
    {
        Render<MudPopoverProvider>();
        var cut = Render<FaceCapture>();

        cut.FindComponent<Microsoft.AspNetCore.Components.Forms.InputFile>().UploadFiles(
            Bunit.InputFileContent.CreateFromBinary(new byte[FaceLimits.MaxImageBytes + 1], "big.jpg", null, "image/jpeg"));

        cut.WaitForAssertion(() => cut.Find("[data-testid=upload-problem]").TextContent.ShouldContain($"{FaceLimits.MaxImageMegabytes} MB"));
    }

    [Fact]
    public void The_file_types_are_named_once_and_include_webp()
    {
        FaceLimits.TypesText.ShouldContain("WebP");
        PhotoGuard.Check(new CapturedImage([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12], "image/jpeg", "x.jpg"))!.ShouldContain(FaceLimits.TypesText);
        Render<MudPopoverProvider>();
        Render<FaceCapture>().Markup.ShouldContain(FaceLimits.TypesText);
    }

    [Fact]
    public void The_browser_script_streams_the_frame_and_never_builds_a_base64_string_of_it()
    {
        var js = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Web", "Blazor", "wwwroot", "js", "face-capture.js"));

        js.ShouldContain("captureStream");
        js.ShouldContain("toBlob");
        js.ShouldNotContain("toDataURL");
    }
}

public class HubLimitTests
{
    [Fact]
    public void The_signalr_message_limit_stays_at_the_default_so_a_circuit_cannot_be_made_to_accept_huge_messages()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("Api:BaseUrl", "http://localhost:5101"));

        var limit = factory.Services.GetRequiredService<IOptions<HubOptions>>().Value.MaximumReceiveMessageSize;

        limit.ShouldBe(32 * 1024);
    }
}
