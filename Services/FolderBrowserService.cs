using System.Diagnostics;
using PhotoTools2.Models;
using Windows.Storage.Pickers;

namespace PhotoTools2.Services;

public static class FolderBrowserService
{
    public static string? NormalizeExistingFolder(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var path = Path.GetFullPath(value.Trim().Trim('"'));
            return Directory.Exists(path) ? path : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    public static string? GetParent(string? path)
    {
        var normalized = NormalizeExistingFolder(path);
        return normalized is null ? null : Directory.GetParent(normalized)?.FullName;
    }

    public static IEnumerable<FileBrowserItem> Enumerate(string path, Func<string, bool> includeFile)
    {
        foreach (var folder in Directory.EnumerateDirectories(path).OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase))
            yield return new FileBrowserItem { Name = Path.GetFileName(folder), Path = folder, IsFolder = true };
        foreach (var file in Directory.EnumerateFiles(path).Where(includeFile).OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase))
        {
            var info = new FileInfo(file);
            yield return new FileBrowserItem { Name = info.Name, Path = info.FullName, IsImage = true, Size = info.Length, Modified = info.LastWriteTime };
        }
    }

    public static async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    public static void OpenFolder(string path) => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    public static void OpenItem(FileBrowserItem item, Action<string> enterFolder) { if (item.IsFolder) enterFolder(item.Path); else Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true }); }
    public static void Reveal(string path) { var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true }; start.ArgumentList.Add($"/select,{path}"); Process.Start(start); }
}
