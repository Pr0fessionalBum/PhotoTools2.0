using System.Runtime.InteropServices.WindowsRuntime;
using PhotoTools2.Models;
using Windows.Data.Pdf;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace PhotoTools2.Services;

public static class PdfConversionService
{
    public static async Task<PdfDocumentSession> OpenAsync(string path, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Choose a PDF file.", nameof(path));
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("The PDF file could not be found.", fullPath);
        if (!Path.GetExtension(fullPath).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The selected file is not a PDF.", nameof(path));

        token.ThrowIfCancellationRequested();
        var file = await StorageFile.GetFileFromPathAsync(fullPath);
        var document = await PdfDocument.LoadFromFileAsync(file);
        token.ThrowIfCancellationRequested();
        return new PdfDocumentSession(fullPath, document);
    }

    public static async Task<byte[]> RenderPreviewAsync(
        PdfDocumentSession session,
        uint pageIndex,
        uint maximumDimension = 1800,
        int rotationQuarterTurns = 0,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (pageIndex >= session.PageCount) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        if (maximumDimension == 0) throw new ArgumentOutOfRangeException(nameof(maximumDimension));

        token.ThrowIfCancellationRequested();
        using var page = session.Document.GetPage(pageIndex);
        using var stream = new InMemoryRandomAccessStream();
        var options = CreateRenderOptions(page.Size, maximumDimension, BitmapEncoder.PngEncoderId);
        await page.RenderToStreamAsync(stream, options);
        token.ThrowIfCancellationRequested();

        if (((rotationQuarterTurns % 4) + 4) % 4 != 0)
        {
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
            using var rotated = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, rotated);
            encoder.SetSoftwareBitmap(bitmap);
            encoder.BitmapTransform.Rotation = ToBitmapRotation(rotationQuarterTurns);
            await encoder.FlushAsync();
            token.ThrowIfCancellationRequested();
            return await ReadBytesAsync(rotated, token);
        }

        return await ReadBytesAsync(stream, token);
    }

    public static async Task ExportAsync(
        PdfDocumentSession session,
        string outputFolder,
        int quality,
        double dpi,
        IProgress<(int completed, int total, string fileName)>? progress = null,
        CancellationToken token = default,
        string? outputBaseName = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(outputFolder)) throw new ArgumentException("Choose an output folder.", nameof(outputFolder));
        if (quality is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(quality));
        if (dpi is < 72 or > 1200) throw new ArgumentOutOfRangeException(nameof(dpi));

        var included = session.Pages.Where(page => page.Include).ToArray();
        if (included.Length == 0) throw new InvalidOperationException("At least one PDF page must be included.");
        Directory.CreateDirectory(outputFolder);
        var baseName = MakeSafeBaseName(string.IsNullOrWhiteSpace(outputBaseName) ? Path.GetFileNameWithoutExtension(session.SourcePath) : outputBaseName);
        var digits = Math.Max(4, session.PageCount.ToString().Length);

        for (var index = 0; index < included.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var edit = included[index];
            var pageNumber = (edit.PageIndex + 1).ToString($"D{digits}");
            var fileName = $"{baseName}-{pageNumber}.jpg";
            var destination = Path.Combine(outputFolder, fileName);
            await ExportPageAsync(session, edit, destination, quality, dpi, token);
            progress?.Report((index + 1, included.Length, fileName));
        }
    }

    private static async Task ExportPageAsync(
        PdfDocumentSession session,
        PdfPageEdit edit,
        string destination,
        int quality,
        double dpi,
        CancellationToken token)
    {
        var temporary = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using var page = session.Document.GetPage(edit.PageIndex);
            using var rendered = new InMemoryRandomAccessStream();
            var longestPixels = checked((uint)Math.Ceiling(Math.Max(page.Size.Width, page.Size.Height) * dpi / 96d));
            await page.RenderToStreamAsync(rendered, CreateRenderOptions(page.Size, longestPixels, BitmapEncoder.PngEncoderId));
            token.ThrowIfCancellationRequested();

            rendered.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(rendered);
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
            await using var fileStream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024, true);
            using var output = fileStream.AsRandomAccessStream();
            var properties = new BitmapPropertySet
            {
                ["ImageQuality"] = new BitmapTypedValue(quality / 100f, Windows.Foundation.PropertyType.Single)
            };
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output, properties);
            encoder.SetSoftwareBitmap(bitmap);
            encoder.BitmapTransform.Rotation = ToBitmapRotation(edit.RotationQuarterTurns);
            await encoder.FlushAsync();
            token.ThrowIfCancellationRequested();
            output.Dispose();
            fileStream.Close();
            File.Move(temporary, destination, true);
            ThumbnailCacheService.Invalidate(destination);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch { }
        }
    }

    private static PdfPageRenderOptions CreateRenderOptions(Size pageSize, uint maximumDimension, Guid encoderId)
    {
        var scale = maximumDimension / Math.Max(pageSize.Width, pageSize.Height);
        return new PdfPageRenderOptions
        {
            DestinationWidth = Math.Max(1u, checked((uint)Math.Round(pageSize.Width * scale))),
            DestinationHeight = Math.Max(1u, checked((uint)Math.Round(pageSize.Height * scale))),
            BitmapEncoderId = encoderId
        };
    }

    private static async Task<byte[]> ReadBytesAsync(IRandomAccessStream stream, CancellationToken token)
    {
        stream.Seek(0);
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        var length = checked((uint)stream.Size);
        await reader.LoadAsync(length);
        token.ThrowIfCancellationRequested();
        var bytes = new byte[length];
        reader.ReadBytes(bytes);
        return bytes;
    }

    private static BitmapRotation ToBitmapRotation(int quarterTurns) => (((quarterTurns % 4) + 4) % 4) switch
    {
        1 => BitmapRotation.Clockwise90Degrees,
        2 => BitmapRotation.Clockwise180Degrees,
        3 => BitmapRotation.Clockwise270Degrees,
        _ => BitmapRotation.None
    };

    private static string MakeSafeBaseName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var safe = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "Page" : safe;
    }
}
