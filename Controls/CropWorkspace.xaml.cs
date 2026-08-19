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

public sealed partial class CropWorkspace : UserControl
{
    private bool _sortDescending;
    private CancellationTokenSource? _cropCancellation;
    private CancellationTokenSource? _folderLoadCancellation;
    private string? _alternateOutputRoot;
    public ObservableCollection<FileBrowserItem> SourceImages { get; } = [];
    public event EventHandler? ContinueToReplacements;

    public CropWorkspace()
    {
        InitializeComponent();
        _alternateOutputRoot = AppSettings.Get("CropOutputRoot");
    }

    public void RefreshFromCurrentAlbum()
    {
        var path = AppSettings.Get("CurrentAlbumPath");
        if (Directory.Exists(path)) LoadFolder(path);
    }

    public void LoadAlbumSelection(string folderPath, IReadOnlyCollection<string> selectedPaths)
    {
        _ = LoadAlbumSelectionAsync(folderPath, selectedPaths);
    }

    private async Task LoadAlbumSelectionAsync(string folderPath, IReadOnlyCollection<string> selectedPaths)
    {
        if (!await LoadFolderAsync(folderPath)) return;
        var selected = selectedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in SourceImages.Where(item => !item.IsFolder && selected.Contains(item.Path))) SourceGrid.SelectedItems.Add(item);
    }

    private async void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        if (await FolderBrowserService.PickFolderAsync() is { } path) LoadFolder(path);
    }

    private void Workspace_DragEnter(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems)) e.AcceptedOperation = DataPackageOperation.Copy;
    }

    private async void Workspace_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        if ((await e.DataView.GetStorageItemsAsync()).FirstOrDefault() is StorageFolder folder) LoadFolder(folder.Path);
    }

    private async void LoadFolder(string path) => await LoadFolderAsync(path);

    private async Task<bool> LoadFolderAsync(string path)
    {
        path = FolderBrowserService.NormalizeExistingFolder(path) ?? path;
        _folderLoadCancellation?.Cancel();
        _folderLoadCancellation?.Dispose();
        var cancellation = _folderLoadCancellation = new CancellationTokenSource();
        FolderPathBox.Text = path;
        AppSettings.Set("CurrentAlbumPath", path);
        SourceImages.Clear();
        ProgressText.Text = "Loading folder...";
        IReadOnlyList<FileBrowserItem> items;
        try { items = await FolderBrowserService.EnumerateAsync(path, ImageFileFormats.IsEditableImage, cancellation.Token); }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ProgressText.Text = $"Could not open this folder: {ex.Message}";
            return false;
        }
        if (!ReferenceEquals(_folderLoadCancellation, cancellation)) return false;
        foreach (var item in items) SourceImages.Add(item);
        var imageCount = SourceImages.Count(item => !item.IsFolder);
        ImageCountText.Text = $"{imageCount:N0} images";
        RunButton.IsEnabled = imageCount > 0;
        var output = GetOutputFolder();
        OutputPathText.Text = $"Output: {output}";
        ReviewButton.IsEnabled = Directory.Exists(output) && Directory.EnumerateFiles(output).Any(ImageFileFormats.IsEditableImage);
        ContinueButton.IsEnabled = ReviewButton.IsEnabled;
        ProgressText.Text = imageCount > 0 ? "Ready to crop. Double-click folders to browse; only images are processed." : "No PNG, JPG, or JPEG photos found here.";
        return true;
    }

    private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (SourceGrid is not null) ApplySort(); }
    private void SortDirection_Click(object sender, RoutedEventArgs e) { _sortDescending = !_sortDescending; SortDirectionButton.Content = _sortDescending ? "Z–A" : "A–Z"; ApplySort(); }
    private void SelectAll_Click(object sender, RoutedEventArgs e) { SourceGrid.SelectedItems.Clear(); foreach (var item in SourceImages.Where(x => !x.IsFolder)) SourceGrid.SelectedItems.Add(item); }
    private void ClearSelection_Click(object sender, RoutedEventArgs e) => SourceGrid.SelectedItems.Clear();
    private void InvertSelection_Click(object sender, RoutedEventArgs e)
    {
        var selected = SourceGrid.SelectedItems.Cast<FileBrowserItem>().ToHashSet();
        SourceGrid.SelectedItems.Clear();
        foreach (var item in SourceImages.Where(item => !item.IsFolder && !selected.Contains(item))) SourceGrid.SelectedItems.Add(item);
    }

    private void SourceGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selectedImages = SourceGrid.SelectedItems.Cast<FileBrowserItem>().Count(item => !item.IsFolder);
        var imageCount = SourceImages.Count(item => !item.IsFolder);
        SelectionText.Text = selectedImages == 0
            ? "Nothing selected — all images will be processed"
            : $"{selectedImages:N0} of {imageCount:N0} selected";
    }

    private void ApplySort()
    {
        Func<FileBrowserItem, object> key = SortBox.SelectedIndex switch
        {
            1 => item => item.Modified,
            2 => item => item.Extension,
            3 => item => item.Size,
            _ => item => item.Name
        };
        var sorted = (_sortDescending ? SourceImages.OrderByDescending(item => item.IsFolder).ThenByDescending(key) : SourceImages.OrderByDescending(item => item.IsFolder).ThenBy(key)).ToArray();
        SourceImages.Clear();
        foreach (var item in sorted) SourceImages.Add(item);
    }

    private async void RunButton_Click(object sender, RoutedEventArgs e)
    {
        var files = SourceGrid.SelectedItems.Cast<FileBrowserItem>().Where(item => !item.IsFolder).ToArray();
        if (files.Length == 0) files = SourceImages.Where(item => !item.IsFolder).ToArray();
        if (files.Length == 0 || !Directory.Exists(FolderPathBox.Text)) return;

        RunButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        ReviewButton.IsEnabled = false;
        ContinueButton.IsEnabled = false;
        var output = GetOutputFolder();
        Directory.CreateDirectory(output);
        var succeeded = 0;
        var failed = 0;
        string? lastError = null;

        _cropCancellation = new CancellationTokenSource();
        var cancelled = false;
        for (var index = 0; index < files.Length; index++)
        {
            var file = files[index];
            ProgressText.Text = $"Cropping {index + 1:N0} of {files.Length:N0}: {file.Name}";
            CropProgress.Value = index * 100d / files.Length;
            var result = await RunImageMagickAsync(file.Path, Path.Combine(output, file.Name), FuzzBox.Value, ShaveBox.Value, _cropCancellation.Token);
            if (result.WasCancelled) { cancelled = true; break; }
            if (result.Succeeded && File.Exists(Path.Combine(output, file.Name))) succeeded++; else { failed++; lastError = result.ErrorMessage; }
        }

        CropProgress.Value = cancelled ? CropProgress.Value : 100;
        ProgressText.Text = cancelled
            ? $"Cancelled safely. {succeeded:N0} completed files remain available for review; originals were untouched."
            : $"Finished: {succeeded:N0} cropped, {failed:N0} failed. Review the output before processing replacements." + (lastError is null ? string.Empty : $" Last error: {lastError}");
        AlbumFileIndexService.Invalidate(FolderPathBox.Text);
        AlbumFileIndexService.Invalidate(Directory.GetParent(output)?.FullName);
        ReviewButton.IsEnabled = succeeded > 0;
        ContinueButton.IsEnabled = succeeded > 0;
        RunButton.IsEnabled = true;
        CancelButton.IsEnabled = false;
        _cropCancellation.Dispose();
        _cropCancellation = null;

        if (cancelled) return;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Cropping complete",
            Content = $"{succeeded:N0} photos are ready in the cropped folder. Review them before continuing to replacement processing.",
            PrimaryButtonText = "Review cropped folder",
            CloseButtonText = "Stay here",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) FolderBrowserService.OpenFolder(output);
    }

    private static Task<ExternalProcessResult> RunImageMagickAsync(string source, string destination, double fuzz, double shave, CancellationToken cancellationToken)
    {
        string[] arguments = [source, "-auto-orient", "-fuzz", $"{fuzz:0.##}%", "-trim", "+repage", "-shave", $"{shave:0.##}%x{shave:0.##}%", destination];
        return ImageMagickService.RunAsync(arguments, cancellationToken);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) { if (Directory.Exists(FolderPathBox.Text)) FolderBrowserService.OpenFolder(FolderPathBox.Text); }
    private void SourceGrid_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) { if (SourceGrid.SelectedItem is FileBrowserItem item) FolderBrowserService.OpenItem(item, LoadFolder); }
    private void OpenPhoto_Click(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.Tag is FileBrowserItem item) FolderBrowserService.OpenItem(item, LoadFolder); }
    private void RevealPhoto_Click(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.Tag is FileBrowserItem item) FolderBrowserService.Reveal(item.Path); }
    private void FolderPathBox_KeyDown(object sender, KeyRoutedEventArgs e) { if (e.Key != Windows.System.VirtualKey.Enter) return; var path = FolderBrowserService.NormalizeExistingFolder(FolderPathBox.Text); if (path is not null) LoadFolder(path); else ProgressText.Text = "That folder path could not be found."; e.Handled = true; }
    private void UpFolder_Click(object sender, RoutedEventArgs e) { if (FolderBrowserService.GetParent(FolderPathBox.Text) is { } parent) LoadFolder(parent); }
    private void CancelButton_Click(object sender, RoutedEventArgs e) { CancelButton.IsEnabled = false; ProgressText.Text = "Cancelling after the current process stops..."; _cropCancellation?.Cancel(); }
    private async void ChooseOutputRoot_Click(object sender, RoutedEventArgs e)
    {
        if (await FolderBrowserService.PickFolderAsync() is not { } path) return;
        _alternateOutputRoot = path; AppSettings.Set("CropOutputRoot", path); OutputPathText.Text = $"Output: {GetOutputFolder()}";
    }
    private void ResetOutputRoot_Click(object sender, RoutedEventArgs e) { _alternateOutputRoot = null; AppSettings.Set("CropOutputRoot", string.Empty); OutputPathText.Text = $"Output: {GetOutputFolder()}"; }
    private string GetOutputFolder()
    {
        if (string.IsNullOrWhiteSpace(_alternateOutputRoot)) return Path.Combine(FolderPathBox.Text, "cropped");
        return Path.Combine(_alternateOutputRoot, Path.GetFileName(FolderPathBox.Text.TrimEnd(Path.DirectorySeparatorChar)), "cropped");
    }
    private void ReviewButton_Click(object sender, RoutedEventArgs e) { var path = GetOutputFolder(); if (Directory.Exists(path)) FolderBrowserService.OpenFolder(path); }
    private void ContinueButton_Click(object sender, RoutedEventArgs e) => ContinueToReplacements?.Invoke(this, EventArgs.Empty);
}
