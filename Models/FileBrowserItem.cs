using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media;
using PhotoTools2.Services;

namespace PhotoTools2.Models;

public sealed class FileBrowserItem : INotifyPropertyChanged
{
    private int _pendingRotationQuarterTurns;
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public bool IsFolder { get; set; }
    public bool IsImage { get; set; }
    public long Size { get; set; }
    public DateTime Modified { get; set; }
    public string Extension => System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant();
    public ImageSource? ThumbnailUri => IsImage
        ? ThumbnailCacheService.Get(Path, Size, Modified)
        : null;
    public int PendingRotationQuarterTurns
    {
        get => _pendingRotationQuarterTurns;
        set
        {
            var normalized = ((value % 4) + 4) % 4;
            if (_pendingRotationQuarterTurns == normalized) return;
            _pendingRotationQuarterTurns = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PendingRotationDegrees));
            OnPropertyChanged(nameof(PendingRotationLabel));
        }
    }
    public double PendingRotationDegrees => PendingRotationQuarterTurns * 90d;
    public string PendingRotationLabel => PendingRotationQuarterTurns == 0 ? string.Empty : $"Pending: {PendingRotationQuarterTurns * 90}°";
    public string FallbackGlyph => IsFolder ? "\uE8B7" : "\uE7C3";
    public string Details => IsFolder ? "Folder" : FormatSize(Size);

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824d:0.0} GB",
        >= 1_048_576 => $"{bytes / 1_048_576d:0.0} MB",
        >= 1024 => $"{bytes / 1024d:0} KB",
        _ => $"{bytes} B"
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    public void RefreshThumbnail() => OnPropertyChanged(nameof(ThumbnailUri));
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
