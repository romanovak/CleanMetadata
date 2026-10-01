# Changelog

## 1.0.0

First release.

- Drag-and-drop GUI front end for ExifTool: drop files or folders, get `_clean` copies.
- Options panel: presets (All, Privacy, Provenance, Custom), one switch per metadata category, extra tags to remove, and a copyable preview of the equivalent ExifTool command. Choices are remembered.
- Inspector: see the metadata a file carries, grouped by category; for cleaned files each tag is marked removed or kept.
- The inspector opens inline under the file's row (no side panel). Files that have not been cleaned get **Clean this file** and **Custom**, a per-file choice of which categories to remove.
- New vector logo (`assets/logo.svg`, plus a white version) used for the icon, the app header and the repository; the pixel-art sprites are generated from it.
- Pixel-art cleaning animation (a brush wipes a document and the metadata turns into dust) and a magnifier animation while inspecting. The cleaning animation can be turned off in Options.
- Two drop areas, Clean and Inspect, and a drag overlay that splits in two; the file list has a counter bar with a Clear list button.
- Optional **Replace originals** (Options, off by default, asks for confirmation): the cleaned file is verified and then swapped in; if a C2PA marker remains the original is kept and a `_clean` copy is saved instead.
- Removes EXIF, XMP, IPTC and C2PA / Content Credentials from videos and photos; originals are never modified unless you turn on Replace originals.
- Flags copies where a C2PA marker is still detected.
- Photos keep their color profile and orientation in the "All" preset.
- Handles file names with any Unicode characters.
- Built-in uninstaller (title bar menu or `--uninstall`) that removes the app's data and shows you the exe to delete, and an About dialog.
- Single portable x64 executable with ExifTool embedded; no installer, no admin rights.
