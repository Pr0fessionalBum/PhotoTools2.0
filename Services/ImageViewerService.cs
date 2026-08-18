using PhotoTools2.Models;
using PhotoTools2.Viewer;

namespace PhotoTools2.Services;

public static class ImageViewerService
{
    private static ImageViewerWindow? _window;
    public static event EventHandler<string>? OpenFailed;

    public static void Open(IEnumerable<FileBrowserItem> items, string selectedPath)
    {
        var paths = items.Where(item => item.IsImage && File.Exists(item.Path)).Select(item => item.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length == 0) return;
        var index = Array.FindIndex(paths, path => string.Equals(path, selectedPath, StringComparison.OrdinalIgnoreCase));
        if (index < 0) index = 0;
        try
        {
            if (_window is null)
            {
                _window = new ImageViewerWindow();
                _window.Closed += (_, _) => _window = null;
            }
            _window.ShowImages(paths, index);
            _window.Activate();
        }
        catch (Exception ex)
        {
            _window = null;
            OpenFailed?.Invoke(null, ex.Message);
        }
    }

    public static void OpenScannerComparisons(IEnumerable<ScannerLineResult> results, ScannerLineResult selected)
    {
        var items = results.Where(result => File.Exists(result.Photo.Path)).ToArray();
        if (items.Length == 0) return;
        var index = Array.FindIndex(items, item => ReferenceEquals(item, selected));
        if (index < 0) index = Array.FindIndex(items, item => string.Equals(item.Photo.Path, selected.Photo.Path, StringComparison.OrdinalIgnoreCase));
        if (index < 0) index = 0;
        try
        {
            if (_window is null)
            {
                _window = new ImageViewerWindow();
                _window.Closed += (_, _) => _window = null;
            }
            _window.ShowScannerComparisons(items, index);
            _window.Activate();
        }
        catch (Exception ex)
        {
            _window = null;
            OpenFailed?.Invoke(null, ex.Message);
        }
    }
}
