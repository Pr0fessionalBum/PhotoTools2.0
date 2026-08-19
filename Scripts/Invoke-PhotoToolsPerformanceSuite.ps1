[CmdletBinding()]
param(
    [ValidateSet('Small', 'Large', 'Mixed', 'All')]
    [string]$Case = 'All',

    [ValidateRange(1, 50)]
    [int]$Iterations = 7,

    [ValidateRange(1, 1000)]
    [int]$ThumbnailSampleSize = 50,

    [int]$Seed = 20260818,

    [string]$OutputPath,

    [string]$TextOutputPath,

    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$CompareWith,

    [switch]$Regenerate
)

$ErrorActionPreference = 'Stop'
$suiteVersion = 1
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
    param([string]$Directory, [int]$Size, [int]$RandomSeed)
    Add-Type -AssemblyName PresentationCore
    Add-Type -AssemblyName WindowsBase
    $random = [Random]::new($RandomSeed)
    foreach ($templateIndex in 0..7) {
        $pixels = [byte[]]::new($Size * $Size * 3)
        $random.NextBytes($pixels)
        $bitmap = [Windows.Media.Imaging.BitmapSource]::Create(
            $Size, $Size, 96, 96,
            [Windows.Media.PixelFormats]::Rgb24,
            $null, $pixels, $Size * 3)

        $pngPath = Join-Path $Directory ('template-{0:D2}.png' -f $templateIndex)
        $pngStream = [IO.File]::Open($pngPath, [IO.FileMode]::CreateNew)
        try {
            $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
            $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
            $encoder.Save($pngStream)
        }
        finally { $pngStream.Dispose() }

        $jpgPath = Join-Path $Directory ('template-{0:D2}.jpg' -f $templateIndex)
        $jpgStream = [IO.File]::Open($jpgPath, [IO.FileMode]::CreateNew)
        try {
            $encoder = [Windows.Media.Imaging.JpegBitmapEncoder]::new()
            $encoder.QualityLevel = 90
            $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
            $encoder.Save($jpgStream)
        }
        finally { $jpgStream.Dispose() }
    }
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
        $manifest.template_size -eq $Definition.TemplateSize
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
    New-DeterministicTemplates $templateDirectory $Definition.TemplateSize ($Seed + $caseSeedOffset)

    for ($index = 0; $index -lt $Definition.Images; $index++) {
        $extension = if ($CaseName -eq 'Mixed' -and $index % 3 -eq 0) { '.png' } else { '.jpg' }
        $template = Join-Path $templateDirectory ('template-{0:D2}{1}' -f ($index % 8), $extension)
        $destination = Join-Path $caseDirectory ('photo-{0:D5}{1}' -f ($index + 1), $extension)
        [IO.File]::Copy($template, $destination, $false)
        [IO.File]::SetLastWriteTimeUtc($destination, [DateTime]::new(2020, 1, 1).AddSeconds($index))
    }
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
