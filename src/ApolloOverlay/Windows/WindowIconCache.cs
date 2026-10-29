using System.Windows.Media;

namespace ApolloOverlay;
/// <summary>
/// Resolves icons away from the UI thread. A window identity includes the observed
/// generation, so an icon for a destroyed HWND cannot be reused for its successor.
/// </summary>
internal sealed class WindowIconCache : IDisposable
{
    private readonly record struct Key(nint Handle, int Pid, long Started, long Generation)
    {
        internal static Key From(WindowRef window) => new(window.Handle, window.Pid, window.Started, window.Generation);
    }

    private sealed class Entry
    {
        internal ImageSource? Image;
        internal bool Pending;
        internal long RetryAfter;
        internal long LastAccess;
    }

    private const int Capacity = 128;
    private const int RetryMilliseconds = 10000;
    private readonly object gate = new();
    private readonly Dictionary<Key, Entry> entries = [];
    private readonly SemaphoreSlim workers = new(2, 2);
    private readonly Func<WindowRef, ImageSource?> load;
    private readonly Func<long> clock;
    private long sequence;
    private bool disposed;
    internal event Action<WindowRef>? Available;
    internal WindowIconCache(Func<WindowRef, ImageSource?> load, Func<long>? clock = null)
    {
        this.load = load;
        this.clock = clock ?? (() => Environment.TickCount64);
    }

    // Returns cached results only. A missing icon remains a fallback until Available.
    internal ImageSource? Get(WindowRef window)
    {
        var key = Key.From(window);
        Entry entry;
        lock (gate)
        {
            if (disposed)
                return null;
            if (!entries.TryGetValue(key, out entry!))
            {
                if (entries.Count >= Capacity)
                {
                    var victim = entries.Where(pair => !pair.Value.Pending).DefaultIfEmpty().MinBy(pair => pair.Value?.LastAccess ?? long.MaxValue);
                    if (victim.Value is null)
                        return null;
                    entries.Remove(victim.Key);
                }

                entry = new Entry();
                entries.Add(key, entry);
            }

            entry.LastAccess = ++sequence;
            if (entry.Image is not null)
                return entry.Image;
            if (entry.Pending || clock() < entry.RetryAfter)
                return null;
            entry.Pending = true;
        }

        _ = LoadAsync(window, key, entry);
        return null;
    }

    private async Task LoadAsync(WindowRef window, Key key, Entry entry)
    {
        // Task.Run is intentional: native calls, including the first validation, must
        // never execute synchronously in the caller's Dispatcher thread.
        await Task.Run(async () =>
        {
            await workers.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (gate)
                    if (disposed)
                        return;
                ImageSource? image;
                try
                {
                    image = load(window);
                }
                catch (Exception)
                {
                    image = null;
                }

                Action<WindowRef>? notify = null;
                lock (gate)
                {
                    if (disposed || !entries.TryGetValue(key, out var current) || !ReferenceEquals(current, entry))
                        return;
                    entry.Pending = false;
                    entry.Image = image;
                    entry.RetryAfter = clock() + RetryMilliseconds;
                    if (image is not null)
                        notify = Available;
                }

                // The observer must marshal to its own UI thread. It may unsubscribe
                // concurrently, so the observer must also guard its own lifetime.
                try
                {
                    notify?.Invoke(window);
                }
                catch (Exception)
                {
                }
            }
            finally
            {
                workers.Release();
            }
        }).ConfigureAwait(false);
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            entries.Clear();
            Available = null;
        }
        // Workers are deliberately not disposed while pending native calls drain.
    }
}
