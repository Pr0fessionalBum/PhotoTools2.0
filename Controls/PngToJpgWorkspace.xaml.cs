using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PhotoTools2.Models;
using PhotoTools2.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace PhotoTools2.Controls;

public sealed partial class PngToJpgWorkspace : UserControl
{
    private CancellationTokenSource? _cancellation;
    private CancellationTokenSource? _folderLoadCancellation;
    private string? _alternateOutputRoot;
    public ObservableCollection<FileBrowserItem> PngImages { get; } = [];
    public event EventHandler? ContinueToReplacements;
    public PngToJpgWorkspace()
    {
        InitializeComponent();
        _alternateOutputRoot = AppSettings.Get("JpgOutputRoot");
    }

    public void RefreshFromCurrentAlbum() { var path = AppSettings.Get("CurrentAlbumPath"); if (Directory.Exists(path)) LoadFolder(path); }
    public void LoadAlbumSelection(string folderPath, IReadOnlyCollection<string> selectedPaths)
    {
        _ = LoadAlbumSelectionAsync(folderPath, selectedPaths);
    }

    private async Task LoadAlbumSelectionAsync(string folderPath, IReadOnlyCollection<string> selectedPaths)
    {
        if (!await LoadFolderAsync(folderPath)) return;
        var selected = selectedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in PngImages.Where(item => !item.IsFolder && selected.Contains(item.Path))) PngGrid.SelectedItems.Add(item);
    }
    private async void ChooseFolder_Click(object sender, RoutedEventArgs e) { if (await FolderBrowserService.PickFolderAsync() is { } path) LoadFolder(path); }
    private void Workspace_DragEnter(object sender, DragEventArgs e) { if (e.DataView.Contains(StandardDataFormats.StorageItems)) e.AcceptedOperation = DataPackageOperation.Copy; }
    private async void Workspace_Drop(object sender, DragEventArgs e) { if (e.DataView.Contains(StandardDataFormats.StorageItems) && (await e.DataView.GetStorageItemsAsync()).FirstOrDefault() is StorageFolder folder) LoadFolder(folder.Path); }

    private async void LoadFolder(string path) => await LoadFolderAsync(path);

    private async Task<bool> LoadFolderAsync(string path)
    {
        path = FolderBrowserService.NormalizeExistingFolder(path) ?? path;
        _folderLoadCancellation?.Cancel();
        _folderLoadCancellation?.Dispose();
        var cancellation = _folderLoadCancellation = new CancellationTokenSource();
        FolderPathBox.Text = path; AppSettings.Set("CurrentAlbumPath", path); PngImages.Clear();
        ProgressText.Text = "Loading folder...";
        IReadOnlyList<FileBrowserItem> items;
        try { items = await FolderBrowserService.EnumerateAsync(path, ImageFileFormats.IsPng, cancellation.Token); }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ProgressText.Text = $"Could not open this folder: {ex.Message}";
            return false;
        }
        if (!ReferenceEquals(_folderLoadCancellation, cancellation)) return false;
        foreach (var item in items) PngImages.Add(item);
        var imageCount = PngImages.Count(item => !item.IsFolder);
        CountText.Text = $"{imageCount:N0} PNG files"; ConvertButton.IsEnabled = imageCount > 0;
        var jpg = GetOutputFolder(); OutputPathText.Text = $"Output: {jpg}"; var ready = Directory.Exists(jpg) && Directory.EnumerateFiles(jpg).Any(ImageFileFormats.IsJpeg);
        ReviewButton.IsEnabled = ready; ContinueButton.IsEnabled = ready;
        ProgressText.Text = imageCount > 0 ? "Ready. Double-click folders to browse; only PNG images are converted." : "No PNG files found here.";
        return true;
    }

    private async void Convert_Click(object sender, RoutedEventArgs e)
    {
        var files = PngGrid.SelectedItems.Cast<FileBrowserItem>().Where(item => !item.IsFolder).ToArray(); if (files.Length == 0) files = PngImages.Where(item => !item.IsFolder).ToArray(); if (files.Length == 0) return;
        var output = GetOutputFolder(); Directory.CreateDirectory(output);
        _cancellation = new CancellationTokenSource(); ConvertButton.IsEnabled = false; CancelButton.IsEnabled = true; ReviewButton.IsEnabled = false; ContinueButton.IsEnabled = false;
        var completed = 0; var failed = 0; var cancelled = false; string? lastError = null;
        foreach (var file in files)
        {
            ProgressText.Text = $"Converting {completed + failed + 1:N0} of {files.Length:N0}: {file.Name}";
            var destination = Path.Combine(output, Path.GetFileNameWithoutExtension(file.Name) + ".jpg");
            var result = await ConvertOneAsync(file.Path, destination, (int)QualityBox.Value, _cancellation.Token);
            if (result.WasCancelled) { cancelled = true; break; }
            if (result.Succeeded && File.Exists(destination)) completed++; else { failed++; lastError = result.ErrorMessage; }
            Progress.Value = (completed + failed) * 100d / files.Length;
        }
        CancelButton.IsEnabled = false; ConvertButton.IsEnabled = true; _cancellation.Dispose(); _cancellation = null;
        ReviewButton.IsEnabled = completed > 0; ContinueButton.IsEnabled = completed > 0;
        ProgressText.Text = cancelled ? $"Cancelled safely. {completed:N0} completed JPG files remain staged." : $"Finished: {completed:N0} converted, {failed:N0} failed. Review JPG files before replacement." + (lastError is null ? string.Empty : $" Last error: {lastError}");
        AlbumFileIndexService.Invalidate(FolderPathBox.Text);
        AlbumFileIndexService.Invalidate(Directory.GetParent(output)?.FullName);
        if (cancelled) return;
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "Conversion complete", Content = $"{completed:N0} JPG files are ready for review. PNG originals have not been changed.", PrimaryButtonText = "Review JPG folder", CloseButtonText = "Stay here", DefaultButton = ContentDialogButton.Primary };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) FolderBrowserService.OpenFolder(output);
    }

    private static Task<ExternalProcessResult> ConvertOneAsync(string source, string destination, int quality, CancellationToken token)
    {
        string[] arguments = [source, "-auto-orient", "-background", "white", "-alpha", "remove", "-alpha", "off", "-quality", quality.ToString(), destination];
        return ImageMagickService.RunAsync(arguments, token);
    }

    private void PngGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = PngGrid.SelectedItems.Cast<FileBrowserItem>().Count(item => !item.IsFolder);
        var total = PngImages.Count(item => !item.IsFolder);
        SelectionText.Text = selected == 0 ? "Nothing selected - all PNG files will be converted" : $"{selected:N0} of {total:N0} selected";
    }
    private void SelectAll_Click(object sender, RoutedEventArgs e) { PngGrid.SelectedItems.Clear(); foreach (var item in PngImages.Where(x => !x.IsFolder)) PngGrid.SelectedItems.Add(item); }
    private void Clear_Click(object sender, RoutedEventArgs e) => PngGrid.SelectedItems.Clear();
    private void Invert_Click(object sender, RoutedEventArgs e) { var selected = PngGrid.SelectedItems.Cast<FileBrowserItem>().ToHashSet(); PngGrid.SelectedItems.Clear(); foreach (var item in PngImages.Where(item => !item.IsFolder && !selected.Contains(item))) PngGrid.SelectedItems.Add(item); }
    private void PngGrid_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) { if (PngGrid.SelectedItem is FileBrowserItem item) FolderBrowserService.OpenItem(item, LoadFolder); }
    private void PngGrid_KeyDown(object sender, KeyRoutedEventArgs e) { if (e.Key == Windows.System.VirtualKey.Enter && PngGrid.SelectedItem is FileBrowserItem item) { FolderBrowserService.OpenItem(item, LoadFolder); e.Handled = true; } }
    private void OpenPhoto_Click(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.Tag is FileBrowserItem item) FolderBrowserService.OpenItem(item, LoadFolder); }
    private void RevealPhoto_Click(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.Tag is FileBrowserItem item) FolderBrowserService.Reveal(item.Path); }
    private void FolderPathBox_KeyDown(object sender, KeyRoutedEventArgs e) { if (e.Key != Windows.System.VirtualKey.Enter) return; var path = FolderBrowserService.NormalizeExistingFolder(FolderPathBox.Text); if (path is not null) LoadFolder(path); else ProgressText.Text = "That folder path could not be found."; e.Handled = true; }
    private void UpFolder_Click(object sender, RoutedEventArgs e) { if (FolderBrowserService.GetParent(FolderPathBox.Text) is { } parent) LoadFolder(parent); }
    private void Cancel_Click(object sender, RoutedEventArgs e) { CancelButton.IsEnabled = false; ProgressText.Text = "Cancelling the active conversion..."; _cancellation?.Cancel(); }
    private async void ChooseOutputRoot_Click(object sender, RoutedEventArgs e)
    {
        if (await FolderBrowserService.PickFolderAsync() is not { } path) return;
        _alternateOutputRoot = path; AppSettings.Set("JpgOutputRoot", path); OutputPathText.Text = $"Output: {GetOutputFolder()}";
    }
    private void ResetOutputRoot_Click(object sender, RoutedEventArgs e) { _alternateOutputRoot = null; AppSettings.Set("JpgOutputRoot", string.Empty); OutputPathText.Text = $"Output: {GetOutputFolder()}"; }
    private string GetOutputFolder()
    {
        if (string.IsNullOrWhiteSpace(_alternateOutputRoot)) return Path.Combine(FolderPathBox.Text, "JPG");
        return Path.Combine(_alternateOutputRoot, Path.GetFileName(FolderPathBox.Text.TrimEnd(Path.DirectorySeparatorChar)), "JPG");
    }
    private void Review_Click(object sender, RoutedEventArgs e) { var path = GetOutputFolder(); if (Directory.Exists(path)) FolderBrowserService.OpenFolder(path); }
    private void Continue_Click(object sender, RoutedEventArgs e) => ContinueToReplacements?.Invoke(this, EventArgs.Empty);
    private void OpenFolder_Click(object sender, RoutedEventArgs e) { if (Directory.Exists(FolderPathBox.Text)) FolderBrowserService.OpenFolder(FolderPathBox.Text); }
}
