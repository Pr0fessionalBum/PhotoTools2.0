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
    private string? _currentFolderPath;
    private bool _fileSortDescending;
    public ObservableCollection<AlbumItem> Albums { get; } = [];
    public ObservableCollection<FileBrowserItem> BrowserItems { get; } = [];

    public HomePage()
    {
        InitializeComponent();
        CropWorkspace.ContinueToReplacements += (_, _) => ShowWorkspace(2);
        ConvertWorkspace.ContinueToReplacements += (_, _) => ShowWorkspace(2);
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (AppSettings.Get("CollectionPath") is { } savedPath && Directory.Exists(savedPath))
        {
            await LoadCollectionAsync(savedPath);
        }
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

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => ApplyFilter();

    private void ToolButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tab } && int.TryParse(tab, out var index))
        {
            ShowWorkspace(index);
            if (index == 1) CropWorkspace.RefreshFromCurrentAlbum();
            if (index == 2) ReplacementWorkspace.RefreshFromCurrentAlbum();
            if (index == 3) ConvertWorkspace.RefreshFromCurrentAlbum();
            if (index == 4) DuplexWorkspace.RefreshFromCurrentAlbum();
            if (index == 5) NumberingWorkspace.RefreshFromCurrentAlbum();
            if (index == 6) StatsWorkspace.RefreshFromCurrentAlbum();
            if (index == 7) ScannerLineWorkspace.RefreshFromCurrentAlbum();
        }
    }

    private void ShowWorkspace(int index)
    {
        FrameworkElement[] workspaces = [AlbumsWorkspace, CropWorkspace, ReplacementWorkspace,
            ConvertWorkspace, DuplexWorkspace, NumberingWorkspace, StatsWorkspace, ScannerLineWorkspace];
        for (var i = 0; i < workspaces.Length; i++)
            workspaces[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
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
        if (AlbumList.SelectedItem is not AlbumItem album) return;
        SelectedAlbumName.Text = album.Name;
        SelectedAlbumPath.Text = album.Path;
        UseSelectedButton.IsEnabled = true;
        OpenSelectedButton.IsEnabled = true;
        await LoadFolderContentsAsync(album.Path);
    }

    private async Task LoadFolderContentsAsync(string path)
    {
        _currentFolderPath = path;
        SelectedAlbumPath.Text = path;
        BackFolderButton.IsEnabled = !string.Equals(path, CollectionPathBox.Text, StringComparison.OrdinalIgnoreCase);
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

    private void FileGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is FileBrowserItem item)
            StatusBar.Message = item.IsFolder ? item.Path : $"Selected {item.Name} ({item.Details}).";
    }

    private void OpenBrowserItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is FileBrowserItem item)
        {
            if (item.IsFolder) _ = LoadFolderContentsAsync(item.Path); else OpenFile(item.Path);
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
        else OpenFile(item.Path);
    }

    private void FileGrid_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter || FileGrid.SelectedItem is not FileBrowserItem item) return;
        if (item.IsFolder) _ = LoadFolderContentsAsync(item.Path); else OpenFile(item.Path);
        e.Handled = true;
    }

    private async void BackFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFolderPath is null) return;
        var parent = Directory.GetParent(_currentFolderPath)?.FullName;
        if (parent is not null && parent.StartsWith(CollectionPathBox.Text, StringComparison.OrdinalIgnoreCase))
            await LoadFolderContentsAsync(parent);
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
