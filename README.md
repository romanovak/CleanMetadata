<p align="center"><img src="assets/icon.png" width="128" alt="CleanMetadata icon"></p>

# CleanMetadata

A lightweight GUI front end for [ExifTool](https://exiftool.org). Drag videos or photos onto it and get copies with their metadata stripped: EXIF, XMP and C2PA / Content Credentials manifests included. No command line, no installer, no dependencies to set up.

CleanMetadata is **not** a metadata engine of its own. All the actual work is done by ExifTool, the reference tool for reading and writing metadata, created and maintained by Phil Harvey. This project wraps ExifTool in a drag-and-drop GUI, lets you pick what gets removed, and adds a few safety checks. It is an independent project, not affiliated with or endorsed by ExifTool or its author. If you are comfortable with a CLI, ExifTool on its own does the same job.

## Download and run

1. Download `CleanMetadata.exe` from the [latest release](../../releases/latest).
2. Run it. Drop files or folders onto the GUI, or press `Ctrl+O` to pick them.
3. A `name_clean.ext` copy is written next to each original. The original is never modified.

Supported formats: `mp4 mov m4v 3gp jpg jpeg png webp heic heif tif tiff gif`.

## Choose what to remove

By default everything is stripped. Open **Options** (the sliders icon in the title bar) to be selective:

| Preset | Removes |
|---|---|
| **All** | All metadata (`exiftool -all=`). Color profile and orientation of photos are kept unless you turn them off. |
| **Privacy** | Location, device and camera, dates and times, author and rights, thumbnails. |
| **Provenance** | C2PA / Content Credentials, editing software and edit history. |
| **Custom** | Any combination of the categories below. |

Categories, each one a switch:

- **Location**: GPS coordinates, city, state, country.
- **Device and camera**: make, model, lens, serial numbers, maker notes.
- **Dates and times**: capture, creation and modification dates.
- **Author and rights**: artist, copyright, captions, keywords (EXIF, IPTC, XMP).
- **Thumbnails and previews**: embedded preview images.
- **Edit history and software**: editing software tags, XMP document IDs and history.
- **C2PA / Content Credentials**: provenance manifests.

You can also list **extra tags** to remove by name (`XMP-xmp:CreatorTool, EXIF:Software`, wildcards such as `GPS*` work). Only plain tag names are accepted, anything else is ignored, so nothing you type can turn into an arbitrary command-line option. The panel shows the equivalent ExifTool command, with a button to copy it. Your choices are remembered between runs.

Heads-up for photos and videos: removing dates writes empty date fields, which is valid but some players show them as unknown.

## Inspect a file

Click the magnifier on any row, or **Inspect...** in the drop area to pick a file, to see what metadata a file carries before you clean it. ExifTool reads it and the tags are grouped by the same categories as the Options switches (location, device and camera, dates, author and rights, thumbnails, edit history and software, C2PA, other). For a file you have already cleaned, every tag is marked as removed or kept, so you can check the result tag by tag.

## Cleaning animation

While files are being cleaned, a pixel-art brush sweeps a document and its metadata turns into dust. The real work takes milliseconds, so the animation holds each file for a moment (about three seconds per batch in total, never more than 1.5 s per file) so you can see it happen. If you would rather not wait, turn **Cleaning animation** off in Options and cleaning is instant.

## Requirements

- Windows 10 or 11, x64.
- Nothing else. It is a portable, single-file executable: it targets the .NET Framework 4.x that ships with Windows, and ExifTool is embedded in the exe. No admin rights, no network access, no installer.
- About 35 MB of free disk space. On first launch the embedded ExifTool is unpacked once to `%LOCALAPPDATA%\CleanMetadata`.

## Uninstall

The app has its own uninstaller: open the `...` menu in the title bar and choose **Uninstall**. It deletes `%LOCALAPPDATA%\CleanMetadata` (the unpacked ExifTool and your settings), closes the app, and opens Explorer with `CleanMetadata.exe` selected so you can delete it. `CleanMetadata.exe --uninstall` does the same without any dialog, only removing the data folder. Your original and `_clean` files are never touched.

The exe does not delete itself on purpose: a program that launches a hidden shell to remove its own file is exactly the behavior antivirus heuristics flag as malware.

CleanMetadata does not write to the registry, add startup entries or make network connections. Its only footprint is the `%LOCALAPPDATA%\CleanMetadata` folder (the unpacked ExifTool and a small `settings.ini`) and a short-lived temp file with ExifTool's arguments.

## Antivirus warnings

The exe is not code-signed and it bundles ExifTool, so some antivirus products may flag it with a generic, heuristic detection. Here is everything it does, so you can judge for yourself:

- It reads the files you give it and writes `_clean` copies next to them. It never modifies or deletes your originals.
- On first run it unpacks the bundled ExifTool into `%LOCALAPPDATA%\CleanMetadata` and runs it as a hidden child process (no console window). This "unpack and run" step is the most likely reason for a heuristic flag.
- It keeps a small `settings.ini` in that same folder and writes short-lived temp files with ExifTool's arguments.
- It does not use the network, write to the registry, add startup entries or services, ask for elevated privileges, inject into other processes, or delete itself.

The source is in this repository and you can build the exe yourself (see below) and compare it with the release. If a product flags the official release, please report it as a false positive to the vendor: [Microsoft](https://www.microsoft.com/wdsi/filesubmission), [Kaspersky](https://opentip.kaspersky.com).

## Verify the download

Each release lists the SHA-256 of the exe. Compare it with:

```powershell
Get-FileHash .\CleanMetadata.exe -Algorithm SHA256
```

The exe is not code-signed, so Windows SmartScreen may flag it as an unrecognized app. If the hash matches, choose **More info > Run anyway**. You can also build the exe yourself from this repository, see below.

## What it does and doesn't do

- Writes a copy of the file with the selected metadata removed, then, when C2PA removal is selected, scans the copy for leftover C2PA markers and flags it if any remain. Videos are remuxed without re-encoding, so quality is unchanged.
- It does **not** remove invisible watermarks embedded in the pixels themselves (some AI image and video generators add one), and it cannot stop a platform from classifying content on its own.
- Removing provenance data does not change what the content is. Follow the disclosure rules of wherever you publish.

## Build from source

Requirements: Windows 10/11 and the [Windows 64-bit ExifTool package](https://exiftool.org). The C# compiler that ships with Windows (`csc.exe`, .NET Framework 4.x) is used, so there is nothing to install. The GUI is WPF.

```powershell
.\build.ps1 -ExifDir C:\path\to\exiftool-13.59_64

# optional tests (run with Windows PowerShell 5.1)
.\tests\smoke.ps1     -ExifDir C:\path\to\exiftool-13.59_64   # end to end: launches the exe on sample images
.\tests\options.ps1   -ExifDir C:\path\to\exiftool-13.59_64   # presets and categories against a JPEG fixture
.\tests\inspector.ps1 -ExifDir C:\path\to\exiftool-13.59_64   # tag reading, categories and removed/kept comparison
.\tests\uninstall.ps1                                         # the uninstaller, on a copy of the exe
```

The output is `dist\CleanMetadata.exe`, with ExifTool and its `exiftool_files` folder embedded as a resource.

`make-icon.ps1` regenerates `assets\icon.ico` and `assets\logo.png` from a PNG, and `make-sprites.ps1` prepares the pixel-art sprites in `assets\sprites` from four source images (document, brush, magnifier, dust). Both re-draw the images, so no metadata from the source files survives.

## License

MIT, see [LICENSE](LICENSE). The release exe bundles an unmodified ExifTool, which has its own license: see [NOTICE.md](NOTICE.md).
