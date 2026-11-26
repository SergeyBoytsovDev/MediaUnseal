# Media Unseal v1.4.0

This stable release adds stream muxing and graphical subtitle workflows, improves long-running export feedback, and expands troubleshooting tools.

## Media processing

- Combine selected video, audio, and subtitle streams into one supported container. The format selectors show compatible choices, prefer stream copy, and show which streams will be transcoded before processing. Improved WebM/Opus handling preserves all six channels when converting compatible 5.1(side) sources.
- Extract Blu-ray PGS as SUP and DVD VobSub as a validated IDX/SUB pair. Reopen either format as a subtitle source. For DVD VOB files, the app can use matching IFO metadata when the subtitle palette and track mapping can be verified.
- Convert PGS and VobSub to SRT, WebVTT, ASS/SSA, or CSV with optional Tesseract OCR. Choose Fast or Best models and one of ten languages. When an end time is missing, the app can estimate a bounded interval and report it. If some cues contain no recognized text, export keeps the recognized cues and reports the skipped ones.

## Desktop experience and diagnostics

- Install, repair, update, or select an existing Tesseract installation from Settings → Installer. Tesseract 5.5.4 and its Fast and Best language packs are distributed separately.
- See individual track progress during sequential extraction and overall progress alongside elapsed time. The progress bar clears when processing ends.
- Preview audio and standalone subtitles with artwork that follows the active color theme. Embedded audio cover art is still used when available. Language selection includes flag icons.
- Copy multiple full entries from Diagnostic Report. The event log uses clearer error messages, while the report retains technical FFmpeg and FFprobe details and operation context.

## Packages

Windows x64 Installer and Portable and the Linux x64 archive include Native AOT builds of the desktop app and CLI. FFmpeg, FFprobe, and Tesseract are separate components. Native Linux desktop packages require the system libraries listed in the README.
