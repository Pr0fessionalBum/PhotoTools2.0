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
    private string? _alternateOutputRoot;
    public ObservableCollection<FileBrowserItem> PngImages { get; } = [];
    public event EventHandler? ContinueToReplacements;
    public PngToJpgWorkspace()
    {
        InitializeComponent();
        _alternateOutputRoot = AppSettings.Get("JpgOutputRoot");
    }

    public void RefreshFromCurrentAlbum() { var path = AppSettings.Get("CurrentAlbumPath"); if (Directory.Exists(path)) LoadFolder(path); }
    private async void ChooseFolder_Click(object sender, RoutedEventArgs e) { if (await FolderBrowserService.PickFolderAsync() is { } path) LoadFolder(path); }
    private void Workspace_DragEnter(object sender, DragEventArgs e) { if (e.DataView.Contains(StandardDataFormats.StorageItems)) e.AcceptedOperation = DataPackageOperation.Copy; }
    private async void Workspace_Drop(object sender, DragEventArgs e) { if (e.DataView.Contains(StandardDataFormats.StorageItems) && (await e.DataView.GetStorageItemsAsync()).FirstOrDefault() is StorageFolder folder) LoadFolder(folder.Path); }

    private void LoadFolder(string path)
    {
        path = FolderBrowserService.NormalizeExistingFolder(path) ?? path;
        FolderPathBox.Text = path; AppSettings.Set("CurrentAlbumPath", path); PngImages.Clear();
        foreach (var item in FolderBrowserService.Enumerate(path, file => Path.GetExtension(file).Equals(".png", StringComparison.OrdinalIgnoreCase))) PngImages.Add(item);
        var imageCount = PngImages.Count(item => !item.IsFolder);
        CountText.Text = $"{imageCount:N0} PNG files"; ConvertButton.IsEnabled = imageCount > 0;
        var jpg = GetOutputFolder(); OutputPathText.Text = $"Output: {jpg}"; var ready = Directory.Exists(jpg) && Directory.EnumerateFiles(jpg).Any(IsJpeg);
        ReviewButton.IsEnabled = ready; ContinueButton.IsEnabled = ready;
        ProgressText.Text = imageCount > 0 ? "Ready. Double-click folders to browse; only PNG images are converted." : "No PNG files found here.";
    }

    private async void Convert_Click(object sender, RoutedEventArgs e)
    {
        var files = PngGrid.SelectedItems.Cast<FileBrowserItem>().Where(item => !item.IsFolder).ToArray(); if (files.Length == 0) files = PngImages.Where(item => !item.IsFolder).ToArray(); if (files.Length == 0) return;
        var output = GetOutputFolder(); Directory.CreateDirectory(output);
        _cancellation = new CancellationTokenSource(); ConvertButton.IsEnabled = false; CancelButton.IsEnabled = true; ReviewButton.IsEnabled = false; ContinueButton.IsEnabled = false;
        var completed = 0; var failed = 0; var cancelled = false;
        foreach (var file in files)
        {
            ProgressText.Text = $"Converting {completed + failed + 1:N0} of {files.Length:N0}: {file.Name}";
            var destination = Path.Combine(output, Path.GetFileNameWithoutExtension(file.Name) + ".jpg");
            var code = await ConvertOneAsync(file.Path, destination, (int)QualityBox.Value, _cancellation.Token);
            if (code == -2) { cancelled = true; break; }
            if (code == 0 && File.Exists(destination)) completed++; else failed++;
            Progress.Value = (completed + failed) * 100d / files.Length;
        }
        CancelButton.IsEnabled = false; ConvertButton.IsEnabled = true; _cancellation.Dispose(); _cancellation = null;
        ReviewButton.IsEnabled = completed > 0; ContinueButton.IsEnabled = completed > 0;
        ProgressText.Text = cancelled ? $"Cancelled safely. {completed:N0} completed JPG files remain staged." : $"Finished: {completed:N0} converted, {failed:N0} failed. Review JPG files before replacement.";
        if (cancelled) return;
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "Conversion complete", Content = $"{completed:N0} JPG files are ready for review. PNG originals have not been changed.", PrimaryButtonText = "Review JPG folder", CloseButtonText = "Stay here", DefaultButton = ContentDialogButton.Primary };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) OpenPath(output);
    }

    private static async Task<int> ConvertOneAsync(string source, string destination, int quality, CancellationToken token)
    {
        try
        {
            var start = new ProcessStartInfo("magick") { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add(source); start.ArgumentList.Add("-auto-orient"); start.ArgumentList.Add("-background"); start.ArgumentList.Add("white"); start.ArgumentList.Add("-alpha"); start.ArgumentList.Add("remove"); start.ArgumentList.Add("-alpha"); start.ArgumentList.Add("off"); start.ArgumentList.Add("-quality"); start.ArgumentList.Add(quality.ToString()); start.ArgumentList.Add(destination);
            using var process = Process.Start(start); if (process is null) return -1;
            try { await process.WaitForExitAsync(token); } catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); return -2; }
            return process.ExitCode;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return -1; }
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
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary }; picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        if (await picker.PickSingleFolderAsync() is not { } folder) return;
        _alternateOutputRoot = folder.Path; AppSettings.Set("JpgOutputRoot", folder.Path); OutputPathText.Text = $"Output: {GetOutputFolder()}";
    }
    private void ResetOutputRoot_Click(object sender, RoutedEventArgs e) { _alternateOutputRoot = null; AppSettings.Set("JpgOutputRoot", string.Empty); OutputPathText.Text = $"Output: {GetOutputFolder()}"; }
    private string GetOutputFolder()
    {
        if (string.IsNullOrWhiteSpace(_alternateOutputRoot)) return Path.Combine(FolderPathBox.Text, "JPG");
        return Path.Combine(_alternateOutputRoot, Path.GetFileName(FolderPathBox.Text.TrimEnd(Path.DirectorySeparatorChar)), "JPG");
    }
    private void Review_Click(object sender, RoutedEventArgs e) { var path = GetOutputFolder(); if (Directory.Exists(path)) OpenPath(path); }
    private void Continue_Click(object sender, RoutedEventArgs e) => ContinueToReplacements?.Invoke(this, EventArgs.Empty);
    private void OpenFolder_Click(object sender, RoutedEventArgs e) { if (Directory.Exists(FolderPathBox.Text)) FolderBrowserService.OpenFolder(FolderPathBox.Text); }
    private static void OpenPath(string path) => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    private static void OpenFile(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    private static void RevealFile(string path) { var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true }; start.ArgumentList.Add($"/select,{path}"); Process.Start(start); }
    private static bool IsJpeg(string path) => Path.GetExtension(path).Equals(".jpg", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
}
