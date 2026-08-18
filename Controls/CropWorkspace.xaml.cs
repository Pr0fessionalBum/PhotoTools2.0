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
        LoadFolder(folderPath);
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

    private void LoadFolder(string path)
    {
        path = FolderBrowserService.NormalizeExistingFolder(path) ?? path;
        FolderPathBox.Text = path;
        AppSettings.Set("CurrentAlbumPath", path);
        SourceImages.Clear();
        foreach (var item in FolderBrowserService.Enumerate(path, IsSupportedImage)) SourceImages.Add(item);
        var imageCount = SourceImages.Count(item => !item.IsFolder);
        ImageCountText.Text = $"{imageCount:N0} images";
        RunButton.IsEnabled = imageCount > 0;
        var output = GetOutputFolder();
        OutputPathText.Text = $"Output: {output}";
        ReviewButton.IsEnabled = Directory.Exists(output) && Directory.EnumerateFiles(output).Any(IsSupportedImage);
        ContinueButton.IsEnabled = ReviewButton.IsEnabled;
        ProgressText.Text = imageCount > 0 ? "Ready to crop. Double-click folders to browse; only images are processed." : "No PNG, JPG, or JPEG photos found here.";
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

        _cropCancellation = new CancellationTokenSource();
        var cancelled = false;
        for (var index = 0; index < files.Length; index++)
        {
            var file = files[index];
            ProgressText.Text = $"Cropping {index + 1:N0} of {files.Length:N0}: {file.Name}";
            CropProgress.Value = index * 100d / files.Length;
            var exitCode = await RunImageMagickAsync(file.Path, Path.Combine(output, file.Name), FuzzBox.Value, ShaveBox.Value, _cropCancellation.Token);
            if (exitCode == -2) { cancelled = true; break; }
            if (exitCode == 0 && File.Exists(Path.Combine(output, file.Name))) succeeded++; else failed++;
        }

        CropProgress.Value = cancelled ? CropProgress.Value : 100;
        ProgressText.Text = cancelled
            ? $"Cancelled safely. {succeeded:N0} completed files remain available for review; originals were untouched."
            : $"Finished: {succeeded:N0} cropped, {failed:N0} failed. Review the output before processing replacements.";
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
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) OpenPath(output);
    }

    private static async Task<int> RunImageMagickAsync(string source, string destination, double fuzz, double shave, CancellationToken cancellationToken)
    {
        try
        {
            var start = new ProcessStartInfo("magick") { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add(source);
            start.ArgumentList.Add("-auto-orient");
            start.ArgumentList.Add("-fuzz"); start.ArgumentList.Add($"{fuzz:0.##}%");
            start.ArgumentList.Add("-trim"); start.ArgumentList.Add("+repage");
            start.ArgumentList.Add("-shave"); start.ArgumentList.Add($"{shave:0.##}%x{shave:0.##}%");
            start.ArgumentList.Add(destination);
            using var process = Process.Start(start);
            if (process is null) return -1;
            try { await process.WaitForExitAsync(cancellationToken); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(true);
                return -2;
            }
            return process.ExitCode;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return -1;
        }
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
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary }; picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        if (await picker.PickSingleFolderAsync() is not { } folder) return;
        _alternateOutputRoot = folder.Path; AppSettings.Set("CropOutputRoot", folder.Path); OutputPathText.Text = $"Output: {GetOutputFolder()}";
    }
    private void ResetOutputRoot_Click(object sender, RoutedEventArgs e) { _alternateOutputRoot = null; AppSettings.Set("CropOutputRoot", string.Empty); OutputPathText.Text = $"Output: {GetOutputFolder()}"; }
    private string GetOutputFolder()
    {
        if (string.IsNullOrWhiteSpace(_alternateOutputRoot)) return Path.Combine(FolderPathBox.Text, "cropped");
        return Path.Combine(_alternateOutputRoot, Path.GetFileName(FolderPathBox.Text.TrimEnd(Path.DirectorySeparatorChar)), "cropped");
    }
    private void ReviewButton_Click(object sender, RoutedEventArgs e) { var path = GetOutputFolder(); if (Directory.Exists(path)) OpenPath(path); }
    private void ContinueButton_Click(object sender, RoutedEventArgs e) => ContinueToReplacements?.Invoke(this, EventArgs.Empty);
    private static void OpenPath(string path) => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    private static void OpenFile(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    private static void RevealFile(string path) { var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true }; start.ArgumentList.Add($"/select,{path}"); Process.Start(start); }
    private static bool IsSupportedImage(string path) => Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".jpg", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
}
