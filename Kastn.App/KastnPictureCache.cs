using Avalonia.Media.Imaging;
using ZETL.Contracts;

namespace KASTN;

// Owns image content, in-flight fetches, and decoded bitmap lifetime. Bitmap
// methods run on the UI thread; content fetches may complete on any thread.
// Displayed bitmaps remain owned until reset/close, so eviction never disposes
// an image still referenced by a reader block or board card.
internal sealed class KastnPictureCache : IDisposable
{
    private const long DefaultContentBudget = 128L * 1024 * 1024;
    private readonly Func<string, ZetlSlipSnapshot, Task<ZetlPictureContent?>> fetch;
    private readonly long contentBudget;
    private readonly object gate = new();
    private readonly Dictionary<string, ZetlPictureContent> content = new(StringComparer.Ordinal);
    private readonly Queue<string> contentOrder = [];
    private readonly Dictionary<(long Generation, string Project, string Sha), Task<ZetlPictureContent?>> loads = new();
    private readonly Dictionary<(string Sha, int Width), Bitmap> decoded = new();
    private long contentBytes;
    private long generation;
    private bool disposed;

    public KastnPictureCache(
        Func<string, ZetlSlipSnapshot, Task<ZetlPictureContent?>> fetch,
        long contentBudget = DefaultContentBudget)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(contentBudget);
        this.fetch = fetch;
        this.contentBudget = contentBudget;
    }

    public async Task<ZetlPictureContent?> GetContentAsync(string projectId, ZetlSlipSnapshot slip)
    {
        if (slip.Picture is null)
        {
            return null;
        }

        Task<ZetlPictureContent?> loading;
        (long Generation, string Project, string Sha) key;
        lock (gate)
        {
            if (disposed)
            {
                return null;
            }
            if (content.TryGetValue(slip.Picture.Sha256, out var cached))
            {
                return cached;
            }
            key = (generation, projectId, slip.Picture.Sha256);
            if (!loads.TryGetValue(key, out loading!))
            {
                loading = FetchAndCacheAsync(key, slip);
                loads[key] = loading;
            }
        }

        try
        {
            return await loading.ConfigureAwait(false);
        }
        finally
        {
            lock (gate)
            {
                // An old waiter cannot remove a newer load for the same content.
                if (loads.TryGetValue(key, out var current) && ReferenceEquals(current, loading))
                {
                    loads.Remove(key);
                }
            }
        }
    }

    private async Task<ZetlPictureContent?> FetchAndCacheAsync(
        (long Generation, string Project, string Sha) key, ZetlSlipSnapshot slip)
    {
        var picture = await fetch(key.Project, slip).ConfigureAwait(false);
        if (picture is null || picture.Bytes.Length == 0
            || !string.Equals(picture.Sha256, key.Sha, StringComparison.Ordinal))
        {
            return null;
        }

        lock (gate)
        {
            if (disposed || generation != key.Generation)
            {
                return null;
            }
            if (picture.Bytes.LongLength <= contentBudget && !content.ContainsKey(key.Sha))
            {
                while (contentBytes + picture.Bytes.LongLength > contentBudget
                    && contentOrder.TryDequeue(out var expired))
                {
                    if (content.Remove(expired, out var removed))
                    {
                        contentBytes -= removed.Bytes.LongLength;
                    }
                }
                content.Add(key.Sha, picture);
                contentOrder.Enqueue(key.Sha);
                contentBytes += picture.Bytes.LongLength;
            }
            return picture;
        }
    }

    public Bitmap? FindDecoded(string sha, int width)
    {
        lock (gate)
        {
            return decoded.TryGetValue((sha, width), out var bitmap) ? bitmap : null;
        }
    }

    public Bitmap Decode(ZetlPictureContent picture, int width)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (decoded.TryGetValue((picture.Sha256, width), out var existing))
            {
                return existing;
            }
            using var stream = new MemoryStream(picture.Bytes, writable: false);
            var bitmap = Bitmap.DecodeToWidth(stream, width);
            decoded.Add((picture.Sha256, width), bitmap);
            return bitmap;
        }
    }

    public void Reset()
    {
        lock (gate)
        {
            Clear();
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            Clear();
        }
    }

    private void Clear()
    {
        generation++;
        loads.Clear();
        content.Clear();
        contentOrder.Clear();
        contentBytes = 0;
        foreach (var bitmap in decoded.Values)
        {
            bitmap.Dispose();
        }
        decoded.Clear();
    }
}
