using Avalonia.Media.Imaging;
using ZETL.Contracts;

namespace KASTN;

// Owns image content, in-flight fetches, and decoded bitmap lifetime. Bitmap
// assignment stays on the UI thread; cache-miss decoding uses one background worker.
// Displayed bitmaps hold leases. The decoded budget evicts only idle images;
// a reset retires leased images until their last control releases them.
internal sealed class KastnPictureCache : IDisposable
{
    private const long DefaultContentBudget = 128L * 1024 * 1024;
    private const long DefaultDecodedBudget = 64L * 1024 * 1024;
    private readonly Func<string, ZetlSlipSnapshot, Task<ZetlPictureContent?>> fetch;
    private readonly Func<ZetlPictureContent, int, Bitmap> decode;
    private readonly long contentBudget;
    private readonly long decodedBudget;
    private readonly object gate = new();
    private readonly Dictionary<string, ZetlPictureContent> content = new(StringComparer.Ordinal);
    private readonly Queue<string> contentOrder = [];
    private readonly Dictionary<(long Generation, string Project, string Sha), Task<ZetlPictureContent?>> loads = new();
    private readonly Dictionary<(string Sha, int Width), DecodedEntry> decoded = new();
    private readonly HashSet<DecodedEntry> ownedDecoded = [];
    private readonly LinkedList<DecodedEntry> idleDecoded = new();
    private readonly SemaphoreSlim decodeSlot = new(1, 1);
    private long decodedBytes;
    private long decodeCount;
    private long decodedEvictions;
    private long contentBytes;
    private long generation;
    private bool disposed;

    public KastnPictureCache(
        Func<string, ZetlSlipSnapshot, Task<ZetlPictureContent?>> fetch,
        long contentBudget = DefaultContentBudget,
        long decodedBudget = DefaultDecodedBudget,
        Func<ZetlPictureContent, int, Bitmap>? decode = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(contentBudget);
        ArgumentOutOfRangeException.ThrowIfNegative(decodedBudget);
        this.fetch = fetch;
        this.contentBudget = contentBudget;
        this.decodedBudget = decodedBudget;
        this.decode = decode ?? DecodeBitmap;
    }

    internal sealed class DecodedEntry((string Sha, int Width) key, Bitmap bitmap)
    {
        public (string Sha, int Width) Key { get; } = key;
        public Bitmap Bitmap { get; } = bitmap;
        public long Bytes { get; } = (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4;
        public int References { get; set; }
        public bool Retired { get; set; }
        public LinkedListNode<DecodedEntry>? Idle { get; set; }
    }

    internal sealed class DecodedLease : IDisposable
    {
        private KastnPictureCache? owner;
        private readonly DecodedEntry entry;
        internal DecodedLease(KastnPictureCache owner, DecodedEntry entry) { this.owner = owner; this.entry = entry; }
        public Bitmap Bitmap => owner is not null ? entry.Bitmap : throw new ObjectDisposedException(nameof(DecodedLease));
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release(entry);
    }

    internal long DecodedResidentBytes { get { lock (gate) return decodedBytes; } }
    internal long PinnedDecodedBytes { get { lock (gate) return ownedDecoded.Where(entry => entry.References > 0).Sum(entry => entry.Bytes); } }
    internal int DecodedCount { get { lock (gate) return ownedDecoded.Count; } }
    internal long DecodeCount { get { lock (gate) return decodeCount; } }
    internal long DecodedEvictions { get { lock (gate) return decodedEvictions; } }

    internal static bool IsLoadFailure(Exception ex) =>
        ex is IOException or InvalidOperationException or OperationCanceledException
            or ArgumentException or NotSupportedException;

    public Task<ZetlPictureContent?> GetContentAsync(string projectId, ZetlSlipSnapshot slip) =>
        GetContentAsync(projectId, slip, allowRetiredGeneration: false);

    // An export renders its captured snapshot even after navigation resets the
    // preview cache. Share fetches, but do not put retired results back in the cache.
    public Task<ZetlPictureContent?> GetCapturedContentAsync(string projectId, ZetlSlipSnapshot slip) =>
        GetContentAsync(projectId, slip, allowRetiredGeneration: true);

    private async Task<ZetlPictureContent?> GetContentAsync(
        string projectId, ZetlSlipSnapshot slip, bool allowRetiredGeneration)
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
            var picture = await loading.ConfigureAwait(false);
            lock (gate)
            {
                return disposed || generation != key.Generation && !allowRetiredGeneration ? null : picture;
            }
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
                return picture;
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

    // Diagnostic lookup; controls must acquire a lease before displaying an image.
    public Bitmap? FindDecoded(string sha, int width)
    {
        lock (gate)
        {
            return decoded.TryGetValue((sha, width), out var entry) ? entry.Bitmap : null;
        }
    }

    public DecodedLease? TryAcquireDecoded(string sha, int width)
    {
        lock (gate)
        {
            return !disposed && decoded.TryGetValue((sha, width), out var entry) ? Acquire(entry) : null;
        }
    }

    public DecodedLease AcquireDecoded(ZetlPictureContent picture, int width)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (decoded.TryGetValue((picture.Sha256, width), out var existing))
            {
                return Acquire(existing);
            }
            var bitmap = decode(picture, width);
            return AddDecoded((picture.Sha256, width), bitmap);
        }
    }

    public async Task<DecodedLease> AcquireDecodedAsync(ZetlPictureContent picture, int width, CancellationToken cancellationToken)
    {
        long capturedGeneration;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (decoded.TryGetValue((picture.Sha256, width), out var cached)) return Acquire(cached);
            capturedGeneration = generation;
        }
        await decodeSlot.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (gate)
            {
                CheckCurrent();
                if (decoded.TryGetValue((picture.Sha256, width), out var cached)) return Acquire(cached);
            }
            var bitmap = await Task.Run(() => decode(picture, width), cancellationToken).ConfigureAwait(false);
            lock (gate)
            {
                try { CheckCurrent(); }
                catch { bitmap.Dispose(); throw; }
                if (decoded.TryGetValue((picture.Sha256, width), out var existing))
                {
                    bitmap.Dispose();
                    return Acquire(existing);
                }
                return AddDecoded((picture.Sha256, width), bitmap);
            }
        }
        finally { decodeSlot.Release(); }

        void CheckCurrent()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (disposed || generation != capturedGeneration) throw new OperationCanceledException("Picture cache was retired.");
        }
    }

    private static Bitmap DecodeBitmap(ZetlPictureContent picture, int width)
    {
        using var stream = new MemoryStream(picture.Bytes, writable: false);
        return Bitmap.DecodeToWidth(stream, width);
    }

    // Called under the cache gate; acquire before trimming so a new image is pinned.
    private DecodedLease AddDecoded((string Sha, int Width) key, Bitmap bitmap)
    {
        var entry = new DecodedEntry(key, bitmap);
        decoded.Add(key, entry);
        ownedDecoded.Add(entry);
        decodedBytes += entry.Bytes;
        decodeCount++;
        var lease = Acquire(entry);
        TrimDecoded();
        return lease;
    }

    private DecodedLease Acquire(DecodedEntry entry)
    {
        if (entry.Idle is { } idle) { idleDecoded.Remove(idle); entry.Idle = null; }
        entry.References++;
        return new(this, entry);
    }

    private void Release(DecodedEntry entry)
    {
        lock (gate)
        {
            if (--entry.References != 0) return;
            if (entry.Retired) DisposeDecoded(entry);
            else
            {
                entry.Idle = idleDecoded.AddLast(entry);
                TrimDecoded();
            }
        }
    }

    private void TrimDecoded()
    {
        while (decodedBytes > decodedBudget && idleDecoded.First is { } oldest)
        {
            var entry = oldest.Value;
            decoded.Remove(entry.Key);
            DisposeDecoded(entry);
            decodedEvictions++;
        }
    }

    private void DisposeDecoded(DecodedEntry entry)
    {
        if (entry.Idle is { } idle) { idleDecoded.Remove(idle); entry.Idle = null; }
        if (!ownedDecoded.Remove(entry)) return;
        decodedBytes -= entry.Bytes;
        entry.Bitmap.Dispose();
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
        foreach (var entry in decoded.Values)
        {
            entry.Retired = true;
            if (entry.Idle is { } idle) { idleDecoded.Remove(idle); entry.Idle = null; }
            if (entry.References == 0) DisposeDecoded(entry);
        }
        decoded.Clear();
    }
}
