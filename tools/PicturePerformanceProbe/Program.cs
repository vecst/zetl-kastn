using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ZETL.Contracts;

// Native decoding only: no windows, project stores, host, IPC or user settings.
// Reflection lets the same harness measure the baseline and lease-based cache.
internal static class Entry
{
    [STAThread]
    public static int Main(string[] args)
    {
        AppBuilder.Configure<Application>().UsePlatformDetect().SetupWithoutStarting();
        var count = args.Length > 0 ? int.Parse(args[0]) : 96;
        if (count is < 8 or > 300) throw new ArgumentOutOfRangeException(nameof(count));
        var pictures = Fixtures(count);
        Console.WriteLine($"Native Skia decode probe: {count} unique synthetic 2048x1536 PNGs; encoded {pictures.Sum(p => p.Bytes.LongLength) / 1048576d:F1} MiB.");
        var cacheType = Assembly.Load("Kastn").GetType("KASTN.KastnPictureCache")!;
        Func<string, ZetlSlipSnapshot, Task<ZetlPictureContent?>> fetch = (_, slip) =>
            Task.FromResult<ZetlPictureContent?>(pictures[int.Parse(slip.Id)]);
        var constructor = cacheType.GetConstructors().Single();
        var parameters = constructor.GetParameters().Select((p, i) => i == 0 ? (object)fetch : p.DefaultValue).ToArray();
        using var cache = (IDisposable)constructor.Invoke(parameters);
        var acquire = cacheType.GetMethod("AcquireDecoded") ?? cacheType.GetMethod("Decode")!;
        var acquireAsync = cacheType.GetMethod("AcquireDecodedAsync");
        var getContent = cacheType.GetMethod("GetContentAsync", [typeof(string), typeof(ZetlSlipSnapshot)])!;
        var live = new Queue<IDisposable>();
        var decodes = new List<double>();
        var fetches = new List<double>();
        var submissions = new List<double>();
        object Acquire(ZetlPictureContent content, int width)
        {
            var started = Stopwatch.GetTimestamp();
            if (acquireAsync is null)
            {
                var result = acquire.Invoke(cache, [content, width])!;
                submissions.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                return result;
            }
            var task = (Task)acquireAsync.Invoke(cache, [content, width, CancellationToken.None])!;
            submissions.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            task.GetAwaiter().GetResult();
            return task.GetType().GetProperty("Result")!.GetValue(task)!;
        }
        var process = Process.GetCurrentProcess();
        void Sample(string label)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            process.Refresh();
            var entries = (IDictionary)cacheType.GetField("decoded", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!;
            long bytes = 0;
            foreach (var value in entries.Values)
            {
                var bitmap = value as Bitmap ?? (Bitmap)value!.GetType().GetProperty("Bitmap")!.GetValue(value)!;
                bytes += (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4;
            }
            Console.WriteLine($"{label}: decoded {entries.Count} / {bytes / 1048576d:F1} MiB; private {process.PrivateMemorySize64 / 1048576d:F1} MiB; managed {GC.GetTotalMemory(false) / 1048576d:F1} MiB.");
        }
        Sample("Before browsing");
        for (var i = 0; i < pictures.Count; i++)
        {
            var picture = pictures[i];
            var slip = new ZetlSlipSnapshot { Id = i.ToString(), Revision = 1, Type = ZetlSlipType.Picture,
                BucketId = "probe", Text = "", Source = "probe", CapturedAtUtc = DateTimeOffset.UnixEpoch,
                Picture = new() { Sha256 = picture.Sha256, Width = picture.Width, Height = picture.Height } };
            var watch = Stopwatch.StartNew();
            var content = ((Task<ZetlPictureContent?>)getContent.Invoke(cache, ["probe", slip])!).GetAwaiter().GetResult()!;
            fetches.Add(watch.Elapsed.TotalMilliseconds);
            foreach (var width in new[] { 1100, 260 })
            {
                watch.Restart();
                var result = Acquire(content, width);
                decodes.Add(watch.Elapsed.TotalMilliseconds);
                if (result is IDisposable lease && result is not Bitmap) live.Enqueue(lease);
            }
            while (live.Count > 8) live.Dequeue().Dispose();
        }
        Sample("After browsing (four pictures live)");
        while (live.TryDequeue(out var lease)) lease.Dispose();
        Sample("After releasing controls");
        foreach (var i in new[] { count - 1, count - 2, 0, 1 })
        {
            var watch = Stopwatch.StartNew();
            var result = Acquire(pictures[i], 1100);
            Console.WriteLine($"Revisit {i}: {watch.Elapsed.TotalMilliseconds:F1} ms.");
            if (result is IDisposable lease && result is not Bitmap) lease.Dispose();
        }
        Console.WriteLine($"Fetch median {fetches.Order().ElementAt(fetches.Count / 2):F2} ms; decode median {decodes.Order().ElementAt(decodes.Count / 2):F1} ms; max {decodes.Max():F1} ms (includes cold JIT).");
        Console.WriteLine($"Decode call return median {submissions.Order().ElementAt(submissions.Count / 2):F2} ms; max {submissions.Max():F1} ms (waiting for background completion excluded).");
        cacheType.GetMethod("Reset")!.Invoke(cache, null);
        Sample("After reset");
        return 0;
    }

    private static IReadOnlyList<ZetlPictureContent> Fixtures(int count)
    {
        const int width = 2048, height = 1536;
        using var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using (var pixels = bitmap.Lock())
        {
            var row = new byte[width * 4];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    row[x * 4] = (byte)x; row[x * 4 + 1] = (byte)y;
                    row[x * 4 + 2] = (byte)(x + y); row[x * 4 + 3] = 255;
                }
                Marshal.Copy(row, 0, pixels.Address + y * pixels.RowBytes, row.Length);
            }
        }
        var result = new List<ZetlPictureContent>();
        for (var i = 0; i < count; i++)
        {
            using (var pixels = bitmap.Lock()) Marshal.WriteInt32(pixels.Address, unchecked((int)0xff000000) | i);
            using var stream = new MemoryStream();
            bitmap.Save(stream);
            var bytes = stream.ToArray();
            result.Add(new() { SlipId = i.ToString(), Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), Width = width, Height = height, Bytes = bytes });
        }
        return result;
    }
}
