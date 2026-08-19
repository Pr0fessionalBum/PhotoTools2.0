[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Container })]
    [string]$FixtureFolder,

    [ValidateRange(0, 1)]
    [double]$Sensitivity = 0.55,

    [ValidateRange(0, 100)]
    [double]$TargetAccuracy = 90,

    [string]$OutputPath,

    [string]$TextOutputPath
)

$ErrorActionPreference = 'Stop'
$fixture = (Resolve-Path -LiteralPath $FixtureFolder).Path
$groundTruth = Join-Path $fixture 'scanner-line-ground-truth.json'
if (-not (Test-Path -LiteralPath $groundTruth -PathType Leaf)) { throw "No scanner-line-ground-truth.json was found in $fixture" }
$resultDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\performance-results'))
$textDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\test results'))
$timestamp = [DateTime]::Now.ToString('yyyyMMdd-HHmmss')
if ([string]::IsNullOrWhiteSpace($OutputPath)) { $OutputPath = Join-Path $resultDirectory "scanner-accuracy-$timestamp.json" }
if ([string]::IsNullOrWhiteSpace($TextOutputPath)) { $TextOutputPath = Join-Path $textDirectory "scanner-accuracy-$timestamp.txt" }
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
$TextOutputPath = [IO.Path]::GetFullPath($TextOutputPath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($OutputPath)) | Out-Null
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($TextOutputPath)) | Out-Null
$runnerProject = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Tools\ScannerLineAccuracyRunner\ScannerLineAccuracyRunner.csproj'))

Write-Host 'Scanner-line accuracy test' -ForegroundColor Cyan
Write-Host "Fixture: $fixture" -ForegroundColor DarkCyan
Write-Host ("Sensitivity: {0:N2}" -f $Sensitivity) -ForegroundColor DarkCyan
Write-Host 'Running the real detector...' -ForegroundColor Yellow
$assetsFile = Join-Path ([IO.Path]::GetDirectoryName($runnerProject)) 'obj\project.assets.json'
if (-not (Test-Path -LiteralPath $assetsFile)) {
    Write-Host 'Restoring the accuracy runner (first run only)...' -ForegroundColor Yellow
    & dotnet restore $runnerProject --nologo | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'The accuracy runner dependency restore failed.' }
}
& dotnet build $runnerProject --configuration Release --no-restore --nologo | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'The accuracy runner could not build. Run dotnet restore on its project once, then retry.' }
$runnerDll = Join-Path ([IO.Path]::GetDirectoryName($runnerProject)) 'bin\Release\net10.0-windows\ScannerLineAccuracyRunner.dll'
& dotnet $runnerDll $fixture $OutputPath $Sensitivity
if ($LASTEXITCODE -ne 0) { throw "Scanner-line accuracy runner failed with exit code $LASTEXITCODE." }
$result = Get-Content -LiteralPath $OutputPath -Raw | ConvertFrom-Json

function Write-Metric {
    param([string]$Label, [double]$Value, [double]$Target)
    $color = if ($Value -ge $Target) { 'Green' } elseif ($Value -ge $Target - 10) { 'Yellow' } else { 'Red' }
    Write-Host ('{0,-30} {1,7:N2}%' -f $Label, $Value) -ForegroundColor $color
}

Write-Host ''
Write-Host 'Accuracy results' -ForegroundColor Cyan
Write-Metric 'Overall accuracy' $result.accuracy_percent $TargetAccuracy
Write-Metric 'Precision' $result.precision_percent $TargetAccuracy
Write-Metric 'Recall' $result.recall_percent $TargetAccuracy
Write-Metric 'Interrupted-line accuracy' $result.interrupted_accuracy_percent $TargetAccuracy
Write-Host ('True positives:  {0,6:N0}' -f $result.true_positive) -ForegroundColor Green
Write-Host ('True negatives:  {0,6:N0}' -f $result.true_negative) -ForegroundColor Green
Write-Host ('False positives: {0,6:N0}' -f $result.false_positive) -ForegroundColor $(if ($result.false_positive -eq 0) { 'Green' } else { 'Red' })
Write-Host ('False negatives: {0,6:N0}' -f $result.false_negative) -ForegroundColor $(if ($result.false_negative -eq 0) { 'Green' } else { 'Red' })
Write-Host ''
Write-Host 'False negatives by pipeline stage' -ForegroundColor Cyan
$stageLines = [Collections.Generic.List[string]]::new()
foreach ($stage in $result.failure_stages.PSObject.Properties) {
    $label = $stage.Name.Replace('_', ' ')
    $line = '  {0,-32} {1,5:N0}' -f $label, $stage.Value
    $stageLines.Add($line)
    Write-Host $line -ForegroundColor Yellow
}

$summary = @(
    'Photo Tools Scanner-Line Accuracy Result'
    "Fixture: $fixture"
    ('Sensitivity: {0:N2}' -f $Sensitivity)
    ('Images: {0:N0}' -f $result.images)
    ''
    ('Overall accuracy:          {0:N2}%' -f $result.accuracy_percent)
    ('Precision:                 {0:N2}%' -f $result.precision_percent)
    ('Recall:                    {0:N2}%' -f $result.recall_percent)
    ('Interrupted-line accuracy: {0:N2}%' -f $result.interrupted_accuracy_percent)
    ''
    ('True positives:  {0:N0}' -f $result.true_positive)
    ('True negatives:  {0:N0}' -f $result.true_negative)
    ('False positives: {0:N0}' -f $result.false_positive)
    ('False negatives: {0:N0}' -f $result.false_negative)
    ''
    'False negatives by pipeline stage'
    $stageLines
    ('Elapsed: {0}' -f $result.elapsed)
)
$summary | Set-Content -LiteralPath $TextOutputPath -Encoding UTF8
Write-Host ''
Write-Host "JSON result: $OutputPath" -ForegroundColor DarkGray
Write-Host "Text result: $TextOutputPath" -ForegroundColor DarkGray
