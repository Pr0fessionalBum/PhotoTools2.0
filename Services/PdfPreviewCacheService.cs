using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using PhotoTools2.Models;

namespace PhotoTools2.Services;

public static class PdfPreviewCacheService
{
    private const long MaximumBytes = 128L * 1024 * 1024;
    private const int MaximumEntries = 384;
    private const long MaximumDiskBytes = 512L * 1024 * 1024;
    private static readonly TimeSpan MaximumDiskAge = TimeSpan.FromDays(30);
    private static readonly string DiskCacheFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoTools2", "PdfPreviewCache", "v2");
    private static readonly object Sync = new();
    private static readonly Dictionary<PreviewKey, CacheEntry> Entries = [];
    private static readonly LinkedList<PreviewKey> Usage = [];
    private static readonly ConcurrentDictionary<PreviewKey, Lazy<Task<byte[]>>> InFlight = new();
    private static readonly SemaphoreSlim RenderSlots = new(2, 2);
    private static readonly Lazy<Task> DiskCleanup = new(CleanupDiskAsync, LazyThreadSafetyMode.ExecutionAndPublication);
    private static long _currentBytes;
    private static long _hits;
    private static long _misses;
    private static long _joinedRenders;
    private static long _evictions;
    private static long _diskHits;
    private static long _diskWrites;
    private static int _writesSinceCleanup;

    public static async Task<byte[]> GetOrRenderAsync(
        PdfDocumentSession session,
        uint pageIndex,
        uint maximumDimension,
        int rotationQuarterTurns,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        _ = DiskCleanup.Value;
        var key = CreateKey(session, pageIndex, maximumDimension, rotationQuarterTurns);
        lock (Sync)
        {
            RemoveStaleEntries(key);
            if (Entries.TryGetValue(key, out var cached))
            {
                _hits++;
                Touch(cached);
                return cached.Bytes;
            }
            _misses++;
        }

        var created = new Lazy<Task<byte[]>>(
            () => LoadDiskOrRenderAsync(key, session),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var pending = InFlight.GetOrAdd(key, created);
        if (!ReferenceEquals(pending, created)) Interlocked.Increment(ref _joinedRenders);
        try { return await pending.Value.WaitAsync(token); }
        finally
        {
            if (pending.IsValueCreated && pending.Value.IsCompleted)
                InFlight.TryRemove(new KeyValuePair<PreviewKey, Lazy<Task<byte[]>>>(key, pending));
        }
    }

    public static void Invalidate(string? pdfPath)
    {
        if (string.IsNullOrWhiteSpace(pdfPath)) return;
        var normalized = NormalizePath(pdfPath);
        lock (Sync)
        {
            foreach (var entry in Entries.Values.Where(entry => entry.Key.Path == normalized).ToArray()) Remove(entry);
        }
        _ = Task.Run(() => DeleteDiskEntries(GetPathHash(normalized) + "-"));
    }

    public static void Clear()
    {
        lock (Sync)
        {
            Entries.Clear(); Usage.Clear(); _currentBytes = 0;
        }
        try { if (Directory.Exists(DiskCacheFolder)) Directory.Delete(DiskCacheFolder, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static PdfPreviewCacheStatistics GetStatistics()
    {
        lock (Sync) return new PdfPreviewCacheStatistics(Entries.Count, _currentBytes, _hits, _misses, _joinedRenders, _evictions, _diskHits, _diskWrites);
    }

    private static async Task<byte[]> LoadDiskOrRenderAsync(PreviewKey key, PdfDocumentSession session)
    {
        var diskBytes = await TryReadDiskAsync(key);
        if (diskBytes is not null)
        {
            Interlocked.Increment(ref _diskHits);
            AddToMemory(key, diskBytes);
            return diskBytes;
        }
        return await RenderAndCacheAsync(key, session);
    }

    private static async Task<byte[]> RenderAndCacheAsync(PreviewKey key, PdfDocumentSession session)
    {
        await RenderSlots.WaitAsync();
        try
        {
            var bytes = await PdfConversionService.RenderPreviewAsync(
                session, key.PageIndex, key.MaximumDimension, key.RotationQuarterTurns, CancellationToken.None);
            AddToMemory(key, bytes);
            _ = WriteDiskSafelyAsync(key, bytes);
            return bytes;
        }
        finally { RenderSlots.Release(); }
    }

    private static void AddToMemory(PreviewKey key, byte[] bytes)
    {
        if (bytes.LongLength > MaximumBytes) return;
        lock (Sync)
        {
            if (Entries.ContainsKey(key)) return;
            var node = Usage.AddFirst(key);
            Entries[key] = new CacheEntry(key, bytes, node);
            _currentBytes += bytes.LongLength;
            Trim();
        }
    }

    private static async Task<byte[]?> TryReadDiskAsync(PreviewKey key)
    {
        var path = GetDiskPath(key);
        try
        {
            if (!File.Exists(path)) return null;
            var bytes = await File.ReadAllBytesAsync(path);
            if (!HasPngSignature(bytes)) { File.Delete(path); return null; }
            try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch (IOException) { }
            return bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static async Task WriteDiskSafelyAsync(PreviewKey key, byte[] bytes)
    {
        var destination = GetDiskPath(key);
        var temporary = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(DiskCacheFolder);
            await File.WriteAllBytesAsync(temporary, bytes);
            File.Move(temporary, destination, true);
            Interlocked.Increment(ref _diskWrites);
            if (Interlocked.Increment(ref _writesSinceCleanup) >= 64)
            {
                Interlocked.Exchange(ref _writesSinceCleanup, 0);
                await CleanupDiskAsync();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static string GetDiskPath(PreviewKey key)
    {
        var pathHash = GetPathHash(key.Path);
        var value = $"{key.Path}|{key.SourceSize}|{key.SourceModifiedUtcTicks}|{key.PageIndex}|{key.MaximumDimension}|{key.RotationQuarterTurns}";
        var keyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        return Path.Combine(DiskCacheFolder, $"{pathHash}-{keyHash}.png");
    }

    private static string GetPathHash(string normalizedPath) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath)))[..16];

    private static bool HasPngSignature(byte[] bytes) => bytes.Length >= 24
        && bytes[0] == 137 && bytes[1] == 80 && bytes[2] == 78 && bytes[3] == 71
        && bytes[4] == 13 && bytes[5] == 10 && bytes[6] == 26 && bytes[7] == 10
        && (bytes[16] != 0 || bytes[17] != 0 || bytes[18] != 0 || bytes[19] != 0)
        && (bytes[20] != 0 || bytes[21] != 0 || bytes[22] != 0 || bytes[23] != 0);

    private static async Task CleanupDiskAsync()
    {
        await Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(DiskCacheFolder)) return;
                var cutoff = DateTime.UtcNow - MaximumDiskAge;
                var files = Directory.EnumerateFiles(DiskCacheFolder, "*.png").Select(path => new FileInfo(path)).ToArray();
                foreach (var file in files.Where(file => file.LastWriteTimeUtc < cutoff))
                {
                    try { file.Delete(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
                files = Directory.EnumerateFiles(DiskCacheFolder, "*.png").Select(path => new FileInfo(path))
                    .OrderByDescending(file => file.LastWriteTimeUtc).ToArray();
                var retainedBytes = 0L;
                foreach (var file in files)
                {
                    retainedBytes += file.Length;
                    if (retainedBytes <= MaximumDiskBytes) continue;
                    try { file.Delete(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        });
    }

    private static void DeleteDiskEntries(string prefix)
    {
        try
        {
            if (!Directory.Exists(DiskCacheFolder)) return;
            foreach (var file in Directory.EnumerateFiles(DiskCacheFolder, prefix + "*.png"))
                try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static PreviewKey CreateKey(PdfDocumentSession session, uint pageIndex, uint maximumDimension, int rotationQuarterTurns) => new(
        NormalizePath(session.SourcePath),
        session.SourceSize,
        session.SourceModifiedUtc.Ticks,
        pageIndex,
        maximumDimension,
        ((rotationQuarterTurns % 4) + 4) % 4);

    private static string NormalizePath(string path) => Path.GetFullPath(path).ToUpperInvariant();

    private static void RemoveStaleEntries(PreviewKey requested)
    {
        foreach (var entry in Entries.Values.Where(entry => entry.Key.Path == requested.Path
            && (entry.Key.SourceSize != requested.SourceSize || entry.Key.SourceModifiedUtcTicks != requested.SourceModifiedUtcTicks)).ToArray())
            Remove(entry);
    }

    private static void Touch(CacheEntry entry)
    {
        Usage.Remove(entry.UsageNode);
        Usage.AddFirst(entry.UsageNode);
    }

    private static void Trim()
    {
        while ((Entries.Count > MaximumEntries || _currentBytes > MaximumBytes) && Usage.Last is { } oldest)
        {
            if (Entries.TryGetValue(oldest.Value, out var entry)) { Remove(entry); _evictions++; }
            else Usage.RemoveLast();
        }
    }

    private static void Remove(CacheEntry entry)
    {
        if (!Entries.Remove(entry.Key)) return;
        Usage.Remove(entry.UsageNode);
        _currentBytes -= entry.Bytes.LongLength;
    }

    private readonly record struct PreviewKey(string Path, long SourceSize, long SourceModifiedUtcTicks, uint PageIndex, uint MaximumDimension, int RotationQuarterTurns);
    private sealed record CacheEntry(PreviewKey Key, byte[] Bytes, LinkedListNode<PreviewKey> UsageNode);
}

public readonly record struct PdfPreviewCacheStatistics(int EntryCount, long Bytes, long Hits, long Misses, long JoinedRenders, long Evictions, long DiskHits, long DiskWrites)
{
    public double HitRate => Hits + Misses == 0 ? 0 : Hits * 100d / (Hits + Misses);
}
