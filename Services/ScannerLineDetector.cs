using System.Collections.Concurrent;
using OpenCvSharp;

namespace PhotoTools2.Services;

public static class ScannerLineDetector
{
    private const int AlgorithmVersion = 17;
    private const int MaximumCacheEntries = 1024;
    private const double MatchTolerance = 0.006;
    private static readonly object CacheSync = new();
    private static readonly Dictionary<DetectionCacheKey, DetectionCacheEntry> DetectionCache = [];
    private static readonly LinkedList<DetectionCacheKey> CacheUsage = [];
    public sealed record Finding(string Path, double Position, bool IsHorizontal, double Confidence, double SingleImageConfidence, double Coverage,
        int WidthPixels, int BatchMatches, double Continuity, double WidthConsistency, double IntensityConsistency,
        double SideContrast, double EndReach, double PositionStability, double GapScore, int LargestMissingGapBands, double BorderPenalty);
    public sealed record DirectionPipelineDiagnostic(bool FastPassPassed, int ProfileCandidateGroups, int CoveragePassed, int StructuralPassed, int FinalAccepted);
    public sealed record ImageDiagnostic(string Path, double MaxZScore, double MaxCoverage, int CandidateColumns, double ProfileMedian, double ProfileMad,
        double ProfileMaximum, double MaxBandMean, DirectionPipelineDiagnostic Vertical, DirectionPipelineDiagnostic Horizontal);
    public sealed record BatchResult(IReadOnlyList<Finding> Findings, IReadOnlyList<(double Position, double Confidence, bool IsHorizontal)> Lines,
        int AnalyzedCount, IReadOnlyList<ImageDiagnostic>? Diagnostics = null, IReadOnlyList<Finding>? CandidateFindings = null);
    private sealed record Candidate(string Path, double Position, bool IsHorizontal, double Confidence, double Coverage, int WidthPixels,
        double Continuity, double WidthConsistency, double IntensityConsistency, double SideContrast, double EndReach,
        double PositionStability, double GapScore, int LargestMissingGapBands, double BorderPenalty);
    private sealed record DirectionResult(IReadOnlyList<Candidate> Candidates, double MaxZ, double MaxCoverage, int CandidateColumns, double Center,
        double Mad, double ProfileMaximum, double MaxBandMean, int ProfileCandidateGroups, int CoveragePassed);
    private sealed record CandidateValidation(double Coverage, double Continuity, double WidthConsistency, double IntensityConsistency,
        double SideContrast, double EndReach, double PositionStability, double GapScore, int LargestMissingGapBands,
        int WidthPixels, double BorderPenalty, double MaximumBandMean);

    public static async Task<BatchResult> AnalyzeAsync(IReadOnlyList<string> paths, double sensitivity, IProgress<(int done, int total)>? progress, CancellationToken token)
    {
        var candidateBag = new ConcurrentBag<Candidate>();
        var diagnosticBag = new ConcurrentBag<ImageDiagnostic>();
        var completed = 0;
        var workerCount = Math.Clamp(Environment.ProcessorCount / 2, 2, 3);
        await Parallel.ForEachAsync(paths, new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = workerCount },
            (path, cancellationToken) =>
            {
                var detected = AnalyzeSingleCached(path, sensitivity, cancellationToken);
                foreach (var candidate in detected.Candidates) candidateBag.Add(candidate);
                diagnosticBag.Add(detected.Diagnostic);
                progress?.Report((Interlocked.Increment(ref completed), paths.Count));
                return ValueTask.CompletedTask;
            });
        var candidates = candidateBag.OrderBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase).ThenBy(candidate => candidate.Position).ToArray();
        var diagnostics = diagnosticBag.OrderBy(diagnostic => diagnostic.Path, StringComparer.OrdinalIgnoreCase).ToArray();

        var positionBuckets = BuildPositionBuckets(candidates);
        var candidateFindings = candidates.Select(candidate =>
        {
            var matches = CountBatchMatches(candidate, positionBuckets);
            var recurrenceBonus = Math.Min(0.18, matches * 0.045);
            var finalConfidence = Math.Clamp(candidate.Confidence + recurrenceBonus, 0, 1);
            return new Finding(candidate.Path, candidate.Position, candidate.IsHorizontal, finalConfidence, candidate.Confidence, candidate.Coverage,
                candidate.WidthPixels, matches, candidate.Continuity, candidate.WidthConsistency, candidate.IntensityConsistency,
                candidate.SideContrast, candidate.EndReach, candidate.PositionStability, candidate.GapScore,
                candidate.LargestMissingGapBands, candidate.BorderPenalty);
        }).ToArray();
        var findings = candidateFindings.Where(finding =>
        {
            var minimumBilateralContrast = 0.32 - Math.Clamp(sensitivity, 0, 1) * 0.08;
            var continuousLine = finding.SideContrast >= minimumBilateralContrast && finding.Coverage >= 0.80
                && finding.Confidence >= Math.Max(0.46, 0.58 - sensitivity * 0.10);
            var recurringPartialLine = finding.SideContrast >= minimumBilateralContrast
                && finding.BatchMatches >= 3 && finding.Coverage >= 0.40 && finding.Continuity >= 0.48
                && finding.EndReach >= 0.34 && finding.GapScore >= 0.50 && finding.Confidence >= 0.54;

            // Interrupted scanner streaks often lose enough bands to miss the normal coverage gate.
            // Admit them only when the remaining sections still describe one narrow, stable line.
            // Strong consistency and a low border penalty keep page edges, frames, and
            // photographic structures from benefiting from the relaxed contrast floor.
            var interruptedLine = finding.Coverage >= 0.70 && finding.Continuity >= 0.55
                && finding.GapScore >= 0.75 && finding.LargestMissingGapBands <= 4
                && finding.PositionStability >= 0.40 && finding.WidthConsistency >= 0.30
                && finding.IntensityConsistency >= 0.55 && finding.SideContrast >= 0.12
                && finding.BorderPenalty <= 0.10 && finding.Confidence >= 0.90;
            return continuousLine || recurringPartialLine || interruptedLine;
        }).ToArray();

        var lines = findings.GroupBy(item => item.IsHorizontal)
            .SelectMany(direction => Cluster(direction.OrderBy(item => item.Position).ToArray())
                .Select(cluster => (cluster.Average(item => item.Position), cluster.Max(item => item.Confidence), direction.Key))).ToArray();
        var finalCounts = findings.GroupBy(finding => (Path: Path.GetFullPath(finding.Path).ToUpperInvariant(), finding.IsHorizontal))
            .ToDictionary(group => group.Key, group => group.Count());
        diagnostics = diagnostics.Select(diagnostic => diagnostic with
        {
            Vertical = diagnostic.Vertical with { FinalAccepted = finalCounts.GetValueOrDefault((Path.GetFullPath(diagnostic.Path).ToUpperInvariant(), false)) },
            Horizontal = diagnostic.Horizontal with { FinalAccepted = finalCounts.GetValueOrDefault((Path.GetFullPath(diagnostic.Path).ToUpperInvariant(), true)) }
        }).ToArray();
        return new BatchResult(findings, lines, paths.Count, diagnostics, candidateFindings);
    }

    private static (IReadOnlyList<Candidate> Candidates, ImageDiagnostic Diagnostic) AnalyzeSingleCached(string path, double sensitivity, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        DetectionCacheKey key;
        try
        {
            var info = new FileInfo(path);
            key = new DetectionCacheKey(Path.GetFullPath(path).ToUpperInvariant(), info.Length, info.LastWriteTimeUtc.Ticks, BitConverter.DoubleToInt64Bits(sensitivity), AlgorithmVersion);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return AnalyzeSingle(path, sensitivity, token);
        }

        lock (CacheSync)
        {
            if (DetectionCache.TryGetValue(key, out var cached))
            {
                CacheUsage.Remove(cached.UsageNode);
                CacheUsage.AddFirst(cached.UsageNode);
                return cached.Result;
            }
        }

        var result = AnalyzeSingle(path, sensitivity, token);
        token.ThrowIfCancellationRequested();
        lock (CacheSync)
        {
            if (!DetectionCache.ContainsKey(key))
            {
                var node = CacheUsage.AddFirst(key);
                DetectionCache[key] = new DetectionCacheEntry(result, node);
                while (DetectionCache.Count > MaximumCacheEntries && CacheUsage.Last is { } oldest)
                {
                    DetectionCache.Remove(oldest.Value);
                    CacheUsage.RemoveLast();
                }
            }
        }
        return result;
    }

    private static Dictionary<(bool IsHorizontal, int Bucket), List<Candidate>> BuildPositionBuckets(IEnumerable<Candidate> candidates)
    {
        var buckets = new Dictionary<(bool IsHorizontal, int Bucket), List<Candidate>>();
        foreach (var candidate in candidates)
        {
            var key = (candidate.IsHorizontal, (int)Math.Floor(candidate.Position / MatchTolerance));
            if (!buckets.TryGetValue(key, out var bucket)) buckets[key] = bucket = [];
            bucket.Add(candidate);
        }
        return buckets;
    }

    private static int CountBatchMatches(Candidate candidate, Dictionary<(bool IsHorizontal, int Bucket), List<Candidate>> buckets)
    {
        var centerBucket = (int)Math.Floor(candidate.Position / MatchTolerance);
        var matchingPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var bucketIndex = centerBucket - 1; bucketIndex <= centerBucket + 1; bucketIndex++)
        {
            if (!buckets.TryGetValue((candidate.IsHorizontal, bucketIndex), out var bucket)) continue;
            foreach (var other in bucket)
            {
                if (!string.Equals(other.Path, candidate.Path, StringComparison.OrdinalIgnoreCase)
                    && Math.Abs(other.Position - candidate.Position) <= MatchTolerance)
                    matchingPaths.Add(other.Path);
            }
        }
        return matchingPaths.Count;
    }

    private static (IReadOnlyList<Candidate> Candidates, ImageDiagnostic Diagnostic) AnalyzeSingle(string path, double sensitivity, CancellationToken token)
    {
        var decodeReduction = GetJpegDecodeReduction(path);
        var readMode = decodeReduction switch
        {
            8 => ImreadModes.ReducedColor8,
            4 => ImreadModes.ReducedColor4,
            2 => ImreadModes.ReducedColor2,
            _ => ImreadModes.Color
        };
        using var source = Cv2.ImRead(path, readMode);
        if (source.Empty()) return ([], EmptyDiagnostic(path));
        token.ThrowIfCancellationRequested();
        using var working = new Mat();
        var resizeScale = Math.Min(1d, 2200d / Math.Max(source.Width, source.Height));
        if (resizeScale < 1) Cv2.Resize(source, working, new Size(), resizeScale, resizeScale, InterpolationFlags.Area); else source.CopyTo(working);
        var scale = resizeScale / decodeReduction;
        if (working.Width < 40 || working.Height < 40) return ([], EmptyDiagnostic(path));

        var likelyDirections = FastFirstPass(working, sensitivity, token);
        if (!likelyDirections.Vertical && !likelyDirections.Horizontal) return ([], EmptyDiagnostic(path));

        using var lab = new Mat();
        Cv2.CvtColor(working, lab, ColorConversionCodes.BGR2Lab);
        var channels = Cv2.Split(lab);
        DirectionResult vertical;
        DirectionResult horizontal;
        try
        {
            vertical = likelyDirections.Vertical
                ? AnalyzeDirection(channels, working.Width, working.Height, path, false, sensitivity, scale, token)
                : EmptyDirection();
            if (likelyDirections.Horizontal)
            {
                var transposedChannels = channels.Select(channel =>
                {
                    var transposed = new Mat();
                    Cv2.Transpose(channel, transposed);
                    return transposed;
                }).ToArray();
                try { horizontal = AnalyzeDirection(transposedChannels, working.Height, working.Width, path, true, sensitivity, scale, token); }
                finally { foreach (var channel in transposedChannels) channel.Dispose(); }
            }
            else horizontal = EmptyDirection();
        }
        finally { foreach (var channel in channels) channel.Dispose(); }
        vertical = vertical with { Candidates = RejectSymmetricOuterFramePairs(vertical.Candidates) };
        horizontal = horizontal with { Candidates = RejectSymmetricOuterFramePairs(horizontal.Candidates) };
        var all = vertical.Candidates.Concat(horizontal.Candidates).OrderByDescending(item => item.Confidence).Take(12).ToArray();
        var strongest = vertical.MaxZ >= horizontal.MaxZ ? vertical : horizontal;
        var verticalDiagnostic = new DirectionPipelineDiagnostic(likelyDirections.Vertical, vertical.ProfileCandidateGroups,
            vertical.CoveragePassed, vertical.Candidates.Count, 0);
        var horizontalDiagnostic = new DirectionPipelineDiagnostic(likelyDirections.Horizontal, horizontal.ProfileCandidateGroups,
            horizontal.CoveragePassed, horizontal.Candidates.Count, 0);
        return (all, new ImageDiagnostic(path, Math.Max(vertical.MaxZ, horizontal.MaxZ), Math.Max(vertical.MaxCoverage, horizontal.MaxCoverage),
            vertical.CandidateColumns + horizontal.CandidateColumns, strongest.Center, strongest.Mad,
            Math.Max(vertical.ProfileMaximum, horizontal.ProfileMaximum), Math.Max(vertical.MaxBandMean, horizontal.MaxBandMean),
            verticalDiagnostic, horizontalDiagnostic));
    }

    private static IReadOnlyList<Candidate> RejectSymmetricOuterFramePairs(IReadOnlyList<Candidate> candidates)
    {
        if (candidates.Count < 2) return candidates;
        return candidates.Where(candidate => !candidates.Any(other =>
            !ReferenceEquals(candidate, other)
            && ((candidate.Position <= 0.16 && other.Position >= 0.84) || (candidate.Position >= 0.84 && other.Position <= 0.16))
            && Math.Abs(candidate.Position + other.Position - 1) <= 0.055
            && Math.Abs(candidate.Coverage - other.Coverage) <= 0.18
            && Math.Min(candidate.Continuity, other.Continuity) >= 0.62
            && Math.Abs(candidate.WidthPixels - other.WidthPixels) <= Math.Max(3, Math.Min(candidate.WidthPixels, other.WidthPixels))
        )).ToArray();
    }

    private static (bool Vertical, bool Horizontal) FastFirstPass(Mat working, double sensitivity, CancellationToken token)
    {
        using var preview = new Mat();
        var previewScale = Math.Min(1d, 700d / Math.Max(working.Width, working.Height));
        if (previewScale < 1) Cv2.Resize(working, preview, new Size(), previewScale, previewScale, InterpolationFlags.Area);
        else working.CopyTo(preview);
        var channels = Cv2.Split(preview);
        try
        {
            var vertical = channels.Any(channel => HasLikelyLine(channel, sensitivity, token));
            token.ThrowIfCancellationRequested();
            var horizontal = false;
            foreach (var channel in channels)
            {
                using var transposed = new Mat();
                Cv2.Transpose(channel, transposed);
                if (HasLikelyLine(transposed, sensitivity, token)) { horizontal = true; break; }
            }
            return (vertical, horizontal);
        }
        finally { foreach (var channel in channels) channel.Dispose(); }
    }

    private static bool HasLikelyLine(Mat channel, double sensitivity, CancellationToken token)
    {
        if (channel.Width < 20 || channel.Height < 20) return false;
        using var background = new Mat();
        using var residual = new Mat();
        using var continuous = new Mat();
        Cv2.GaussianBlur(channel, background, new Size(15, 1), 0);
        Cv2.Absdiff(channel, background, residual);
        var continuityKernel = Math.Max(21, channel.Height / 24); if (continuityKernel % 2 == 0) continuityKernel++;
        Cv2.GaussianBlur(residual, continuous, new Size(1, continuityKernel), 0);
        token.ThrowIfCancellationRequested();

        var width = channel.Width;
        var height = channel.Height;
        var marginY = Math.Max(2, height / 30);
        var marginX = Math.Max(3, width / 25);
        using var profileSource = continuous.RowRange(marginY, height - marginY);
        using var reducedProfile = new Mat();
        Cv2.Reduce(profileSource, reducedProfile, ReduceDimension.Row, ReduceTypes.Avg, MatType.CV_64FC1.Value);
        var profile = new double[width];
        for (var x = 0; x < profile.Length; x++) profile[x] = reducedProfile.At<double>(0, x);
        var center = Median(profile);
        var mad = Math.Max(0.08, Median(profile.Select(value => Math.Abs(value - center)).ToArray()));
        var deviation = 1.4826 * mad;
        var permissiveThreshold = 2.0 - Math.Clamp(sensitivity, 0, 1) * 0.6;
        for (var x = marginX; x < width - marginX; x++)
            if ((profile[x] - center) / deviation >= permissiveThreshold) return true;
        return false;
    }

    private static DirectionResult AnalyzeDirection(Mat[] channels, int width, int height, string path, bool isHorizontal, double sensitivity, double scale, CancellationToken token)
    {
        using var combined = new Mat(height, width, MatType.CV_8UC1, Scalar.All(0));
        foreach (var channel in channels)
        {
            var continuityKernel = Math.Max(31, height / 20); if (continuityKernel % 2 == 0) continuityKernel++;
            foreach (var residualKernel in new[] { 15, 31, 61 })
            {
                using var background = new Mat();
                using var residual = new Mat();
                using var directionalEvidence = new Mat();
                Cv2.GaussianBlur(channel, background, new Size(residualKernel, 1), 0);
                Cv2.Absdiff(channel, background, residual);
                Cv2.GaussianBlur(residual, directionalEvidence, new Size(1, continuityKernel), 0);
                if (residualKernel == 31) Cv2.Max(combined, directionalEvidence, combined);
                else
                {
                    using var weightedEvidence = new Mat();
                    Cv2.ConvertScaleAbs(directionalEvidence, weightedEvidence, 0.68);
                    Cv2.Max(combined, weightedEvidence, combined);
                }
            }
            AccumulateBilateralContrast(channel, combined, width, continuityKernel);
        }

        token.ThrowIfCancellationRequested();
        var marginY = Math.Max(2, height / 30);
        var profile = new double[width];
        using (var profileSource = combined.RowRange(marginY, height - marginY))
        using (var reducedProfile = new Mat())
        {
            Cv2.Reduce(profileSource, reducedProfile, ReduceDimension.Row, ReduceTypes.Avg, MatType.CV_64FC1.Value);
            for (var x = 0; x < width; x++) profile[x] = reducedProfile.At<double>(0, x);
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
        var candidateGroups = GroupAdjacent(selected, 2);
        var coveragePassed = 0;
        foreach (var group in candidateGroups)
        {
            var strongest = group.OrderByDescending(x => profile[x]).First();
            var z = (profile[strongest] - center) / (1.4826 * mad);
            var pixelThreshold = Math.Max(1, center + 1.25 * 1.4826 * mad);
            var validation = ValidateCandidate(combined, channels[0], strongest, marginY, pixelThreshold, scale);
            maxBandMean = Math.Max(maxBandMean, validation.MaximumBandMean);
            var coverage = validation.Coverage;
            maxCoverage = Math.Max(maxCoverage, coverage);
            if (coverage < 0.16) continue;
            coveragePassed++;
            var zScore = Math.Clamp((z - zThreshold) / 7d, 0, 1);
            var coverageScore = Math.Clamp((coverage - 0.16) / 0.7, 0, 1);
            var widthAtOriginal = validation.WidthPixels;
            var widthScore = widthAtOriginal <= 12 ? 1d : Math.Clamp(1 - (widthAtOriginal - 12) / 30d, 0.15, 1);
            var structuralScore = validation.Continuity * 0.15 + validation.GapScore * 0.08 + validation.WidthConsistency * 0.14
                + validation.IntensityConsistency * 0.10 + validation.SideContrast * 0.12
                + validation.EndReach * 0.10 + validation.PositionStability * 0.14;
            var confidence = Math.Clamp(0.18 + zScore * 0.22 + coverageScore * 0.18 + widthScore * 0.10
                + structuralScore - validation.BorderPenalty * 0.24, 0, 0.92);
            if (validation.Continuity < 0.24 || validation.GapScore < 0.30 || validation.PositionStability < 0.20 || validation.WidthConsistency < 0.12) continue;
            results.Add(new Candidate(path, strongest / (double)Math.Max(1, width - 1), isHorizontal, confidence, coverage, widthAtOriginal,
                validation.Continuity, validation.WidthConsistency, validation.IntensityConsistency, validation.SideContrast,
                validation.EndReach, validation.PositionStability, validation.GapScore, validation.LargestMissingGapBands, validation.BorderPenalty));
        }
        var maxZ = selected.Length == 0 ? 0 : selected.Max(x => (profile[x] - center) / (1.4826 * mad));
        return new DirectionResult(results.OrderByDescending(result => result.Confidence).Take(8).ToArray(), maxZ, maxCoverage,
            selected.Length, center, mad, profile.Max(), maxBandMean, candidateGroups.Count, coveragePassed);
    }

    private static void AccumulateBilateralContrast(Mat channel, Mat combined, int width, int continuityKernel)
    {
        foreach (var offset in new[] { 3, 6, 10 })
        {
            if (width <= offset * 2 + 2) continue;
            using var left = channel.ColRange(0, width - offset * 2);
            using var center = channel.ColRange(offset, width - offset);
            using var right = channel.ColRange(offset * 2, width);
            using var leftContrast = new Mat();
            using var rightContrast = new Mat();
            using var bilateralContrast = new Mat();
            using var continuousContrast = new Mat();
            Cv2.Absdiff(center, left, leftContrast);
            Cv2.Absdiff(center, right, rightContrast);
            Cv2.Min(leftContrast, rightContrast, bilateralContrast);
            Cv2.GaussianBlur(bilateralContrast, continuousContrast, new Size(1, continuityKernel), 0);
            using var destination = combined.ColRange(offset, width - offset);
            using var weightedContrast = new Mat();
            Cv2.ConvertScaleAbs(continuousContrast, weightedContrast, 0.82);
            Cv2.Max(destination, weightedContrast, destination);
        }
    }

    private static CandidateValidation ValidateCandidate(Mat evidence, Mat luminance, int centerX, int marginY, double threshold, double scale)
    {
        const int bandCount = 32;
        const int searchRadius = 3;
        const int widthRadius = 7;
        var evidenceWidth = evidence.Width;
        var evidenceHeight = evidence.Height;
        var luminanceWidth = luminance.Width;
        var usableHeight = evidenceHeight - marginY * 2;
        var present = new bool[bandCount];
        var positions = new List<double>(bandCount);
        var widths = new List<double>(bandCount);
        var signals = new List<double>(bandCount);
        var contrasts = new List<double>(bandCount);
        var maximumBandMean = 0d;

        for (var band = 0; band < bandCount; band++)
        {
            var y1 = marginY + usableHeight * band / bandCount;
            var y2 = marginY + usableHeight * (band + 1) / bandCount;
            var bandHeight = Math.Max(1, y2 - y1);
            var bestX = centerX;
            var bestSignal = double.MinValue;
            for (var x = Math.Max(0, centerX - searchRadius); x <= Math.Min(evidenceWidth - 1, centerX + searchRadius); x++)
            {
                using var column = new Mat(evidence, new Rect(x, y1, 1, bandHeight));
                var signal = Cv2.Mean(column).Val0;
                if (signal > bestSignal) { bestSignal = signal; bestX = x; }
            }
            maximumBandMean = Math.Max(maximumBandMean, bestSignal);
            if (bestSignal < threshold) continue;

            present[band] = true;
            positions.Add(bestX);
            signals.Add(bestSignal);
            var bandWidth = 1;
            for (var offset = 1; offset <= widthRadius; offset++)
            {
                var found = false;
                foreach (var x in new[] { bestX - offset, bestX + offset })
                {
                    if (x < 0 || x >= evidenceWidth) continue;
                    using var neighbor = new Mat(evidence, new Rect(x, y1, 1, bandHeight));
                    if (Cv2.Mean(neighbor).Val0 >= threshold * 0.72) { bandWidth++; found = true; }
                }
                if (!found && offset > 2) break;
            }
            widths.Add(bandWidth / Math.Max(scale, 0.000001));

            using var lineColumn = new Mat(luminance, new Rect(bestX, y1, 1, bandHeight));
            var lineMean = Cv2.Mean(lineColumn).Val0;
            var sideOffset = Math.Max(3, bandWidth + 2);
            var leftX = Math.Max(0, bestX - sideOffset);
            var rightX = Math.Min(luminanceWidth - 1, bestX + sideOffset);
            using var leftColumn = new Mat(luminance, new Rect(leftX, y1, 1, bandHeight));
            using var rightColumn = new Mat(luminance, new Rect(rightX, y1, 1, bandHeight));
            var leftContrast = Math.Abs(lineMean - Cv2.Mean(leftColumn).Val0);
            var rightContrast = Math.Abs(lineMean - Cv2.Mean(rightColumn).Val0);
            contrasts.Add(Math.Clamp(Math.Min(leftContrast, rightContrast) / 24d, 0, 1));
        }

        var presentCount = present.Count(value => value);
        var coverage = presentCount / (double)bandCount;
        var continuity = LongestRunAllowingShortGaps(present, 2) / (double)bandCount;
        var largestMissingGap = LargestInternalGap(present);
        var gapScore = Math.Clamp(1 - Math.Max(0, largestMissingGap - 2) / 8d, 0, 1);
        var endReach = (present.Take(3).Count(value => value) + present.TakeLast(3).Count(value => value)) / 6d;
        var widthConsistency = Consistency(widths);
        var intensityConsistency = Consistency(signals);
        var positionStability = positions.Count < 2 ? 0 : Math.Clamp(1 - StandardDeviation(positions) / 2.5, 0, 1);
        var averageWidth = widths.Count == 0 ? 1 : widths.Average();
        var positionFraction = centerX / (double)Math.Max(1, evidenceWidth - 1);
        var edgeDistance = Math.Min(positionFraction, 1 - positionFraction);
        var borderPenalty = Math.Clamp((0.09 - edgeDistance) / 0.09, 0, 1)
            + Math.Clamp((averageWidth - 24) / 40d, 0, 0.75);
        return new CandidateValidation(coverage, continuity, widthConsistency, intensityConsistency,
            contrasts.Count == 0 ? 0 : contrasts.Average(), endReach, positionStability, gapScore, largestMissingGap,
            Math.Max(1, (int)Math.Round(averageWidth)), Math.Clamp(borderPenalty, 0, 1), maximumBandMean);
    }

    private static int LongestRunAllowingShortGaps(IReadOnlyList<bool> present, int allowedGap)
    {
        var longest = 0;
        var start = 0;
        var missing = new Queue<int>();
        for (var end = 0; end < present.Count; end++)
        {
            if (!present[end]) missing.Enqueue(end);
            while (missing.Count > allowedGap) start = missing.Dequeue() + 1;
            longest = Math.Max(longest, end - start + 1);
        }
        return longest;
    }

    private static int LargestInternalGap(IReadOnlyList<bool> present)
    {
        var first = -1;
        var last = -1;
        for (var index = 0; index < present.Count; index++) if (present[index]) { first = index; break; }
        for (var index = present.Count - 1; index >= 0; index--) if (present[index]) { last = index; break; }
        if (first < 0 || last <= first) return present.Count;
        var largest = 0;
        var current = 0;
        for (var index = first + 1; index < last; index++)
        {
            if (!present[index]) { current++; largest = Math.Max(largest, current); }
            else current = 0;
        }
        return largest;
    }

    private static double Consistency(IReadOnlyCollection<double> values)
    {
        if (values.Count < 2) return 0;
        var mean = values.Average();
        return Math.Clamp(1 - StandardDeviation(values) / Math.Max(1, mean), 0, 1);
    }

    private static double StandardDeviation(IReadOnlyCollection<double> values)
    {
        if (values.Count < 2) return 0;
        var mean = values.Average();
        return Math.Sqrt(values.Sum(value => (value - mean) * (value - mean)) / values.Count);
    }

    private static ImageDiagnostic EmptyDiagnostic(string path) => new(path, 0, 0, 0, 0, 0, 0, 0,
        new(false, 0, 0, 0, 0), new(false, 0, 0, 0, 0));
    private static DirectionResult EmptyDirection() => new([], 0, 0, 0, 0, 0, 0, 0, 0, 0);
    private static int GetJpegDecodeReduction(string path)
    {
        var extension = Path.GetExtension(path);
        if (!extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)) return 1;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
            if (stream.ReadByte() != 0xFF || stream.ReadByte() != 0xD8) return 1;
            while (stream.Position + 4 < stream.Length)
            {
                int prefix;
                do { prefix = stream.ReadByte(); } while (prefix != -1 && prefix != 0xFF);
                int marker;
                do { marker = stream.ReadByte(); } while (marker == 0xFF);
                if (marker is -1 or 0xD9 or 0xDA) break;
                var lengthHigh = stream.ReadByte();
                var lengthLow = stream.ReadByte();
                if (lengthHigh < 0 || lengthLow < 0) break;
                var segmentLength = (lengthHigh << 8) | lengthLow;
                if (segmentLength < 2) break;
                if (marker is >= 0xC0 and <= 0xC3 or >= 0xC5 and <= 0xC7 or >= 0xC9 and <= 0xCB or >= 0xCD and <= 0xCF)
                {
                    if (stream.ReadByte() < 0) break;
                    var height = (stream.ReadByte() << 8) | stream.ReadByte();
                    var width = (stream.ReadByte() << 8) | stream.ReadByte();
                    var largest = Math.Max(width, height);
                    return largest >= 17600 ? 8 : largest >= 8800 ? 4 : largest >= 4400 ? 2 : 1;
                }
                stream.Seek(segmentLength - 2, SeekOrigin.Current);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        return 1;
    }
    private static List<Finding[]> Cluster(Finding[] values) { var groups = new List<List<Finding>>(); foreach (var value in values) { if (groups.Count == 0 || value.Position - groups[^1][^1].Position > 0.008) groups.Add([value]); else groups[^1].Add(value); } return groups.Select(group => group.ToArray()).ToList(); }
    private static List<int[]> GroupAdjacent(int[] values, int allowedGap) { var groups = new List<List<int>>(); foreach (var value in values) { if (groups.Count == 0 || value > groups[^1][^1] + allowedGap + 1) groups.Add([value]); else groups[^1].Add(value); } return groups.Select(group => group.ToArray()).ToList(); }
    private static double Median(double[] values) { if (values.Length == 0) return 0; var ordered = (double[])values.Clone(); Array.Sort(ordered); return ordered.Length % 2 == 0 ? (ordered[ordered.Length / 2 - 1] + ordered[ordered.Length / 2]) / 2 : ordered[ordered.Length / 2]; }
    private readonly record struct DetectionCacheKey(string Path, long Size, long ModifiedUtcTicks, long SensitivityBits, int AlgorithmVersion);
    private sealed record DetectionCacheEntry((IReadOnlyList<Candidate> Candidates, ImageDiagnostic Diagnostic) Result, LinkedListNode<DetectionCacheKey> UsageNode);
}
