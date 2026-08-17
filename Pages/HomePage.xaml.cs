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
    public ObservableCollection<AlbumItem> Albums { get; } = [];

    public HomePage() => InitializeComponent();

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (ApplicationData.Current.LocalSettings.Values["CollectionPath"] is string savedPath)
        {
            CollectionPathBox.Text = savedPath;
            StatusBar.Message = "Saved collection ready. Click Refresh to load its albums.";
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
        ApplicationData.Current.LocalSettings.Values["CollectionPath"] = path;
        RefreshButton.IsEnabled = false;
        StatusBar.Severity = InfoBarSeverity.Informational;
        StatusBar.Message = "Scanning album folders...";
        try
        {
            var results = await Task.Run(() => AlbumScanner.ScanCollection(path));
            _allAlbums.Clear();
            _allAlbums.AddRange(results);
            ApplyFilter();
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

    private void ApplyFilter()
    {
        var search = SearchBox.Text.Trim();
        Albums.Clear();
        foreach (var album in _allAlbums.Where(album => search.Length == 0 || album.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase))) Albums.Add(album);
        AlbumCountText.Text = $"{Albums.Count:N0} albums";
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
        if (ItemFrom(sender) is { } album) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{album.Path}\"") { UseShellExecute = true });
    }

    private void UseAlbum_Click(object sender, RoutedEventArgs e)
    {
        if (ItemFrom(sender) is not { } album) return;
        ApplicationData.Current.LocalSettings.Values["CurrentAlbumPath"] = album.Path;
        StatusBar.Severity = InfoBarSeverity.Success;
        StatusBar.Message = $"{album.Name} is now the current album for Photo Tools.";
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
