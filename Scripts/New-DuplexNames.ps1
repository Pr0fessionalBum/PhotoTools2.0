<#
.SYNOPSIS
    Renames "scan*.png" / "scan*.jpg" / "scan*.jpeg" files into alternating
    front/back pairs: "<Name> (N).ext" and "<Name> (NB).ext" - each file
    keeps its own original extension.

.DESCRIPTION
    Same behavior as the original batch script. You provide the base "Name"
    either by passing -Name, or you'll be prompted for it when the script runs.

.PARAMETER Path
    Folder containing the scan files. Defaults to current folder.

.PARAMETER Start
    First number to use (front gets N, matching back gets NB). Defaults to 20.

.PARAMETER Name
    The base name to use for output files. If omitted, you'll be prompted.

.PARAMETER DryRun
    Preview only, no files renamed.

.EXAMPLE
    .\New-DuplexNames.ps1 -Path "C:\Scans\Xtapa\Duplex" -Name "Xtapa" -DryRun
#>

param(
    [string]$Path = ".",
    [int]$Start = 20,
    [string]$Name = "",
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
$Path = (Resolve-Path $Path).Path

if ([string]::IsNullOrWhiteSpace($Name)) {
    $Name = Read-Host "Enter the name to use for these scans"
}
if ([string]::IsNullOrWhiteSpace($Name)) {
    throw "No name provided. Aborting."
}

$files = Get-ChildItem -Path $Path -File | Where-Object { $_.Name -match '^scan.*\.(png|jpe?g)$' } | Sort-Object Name

if ($files.Count -eq 0) {
    Write-Host "No files matching 'scan*.png / .jpg / .jpeg' found in $Path."
    exit
}

Write-Host ""
Write-Host "--- Plan (Name: '$Name', Start: $Start) ---"

$i = $Start
$count = 0
$plan = @()
foreach ($f in $files) {
    if ($count % 2 -eq 0) {
        $newName = "{0} ({1}){2}" -f $Name, $i, $f.Extension
    } else {
        $newName = "{0} ({1}B){2}" -f $Name, $i, $f.Extension
        $i++
    }
    $plan += [PSCustomObject]@{ Old = $f.Name; New = $newName }
    $count++
}

foreach ($p in $plan) { Write-Host "  $($p.Old)  ->  $($p.New)" }

if ($DryRun) {
    Write-Host ""
    Write-Host "Dry run complete. No files were changed." -ForegroundColor Cyan
    exit
}

foreach ($p in $plan) {
    Rename-Item -Path (Join-Path $Path $p.Old) -NewName $p.New
}

Write-Host ""
Write-Host "Done! Renamed $($plan.Count) files." -ForegroundColor Green
