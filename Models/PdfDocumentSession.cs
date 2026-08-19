using Windows.Data.Pdf;

namespace PhotoTools2.Models;

public sealed class PdfDocumentSession
{
    internal PdfDocumentSession(string sourcePath, PdfDocument document)
    {
        SourcePath = sourcePath;
        Document = document;
        var info = new FileInfo(sourcePath);
        SourceSize = info.Length;
        SourceModifiedUtc = info.LastWriteTimeUtc;
        Pages = Enumerable.Range(0, checked((int)document.PageCount))
            .Select(index => new PdfPageEdit { PageIndex = (uint)index })
            .ToArray();
    }

    public string SourcePath { get; }
    public string Name => Path.GetFileName(SourcePath);
    public long SourceSize { get; }
    public DateTime SourceModifiedUtc { get; }
    public uint PageCount => Document.PageCount;
    public IReadOnlyList<PdfPageEdit> Pages { get; }
    internal PdfDocument Document { get; }
}
