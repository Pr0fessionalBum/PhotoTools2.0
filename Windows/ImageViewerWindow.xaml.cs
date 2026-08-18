using System.Diagnostics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using PhotoTools2.Models;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace PhotoTools2.Viewer;

public sealed partial class ImageViewerWindow : Window
{
    private string[] _paths = [];
    private int _index;
    private BitmapImage? _currentBitmap;
    private ScannerLineResult[] _scannerResults = [];
    private bool _comparisonMode;
    private bool _synchronizingViews;
    private ScrollViewer? _activeDragScroller;
    private int _decodedWidth;
    private int _decodedHeight;
    private uint _sourcePixelWidth;
    private uint _sourcePixelHeight;
    private int _rotationQuarterTurns;
    private bool _updatingZoom;
    private bool _dragging;
    private global::Windows.Foundation.Point _dragStart;
    private double _dragHorizontal;
    private double _dragVertical;

    public ImageViewerWindow()
    {
        InitializeComponent();
        Title = "Photo Tools 2.0 Viewer";
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        ImageScroller.ViewChanged += ImageScroller_ViewChanged;
        ImageScroller.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(ImageSurface_PointerPressed), true);
        ImageScroller.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(ImageSurface_PointerMoved), true);
        ImageScroller.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(ImageSurface_PointerEnded), true);
        ImageScroller.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(ImageSurface_PointerEnded), true);
        ImageScroller.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(ImageScroller_PointerWheelChanged), true);
        HighlightScroller.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(ImageSurface_PointerPressed), true);
        HighlightScroller.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(ImageSurface_PointerMoved), true);
        HighlightScroller.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(ImageSurface_PointerEnded), true);
        HighlightScroller.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(ImageSurface_PointerEnded), true);
        HighlightScroller.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(ImageScroller_PointerWheelChanged), true);
        HighlightScroller.ViewChanged += HighlightScroller_ViewChanged;
        RootGrid.Loaded += (_, _) => RootGrid.Focus(FocusState.Programmatic);
    }

    public void ShowImages(IReadOnlyList<string> paths, int selectedIndex)
    {
        _comparisonMode = false;
        _scannerResults = [];
        HighlightedPane.Visibility = Visibility.Collapsed;
        EditToolbar.Visibility = Visibility.Visible;
        Grid.SetColumnSpan(OriginalPane, 2);
        OriginalPaneLabel.Text = "Image";
        _paths = paths.Where(File.Exists).ToArray();
        if (_paths.Length == 0) return;
        _index = Math.Clamp(selectedIndex, 0, _paths.Length - 1);
        _ = LoadCurrentAsync(true);
    }

    public void ShowScannerComparisons(IReadOnlyList<ScannerLineResult> results, int selectedIndex)
    {
        _scannerResults = results.Where(result => File.Exists(result.Photo.Path)).ToArray();
        if (_scannerResults.Length == 0) return;
        _comparisonMode = true;
        _paths = _scannerResults.Select(result => result.Photo.Path).ToArray();
        _index = Math.Clamp(selectedIndex, 0, _paths.Length - 1);
        Grid.SetColumnSpan(OriginalPane, 1);
        HighlightedPane.Visibility = Visibility.Visible;
        EditToolbar.Visibility = Visibility.Collapsed;
        OriginalPaneLabel.Text = "Original";
        _ = LoadCurrentAsync(true);
    }

    private async Task LoadCurrentAsync(bool resizeWindow)
    {
        if (_paths.Length == 0) return;
        var path = _paths[_index];
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var pixelWidth = Math.Max(1u, decoder.PixelWidth);
            var pixelHeight = Math.Max(1u, decoder.PixelHeight);
            _sourcePixelWidth = pixelWidth;
            _sourcePixelHeight = pixelHeight;
            var decodeScale = Math.Min(1d, 3072d / Math.Max(pixelWidth, pixelHeight));
            var decodedWidth = Math.Max(1, (int)Math.Round(pixelWidth * decodeScale));
            var decodedHeight = Math.Max(1, (int)Math.Round(pixelHeight * decodeScale));
            _decodedWidth = decodedWidth;
            _decodedHeight = decodedHeight;
            _rotationQuarterTurns = 0;
            ViewerTransform.Rotation = 0;
            ViewerTransform.TranslateX = 0;
            ViewerTransform.TranslateY = 0;
            ResetEditsButton.IsEnabled = false;
            SaveChangesButton.IsEnabled = false;
            EditStatusText.Text = "No pending edits";

            ViewerImage.Source = null;
            _currentBitmap = new BitmapImage
            {
                UriSource = new Uri(path),
                DecodePixelWidth = decodedWidth,
                CreateOptions = BitmapCreateOptions.IgnoreImageCache
            };
            ViewerImage.Width = decodedWidth;
            ViewerImage.Height = decodedHeight;
            ImageSurface.Width = decodedWidth;
            ImageSurface.Height = decodedHeight;
            ViewerImage.Source = _currentBitmap;
            HighlightSurface.Width = decodedWidth;
            HighlightSurface.Height = decodedHeight;
            HighlightImage.Width = decodedWidth;
            HighlightImage.Height = decodedHeight;
            HighlightImage.Source = _comparisonMode ? _currentBitmap : null;
            if (_comparisonMode && _index < _scannerResults.Length)
            {
                var result = _scannerResults[_index];
                if (result.IsHorizontal)
                {
                    LineMarker.Width = decodedWidth; LineMarker.Height = 4; LineMarker.HorizontalAlignment = HorizontalAlignment.Left; LineMarker.VerticalAlignment = VerticalAlignment.Top;
                    LineMarker.Margin = new Thickness(0, decodedHeight * result.LinePosition - 2, 0, 0);
                }
                else
                {
                    LineMarker.Width = 4; LineMarker.Height = decodedHeight; LineMarker.HorizontalAlignment = HorizontalAlignment.Left; LineMarker.VerticalAlignment = VerticalAlignment.Top;
                    LineMarker.Margin = new Thickness(decodedWidth * result.LinePosition - 2, 0, 0, 0);
                }
            }
            FileNameText.Text = file.Name;
            Title = $"{file.Name} — Photo Tools 2.0 Viewer";
            var info = new FileInfo(path);
            DetailsText.Text = _comparisonMode && _index < _scannerResults.Length
                ? $"{pixelWidth:N0} × {pixelHeight:N0} • {_scannerResults[_index].PositionLabel} • {_scannerResults[_index].ConfidenceLabel} • Read-only"
                : $"{pixelWidth:N0} × {pixelHeight:N0} • {FormatSize(info.Length)} • Read-only";
            PositionText.Text = $"{_index + 1:N0} of {_paths.Length:N0}";
            PreviousButton.IsEnabled = _index > 0;
            NextButton.IsEnabled = _index < _paths.Length - 1;
            if (resizeWindow) ResizeForImage(pixelWidth, pixelHeight);
            await Task.Delay(35);
            FitImage();
            if (resizeWindow) Activate();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            DetailsText.Text = $"Could not load this image: {ex.Message}";
        }
    }

    private void ResizeForImage(uint imageWidth, uint imageHeight)
    {
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var work = display.WorkArea;
        var maxWidth = Math.Max(760, (int)(work.Width * 0.9));
        var maxHeight = Math.Max(560, (int)(work.Height * 0.9));
        var naturalWidth = _comparisonMode ? (long)imageWidth * 2 + 120 : imageWidth + 70L;
        var desiredWidth = Math.Clamp((int)Math.Min(int.MaxValue, naturalWidth), 760, maxWidth);
        var desiredHeight = Math.Clamp((int)imageHeight + 175, 560, maxHeight);
        AppWindow.Resize(new global::Windows.Graphics.SizeInt32(desiredWidth, desiredHeight));
        AppWindow.Move(new global::Windows.Graphics.PointInt32(work.X + (work.Width - desiredWidth) / 2, work.Y + (work.Height - desiredHeight) / 2));
    }

    private void Previous_Click(object sender, RoutedEventArgs e) => Navigate(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => Navigate(1);
    private void Navigate(int offset)
    {
        var next = _index + offset;
        if (next < 0 || next >= _paths.Length) return;
        _index = next;
        _ = LoadCurrentAsync(false);
    }

    private void Fit_Click(object sender, RoutedEventArgs e) => FitImage();
    private void ActualSize_Click(object sender, RoutedEventArgs e) => SetZoom(1f);
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(Math.Min(10f, ImageScroller.ZoomFactor * 1.25f));
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(Math.Max(0.1f, ImageScroller.ZoomFactor / 1.25f));

    private void FitImage()
    {
        if (ImageSurface.Width <= 0 || ImageSurface.Height <= 0 || ImageScroller.ViewportWidth <= 0 || ImageScroller.ViewportHeight <= 0) return;
        var factor = (float)Math.Min(1d, Math.Min(ImageScroller.ViewportWidth / ImageSurface.Width, ImageScroller.ViewportHeight / ImageSurface.Height));
        SetZoom(Math.Max(0.1f, factor));
        ImageScroller.ChangeView(0, 0, null, true);
        if (_comparisonMode) HighlightScroller.ChangeView(0, 0, null, true);
    }

    private void SetZoom(float factor)
    {
        ImageScroller.ChangeView(null, null, factor, true);
        if (_comparisonMode) HighlightScroller.ChangeView(null, null, factor, true);
        UpdateZoomDisplay(factor);
    }

    private void ImageScroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        UpdateZoomDisplay(ImageScroller.ZoomFactor);
        if (_comparisonMode) MirrorView(ImageScroller, HighlightScroller);
    }
    private void HighlightScroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_comparisonMode) { UpdateZoomDisplay(HighlightScroller.ZoomFactor); MirrorView(HighlightScroller, ImageScroller); }
    }
    private void MirrorView(ScrollViewer source, ScrollViewer target)
    {
        if (_synchronizingViews) return;
        _synchronizingViews = true;
        target.ChangeView(source.HorizontalOffset, source.VerticalOffset, source.ZoomFactor, true);
        _synchronizingViews = false;
    }
    private void UpdateZoomDisplay(float factor)
    {
        _updatingZoom = true;
        ZoomSlider.Value = Math.Clamp(factor * 100d, ZoomSlider.Minimum, ZoomSlider.Maximum);
        ZoomText.Text = $"{factor:P0}";
        _updatingZoom = false;
    }

    private void ZoomSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_updatingZoom && ImageScroller is not null) ImageScroller.ChangeView(null, null, (float)(e.NewValue / 100d), true);
    }

    private void ImageSurface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var scroller = sender as ScrollViewer ?? ImageScroller;
        var point = e.GetCurrentPoint(scroller);
        if (!point.Properties.IsLeftButtonPressed && !point.Properties.IsMiddleButtonPressed) return;
        _activeDragScroller = scroller;
        _dragging = true; _dragStart = point.Position; _dragHorizontal = scroller.HorizontalOffset; _dragVertical = scroller.VerticalOffset;
        scroller.CapturePointer(e.Pointer); e.Handled = true;
    }

    private void ImageSurface_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        var scroller = _activeDragScroller ?? ImageScroller;
        var point = e.GetCurrentPoint(scroller).Position;
        scroller.ChangeView(Math.Max(0, _dragHorizontal - (point.X - _dragStart.X)), Math.Max(0, _dragVertical - (point.Y - _dragStart.Y)), null, true);
        e.Handled = true;
    }

    private void ImageSurface_PointerEnded(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false; (_activeDragScroller ?? ImageScroller).ReleasePointerCapture(e.Pointer); _activeDragScroller = null; e.Handled = true;
    }

    private void ImageScroller_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var scroller = sender as ScrollViewer ?? ImageScroller;
        var point = e.GetCurrentPoint(scroller);
        var delta = point.Properties.MouseWheelDelta;
        if (delta == 0) return;
        var oldZoom = scroller.ZoomFactor;
        var newZoom = Math.Clamp(delta > 0 ? oldZoom * 1.18f : oldZoom / 1.18f, 0.1f, 10f);
        var imageX = (scroller.HorizontalOffset + point.Position.X) / oldZoom;
        var imageY = (scroller.VerticalOffset + point.Position.Y) / oldZoom;
        var newHorizontal = Math.Max(0, imageX * newZoom - point.Position.X);
        var newVertical = Math.Max(0, imageY * newZoom - point.Position.Y);
        scroller.ChangeView(newHorizontal, newVertical, newZoom, true);
        e.Handled = true;
    }

    private void ViewerImage_ImageOpened(object sender, RoutedEventArgs e) => FitImage();

    private void RotateLeft_Click(object sender, RoutedEventArgs e) { _rotationQuarterTurns = (_rotationQuarterTurns + 3) % 4; ApplyRotationPreview(); }
    private void RotateRight_Click(object sender, RoutedEventArgs e) { _rotationQuarterTurns = (_rotationQuarterTurns + 1) % 4; ApplyRotationPreview(); }
    private void ResetEdits_Click(object sender, RoutedEventArgs e) { _rotationQuarterTurns = 0; ApplyRotationPreview(); }

    private void ApplyRotationPreview()
    {
        ViewerTransform.Rotation = _rotationQuarterTurns * 90;
        ViewerTransform.TranslateX = _rotationQuarterTurns switch { 1 => _decodedHeight, 2 => _decodedWidth, _ => 0 };
        ViewerTransform.TranslateY = _rotationQuarterTurns switch { 2 => _decodedHeight, 3 => _decodedWidth, _ => 0 };
        var sideways = _rotationQuarterTurns % 2 == 1;
        ImageSurface.Width = sideways ? _decodedHeight : _decodedWidth;
        ImageSurface.Height = sideways ? _decodedWidth : _decodedHeight;
        ResizeForImage(sideways ? _sourcePixelHeight : _sourcePixelWidth, sideways ? _sourcePixelWidth : _sourcePixelHeight);
        var edited = _rotationQuarterTurns != 0;
        ResetEditsButton.IsEnabled = edited;
        SaveChangesButton.IsEnabled = edited;
        EditStatusText.Text = edited ? $"Pending rotation: {_rotationQuarterTurns * 90}° clockwise" : "No pending edits";
        ScheduleFitAfterLayout();
    }

    private void ScheduleFitAfterLayout()
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(80);
            ImageSurface.UpdateLayout();
            ImageScroller.UpdateLayout();
            FitImage();
        });
    }

    private async void SaveChanges_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentPath is not { } source || _rotationQuarterTurns == 0) return;
        var extension = Path.GetExtension(source).ToLowerInvariant();
        var directory = Path.GetDirectoryName(source)!;
        var temporary = Path.Combine(directory, $".{Path.GetFileNameWithoutExtension(source)}.{Guid.NewGuid():N}.editing{extension}");
        var originalCreationTime = File.GetCreationTimeUtc(source);
        SaveChangesButton.IsEnabled = false;
        EditStatusText.Text = "Applying rotation...";
        try
        {
            var start = new ProcessStartInfo("magick") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            start.ArgumentList.Add(source);
            start.ArgumentList.Add("-auto-orient");
            start.ArgumentList.Add("-rotate");
            start.ArgumentList.Add((_rotationQuarterTurns * 90).ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add(temporary);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("ImageMagick could not be started.");
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var error = await errorTask;
            if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? $"ImageMagick exited with code {process.ExitCode}." : error.Trim());
            ViewerImage.Source = null;
            _currentBitmap = null;
            File.Move(temporary, source, true);
            File.SetCreationTimeUtc(source, originalCreationTime);
            await LoadCurrentAsync(false);
            EditStatusText.Text = "Rotation saved to original";
        }
        catch (Exception ex)
        {
            EditStatusText.Text = $"Save failed: {ex.Message}";
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch { /* A leftover temporary file is safer than risking the original. */ }
            SaveChangesButton.IsEnabled = _rotationQuarterTurns != 0;
        }
    }

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == global::Windows.System.VirtualKey.Left) Navigate(-1);
        else if (e.Key == global::Windows.System.VirtualKey.Right) Navigate(1);
        else if (e.Key == global::Windows.System.VirtualKey.Add) ZoomIn_Click(sender, e);
        else if (e.Key == global::Windows.System.VirtualKey.Subtract) ZoomOut_Click(sender, e);
        else if (e.Key == global::Windows.System.VirtualKey.Q && !_comparisonMode) RotateLeft_Click(sender, e);
        else if (e.Key == global::Windows.System.VirtualKey.E && !_comparisonMode) RotateRight_Click(sender, e);
        else if (e.Key == global::Windows.System.VirtualKey.Space && SaveChangesButton.IsEnabled) SaveChanges_Click(sender, e);
        else if (e.Key == global::Windows.System.VirtualKey.F) FitImage();
        else return;
        e.Handled = true;
    }

    private void OpenDefault_Click(object sender, RoutedEventArgs e) { if (CurrentPath is { } path) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
    private void Reveal_Click(object sender, RoutedEventArgs e) { if (CurrentPath is not { } path) return; var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true }; start.ArgumentList.Add($"/select,{path}"); Process.Start(start); }
    private void CopyPath_Click(object sender, RoutedEventArgs e) { if (CurrentPath is not { } path) return; var package = new DataPackage(); package.SetText(path); Clipboard.SetContent(package); DetailsText.Text = "Path copied to clipboard."; }
    private string? CurrentPath => _paths.Length == 0 || _index < 0 || _index >= _paths.Length ? null : _paths[_index];

    private static string FormatSize(long bytes) => bytes switch { >= 1_073_741_824 => $"{bytes / 1_073_741_824d:0.0} GB", >= 1_048_576 => $"{bytes / 1_048_576d:0.0} MB", >= 1024 => $"{bytes / 1024d:0} KB", _ => $"{bytes} B" };
}
