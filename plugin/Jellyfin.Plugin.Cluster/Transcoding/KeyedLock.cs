namespace Jellyfin.Plugin.Cluster.Transcoding;

/// <summary>
/// jfc: minimal in-file replacement for the <c>AsyncKeyedLock</c> package's <c>AsyncKeyedLocker&lt;string&gt;</c>,
/// which upstream's <c>TranscodeManager._transcodingLocks</c> uses. Same semantics: one mutual-exclusion lock
/// per key, ref-counted so the underlying <see cref="SemaphoreSlim"/> is created lazily and disposed once the
/// last holder for that key releases it. We don't ship a second copy of a package Jellyfin already loads, and
/// this plugin only ever locks on a handful of concurrently-active output paths, so a pooled/allocation-free
/// implementation isn't worth the complexity.
/// </summary>
public sealed class KeyedLock : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>Acquires the lock for <paramref name="key"/>, waiting if another caller currently holds it.</summary>
    /// <param name="key">The lock key (upstream uses the transcode output path).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An <see cref="IDisposable"/> that releases the lock when disposed.</returns>
    public async ValueTask<IDisposable> LockAsync(string key, CancellationToken cancellationToken = default)
    {
        var entry = Rent(key);

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            ReleaseRef(key, entry);
            throw;
        }

        return new Releaser(this, key, entry);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var entry in _entries.Values)
            {
                entry.Semaphore.Dispose();
            }

            _entries.Clear();
        }
    }

    private Entry Rent(string key)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                existing.RefCount++;
                return existing;
            }

            var created = new Entry();
            _entries[key] = created;
            return created;
        }
    }

    private void ReleaseRef(string key, Entry entry)
    {
        var removed = false;

        lock (_gate)
        {
            entry.RefCount--;
            if (entry.RefCount == 0)
            {
                _entries.Remove(key);
                removed = true;
            }
        }

        if (removed)
        {
            entry.Semaphore.Dispose();
        }
    }

    private sealed class Entry
    {
        internal readonly SemaphoreSlim Semaphore = new(1, 1);
        internal int RefCount = 1;
    }

    private sealed class Releaser : IDisposable
    {
        private readonly KeyedLock _owner;
        private readonly string _key;
        private readonly Entry _entry;
        private bool _disposed;

        internal Releaser(KeyedLock owner, string key, Entry entry)
        {
            _owner = owner;
            _key = key;
            _entry = entry;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _entry.Semaphore.Release();
            _owner.ReleaseRef(_key, _entry);
        }
    }
}
