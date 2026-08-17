<#
.SYNOPSIS
    Fixes skipped numbering in "<Name> (N).ext" / "<Name> (NB).ext" pairs
    (.png, .jpg, and .jpeg all supported - each file keeps its own extension).
    The "Name" is auto-detected per folder from the file tagged "(1)".
    Renumbering always resets to start from (1) and adjusts all other numbers
    by the same difference.

.DESCRIPTION
    For each folder processed:
      1. Finds the file matching "<Name> (1).ext" (front, not "1B") and uses
         its <Name> as the naming scheme for that entire folder.
      2. ERRORS AND SKIPS the folder if no such "(1)" anchor file exists, or
         if multiple conflicting "(1)" files with different names are found.
         It will never guess a name.
      3. Any file in the folder that doesn't match the derived name is left
         untouched and reported as a warning (protects against stray files
         from a different scan job).
      4. Backs ("NB") with no matching front are quarantined into a
         "Quarantine" subfolder of THAT folder, keeping their number unchanged
         so you can trace the pair in your backup. This is reported clearly
         as a missing-pair alert.
      5. The remaining numbers are renumbered to always start from 1 and close
         any gaps (e.g. 5, 9, 10 -> 1, 2, 3). Front/back pairs always move
         together.

    With -Recurse, every subfolder is processed independently: the name is
    re-derived from scratch for each one (nothing carries over between
    folders), and the console clearly logs "Entering folder: X" plus the
    derived name each time it crosses a folder boundary.

.PARAMETER Path
    Root folder to process. Defaults to current folder.

.PARAMETER QuarantineFolder
    Name of the subfolder (created inside each processed folder) for orphaned backs.

.PARAMETER Recurse
    Also process every subfolder of -Path, each with its own independently
    derived name.

.PARAMETER DryRun
    Preview only — nothing is moved or renamed.

.EXAMPLE
    .\Fix-ScanNames.ps1 -Path "C:\Scans" -Recurse -DryRun
#>

param(
    [string]$Path = ".",
    [string]$QuarantineFolder = "Quarantine",
    [string]$NameOverride = "",
    [switch]$Recurse,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
$Path = (Resolve-Path $Path).Path

function Process-Folder {
    param(
        [string]$FolderPath,
        [string]$QuarantineFolder,
        [string]$NameOverride,
        [switch]$DryRun
    )

    # Generic pattern: "<anything> (<number>)[B].<ext>"  ext = png, jpg, or jpeg
    # Any file whose number is immediately followed by B is treated as a back image,
    # whether it is a normal pair like "Family reunions (12B).png" or a raw scan like "scan012B.png".
    $pattern = '^(.+) \((\d+)([AB])?\)\.(png|jpe?g)$'

    Write-Host ""
    Write-Host "==================================================="
    Write-Host " Entering folder: $FolderPath"
    Write-Host "==================================================="

    $files = Get-ChildItem -Path $FolderPath -File | Where-Object { $_.Extension -match '\.(png|jpe?g)$' }
    $matched = @()
    foreach ($f in $files) {
        if ($f.Name -match $pattern) {
            $matched += [PSCustomObject]@{
                File   = $f
                Prefix = $Matches[1]
                Num    = [int]$Matches[2]
                IsBack = [bool]($Matches[3] -eq 'B')
                FrontSuffix = if ($Matches[3] -eq 'A') { 'A' } else { '' }
                Ext    = $Matches[4]
            }
        }
    }

    # ---- Derive this folder's name from the "(1)" anchor (front only), or fall back to the lowest existing number if there is no 1 ----
    $anchors = @($matched | Where-Object { $_.Num -eq 1 -and -not $_.IsBack })
    $folderName = $null

    if ($matched.Count -eq 0) {
        # Raw scanner folders often contain "Folder Name.jpg", then scan0002.jpg,
        # scan0003.jpg, etc. In that layout the folder/title image is number 1.
        $folderName = Split-Path $FolderPath -Leaf
        Write-Host "  Derived name: '$folderName'  (from the folder name; raw scan filenames detected)" -ForegroundColor Cyan
    } elseif ($anchors.Count -gt 0) {
        $distinctPrefixes = @($anchors | Select-Object -ExpandProperty Prefix -Unique)
        if ($distinctPrefixes.Count -gt 1) {
            Write-Host "  ERROR: Multiple conflicting '(1)' files found with different names: $($distinctPrefixes -join ', ')" -ForegroundColor Red
            Write-Host "         SKIPPING this folder to avoid mixing up two different scan jobs." -ForegroundColor Red
            return
        }

        $folderName = $distinctPrefixes[0]
        Write-Host "  Derived name: '$folderName'  (from '$($anchors[0].File.Name)')" -ForegroundColor Cyan
    } else {
        $lowestNum = ($matched | Sort-Object Num | Select-Object -First 1).Num
        $fallbackMatches = @($matched | Where-Object { $_.Num -eq $lowestNum })
        $fallbackPrefixes = @($fallbackMatches | Select-Object -ExpandProperty Prefix -Unique)

        if ($fallbackPrefixes.Count -gt 1) {
            Write-Host "  ERROR: No '(1)' anchor file found, and the lowest available number contains conflicting names: $($fallbackPrefixes -join ', ')" -ForegroundColor Red
            Write-Host "         SKIPPING this folder to avoid mixing up two different scan jobs." -ForegroundColor Red
            return
        }

        $folderName = $fallbackPrefixes[0]
        Write-Host "  WARNING: No '(1)' anchor file found. Falling back to lowest existing number: $lowestNum, using name '$folderName'." -ForegroundColor Yellow
    }

    if (-not [string]::IsNullOrWhiteSpace($NameOverride)) {
        $folderName = $NameOverride.Trim()
        foreach ($entry in $matched) { $entry.Prefix = $folderName }
        Write-Host "  Using name override: '$folderName'" -ForegroundColor Cyan
    }

    # ---- Include raw scanner files and preserve the number embedded in their filename ----
    $extraEntries = @()
    foreach ($f in $files) {
        $alreadyHandled = @($matched | Where-Object { $_.File.FullName -eq $f.FullName })
        if ($alreadyHandled.Count -gt 0) { continue }

        if ($f.Name -match '^.*?(\d+)([AB])?\.(png|jpe?g)$') {
            $extraEntries += [PSCustomObject]@{
                File   = $f
                Prefix = $folderName
                Num    = [int]$Matches[1]
                IsBack = [bool]($Matches[2] -eq 'B')
                FrontSuffix = if ($Matches[2] -eq 'A') { 'A' } else { '' }
                Ext    = $Matches[3]
            }
        } elseif ([System.IO.Path]::GetFileNameWithoutExtension($f.Name) -eq $folderName -and
                  -not (@($matched + $extraEntries | Where-Object { $_.Num -eq 1 -and -not $_.IsBack }).Count -gt 0)) {
            $extraEntries += [PSCustomObject]@{
                File   = $f
                Prefix = $folderName
                Num    = 1
                IsBack = $false
                FrontSuffix = ''
                Ext    = $f.Extension.TrimStart('.')
            }
        }
    }
    $matched += $extraEntries

    if ($matched.Count -eq 0) {
        Write-Host "  No numbered or raw scan files were detected here. Skipping." -ForegroundColor DarkGray
        return
    }

    # ---- Consistency check: anything NOT matching the derived name is left alone ----
    $mismatched = @($matched | Where-Object { $_.Prefix -ne $folderName })
    if ($mismatched.Count -gt 0) {
        Write-Host "  WARNING: $($mismatched.Count) file(s) here don't match '$folderName' and will be left untouched:" -ForegroundColor Yellow
        foreach ($m in $mismatched) { Write-Host "    - $($m.File.Name)" -ForegroundColor Yellow }
    }
    $matched = @($matched | Where-Object { $_.Prefix -eq $folderName })

    # ---- Group by number ----
    $groups = @{}
    foreach ($m in $matched) {
        if (-not $groups.ContainsKey($m.Num)) { $groups[$m.Num] = @{ Front = $null; FrontExt = $null; FrontSuffix = ''; Back = $null; BackExt = $null } }
        if ($m.IsBack) { $groups[$m.Num].Back = $m.File.Name; $groups[$m.Num].BackExt = $m.Ext }
        else { $groups[$m.Num].Front = $m.File.Name; $groups[$m.Num].FrontExt = $m.Ext; $groups[$m.Num].FrontSuffix = $m.FrontSuffix }
    }

    # ---- Orphan backs -> quarantine (number unchanged) = missing-pair alert ----
    $orphanNums = @($groups.Keys | Where-Object { $groups[$_].Back -and -not $groups[$_].Front } | Sort-Object)
    if ($orphanNums.Count -gt 0) {
        $quarPath = Join-Path $FolderPath $QuarantineFolder
        Write-Host "  --- MISSING PAIR ALERT: orphaned back scan(s) with no matching front ---" -ForegroundColor Yellow
        if (-not $DryRun -and -not (Test-Path $quarPath)) { New-Item -ItemType Directory -Path $quarPath | Out-Null }
        foreach ($num in $orphanNums) {
            $name = $groups[$num].Back
            Write-Host "    Quarantining: $name (number $num kept unchanged so you can trace it in your backup)" -ForegroundColor Yellow
            if (-not $DryRun) { Move-Item -Path (Join-Path $FolderPath $name) -Destination (Join-Path $quarPath $name) -Force }
            $groups.Remove($num) | Out-Null
        }
    } else {
        Write-Host "  No missing pairs found." -ForegroundColor DarkGray
    }

    # ---- Renumber all files to start from 1, adjusting all numbers by the offset ----
    $remainingNums = @($groups.Keys | Sort-Object)
    if ($remainingNums.Count -eq 0) {
        Write-Host "  Nothing left to renumber after quarantine." -ForegroundColor DarkGray
        return
    }

    $lowestNum = $remainingNums[0]
    $offset = $lowestNum - 1
    $newNumMap = @{}
    $n = 1
    foreach ($num in $remainingNums) { $newNumMap[$num] = $n; $n++ }

    $gapsFound = @($remainingNums | Where-Object { $newNumMap[$_] -ne $_ })
    $needsRawRename = ($extraEntries.Count -gt 0)
    if ($gapsFound.Count -eq 0 -and -not $needsRawRename) {
        Write-Host "  Sequence is already continuous starting at 1 - nothing to renumber." -ForegroundColor DarkGray
        return
    }

    Write-Host "  --- Renumbering plan (resetting to start from 1) ---"
    foreach ($num in $remainingNums) {
        $g = $groups[$num]; $t = $newNumMap[$num]

        if ($g.Front) {
            $isAlreadyCorrect = ($t -eq $num) -and (-not $needsRawRename)
            if ($isAlreadyCorrect) { continue }
            $oldName = $g.Front
            if ($oldName -match '^.*?\.(png|jpe?g)$') {
                $originalLabel = $oldName
            } else {
                $originalLabel = "{0} ({1}).{2}" -f $folderName, $num, $g.FrontExt
            }
            Write-Host "    $originalLabel -> $folderName ($t$($g.FrontSuffix)).$($g.FrontExt)"
        }
        if ($g.Back) {
            $isAlreadyCorrect = ($t -eq $num) -and (-not $needsRawRename)
            if ($isAlreadyCorrect) { continue }
            $originalLabel = if ($g.Back -match '^.*?\.(png|jpe?g)$') { $g.Back } else { "{0} ({1}B).{2}" -f $folderName, $num, $g.BackExt }
            Write-Host ("    {0} -> {1} ({2}B).{3}" -f $originalLabel, $folderName, $t, $g.BackExt)
        }
    }

    if ($DryRun) {
        Write-Host "  (dry run - no changes applied)" -ForegroundColor Cyan
        return
    }

    # two-phase rename to avoid collisions
    $tempMap = @(); $ti = 0
    foreach ($num in $remainingNums) {
        $g = $groups[$num]
        if ($g.Front) {
            $tn = "__tmp_$ti.$($g.FrontExt)"
            Rename-Item -Path (Join-Path $FolderPath $g.Front) -NewName $tn
            $tempMap += [PSCustomObject]@{ Temp = $tn; Num = $num; IsBack = $false; FrontSuffix = $g.FrontSuffix; Ext = $g.FrontExt }
            $ti++
        }
        if ($g.Back) {
            $tn = "__tmp_$ti.$($g.BackExt)"
            Rename-Item -Path (Join-Path $FolderPath $g.Back) -NewName $tn
            $tempMap += [PSCustomObject]@{ Temp = $tn; Num = $num; IsBack = $true; FrontSuffix = ''; Ext = $g.BackExt }
            $ti++
        }
    }
    foreach ($e in $tempMap) {
        $fn = $newNumMap[$e.Num]
        $finalName = if ($e.IsBack) { "{0} ({1}B).{2}" -f $folderName, $fn, $e.Ext } else { "{0} ({1}{2}).{3}" -f $folderName, $fn, $e.FrontSuffix, $e.Ext }
        Rename-Item -Path (Join-Path $FolderPath $e.Temp) -NewName $finalName
    }

    Write-Host "  Done. Renumbered $($gapsFound.Count) item(s)." -ForegroundColor Green
    if ($orphanNums.Count -gt 0) { Write-Host "  $($orphanNums.Count) orphan(s) quarantined." -ForegroundColor Yellow }
}

# ---- Main: build the folder list, then process each one independently ----
$foldersToProcess = @($Path)
if ($Recurse) {
    $subfolders = Get-ChildItem -Path $Path -Directory -Recurse |
        Where-Object {
            $segments = $_.FullName -split '[\\/]'
            -not ($segments | Where-Object { $_ -eq $QuarantineFolder })
        } |
        Select-Object -ExpandProperty FullName
    $foldersToProcess += $subfolders
}

foreach ($folder in $foldersToProcess) {
    Process-Folder -FolderPath $folder -QuarantineFolder $QuarantineFolder -NameOverride $NameOverride -DryRun:$DryRun
}
