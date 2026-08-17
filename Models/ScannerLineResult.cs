namespace PhotoTools2.Models;

public sealed class ScannerLineResult
{
    public FileBrowserItem Photo { get; set; } = new();
    public string PositionLabel { get; set; } = string.Empty;
    public string ConfidenceLabel { get; set; } = string.Empty;
    public double ConfidencePercent { get; set; }
    public double LinePosition { get; set; }
    public bool IsHorizontal { get; set; }
    public string Explanation { get; set; } = string.Empty;
}
