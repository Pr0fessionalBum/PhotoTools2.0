using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace PhotoTools2.Services;

public static class ThumbnailCacheService
{
    private const int DefaultDecodeWidth = 320;
    private const int MaximumEntries = 256;
    private static readonly object Sync = new();
    private static readonly Dictionary<string, CacheEntry> Entries = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<string> Usage = new();
    private static long _hits;
    private static long _misses;
    private static long _evictions;

    public static ImageSource? Get(string? path, long size, DateTime modified, int decodeWidth = DefaultDecodeWidth)
    {
        if (string.IsNullOrWhiteSpace(path) || decodeWidth <= 0) return null;
        var normalizedPath = NormalizePath(path);
        var signature = new FileSignature(size, modified.ToUniversalTime().Ticks, decodeWidth);

        lock (Sync)
        {
            if (Entries.TryGetValue(normalizedPath, out var existing))
            {
                if (existing.Signature == signature)
                {
                    _hits++;
                    Touch(existing);
                    return existing.Image;
                }

                Remove(existing);
            }

            _misses++;
            var image = new BitmapImage
            {
                UriSource = new Uri(normalizedPath),
                DecodePixelWidth = decodeWidth,
                CreateOptions = BitmapCreateOptions.IgnoreImageCache
            };
            var node = Usage.AddFirst(normalizedPath);
            Entries[normalizedPath] = new CacheEntry(normalizedPath, signature, image, node);
            Trim();
            return image;
        }
    }

    public static ImageSource? Get(string? path, int decodeWidth = DefaultDecodeWidth)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? Get(info.FullName, info.Length, info.LastWriteTime, decodeWidth) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public static void Invalidate(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var normalizedPath = NormalizePath(path);
        lock (Sync)
        {
            if (Entries.TryGetValue(normalizedPath, out var entry)) Remove(entry);
        }
    }

    public static void Clear()
    {
        lock (Sync)
        {
            Entries.Clear();
            Usage.Clear();
        }
    }

    public static ThumbnailCacheStatistics GetStatistics()
    {
        lock (Sync) return new ThumbnailCacheStatistics(Entries.Count, _hits, _misses, _evictions);
    }

    private static string NormalizePath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }

    private static void Touch(CacheEntry entry)
    {
        Usage.Remove(entry.UsageNode);
        Usage.AddFirst(entry.UsageNode);
    }

    private static void Trim()
    {
        while (Entries.Count > MaximumEntries && Usage.Last is { } oldest)
        {
            if (Entries.Remove(oldest.Value)) _evictions++;
            Usage.RemoveLast();
        }
    }

    private static void Remove(CacheEntry entry)
    {
        Entries.Remove(entry.Path);
        Usage.Remove(entry.UsageNode);
    }

    private readonly record struct FileSignature(long Size, long ModifiedUtcTicks, int DecodeWidth);
    private sealed record CacheEntry(string Path, FileSignature Signature, BitmapImage Image, LinkedListNode<string> UsageNode);
}

public readonly record struct ThumbnailCacheStatistics(int EntryCount, long Hits, long Misses, long Evictions)
{
    public double HitRate => Hits + Misses == 0 ? 0 : Hits * 100d / (Hits + Misses);
}
