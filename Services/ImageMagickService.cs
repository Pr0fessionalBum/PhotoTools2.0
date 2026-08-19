namespace PhotoTools2.Services;

public static class ImageMagickService
{
    public static Task<bool> IsAvailableAsync() => ExternalProcessRunner.IsAvailableAsync("magick", ["-version"]);

    public static async Task<ExternalProcessResult> RunAsync(IEnumerable<string> arguments, CancellationToken token = default)
    {
        if (!await IsAvailableAsync())
            return new ExternalProcessResult(false, -1, string.Empty, "ImageMagick was not found. Install ImageMagick 7 and ensure 'magick' is on PATH.", false);
        return await ExternalProcessRunner.RunAsync("magick", arguments, token);
    }
}
