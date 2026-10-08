using Microsoft.AspNetCore.Components.Forms;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Faces;
using NexaVerify.Contracts.Licensing;
using NexaVerify.Web.Components;
using EnrollPage = NexaVerify.Web.Pages.Client.Enroll;
using HistoryPage = NexaVerify.Web.Pages.Client.History;
using IdentifyPage = NexaVerify.Web.Pages.Client.Identify;
using LicenseDetailPage = NexaVerify.Web.Pages.Client.LicenseDetail;
using LicensePage = NexaVerify.Web.Pages.Client.License;
using ProfileDetailPage = NexaVerify.Web.Pages.Client.ProfileDetail;
using ProfilesPage = NexaVerify.Web.Pages.Client.Profiles;
using VerifyPage = NexaVerify.Web.Pages.Client.Verify;

namespace NexaVerify.Web.ComponentTests;

public class ClientLicensePagesTests : ClientPageTestBase
{
    [Fact]
    public void The_license_page_shows_a_skeleton_then_the_gauge_in_plain_words()
    {
        SignInAsClientAdmin();
        var gate = new Gate<ApiResult<LicenseSummaryDto>>();
        ClientLicense.Summary = () => gate.Task;

        var cut = Render<LicensePage>();

        cut.FindAll("[data-testid=skeleton-card]").Count.ShouldBeGreaterThan(0);
        gate.Release(ApiResult<LicenseSummaryDto>.Ok(ClientSample.Summary()));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=license-gauge]").Count.ShouldBe(1));
        cut.Find("[data-testid=license-message]").TextContent.ShouldBe("842 credits available.");
        cut.Find("[data-testid=license-table] tbody a").GetAttribute("href").ShouldStartWith("client/license/");
    }

    [Fact]
    public void No_license_yet_is_explained_not_blank()
    {
        SignInAsClientAdmin();
        ClientLicense.Summary = () => Ok.Of(ClientSample.Summary() with { Licenses = [], ActiveLicenses = 0 });

        var cut = Render<LicensePage>();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=empty-state]").Count.ShouldBe(1));
        cut.Markup.ShouldContain("Contact your account manager");
    }

    [Fact]
    public void A_failed_load_shows_the_reference_and_retry_reloads()
    {
        SignInAsClientAdmin();
        var attempts = 0;
        ClientLicense.Summary = () => ++attempts == 1 ? Ok.Fail<LicenseSummaryDto>(correlation: "corr-lic") : Ok.Of(ClientSample.Summary());

        var cut = Render<LicensePage>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=correlation-id]").TextContent.ShouldBe("corr-lic"));
        cut.FindAll("button").Single(b => b.TextContent.Contains("Try again")).Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=license-gauge]").Count.ShouldBe(1));
    }

    [Fact]
    public void The_csv_download_goes_through_the_portal_and_is_hidden_without_the_permission()
    {
        SignInAs(WebPermissions.LicenseRead);
        var readOnly = Render<LicensePage>();
        readOnly.WaitForAssertion(() => readOnly.FindAll("[data-testid=license-gauge]").Count.ShouldBe(1));
        readOnly.FindAll("[data-testid=download-csv]").ShouldBeEmpty();

        SignInAs(WebPermissions.LicenseRead, WebPermissions.UsageRead);
        var cut = Render<LicensePage>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=download-csv]").Count.ShouldBe(1));
        var href = cut.Find("[data-testid=download-csv]").GetAttribute("href")!;
        href.ShouldStartWith("bff/client/reports/usage.csv?from=2026-05-17&to=2026-06-15");
        href.ShouldNotContain("token", Case.Insensitive);
    }

    [Fact]
    public void License_detail_lists_the_credit_history_with_friendly_labels()
    {
        SignInAsClientAdmin();

        var cut = Render<LicenseDetailPage>(p => p.Add(x => x.Id, Guid.NewGuid()));

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=license-gauge]").Count.ShouldBe(1));
        cut.WaitForAssertion(() => cut.FindAll("table tbody tr").Count.ShouldBe(1));
        cut.Find("table tbody tr").TextContent.ShouldContain("Used");
        cut.Markup.ShouldContain("Credit history");
    }
}

public class PhotoGuardTests
{
    [Fact]
    public void A_missing_oversized_or_non_image_photo_is_refused_with_a_friendly_message()
    {
        PhotoGuard.Check(null).ShouldBe("Add a photo first.");
        PhotoGuard.Check(new CapturedImage(new byte[FaceLimits.MaxImageBytes + 1], "image/jpeg", "a.jpg"))!.ShouldContain("5 MB");
        PhotoGuard.Check(new CapturedImage("not an image at all"u8.ToArray(), "image/jpeg", "a.jpg"))!.ShouldContain("does not look like a valid photo");
        PhotoGuard.Check(new CapturedImage([0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3], "image/jpeg", "a.jpg")).ShouldBeNull();
    }

    [Fact]
    public void Forgetting_a_photo_overwrites_its_bytes_and_every_submit_gets_its_own_key()
    {
        var image = new CapturedImage([1, 2, 3, 4], "image/jpeg", "a.jpg");

        PhotoGuard.Forget(image);

        image.Data.ShouldAllBe(b => b == 0);
        PhotoGuard.NewIdempotencyKey().ShouldNotBe(PhotoGuard.NewIdempotencyKey());
        PhotoGuard.NewIdempotencyKey().Length.ShouldBeLessThanOrEqualTo(100);
    }
}

public class FacePhotoUploadTests : ClientPageTestBase
{
    [Fact]
    public void A_photo_over_five_megabytes_is_refused_before_anything_is_sent()
    {
        SignInAsClientAdmin();
        var cut = Render<EnrollPage>();

        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary(new byte[FaceLimits.MaxImageBytes + 1], "big.jpg", null, "image/jpeg"));

        cut.WaitForAssertion(() => cut.Find("[data-testid=upload-problem]").TextContent.ShouldContain("limit is 5 MB"));
        cut.FindAll("[data-testid=photo-ready]").ShouldBeEmpty();
        Faces.Calls.ShouldBeEmpty();
    }

    [Fact]
    public void Only_photo_types_are_accepted_and_the_real_content_is_checked()
    {
        SignInAsClientAdmin();
        var cut = Render<EnrollPage>();

        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText("hello", "notes.txt", null, "text/plain"));
        cut.WaitForAssertion(() => cut.Find("[data-testid=upload-problem]").TextContent.ShouldContain("JPEG, PNG or WebP"));

        // A script renamed to .jpg and labelled image/jpeg is caught by looking at the bytes.
        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText("<script>alert(1)</script>", "evil.jpg", null, "image/jpeg"));
        cut.WaitForAssertion(() => cut.Find("[data-testid=upload-problem]").TextContent.ShouldContain("does not look like a valid photo"));
        cut.FindAll("[data-testid=photo-ready]").ShouldBeEmpty();
    }
}

public class EnrollPageTests : ClientPageTestBase
{
    [Fact]
    public void Registering_needs_a_photo_a_reference_and_a_consent_reference()
    {
        SignInAsClientAdmin();
        var cut = Render<EnrollPage>();
        cut.Find("[data-testid=enroll-submit]").HasAttribute("disabled").ShouldBeTrue("no photo yet");

        ChoosePhoto(cut, Jpeg());
        cut.Find("[data-testid=enroll-submit]").HasAttribute("disabled").ShouldBeFalse();
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Enter your own reference for this person"));
        cut.Markup.ShouldContain("consent is recorded");
        Faces.Calls.ShouldBeEmpty();
    }

    [Fact]
    public void A_valid_registration_sends_the_details_shows_the_cost_and_forgets_the_photo()
    {
        SignInAsClientAdmin();
        var cut = Render<EnrollPage>();
        ChoosePhoto(cut, Jpeg());
        Fill(cut, "Your reference for this person", "EMP-1001");
        Fill(cut, "Where their consent is recorded", "FORM-42");

        cut.Find("form").Submit();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=enroll-result]").Single().TextContent.ShouldContain("The person was registered."));
        Faces.Calls.ShouldBe(["enroll:EMP-1001:FORM-42"]);
        cut.Find("[data-testid=credits-charged]").TextContent.ShouldBe("1");
        cut.Find("[data-testid=credits-remaining]").TextContent.ShouldBe("99");
        cut.Markup.ShouldContain("Photos registered for this person");
        cut.FindAll("[data-testid=photo-ready]").ShouldBeEmpty("the chosen photo is dropped after use");
        Faces.LiveBuffers.Single().ShouldAllBe(b => b == 0, "the portal overwrites the image bytes once the request is done");
        Faces.UploadedImages.Single().Length.ShouldBe(2048);
    }

    [Fact]
    public void Every_submit_gets_a_fresh_idempotency_key()
    {
        SignInAsClientAdmin();
        var cut = Render<EnrollPage>();
        for (var i = 0; i < 2; i++)
        {
            ChoosePhoto(cut, Jpeg());
            Fill(cut, "Your reference for this person", $"EMP-{i}");
            Fill(cut, "Where their consent is recorded", "FORM-42");
            cut.Find("form").Submit();
            var expected = i + 1;
            cut.WaitForAssertion(() => Faces.IdempotencyKeys.Count.ShouldBe(expected));
        }

        Faces.IdempotencyKeys.Distinct().Count().ShouldBe(2);
    }

    [Fact]
    public void An_api_refusal_is_shown_with_the_reference_and_the_photo_is_still_dropped()
    {
        SignInAsClientAdmin();
        Faces.Enroll = () => Ok.Fail<EnrollFaceResponse>("NO_FACE_DETECTED", "We could not find a face in the photo.", "corr-nf", 422);
        var cut = Render<EnrollPage>();
        ChoosePhoto(cut, Jpeg());
        Fill(cut, "Your reference for this person", "EMP-1");
        Fill(cut, "Where their consent is recorded", "FORM-1");

        cut.Find("form").Submit();

        cut.WaitForAssertion(() => cut.Find("[data-testid=correlation-id]").TextContent.ShouldBe("corr-nf"));
        Faces.LiveBuffers.Single().ShouldAllBe(b => b == 0);
        cut.FindAll("[data-testid=enroll-result] [data-testid=charge-summary]").ShouldBeEmpty();
    }

    [Fact]
    public void Only_people_who_may_register_faces_see_the_nav_entry()
    {
        var clientUser = NavigationCatalog.Trim(NavigationCatalog.Client, p => WebPermissions.ClientAdminDefaults.Contains(p) && p != WebPermissions.FacesEnroll);

        clientUser.SelectMany(g => g.Items).Select(i => i.Href).ShouldNotContain("client/enroll");
    }
}

public class VerifyAndIdentifyPageTests : ClientPageTestBase
{
    [Fact]
    public void Verify_shows_the_verdict_the_score_the_cost_and_the_speed()
    {
        SignInAsClientAdmin();
        var cut = Render<VerifyPage>();
        ChoosePhoto(cut, Jpeg());
        Fill(cut, "Reference of the person to check", "EMP-1001");

        cut.Find("form").Submit();

        cut.WaitForAssertion(() => cut.Find("[data-testid=verify-verdict]").TextContent.ShouldBe("This looks like the same person."));
        cut.Find("[data-testid=verify-result]").TextContent.ShouldContain("0.912");
        cut.Find("[data-testid=credits-charged]").TextContent.ShouldBe("1");
        cut.FindAll("[data-testid=elapsed]").Count.ShouldBe(1);
        Faces.Calls.ShouldBe(["verify:EMP-1001"]);
    }

    [Fact]
    public void Verify_says_no_match_plainly()
    {
        SignInAsClientAdmin();
        Faces.Verify = () => Ok.Of(new VerifyFaceResponse(false, 0.21m, 0.6m, "NoMatch", Guid.NewGuid(), Guid.NewGuid(), new CreditsDto(1, 5)));
        var cut = Render<VerifyPage>();
        ChoosePhoto(cut, Jpeg());
        Fill(cut, "Reference of the person to check", "EMP-1001");

        cut.Find("form").Submit();

        cut.WaitForAssertion(() => cut.Find("[data-testid=verify-verdict]").TextContent.ShouldBe("This does not look like the same person."));
    }

    [Fact]
    public void Verify_needs_a_reference_before_anything_is_uploaded()
    {
        SignInAsClientAdmin();
        var cut = Render<VerifyPage>();
        ChoosePhoto(cut, Jpeg());

        cut.Find("form").Submit();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Enter the reference of the person to check against."));
        Faces.Calls.ShouldBeEmpty();
    }

    [Fact]
    public void Identify_lists_candidates_as_text_even_when_a_reference_contains_markup()
    {
        SignInAsClientAdmin();
        Faces.Identify = () => Ok.Of(new IdentifyFaceResponse(
            [new FaceMatchDto(Guid.NewGuid(), "<img src=x onerror=alert(1)>", 0.91m), new FaceMatchDto(Guid.NewGuid(), "EMP-2", 0.7m)], 0.91m, 0.6m, "Matched", Guid.NewGuid(), new CreditsDto(2, 10)));
        var cut = Render<IdentifyPage>();
        ChoosePhoto(cut, Jpeg());

        cut.Find("form").Submit();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=identify-matches] tbody tr").Count.ShouldBe(2));
        cut.Find("[data-testid=identify-matches]").InnerHtml.ShouldContain("&lt;img src=x onerror=alert(1)&gt;");
        cut.FindAll("[data-testid=identify-matches] img").ShouldBeEmpty();
        cut.Find("[data-testid=credits-charged]").TextContent.ShouldBe("2");
        Faces.Calls.ShouldBe(["identify:5"]);
    }

    [Fact]
    public void Identify_without_a_match_says_so()
    {
        SignInAsClientAdmin();
        Faces.Identify = () => Ok.Of(new IdentifyFaceResponse([], null, 0.6m, "NoMatch", Guid.NewGuid(), new CreditsDto(2, 10)));
        var cut = Render<IdentifyPage>();
        ChoosePhoto(cut, Jpeg());

        cut.Find("form").Submit();

        cut.WaitForAssertion(() => cut.Find("[data-testid=identify-verdict]").TextContent.ShouldBe("No one matched this photo."));
        cut.FindAll("[data-testid=identify-matches]").ShouldBeEmpty();
    }
}

public class ProfilesPageTests : ClientPageTestBase
{
    [Fact]
    public void Profiles_show_rows_the_photo_count_and_a_link()
    {
        SignInAsClientAdmin();
        var cut = Render<ProfilesPage>();

        cut.WaitForAssertion(() => cut.FindAll("table tbody tr").Count.ShouldBe(1));
        var row = cut.Find("table tbody tr");
        row.TextContent.ShouldContain("EMP-1001");
        row.TextContent.ShouldContain("Ada Lovelace");
        row.QuerySelectorAll("a").Select(a => a.GetAttribute("href")).ShouldContain(h => h!.StartsWith("client/profiles/", StringComparison.Ordinal));
    }

    [Fact]
    public void No_people_yet_invites_the_first_registration_and_failures_show_the_reference()
    {
        SignInAsClientAdmin();
        Faces.Profiles = _ => Task.FromResult(Ok.Page<FaceProfileListItemDto>());
        var empty = Render<ProfilesPage>();
        empty.WaitForAssertion(() => empty.FindAll("[data-testid=empty-state]").Count.ShouldBe(1));

        Faces.Profiles = _ => Ok.Fail<PagedResult<FaceProfileListItemDto>>(correlation: "corr-pf");
        var failed = Render<ProfilesPage>();
        failed.WaitForAssertion(() => failed.Find("[data-testid=correlation-id]").TextContent.ShouldBe("corr-pf"));
    }

    [Fact]
    public void Erasing_is_hidden_without_the_permission()
    {
        SignInAs(WebPermissions.FacesRead);
        var readOnly = Render<ProfilesPage>();
        readOnly.WaitForAssertion(() => readOnly.FindAll("table tbody tr").Count.ShouldBe(1));
        readOnly.FindAll("[data-testid=erase-person]").ShouldBeEmpty();
        readOnly.FindAll("[data-testid=register-person]").ShouldBeEmpty();
    }

    [Fact]
    public void Erasing_needs_the_typed_word_and_then_removes_the_person()
    {
        SignInAsClientAdmin();
        var providers = Providers();
        var cut = Render<ProfilesPage>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=erase-person]").Count.ShouldBeGreaterThan(0));

        cut.Find("[data-testid=erase-person]").Click();
        providers.WaitForAssertion(() => providers.FindAll("[data-testid=confirm-ok]").Count.ShouldBe(1));
        providers.Find("[data-testid=confirm-phrase]").TextContent.ShouldBe("ERASE");
        providers.Find("[data-testid=confirm-ok]").HasAttribute("disabled").ShouldBeTrue();
        providers.Find(".mud-dialog input").Input("ERASE");
        providers.Find("[data-testid=confirm-ok]").Click();

        cut.WaitForAssertion(() => Faces.Calls.ShouldContain(c => c.StartsWith("erase:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_search_is_passed_to_the_api()
    {
        SignInAsClientAdmin();
        var cut = Render<ProfilesPage>();
        cut.WaitForAssertion(() => Faces.Calls.Count.ShouldBeGreaterThan(0));

        var field = cut.FindComponent<MudTextField<string>>();
        await cut.InvokeAsync(() => field.Instance.ValueChanged.InvokeAsync("ada"));

        cut.WaitForAssertion(() => Faces.Calls.ShouldContain(c => c.StartsWith("profiles:ada:", StringComparison.Ordinal)));
    }

    [Fact]
    public void Profile_detail_shows_the_photo_count_and_keeps_client_text_inert()
    {
        SignInAsClientAdmin();

        var cut = Render<ProfileDetailPage>(p => p.Add(x => x.Id, Guid.NewGuid()));

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=templates-table] tbody tr").Count.ShouldBe(2));
        cut.Markup.ShouldContain("Registered photos (2)");
        cut.Markup.ShouldContain("&lt;b&gt;x&lt;/b&gt;");
        cut.FindAll("[data-testid=detail-list] b").ShouldBeEmpty();
    }
}

public class HistoryPageTests : ClientPageTestBase
{
    [Fact]
    public async Task History_lists_checks_with_a_friendly_result_and_passes_the_filters_on()
    {
        SignInAsClientAdmin();
        var cut = Render<HistoryPage>();
        cut.WaitForAssertion(() => cut.FindAll("table tbody tr").Count.ShouldBe(1));
        cut.Find("table tbody tr .nv-status-chip").GetAttribute("data-status").ShouldBe("Match");

        var select = cut.FindComponent<MudSelect<string>>();
        await cut.InvokeAsync(() => select.Instance.ValueChanged.InvokeAsync("Verify"));
        cut.Find("[data-testid=apply-filters]").Click();

        cut.WaitForAssertion(() => Faces.Calls.ShouldContain("history:Verify:"));
    }

    [Fact]
    public void History_without_rows_and_with_errors_use_the_standard_states()
    {
        SignInAsClientAdmin();
        Faces.History = _ => Task.FromResult(Ok.Page<RecognitionRequestDto>());
        var empty = Render<HistoryPage>();
        empty.WaitForAssertion(() => empty.FindAll("[data-testid=empty-state]").Count.ShouldBe(1));

        Faces.History = _ => Ok.Fail<PagedResult<RecognitionRequestDto>>(correlation: "corr-h");
        var failed = Render<HistoryPage>();
        failed.WaitForAssertion(() => failed.Find("[data-testid=correlation-id]").TextContent.ShouldBe("corr-h"));
    }

    [Fact]
    public void Details_open_in_a_dialog_with_the_candidates()
    {
        SignInAsClientAdmin();
        var providers = Providers();
        var cut = Render<HistoryPage>();
        cut.WaitForAssertion(() => cut.FindAll("table tbody tr button").Count.ShouldBe(1));

        cut.Find("table tbody tr button").Click();

        providers.WaitForAssertion(() => providers.FindAll("[data-testid=candidates] tbody tr").Count.ShouldBe(1));
    }
}
