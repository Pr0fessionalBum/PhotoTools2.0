using PhotoTools2.Models;

namespace PhotoTools2.Services;

public static class AlbumScanner
{
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
                    PhotoCount = files.Count(ImageFileFormats.IsLibraryPhoto),
                    PngCount = files.Count(ImageFileFormats.IsPng),
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
        ? Directory.EnumerateFiles(folder).Count(ImageFileFormats.IsLibraryPhoto)
        : 0;

    private static int CountJpegs(string folder) => Directory.Exists(folder)
        ? Directory.EnumerateFiles(folder).Count(ImageFileFormats.IsJpeg)
        : 0;
}
