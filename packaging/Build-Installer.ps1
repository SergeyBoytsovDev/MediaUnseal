param([string]$IsccPath, [string]$Version)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$project = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$packages = Join-Path $project 'bin\release-packages'
if (-not $Version) {
    $Version = ([xml](Get-Content (Join-Path $project 'MediaUnseal.csproj') -Raw)).SelectSingleNode('/Project/PropertyGroup/Version').InnerText
}
if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') { throw 'Invalid application version.' }
$portable = Join-Path $packages ("MediaUnseal-$Version-win-x64-portable.zip")
$setup = Join-Path $packages ("MediaUnseal-$Version-win-x64-setup.exe")
if (-not (Test-Path -LiteralPath $portable)) { throw "Portable ZIP is missing: $portable" }

if (-not $IsccPath) {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { $IsccPath = $command.Source }
    foreach ($candidate in @((Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe'),
            'C:\Program Files\Inno Setup 7\ISCC.exe',
            'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
            'C:\Program Files\Inno Setup 6\ISCC.exe')) {
        if (-not $IsccPath -and (Test-Path -LiteralPath $candidate)) { $IsccPath = $candidate }
    }
}
if (-not $IsccPath -or -not (Test-Path -LiteralPath $IsccPath)) {
    throw 'Inno Setup ISCC.exe is not installed. Install Inno Setup 6/7, then run this script again.'
}

$stage = Join-Path $packages ('installer-input-' + [guid]::NewGuid().ToString('N'))
try {
    [System.IO.Compression.ZipFile]::ExtractToDirectory($portable, $stage)
    $source = Join-Path $stage ("MediaUnseal-$Version-win-x64-portable")
    $runtimeLicenseDirs = @(Get-ChildItem -LiteralPath (Join-Path $source 'licenses') -Directory -Filter 'dotnet-*')
    if ($runtimeLicenseDirs.Count -ne 1) { throw 'Portable package must contain exactly one .NET runtime notice directory.' }
    $runtimeLicenseDir = Join-Path 'licenses' $runtimeLicenseDirs[0].Name
    foreach ($file in @('MediaUnseal.exe', 'MediaUnseal.Updater.exe', 'mucli.exe', 'CLI.md',
            'libSkiaSharp.dll', 'libHarfBuzzSharp.dll', 'av_libglesv2.dll',
            'LICENSE.md', 'DEPENDENCIES.md', 'THIRD-PARTY-NOTICES.md',
            (Join-Path $runtimeLicenseDir 'RUNTIME-LICENSE.TXT'),
            (Join-Path $runtimeLicenseDir 'RUNTIME-THIRD-PARTY-NOTICES.TXT'),
            (Join-Path $runtimeLicenseDir 'WINDOWS-DOTNET-LIBRARY-LICENSE.txt'))) {
        if (-not (Test-Path -LiteralPath (Join-Path $source $file))) {
            throw "Portable package is incomplete: $file"
        }
    }
    foreach ($culture in @('de', 'en', 'es', 'fr', 'it', 'ja', 'ko', 'pt', 'zh-CN')) {
        if (-not (Test-Path -LiteralPath (Join-Path $source "$culture\MediaUnseal.resources.dll"))) {
            throw "Missing localization: $culture"
        }
    }
    $native = (Get-Item -LiteralPath (Join-Path $source 'MediaUnseal.exe')).VersionInfo
    if ($native.FileMajorPart -ne ([Version]$Version).Major -or
        $native.FileMinorPart -ne ([Version]$Version).Minor -or
        $native.FileBuildPart -ne ([Version]$Version).Build) {
        throw 'Portable Native AOT application version does not match the requested version.'
    }
    foreach ($obsolete in @('MediaUnseal.dll', 'MediaUnseal.deps.json',
            'MediaUnseal.runtimeconfig.json', 'hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll')) {
        if (Test-Path -LiteralPath (Join-Path $source $obsolete)) {
            throw "Portable package contains obsolete .NET payload: $obsolete"
        }
    }
    if (Get-ChildItem -LiteralPath $source -Recurse -File | Where-Object {
            $_.Name -match '^(ffmpeg|ffprobe)(\.exe)?$|DiagnosticsSupport|\.pdb$' }) {
        throw 'Portable package includes excluded tools or debug files.'
    }
    & $IsccPath "-dAppVersion=$Version" "-dReleaseSource=$source" "-o$packages" (Join-Path $PSScriptRoot 'MediaUnseal.iss')
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $setup)) {
        throw "Inno Setup did not produce the expected installer: $setup"
    }
    Get-Item -LiteralPath $setup | Select-Object FullName, Length
    Get-FileHash -LiteralPath $setup -Algorithm SHA256 | Select-Object Hash
}
finally {
    $parent = [System.IO.Path]::GetFullPath($packages).TrimEnd('\') + '\'
    $target = [System.IO.Path]::GetFullPath($stage)
    if ($target.StartsWith($parent, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $target)) {
        Remove-Item -LiteralPath $target -Recurse -Force
    }
}
