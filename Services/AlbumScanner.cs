using PhotoTools2.Models;

namespace PhotoTools2.Services;

public static class AlbumScanner
{
    private static readonly HashSet<string> PhotoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jfif", ".png", ".tif", ".tiff", ".bmp", ".gif",
        ".webp", ".heic", ".heif", ".avif", ".dng"
    };

    public static IReadOnlyList<AlbumItem> ScanCollection(string collectionPath)
    {
        var albums = new List<AlbumItem>();
        foreach (var folder in Directory.EnumerateDirectories(collectionPath).OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase))
        {
            try
            {
                var files = Directory.EnumerateFiles(folder).ToArray();
                var croppedPath = Path.Combine(folder, "cropped");
                var convertedPath = Path.Combine(folder, "JPG");
                albums.Add(new AlbumItem
                {
                    Name = Path.GetFileName(folder),
                    Path = folder,
                    PhotoCount = files.Count(file => PhotoExtensions.Contains(Path.GetExtension(file))),
                    PngCount = files.Count(file => Path.GetExtension(file).Equals(".png", StringComparison.OrdinalIgnoreCase)),
                    CroppedCount = CountImages(croppedPath),
                    ConvertedCount = CountJpegs(convertedPath)
                });
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
        return albums;
    }

    private static int CountImages(string folder) => Directory.Exists(folder)
        ? Directory.EnumerateFiles(folder).Count(file => PhotoExtensions.Contains(Path.GetExtension(file)))
        : 0;

    private static int CountJpegs(string folder) => Directory.Exists(folder)
        ? Directory.EnumerateFiles(folder).Count(file =>
            Path.GetExtension(file).Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(file).Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        : 0;
}
