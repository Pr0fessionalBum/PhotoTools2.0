using System.Text.Json;
using PhotoTools2.Services;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: ScannerLineAccuracyRunner <fixture-folder> <output-json> [sensitivity]");
    return 2;
}

var fixtureFolder = Path.GetFullPath(args[0]);
var outputPath = Path.GetFullPath(args[1]);
var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };
var sensitivity = args.Length >= 3 && double.TryParse(args[2], System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : 0.55;
var manifestPath = Path.Combine(fixtureFolder, "scanner-line-ground-truth.json");
if (!File.Exists(manifestPath)) throw new FileNotFoundException("Scanner-line ground truth was not found.", manifestPath);
var manifest = JsonSerializer.Deserialize<GroundTruthManifest>(await File.ReadAllTextAsync(manifestPath), jsonOptions)
    ?? throw new InvalidDataException("The scanner-line ground-truth manifest is invalid.");
var expected = manifest.Lines.ToDictionary(line => Path.GetFullPath(Path.Combine(fixtureFolder, line.File)), StringComparer.OrdinalIgnoreCase);
var files = Directory.EnumerateFiles(fixtureFolder)
    .Where(path => new[] { ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
var progress = new Progress<(int done, int total)>(value => Console.Write($"\rAnalyzing {value.done,5:N0} / {value.total,-5:N0}"));
var started = DateTime.UtcNow;
var result = await ScannerLineDetector.AnalyzeAsync(files, sensitivity, progress, CancellationToken.None);
Console.WriteLine();
var findings = result.Findings.GroupBy(finding => Path.GetFullPath(finding.Path), StringComparer.OrdinalIgnoreCase)
    .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
var candidates = (result.CandidateFindings ?? []).GroupBy(finding => Path.GetFullPath(finding.Path), StringComparer.OrdinalIgnoreCase)
    .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
var diagnostics = (result.Diagnostics ?? []).ToDictionary(diagnostic => Path.GetFullPath(diagnostic.Path), StringComparer.OrdinalIgnoreCase);

var truePositive = 0;
var falseNegative = 0;
var trueNegative = 0;
var falsePositive = 0;
var interruptedTotal = 0;
var interruptedDetected = 0;
var details = new List<ImageEvaluation>();
foreach (var file in files)
{
    expected.TryGetValue(file, out var truth);
    findings.TryGetValue(file, out var detected);
    detected ??= [];
    var matched = truth is not null && detected.Any(finding =>
        finding.IsHorizontal == truth.Orientation.Equals("horizontal", StringComparison.OrdinalIgnoreCase)
        && Math.Abs(finding.Position - truth.PositionNormalized) <= 0.012);
    if (truth is not null)
    {
        if (matched) truePositive++;
        else
        {
            falseNegative++;
            if (detected.Length > 0) falsePositive++;
        }
        if (truth.Interrupted) { interruptedTotal++; if (matched) interruptedDetected++; }
    }
    else if (detected.Length == 0) trueNegative++;
    else falsePositive++;
    diagnostics.TryGetValue(file, out var diagnostic);
    var failureStage = truth is null ? "none" : matched ? "detected" : InferFailureStage(truth, diagnostic, detected);
    ScannerLineDetector.Finding? nearestCandidate = null;
    if (truth is not null && candidates.TryGetValue(file, out var fileCandidates))
        nearestCandidate = fileCandidates.Where(candidate => candidate.IsHorizontal == truth.Orientation.Equals("horizontal", StringComparison.OrdinalIgnoreCase))
            .OrderBy(candidate => Math.Abs(candidate.Position - truth.PositionNormalized)).FirstOrDefault();
    else if (truth is null && candidates.TryGetValue(file, out fileCandidates))
        nearestCandidate = fileCandidates.OrderByDescending(candidate => candidate.Confidence).FirstOrDefault();
    details.Add(new ImageEvaluation(Path.GetFileName(file), truth is not null, truth?.Interrupted ?? false, matched, detected.Length,
        failureStage, nearestCandidate));
}

var total = Math.Max(1, files.Length);
var accuracy = (truePositive + trueNegative) * 100d / total;
var precision = truePositive * 100d / Math.Max(1, truePositive + falsePositive);
var recall = truePositive * 100d / Math.Max(1, truePositive + falseNegative);
var interruptedAccuracy = interruptedDetected * 100d / Math.Max(1, interruptedTotal);
var failureStages = details.Where(detail => detail.ExpectedLine && !detail.Matched)
    .GroupBy(detail => detail.FailureStage).OrderByDescending(group => group.Count())
    .ToDictionary(group => group.Key, group => group.Count());
var report = new AccuracyReport(1, DateTime.UtcNow, fixtureFolder, sensitivity, files.Length, truePositive, trueNegative,
    falsePositive, falseNegative, accuracy, precision, recall, interruptedTotal, interruptedDetected,
    interruptedAccuracy, DateTime.UtcNow - started, failureStages, details);
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(report, jsonOptions));
return 0;

static string InferFailureStage(GroundTruthLine truth, ScannerLineDetector.ImageDiagnostic? diagnostic, ScannerLineDetector.Finding[] findings)
{
    if (diagnostic is null) return "missing_diagnostic";
    var direction = truth.Orientation.Equals("horizontal", StringComparison.OrdinalIgnoreCase) ? diagnostic.Horizontal : diagnostic.Vertical;
    if (!direction.FastPassPassed) return "fast_first_pass";
    if (direction.ProfileCandidateGroups == 0) return "profile_threshold";
    if (direction.CoveragePassed == 0) return "band_coverage";
    if (direction.StructuralPassed == 0) return "structural_validation";
    if (direction.FinalAccepted == 0) return "final_confidence";
    return findings.Length > 0 ? "position_or_orientation_mismatch" : "unknown";
}

record GroundTruthManifest(List<GroundTruthLine> Lines);
record GroundTruthLine(string File, string Orientation, double PositionNormalized, bool Interrupted);
record ImageEvaluation(string File, bool ExpectedLine, bool Interrupted, bool Matched, int FindingCount, string FailureStage,
    ScannerLineDetector.Finding? NearestCandidate);
record AccuracyReport(int SchemaVersion, DateTime MeasuredAtUtc, string FixtureFolder, double Sensitivity, int Images,
    int TruePositive, int TrueNegative, int FalsePositive, int FalseNegative, double AccuracyPercent, double PrecisionPercent,
    double RecallPercent, int InterruptedTotal, int InterruptedDetected, double InterruptedAccuracyPercent, TimeSpan Elapsed,
    Dictionary<string, int> FailureStages, List<ImageEvaluation> ImagesEvaluated);
