using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace PhotoTools2.Models;

public sealed class FileBrowserItem
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public bool IsFolder { get; set; }
    public bool IsImage { get; set; }
    public long Size { get; set; }
    public DateTime Modified { get; set; }
    public string Extension => System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant();
    public ImageSource? ThumbnailUri => IsImage ? new BitmapImage
    {
        UriSource = new Uri(Path),
        DecodePixelWidth = 320,
        CreateOptions = BitmapCreateOptions.IgnoreImageCache
    } : null;
    public string FallbackGlyph => IsFolder ? "\uE8B7" : "\uE7C3";
    public string Details => IsFolder ? "Folder" : FormatSize(Size);

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824d:0.0} GB",
        >= 1_048_576 => $"{bytes / 1_048_576d:0.0} MB",
        >= 1024 => $"{bytes / 1024d:0} KB",
        _ => $"{bytes} B"
    };
}
