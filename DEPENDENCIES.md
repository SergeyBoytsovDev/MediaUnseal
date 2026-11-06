# Media Unseal — Dependencies

**Application version:** 1.0.0  
**Application copyright:** © 2026 Sergey Boytsov


This document identifies the third-party software used by Media Unseal. It distinguishes application-level NuGet dependencies, native assets, build/test tools, and independently installed tools. Licenses and notices are described in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). The license for Media Unseal's own code is in [LICENSE.md](LICENSE.md).

> **Scope:** The package versions below are based on the application's audited .NET 10 dependency graph. NuGet's resolved graph is not identical to the contents of every Windows or Linux release: build-only, test-only, and platform-specific assets may be excluded. The contents of a distributed release package determine which notices must accompany that package. The audited Windows and Linux outputs were **framework-dependent** and are not necessarily identical to the eventual installers and archives.

## Application platform

| Component | Version / role | License / distribution notes |
| --- | --- | --- |
| .NET | Target framework `net10.0` | Microsoft/.NET Foundation and contributors; licenses vary by file and distribution. The audited Windows/Linux publishes were framework-dependent. Self-contained packages require an updated inventory. |
| Avalonia UI | 12.1.4 | MIT; cross-platform desktop interface. |
| FFmpeg / FFprobe | External programs; version depends on installation | LGPL or GPL depending on the actual build. **Not included** in the audited application publish folders. See below. |

## Direct NuGet dependencies

| Package | Version | Declared license / qualification |
| --- | --- | --- |
| [Avalonia](https://www.nuget.org/packages/Avalonia/12.1.4) | 12.1.4 | MIT |
| [Avalonia.Desktop](https://www.nuget.org/packages/Avalonia.Desktop/12.1.4) | 12.1.4 | MIT |
| [Avalonia.Themes.Fluent](https://www.nuget.org/packages/Avalonia.Themes.Fluent/12.1.4) | 12.1.4 | MIT |
| [Avalonia.Fonts.Inter](https://www.nuget.org/packages/Avalonia.Fonts.Inter/12.1.4) | 12.1.4 | MIT (package); embedded Inter font under SIL OFL 1.1 |
| [AvaloniaUI.DiagnosticsSupport](https://www.nuget.org/packages/AvaloniaUI.DiagnosticsSupport/2.2.3) | 2.2.3 | Licensing **not established** from available package metadata; used in Debug only and absent from audited Release publishes |
| [CommunityToolkit.Mvvm](https://www.nuget.org/packages/CommunityToolkit.Mvvm/8.4.2) | 8.4.2 | MIT |
| [Markdown.Avalonia.Tight](https://www.nuget.org/packages/Markdown.Avalonia.Tight/12.0.2) | 12.0.2 | MIT |

## Transitive NuGet dependencies

| Package | Version | Declared license / qualification |
| --- | --- | --- |
| Avalonia.Angle.Windows.Natives | 2.1.27548.20260419 | ANGLE's BSD-style license; nested native notices may apply |
| Avalonia.BuildServices | 11.3.2 | MIT; build-time component |
| Avalonia.FreeDesktop | 12.1.4 | MIT |
| Avalonia.FreeDesktop.AtSpi | 12.1.4 | MIT |
| Avalonia.HarfBuzz | 12.1.4 | MIT wrapper; HarfBuzz native terms also apply |
| Avalonia.Native | 12.1.4 | MIT wrapper; native notices to verify |
| Avalonia.Remote.Protocol | 12.1.4 | MIT |
| Avalonia.Skia | 12.1.4 | MIT wrapper; Skia native terms also apply |
| Avalonia.Win32 | 12.1.4 | MIT |
| Avalonia.X11 | 12.1.4 | MIT |
| ColorDocument.Avalonia | 12.0.2 | MIT |
| ColorTextBlock.Avalonia | 12.0.2 | MIT |
| HarfBuzzSharp | 8.3.1.3 | MIT wrapper; native HarfBuzz terms also apply |
| HarfBuzzSharp.NativeAssets.Linux | 8.3.1.3 | MIT wrapper and native notices; Linux conditional |
| HarfBuzzSharp.NativeAssets.macOS | 8.3.1.3 | MIT wrapper and native notices; not in audited Windows/Linux packages |
| HarfBuzzSharp.NativeAssets.WebAssembly | 8.3.1.3 | MIT wrapper and native notices; not in audited desktop packages |
| HarfBuzzSharp.NativeAssets.Win32 | 8.3.1.3 | MIT wrapper and native notices; Windows conditional |
| MicroCom.Runtime | 0.11.6 | MIT |
| Microsoft.Extensions.DependencyInjection.Abstractions | 8.0.0 | MIT |
| Microsoft.Extensions.Logging.Abstractions | 8.0.0 | MIT |
| Microsoft.IO.RecyclableMemoryStream | 3.0.1 | MIT |
| SkiaSharp | 3.119.4 | MIT wrapper; Skia native terms also apply |
| SkiaSharp.NativeAssets.Linux | 3.119.4 | MIT wrapper and bundled third-party notices; Linux conditional |
| SkiaSharp.NativeAssets.macOS | 3.119.4 | MIT wrapper and native notices; not in audited Windows/Linux packages |
| SkiaSharp.NativeAssets.WebAssembly | 3.119.4 | MIT wrapper and native notices; not in audited desktop packages |
| SkiaSharp.NativeAssets.Win32 | 3.119.4 | MIT wrapper and native notices; Windows conditional |
| Tmds.DBus.Protocol | 0.94.1 | MIT |

All package names above correspond to entries on [NuGet](https://www.nuget.org/). Declared package licenses do not necessarily describe separately packaged native binaries or fonts. The application graph lists **7 direct and 27 transitive packages**; it should not be interpreted as 34 independently redistributed binary components.

## Confirmed components of the audited publish folders

| Component family | Windows (`win-x64`) | Linux (`linux-x64`) |
| --- | --- | --- |
| Avalonia 12.1.4 and associated managed assemblies | Included | Included |
| CommunityToolkit.Mvvm 8.4.2, Markdown.Avalonia family 12.0.2 | Included | Included |
| SkiaSharp 3.119.4, HarfBuzzSharp 8.3.1.3 and associated managed components | Included | Included |
| Native `libSkiaSharp` / `libHarfBuzzSharp` | `.dll` | `.so` |
| ANGLE native `av_libglesv2.dll` | Included | Not included |
| Inter font resources | Included via package | Included via package |
| .NET application host | Included | Included |
| Full .NET runtime / FFmpeg / FFprobe | Not included | Not included |
| AvaloniaUI.DiagnosticsSupport (Debug tooling) | Not included | Not included |

The above reflects the **inspected Release outputs only**. Publishing as self-contained would change the distributed .NET payload.

## Fonts and native components

| Component | Role | License / notices |
| --- | --- | --- |
| [Inter](https://github.com/rsms/inter) | UI font (embedded via Avalonia.Fonts.Inter for non-Windows) | SIL Open Font License 1.1; Inter Project Authors |
| [ANGLE](https://github.com/google/angle) | Windows rendering/native component | BSD-style license and any bundled subcomponent notices |
| [Skia](https://github.com/google/skia) / [SkiaSharp](https://github.com/mono/SkiaSharp) | Rendering and managed bindings | Skia's BSD-style license; SkiaSharp's MIT license; nested native notices |
| [HarfBuzz](https://github.com/harfbuzz/harfbuzz) / HarfBuzzSharp | Text shaping and managed bindings | HarfBuzz “Old MIT” and component notices; wrapper MIT |

## External FFmpeg / FFprobe

Media Unseal starts FFmpeg and FFprobe **as separate processes**, without linking FFmpeg libraries into its application code. The audited `win-x64` and `linux-x64` publish directories did not bundle their executables.

- An existing user-installed FFmpeg/FFprobe may be used.
- On Windows the application can use WinGet (`Gyan.FFmpeg`) or download a third-party Gyan FFmpeg release archive as a separate installation path.
- The FFmpeg build inspected in the audit was a **local sample**: Gyan `9.0.2 full_build`, reporting `GPL version 3 or later` for FFmpeg and FFprobe. That finding **must not** be generalized to other user-installed or automatically downloaded builds.
- The licensing of a specific FFmpeg binary depends on its configuration and linked third-party code. See [FFmpeg's official legal page](https://ffmpeg.org/legal.html) and [Gyan's build information](https://www.gyan.dev/ffmpeg/builds/).

FFmpeg is **not** covered by the Media Unseal license. Anyone redistributing an FFmpeg build must separately satisfy its license, notice, and corresponding-source obligations.

## Build and test dependencies (not end-user components by default)

- .NET SDK (audit host: `10.0.403`) is a development tool, not the application's target version.
- `Avalonia.Headless` 12.1.4 (MIT) is used by the test project.
- Build-only generators, analyzers, and Debug-only tooling are not assumed to be shipped in Release packages.

## Accompanying original legal materials

The audited local NuGet cache yielded five unique original notice/license files covering 13 cache occurrences. Those original texts have been incorporated, without substantive abbreviation, into [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md), alongside attribution and license texts for Avalonia, Inter, Markdown.Avalonia and other confirmed packages. See the notice document for upstream references and the scope of the supplied material.

## Release packaging and license scope

The legally relevant component list is the content **actually included** in each published installer or archive. For example, self-contained .NET deployment embeds more .NET runtime components than a framework-dependent deployment; Windows and Linux packages have different native assets. Relevant license texts, copyright statements, and bundled third-party notices must accompany the shipped components. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
