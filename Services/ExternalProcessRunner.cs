using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;

namespace PhotoTools2.Services;

public static class ExternalProcessRunner
{
    private static readonly SemaphoreSlim ProcessSlots = new(2, 2);
    private static readonly ConcurrentDictionary<string, Task<bool>> Availability = new(StringComparer.OrdinalIgnoreCase);

    public static Task<bool> IsAvailableAsync(string executable, IReadOnlyList<string>? probeArguments = null) =>
        Availability.GetOrAdd(executable, _ => ProbeAsync(executable, probeArguments ?? ["--version"]));

    public static async Task<ExternalProcessResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        CancellationToken token = default)
    {
        try { await ProcessSlots.WaitAsync(token); }
        catch (OperationCanceledException) { return new ExternalProcessResult(false, -2, string.Empty, string.Empty, true); }
        try { return await RunCoreAsync(executable, arguments, token); }
        finally { ProcessSlots.Release(); }
    }

    private static async Task<bool> ProbeAsync(string executable, IReadOnlyList<string> arguments)
    {
        var result = await RunCoreAsync(executable, arguments, CancellationToken.None);
        return result.Started && result.ExitCode == 0;
    }

    private static async Task<ExternalProcessResult> RunCoreAsync(string executable, IEnumerable<string> arguments, CancellationToken token)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start);
            if (process is null) return new ExternalProcessResult(false, -1, string.Empty, $"Could not start {executable}.", false);
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync(token); }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
                try { await process.WaitForExitAsync(); } catch (InvalidOperationException) { }
                return new ExternalProcessResult(true, -2, await outputTask, await errorTask, true);
            }
            return new ExternalProcessResult(true, process.ExitCode, await outputTask, await errorTask, false);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Availability.TryRemove(executable, out _);
            return new ExternalProcessResult(false, -1, string.Empty, ex.Message, false);
        }
    }
}

public sealed record ExternalProcessResult(bool Started, int ExitCode, string StandardOutput, string StandardError, bool WasCancelled)
{
    public bool Succeeded => Started && !WasCancelled && ExitCode == 0;
    public string ErrorMessage => !string.IsNullOrWhiteSpace(StandardError)
        ? StandardError.Trim()
        : !Started ? "The external program could not be started." : $"The external program exited with code {ExitCode}.";
}
