# Media Unseal

**Media Extraction & Conversion**  
**Version 1.2.1**

Media Unseal is a desktop application for inspecting media files, extracting individual streams, and converting selected content. It provides a straightforward interface for working with audio, video, subtitles, and embedded attachments.

## Features

- Inspect a media file and view its available streams.
- Extract audio tracks, video streams, and subtitles.
- Export embedded attachments when present.
- Convert selected streams to supported output formats, including WAV, FLAC, MP3, MP4 (H.264), and SRT.
- Add files using drag and drop or the file picker.
- Select streams and export options through a multilingual desktop interface.

## Requirements

- **Windows 10/11 (64-bit)** or **Linux (64-bit)**.
- **FFmpeg and FFprobe** for media inspection and processing. On Linux, install them using your distribution's package manager.
- On Linux, install ICU, Fontconfig, X11, ICE and SM system libraries. Debian: `sudo apt install libicu76 libfontconfig1 libx11-6 libice6 libsm6`. Fedora: `sudo dnf install libicu fontconfig libX11 libICE libSM`. An X11 session or XWayland is required for the current Avalonia backend.
- For Chinese, Japanese and Korean text, Noto CJK fonts are recommended but optional (`fonts-noto-cjk` on Debian, `google-noto-sans-cjk-fonts` on Fedora).

## Getting Started

1. Download the package for your operating system from the repository's Releases section.
2. Install or extract it, then launch **Media Unseal**.
3. Open a media file or drag it into the application.
4. Select the streams and output formats you need.
5. Choose a destination and start the export.

Available output options depend on the source media and the installed FFmpeg build.

## License and Third-Party Software

See [LICENSE.md](LICENSE.md) for the application's licensing terms and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for third-party acknowledgments and license information. Media processing is powered by [FFmpeg](https://ffmpeg.org/).
