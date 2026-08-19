[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Container })]
    [string]$PhotoFolder,

    [ValidateRange(1, 50)]
    [int]$Iterations = 5,

    [ValidateRange(1, 1000)]
    [int]$ThumbnailSampleSize = 50,

    [string]$OutputPath,

    [string]$TextOutputPath,

    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$CompareWith
)

$ErrorActionPreference = 'Stop'
$resolvedPhotoFolder = (Resolve-Path -LiteralPath $PhotoFolder).Path
$imageExtensions = @('.jpg', '.jpeg', '.png', '.bmp', '.gif', '.tif', '.tiff')

function Get-Percentile {
    param([double[]]$Values, [double]$Percentile)
    if ($Values.Count -eq 0) { return 0 }
    $sorted = @($Values | Sort-Object)
    $position = ($Percentile / 100) * ($sorted.Count - 1)
    $lower = [Math]::Floor($position)
    $upper = [Math]::Ceiling($position)
    $weight = $position - $lower
    $value = $sorted[$lower] + (($sorted[$upper] - $sorted[$lower]) * $weight)
    return [Math]::Round($value, 2)
}

function New-Metric {
    param([string]$Name, [double[]]$Values)
    [ordered]@{
        name = $Name
        samples_ms = @($Values | ForEach-Object { [Math]::Round($_, 2) })
        median_ms = Get-Percentile $Values 50
        p95_ms = Get-Percentile $Values 95
        minimum_ms = if ($Values.Count) { [Math]::Round(($Values | Measure-Object -Minimum).Minimum, 2) } else { 0 }
    }
}

function Test-IsImage {
    param([string]$Path)
    return $imageExtensions -contains [IO.Path]::GetExtension($Path).ToLowerInvariant()
}

function Measure-Enumeration {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $folders = @(Get-ChildItem -LiteralPath $resolvedPhotoFolder -Directory -Force)
    $images = @(Get-ChildItem -LiteralPath $resolvedPhotoFolder -File -Force | Where-Object { Test-IsImage $_.FullName })
    $items = @($folders) + @($images)
    $ordered = @($items | Sort-Object @{ Expression = { -not $_.PSIsContainer } }, Name)
    $watch.Stop()
    return [pscustomobject]@{ Milliseconds = $watch.Elapsed.TotalMilliseconds; Count = $ordered.Count; Images = $images }
}

function Measure-FirstBatch {
    param([int]$TargetCount = 25)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $count = 0
    foreach ($path in [IO.Directory]::EnumerateDirectories($resolvedPhotoFolder)) {
        $null = [IO.Path]::GetFileName($path)
        if ((++$count) -ge $TargetCount) { break }
    }
    if ($count -lt $TargetCount) {
        foreach ($path in [IO.Directory]::EnumerateFiles($resolvedPhotoFolder)) {
            if (-not (Test-IsImage $path)) { continue }
            $info = [IO.FileInfo]::new($path)
            $null = $info.Length
            if ((++$count) -ge $TargetCount) { break }
        }
    }
    $watch.Stop()
    return [pscustomobject]@{ Milliseconds = $watch.Elapsed.TotalMilliseconds; Count = $count }
}

function Measure-ThumbnailDecode {
    param([IO.FileInfo[]]$Images)
    if ($Images.Count -eq 0) { return [pscustomobject]@{ Milliseconds = 0; Count = 0; Failed = 0 } }
    Add-Type -AssemblyName PresentationCore
    Add-Type -AssemblyName WindowsBase
    $sample = @($Images | Select-Object -First $ThumbnailSampleSize)
    $failed = 0
    $watch = [Diagnostics.Stopwatch]::StartNew()
    foreach ($image in $sample) {
        try {
            $stream = [IO.File]::Open($image.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
            try {
                $bitmap = [Windows.Media.Imaging.BitmapImage]::new()
                $bitmap.BeginInit()
                $bitmap.CacheOption = [Windows.Media.Imaging.BitmapCacheOption]::OnLoad
                $bitmap.DecodePixelWidth = 320
                $bitmap.StreamSource = $stream
                $bitmap.EndInit()
                $bitmap.Freeze()
            }
            finally { $stream.Dispose() }
        }
        catch { $failed++ }
    }
    $watch.Stop()
    return [pscustomobject]@{ Milliseconds = $watch.Elapsed.TotalMilliseconds; Count = $sample.Count; Failed = $failed }
}

# One warm-up prevents one-time PowerShell/JIT startup from dominating the measurements.
$warmup = Measure-Enumeration
$enumerationTimes = [Collections.Generic.List[double]]::new()
$firstBatchTimes = [Collections.Generic.List[double]]::new()
$thumbnailTimes = [Collections.Generic.List[double]]::new()
$itemCount = 0
$imageCount = 0
$thumbnailCount = 0
$thumbnailFailures = 0

for ($iteration = 1; $iteration -le $Iterations; $iteration++) {
    $firstBatch = Measure-FirstBatch
    $enumeration = Measure-Enumeration
    $thumbnail = Measure-ThumbnailDecode -Images $enumeration.Images
    $firstBatchTimes.Add($firstBatch.Milliseconds)
    $enumerationTimes.Add($enumeration.Milliseconds)
    $thumbnailTimes.Add($thumbnail.Milliseconds)
    $itemCount = $enumeration.Count
    $imageCount = $enumeration.Images.Count
    $thumbnailCount = $thumbnail.Count
    $thumbnailFailures = $thumbnail.Failed
    Write-Host ("Iteration {0}/{1}: first items {2:N2} ms, enumeration {3:N2} ms, thumbnails {4:N2} ms" -f $iteration, $Iterations, $firstBatch.Milliseconds, $enumeration.Milliseconds, $thumbnail.Milliseconds)
}

$result = [ordered]@{
    schema_version = 1
    measured_at_utc = [DateTime]::UtcNow.ToString('o')
    machine = $env:COMPUTERNAME
    powershell = $PSVersionTable.PSVersion.ToString()
    photo_folder = $resolvedPhotoFolder
    iterations = $Iterations
    counts = [ordered]@{
        folder_items = $itemCount
        images = $imageCount
        thumbnails_per_iteration = $thumbnailCount
        thumbnail_failures = $thumbnailFailures
    }
    metrics = [ordered]@{
        first_25_items = New-Metric 'First 25 items' $firstBatchTimes.ToArray()
        full_folder_enumeration = New-Metric 'Full folder enumeration' $enumerationTimes.ToArray()
        thumbnail_decode_320px = New-Metric '320px thumbnail decode sample' $thumbnailTimes.ToArray()
    }
}

if ($CompareWith) {
    $baseline = Get-Content -LiteralPath $CompareWith -Raw | ConvertFrom-Json
    $comparison = [ordered]@{}
    foreach ($metricName in $result.metrics.Keys) {
        $current = [double]$result.metrics[$metricName].median_ms
        $previous = [double]$baseline.metrics.$metricName.median_ms
        $change = if ($previous -gt 0) { (($current - $previous) / $previous) * 100 } else { 0 }
        $comparison[$metricName] = [ordered]@{
            baseline_median_ms = [Math]::Round($previous, 2)
            current_median_ms = [Math]::Round($current, 2)
            change_percent = [Math]::Round($change, 1)
        }
    }
    $result.comparison = $comparison
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $resultDirectory = Join-Path $PSScriptRoot '..\performance-results'
    $fileName = 'performance-{0}.json' -f [DateTime]::Now.ToString('yyyyMMdd-HHmmss')
    $OutputPath = Join-Path $resultDirectory $fileName
}

$resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = [IO.Path]::GetDirectoryName($resolvedOutput)
if (-not [IO.Directory]::Exists($outputDirectory)) { $null = [IO.Directory]::CreateDirectory($outputDirectory) }
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $resolvedOutput -Encoding UTF8

if ([string]::IsNullOrWhiteSpace($TextOutputPath)) {
    $textDirectory = Join-Path $PSScriptRoot '..\test results'
    $TextOutputPath = Join-Path $textDirectory ('performance-{0}.txt' -f [DateTime]::Now.ToString('yyyyMMdd-HHmmss'))
}
$resolvedTextOutput = [IO.Path]::GetFullPath($TextOutputPath)
$textOutputDirectory = [IO.Path]::GetDirectoryName($resolvedTextOutput)
if (-not [IO.Directory]::Exists($textOutputDirectory)) { $null = [IO.Directory]::CreateDirectory($textOutputDirectory) }
$summaryLines = [Collections.Generic.List[string]]::new()
$summaryLines.Add('Photo Tools Performance Result')
$summaryLines.Add(('Measured: {0}' -f $result.measured_at_utc))
$summaryLines.Add(('Folder: {0}' -f $result.photo_folder))
$summaryLines.Add(('Iterations: {0}' -f $result.iterations))
$summaryLines.Add(('Items: {0:N0} | Images: {1:N0} | Thumbnail sample: {2:N0}' -f $result.counts.folder_items, $result.counts.images, $result.counts.thumbnails_per_iteration))
$summaryLines.Add('')
$summaryLines.Add('Metric                          Median ms      P95 ms      Minimum ms')
$summaryLines.Add('--------------------------------------------------------------------')
foreach ($metricName in @('first_25_items', 'full_folder_enumeration', 'thumbnail_decode_320px')) {
    $metric = $result.metrics[$metricName]
    $summaryLines.Add(('{0,-30} {1,10:N2} {2,11:N2} {3,15:N2}' -f $metric.name, $metric.median_ms, $metric.p95_ms, $metric.minimum_ms))
}
if ($result.comparison) {
    $summaryLines.Add('')
    $summaryLines.Add('Comparison with baseline (negative is faster)')
    $summaryLines.Add('------------------------------------------------')
    foreach ($metricName in @('first_25_items', 'full_folder_enumeration', 'thumbnail_decode_320px')) {
        $metric = $result.metrics[$metricName]
        $change = $result.comparison[$metricName].change_percent
        $summaryLines.Add(('{0,-30} {1,8:N1}%' -f $metric.name, $change))
    }
}
$summaryLines | Set-Content -LiteralPath $resolvedTextOutput -Encoding UTF8

Write-Host ""
Write-Host "Performance result: $resolvedOutput"
Write-Host "Text summary:       $resolvedTextOutput"
Write-Host ("Median folder enumeration: {0:N2} ms" -f $result.metrics.full_folder_enumeration.median_ms)
Write-Host ("Median thumbnail sample:   {0:N2} ms" -f $result.metrics.thumbnail_decode_320px.median_ms)
if ($result.comparison) {
    Write-Host "Negative comparison percentages are improvements; positive percentages are regressions."
}
