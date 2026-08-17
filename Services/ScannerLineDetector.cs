using OpenCvSharp;

namespace PhotoTools2.Services;

public static class ScannerLineDetector
{
    public sealed record Finding(string Path, double Position, bool IsHorizontal, double Confidence, double SingleImageConfidence, double Coverage, int WidthPixels, int BatchMatches);
    public sealed record ImageDiagnostic(string Path, double MaxZScore, double MaxCoverage, int CandidateColumns, double ProfileMedian, double ProfileMad, double ProfileMaximum, double MaxBandMean);
    public sealed record BatchResult(IReadOnlyList<Finding> Findings, IReadOnlyList<(double Position, double Confidence, bool IsHorizontal)> Lines, int AnalyzedCount, IReadOnlyList<ImageDiagnostic>? Diagnostics = null);
    private sealed record Candidate(string Path, double Position, bool IsHorizontal, double Confidence, double Coverage, int WidthPixels);
    private sealed record DirectionResult(IReadOnlyList<Candidate> Candidates, double MaxZ, double MaxCoverage, int CandidateColumns, double Center, double Mad, double ProfileMaximum, double MaxBandMean);

    public static async Task<BatchResult> AnalyzeAsync(IReadOnlyList<string> paths, double sensitivity, IProgress<(int done, int total)>? progress, CancellationToken token)
    {
        var candidates = new List<Candidate>();
        var diagnostics = new List<ImageDiagnostic>();
        for (var index = 0; index < paths.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var detected = await Task.Run(() => AnalyzeSingle(paths[index], sensitivity, token), token);
            candidates.AddRange(detected.Candidates);
            diagnostics.Add(detected.Diagnostic);
            progress?.Report((index + 1, paths.Count));
        }

        var findings = candidates.Select(candidate =>
        {
            var matches = candidates.Where(other => other.IsHorizontal == candidate.IsHorizontal && !string.Equals(other.Path, candidate.Path, StringComparison.OrdinalIgnoreCase) && Math.Abs(other.Position - candidate.Position) <= 0.006)
                .Select(other => other.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var recurrenceBonus = Math.Min(0.18, matches * 0.045);
            var finalConfidence = Math.Clamp(candidate.Confidence + recurrenceBonus, 0, 1);
            return new Finding(candidate.Path, candidate.Position, candidate.IsHorizontal, finalConfidence, candidate.Confidence, candidate.Coverage, candidate.WidthPixels, matches);
        }).Where(finding =>
            (finding.Coverage >= 0.80 && finding.Confidence >= Math.Max(0.46, 0.58 - sensitivity * 0.10)) ||
            (finding.BatchMatches >= 2 && finding.Coverage >= 0.32 && finding.Confidence >= 0.54)).ToArray();

        var lines = findings.GroupBy(item => item.IsHorizontal)
            .SelectMany(direction => Cluster(direction.OrderBy(item => item.Position).ToArray())
                .Select(cluster => (cluster.Average(item => item.Position), cluster.Max(item => item.Confidence), direction.Key))).ToArray();
        return new BatchResult(findings, lines, paths.Count, diagnostics);
    }

    private static (IReadOnlyList<Candidate> Candidates, ImageDiagnostic Diagnostic) AnalyzeSingle(string path, double sensitivity, CancellationToken token)
    {
        using var source = Cv2.ImRead(path, ImreadModes.Color);
        if (source.Empty()) return ([], EmptyDiagnostic(path));
        token.ThrowIfCancellationRequested();
        using var working = new Mat();
        var scale = Math.Min(1d, 2200d / Math.Max(source.Width, source.Height));
        if (scale < 1) Cv2.Resize(source, working, new Size(), scale, scale, InterpolationFlags.Area); else source.CopyTo(working);
        if (working.Width < 40 || working.Height < 40) return ([], EmptyDiagnostic(path));

        var vertical = AnalyzeDirection(working, path, false, sensitivity, scale, token);
        using var transposed = new Mat();
        Cv2.Transpose(working, transposed);
        var horizontal = AnalyzeDirection(transposed, path, true, sensitivity, scale, token);
        var all = vertical.Candidates.Concat(horizontal.Candidates).OrderByDescending(item => item.Confidence).Take(12).ToArray();
        var strongest = vertical.MaxZ >= horizontal.MaxZ ? vertical : horizontal;
        return (all, new ImageDiagnostic(path, Math.Max(vertical.MaxZ, horizontal.MaxZ), Math.Max(vertical.MaxCoverage, horizontal.MaxCoverage), vertical.CandidateColumns + horizontal.CandidateColumns, strongest.Center, strongest.Mad, Math.Max(vertical.ProfileMaximum, horizontal.ProfileMaximum), Math.Max(vertical.MaxBandMean, horizontal.MaxBandMean)));
    }

    private static DirectionResult AnalyzeDirection(Mat working, string path, bool isHorizontal, double sensitivity, double scale, CancellationToken token)
    {
        var width = working.Width;
        var height = working.Height;
        using var lab = new Mat();
        Cv2.CvtColor(working, lab, ColorConversionCodes.BGR2Lab);
        var channels = Cv2.Split(lab);
        using var combined = new Mat(height, width, MatType.CV_8UC1, Scalar.All(0));
        try
        {
            foreach (var channel in channels)
            {
                using var background = new Mat();
                using var residual = new Mat();
                using var directionalEvidence = new Mat();
                Cv2.GaussianBlur(channel, background, new Size(31, 1), 0);
                Cv2.Absdiff(channel, background, residual);
                var continuityKernel = Math.Max(31, height / 20); if (continuityKernel % 2 == 0) continuityKernel++;
                Cv2.GaussianBlur(residual, directionalEvidence, new Size(1, continuityKernel), 0);
                Cv2.Max(combined, directionalEvidence, combined);
            }
        }
        finally { foreach (var channel in channels) channel.Dispose(); }

        token.ThrowIfCancellationRequested();
        var marginY = Math.Max(2, height / 30);
        var profile = new double[width];
        for (var x = 0; x < width; x++)
        {
            using var column = new Mat(combined, new Rect(x, marginY, 1, height - marginY * 2));
            profile[x] = Cv2.Mean(column).Val0;
        }
        var center = Median(profile);
        var mad = Math.Max(0.08, Median(profile.Select(value => Math.Abs(value - center)).ToArray()));
        var zThreshold = 5.0 - Math.Clamp(sensitivity, 0, 1) * 2.2;
        // Scanner streaks can occur almost anywhere, but the outer strip is dominated by
        // paper edges, album borders, and scanner-bed shadows. Suppress that strip more
        // aggressively while allowing high sensitivity to recover a little of it.
        var edgeFraction = 0.07 - Math.Clamp(sensitivity, 0, 1) * 0.02;
        var edgeMargin = Math.Max(4, (int)Math.Round(width * edgeFraction));
        var selected = Enumerable.Range(edgeMargin, width - edgeMargin * 2).Where(x => (profile[x] - center) / (1.4826 * mad) >= zThreshold).ToArray();

        var results = new List<Candidate>();
        var maxCoverage = 0d;
        var maxBandMean = 0d;
        foreach (var group in GroupAdjacent(selected, 2))
        {
            var strongest = group.OrderByDescending(x => profile[x]).First();
            var z = (profile[strongest] - center) / (1.4826 * mad);
            var pixelThreshold = Math.Max(1, center + 1.25 * 1.4826 * mad);
            const int bandCount = 24;
            var presentBands = 0;
            var usableHeight = height - marginY * 2;
            for (var band = 0; band < bandCount; band++)
            {
                var y1 = marginY + usableHeight * band / bandCount;
                var y2 = marginY + usableHeight * (band + 1) / bandCount;
                using var bandColumn = new Mat(combined, new Rect(strongest, y1, 1, Math.Max(1, y2 - y1)));
                var bandMean = Cv2.Mean(bandColumn).Val0;
                maxBandMean = Math.Max(maxBandMean, bandMean);
                if (bandMean >= pixelThreshold) presentBands++;
            }
            var coverage = presentBands / (double)bandCount;
            maxCoverage = Math.Max(maxCoverage, coverage);
            if (coverage < 0.16) continue;
            var zScore = Math.Clamp((z - zThreshold) / 7d, 0, 1);
            var coverageScore = Math.Clamp((coverage - 0.16) / 0.7, 0, 1);
            var coreThreshold = center + (profile[strongest] - center) * 0.55;
            var coreWidth = Math.Max(1, group.Count(x => profile[x] >= coreThreshold));
            var widthAtOriginal = Math.Max(1, (int)Math.Round(coreWidth / scale));
            var widthScore = widthAtOriginal <= 12 ? 1d : Math.Clamp(1 - (widthAtOriginal - 12) / 30d, 0.15, 1);
            var confidence = Math.Clamp(0.3 + zScore * 0.34 + coverageScore * 0.3 + widthScore * 0.12, 0, 0.88);
            results.Add(new Candidate(path, strongest / (double)Math.Max(1, width - 1), isHorizontal, confidence, coverage, widthAtOriginal));
        }
        var maxZ = selected.Length == 0 ? 0 : selected.Max(x => (profile[x] - center) / (1.4826 * mad));
        return new DirectionResult(results.OrderByDescending(result => result.Confidence).Take(8).ToArray(), maxZ, maxCoverage, selected.Length, center, mad, profile.Max(), maxBandMean);
    }

    private static ImageDiagnostic EmptyDiagnostic(string path) => new(path, 0, 0, 0, 0, 0, 0, 0);
    private static List<Finding[]> Cluster(Finding[] values) { var groups = new List<List<Finding>>(); foreach (var value in values) { if (groups.Count == 0 || value.Position - groups[^1][^1].Position > 0.008) groups.Add([value]); else groups[^1].Add(value); } return groups.Select(group => group.ToArray()).ToList(); }
    private static List<int[]> GroupAdjacent(int[] values, int allowedGap) { var groups = new List<List<int>>(); foreach (var value in values) { if (groups.Count == 0 || value > groups[^1][^1] + allowedGap + 1) groups.Add([value]); else groups[^1].Add(value); } return groups.Select(group => group.ToArray()).ToList(); }
    private static double Median(double[] values) { if (values.Length == 0) return 0; var ordered = (double[])values.Clone(); Array.Sort(ordered); return ordered.Length % 2 == 0 ? (ordered[ordered.Length / 2 - 1] + ordered[ordered.Length / 2]) / 2 : ordered[ordered.Length / 2]; }
}
