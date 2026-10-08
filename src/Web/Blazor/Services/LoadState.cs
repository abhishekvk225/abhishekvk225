namespace NexaVerify.Web.Services;

/// <summary>
/// The "load something for a page" pattern in one place: <c>Result</c> is null while loading, a newer load cancels the older one and
/// can never be overwritten by it (navigating from client A to client B quickly cannot show A under B), and disposing cancels
/// whatever is still in flight.
/// </summary>
public sealed class LoadState<T> : IDisposable
{
    private CancellationTokenSource? _cts;
    private int _version;

    public ApiResult<T>? Result { get; private set; }

    /// <summary>Starts a load. Returns true when this load's answer is the one now shown.</summary>
    /// <param name="load">The call; receives the token to pass on to the API client.</param>
    /// <param name="keepCurrent">Keep showing the current content while reloading (no skeleton flash).</param>
    public async Task<bool> LoadAsync(Func<CancellationToken, Task<ApiResult<T>>> load, bool keepCurrent = false)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        var cts = _cts = new CancellationTokenSource();
        var version = ++_version;
        if (!keepCurrent)
        {
            Result = null;
        }

        ApiResult<T> loaded;
        try
        {
            loaded = await load(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        if (version != _version)
        {
            return false;
        }

        Result = loaded;
        return true;
    }

    /// <summary>Shows a result the page already has (for example the response of an update) and drops any load in flight.</summary>
    public void Set(ApiResult<T> result)
    {
        _cts?.Cancel();
        _version++;
        Result = result;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
