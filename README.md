# Photo Tools 2.0

**[Browse the full documentation](docs/index.html)** — installation, user guide, architecture, development, and repeatable testing.

Photo Tools 2.0 is a Windows desktop application for organizing and processing scanned photo collections. It combines a modern WinUI file-browser interface with cropping, conversion, replacement, renaming, statistics, and scanner-line inspection tools.

The application is designed for local photo workflows. Files remain on the computer, and potentially destructive operations provide previews, cancellation, or safety checks where appropriate.

## Current features

- Central folder browser with editable paths, folder navigation, drag and drop, sorting, and selection tools.
- Full-image thumbnails that preserve the entire photograph without cropping the preview.
- Batch crop processing through ImageMagick, including progress and cancellation.
- PNG-to-JPG conversion into a `JPG` subfolder, with an optional alternate output location.
- Native multi-PDF page preview and batch JPG export with editable output names, per-page rotation, inclusion, quality, and DPI controls.
- Cropped/JPG replacement matching based on filenames.
- Front/back scan renaming and numbering repair, including `A`/`B` suffix handling.
- Photo statistics and estimated scanning sessions based on photo creation times.
- Scanner-line detection in both horizontal and vertical directions using OpenCV.
- Highlighted scanner-line results with confidence, coverage, and estimated width.
- Side-by-side original/highlighted inspection with synchronized zoom and panning.
- Previous/next result navigation with buttons or the Left and Right arrow keys.

## Requirements

- Windows 10 version 1809 or newer; Windows 11 is recommended.
- 64-bit Windows for the provided launcher and normal development workflow.
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
- [ImageMagick 7](https://imagemagick.org/script/download.php#windows), including the `magick` command on the system `PATH`.
- PowerShell 5.1 or newer for the renaming scripts.
- Visual Studio 2022 or newer is optional. Install the .NET desktop and Windows App SDK/WinUI development components if using Visual Studio.

## Installation and first launch

1. Download or clone the repository, then open the `PhotoTools2.0` folder.
2. Install the .NET 10 SDK and ImageMagick 7.
3. Open PowerShell in the project folder and restore the packages:

   ```powershell
   & 'C:\Program Files\dotnet\dotnet.exe' restore
   ```

4. Build the x64 application:

   ```powershell
   & 'C:\Program Files\dotnet\dotnet.exe' build -p:Platform=x64 -r win-x64
   ```

5. Launch it with one of these options:

   - Double-click `Launch Photo Tools 2.bat` to build and run from source.
   - Double-click `Photo Tools 2.0.lnk` on the computer where that shortcut was created.
   - Run the built executable at:

     ```text
     bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\PhotoTools2.exe
     ```

The `.lnk` file contains a local absolute path. If the repository is moved or cloned onto another computer, rebuild the project and recreate the shortcut, or use the BAT launcher.

### Developer Mode

Some Windows App SDK development launches may request Developer Mode. If Windows reports a registration or Developer Mode error:

1. Open **Settings → System → For developers**.
2. Enable **Developer Mode**.
3. Restart the BAT launcher.

The directly built unpackaged executable may work without this setting, depending on the installed Windows App Runtime and launch method.

## ImageMagick setup check

Crop and PNG-to-JPG conversion require ImageMagick. Verify the command is available:

```powershell
magick -version
```

If Windows cannot find `magick`, reinstall ImageMagick and enable its option to add the application directory to `PATH`, or add the ImageMagick installation directory to `PATH` manually. Restart Photo Tools after changing `PATH`.

## Using the application

1. Select a tool from the left sidebar.
2. Paste a folder into the location bar, choose a folder, or drag a folder onto the workspace.
3. Navigate subfolders by double-clicking them.
4. Select the photos to process and review the tool-specific options.
5. Start the operation and monitor its progress. Use **Cancel** when available.

Scanner-line analysis is read-only. Double-click a flagged result to open the comparison inspector. The original appears on the left and the highlighted copy on the right. Drag either pane with the left or middle mouse button; zooming and movement are mirrored. Use Left/Right arrows to inspect other flagged photos.

## Project overview

| Area | Responsibility |
|---|---|
| `App.xaml` / `MainWindow.xaml` | Application startup, global resources, and the desktop window |
| `Pages/HomePage` | Sidebar navigation and workspace hosting |
| `Controls/*Workspace` | UI and orchestration for each photo tool |
| `Services/FolderBrowserService` | Shared folder enumeration, navigation, opening, and reveal behavior |
| `Services/AlbumScanner` | Album and image discovery |
| `Services/ScannerLineDetector` | OpenCV-based horizontal and vertical streak detection |
| `Services/AppSettings` | Persistent local application settings |
| `Models` | File, album, rename, replacement, statistics, and detection result models |
| `Scripts` | PowerShell implementations for scan-name and duplex-name operations |
| `Assets` | Application icons and Windows visual assets |

### Technology stack

- C# and .NET 10
- WinUI 3 / Windows App SDK 1.8
- OpenCvSharp 4 for scanner-line analysis
- ImageMagick 7 for crop and image conversion operations
- PowerShell for specialized batch-renaming workflows

## Safety and file behavior

- Scanner-line analysis never modifies source photos.
- PNG-to-JPG output defaults to a separate `JPG` subfolder.
- Crop output defaults to a separate `cropped` subfolder.
- Replacement tools match filenames before proposing changes.
- Empty output folders must not trigger source deletion.
- Keep a backup of irreplaceable scans before running bulk rename or replacement operations.

## Troubleshooting

### The app opens but a tool does nothing

- Confirm the selected folder contains supported images: PNG, JPG/JPEG, BMP, TIFF, or TIF.
- For crop and conversion, confirm `magick -version` works in a new PowerShell window.
- Confirm the app can write to the selected output location.
- Try the operation on a small test folder first.

### The BAT launcher cannot find .NET

Install the .NET 10 SDK in its standard location, or update `PHOTOTOOLS_DOTNET` inside `Launch Photo Tools 2.bat`.

### The shortcut stops working after moving the project

The shortcut points to the build folder using an absolute path. Use `Launch Photo Tools 2.bat`, rebuild the project, or recreate the shortcut for the new location.

### Build command

The normal verification build is:

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' build -p:Platform=x64 -r win-x64 --no-restore
```

## Planned future features

The roadmap is exploratory and may change as workflows are tested.

- Scanner-line correction after detection, with a non-destructive preview and approval step.
- Better rejection of real borders, frames, architecture, and other legitimate straight edges.
- Multiple highlighted line candidates per image in the detailed inspector.
- Auto-rotation using face/scene orientation detection, with manual confirmation for uncertain photos.
- Red-eye detection and correction if a reliable maintained library can be integrated.
- More ImageMagick crop, quality, format, and metadata options.
- A unified album browser with saved locations, recent albums, favorites, and stronger search/filtering.
- Richer right-click actions throughout file and comparison views.
- Session history, operation reports, and undo/recovery support for file-changing tools.
- Installer or portable distribution that does not depend on a repository-local build folder.
- Performance improvements for very large albums, including thumbnail caching and incremental scanning.
- Automated tests for filename matching, numbering rules, statistics, and image-analysis safeguards.

## Development notes

- Preserve user photos during development; use copied test folders for operations that rename or replace files.
- Shared browser behavior belongs in `FolderBrowserService` instead of being reimplemented per workspace.
- Keep long-running operations asynchronous, cancellable, and accompanied by visible progress.
- Build x64 before testing because the current launcher and native OpenCV runtime target `win-x64`.

### Performance baselines

Use the read-only benchmark script before and after performance changes. Run it against the same representative photo folder, with other heavy applications closed:

```powershell
.\Scripts\Measure-PhotoToolsPerformance.ps1 'D:\Photos\Large Album' -Iterations 7
```

Results are saved as timestamped JSON files under `performance-results`. Compare a later run with an earlier baseline:

```powershell
.\Scripts\Measure-PhotoToolsPerformance.ps1 'D:\Photos\Large Album' -Iterations 7 -CompareWith '.\performance-results\performance-20260818-180000.json'
```

The script measures time to discover the first 25 items, complete folder enumeration, and decode a representative sample of 320-pixel thumbnails. Negative comparison percentages indicate an improvement. The benchmark never modifies source photos.

For consistent generated test data, run the complete deterministic suite:

```powershell
.\Scripts\Invoke-PhotoToolsPerformanceSuite.ps1 -Case All -Iterations 7
```

The suite creates fixed `Small`, `Large`, and `Mixed` cases under `performance-fixtures`. The same seed and files are reused on later runs. Compare the suite after a code change with a previous suite result:

```powershell
.\Scripts\Invoke-PhotoToolsPerformanceSuite.ps1 -Case All -Iterations 7 -CompareWith '.\performance-results\suite-20260818-190000.json'
```

Use `-Regenerate` to rebuild the fixtures from the fixed seed. Both generated fixtures and results are excluded from Git.

To generate a fixed number of images containing deterministic scanner lines in each case, use:

```powershell
.\Scripts\Invoke-PhotoToolsPerformanceSuite.ps1 -Case All -ScannerLineImagesPerCase 40 -InterruptedScannerLineImagesPerCase 12 -Regenerate
```

Injected lines alternate between vertical and horizontal and vary in seeded position, width, and signed local contrast. Every image has a unique seeded, photo-like gradient and texture instead of sharing cloned noise templates. A configurable subset contains one to three deterministic interruptions. Each case writes `scanner-line-ground-truth.json`, which records exact line measurements and interruption ranges; generated images not listed in that file are clean controls.

Run the detector-specific accuracy test against a generated case with:

```powershell
.\Scripts\Measure-ScannerLineAccuracy.ps1 .\performance-fixtures\small -Sensitivity 0.55 -TargetAccuracy 90
```

The console colors accuracy, precision, recall, and interrupted-line accuracy green, yellow, or red relative to the target. Detailed JSON and text reports are written under `performance-results` and `test results`.

Windows batch launchers are also available under `Scripts`:

- `Generate-ScannerLineTestFixtures.bat` regenerates all deterministic fixtures with 40 line images and 12 interrupted lines per case.
- `Run-PhotoToolsPerformanceSuite.bat` runs the complete generated performance suite.
- `Run-ScannerLineAccuracyTest.bat` tests the Small scanner-line fixture at sensitivity `0.55` with a 90% target.

Double-click a launcher to use its defaults, or run it from Command Prompt with the same arguments accepted by its underlying PowerShell script.

Every benchmark also writes a readable text summary into the project-level `test results` folder. Detailed JSON remains in `performance-results` for automated baseline comparisons.

## Status

Photo Tools 2.0 is under active development. The source project is editable even when using the generated launcher or built executable.
