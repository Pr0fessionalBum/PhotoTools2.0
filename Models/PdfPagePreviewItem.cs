using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media;

namespace PhotoTools2.Models;

public sealed class PdfPagePreviewItem(PdfPageEdit edit) : INotifyPropertyChanged
{
    private ImageSource? _thumbnail;
    public PdfPageEdit Edit { get; } = edit;
    public uint PageIndex => Edit.PageIndex;
    public string PageLabel => $"Page {PageIndex + 1:N0}";
    public string RotationLabel => Edit.RotationDegrees == 0 ? "Original orientation" : $"{Edit.RotationDegrees}° clockwise";
    public ImageSource? Thumbnail { get => _thumbnail; set { _thumbnail = value; OnPropertyChanged(); } }
    public bool IsIncluded { get => Edit.Include; set { if (Edit.Include == value) return; Edit.Include = value; OnPropertyChanged(); } }
    public event PropertyChangedEventHandler? PropertyChanged;

    public void RotateLeft() { Edit.RotateLeft(); OnPropertyChanged(nameof(RotationLabel)); }
    public void RotateRight() { Edit.RotateRight(); OnPropertyChanged(nameof(RotationLabel)); }
    public void ResetRotation() { Edit.ResetRotation(); OnPropertyChanged(nameof(RotationLabel)); }
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
