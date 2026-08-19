using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PhotoTools2.Models;
using PhotoTools2.Services;
using Windows.Storage.Pickers;

namespace PhotoTools2.Controls;

public sealed partial class RenameWorkspace : UserControl
{
    private Process? _activeProcess;
    private bool _cancelRequested;
    private CancellationTokenSource? _folderLoadCancellation;
    public ObservableCollection<FileBrowserItem> Photos { get; } = [];
    public ObservableCollection<RenamePreviewItem> PreviewItems { get; } = [];

    public RenameWorkspace()
    {
        InitializeComponent();
        PhotosList.ItemsSource = Photos;
        PreviewList.ItemsSource = PreviewItems;
        ChooseFolderButton.Click += ChooseFolder_Click;
        UpFolderButton.Click += UpFolder_Click;
        OpenFolderButton.Click += OpenFolder_Click;
        FolderPathBox.KeyDown += FolderPathBox_KeyDown;
        PhotosList.DoubleTapped += PhotosList_DoubleTapped;
        CancelButton.Click += Cancel_Click;
        PreviewButton.Click += Preview_Click;
        ApplyButton.Click += Apply_Click;
        Loaded += RenameWorkspace_Loaded;
    }

    private bool IsDuplex => string.Equals(Tag?.ToString(), "Duplex", StringComparison.OrdinalIgnoreCase);

    private void RenameWorkspace_Loaded(object sender, RoutedEventArgs e) => ConfigureMode();

    private void ConfigureMode()
    {
        HeaderTitle.Text = IsDuplex ? "Name Front / Back Scans" : "Fix Scan Numbering";
        HeaderDescription.Text = IsDuplex ? "Pair sorted scan files and apply consistent front/back names." : "Close numbering gaps and quarantine orphaned back scans.";
        var showName = IsDuplex || RecurseBox.IsChecked != true;
        NameLabel.Visibility = showName ? Visibility.Visible : Visibility.Collapsed;
        NameBox.Visibility = showName ? Visibility.Visible : Visibility.Collapsed;
        NameLabel.Text = IsDuplex ? "Album name" : "Album name override (optional)";
        NameBox.PlaceholderText = IsDuplex ? "Example: Family Album" : "Leave blank to auto-detect";
        StartLabel.Visibility = IsDuplex ? Visibility.Visible : Visibility.Collapsed;
        StartBox.Visibility = IsDuplex ? Visibility.Visible : Visibility.Collapsed;
        RecurseBox.Visibility = IsDuplex ? Visibility.Collapsed : Visibility.Visible;
        if (IsDuplex && string.IsNullOrWhiteSpace(NameBox.Text)) NameBox.Text = AppSettings.Get("DuplexName") ?? string.Empty;
    }

    private void RecurseBox_Changed(object sender, RoutedEventArgs e)
    {
        if (IsDuplex || NameLabel is null || NameBox is null) return;
        var showName = RecurseBox.IsChecked != true;
        NameLabel.Visibility = showName ? Visibility.Visible : Visibility.Collapsed;
        NameBox.Visibility = showName ? Visibility.Visible : Visibility.Collapsed;
    }

    public void RefreshFromCurrentAlbum()
    {
        ConfigureMode();
        var path = AppSettings.Get("CurrentAlbumPath");
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)) LoadFolder(path);
    }

    public void LoadAlbumFolder(string folderPath)
    {
        ConfigureMode();
        LoadFolder(folderPath);
    }

    private async void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        if (await FolderBrowserService.PickFolderAsync() is { } path) LoadFolder(path);
    }

    private async void LoadFolder(string path)
    {
        path = FolderBrowserService.NormalizeExistingFolder(path) ?? path;
        _folderLoadCancellation?.Cancel();
        _folderLoadCancellation?.Dispose();
        var cancellation = _folderLoadCancellation = new CancellationTokenSource();
        FolderPathBox.Text = path;
        AppSettings.Set("CurrentAlbumPath", path);
        Photos.Clear();
        StatusText.Text = "Loading folder...";
        IReadOnlyList<FileBrowserItem> items;
        try { items = await FolderBrowserService.EnumerateAsync(path, ImageFileFormats.IsEditableImage, cancellation.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Could not open this folder: {ex.Message}";
            return;
        }
        if (!ReferenceEquals(_folderLoadCancellation, cancellation)) return;
        foreach (var item in items) Photos.Add(item);
        var affectedCount = Photos.Count(item => !item.IsFolder && (!IsDuplex || item.Name.StartsWith("scan", StringComparison.OrdinalIgnoreCase)));
        PhotoCountText.Text = $"{affectedCount:N0} affected photos";
        PreviewItems.Clear();
        PreviewSummaryText.Text = "Preview changes to see the new filenames.";
        ApplyButton.IsEnabled = false;
        StatusText.Text = affectedCount == 0 ? "No matching photos found in this folder." : "Preview is required before applying changes.";
        if (IsDuplex) AutoDetectFromParent(path);
    }

    private void AutoDetectFromParent(string scanFolder)
    {
        var parent = Directory.GetParent(scanFolder);
        if (parent is null) return;
        var matches = Directory.EnumerateFiles(parent.FullName).Where(ImageFileFormats.IsEditableImage).Select(file =>
        {
            var match = Regex.Match(Path.GetFileNameWithoutExtension(file), @"^(.+) \((\d+)(?:B)?\)$", RegexOptions.IgnoreCase);
            return match.Success ? new { Name = match.Groups[1].Value, Number = int.Parse(match.Groups[2].Value), File = Path.GetFileName(file) } : null;
        }).Where(item => item is not null).OrderByDescending(item => item!.Number).ToArray();
        if (matches.FirstOrDefault() is not { } last) return;
        NameBox.Text = last.Name;
        StartBox.Text = (last.Number + 1).ToString();
        StatusText.Text = $"Detected {last.File} in the parent folder. Continuing as {last.Name} ({last.Number + 1}). Preview before applying.";
    }

    private void FolderPathBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        try
        {
            var path = FolderBrowserService.NormalizeExistingFolder(FolderPathBox.Text);
            if (path is not null) LoadFolder(path); else StatusText.Text = "That folder path could not be found.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { StatusText.Text = $"Could not open that folder: {ex.Message}"; }
        e.Handled = true;
    }

    private void PhotosList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (PhotosList.SelectedItem is FileBrowserItem item) FolderBrowserService.OpenItem(item, LoadFolder);
    }

    private void UpFolder_Click(object sender, RoutedEventArgs e)
    {
        if (FolderBrowserService.GetParent(FolderPathBox.Text) is { } parent) LoadFolder(parent);
    }

    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateInputs()) return;
        var result = await ExecuteScriptAsync(true);
        ShowFriendlyResult(result.output, result.exitCode, result.cancelled, true);
        ApplyButton.IsEnabled = result.exitCode == 0 && !result.cancelled;
        StatusText.Text = result.cancelled ? "Preview cancelled." : result.exitCode == 0 ? "Review the complete plan before applying it." : "Preview failed. No files were changed.";
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateInputs()) return;
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "Apply this reviewed rename plan?", Content = "Front/back files will be renamed. Fix Numbering may move orphaned backs into Quarantine.", PrimaryButtonText = "Apply plan", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var result = await ExecuteScriptAsync(false);
        ShowFriendlyResult(result.output, result.exitCode, result.cancelled, false);
        ApplyButton.IsEnabled = false;
        StatusText.Text = result.cancelled ? "Operation cancelled. Refresh and preview before trying again." : result.exitCode == 0 ? "Rename operation completed." : "Rename operation stopped with an error. Review the log.";
    }

    private bool ValidateInputs()
    {
        if (!Directory.Exists(FolderPathBox.Text)) { StatusText.Text = "Choose a valid folder first."; return false; }
        if (IsDuplex && string.IsNullOrWhiteSpace(NameBox.Text)) { StatusText.Text = "Enter the album name for the scan pairs."; return false; }
        if (IsDuplex && (!int.TryParse(StartBox.Text, out var startNumber) || startNumber < 1)) { StatusText.Text = "Enter a starting number of 1 or higher."; return false; }
        if (IsDuplex) AppSettings.Set("DuplexName", NameBox.Text.Trim());
        return true;
    }

    private async Task<(int exitCode, string output, bool cancelled)> ExecuteScriptAsync(bool dryRun)
    {
        var scriptName = IsDuplex ? "New-DuplexNames.ps1" : "Fix-ScanNames.ps1";
        var scriptPath = Path.Combine(AppContext.BaseDirectory, "Scripts", scriptName);
        if (!File.Exists(scriptPath))
            scriptPath = Path.Combine(Directory.GetCurrentDirectory(), "Scripts", scriptName);
        if (!File.Exists(scriptPath))
            return (-1, $"The rename engine is missing: {scriptName}", false);
        PreviewButton.IsEnabled = false; ApplyButton.IsEnabled = false; CancelButton.IsEnabled = true; OperationProgress.IsIndeterminate = true; BusyRing.IsActive = true; _cancelRequested = false;
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-ExecutionPolicy"); start.ArgumentList.Add("Bypass"); start.ArgumentList.Add("-File"); start.ArgumentList.Add(scriptPath); start.ArgumentList.Add("-Path"); start.ArgumentList.Add(FolderPathBox.Text);
        if (IsDuplex) { start.ArgumentList.Add("-Name"); start.ArgumentList.Add(NameBox.Text.Trim()); start.ArgumentList.Add("-Start"); start.ArgumentList.Add(StartBox.Text.Trim()); }
        if (!IsDuplex && RecurseBox.IsChecked != true && !string.IsNullOrWhiteSpace(NameBox.Text)) { start.ArgumentList.Add("-NameOverride"); start.ArgumentList.Add(NameBox.Text.Trim()); }
        if (!IsDuplex && RecurseBox.IsChecked == true) start.ArgumentList.Add("-Recurse");
        if (dryRun) start.ArgumentList.Add("-DryRun");
        try
        {
            _activeProcess = Process.Start(start);
            if (_activeProcess is null) return (-1, "Could not start the rename engine.", false);
            var stdout = _activeProcess.StandardOutput.ReadToEndAsync();
            var stderr = _activeProcess.StandardError.ReadToEndAsync();
            await _activeProcess.WaitForExitAsync();
            var combined = (await stdout) + (await stderr);
            return (_activeProcess.ExitCode, combined, _cancelRequested);
        }
        finally
        {
            _activeProcess?.Dispose(); _activeProcess = null; CancelButton.IsEnabled = false; PreviewButton.IsEnabled = true; OperationProgress.IsIndeterminate = false; BusyRing.IsActive = false;
        }
    }

    private void ShowFriendlyResult(string output, int exitCode, bool cancelled, bool preview)
    {
        PreviewItems.Clear();
        var warnings = 0;
        var errors = 0;
        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Contains("->", StringComparison.Ordinal))
            {
                var parts = line.Split("->", 2, StringSplitOptions.TrimEntries);
                PreviewItems.Add(new RenamePreviewItem { SourceName = parts[0], DestinationName = parts[1], Detail = preview ? "Ready to rename" : "Renamed", Glyph = preview ? "\uE8B5" : "\uE73E", StatusBrush = new SolidColorBrush(preview ? Colors.DodgerBlue : Colors.MediumSeaGreen) });
            }
            else if (line.StartsWith("Quarantining:", StringComparison.OrdinalIgnoreCase))
            {
                var name = Regex.Match(line, @"Quarantining:\s*(.*?)\s*\(").Groups[1].Value;
                PreviewItems.Add(new RenamePreviewItem { SourceName = name, DestinationName = "Quarantine", Detail = "Missing front - kept safely", Glyph = "\uE7BA", StatusBrush = new SolidColorBrush(Colors.DarkOrange) });
                warnings++;
            }
            else if (line.Contains("WARNING", StringComparison.OrdinalIgnoreCase) || line.Contains("MISSING PAIR", StringComparison.OrdinalIgnoreCase)) warnings++;
            else if (line.Contains("ERROR", StringComparison.OrdinalIgnoreCase) || line.Contains("Exception", StringComparison.OrdinalIgnoreCase)) errors++;
        }

        if (cancelled)
        {
            PreviewSummaryText.Text = "Cancelled. No additional changes were started.";
            return;
        }
        if (exitCode != 0 || errors > 0)
        {
            PreviewItems.Insert(0, new RenamePreviewItem { SourceName = "No changes were applied", DestinationName = "Needs attention", Detail = "Check the selected folder and filenames", Glyph = "\uEA39", StatusBrush = new SolidColorBrush(Colors.IndianRed) });
            PreviewSummaryText.Text = "The plan could not be completed safely.";
            return;
        }
        if (PreviewItems.Count == 0)
            PreviewItems.Add(new RenamePreviewItem { SourceName = "Everything is already organized", DestinationName = "No changes needed", Detail = "Folder is ready", Glyph = "\uE73E", StatusBrush = new SolidColorBrush(Colors.MediumSeaGreen) });

        var action = preview ? "planned change" : "completed change";
        PreviewSummaryText.Text = $"{PreviewItems.Count:N0} {action}{(PreviewItems.Count == 1 ? string.Empty : "s")}" + (warnings > 0 ? $" - {warnings:N0} item(s) need attention" : " - everything looks ready");
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cancelRequested = true; CancelButton.IsEnabled = false; StatusText.Text = "Cancelling...";
        try { if (_activeProcess is not null && !_activeProcess.HasExited) _activeProcess.Kill(true); } catch (InvalidOperationException) { }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(FolderPathBox.Text)) FolderBrowserService.OpenFolder(FolderPathBox.Text);
    }

}
