namespace PhotoTools2.Models;

public sealed class PhotoSessionItem
{
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public int PhotoCount { get; set; }
    public int SectionNumber { get; set; }
    public string DateLabel => Start.ToString("dddd, MMMM d, yyyy");
    public string TimeLabel => $"{Start:t} - {End:t}";
    public string CountLabel => $"{PhotoCount:N0} photo{(PhotoCount == 1 ? string.Empty : "s")}";
    public string DurationLabel => FormatDuration(End - Start);

    public static string FormatDuration(TimeSpan duration)
    {
        var minutes = Math.Max(0, (int)Math.Round(duration.TotalMinutes));
        return minutes >= 60 ? $"{minutes / 60} hr {minutes % 60} min" : $"{minutes} min";
    }
}
