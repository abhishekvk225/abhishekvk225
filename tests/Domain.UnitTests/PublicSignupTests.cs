using NexaVerify.Domain.Common;
using NexaVerify.Domain.Identity;
using NexaVerify.Domain.Licensing;
using NexaVerify.Domain.Tenancy;

namespace NexaVerify.Domain.UnitTests;

public class PublicSignupTests
{
    private static readonly DateTime Now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private static PendingSignup NewPending(string email = "  Ada@Example.COM ") =>
        PendingSignup.Issue("  Acme Ltd ", " Ada Lovelace ", email, "hash", [1, 2, 3], Now, TimeSpan.FromHours(24));

    [Fact]
    public void A_pending_signup_normalises_its_fields_and_expires_after_its_lifetime()
    {
        var pending = NewPending();

        pending.CompanyName.ShouldBe("Acme Ltd");
        pending.FullName.ShouldBe("Ada Lovelace");
        pending.Email.ShouldBe("Ada@Example.COM");
        pending.NormalizedEmail.ShouldBe(User.Normalize("ada@example.com"));
        pending.ExpiresAt.ShouldBe(Now.AddHours(24));
        pending.IsLive(Now.AddHours(23)).ShouldBeTrue();
        pending.IsLive(Now.AddHours(24)).ShouldBeFalse();
        pending.ConsumedAt.ShouldBeNull();
    }

    [Theory]
    [InlineData("", "Ada", "a@b.test", "SIGNUP_COMPANY_INVALID")]
    [InlineData("Acme", " ", "a@b.test", "SIGNUP_NAME_INVALID")]
    [InlineData("Acme", "Ada", "", "SIGNUP_EMAIL_INVALID")]
    public void Incomplete_signups_are_rejected(string company, string name, string email, string code)
    {
        Should.Throw<DomainException>(() => PendingSignup.Issue(company, name, email, "hash", [1], Now, TimeSpan.FromHours(1))).Code.ShouldBe(code);
    }

    [Fact]
    public void Over_long_values_and_non_positive_lifetimes_are_rejected()
    {
        Should.Throw<DomainException>(() => PendingSignup.Issue(new string('x', 151), "Ada", "a@b.test", "h", [1], Now, TimeSpan.FromHours(1))).Code.ShouldBe("SIGNUP_COMPANY_INVALID");
        Should.Throw<DomainException>(() => PendingSignup.Issue("Acme", "Ada", "a@b.test", "h", [1], Now, TimeSpan.Zero)).Code.ShouldBe("SIGNUP_LIFETIME_INVALID");
    }

    [Fact]
    public void Reissue_replaces_the_token_restarts_the_period_and_stops_after_three_resends()
    {
        var pending = NewPending();

        for (var i = 1; i <= PendingSignup.MaxResends; i++)
        {
            pending.Reissue([9, (byte)i], Now.AddHours(i), TimeSpan.FromHours(24)).ShouldBeTrue();
            pending.TokenHash.ShouldBe([9, (byte)i]);
            pending.ExpiresAt.ShouldBe(Now.AddHours(i + 24));
            pending.ResendCount.ShouldBe(i);
        }

        pending.Reissue([1], Now.AddHours(5), TimeSpan.FromHours(24)).ShouldBeFalse();
        NewPending().Reissue([1], Now.AddHours(25), TimeSpan.FromHours(24)).ShouldBeFalse(); // already expired
    }

    [Fact]
    public void Contact_requests_trim_values_and_enforce_limits()
    {
        var request = ContactRequest.Create(" Ada ", " ada@b.test ", "  ", " Hello ", Now);

        request.Name.ShouldBe("Ada");
        request.Email.ShouldBe("ada@b.test");
        request.Company.ShouldBeNull();
        request.Message.ShouldBe("Hello");
        request.CreatedAt.ShouldBe(Now);

        Should.Throw<DomainException>(() => ContactRequest.Create("Ada", "a@b.test", null, " ", Now)).Code.ShouldBe("CONTACT_MESSAGE_INVALID");
        Should.Throw<DomainException>(() => ContactRequest.Create("Ada", "a@b.test", null, new string('m', 4001), Now)).Code.ShouldBe("CONTACT_MESSAGE_INVALID");
        Should.Throw<DomainException>(() => ContactRequest.Create("", "a@b.test", null, "Hi", Now)).Code.ShouldBe("CONTACT_NAME_INVALID");
    }

    [Fact]
    public void New_plans_are_private_by_default_and_carry_no_highlights()
    {
        var plan = Plan.Create("PRO", "Pro", 1000, 30);

        plan.IsPublic.ShouldBeFalse();
        plan.IsTrial.ShouldBeFalse();
        plan.Highlights.ShouldBeEmpty();
        plan.DisplayPrice.ShouldBeNull();
        plan.DisplayOrder.ShouldBe(0);
    }
}
