using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using PhotoTools2.Models;
using PhotoTools2.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace PhotoTools2.Controls;

public sealed partial class PdfToJpgWorkspace : UserControl
{
    private PdfDocumentJob? _selectedJob;
    private CancellationTokenSource? _documentCancellation;
    private CancellationTokenSource? _thumbnailCancellation;
    private CancellationTokenSource? _editThumbnailCancellation;
    private CancellationTokenSource? _previewCancellation;
    private CancellationTokenSource? _exportCancellation;
    private bool _suppressDocumentSelection;
    private int _centerRequest;
    private uint _previewPixelWidth;
    private uint _previewPixelHeight;
    public ObservableCollection<PdfDocumentJob> Documents { get; } = [];
    public ObservableCollection<PdfPagePreviewItem> Pages { get; } = [];

    public PdfToJpgWorkspace() => InitializeComponent();
    public void RefreshFromCurrentAlbum() { }

    private async void AddPdfs_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".pdf");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        var files = await picker.PickMultipleFilesAsync();
        foreach (var file in files) await LoadPdfAsync(file.Path);
    }

    private async void ChooseDocument_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not PdfDocumentJob job) return;
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".pdf");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        if (await picker.PickSingleFileAsync() is { } file) await LoadPdfAsync(file.Path, job);
    }

    private void Workspace_DragEnter(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems)) e.AcceptedOperation = DataPackageOperation.Copy;
    }

    private async void Workspace_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var files = (await e.DataView.GetStorageItemsAsync()).OfType<StorageFile>()
            .Where(item => Path.GetExtension(item.Path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (files.Length == 0) { StatusText.Text = "Drop one or more PDF files here."; return; }
        foreach (var file in files) await LoadPdfAsync(file.Path);
    }

    private async Task LoadPdfAsync(string path, PdfDocumentJob? replacedJob = null)
    {
        var previousJob = _selectedJob;
        var fullPath = Path.GetFullPath(path);
        if (Documents.FirstOrDefault(job => string.Equals(job.SourcePath, fullPath, StringComparison.OrdinalIgnoreCase)) is { } existing)
        {
            if (replacedJob is not null && !ReferenceEquals(existing, replacedJob)) Documents.Remove(replacedJob);
            SelectDocument(existing);
            return;
        }
        CancelDocumentWork();
        var cancellation = _documentCancellation = new CancellationTokenSource();
        _selectedJob = null;
        Pages.Clear(); PreviewImage.Source = null; PageList.IsEnabled = false; ExportButton.IsEnabled = false;
        StatusText.Text = $"Opening {Path.GetFileName(path)}..."; PreviewBusy.IsActive = true;
        try
        {
            var session = await PdfConversionService.OpenAsync(path, cancellation.Token);
            if (!ReferenceEquals(_documentCancellation, cancellation)) return;
            var job = PdfDocumentJob.Create(session);
            if (replacedJob is not null && Documents.IndexOf(replacedJob) is var replacementIndex && replacementIndex >= 0)
                Documents[replacementIndex] = job;
            else Documents.Add(job);
            SelectDocument(job);
            PageCountText.Text = $"{session.PageCount:N0} page{(session.PageCount == 1 ? string.Empty : "s")}";
            PageList.IsEnabled = true;
            UpdateIncludedCount();
            StatusText.Text = "PDF loaded. Rotate or exclude pages, then export JPG copies.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not open the PDF: {ex.Message}";
            if (previousJob is not null && Documents.Contains(previousJob)) SelectDocument(previousJob);
        }
        finally { if (ReferenceEquals(_documentCancellation, cancellation)) PreviewBusy.IsActive = false; }
    }

    private void ActivateDocument(PdfDocumentJob job)
    {
        if (_selectedJob is not null)
        {
            _selectedJob.CurrentPageIndex = PageList.SelectedIndex;
            _selectedJob.OutputFolder = OutputPathBox.Text.Trim();
        }
        _selectedJob = job;
        Pages.Clear();
        foreach (var page in job.Pages) Pages.Add(page);
        OutputPathBox.Text = job.OutputFolder;
        PageCountText.Text = $"{job.PageCount:N0} page{(job.PageCount == 1 ? string.Empty : "s")}";
        PageList.SelectedIndex = job.CurrentPageIndex;
        UpdateIncludedCount();
        OpenOutputButton.IsEnabled = Directory.Exists(job.OutputFolder);
        _thumbnailCancellation?.Cancel(); _thumbnailCancellation?.Dispose();
        _thumbnailCancellation = new CancellationTokenSource();
        _ = LoadThumbnailsAsync(job, _thumbnailCancellation.Token);
    }

    private void SelectDocument(PdfDocumentJob job)
    {
        _suppressDocumentSelection = true;
        DocumentList.SelectedItem = job;
        _suppressDocumentSelection = false;
        ActivateDocument(job);
        DocumentList.ScrollIntoView(job);
    }

    private void DocumentList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppressDocumentSelection && DocumentList.SelectedItem is PdfDocumentJob job && !ReferenceEquals(job, _selectedJob))
            ActivateDocument(job);
    }

    private void OpenDocument_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is PdfDocumentJob job && File.Exists(job.SourcePath)) FolderBrowserService.OpenFile(job.SourcePath);
    }

    private void RemoveDocument_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not PdfDocumentJob job) return;
        var index = Documents.IndexOf(job);
        if (index < 0) return;
        var wasSelected = ReferenceEquals(job, _selectedJob);
        Documents.RemoveAt(index);
        if (!wasSelected) return;
        if (Documents.Count > 0) SelectDocument(Documents[Math.Min(index, Documents.Count - 1)]);
        else ClearActiveDocument();
    }

    private void ClearActiveDocument()
    {
        _selectedJob = null; Pages.Clear(); PreviewImage.Source = null;
        _previewPixelWidth = _previewPixelHeight = 0;
        OutputPathBox.Text = string.Empty; PageCountText.Text = "0 pages"; CurrentPageText.Text = "No PDF loaded";
        PreviousButton.IsEnabled = NextButton.IsEnabled = RotateLeftButton.IsEnabled = RotateRightButton.IsEnabled = ResetRotationButton.IsEnabled = false;
        ExportButton.IsEnabled = ExportAllButton.IsEnabled = OpenOutputButton.IsEnabled = false;
        IncludedCountText.Text = "0 pages included";
        StatusText.Text = "Add or drop one or more PDFs to begin.";
    }

    private async Task LoadThumbnailsAsync(PdfDocumentJob job, CancellationToken token)
    {
        if (job.Session is null) return;
        for (var index = 0; index < job.Pages.Count; index++)
        {
            if (job.Pages[index].Thumbnail is not null) continue;
            try
            {
                var rotation = job.Pages[index].Edit.RotationQuarterTurns;
                var bytes = await PdfPreviewCacheService.GetOrRenderAsync(job.Session, (uint)index, 240, rotation, token);
                token.ThrowIfCancellationRequested();
                var bitmap = await CreateBitmapAsync(bytes);
                token.ThrowIfCancellationRequested();
                if (job.Pages[index].Edit.RotationQuarterTurns == rotation) job.Pages[index].Thumbnail = bitmap;
            }
            catch (OperationCanceledException) { return; }
            catch { }
        }
    }

    private async void PageList_SelectionChanged(object sender, SelectionChangedEventArgs e) => await RenderSelectedPageAsync();

    private async Task RenderSelectedPageAsync()
    {
        if (_selectedJob is not { Session: { } session } activeJob || PageList.SelectedItem is not PdfPagePreviewItem item) return;
        activeJob.CurrentPageIndex = PageList.SelectedIndex;
        _previewCancellation?.Cancel(); _previewCancellation?.Dispose();
        var cancellation = _previewCancellation = new CancellationTokenSource();
        PreviewBusy.IsActive = true; PreviewImage.Source = null;
        _previewPixelWidth = _previewPixelHeight = 0;
        PreviewImage.Width = PreviewImage.Height = double.NaN;
        PreviewRotationTransform.Angle = 0;
        CurrentPageText.Text = $"{item.PageLabel} of {session.PageCount:N0}";
        PreviousButton.IsEnabled = item.PageIndex > 0;
        NextButton.IsEnabled = item.PageIndex + 1 < session.PageCount;
        RotateLeftButton.IsEnabled = RotateRightButton.IsEnabled = true;
        ResetRotationButton.IsEnabled = item.Edit.RotationQuarterTurns != 0;
        try
        {
            PreviewScroller.ChangeView(0, 0, 1, true);
            var bytes = await PdfPreviewCacheService.GetOrRenderAsync(session, item.PageIndex, 2200, 0, cancellation.Token);
            if (!ReferenceEquals(_previewCancellation, cancellation)) return;
            var bitmap = await CreateBitmapAsync(bytes);
            if (!ReferenceEquals(_previewCancellation, cancellation) || !ReferenceEquals(_selectedJob, activeJob)
                || !ReferenceEquals(PageList.SelectedItem, item)) return;
            (_previewPixelWidth, _previewPixelHeight) = ReadPngDimensions(bytes);
            if (_previewPixelWidth == 0 || _previewPixelHeight == 0)
                throw new InvalidDataException("The cached PDF preview has invalid dimensions.");
            PreviewImage.Source = bitmap;
            ApplyPreviewRotationAndFit();
            if (ReferenceEquals(_selectedJob, activeJob)) _ = PreloadAdjacentPreviewsAsync(activeJob, item.PageIndex);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText.Text = $"Could not render {item.PageLabel}: {ex.Message}"; }
        finally { if (ReferenceEquals(_previewCancellation, cancellation)) PreviewBusy.IsActive = false; }
    }

    private static async Task PreloadAdjacentPreviewsAsync(PdfDocumentJob? job, uint currentPageIndex)
    {
        if (job?.Session is not { } session) return;
        var adjacent = new List<uint>(2);
        if (currentPageIndex > 0) adjacent.Add(currentPageIndex - 1);
        if (currentPageIndex + 1 < session.PageCount) adjacent.Add(currentPageIndex + 1);
        foreach (var pageIndex in adjacent)
        {
            try
            {
                await PdfPreviewCacheService.GetOrRenderAsync(session, pageIndex, 2200, 0);
            }
            catch { }
        }
    }

    private static async Task<BitmapImage> CreateBitmapAsync(byte[] bytes)
    {
        using var memory = new MemoryStream(bytes, false);
        using var stream = memory.AsRandomAccessStream();
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }

    private void Previous_Click(object sender, RoutedEventArgs e) { if (PageList.SelectedIndex > 0) PageList.SelectedIndex--; }
    private void Next_Click(object sender, RoutedEventArgs e) { if (PageList.SelectedIndex + 1 < Pages.Count) PageList.SelectedIndex++; }
    private void RotateLeft_Click(object sender, RoutedEventArgs e) => RotateSelected(false);
    private void RotateRight_Click(object sender, RoutedEventArgs e) => RotateSelected(true);
    private void ResetRotation_Click(object sender, RoutedEventArgs e)
    {
        if (PageList.SelectedItem is not PdfPagePreviewItem item) return;
        item.ResetRotation(); ApplyPreviewRotationAndFit(); _ = CenterPreviewAsync(); _ = RefreshEditedThumbnailAsync(item);
    }
    private void RotateSelected(bool clockwise)
    {
        if (PageList.SelectedItem is not PdfPagePreviewItem item) return;
        if (clockwise) item.RotateRight(); else item.RotateLeft();
        ApplyPreviewRotationAndFit();
        _ = CenterPreviewAsync();
        _ = RefreshEditedThumbnailAsync(item);
    }
    private async Task RefreshEditedThumbnailAsync(PdfPagePreviewItem item)
    {
        var rotation = item.Edit.RotationQuarterTurns;
        ResetRotationButton.IsEnabled = item.Edit.RotationQuarterTurns != 0;
        if (_selectedJob?.Session is not { } session) return;
        _editThumbnailCancellation?.Cancel(); _editThumbnailCancellation?.Dispose();
        var cancellation = _editThumbnailCancellation = new CancellationTokenSource();
        try
        {
            await Task.Delay(120, cancellation.Token);
            var bytes = await PdfPreviewCacheService.GetOrRenderAsync(session, item.PageIndex, 240, rotation, cancellation.Token);
            var bitmap = await CreateBitmapAsync(bytes);
            if (!cancellation.IsCancellationRequested && item.Edit.RotationQuarterTurns == rotation) item.Thumbnail = bitmap;
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private void PageInclude_Changed(object sender, RoutedEventArgs e) => UpdateIncludedCount();
    private void IncludeAll_Click(object sender, RoutedEventArgs e) { foreach (var page in Pages) page.IsIncluded = true; UpdateIncludedCount(); }
    private void ExcludeAll_Click(object sender, RoutedEventArgs e) { foreach (var page in Pages) page.IsIncluded = false; UpdateIncludedCount(); }
    private void UpdateIncludedCount()
    {
        var included = Pages.Count(page => page.IsIncluded);
        IncludedCountText.Text = $"{included:N0} of {Pages.Count:N0} pages included";
        ExportButton.IsEnabled = _selectedJob?.Session is not null && included > 0 && _exportCancellation is null;
        ExportAllButton.IsEnabled = Documents.Any(job => job.Session is not null && job.Pages.Any(page => page.IsIncluded)) && _exportCancellation is null;
    }

    private async void ChooseOutput_Click(object sender, RoutedEventArgs e)
    {
        if (await FolderBrowserService.PickFolderAsync() is { } path) OutputPathBox.Text = path;
    }
    private void OpenOutput_Click(object sender, RoutedEventArgs e) { if (Directory.Exists(OutputPathBox.Text)) FolderBrowserService.OpenFolder(OutputPathBox.Text); }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedJob?.Session is not { } session || Pages.All(page => !page.IsIncluded)) return;
        var output = OutputPathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(output)) { StatusText.Text = "Choose an output folder."; return; }
        _exportCancellation = new CancellationTokenSource();
        SetEditingEnabled(false); CancelButton.IsEnabled = true; ExportProgress.Value = 0;
        var progress = new Progress<(int completed, int total, string fileName)>(value =>
        {
            ExportProgress.Value = value.completed * 100d / value.total;
            StatusText.Text = $"Exporting {value.completed:N0} of {value.total:N0}: {value.fileName}";
        });
        try
        {
            var quality = double.IsNaN(QualityBox.Value) ? 92 : (int)QualityBox.Value;
            var dpi = double.IsNaN(DpiBox.Value) ? 300 : DpiBox.Value;
            _selectedJob.OutputFolder = output;
            await PdfConversionService.ExportAsync(session, output, quality, dpi, progress, _exportCancellation.Token, _selectedJob.OutputName);
            OpenOutputButton.IsEnabled = true;
            AlbumFileIndexService.Invalidate(output);
            StatusText.Text = $"Export complete. {Pages.Count(page => page.IsIncluded):N0} JPG pages are ready.";
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "PDF conversion complete", Content = "The JPG pages were exported without changing the original PDF.", PrimaryButtonText = "Open output folder", CloseButtonText = "Stay here", DefaultButton = ContentDialogButton.Primary };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary) FolderBrowserService.OpenFolder(output);
        }
        catch (OperationCanceledException) { StatusText.Text = "Export cancelled. Completed JPG pages remain in the output folder."; }
        catch (Exception ex) { StatusText.Text = $"Export stopped safely: {ex.Message}"; }
        finally
        {
            _exportCancellation.Dispose(); _exportCancellation = null;
            CancelButton.IsEnabled = false; SetEditingEnabled(true); UpdateIncludedCount();
        }
    }

    private async void ExportAll_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedJob is not null) _selectedJob.OutputFolder = OutputPathBox.Text.Trim();
        var jobs = Documents.Where(job => job.Session is not null && job.Pages.Any(page => page.IsIncluded)).ToArray();
        if (jobs.Length == 0) return;
        if (jobs.Any(job => string.IsNullOrWhiteSpace(job.OutputFolder))) { StatusText.Text = "Every PDF needs an output folder."; return; }

        _exportCancellation = new CancellationTokenSource();
        SetEditingEnabled(false); CancelButton.IsEnabled = true; ExportProgress.Value = 0;
        var totalPages = jobs.Sum(job => job.Pages.Count(page => page.IsIncluded));
        var completedBefore = 0;
        try
        {
            var quality = double.IsNaN(QualityBox.Value) ? 92 : (int)QualityBox.Value;
            var dpi = double.IsNaN(DpiBox.Value) ? 300 : DpiBox.Value;
            for (var jobIndex = 0; jobIndex < jobs.Length; jobIndex++)
            {
                var job = jobs[jobIndex];
                var session = job.Session!;
                var offset = completedBefore;
                var displayJobNumber = jobIndex + 1;
                var progress = new Progress<(int completed, int total, string fileName)>(value =>
                {
                    ExportProgress.Value = (offset + value.completed) * 100d / totalPages;
                    StatusText.Text = $"PDF {displayJobNumber:N0} of {jobs.Length:N0} · {value.fileName}";
                });
                await PdfConversionService.ExportAsync(session, job.OutputFolder, quality, dpi, progress, _exportCancellation.Token, job.OutputName);
                completedBefore += job.Pages.Count(page => page.IsIncluded);
                AlbumFileIndexService.Invalidate(job.OutputFolder);
            }
            OpenOutputButton.IsEnabled = _selectedJob is not null && Directory.Exists(_selectedJob.OutputFolder);
            StatusText.Text = $"Export complete. {totalPages:N0} JPG pages from {jobs.Length:N0} PDFs are ready.";
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "Batch PDF conversion complete", Content = $"Exported {totalPages:N0} pages from {jobs.Length:N0} PDFs. The original PDFs were not changed.", CloseButtonText = "Close" };
            await dialog.ShowAsync();
        }
        catch (OperationCanceledException) { StatusText.Text = "Batch export cancelled. Completed JPG pages remain in their output folders."; }
        catch (Exception ex) { StatusText.Text = $"Batch export stopped safely: {ex.Message}"; }
        finally
        {
            _exportCancellation.Dispose(); _exportCancellation = null;
            CancelButton.IsEnabled = false; SetEditingEnabled(true); UpdateIncludedCount();
        }
    }

    private void SetEditingEnabled(bool enabled)
    {
        PageList.IsEnabled = enabled;
        AddPdfButton.IsEnabled = enabled;
        DocumentList.IsEnabled = enabled;
        ChooseOutputButton.IsEnabled = enabled;
        OutputPathBox.IsEnabled = enabled;
        QualityBox.IsEnabled = enabled;
        DpiBox.IsEnabled = enabled;
        RotateLeftButton.IsEnabled = enabled && PageList.SelectedItem is not null;
        RotateRightButton.IsEnabled = enabled && PageList.SelectedItem is not null;
        ResetRotationButton.IsEnabled = enabled && PageList.SelectedItem is PdfPagePreviewItem { Edit.RotationQuarterTurns: not 0 };
        if (!enabled) ExportButton.IsEnabled = ExportAllButton.IsEnabled = false;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) { CancelButton.IsEnabled = false; StatusText.Text = "Cancelling export..."; _exportCancellation?.Cancel(); }
    private async void Fit_Click(object sender, RoutedEventArgs e) => await CenterPreviewAsync();
    private async void PreviewImage_ImageOpened(object sender, RoutedEventArgs e)
    {
        ApplyPreviewRotationAndFit();
        await CenterPreviewAsync();
    }
    private void ApplyPreviewRotationAndFit()
    {
        if (PageList.SelectedItem is not PdfPagePreviewItem item || PreviewImage.Source is null
            || _previewPixelWidth == 0 || _previewPixelHeight == 0) return;
        PreviewRotationTransform.Angle = item.Edit.RotationDegrees;
        var sideways = item.Edit.RotationQuarterTurns % 2 == 1;
        var rotatedWidth = sideways ? _previewPixelHeight : _previewPixelWidth;
        var rotatedHeight = sideways ? _previewPixelWidth : _previewPixelHeight;
        var availableWidth = Math.Max(1, PreviewScroller.ViewportWidth - 32);
        var availableHeight = Math.Max(1, PreviewScroller.ViewportHeight - 32);
        var scale = Math.Min(1d, Math.Min(availableWidth / rotatedWidth, availableHeight / rotatedHeight));
        PreviewImage.Width = Math.Max(1, _previewPixelWidth * scale);
        PreviewImage.Height = Math.Max(1, _previewPixelHeight * scale);
    }
    private static (uint Width, uint Height) ReadPngDimensions(byte[] bytes)
    {
        if (bytes.Length < 24 || bytes[0] != 137 || bytes[1] != 80 || bytes[2] != 78 || bytes[3] != 71) return (0, 0);
        var width = ((uint)bytes[16] << 24) | ((uint)bytes[17] << 16) | ((uint)bytes[18] << 8) | bytes[19];
        var height = ((uint)bytes[20] << 24) | ((uint)bytes[21] << 16) | ((uint)bytes[22] << 8) | bytes[23];
        return (width, height);
    }
    private async Task CenterPreviewAsync()
    {
        var request = ++_centerRequest;
        PreviewCanvas.MinWidth = Math.Max(300, PreviewScroller.ViewportWidth);
        PreviewCanvas.MinHeight = Math.Max(300, PreviewScroller.ViewportHeight);
        ApplyPreviewRotationAndFit();
        PreviewScroller.ChangeView(0, 0, 1, true);
        await Task.Delay(16);
        if (request != _centerRequest) return;
        PreviewCanvas.UpdateLayout(); PreviewScroller.UpdateLayout();
        await Task.Delay(32);
        if (request != _centerRequest) return;
        var horizontal = Math.Max(0, PreviewScroller.ScrollableWidth / 2d);
        var vertical = Math.Max(0, PreviewScroller.ScrollableHeight / 2d);
        PreviewScroller.ChangeView(horizontal, vertical, 1, true);
    }
    private async void PreviewScroller_SizeChanged(object sender, SizeChangedEventArgs e) => await CenterPreviewAsync();
    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Left) { Previous_Click(sender, e); e.Handled = true; }
        else if (e.Key == Windows.System.VirtualKey.Right) { Next_Click(sender, e); e.Handled = true; }
        else if (e.Key == Windows.System.VirtualKey.Q) { RotateLeft_Click(sender, e); e.Handled = true; }
        else if (e.Key == Windows.System.VirtualKey.E) { RotateRight_Click(sender, e); e.Handled = true; }
    }
    private void Workspace_Unloaded(object sender, RoutedEventArgs e) => CancelDocumentWork();
    private void CancelDocumentWork()
    {
        _documentCancellation?.Cancel(); _documentCancellation?.Dispose(); _documentCancellation = null;
        _thumbnailCancellation?.Cancel(); _thumbnailCancellation?.Dispose(); _thumbnailCancellation = null;
        _editThumbnailCancellation?.Cancel(); _editThumbnailCancellation?.Dispose(); _editThumbnailCancellation = null;
        _previewCancellation?.Cancel(); _previewCancellation?.Dispose(); _previewCancellation = null;
        _exportCancellation?.Cancel();
    }
}
