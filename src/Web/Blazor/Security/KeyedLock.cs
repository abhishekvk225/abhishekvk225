namespace NexaVerify.Web.Security;

/// <summary>
/// Mutual exclusion per key (for example per session id) with memory bounded by the number of callers currently holding or
/// waiting for a lock: an entry is created on first use and removed only when the last reference is released, so it can neither
/// leak for abandoned keys nor be dropped while somebody still holds it (which would let two callers run at once).
/// </summary>
public sealed class KeyedLock
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>Number of keys currently held or awaited (for diagnostics and tests).</summary>
    public int ActiveKeys
    {
        get
        {
            lock (_entries)
            {
                return _entries.Count;
            }
        }
    }

    public async Task<IDisposable> AcquireAsync(string key, CancellationToken ct = default)
    {
        Entry entry;
        lock (_entries)
        {
            if (!_entries.TryGetValue(key, out entry!))
            {
                entry = new Entry();
                _entries[key] = entry;
            }

            entry.References++;
        }

        try
        {
            await entry.Gate.WaitAsync(ct);
        }
        catch
        {
            Unreference(key, entry);
            throw;
        }

        return new Releaser(this, key, entry);
    }

    private void Unreference(string key, Entry entry)
    {
        lock (_entries)
        {
            if (--entry.References == 0)
            {
                _entries.Remove(key);
            }
        }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public int References { get; set; }
    }

    private sealed class Releaser(KeyedLock owner, string key, Entry entry) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                entry.Gate.Release();
                owner.Unreference(key, entry);
            }
        }
    }
}
