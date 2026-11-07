# Media Unseal 1.1.0

- Added single-instance behavior on Windows and Linux: a second launch activates the existing window.
- Added update checks and verified downloads for Windows Installer, Windows Portable, and Linux x64. Updates require user consent.
- Improved settings persistence with atomic writes and recovery after write failures.
- Improved temporary-file cleanup and protection of export paths.
- Redacted sensitive data in new and historical diagnostic logs and exported reports.
- Set English as the first-run interface language and refreshed language ordering. All ten interface languages remain available.

FFmpeg and FFprobe remain separate prerequisites and are not bundled with Media Unseal.
