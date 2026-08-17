using Microsoft.UI;
using Microsoft.UI.Xaml.Media;

namespace PhotoTools2.Models;

public sealed class RenamePreviewItem
{
    public string SourceName { get; set; } = string.Empty;
    public string DestinationName { get; set; } = string.Empty;
    public string Detail { get; set; } = "Rename";
    public string Glyph { get; set; } = "\uE8AC";
    public SolidColorBrush StatusBrush { get; set; } = new(Colors.DodgerBlue);
}
