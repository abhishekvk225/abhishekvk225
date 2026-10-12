using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Application.Verification.Processing;
using NexaVerify.Application.Verification.Providers;
using NexaVerify.Domain.Verification;
using NexaVerify.Infrastructure.Verification.Providers;
using NexaVerify.TestSupport.Verification;

namespace NexaVerify.Infrastructure.UnitTests.Verification;

public class ProviderTests
{
    private static async Task<ExternalStageOutcome> Guard(FakePanVerificationProvider p, int timeoutSeconds = 1) =>
        await ProviderCallGuard.RunAsync(
            p.ProviderId, p.IsConfigured, timeoutSeconds, ct => p.VerifyAsync(new PanProviderRequest("ABCPT1234" + "F", null, null, "c"), ct),
            NullLogger.Instance, CancellationToken.None);

    [Fact]
    public async Task Not_configured_provider_never_succeeds()
    {
        var pan = new NotConfiguredPanVerificationProvider();
        pan.IsConfigured.ShouldBeFalse();
        var r = await pan.VerifyAsync(new PanProviderRequest("ABCPT1234F", null, null, "c"), CancellationToken.None);
        r.State.ShouldBe(ExternalState.ProviderNotConfigured);
        r.ProviderReference.ShouldBeNull();

        var pp = await new NotConfiguredPassportVerificationProvider().VerifyAsync(new PassportProviderRequest("X1234567", "IND", null, null, null, "c"), CancellationToken.None);
        pp.State.ShouldBe(ExternalState.ProviderNotConfigured);
    }

    [Theory]
    [InlineData(FakeProviderMode.Verified, ExternalState.Verified)]
    [InlineData(FakeProviderMode.NotVerified, ExternalState.NotVerified)]
    [InlineData(FakeProviderMode.Inconclusive, ExternalState.Inconclusive)]
    [InlineData(FakeProviderMode.Error, ExternalState.ProviderError)]
    [InlineData(FakeProviderMode.Throws, ExternalState.ProviderError)]
    [InlineData(FakeProviderMode.Timeout, ExternalState.Timeout)]
    [InlineData(FakeProviderMode.VerifiedWithoutReference, ExternalState.ProviderError)] // Rule V-1
    public async Task Guard_maps_every_provider_behaviour(FakeProviderMode mode, ExternalState expected)
    {
        var provider = new FakePanVerificationProvider { Mode = mode };
        var outcome = await Guard(provider);
        outcome.State.ShouldBe(expected);
        if (expected == ExternalState.Verified)
        {
            outcome.ProviderId.ShouldBe("fake-pan");
            outcome.ProviderReference.ShouldNotBeNullOrWhiteSpace();
        }
        else if (expected != ExternalState.NotVerified)
        {
            outcome.ProviderReference.ShouldBeNull();
        }
    }

    [Fact]
    public async Task Guard_does_not_call_an_unconfigured_provider()
    {
        var provider = new FakePanVerificationProvider { IsConfigured = false };
        (await Guard(provider)).State.ShouldBe(ExternalState.ProviderNotConfigured);
        provider.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Guard_rethrows_caller_cancellation_but_not_provider_timeouts()
    {
        using var cts = new CancellationTokenSource();
        var provider = new FakePanVerificationProvider { Mode = FakeProviderMode.Timeout };
        var task = ProviderCallGuard.RunAsync(provider.ProviderId, true, 30, ct => provider.VerifyAsync(new PanProviderRequest("A", null, null, "c"), ct), NullLogger.Instance, cts.Token);
        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public void Guard_rejects_states_a_provider_may_not_return()
    {
        ProviderCallGuard.Map("p", new ProviderResult(ExternalState.Pending, "r", [], null)).State.ShouldBe(ExternalState.ProviderError);
        ProviderCallGuard.Map("p", new ProviderResult(ExternalState.NotRequested, "r", [], null)).State.ShouldBe(ExternalState.ProviderError);
        ProviderCallGuard.Map("", new ProviderResult(ExternalState.Verified, "r", [], null)).State.ShouldBe(ExternalState.ProviderError);
        ProviderCallGuard.Map("p", new ProviderResult(ExternalState.Verified, " ", [], null)).State.ShouldBe(ExternalState.ProviderError);
    }

    [Fact]
    public async Task Fake_provider_script_is_driven_by_the_last_character()
    {
        var p = new FakePanVerificationProvider();
        foreach (var (suffix, state) in new[] { ('V', ExternalState.Verified), ('N', ExternalState.NotVerified), ('I', ExternalState.Inconclusive), ('E', ExternalState.ProviderError) })
        {
            (await p.VerifyAsync(new PanProviderRequest("ABCPT1234" + suffix, null, null, "c"), CancellationToken.None)).State.ShouldBe(state);
        }
    }

    private static IOptions<VerificationOptions> Cfg(string pan = "None", string passport = "None")
    {
        var o = new VerificationOptions();
        o.Providers.Pan.Provider = pan;
        o.Providers.Passport.Provider = passport;
        return Options.Create(o);
    }

    [Fact]
    public void Resolver_defaults_to_not_configured()
    {
        var pan = new PanVerificationProviderResolver(Cfg(), [new NotConfiguredPanVerificationProvider()], NullLogger<PanVerificationProviderResolver>.Instance).Resolve();
        pan.IsConfigured.ShouldBeFalse();
        var pp = new PassportVerificationProviderResolver(Cfg(), [new NotConfiguredPassportVerificationProvider()], NullLogger<PassportVerificationProviderResolver>.Instance).Resolve();
        pp.IsConfigured.ShouldBeFalse();
        new PanVerificationProviderResolver(Cfg(), [], NullLogger<PanVerificationProviderResolver>.Instance).Resolve().IsConfigured.ShouldBeFalse();
    }

    [Fact]
    public void Resolver_selects_a_registered_configured_provider_by_id_only()
    {
        var fake = new FakePanVerificationProvider();
        var providers = new IPanVerificationProvider[] { new NotConfiguredPanVerificationProvider(), fake };
        new PanVerificationProviderResolver(Cfg(pan: "FAKE-PAN"), providers, NullLogger<PanVerificationProviderResolver>.Instance).Resolve().ShouldBeSameAs(fake);
        new PanVerificationProviderResolver(Cfg(pan: "None"), providers, NullLogger<PanVerificationProviderResolver>.Instance).Resolve().IsConfigured.ShouldBeFalse();
        new PanVerificationProviderResolver(Cfg(pan: "unknown-vendor"), providers, NullLogger<PanVerificationProviderResolver>.Instance).Resolve().IsConfigured.ShouldBeFalse();
        new PanVerificationProviderResolver(Cfg(pan: "FAKE-PAN"), [new FakePanVerificationProvider { IsConfigured = false }], NullLogger<PanVerificationProviderResolver>.Instance).Resolve().IsConfigured.ShouldBeFalse();

        var fakePp = new FakePassportVerificationProvider();
        new PassportVerificationProviderResolver(Cfg(passport: "fake-passport"), [fakePp], NullLogger<PassportVerificationProviderResolver>.Instance).Resolve().ShouldBeSameAs(fakePp);
    }
}
