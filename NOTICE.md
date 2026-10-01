# Third-party software

## ExifTool

The released `CleanMetadata.exe` embeds an unmodified copy of **ExifTool** by Phil Harvey (https://exiftool.org), including its Perl runtime and libraries. Every metadata operation CleanMetadata performs is carried out by ExifTool.

ExifTool is free software, distributed under the same terms as Perl itself: the Artistic License or the GNU General Public License, at your option. The license texts ship inside the ExifTool package and inside the `exiftool_files` folder that CleanMetadata unpacks on first run (`%LOCALAPPDATA%\CleanMetadata`).

CleanMetadata is an independent project. It is not affiliated with, sponsored by or endorsed by ExifTool or Phil Harvey. If you find ExifTool useful, consider supporting its author: https://exiftool.org/donate.html

This repository does not contain ExifTool. `build.ps1` expects you to download it from the official site.
