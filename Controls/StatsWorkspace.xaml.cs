using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PhotoTools2.Models;
using PhotoTools2.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace PhotoTools2.Controls;

public sealed partial class StatsWorkspace : UserControl
{
    private CancellationTokenSource? _scanCancellation;
    private StatsResult? _lastResult;
    public ObservableCollection<PhotoSessionItem> Sessions { get; } = [];

    public StatsWorkspace()
    {
        InitializeComponent();
        SessionsList.ItemsSource = Sessions;
        if (int.TryParse(AppSettings.Get("StatsGapMinutes"), out var gap) && gap > 0) GapMinutesBox.Value = gap;
    }

    public void RefreshFromCurrentAlbum()
    {
        var path = AppSettings.Get("CurrentAlbumPath");
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)) SetFolder(path);
    }

    private async void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        if (await FolderBrowserService.PickFolderAsync() is { } path) SetFolder(path);
    }

    private void SetFolder(string path)
    {
        path = FolderBrowserService.NormalizeExistingFolder(path) ?? path;
        FolderPathBox.Text = path;
        AppSettings.Set("CurrentAlbumPath", path);
        StatusText.Text = "Ready to scan this album and its subfolders.";
        ExportButton.IsEnabled = false;
    }

    private void FolderPathBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        var path = FolderBrowserService.NormalizeExistingFolder(FolderPathBox.Text);
        if (path is not null) SetFolder(path); else StatusText.Text = "That folder path could not be found.";
        e.Handled = true;
    }

    private async void Generate_Click(object sender, RoutedEventArgs e)
    {
        var folderPath = FolderBrowserService.NormalizeExistingFolder(FolderPathBox.Text);
        if (folderPath is null) { StatusText.Text = "Choose a valid album folder first."; return; }
        var gap = Math.Max(1, (int)GapMinutesBox.Value);
        AppSettings.Set("StatsGapMinutes", gap.ToString());
        _scanCancellation = new CancellationTokenSource();
        SetBusy(true);
        StatusText.Text = "Scanning images and creation dates...";
        try
        {
            var result = await Task.Run(() => BuildStats(folderPath, gap, _scanCancellation.Token));
            _lastResult = result;
            ShowResult(result);
            ExportButton.IsEnabled = true;
            StatusText.Text = $"Complete - found {result.PhotoCount:N0} images and {result.Sessions.Count:N0} billing sections.";
        }
        catch (OperationCanceledException) { StatusText.Text = "Stats scan cancelled."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { StatusText.Text = $"Could not scan the folder: {ex.Message}"; }
        catch (Exception ex) { StatusText.Text = $"Stats stopped safely: {ex.Message}"; }
        finally { SetBusy(false); _scanCancellation?.Dispose(); _scanCancellation = null; }
    }

    private void ShowResult(StatsResult result)
    {
        PhotoCountText.Text = result.PhotoCount.ToString("N0");
        FolderCountText.Text = result.FolderCount.ToString("N0");
        TotalSizeText.Text = FormatBytes(result.TotalBytes);
        AverageSizeText.Text = FormatBytes(result.PhotoCount == 0 ? 0 : result.TotalBytes / result.PhotoCount);
        ActiveTimeText.Text = PhotoSessionItem.FormatDuration(result.ActiveTime);
        DateRangeText.Text = result.OldestModified is null ? "No images scanned" : $"{result.OldestModified:g} - {result.NewestModified:g}";
        Sessions.Clear(); foreach (var session in result.Sessions) Sessions.Add(session);
        SessionSummaryText.Text = result.Sessions.Count == 0 ? "No creation dates were available." : $"{result.Sessions.Select(x => x.Start.Date).Distinct().Count():N0} days - {result.Sessions.Count:N0} sections - split after {result.GapMinutes:N0} idle minutes";
        FileTypesList.ItemsSource = result.Extensions.OrderByDescending(x => x.Value).Select(x => $".{x.Key.ToUpperInvariant()}   {x.Value:N0} photos").ToArray();
    }

    private static StatsResult BuildStats(string root, int gapMinutes, CancellationToken token)
    {
        var files = new List<FileInfo>();
        var folders = 1;
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var folder = pending.Pop();
            try
            {
                foreach (var child in Directory.EnumerateDirectories(folder)) { pending.Push(child); folders++; }
                foreach (var path in Directory.EnumerateFiles(folder)) if (IsImage(path)) files.Add(new FileInfo(path));
            }
            catch (UnauthorizedAccessException) { }
            catch (DirectoryNotFoundException) { }
        }
        var orderedDates = files.Select(x => x.CreationTime).Where(x => x.Year > 1900).OrderBy(x => x).ToArray();
        var sessions = new List<PhotoSessionItem>();
        if (orderedDates.Length > 0)
        {
            var start = orderedDates[0]; var previous = start; var count = 1; var section = 1;
            for (var i = 1; i < orderedDates.Length; i++)
            {
                var current = orderedDates[i];
                if (current.Date != previous.Date || current - previous > TimeSpan.FromMinutes(gapMinutes))
                {
                    sessions.Add(new PhotoSessionItem { Start = start, End = previous, PhotoCount = count, SectionNumber = section });
                    section = current.Date == previous.Date ? section + 1 : 1; start = current; count = 1;
                }
                else count++;
                previous = current;
            }
            sessions.Add(new PhotoSessionItem { Start = start, End = previous, PhotoCount = count, SectionNumber = section });
        }
        return new StatsResult(root, gapMinutes, files.Count, folders, files.Sum(x => x.Length), files.Count == 0 ? null : files.Min(x => x.LastWriteTime), files.Count == 0 ? null : files.Max(x => x.LastWriteTime), files.GroupBy(x => x.Extension.TrimStart('.').ToLowerInvariant()).ToDictionary(x => x.Key, x => x.Count()), sessions);
    }

    private void SetBusy(bool busy) { BusyRing.IsActive = busy; ScanProgress.IsIndeterminate = busy; GenerateButton.IsEnabled = !busy; CancelButton.IsEnabled = busy; }
    private void Cancel_Click(object sender, RoutedEventArgs e) { CancelButton.IsEnabled = false; StatusText.Text = "Cancelling..."; _scanCancellation?.Cancel(); }
    private void OpenFolder_Click(object sender, RoutedEventArgs e) { if (Directory.Exists(FolderPathBox.Text)) FolderBrowserService.OpenFolder(FolderPathBox.Text); }
    private void UpFolder_Click(object sender, RoutedEventArgs e) { if (FolderBrowserService.GetParent(FolderPathBox.Text) is { } parent) SetFolder(parent); }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_lastResult is null) return;
        var picker = new FileSavePicker { SuggestedFileName = $"{Path.GetFileName(_lastResult.RootPath)} photo stats" };
        picker.FileTypeChoices.Add("CSV spreadsheet", [".csv"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        var file = await picker.PickSaveFileAsync(); if (file is null) return;
        var csv = new StringBuilder("Date,Section,Start,End,Photos,Active Minutes\r\n");
        foreach (var item in _lastResult.Sessions) csv.AppendLine($"\"{item.Start:d}\",{item.SectionNumber},\"{item.Start:t}\",\"{item.End:t}\",{item.PhotoCount},{Math.Max(0, (int)Math.Round((item.End-item.Start).TotalMinutes))}");
        await File.WriteAllTextAsync(file.Path, csv.ToString(), Encoding.UTF8); StatusText.Text = $"Exported {file.Name}.";
    }

    private void Workspace_DragEnter(object sender, DragEventArgs e) { if (e.DataView.Contains(StandardDataFormats.StorageItems)) e.AcceptedOperation = DataPackageOperation.Copy; }
    private async void Workspace_Drop(object sender, DragEventArgs e) { var items = await e.DataView.GetStorageItemsAsync(); var folder = items.OfType<Windows.Storage.StorageFolder>().FirstOrDefault(); if (folder is not null) SetFolder(folder.Path); }
    private static bool IsImage(string path) => new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    private static string FormatBytes(long bytes) { string[] units = ["B", "KB", "MB", "GB", "TB"]; double value = bytes; var unit = 0; while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; } return $"{value:0.##} {units[unit]}"; }

    private sealed record StatsResult(string RootPath, int GapMinutes, int PhotoCount, int FolderCount, long TotalBytes, DateTime? OldestModified, DateTime? NewestModified, Dictionary<string, int> Extensions, List<PhotoSessionItem> Sessions)
    {
        public TimeSpan ActiveTime => TimeSpan.FromTicks(Sessions.Sum(x => (x.End - x.Start).Ticks));
    }
}
