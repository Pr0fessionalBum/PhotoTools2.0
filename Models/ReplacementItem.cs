using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace PhotoTools2.Models;

public sealed class ReplacementItem
{
    public string Name { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string DestinationPath { get; set; } = string.Empty;
    public string Stage { get; set; } = string.Empty;
    public bool ReplacesPng { get; set; }
    public string? OriginalPath { get; set; }
    public string MatchStatus { get; set; } = "No matching original";
    public ImageSource? Thumbnail => string.IsNullOrWhiteSpace(SourcePath)
        ? null
        : new BitmapImage(new Uri(SourcePath));
}
