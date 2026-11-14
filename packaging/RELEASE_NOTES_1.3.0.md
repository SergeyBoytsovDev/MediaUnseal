# Media Unseal 1.3.0

- Added Matroska Chapters inspection and XML export in the desktop application and CLI, including nested and ordered chapters and chapter metadata.
- Added universal nine-column CSV export for text subtitles and four-column DaVinci Resolve ADR cue-list export, with configurable delimiters and ADR timecode settings.
- Added CSV subtitle input and conversion to SRT, WebVTT, and ASS/SSA. Universal and ADR CSV are recognized by their content; ADR conversion uses the frame rate selected by the user.
- Improved chapter properties and status display, and clarified when `ChapterStringUID` cannot be represented in exported XML.
- Refined the subtitle conversion controls and CSV handling following user testing.

The application version is 1.3.0. FFmpeg and FFprobe remain separate installations and are not included in the packages.
