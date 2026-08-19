namespace PhotoTools2.Services;

public static class ImageFileFormats
{
    private static readonly HashSet<string> LibraryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jfif", ".png", ".tif", ".tiff", ".bmp", ".gif",
        ".webp", ".heic", ".heif", ".avif", ".dng"
    };

    private static readonly HashSet<string> CommonExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp"
    };

    private static readonly HashSet<string> EditableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg"
    };

    private static readonly HashSet<string> AnalysisExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff"
    };

    public static bool IsLibraryPhoto(string path) => LibraryExtensions.Contains(Path.GetExtension(path));
    public static bool IsCommonImage(string path) => CommonExtensions.Contains(Path.GetExtension(path));
    public static bool IsEditableImage(string path) => EditableExtensions.Contains(Path.GetExtension(path));
    public static bool IsAnalysisImage(string path) => AnalysisExtensions.Contains(Path.GetExtension(path));
    public static bool IsPng(string path) => HasExtension(path, ".png");
    public static bool IsJpeg(string path) => HasExtension(path, ".jpg") || HasExtension(path, ".jpeg");

    private static bool HasExtension(string path, string extension) =>
        Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase);
}
