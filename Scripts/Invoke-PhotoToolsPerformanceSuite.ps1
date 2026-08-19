[CmdletBinding()]
param(
    [ValidateSet('Small', 'Large', 'Mixed', 'All')]
    [string]$Case = 'All',

    [ValidateRange(1, 50)]
    [int]$Iterations = 7,

    [ValidateRange(1, 1000)]
    [int]$ThumbnailSampleSize = 50,

    [int]$Seed = 20260818,

    [ValidateRange(0, 1000)]
    [int]$ScannerLineImagesPerCase = 40,

    [ValidateRange(0, 1000)]
    [int]$InterruptedScannerLineImagesPerCase = 12,

    [string]$OutputPath,

    [string]$TextOutputPath,

    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$CompareWith,

    [switch]$Regenerate
)

$ErrorActionPreference = 'Stop'
$suiteVersion = 5
$fixtureRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\performance-fixtures'))
$resultRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\performance-results'))
$measureScript = Join-Path $PSScriptRoot 'Measure-PhotoToolsPerformance.ps1'
$selectedCases = if ($Case -eq 'All') { @('Small', 'Large', 'Mixed') } else { @($Case) }

$caseDefinitions = [ordered]@{
    Small = [ordered]@{ Images = 100; OtherFiles = 10; Folders = 5; TemplateSize = 384 }
    Large = [ordered]@{ Images = 2000; OtherFiles = 20; Folders = 20; TemplateSize = 512 }
    Mixed = [ordered]@{ Images = 750; OtherFiles = 750; Folders = 75; TemplateSize = 768 }
}

function New-DeterministicTemplates {
    param([string]$Directory, [int]$Size, [int]$RandomSeed, [string]$CaseName, [int]$Count)
    Add-Type -AssemblyName PresentationCore
    Add-Type -AssemblyName WindowsBase
    if (-not ('PhotoToolsFixturePixels' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
public static class PhotoToolsFixturePixels
{
    public static byte[] Create(int size, int seed)
    {
        var random = new Random(seed);
        var pixels = new byte[size * size * 3];
        var corners = new int[4, 3];
        for (var corner = 0; corner < 4; corner++)
            for (var channel = 0; channel < 3; channel++) corners[corner, channel] = random.Next(35, 221);
        const int blobCount = 7;
        var blobX = new double[blobCount]; var blobY = new double[blobCount]; var blobRadius = new double[blobCount];
        var blobColor = new int[blobCount, 3];
        for (var blob = 0; blob < blobCount; blob++)
        {
            blobX[blob] = random.NextDouble(); blobY[blob] = random.NextDouble(); blobRadius[blob] = 0.08 + random.NextDouble() * 0.30;
            for (var channel = 0; channel < 3; channel++) blobColor[blob, channel] = random.Next(-65, 66);
        }
        const int rectangleCount = 4;
        var rectX1 = new double[rectangleCount]; var rectX2 = new double[rectangleCount];
        var rectY1 = new double[rectangleCount]; var rectY2 = new double[rectangleCount];
        var rectColor = new int[rectangleCount, 3];
        for (var rect = 0; rect < rectangleCount; rect++)
        {
            rectX1[rect] = random.NextDouble() * 0.72; rectX2[rect] = Math.Min(1, rectX1[rect] + 0.08 + random.NextDouble() * 0.30);
            rectY1[rect] = random.NextDouble() * 0.72; rectY2[rect] = Math.Min(1, rectY1[rect] + 0.08 + random.NextDouble() * 0.30);
            for (var channel = 0; channel < 3; channel++) rectColor[rect, channel] = random.Next(-42, 43);
        }
        var hasInsetFrame = Math.Abs(seed % 5) == 0;
        var frameInset = 0.035 + random.NextDouble() * 0.07;
        var frameWidth = 0.004 + random.NextDouble() * 0.012;
        var frameDelta = random.Next(-60, -22);
        var waveX = 1 + random.Next(1, 6); var waveY = 1 + random.Next(1, 6); var phase = random.NextDouble() * Math.PI * 2;
        for (var y = 0; y < size; y++)
        {
            var fy = y / (double)Math.Max(1, size - 1);
            for (var x = 0; x < size; x++)
            {
                var fx = x / (double)Math.Max(1, size - 1);
                for (var channel = 0; channel < 3; channel++)
                {
                    var top = corners[0, channel] * (1 - fx) + corners[1, channel] * fx;
                    var bottom = corners[2, channel] * (1 - fx) + corners[3, channel] * fx;
                    var value = top * (1 - fy) + bottom * fy;
                    for (var blob = 0; blob < blobCount; blob++)
                    {
                        var dx = fx - blobX[blob]; var dy = fy - blobY[blob];
                        var influence = Math.Max(0, 1 - Math.Sqrt(dx * dx + dy * dy) / blobRadius[blob]);
                        value += blobColor[blob, channel] * influence * influence;
                    }
                    for (var rect = 0; rect < rectangleCount; rect++)
                        if (fx >= rectX1[rect] && fx <= rectX2[rect] && fy >= rectY1[rect] && fy <= rectY2[rect]) value += rectColor[rect, channel];
                    if (hasInsetFrame)
                    {
                        var onVertical = (Math.Abs(fx - frameInset) <= frameWidth || Math.Abs(fx - (1 - frameInset)) <= frameWidth) && fy >= frameInset && fy <= 1 - frameInset;
                        var onHorizontal = (Math.Abs(fy - frameInset) <= frameWidth || Math.Abs(fy - (1 - frameInset)) <= frameWidth) && fx >= frameInset && fx <= 1 - frameInset;
                        if (onVertical || onHorizontal) value += frameDelta;
                    }
                    value += Math.Sin((fx * waveX + fy * waveY) * Math.PI * 2 + phase + channel * 0.7) * 5;
                    var hash = unchecked(seed * 397 ^ x * 73856093 ^ y * 19349663 ^ channel * 83492791);
                    value += ((hash & 15) - 7.5) * 0.45;
                    pixels[(y * size + x) * 3 + channel] = (byte)Math.Max(0, Math.Min(255, Math.Round(value)));
                }
            }
        }
        return pixels;
    }
}
'@ -ReferencedAssemblies System.dll
    }
    for ($templateIndex = 0; $templateIndex -lt $Count; $templateIndex++) {
        $pixels = [PhotoToolsFixturePixels]::Create($Size, $RandomSeed + $templateIndex * 7919)
        $bitmap = [Windows.Media.Imaging.BitmapSource]::Create(
            $Size, $Size, 96, 96,
            [Windows.Media.PixelFormats]::Rgb24,
            $null, $pixels, $Size * 3)

        $extension = if ($CaseName -eq 'Mixed' -and $templateIndex % 3 -eq 0) { '.png' } else { '.jpg' }
        $templatePath = Join-Path $Directory ('template-{0:D5}{1}' -f $templateIndex, $extension)
        $outputStream = [IO.File]::Open($templatePath, [IO.FileMode]::CreateNew)
        try {
            if ($extension -eq '.png') {
            $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
            }
            else {
                $encoder = [Windows.Media.Imaging.JpegBitmapEncoder]::new()
                $encoder.QualityLevel = 90
            }
            $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
            $encoder.Save($outputStream)
        }
        finally { $outputStream.Dispose() }
    }
}

function Add-DeterministicScannerLines {
    param([string]$Directory, [int]$ImageCount, [int]$InterruptedCount, [int]$RandomSeed)
    Add-Type -AssemblyName PresentationCore
    Add-Type -AssemblyName WindowsBase
    $random = [Random]::new($RandomSeed)
    $contrastLevels = @(-48, -32, -24, -16, -12, -8, -6, 6, 8, 12, 16, 24, 32, 48)
    $groundTruth = [Collections.Generic.List[object]]::new()
    $images = @(Get-ChildItem -LiteralPath $Directory -File | Where-Object { $_.Extension -in @('.jpg', '.jpeg', '.png') } | Sort-Object Name | Select-Object -First $ImageCount)
    for ($index = 0; $index -lt $images.Count; $index++) {
        $image = $images[$index]
        $stream = [IO.File]::Open($image.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try {
            $decoder = [Windows.Media.Imaging.BitmapDecoder]::Create($stream, [Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat, [Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
            $frame = $decoder.Frames[0]
            $converted = [Windows.Media.Imaging.FormatConvertedBitmap]::new($frame, [Windows.Media.PixelFormats]::Bgra32, $null, 0)
            $width = $converted.PixelWidth
            $height = $converted.PixelHeight
            $stride = $width * 4
            $pixels = [byte[]]::new($stride * $height)
            $converted.CopyPixels($pixels, $stride, 0)
        }
        finally { $stream.Dispose() }

        $orientation = if ($index % 2 -eq 0) { 'vertical' } else { 'horizontal' }
        $axisLength = if ($orientation -eq 'vertical') { $width } else { $height }
        $position = $random.Next([Math]::Max(1, [int]($axisLength * 0.10)), [Math]::Max(2, [int]($axisLength * 0.90)))
        $lineWidth = $random.Next(1, 5)
        $contrastDelta = $contrastLevels[$random.Next($contrastLevels.Count)]
        $isInterrupted = $index -lt $InterruptedCount
        $lineLength = if ($orientation -eq 'vertical') { $height } else { $width }
        $gapMask = [bool[]]::new($lineLength)
        $gaps = [Collections.Generic.List[object]]::new()
        if ($isInterrupted) {
            $gapCount = $random.Next(1, 4)
            for ($gapIndex = 0; $gapIndex -lt $gapCount; $gapIndex++) {
                $gapLength = $random.Next([Math]::Max(2, [int]($lineLength * 0.025)), [Math]::Max(3, [int]($lineLength * 0.075)))
                $gapStart = $random.Next([Math]::Max(1, [int]($lineLength * 0.12)), [Math]::Max(2, [int]($lineLength * 0.88) - $gapLength))
                $gapEnd = [Math]::Min($lineLength, $gapStart + $gapLength)
                for ($gapPixel = $gapStart; $gapPixel -lt $gapEnd; $gapPixel++) { $gapMask[$gapPixel] = $true }
                $gaps.Add([ordered]@{ start_pixels = $gapStart; length_pixels = $gapEnd - $gapStart; start_normalized = [Math]::Round($gapStart / [double][Math]::Max(1, $lineLength - 1), 8); length_normalized = [Math]::Round(($gapEnd - $gapStart) / [double]$lineLength, 8) })
            }
        }
        if ($orientation -eq 'vertical') {
            for ($x = $position; $x -lt [Math]::Min($width, $position + $lineWidth); $x++) {
                for ($y = 0; $y -lt $height; $y++) {
                    if ($gapMask[$y]) { continue }
                    $offset = $y * $stride + $x * 4
                    for ($channel = 0; $channel -lt 3; $channel++) { $pixels[$offset + $channel] = [byte][Math]::Max(0, [Math]::Min(255, [int]$pixels[$offset + $channel] + $contrastDelta)) }
                    $pixels[$offset + 3] = 255
                }
            }
        }
        else {
            for ($y = $position; $y -lt [Math]::Min($height, $position + $lineWidth); $y++) {
                for ($x = 0; $x -lt $width; $x++) {
                    if ($gapMask[$x]) { continue }
                    $offset = $y * $stride + $x * 4
                    for ($channel = 0; $channel -lt 3; $channel++) { $pixels[$offset + $channel] = [byte][Math]::Max(0, [Math]::Min(255, [int]$pixels[$offset + $channel] + $contrastDelta)) }
                    $pixels[$offset + 3] = 255
                }
            }
        }

        $bitmap = [Windows.Media.Imaging.BitmapSource]::Create($width, $height, 96, 96, [Windows.Media.PixelFormats]::Bgra32, $null, $pixels, $stride)
        $temporary = $image.FullName + '.line-fixture.tmp'
        $output = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew)
        try {
            $encoder = if ($image.Extension -ieq '.png') { [Windows.Media.Imaging.PngBitmapEncoder]::new() } else { $jpeg = [Windows.Media.Imaging.JpegBitmapEncoder]::new(); $jpeg.QualityLevel = 90; $jpeg }
            $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
            $encoder.Save($output)
        }
        finally { $output.Dispose() }
        [IO.File]::Copy($temporary, $image.FullName, $true)
        [IO.File]::Delete($temporary)
        $groundTruth.Add([ordered]@{
            file = $image.Name
            orientation = $orientation
            position_pixels = $position
            position_normalized = [Math]::Round(($position + (($lineWidth - 1) / 2.0)) / [Math]::Max(1, $axisLength - 1), 8)
            width_pixels = $lineWidth
            contrast_delta = $contrastDelta
            contrast_magnitude = [Math]::Abs($contrastDelta)
            interrupted = $isInterrupted
            gaps = $gaps.ToArray()
            image_width = $width
            image_height = $height
        })
    }
    return $groundTruth.ToArray()
}

function Test-FixtureManifest {
    param([string]$Directory, [string]$CaseName, $Definition)
    $manifestPath = Join-Path $Directory 'fixture.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { return $false }
    try { $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json }
    catch { return $false }
    return $manifest.suite_version -eq $suiteVersion -and
        $manifest.case -eq $CaseName -and
        $manifest.seed -eq $Seed -and
        $manifest.images -eq $Definition.Images -and
        $manifest.other_files -eq $Definition.OtherFiles -and
        $manifest.folders -eq $Definition.Folders -and
        $manifest.template_size -eq $Definition.TemplateSize -and
        $manifest.scanner_line_images -eq [Math]::Min($ScannerLineImagesPerCase, $Definition.Images) -and
        $manifest.interrupted_scanner_line_images -eq [Math]::Min($InterruptedScannerLineImagesPerCase, [Math]::Min($ScannerLineImagesPerCase, $Definition.Images))
}

function New-FixtureCase {
    param([string]$CaseName, $Definition)
    $caseDirectory = Join-Path $fixtureRoot $CaseName.ToLowerInvariant()
    if (-not $Regenerate -and (Test-FixtureManifest $caseDirectory $CaseName $Definition)) {
        Write-Host "Reusing deterministic $CaseName fixture."
        return $caseDirectory
    }

    if (Test-Path -LiteralPath $caseDirectory) {
        $resolvedCase = [IO.Path]::GetFullPath($caseDirectory)
        if (-not $resolvedCase.StartsWith($fixtureRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to replace a fixture outside $fixtureRoot"
        }
        Remove-Item -LiteralPath $resolvedCase -Recurse -Force
    }
    $null = New-Item -ItemType Directory -Path $caseDirectory
    $templateDirectory = Join-Path $caseDirectory '.templates'
    $null = New-Item -ItemType Directory -Path $templateDirectory
    $caseSeedOffset = switch ($CaseName) { 'Small' { 101 }; 'Large' { 202 }; 'Mixed' { 303 } }
    New-DeterministicTemplates $templateDirectory $Definition.TemplateSize ($Seed + $caseSeedOffset) $CaseName $Definition.Images

    for ($index = 0; $index -lt $Definition.Images; $index++) {
        $extension = if ($CaseName -eq 'Mixed' -and $index % 3 -eq 0) { '.png' } else { '.jpg' }
        $template = Join-Path $templateDirectory ('template-{0:D5}{1}' -f $index, $extension)
        $destination = Join-Path $caseDirectory ('photo-{0:D5}{1}' -f ($index + 1), $extension)
        [IO.File]::Copy($template, $destination, $false)
        [IO.File]::SetLastWriteTimeUtc($destination, [DateTime]::new(2020, 1, 1).AddSeconds($index))
    }
    $backgroundGroundTruth = for ($index = 0; $index -lt $Definition.Images; $index++) {
        $extension = if ($CaseName -eq 'Mixed' -and $index % 3 -eq 0) { '.png' } else { '.jpg' }
        $backgroundSeed = $Seed + $caseSeedOffset + $index * 7919
        [ordered]@{
            file = 'photo-{0:D5}{1}' -f ($index + 1), $extension
            background_seed = $backgroundSeed
            unique_background = $true
            rectangle_count = 4
            has_inset_frame = [Math]::Abs($backgroundSeed % 5) -eq 0
        }
    }
    [ordered]@{ schema_version = 1; suite_version = $suiteVersion; case = $CaseName; images = $backgroundGroundTruth } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $caseDirectory 'scanner-background-ground-truth.json') -Encoding UTF8
    $lineCount = [Math]::Min($ScannerLineImagesPerCase, $Definition.Images)
    $interruptedLineCount = [Math]::Min($InterruptedScannerLineImagesPerCase, $lineCount)
    $lineGroundTruth = Add-DeterministicScannerLines $caseDirectory $lineCount $interruptedLineCount ($Seed + $caseSeedOffset + 5000)
    $generatedImages = @(Get-ChildItem -LiteralPath $caseDirectory -File | Where-Object { $_.Extension -in @('.jpg', '.jpeg', '.png') } | Sort-Object Name)
    for ($index = 0; $index -lt $generatedImages.Count; $index++) {
        [IO.File]::SetLastWriteTimeUtc($generatedImages[$index].FullName, [DateTime]::new(2020, 1, 1).AddSeconds($index))
    }
    $lineManifest = [ordered]@{
        schema_version = 1; suite_version = $suiteVersion; case = $CaseName; seed = $Seed
        line_image_count = $lineGroundTruth.Count; interrupted_line_image_count = $interruptedLineCount
        clean_image_count = $Definition.Images - $lineGroundTruth.Count
        lines = $lineGroundTruth
    }
    $lineManifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $caseDirectory 'scanner-line-ground-truth.json') -Encoding UTF8
    for ($index = 0; $index -lt $Definition.OtherFiles; $index++) {
        [IO.File]::WriteAllText((Join-Path $caseDirectory ('notes-{0:D5}.txt' -f ($index + 1))), "Deterministic test file $index`n")
    }
    for ($index = 0; $index -lt $Definition.Folders; $index++) {
        $null = New-Item -ItemType Directory -Path (Join-Path $caseDirectory ('folder-{0:D4}' -f ($index + 1)))
    }
    Remove-Item -LiteralPath $templateDirectory -Recurse -Force

    $manifest = [ordered]@{
        suite_version = $suiteVersion; case = $CaseName; seed = $Seed
        images = $Definition.Images; other_files = $Definition.OtherFiles
        folders = $Definition.Folders; template_size = $Definition.TemplateSize
        scanner_line_images = $lineGroundTruth.Count; interrupted_scanner_line_images = $interruptedLineCount
        scanner_line_ground_truth = 'scanner-line-ground-truth.json'
        scanner_background_ground_truth = 'scanner-background-ground-truth.json'
    }
    $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $caseDirectory 'fixture.json') -Encoding UTF8
    Write-Host "Generated deterministic $CaseName fixture at $caseDirectory"
    return $caseDirectory
}

if (-not (Test-Path -LiteralPath $fixtureRoot)) { $null = New-Item -ItemType Directory -Path $fixtureRoot }
if (-not (Test-Path -LiteralPath $resultRoot)) { $null = New-Item -ItemType Directory -Path $resultRoot }
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $resultRoot ('suite-{0}.json' -f [DateTime]::Now.ToString('yyyyMMdd-HHmmss'))
}
$resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
$runDirectory = Join-Path $resultRoot ('.run-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $runDirectory

try {
    $caseResults = [ordered]@{}
    foreach ($caseName in $selectedCases) {
        $definition = $caseDefinitions[$caseName]
        $fixture = New-FixtureCase $caseName $definition
        $caseOutput = Join-Path $runDirectory ($caseName.ToLowerInvariant() + '.json')
        $caseTextOutput = Join-Path $runDirectory ($caseName.ToLowerInvariant() + '.txt')
        & $measureScript $fixture -Iterations $Iterations -ThumbnailSampleSize $ThumbnailSampleSize -OutputPath $caseOutput -TextOutputPath $caseTextOutput
        $caseResults[$caseName] = Get-Content -LiteralPath $caseOutput -Raw | ConvertFrom-Json
    }

    $suiteResult = [ordered]@{
        schema_version = 1
        suite_version = $suiteVersion
        measured_at_utc = [DateTime]::UtcNow.ToString('o')
        seed = $Seed
        iterations = $Iterations
        thumbnail_sample_size = $ThumbnailSampleSize
        cases = $caseResults
    }

    if ($CompareWith) {
        $baseline = Get-Content -LiteralPath $CompareWith -Raw | ConvertFrom-Json
        $comparison = [ordered]@{}
        foreach ($caseName in $selectedCases) {
            $caseComparison = [ordered]@{}
            foreach ($metricName in @('first_25_items', 'full_folder_enumeration', 'thumbnail_decode_320px')) {
                $current = [double]$caseResults[$caseName].metrics.$metricName.median_ms
                $previous = [double]$baseline.cases.$caseName.metrics.$metricName.median_ms
                $change = if ($previous -gt 0) { (($current - $previous) / $previous) * 100 } else { 0 }
                $caseComparison[$metricName] = [Math]::Round($change, 1)
            }
            $comparison[$caseName] = $caseComparison
        }
        $suiteResult.comparison_percent = $comparison
    }

    $outputDirectory = [IO.Path]::GetDirectoryName($resolvedOutput)
    if (-not [IO.Directory]::Exists($outputDirectory)) { $null = [IO.Directory]::CreateDirectory($outputDirectory) }
    $suiteResult | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $resolvedOutput -Encoding UTF8
    if ([string]::IsNullOrWhiteSpace($TextOutputPath)) {
        $textDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\test results'))
        $TextOutputPath = Join-Path $textDirectory ('suite-{0}.txt' -f [DateTime]::Now.ToString('yyyyMMdd-HHmmss'))
    }
    $resolvedTextOutput = [IO.Path]::GetFullPath($TextOutputPath)
    $textOutputDirectory = [IO.Path]::GetDirectoryName($resolvedTextOutput)
    if (-not [IO.Directory]::Exists($textOutputDirectory)) { $null = [IO.Directory]::CreateDirectory($textOutputDirectory) }
    $summaryLines = [Collections.Generic.List[string]]::new()
    $summaryLines.Add('Photo Tools Generated Performance Suite')
    $summaryLines.Add(('Measured: {0}' -f $suiteResult.measured_at_utc))
    $summaryLines.Add(('Seed: {0} | Iterations: {1} | Thumbnail sample: {2}' -f $Seed, $Iterations, $ThumbnailSampleSize))
    foreach ($caseName in $selectedCases) {
        $caseResult = $caseResults[$caseName]
        $summaryLines.Add('')
        $summaryLines.Add(('[{0}] {1:N0} items, {2:N0} images' -f $caseName, $caseResult.counts.folder_items, $caseResult.counts.images))
        foreach ($metricName in @('first_25_items', 'full_folder_enumeration', 'thumbnail_decode_320px')) {
            $metric = $caseResult.metrics.$metricName
            $line = '  {0,-30} median {1,9:N2} ms | p95 {2,9:N2} ms' -f $metric.name, $metric.median_ms, $metric.p95_ms
            if ($suiteResult.comparison_percent) {
                $change = $suiteResult.comparison_percent[$caseName][$metricName]
                $line += ' | change {0,7:N1}%' -f $change
            }
            $summaryLines.Add($line)
        }
    }
    if ($suiteResult.comparison_percent) {
        $summaryLines.Add('')
        $summaryLines.Add('Negative change percentages are improvements; positive percentages are regressions.')
    }
    $summaryLines | Set-Content -LiteralPath $resolvedTextOutput -Encoding UTF8
    Write-Host ""
    Write-Host "Suite result: $resolvedOutput"
    Write-Host "Text summary: $resolvedTextOutput"
    if ($suiteResult.comparison_percent) { Write-Host 'Negative comparison percentages are improvements; positive percentages are regressions.' }
}
finally {
    if (Test-Path -LiteralPath $runDirectory) { Remove-Item -LiteralPath $runDirectory -Recurse -Force }
}
