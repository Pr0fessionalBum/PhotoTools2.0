using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PhotoTools2.Models;
using PhotoTools2.Services;
using Windows.Storage.Pickers;

namespace PhotoTools2.Controls;

public sealed partial class ReplacementWorkspace : UserControl
{
    private CancellationTokenSource? _cancellation;
    private CancellationTokenSource? _loadCancellation;
    private int _compareIndex;
    public ObservableCollection<FileBrowserItem> Originals { get; } = [];
    public ObservableCollection<FileBrowserItem> MatchedOriginals { get; } = [];
    public ObservableCollection<FileBrowserItem> OtherOriginals { get; } = [];
    public ObservableCollection<ReplacementItem> Replacements { get; } = [];

    public ReplacementWorkspace() => InitializeComponent();

    public void RefreshFromCurrentAlbum()
    {
        var path = AppSettings.Get("CurrentAlbumPath");
        if (Directory.Exists(path)) LoadAlbum(path);
    }

    public void LoadAlbumFolder(string folderPath) => LoadAlbum(folderPath);

    private async void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        if (await FolderBrowserService.PickFolderAsync() is { } path) { AppSettings.Set("CurrentAlbumPath", path); LoadAlbum(path); }
    }

    private async void LoadAlbum(string path)
    {
        path = FolderBrowserService.NormalizeExistingFolder(path) ?? path;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        var cancellation = _loadCancellation = new CancellationTokenSource();
        FolderPathBox.Text = path;
        Originals.Clear(); MatchedOriginals.Clear(); OtherOriginals.Clear(); Replacements.Clear();
        StatusText.Text = "Indexing album files...";
        AlbumFileIndex index;
        try { index = await AlbumFileIndexService.GetAsync(path, cancellation.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Could not index this album: {ex.Message}";
            return;
        }
        if (!ReferenceEquals(_loadCancellation, cancellation)) return;
        var cropped = Path.Combine(path, "cropped");
        var jpg = Path.Combine(path, "JPG");
        var originalFiles = index.Files
            .Where(ImageFileFormats.IsEditableImage)
            .Where(file => !file.StartsWith(cropped + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !file.StartsWith(jpg + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => Path.GetRelativePath(path, file), StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        foreach (var file in originalFiles)
        {
            var info = new FileInfo(file);
            Originals.Add(new FileBrowserItem { Name = Path.GetRelativePath(path, file), Path = info.FullName, IsImage = true, Size = info.Length, Modified = info.LastWriteTime });
        }

        if (Directory.Exists(cropped))
            foreach (var file in index.FilesUnder(cropped, false).Where(ImageFileFormats.IsEditableImage))
            {
                var destination = Path.Combine(path, Path.GetFileName(file));
                Replacements.Add(CreateReplacement(file, destination, "cropped", false, originalFiles));
            }

        if (Directory.Exists(jpg))
            foreach (var file in index.FilesUnder(jpg).Where(ImageFileFormats.IsJpeg))
            {
                var relative = Path.GetRelativePath(jpg, file);
                Replacements.Add(CreateReplacement(file, Path.Combine(path, relative), "JPG", true, originalFiles, relative));
            }

        var matchedPaths = Replacements.Where(item => item.OriginalPath is not null).Select(item => item.OriginalPath!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var original in Originals)
        {
            if (matchedPaths.Contains(original.Path)) MatchedOriginals.Add(original); else OtherOriginals.Add(original);
        }
        foreach (var folder in index.Directories.Where(folder => string.Equals(Path.GetDirectoryName(folder), path, StringComparison.OrdinalIgnoreCase)))
        {
            if (string.Equals(Path.GetFileName(folder), "cropped", StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetFileName(folder), "JPG", StringComparison.OrdinalIgnoreCase)) continue;
            OtherOriginals.Insert(0, new FileBrowserItem { Name = Path.GetFileName(folder), Path = folder, IsFolder = true });
        }

        MatchedCountText.Text = $"Matching originals · {MatchedOriginals.Count:N0}";
        OtherCountText.Text = $"Other originals · {OtherOriginals.Count:N0}";
        var matched = Replacements.Count(item => item.OriginalPath is not null);
        ReplacementCountText.Text = $"Ready replacements · {Replacements.Count:N0} ({matched:N0} matched)";
        ProcessButton.IsEnabled = Replacements.Count > 0;
        StatusText.Text = Replacements.Count == 0
            ? "No replacements found. Empty staging folders will never trigger cleanup."
            : $"{Replacements.Count:N0} staged files found. Review both panes before processing.";
    }

    private static ReplacementItem CreateReplacement(string source, string destination, string stage, bool replacesPng, IReadOnlyList<string> originals, string? displayName = null)
    {
        var exact = originals.FirstOrDefault(file => string.Equals(file, destination, StringComparison.OrdinalIgnoreCase));
        var converted = replacesPng ? originals.FirstOrDefault(file => string.Equals(file, Path.ChangeExtension(destination, ".png"), StringComparison.OrdinalIgnoreCase)) : null;
        var baseMatch = originals.FirstOrDefault(file => string.Equals(Path.GetFileNameWithoutExtension(file), Path.GetFileNameWithoutExtension(destination), StringComparison.OrdinalIgnoreCase));
        var original = exact ?? converted ?? baseMatch;
        return new ReplacementItem
        {
            Name = displayName ?? Path.GetFileName(source), SourcePath = source, DestinationPath = destination,
            Stage = stage, ReplacesPng = replacesPng, OriginalPath = original,
            MatchStatus = exact is not null ? "Exact name match" : converted is not null ? "Matches PNG original" : baseMatch is not null ? "Base-name match" : "No matching original"
        };
    }

    private void OriginalGrid_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) { if (sender is GridView { SelectedItem: FileBrowserItem item }) FolderBrowserService.OpenItem(item, LoadAlbum); }
    private void ReplacementGrid_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e) { if (ReplacementGrid.SelectedItem is ReplacementItem item) FolderBrowserService.OpenFile(item.SourcePath); }
    private void ReplacementGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CompareButton.IsEnabled = ReplacementGrid.SelectedItem is ReplacementItem { OriginalPath: not null };
        if (ReplacementGrid.SelectedItem is not ReplacementItem replacement || replacement.OriginalPath is null) return;
        MatchedOriginalGrid.SelectedItem = MatchedOriginals.FirstOrDefault(item => string.Equals(item.Path, replacement.OriginalPath, StringComparison.OrdinalIgnoreCase));
        MatchedOriginalGrid.ScrollIntoView(MatchedOriginalGrid.SelectedItem);
    }

    private void Compare_Click(object sender, RoutedEventArgs e)
    {
        if (ReplacementGrid.SelectedItem is not ReplacementItem selected || selected.OriginalPath is null) return;
        _compareIndex = Replacements.IndexOf(selected);
        ShowComparison();
    }

    private void ShowComparison()
    {
        if (Replacements.Count == 0) return;
        _compareIndex = Math.Clamp(_compareIndex, 0, Replacements.Count - 1);
        var replacement = Replacements[_compareIndex];
        if (replacement.OriginalPath is null)
        {
            var next = Replacements.Select((item, index) => (item, index)).FirstOrDefault(pair => pair.index > _compareIndex && pair.item.OriginalPath is not null);
            if (next.item is null) return;
            _compareIndex = next.index; replacement = next.item;
        }
        var original = Originals.First(item => string.Equals(item.Path, replacement.OriginalPath, StringComparison.OrdinalIgnoreCase));
        CompareOriginalImage.Source = original.ThumbnailUri;
        CompareReplacementImage.Source = replacement.Thumbnail;
        CompareNamesText.Text = $"{original.Name}  ↔  {replacement.Name}   ·   {replacement.MatchStatus}";
        BrowserPane.Visibility = Visibility.Collapsed;
        ComparePane.Visibility = Visibility.Visible;
    }

    private void PreviousCompare_Click(object sender, RoutedEventArgs e)
    {
        for (var index = _compareIndex - 1; index >= 0; index--) if (Replacements[index].OriginalPath is not null) { _compareIndex = index; ShowComparison(); return; }
    }
    private void NextCompare_Click(object sender, RoutedEventArgs e)
    {
        for (var index = _compareIndex + 1; index < Replacements.Count; index++) if (Replacements[index].OriginalPath is not null) { _compareIndex = index; ShowComparison(); return; }
    }
    private void CloseCompare_Click(object sender, RoutedEventArgs e) { ComparePane.Visibility = Visibility.Collapsed; BrowserPane.Visibility = Visibility.Visible; }
    private void Workspace_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (ComparePane.Visibility == Visibility.Visible)
        {
            if (e.Key == Windows.System.VirtualKey.Left) PreviousCompare_Click(sender, e);
            else if (e.Key == Windows.System.VirtualKey.Right) NextCompare_Click(sender, e);
            else if (e.Key == Windows.System.VirtualKey.Escape) CloseCompare_Click(sender, e);
            else if (e.Key == Windows.System.VirtualKey.Enter && _compareIndex < Replacements.Count) FolderBrowserService.OpenFile(Replacements[_compareIndex].SourcePath);
            else return;
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Enter && CompareButton.IsEnabled)
        {
            Compare_Click(sender, e);
            e.Handled = true;
        }
    }
    private void OpenOriginal_Click(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.Tag is FileBrowserItem item) FolderBrowserService.OpenFile(item.Path); }
    private void RevealOriginal_Click(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.Tag is FileBrowserItem item) FolderBrowserService.Reveal(item.Path); }
    private void OpenReplacement_Click(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.Tag is ReplacementItem item) FolderBrowserService.OpenFile(item.SourcePath); }
    private void OpenMatchedOriginal_Click(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.Tag is ReplacementItem { OriginalPath: { } path }) FolderBrowserService.OpenFile(path); }
    private void RevealReplacement_Click(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.Tag is ReplacementItem item) FolderBrowserService.Reveal(item.SourcePath); }

    private async void Process_Click(object sender, RoutedEventArgs e)
    {
        if (Replacements.Count == 0) return;
        var confirm = new ContentDialog { XamlRoot = XamlRoot, Title = "Apply reviewed replacements?", Content = $"This will copy {Replacements.Count:N0} reviewed files into the album, verify them, remove matching PNG originals for JPG conversions, and only then remove non-empty staging folders.", PrimaryButtonText = "Process replacements", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        _cancellation = new CancellationTokenSource();
        ProcessButton.IsEnabled = false; CancelButton.IsEnabled = true;
        var completed = 0;
        try
        {
            foreach (var item in Replacements)
            {
                _cancellation.Token.ThrowIfCancellationRequested();
                StatusText.Text = $"Copying {completed + 1:N0} of {Replacements.Count:N0}: {item.Name}";
                Directory.CreateDirectory(Path.GetDirectoryName(item.DestinationPath)!);
                await Task.Run(() => File.Copy(item.SourcePath, item.DestinationPath, true), _cancellation.Token);
                if (!File.Exists(item.DestinationPath) || new FileInfo(item.SourcePath).Length != new FileInfo(item.DestinationPath).Length)
                    throw new IOException($"Verification failed for {item.Name}.");
                ThumbnailCacheService.Invalidate(item.DestinationPath);
                completed++;
                Progress.Value = completed * 100d / Replacements.Count;
            }

            foreach (var item in Replacements.Where(item => item.ReplacesPng))
            {
                var png = Path.ChangeExtension(item.DestinationPath, ".png");
                if (File.Exists(png)) File.Delete(png);
            }
            DeleteVerifiedStage(Path.Combine(FolderPathBox.Text, "cropped"), Replacements.Any(item => item.Stage == "cropped"));
            DeleteVerifiedStage(Path.Combine(FolderPathBox.Text, "JPG"), Replacements.Any(item => item.Stage == "JPG"));
            AlbumFileIndexService.Invalidate(FolderPathBox.Text);
            StatusText.Text = $"Completed and verified {completed:N0} replacements.";
            LoadAlbum(FolderPathBox.Text);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = $"Cancelled safely after {completed:N0} verified copies. Staging folders were preserved.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Stopped safely: {ex.Message} Staging folders were preserved.";
        }
        finally
        {
            AlbumFileIndexService.Invalidate(FolderPathBox.Text);
            CancelButton.IsEnabled = false;
            ProcessButton.IsEnabled = Replacements.Count > 0;
            _cancellation.Dispose(); _cancellation = null;
        }
    }

    private static void DeleteVerifiedStage(string path, bool containsVerifiedImages)
    {
        if (!containsVerifiedImages || !Directory.Exists(path)) return;
        Directory.Delete(path, true);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) { CancelButton.IsEnabled = false; StatusText.Text = "Cancelling after the current file..."; _cancellation?.Cancel(); }
    private void Refresh_Click(object sender, RoutedEventArgs e) { if (Directory.Exists(FolderPathBox.Text)) { AlbumFileIndexService.Invalidate(FolderPathBox.Text); LoadAlbum(FolderPathBox.Text); } }
    private void Open_Click(object sender, RoutedEventArgs e) { if (Directory.Exists(FolderPathBox.Text)) FolderBrowserService.OpenFolder(FolderPathBox.Text); }
    private void FolderPathBox_KeyDown(object sender, KeyRoutedEventArgs e) { if (e.Key != Windows.System.VirtualKey.Enter) return; var path = FolderBrowserService.NormalizeExistingFolder(FolderPathBox.Text); if (path is not null) LoadAlbum(path); else StatusText.Text = "That folder path could not be found."; e.Handled = true; }
    private void UpFolder_Click(object sender, RoutedEventArgs e) { if (FolderBrowserService.GetParent(FolderPathBox.Text) is { } parent) LoadAlbum(parent); }
}
