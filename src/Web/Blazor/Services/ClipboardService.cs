using Microsoft.JSInterop;

namespace NexaVerify.Web.Services;

public interface IClipboardService
{
    /// <summary>Copies text; returns false when the browser refuses (permissions, insecure context).</summary>
    Task<bool> CopyAsync(string text);
}

public sealed class ClipboardService(IJSRuntime js) : IClipboardService
{
    public async Task<bool> CopyAsync(string text)
    {
        try
        {
            return await js.InvokeAsync<bool>("nexa.clipboard.copy", text);
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException or JSDisconnectedException or TaskCanceledException)
        {
            return false;
        }
    }
}
