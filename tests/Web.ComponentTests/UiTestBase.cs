using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace NexaVerify.Web.ComponentTests;

public sealed class FakeClipboard : IClipboardService
{
    public string? Last { get; private set; }

    public Task<bool> CopyAsync(string text)
    {
        Last = text;
        return Task.FromResult(true);
    }
}

/// <summary>Shared bUnit setup: MudBlazor services, loose JS interop, a fixed clock, and the providers dialogs need.</summary>
public abstract class UiTestBase : BunitContext, IAsyncLifetime
{
    protected UiTestBase()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton<TimeProvider>(Clock);
        Services.AddSingleton<FakeClipboard>();
        Services.AddSingleton<IClipboardService>(sp => sp.GetRequiredService<FakeClipboard>());
        Services.AddScoped<IAppSnackbar, AppSnackbar>();
    }

    // MudBlazor registers async-only disposables; dispose the container asynchronously (xUnit then skips the failing sync path).
    public Task InitializeAsync() => Task.CompletedTask;

    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    protected FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 6, 15, 9, 0, 0, TimeSpan.Zero));

    protected IRenderedComponent<MudDialogProvider> RenderProviders()
    {
        Render<MudPopoverProvider>();
        return Render<MudDialogProvider>();
    }
}
