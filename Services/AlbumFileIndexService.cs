using System.Collections.Concurrent;
using PhotoTools2.Models;

namespace PhotoTools2.Services;

public static class AlbumFileIndexService
{
    private static readonly ConcurrentDictionary<string, Lazy<Task<AlbumFileIndex>>> Indexes = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<AlbumFileIndex> GetAsync(string albumPath, CancellationToken token = default)
    {
        var root = Path.GetFullPath(albumPath);
        var lazy = Indexes.GetOrAdd(root, path => new Lazy<Task<AlbumFileIndex>>(
            () => BuildAsync(path), LazyThreadSafetyMode.ExecutionAndPublication));
        try { return await lazy.Value.WaitAsync(token); }
        catch
        {
            if (lazy.IsValueCreated && lazy.Value.IsFaulted) Indexes.TryRemove(new KeyValuePair<string, Lazy<Task<AlbumFileIndex>>>(root, lazy));
            throw;
        }
    }

    public static void Invalidate(string? albumPath)
    {
        if (string.IsNullOrWhiteSpace(albumPath)) return;
        try { Indexes.TryRemove(Path.GetFullPath(albumPath), out _); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
    }

    public static void Clear() => Indexes.Clear();

    private static Task<AlbumFileIndex> BuildAsync(string root) => Task.Run(() =>
    {
        var files = new List<string>();
        var directories = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            string[] children;
            string[] directoryFiles;
            try
            {
                children = Directory.GetDirectories(directory);
                directoryFiles = Directory.GetFiles(directory);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { continue; }
            files.AddRange(directoryFiles);
            foreach (var child in children)
            {
                directories.Add(child);
                pending.Push(child);
            }
        }
        files.Sort(StringComparer.CurrentCultureIgnoreCase);
        directories.Sort(StringComparer.CurrentCultureIgnoreCase);
        return new AlbumFileIndex(root, files, directories);
    });
}
