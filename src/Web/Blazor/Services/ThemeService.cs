using Microsoft.JSInterop;

namespace NexaVerify.Web.Services;

public enum ThemePreference
{
    System,
    Light,
    Dark,
}

/// <summary>
/// Holds the user's theme choice. Persisted in the browser's localStorage (per-viewer convenience, never required);
/// "System" follows <c>prefers-color-scheme</c>. All browser access is wrapped so private windows / blocked storage degrade silently.
/// </summary>
public sealed class ThemeService(IJSRuntime js)
{
    public const string StorageKey = "nexa.theme";

    private bool _initialized;
    private bool _systemPrefersDark;

    public event Action? Changed;

    public ThemePreference Preference { get; private set; } = ThemePreference.System;

    public bool IsDarkMode => Preference switch
    {
        ThemePreference.Dark => true,
        ThemePreference.Light => false,
        _ => _systemPrefersDark,
    };

    /// <summary>Reads stored preference and the OS setting. Call after first render (needs JS). Idempotent.</summary>
    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        try
        {
            var stored = await js.InvokeAsync<string?>("nexa.storage.get", StorageKey);
            Preference = Parse(stored);
        }
        catch (Exception ex) when (IsBrowserFailure(ex))
        {
            Preference = ThemePreference.System;
        }

        try
        {
            _systemPrefersDark = await js.InvokeAsync<bool>("nexa.theme.prefersDark");
        }
        catch (Exception ex) when (IsBrowserFailure(ex))
        {
            _systemPrefersDark = false;
        }

        Changed?.Invoke();
    }

    public async Task SetPreferenceAsync(ThemePreference preference)
    {
        Preference = preference;
        Changed?.Invoke();
        try
        {
            await js.InvokeVoidAsync("nexa.storage.set", StorageKey, preference.ToString());
        }
        catch (Exception ex) when (IsBrowserFailure(ex))
        {
            // Persistence is a convenience; the in-memory choice still applies for this session.
        }
    }

    /// <summary>Called when the OS colour scheme changes while the page is open.</summary>
    public void SetSystemPrefersDark(bool value)
    {
        if (_systemPrefersDark == value)
        {
            return;
        }

        _systemPrefersDark = value;
        if (Preference == ThemePreference.System)
        {
            Changed?.Invoke();
        }
    }

    public static ThemePreference Parse(string? stored) =>
        Enum.TryParse<ThemePreference>(stored, ignoreCase: true, out var p) && Enum.IsDefined(p) ? p : ThemePreference.System;

    private static bool IsBrowserFailure(Exception ex) =>
        ex is JSException or InvalidOperationException or JSDisconnectedException or TaskCanceledException;
}
