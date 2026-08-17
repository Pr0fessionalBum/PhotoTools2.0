using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using PhotoTools2.Models;
using PhotoTools2.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace PhotoTools2.Controls;

public sealed partial class ScannerLineWorkspace : UserControl
{
    private CancellationTokenSource? _cancellation;
    private string[] _analysisFiles = [];
    public ObservableCollection<FileBrowserItem> FolderItems { get; } = [];
    public ObservableCollection<ScannerLineResult> Results { get; } = [];

    public ScannerLineWorkspace() { InitializeComponent(); FolderItemsList.ItemsSource = FolderItems; ResultsGrid.ItemsSource = Results; }
    public void RefreshFromCurrentAlbum() { var path = AppSettings.Get("CurrentAlbumPath"); if (Directory.Exists(path)) LoadFolder(path); }
    private async void ChooseFolder_Click(object sender, RoutedEventArgs e) { if (await FolderBrowserService.PickFolderAsync() is { } path) LoadFolder(path); }
    private void LoadFolder(string path)
    {
        path = FolderBrowserService.NormalizeExistingFolder(path) ?? path; FolderPathBox.Text = path; AppSettings.Set("CurrentAlbumPath", path); FolderItems.Clear(); Results.Clear();
        foreach (var item in FolderBrowserService.Enumerate(path, IsImage)) FolderItems.Add(item);
        _analysisFiles = EnumerateAnalysisFiles(path, IncludeSubfoldersBox.IsChecked == true).ToArray();
        var count = _analysisFiles.Length; ImageCountText.Text = $"{count:N0} images"; AnalyzeButton.IsEnabled = count > 0;
        ResultSummaryText.Text = count < 3 ? "Fewer than three images: results may include normal image details." : "Ready for batch comparison.";
        StatusText.Text = count == 0 ? "No supported images found here." : "Ready. Analysis is read-only and uses downscaled copies.";
    }
    private async void Analyze_Click(object sender, RoutedEventArgs e)
    {
        var files = _analysisFiles; if (files.Length == 0) return;
        _cancellation = new CancellationTokenSource(); SetBusy(true); Results.Clear();
        var progress = new Progress<(int done, int total)>(value => { AnalysisProgress.Value = value.done * 100d / value.total; StatusText.Text = $"Analyzing {value.done:N0} of {value.total:N0}..."; });
        try
        {
            var result = await ScannerLineDetector.AnalyzeAsync(files, SensitivitySlider.Value, progress, _cancellation.Token);
            foreach (var group in result.Findings.GroupBy(item => item.Path))
            {
                var info = new FileInfo(group.Key); var strongest = group.OrderByDescending(item => item.Confidence).First();
                var direction = strongest.IsHorizontal ? "horizontal" : "vertical";
                var reasons = $"{strongest.Coverage:P0} {direction} coverage, about {strongest.WidthPixels}px wide" + (strongest.BatchMatches > 0 ? $", confirmed in {strongest.BatchMatches:N0} other image(s)" : ", detected from this image alone");
                Results.Add(new ScannerLineResult { Photo = new FileBrowserItem { Name = info.Name, Path = info.FullName, IsImage = true, Size = info.Length, Modified = info.LastWriteTime }, PositionLabel = string.Join(", ", group.Select(item => $"{(item.IsHorizontal ? "Horizontal" : "Vertical")} line near {item.Position:P0}")), ConfidencePercent = strongest.Confidence * 100, ConfidenceLabel = $"{strongest.Confidence:P0} confidence", LinePosition = strongest.Position, IsHorizontal = strongest.IsHorizontal, Explanation = reasons });
            }
            ResultSummaryText.Text = result.Lines.Count == 0 ? $"No high-confidence scanner lines found across {result.AnalyzedCount:N0} images." : $"{result.Lines.Count:N0} candidate line position(s) found; {Results.Count:N0} images flagged.";
            StatusText.Text = result.Lines.Count == 0 ? "Analysis complete. No strong single-image streak was detected." : "Analysis complete. Double-click a result to inspect the original.";
        }
        catch (OperationCanceledException) { StatusText.Text = "Analysis cancelled."; }
        catch (Exception ex) { StatusText.Text = $"Analysis stopped safely: {ex.Message}"; }
        finally { SetBusy(false); _cancellation?.Dispose(); _cancellation = null; }
    }
    private void SetBusy(bool busy) { BusyRing.IsActive = busy; AnalyzeButton.IsEnabled = !busy && FolderItems.Any(item => !item.IsFolder); CancelButton.IsEnabled = busy; if (busy) AnalysisProgress.Value = 0; }
    private void Cancel_Click(object sender, RoutedEventArgs e) { CancelButton.IsEnabled = false; StatusText.Text = "Cancelling analysis..."; _cancellation?.Cancel(); }
    private void FolderItemsList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) { if (FolderItemsList.SelectedItem is FileBrowserItem item) FolderBrowserService.OpenItem(item, LoadFolder); }
    private async void ResultsGrid_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) { if (ResultsGrid.SelectedItem is ScannerLineResult item) await ShowLinePreviewAsync(item); }
    private async Task ShowLinePreviewAsync(ScannerLineResult item)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(item.Photo.Path);
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var sourceWidth = Math.Max(1d, decoder.PixelWidth);
            var sourceHeight = Math.Max(1d, decoder.PixelHeight);
            var displayScale = Math.Min(1d, 2400d / Math.Max(sourceWidth, sourceHeight));
            var displayWidth = sourceWidth * displayScale;
            var displayHeight = sourceHeight * displayScale;

            var originalImage = new Image { Source = new BitmapImage(new Uri(item.Photo.Path)), Width = displayWidth, Height = displayHeight, Stretch = Microsoft.UI.Xaml.Media.Stretch.Fill };
            var detectedImage = new Image { Source = new BitmapImage(new Uri(item.Photo.Path)), Width = displayWidth, Height = displayHeight, Stretch = Microsoft.UI.Xaml.Media.Stretch.Fill };
            var marker = new Border { Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 59, 48)), BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)), BorderThickness = new Thickness(1), IsHitTestVisible = false };
            if (item.IsHorizontal)
            {
                marker.Width = displayWidth; marker.Height = 4; marker.HorizontalAlignment = HorizontalAlignment.Left; marker.VerticalAlignment = VerticalAlignment.Top;
                marker.Margin = new Thickness(0, displayHeight * item.LinePosition - 2, 0, 0);
            }
            else
            {
                marker.Width = 4; marker.Height = displayHeight; marker.HorizontalAlignment = HorizontalAlignment.Left; marker.VerticalAlignment = VerticalAlignment.Top;
                marker.Margin = new Thickness(displayWidth * item.LinePosition - 2, 0, 0, 0);
            }
            var black = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 0, 0));
            var originalPreview = new Grid { Width = displayWidth, Height = displayHeight, Background = black };
            originalPreview.Children.Add(originalImage);
            var detectedPreview = new Grid { Width = displayWidth, Height = displayHeight, Background = black };
            detectedPreview.Children.Add(detectedImage); detectedPreview.Children.Add(marker);
            const double paneWidth = 700;
            ScrollViewer CreateScroller(UIElement preview) => new() { Width = paneWidth, Height = 680, ZoomMode = ZoomMode.Enabled, MinZoomFactor = 0.1f, MaxZoomFactor = 12f, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Enabled, Content = preview };
            var originalScroller = CreateScroller(originalPreview);
            var detectedScroller = CreateScroller(detectedPreview);
            Border FramePane(string title, ScrollViewer scroller)
            {
                var layout = new Grid { RowSpacing = 8 };
                layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(680) });
                layout.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(4, 0, 0, 0) });
                Grid.SetRow(scroller, 1); layout.Children.Add(scroller);
                return new Border { Padding = new Thickness(10), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(2), BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(150, 128, 128, 128)), Child = layout };
            }
            var originalFrame = FramePane("Original", originalScroller);
            var detectedFrame = FramePane("Highlighted detection", detectedScroller);
            var comparison = new Grid { ColumnSpacing = 48 };
            comparison.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(paneWidth + 24) });
            comparison.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(paneWidth + 24) });
            Grid.SetColumn(detectedFrame, 1);
            comparison.Children.Add(originalFrame); comparison.Children.Add(detectedFrame);
            var synchronizing = false;
            void MirrorView(ScrollViewer source, ScrollViewer target)
            {
                if (synchronizing) return;
                synchronizing = true;
                target.ChangeView(source.HorizontalOffset, source.VerticalOffset, source.ZoomFactor, true);
                synchronizing = false;
            }
            originalScroller.ViewChanged += (_, _) => MirrorView(originalScroller, detectedScroller);
            detectedScroller.ViewChanged += (_, _) => MirrorView(detectedScroller, originalScroller);
            void CenterOnLine(float zoom)
            {
                var targetX = (item.IsHorizontal ? displayWidth / 2 : displayWidth * item.LinePosition) * zoom - detectedScroller.ViewportWidth / 2;
                var targetY = (item.IsHorizontal ? displayHeight * item.LinePosition : displayHeight / 2) * zoom - detectedScroller.ViewportHeight / 2;
                detectedScroller.ChangeView(Math.Max(0, targetX), Math.Max(0, targetY), zoom, true);
                originalScroller.ChangeView(Math.Max(0, targetX), Math.Max(0, targetY), zoom, true);
            }
            void EnableMousePan(ScrollViewer viewer)
            {
                var dragging = false;
                Windows.Foundation.Point dragStart = default;
                double startHorizontal = 0, startVertical = 0;
                void Pressed(object sender, PointerRoutedEventArgs e) { var point = e.GetCurrentPoint(viewer); if (!point.Properties.IsLeftButtonPressed && !point.Properties.IsMiddleButtonPressed) return; dragging = true; dragStart = point.Position; startHorizontal = viewer.HorizontalOffset; startVertical = viewer.VerticalOffset; viewer.CapturePointer(e.Pointer); e.Handled = true; }
                void Moved(object sender, PointerRoutedEventArgs e) { if (!dragging) return; var current = e.GetCurrentPoint(viewer).Position; viewer.ChangeView(Math.Max(0, startHorizontal - (current.X - dragStart.X)), Math.Max(0, startVertical - (current.Y - dragStart.Y)), null, true); e.Handled = true; }
                void Ended(object sender, PointerRoutedEventArgs e) { if (!dragging) return; dragging = false; viewer.ReleasePointerCapture(e.Pointer); e.Handled = true; }
                viewer.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Pressed), true);
                viewer.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(Moved), true);
                viewer.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(Ended), true);
                viewer.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(Ended), true);
            }
            EnableMousePan(originalScroller); EnableMousePan(detectedScroller);
            ScannerLineResult? navigateTo = null;
            ContentDialog? dialog = null;
            var resultIndex = Results.IndexOf(item);
            void Navigate(int offset)
            {
                if (Results.Count < 2 || resultIndex < 0) return;
                navigateTo = Results[(resultIndex + offset + Results.Count) % Results.Count];
                dialog?.Hide();
            }
            var previousButton = new Button { Content = "← Previous", IsEnabled = Results.Count > 1 };
            var centerButton = new Button { Content = "Center on detected line", HorizontalAlignment = HorizontalAlignment.Left };
            var nextButton = new Button { Content = "Next →", IsEnabled = Results.Count > 1 };
            previousButton.Click += (_, _) => Navigate(-1);
            centerButton.Click += (_, _) => CenterOnLine(Math.Max(2.5f, detectedScroller.ZoomFactor));
            nextButton.Click += (_, _) => Navigate(1);
            var toolbar = new Grid { ColumnSpacing = 10 };
            toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(centerButton, 1); centerButton.HorizontalAlignment = HorizontalAlignment.Center; Grid.SetColumn(nextButton, 2);
            toolbar.Children.Add(previousButton); toolbar.Children.Add(centerButton); toolbar.Children.Add(nextButton);
            var content = new StackPanel { Spacing = 10 };
            content.Children.Add(new TextBlock { Text = "Original (left)  |  Detected line (right)", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            content.Children.Add(new TextBlock { Text = "Drag with the left or middle mouse button to pan both views together.", FontSize = 12 });
            content.Children.Add(new TextBlock { Text = $"{(item.IsHorizontal ? "Horizontal" : "Vertical")} candidate • {item.ConfidenceLabel} • Ctrl+wheel or pinch to zoom", Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 128, 128, 128)) });
            content.Children.Add(toolbar); content.Children.Add(comparison);
            dialog = new ContentDialog { Title = $"{item.Photo.Name}  ({resultIndex + 1} of {Results.Count})", Content = content, CloseButtonText = "Close", XamlRoot = XamlRoot, MaxWidth = 1540 };
            dialog.Resources["ContentDialogMaxWidth"] = 1540d;
            dialog.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Left) { Navigate(-1); e.Handled = true; } else if (e.Key == Windows.System.VirtualKey.Right) { Navigate(1); e.Handled = true; } };
            dialog.Opened += (_, _) =>
            {
                var fitZoom = (float)Math.Min(1d, Math.Min(detectedScroller.ViewportWidth / displayWidth, detectedScroller.ViewportHeight / displayHeight));
                originalScroller.ChangeView(0, 0, Math.Max(0.1f, fitZoom), true);
                detectedScroller.ChangeView(0, 0, Math.Max(0.1f, fitZoom), true);
            };
            await dialog.ShowAsync();
            if (navigateTo is not null) await ShowLinePreviewAsync(navigateTo);
        }
        catch (Exception ex) { StatusText.Text = $"Could not open the inspection preview: {ex.Message}"; }
    }
    private void PreviewImage_ImageOpened(object sender, RoutedEventArgs e) { if (sender is Image image && image.Parent is Grid preview) PositionLineMarker(preview); }
    private void PreviewGrid_SizeChanged(object sender, SizeChangedEventArgs e) { if (sender is Grid preview) PositionLineMarker(preview); }
    private static void PositionLineMarker(Grid preview)
    {
        if (preview.Tag is not ScannerLineResult result || preview.Children.Count < 2 || preview.Children[0] is not Image image || preview.Children[1] is not Border marker || image.Source is not BitmapImage bitmap || bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0 || preview.ActualWidth <= 0 || preview.ActualHeight <= 0) return;
        var imageRatio = bitmap.PixelWidth / (double)bitmap.PixelHeight;
        var displayedWidth = Math.Min(preview.ActualWidth, preview.ActualHeight * imageRatio);
        var displayedHeight = displayedWidth / imageRatio;
        var leftPadding = (preview.ActualWidth - displayedWidth) / 2d;
        var topPadding = (preview.ActualHeight - displayedHeight) / 2d;
        if (result.IsHorizontal)
        {
            marker.Width = displayedWidth;
            marker.Height = 3;
            marker.HorizontalAlignment = HorizontalAlignment.Left;
            marker.VerticalAlignment = VerticalAlignment.Top;
            marker.Margin = new Thickness(leftPadding, topPadding + displayedHeight * Math.Clamp(result.LinePosition, 0, 1) - 1.5, 0, 0);
        }
        else
        {
            marker.Width = 3;
            marker.Height = displayedHeight;
            marker.HorizontalAlignment = HorizontalAlignment.Left;
            marker.VerticalAlignment = VerticalAlignment.Top;
            marker.Margin = new Thickness(leftPadding + displayedWidth * Math.Clamp(result.LinePosition, 0, 1) - 1.5, topPadding, 0, 0);
        }
    }
    private void FolderPathBox_KeyDown(object sender, KeyRoutedEventArgs e) { if (e.Key != Windows.System.VirtualKey.Enter) return; var path = FolderBrowserService.NormalizeExistingFolder(FolderPathBox.Text); if (path is not null) LoadFolder(path); else StatusText.Text = "That folder path could not be found."; e.Handled = true; }
    private void UpFolder_Click(object sender, RoutedEventArgs e) { if (FolderBrowserService.GetParent(FolderPathBox.Text) is { } parent) LoadFolder(parent); }
    private void IncludeSubfolders_Changed(object sender, RoutedEventArgs e) { if (FolderPathBox is not null && Directory.Exists(FolderPathBox.Text)) LoadFolder(FolderPathBox.Text); }
    private void OpenFolder_Click(object sender, RoutedEventArgs e) { if (Directory.Exists(FolderPathBox.Text)) FolderBrowserService.OpenFolder(FolderPathBox.Text); }
    private void Workspace_DragEnter(object sender, DragEventArgs e) { if (e.DataView.Contains(StandardDataFormats.StorageItems)) e.AcceptedOperation = DataPackageOperation.Copy; }
    private async void Workspace_Drop(object sender, DragEventArgs e) { if (e.DataView.Contains(StandardDataFormats.StorageItems) && (await e.DataView.GetStorageItemsAsync()).FirstOrDefault() is StorageFolder folder) LoadFolder(folder.Path); }
    private static bool IsImage(string path) => new[] { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    private static IEnumerable<string> EnumerateAnalysisFiles(string root, bool recurse)
    {
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0)
        {
            var folder = pending.Pop();
            IEnumerable<string> files; try { files = Directory.EnumerateFiles(folder).Where(IsImage).ToArray(); } catch (UnauthorizedAccessException) { continue; }
            foreach (var file in files) yield return file;
            if (!recurse) continue;
            IEnumerable<string> children; try { children = Directory.EnumerateDirectories(folder).ToArray(); } catch (UnauthorizedAccessException) { continue; }
            foreach (var child in children.Where(path => !new[] { "cropped", "JPG", "Quarantine" }.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))) pending.Push(child);
        }
    }
}
