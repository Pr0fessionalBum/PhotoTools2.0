namespace PhotoTools2.Models;

public sealed record AlbumFileIndex(string RootPath, IReadOnlyList<string> Files, IReadOnlyList<string> Directories)
{
    public IEnumerable<string> FilesUnder(string directory, bool recursive = true)
    {
        var prefix = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Files.Where(file => file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && (recursive || string.Equals(Path.GetDirectoryName(file), directory, StringComparison.OrdinalIgnoreCase)));
    }
}
