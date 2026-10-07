using Microsoft.JSInterop;

namespace NexaVerify.Web.ComponentTests;

public class ThemeServiceTests : UiTestBase
{
    private ThemeService Create()
    {
        JSInterop.Mode = JSRuntimeMode.Strict;
        return new ThemeService(Services.GetRequiredService<IJSRuntime>());
    }

    [Fact]
    public async Task Uses_stored_preference_over_system()
    {
        JSInterop.Setup<string?>("nexa.storage.get", ThemeService.StorageKey).SetResult("Light");
        JSInterop.Setup<bool>("nexa.theme.prefersDark").SetResult(true);
        var sut = Create();

        await sut.InitializeAsync();

        sut.Preference.ShouldBe(ThemePreference.Light);
        sut.IsDarkMode.ShouldBeFalse();
    }

    [Fact]
    public async Task Falls_back_to_prefers_color_scheme_when_nothing_stored()
    {
        JSInterop.Setup<string?>("nexa.storage.get", ThemeService.StorageKey).SetResult(null);
        JSInterop.Setup<bool>("nexa.theme.prefersDark").SetResult(true);
        var sut = Create();

        await sut.InitializeAsync();

        sut.Preference.ShouldBe(ThemePreference.System);
        sut.IsDarkMode.ShouldBeTrue();
    }

    [Fact]
    public async Task Blocked_storage_does_not_throw_and_defaults_to_system()
    {
        JSInterop.Setup<string?>("nexa.storage.get", ThemeService.StorageKey).SetException(new JSException("SecurityError"));
        JSInterop.Setup<bool>("nexa.theme.prefersDark").SetException(new JSException("no matchMedia"));
        var sut = Create();

        await sut.InitializeAsync();

        sut.Preference.ShouldBe(ThemePreference.System);
        sut.IsDarkMode.ShouldBeFalse();
    }

    [Fact]
    public async Task Setting_preference_persists_raises_changed_and_survives_storage_failure()
    {
        JSInterop.Setup<string?>("nexa.storage.get", ThemeService.StorageKey).SetResult(null);
        JSInterop.Setup<bool>("nexa.theme.prefersDark").SetResult(false);
        var save = JSInterop.SetupVoid("nexa.storage.set", ThemeService.StorageKey, "Dark");
        save.SetVoidResult();
        var sut = Create();
        await sut.InitializeAsync();
        var changed = 0;
        sut.Changed += () => changed++;

        await sut.SetPreferenceAsync(ThemePreference.Dark);

        sut.IsDarkMode.ShouldBeTrue();
        changed.ShouldBe(1);
        save.Invocations.Count.ShouldBe(1);

        JSInterop.SetupVoid("nexa.storage.set", ThemeService.StorageKey, "Light").SetException(new JSException("QuotaExceeded"));
        await sut.SetPreferenceAsync(ThemePreference.Light); // must not throw
        sut.IsDarkMode.ShouldBeFalse();
    }

    [Fact]
    public async Task System_change_only_matters_in_system_mode()
    {
        JSInterop.Setup<string?>("nexa.storage.get", ThemeService.StorageKey).SetResult("Dark");
        JSInterop.Setup<bool>("nexa.theme.prefersDark").SetResult(false);
        var sut = Create();
        await sut.InitializeAsync();
        var changed = 0;
        sut.Changed += () => changed++;

        sut.SetSystemPrefersDark(true);
        changed.ShouldBe(0);
        sut.IsDarkMode.ShouldBeTrue();
    }

    [Theory]
    [InlineData("Dark", ThemePreference.Dark)]
    [InlineData("light", ThemePreference.Light)]
    [InlineData("garbage", ThemePreference.System)]
    [InlineData("42", ThemePreference.System)]
    [InlineData(null, ThemePreference.System)]
    public void Parse_is_forgiving(string? input, ThemePreference expected) => ThemeService.Parse(input).ShouldBe(expected);
}
