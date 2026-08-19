using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PhotoTools2.Models;

public sealed class PdfDocumentJob : INotifyPropertyChanged
{
    private string _outputName = string.Empty;
    private string _outputFolder = string.Empty;
    private int _currentPageIndex;

    public string SourcePath { get; set; } = string.Empty;
    public PdfDocumentSession? Session { get; set; }
    public ObservableCollection<PdfPagePreviewItem> Pages { get; } = [];
    public string FileName => Path.GetFileName(SourcePath);
    public uint PageCount => Session?.PageCount ?? 0;

    public string OutputName
    {
        get => _outputName;
        set { if (_outputName == value) return; _outputName = value; OnPropertyChanged(); }
    }

    public string OutputFolder
    {
        get => _outputFolder;
        set { if (_outputFolder == value) return; _outputFolder = value; OnPropertyChanged(); }
    }

    public int CurrentPageIndex
    {
        get => _currentPageIndex;
        set { var next = Pages.Count == 0 ? -1 : Math.Clamp(value, 0, Pages.Count - 1); if (_currentPageIndex == next) return; _currentPageIndex = next; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static PdfDocumentJob Create(PdfDocumentSession session)
    {
        var name = Path.GetFileNameWithoutExtension(session.SourcePath);
        var job = new PdfDocumentJob
        {
            SourcePath = session.SourcePath,
            Session = session,
            OutputName = name,
            OutputFolder = Path.Combine(Path.GetDirectoryName(session.SourcePath)!, name + " JPG")
        };
        foreach (var edit in session.Pages) job.Pages.Add(new PdfPagePreviewItem(edit));
        job.CurrentPageIndex = 0;
        return job;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
