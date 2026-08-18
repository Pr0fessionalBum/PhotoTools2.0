using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoTools2.Models;
using PhotoTools2.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace PhotoTools2.Pages;

public sealed partial class HomePage : Page
{
    private readonly List<AlbumItem> _allAlbums = [];
    private readonly List<FileBrowserItem> _allBrowserItems = [];
    private readonly Stack<string> _folderHistory = [];
    private string? _currentFolderPath;
    private bool _fileSortDescending;
    private bool _suppressAlbumSelection;
    private bool _watchRefreshRunning;
    private bool _watchRefreshPending;
    private FileSystemWatcher? _collectionWatcher;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _refreshDebounceTimer;
    public ObservableCollection<AlbumItem> Albums { get; } = [];
    public ObservableCollection<FileBrowserItem> BrowserItems { get; } = [];

    public HomePage()
    {
        InitializeComponent();
        CropWorkspace.ContinueToReplacements += (_, _) => OpenImageProcessingTab(2);
        ConvertWorkspace.ContinueToReplacements += (_, _) => OpenImageProcessingTab(2);
        ImageViewerService.OpenFailed += (_, message) => { StatusBar.Severity = InfoBarSeverity.Error; StatusBar.Message = $"The image viewer could not open: {message}"; };
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (AppSettings.Get("CollectionPath") is { } savedPath && Directory.Exists(savedPath))
        {
            await LoadCollectionAsync(savedPath);
        }
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        _refreshDebounceTimer?.Stop();
        _collectionWatcher?.Dispose();
        _collectionWatcher = null;
    }

    private async void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        if (await picker.PickSingleFolderAsync() is { } folder) await LoadCollectionAsync(folder.Path);
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(CollectionPathBox.Text)) await LoadCollectionAsync(CollectionPathBox.Text);
    }

    private async Task LoadCollectionAsync(string path)
    {
        CollectionPathBox.Text = path;
        AppSettings.Set("CollectionPath", path);
        RefreshButton.IsEnabled = false;
        StatusBar.Severity = InfoBarSeverity.Informational;
        StatusBar.Message = "Scanning album folders...";
        try
        {
            var results = await Task.Run(() => AlbumScanner.ScanCollection(path));
            _allAlbums.Clear();
            _allAlbums.AddRange(results);
            ApplyFilter();
            if (Albums.Count > 0) AlbumList.SelectedIndex = 0;
            _folderHistory.Clear();
            ConfigureCollectionWatcher(path);
            NewFolderButton.IsEnabled = true;
            StatusBar.Severity = InfoBarSeverity.Success;
            StatusBar.Message = $"Loaded {_allAlbums.Count:N0} albums. Right-click an album for actions.";
        }
        catch (Exception ex)
        {
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.Message = $"Could not scan this collection: {ex.Message}";
        }
        finally { RefreshButton.IsEnabled = true; }
    }

    private void ConfigureCollectionWatcher(string path)
    {
        _collectionWatcher?.Dispose();
        _collectionWatcher = null;
        try
        {
            var watcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 16 * 1024
            };
            watcher.Created += CollectionChanged;
            watcher.Deleted += CollectionChanged;
            watcher.Changed += CollectionChanged;
            watcher.Renamed += CollectionRenamed;
            watcher.Error += CollectionWatcher_Error;
            watcher.EnableRaisingEvents = true;
            _collectionWatcher = watcher;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StatusBar.Severity = InfoBarSeverity.Warning;
            StatusBar.Message = $"Albums loaded, but automatic refresh could not start: {ex.Message}";
        }
    }

    private void CollectionChanged(object sender, FileSystemEventArgs e) => ScheduleAutomaticRefresh();
    private void CollectionRenamed(object sender, RenamedEventArgs e) => ScheduleAutomaticRefresh();
    private void CollectionWatcher_Error(object sender, ErrorEventArgs e) => ScheduleAutomaticRefresh();

    private void ScheduleAutomaticRefresh()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _refreshDebounceTimer ??= DispatcherQueue.CreateTimer();
            _refreshDebounceTimer.Interval = TimeSpan.FromMilliseconds(650);
            _refreshDebounceTimer.IsRepeating = false;
            _refreshDebounceTimer.Tick -= RefreshDebounceTimer_Tick;
            _refreshDebounceTimer.Tick += RefreshDebounceTimer_Tick;
            _refreshDebounceTimer.Stop();
            _refreshDebounceTimer.Start();
        });
    }

    private async void RefreshDebounceTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (_watchRefreshRunning) { _watchRefreshPending = true; return; }
        await RefreshFromWatcherAsync();
    }

    private async Task RefreshFromWatcherAsync()
    {
        var collectionPath = CollectionPathBox.Text;
        if (!Directory.Exists(collectionPath)) return;
        _watchRefreshRunning = true;
        var selectedAlbumPath = (AlbumList.SelectedItem as AlbumItem)?.Path;
        var selectedFilePaths = FileGrid.SelectedItems.Cast<FileBrowserItem>().Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var viewedFolder = _currentFolderPath;
        try
        {
            var results = await Task.Run(() => AlbumScanner.ScanCollection(collectionPath));
            _suppressAlbumSelection = true;
            _allAlbums.Clear(); _allAlbums.AddRange(results); ApplyFilter();
            var albumIndex = selectedAlbumPath is null ? -1 : Albums.ToList().FindIndex(album => string.Equals(album.Path, selectedAlbumPath, StringComparison.OrdinalIgnoreCase));
            AlbumList.SelectedIndex = albumIndex;
            _suppressAlbumSelection = false;

            if (viewedFolder is not null && Directory.Exists(viewedFolder)) await LoadFolderContentsAsync(viewedFolder, false);
            else if (albumIndex >= 0) await LoadFolderContentsAsync(Albums[albumIndex].Path, false);
            else { _currentFolderPath = null; _allBrowserItems.Clear(); BrowserItems.Clear(); FileCountText.Text = "0 items"; }
            foreach (var browserItem in BrowserItems.Where(item => selectedFilePaths.Contains(item.Path))) FileGrid.SelectedItems.Add(browserItem);
            StatusBar.Severity = InfoBarSeverity.Informational;
            StatusBar.Message = "Album Hub updated automatically.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusBar.Severity = InfoBarSeverity.Warning;
            StatusBar.Message = $"Automatic refresh will retry after the next change: {ex.Message}";
        }
        finally
        {
            _suppressAlbumSelection = false;
            _watchRefreshRunning = false;
            if (_watchRefreshPending) { _watchRefreshPending = false; ScheduleAutomaticRefresh(); }
        }
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => ApplyFilter();

    private void ToolButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tab } && int.TryParse(tab, out var index))
        {
            ShowWorkspace(index);
            RefreshActiveGroupedTool(index);
        }
    }

    private void ShowWorkspace(int index)
    {
        FrameworkElement[] workspaces = [AlbumsWorkspace, ImageProcessingWorkspace, NumberingToolsWorkspace, AnalysisWorkspace];
        for (var i = 0; i < workspaces.Length; i++)
            workspaces[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshActiveGroupedTool(int group)
    {
        if (group == 1) RefreshImageProcessingTab();
        else if (group == 2) RefreshNumberingTab();
        else if (group == 3) RefreshAnalysisTab();
    }

    private void OpenImageProcessingTab(int tabIndex)
    {
        ShowWorkspace(1);
        ImageProcessingWorkspace.SelectedIndex = tabIndex;
        RefreshImageProcessingTab();
    }

    private void ImageProcessingTabs_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshImageProcessingTab();
    private void NumberingTabs_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshNumberingTab();
    private void AnalysisTabs_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshAnalysisTab();

    private void RefreshImageProcessingTab()
    {
        if (ImageProcessingWorkspace is null || ImageProcessingWorkspace.Visibility != Visibility.Visible) return;
        if (ImageProcessingWorkspace.SelectedIndex == 0) CropWorkspace.RefreshFromCurrentAlbum();
        else if (ImageProcessingWorkspace.SelectedIndex == 1) ConvertWorkspace.RefreshFromCurrentAlbum();
        else if (ImageProcessingWorkspace.SelectedIndex == 2) ReplacementWorkspace.RefreshFromCurrentAlbum();
    }

    private void RefreshNumberingTab()
    {
        if (NumberingToolsWorkspace is null || NumberingToolsWorkspace.Visibility != Visibility.Visible) return;
        if (NumberingToolsWorkspace.SelectedIndex == 0) DuplexWorkspace.RefreshFromCurrentAlbum();
        else if (NumberingToolsWorkspace.SelectedIndex == 1) NumberingWorkspace.RefreshFromCurrentAlbum();
    }

    private void RefreshAnalysisTab()
    {
        if (AnalysisWorkspace is null || AnalysisWorkspace.Visibility != Visibility.Visible) return;
        if (AnalysisWorkspace.SelectedIndex == 0) StatsWorkspace.RefreshFromCurrentAlbum();
        else if (AnalysisWorkspace.SelectedIndex == 1) ScannerLineWorkspace.RefreshFromCurrentAlbum();
    }

    private void ExternalToolButton_Click(object sender, RoutedEventArgs e)
    {
        var tool = (sender as Button)?.Tag as string == "vuescan" ? "VueScan" : "Duplicate Finder";
        StatusBar.Severity = InfoBarSeverity.Informational;
        StatusBar.Message = $"{tool} will be connected during the feature pass.";
    }

    private void OpenCollection_Click(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(CollectionPathBox.Text)) OpenFolder(CollectionPathBox.Text);
    }

    private void ApplyFilter()
    {
        var search = SearchBox.Text.Trim();
        Albums.Clear();
        foreach (var album in _allAlbums.Where(album => search.Length == 0 || album.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase))) Albums.Add(album);
        AlbumCountText.Text = $"{Albums.Count:N0} albums";
    }

    private async void AlbumList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressAlbumSelection) return;
        if (AlbumList.SelectedItem is not AlbumItem album) return;
        SelectedAlbumName.Text = album.Name;
        SelectedAlbumPath.Text = album.Path;
        UseSelectedButton.IsEnabled = true;
        OpenSelectedButton.IsEnabled = true;
        _folderHistory.Clear();
        await LoadFolderContentsAsync(album.Path, false);
    }

    private async Task LoadFolderContentsAsync(string path, bool addToHistory = true)
    {
        if (addToHistory && _currentFolderPath is not null && !string.Equals(_currentFolderPath, path, StringComparison.OrdinalIgnoreCase)) _folderHistory.Push(_currentFolderPath);
        _currentFolderPath = path;
        SelectedAlbumPath.Text = path;
        UpdateFolderNavigationButtons();
        try
        {
            var items = await Task.Run(() => EnumerateFolder(path));
            _allBrowserItems.Clear();
            _allBrowserItems.AddRange(items);
            ApplyFileFilter();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.Message = $"Could not open this folder: {ex.Message}";
        }
    }

    private static IReadOnlyList<FileBrowserItem> EnumerateFolder(string path)
    {
        var folders = Directory.EnumerateDirectories(path).Select(folder => new FileBrowserItem
        {
            Name = Path.GetFileName(folder), Path = folder, IsFolder = true
        });
        var files = Directory.EnumerateFiles(path).Select(file => new FileInfo(file)).Select(file => new FileBrowserItem
        {
            Name = file.Name,
            Path = file.FullName,
            IsImage = IsImageFile(file.Extension),
            Size = file.Length,
            Modified = file.LastWriteTime
        });
        return folders.Concat(files).OrderByDescending(item => item.IsFolder)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static bool IsImageFile(string extension) => extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) || extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase) || extension.Equals(".gif", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".tif", StringComparison.OrdinalIgnoreCase) || extension.Equals(".tiff", StringComparison.OrdinalIgnoreCase);

    private void FileSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => ApplyFileFilter();

    private void ApplyFileFilter()
    {
        if (FileSearchBox is null || FileSortBox is null || FileCountText is null) return;
        var search = FileSearchBox.Text.Trim();
        IEnumerable<FileBrowserItem> items = _allBrowserItems.Where(item => search.Length == 0 || item.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase));
        Func<FileBrowserItem, object> key = FileSortBox.SelectedIndex switch
        {
            1 => item => item.Modified,
            2 => item => item.Extension,
            3 => item => item.Size,
            _ => item => item.Name
        };
        items = _fileSortDescending
            ? items.OrderByDescending(item => item.IsFolder).ThenByDescending(key)
            : items.OrderByDescending(item => item.IsFolder).ThenBy(key);
        BrowserItems.Clear();
        foreach (var item in items) BrowserItems.Add(item);
        FileCountText.Text = $"{BrowserItems.Count:N0} items";
    }

    private void FileSortBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (FileSearchBox is not null) ApplyFileFilter(); }
    private void FileSortDirection_Click(object sender, RoutedEventArgs e) { _fileSortDescending = !_fileSortDescending; FileSortDirectionButton.Content = _fileSortDescending ? "Z–A" : "A–Z"; ApplyFileFilter(); }
    private void SelectAllFiles_Click(object sender, RoutedEventArgs e) => FileGrid.SelectAll();
    private void ClearFiles_Click(object sender, RoutedEventArgs e) => FileGrid.SelectedItems.Clear();
    private void InvertFiles_Click(object sender, RoutedEventArgs e)
    {
        var selected = FileGrid.SelectedItems.Cast<FileBrowserItem>().ToHashSet();
        FileGrid.SelectedItems.Clear();
        foreach (var item in BrowserItems.Where(item => !selected.Contains(item))) FileGrid.SelectedItems.Add(item);
    }

    private void FileGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        FileCountText.Text = FileGrid.SelectedItems.Count == 0
            ? $"{BrowserItems.Count:N0} items"
            : $"{FileGrid.SelectedItems.Count:N0} of {BrowserItems.Count:N0} selected";
    }

    private void SendToTool_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string route || _currentFolderPath is null) return;
        var selectedItems = FileGrid.SelectedItems.Cast<FileBrowserItem>().ToArray();
        var selectedFiles = selectedItems.Where(item => !item.IsFolder && item.IsImage).Select(item => item.Path).ToArray();
        var selectedFolders = selectedItems.Where(item => item.IsFolder && Directory.Exists(item.Path)).ToArray();
        var folderPath = selectedFiles.Length == 0 && selectedFolders.Length == 1 ? selectedFolders[0].Path : _currentFolderPath;
        if (!Directory.Exists(folderPath)) return;

        AppSettings.Set("CurrentAlbumPath", folderPath);
        switch (route)
        {
            case "1:0": ShowWorkspace(1); ImageProcessingWorkspace.SelectedIndex = 0; CropWorkspace.LoadAlbumSelection(folderPath, selectedFiles); break;
            case "1:1": ShowWorkspace(1); ImageProcessingWorkspace.SelectedIndex = 1; ConvertWorkspace.LoadAlbumSelection(folderPath, selectedFiles); break;
            case "1:2": ShowWorkspace(1); ImageProcessingWorkspace.SelectedIndex = 2; ReplacementWorkspace.LoadAlbumFolder(folderPath); break;
            case "2:0": ShowWorkspace(2); NumberingToolsWorkspace.SelectedIndex = 0; DuplexWorkspace.LoadAlbumFolder(folderPath); break;
            case "2:1": ShowWorkspace(2); NumberingToolsWorkspace.SelectedIndex = 1; NumberingWorkspace.LoadAlbumFolder(folderPath); break;
            case "3:0": ShowWorkspace(3); AnalysisWorkspace.SelectedIndex = 0; StatsWorkspace.LoadAlbumFolder(folderPath); break;
            case "3:1": ShowWorkspace(3); AnalysisWorkspace.SelectedIndex = 1; ScannerLineWorkspace.LoadAlbumSelection(folderPath, selectedFiles); break;
            default: return;
        }
        StatusBar.Severity = InfoBarSeverity.Success;
        StatusBar.Message = selectedFiles.Length > 0
            ? $"Sent {selectedFiles.Length:N0} selected image(s) to the tool."
            : $"Opened {Path.GetFileName(folderPath)} in the tool.";
    }

    private void SendToToolMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var menu = new MenuFlyout();
        AddTool("Image Processing · Crop / Trim", "1:0");
        AddTool("Image Processing · PNG to JPG", "1:1");
        AddTool("Image Processing · Replacements", "1:2");
        menu.Items.Add(new MenuFlyoutSeparator());
        AddTool("Numbering · Front / Back", "2:0");
        AddTool("Numbering · Fix Numbering", "2:1");
        menu.Items.Add(new MenuFlyoutSeparator());
        AddTool("Analysis · Photo Stats", "3:0");
        AddTool("Analysis · Scanner Lines", "3:1");
        menu.ShowAt(button);

        void AddTool(string text, string route)
        {
            var item = new MenuFlyoutItem { Text = text, Tag = route };
            item.Click += SendToTool_Click;
            menu.Items.Add(item);
        }
    }

    private void FileGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is FileBrowserItem item)
            StatusBar.Message = item.IsFolder ? item.Path : $"Selected {item.Name} ({item.Details}).";
    }

    private void OpenBrowserItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is FileBrowserItem item)
        {
            if (item.IsFolder) _ = LoadFolderContentsAsync(item.Path);
            else if (item.IsImage) ImageViewerService.Open(BrowserItems, item.Path);
            else OpenFile(item.Path);
        }
    }

    private void RevealBrowserItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is FileBrowserItem item) RevealInExplorer(item.Path);
    }

    private async void FileGrid_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (FileGrid.SelectedItem is not FileBrowserItem item) return;
        if (item.IsFolder) await LoadFolderContentsAsync(item.Path);
        else if (item.IsImage) ImageViewerService.Open(BrowserItems, item.Path);
        else OpenFile(item.Path);
    }

    private void FileGrid_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter || FileGrid.SelectedItem is not FileBrowserItem item) return;
        if (item.IsFolder) _ = LoadFolderContentsAsync(item.Path);
        else if (item.IsImage) ImageViewerService.Open(BrowserItems, item.Path);
        else OpenFile(item.Path);
        e.Handled = true;
    }

    private async void BackFolder_Click(object sender, RoutedEventArgs e)
    {
        while (_folderHistory.Count > 0)
        {
            var previous = _folderHistory.Pop();
            if (Directory.Exists(previous)) { await LoadFolderContentsAsync(previous, false); return; }
        }
        UpdateFolderNavigationButtons();
    }

    private async void UpFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFolderPath is null) return;
        var parent = Directory.GetParent(_currentFolderPath)?.FullName;
        if (parent is not null && IsInsideCollection(parent)) await LoadFolderContentsAsync(parent);
    }

    private void UpdateFolderNavigationButtons()
    {
        BackFolderButton.IsEnabled = _folderHistory.Any(Directory.Exists);
        var parent = _currentFolderPath is null ? null : Directory.GetParent(_currentFolderPath)?.FullName;
        UpFolderButton.IsEnabled = parent is not null && IsInsideCollection(parent);
        NewFolderButton.IsEnabled = (_currentFolderPath is not null && Directory.Exists(_currentFolderPath)) || Directory.Exists(CollectionPathBox.Text);
        SendToToolButton.IsEnabled = _currentFolderPath is not null && Directory.Exists(_currentFolderPath);
    }

    private bool IsInsideCollection(string path)
    {
        try
        {
            var root = Path.GetFullPath(CollectionPathBox.Text).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var candidate = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private async void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        var parent = _currentFolderPath is not null && Directory.Exists(_currentFolderPath) ? _currentFolderPath : CollectionPathBox.Text;
        if (!Directory.Exists(parent)) return;
        var nameBox = new TextBox { Header = "Folder name", PlaceholderText = "New folder", MinWidth = 360 };
        var dialog = new ContentDialog { Title = "Create a new folder", Content = nameBox, PrimaryButtonText = "Create", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var name = nameBox.Text.Trim();
        if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar))
        {
            StatusBar.Severity = InfoBarSeverity.Warning; StatusBar.Message = "Enter a valid folder name without slashes or reserved characters."; return;
        }
        var newPath = Path.Combine(parent, name);
        if (Directory.Exists(newPath) || File.Exists(newPath)) { StatusBar.Severity = InfoBarSeverity.Warning; StatusBar.Message = $"An item named '{name}' already exists."; return; }
        try
        {
            Directory.CreateDirectory(newPath);
            await LoadFolderContentsAsync(parent, false);
            StatusBar.Severity = InfoBarSeverity.Success; StatusBar.Message = $"Created folder '{name}'.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StatusBar.Severity = InfoBarSeverity.Error; StatusBar.Message = $"Could not create the folder: {ex.Message}";
        }
    }

    private void UseSelectedAlbum_Click(object sender, RoutedEventArgs e)
    {
        if (AlbumList.SelectedItem is AlbumItem album) SetCurrentAlbum(album);
    }

    private void OpenSelectedAlbum_Click(object sender, RoutedEventArgs e)
    {
        if (AlbumList.SelectedItem is AlbumItem album) OpenFolder(album.Path);
    }

    private void Page_DragEnter(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems)) e.AcceptedOperation = DataPackageOperation.Copy;
    }

    private async void Page_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        if (items.FirstOrDefault() is StorageFolder folder) await LoadCollectionAsync(folder.Path);
    }

    private static AlbumItem? ItemFrom(object sender) => (sender as FrameworkElement)?.Tag as AlbumItem;

    private void OpenAlbum_Click(object sender, RoutedEventArgs e)
    {
        if (ItemFrom(sender) is { } album) OpenFolder(album.Path);
    }

    private void UseAlbum_Click(object sender, RoutedEventArgs e)
    {
        if (ItemFrom(sender) is { } album) SetCurrentAlbum(album);
    }

    private void SetCurrentAlbum(AlbumItem album)
    {
        AppSettings.Set("CurrentAlbumPath", album.Path);
        StatusBar.Severity = InfoBarSeverity.Success;
        StatusBar.Message = $"{album.Name} is now the current album for Photo Tools.";
    }

    private static void OpenFolder(string path) => Process.Start(
        new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });

    private static void OpenFile(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    private static void RevealInExplorer(string path)
    {
        if (Directory.Exists(path)) { OpenFolder(path); return; }
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        start.ArgumentList.Add($"/select,{path}");
        Process.Start(start);
    }

    private async void RefreshAlbum_Click(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(CollectionPathBox.Text)) await LoadCollectionAsync(CollectionPathBox.Text);
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (ItemFrom(sender) is not { } album) return;
        var package = new DataPackage(); package.SetText(album.Path); Clipboard.SetContent(package);
        StatusBar.Message = "Album path copied to the clipboard.";
    }
}
