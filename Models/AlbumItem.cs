namespace PhotoTools2.Models;

public sealed class AlbumItem
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public int PhotoCount { get; set; }
    public int PngCount { get; set; }
    public int CroppedCount { get; set; }
    public int ConvertedCount { get; set; }

    public string PhotoLabel => $"{PhotoCount:N0} photos";
    public string PngLabel => PngCount == 0 ? "No PNGs" : $"{PngCount:N0} PNG";
    public string Status => CroppedCount > 0
        ? $"{CroppedCount:N0} cropped ready"
        : ConvertedCount > 0
            ? $"{ConvertedCount:N0} JPG ready"
            : "Ready";
}
