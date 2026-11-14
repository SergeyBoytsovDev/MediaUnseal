#Requires -Version 7.0
param([string]$Version, [ValidateSet('win-x64','linux-x64')][string]$Target = $(if ($IsWindows) { 'win-x64' } else { 'linux-x64' }))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$project = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $Version) { $Version = ([xml](Get-Content (Join-Path $project 'MediaUnseal.csproj') -Raw)).SelectSingleNode('/Project/PropertyGroup/Version').InnerText }
if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') { throw 'Invalid version.' }
if ($Target -eq 'linux-x64' -and -not $IsLinux) { throw 'Linux Native AOT must be published on a Linux x64 host.' }
if ($Target -eq 'win-x64' -and -not $IsWindows) { throw 'Windows Native AOT must be published on a Windows x64 host.' }
$packages = Join-Path $project 'bin/release-packages'
New-Item -ItemType Directory -Path $packages -Force | Out-Null
foreach ($releaseTarget in @(@{Rid='win-x64';Suffix='win';Apphost='MediaUnseal.exe';Helper='MediaUnseal.Updater.exe'}, @{Rid='linux-x64';Suffix='linux';Apphost='MediaUnseal';Helper='MediaUnseal.Updater'} | Where-Object Rid -eq $Target)) {
    $publish = Join-Path $packages ("publish-" + $releaseTarget.Rid + "-" + $Version)
    $stage = Join-Path $packages $(if ($releaseTarget.Suffix -eq 'win') { "MediaUnseal-$Version-win-x64-portable" } else { "_stage-linux-$Version" })
    $expectedParent = [System.IO.Path]::GetFullPath($packages).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    foreach ($path in @($publish,$stage)) {
        $resolved = [System.IO.Path]::GetFullPath($path)
        if (-not $resolved.StartsWith($expectedParent,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe stage path.' }
        if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    }
    if ($releaseTarget.Suffix -eq 'win') {
        & dotnet publish (Join-Path $project 'MediaUnseal.csproj') -c Release -r win-x64 -p:PublishAot=true -o $publish
    } else {
        & dotnet publish (Join-Path $project 'MediaUnseal.csproj') -c Release -r linux-x64 -p:PublishAot=true --self-contained true -o $publish
    }
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $($releaseTarget.Rid)" }
    $cliPublish = Join-Path $packages ("publish-mucli-" + $releaseTarget.Rid + "-" + $Version)
    & dotnet publish (Join-Path $project 'Cli/mucli.csproj') -c Release -r $releaseTarget.Rid --self-contained true -o $cliPublish
    if ($LASTEXITCODE -ne 0) { throw "CLI publish failed: $($releaseTarget.Rid)" }
    $cliName = if ($releaseTarget.Suffix -eq 'win') { 'mucli.exe' } else { 'mucli' }
    Copy-Item -LiteralPath (Join-Path $cliPublish $cliName) -Destination $publish -Force
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    Get-ChildItem -LiteralPath $publish -Force | Where-Object { $_.Extension -ne '.pdb' } | Copy-Item -Destination $stage -Recurse -Force
    foreach ($file in @('LICENSE.md','DEPENDENCIES.md','THIRD-PARTY-NOTICES.md')) {
        Copy-Item -LiteralPath (Join-Path $project $file) -Destination $stage -Force
    }
    Copy-Item -LiteralPath (Join-Path $project 'docs\CLI.md') -Destination $stage -Force
    $assetsPath = Join-Path $project 'Cli/obj/project.assets.json'
    if (-not (Test-Path -LiteralPath $assetsPath)) { throw "CLI restore assets are missing: $assetsPath" }
    $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
    $runtimePackage = "Microsoft.NETCore.App.Runtime.$($releaseTarget.Rid)"
    $runtimeDependencies = @($assets.project.frameworks.'net10.0'.downloadDependencies |
        Where-Object { $_.name -eq $runtimePackage })
    if ($runtimeDependencies.Count -ne 1) {
        throw "Cannot determine the restored version of $runtimePackage from $assetsPath"
    }
    $versionMatch = [regex]::Match([string]$runtimeDependencies[0].version,
        '^\[(\d+\.\d+\.\d+),\s*\1\]$')
    if (-not $versionMatch.Success) {
        throw "Invalid restored version of $runtimePackage in $assetsPath"
    }
    $runtimeVersion = $versionMatch.Groups[1].Value
    $packageRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else {
        Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages'
    }
    $pack = Join-Path $packageRoot ("microsoft.netcore.app.runtime.$($releaseTarget.Rid)/$runtimeVersion")
    foreach ($licenseFile in @('LICENSE.TXT','THIRD-PARTY-NOTICES.TXT')) {
        if (-not (Test-Path -LiteralPath (Join-Path $pack $licenseFile) -PathType Leaf)) {
            throw "Required .NET runtime notice is missing: $(Join-Path $pack $licenseFile)"
        }
    }
    $licenseDir = Join-Path $stage ("licenses/dotnet-" + $runtimeVersion)
    New-Item -ItemType Directory -Path $licenseDir -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $pack 'LICENSE.TXT') -Destination (Join-Path $licenseDir 'RUNTIME-LICENSE.TXT')
    Copy-Item -LiteralPath (Join-Path $pack 'THIRD-PARTY-NOTICES.TXT') -Destination (Join-Path $licenseDir 'RUNTIME-THIRD-PARTY-NOTICES.TXT')
    if ($releaseTarget.Suffix -eq 'win') {
        Copy-Item -LiteralPath (Join-Path $project 'packaging\licenses\WINDOWS-DOTNET-LIBRARY-LICENSE.txt') -Destination $licenseDir
    } else {
        Copy-Item -LiteralPath (Join-Path $project 'packaging/licenses/WINDOWS-DOTNET-LIBRARY-LICENSE.txt') -Destination (Join-Path $licenseDir 'DOTNET-LIBRARY-LICENSE.txt')
    }
    $required = @($releaseTarget.Apphost,$releaseTarget.Helper,$cliName,'CLI.md','LICENSE.md',
        'DEPENDENCIES.md','THIRD-PARTY-NOTICES.md')
    if ($releaseTarget.Suffix -eq 'win') {
        $required += @('libSkiaSharp.dll','libHarfBuzzSharp.dll','av_libglesv2.dll')
        $native = (Get-Item -LiteralPath (Join-Path $stage 'MediaUnseal.exe')).VersionInfo
        if ($native.FileMajorPart -ne ([Version]$Version).Major -or
            $native.FileMinorPart -ne ([Version]$Version).Minor -or
            $native.FileBuildPart -ne ([Version]$Version).Build) {
            throw 'Windows Native AOT application version does not match the requested version.'
        }
        foreach ($obsolete in @('MediaUnseal.dll','MediaUnseal.deps.json',
                'MediaUnseal.runtimeconfig.json','hostfxr.dll','hostpolicy.dll','coreclr.dll')) {
            if (Test-Path -LiteralPath (Join-Path $stage $obsolete)) {
                throw "Windows package contains obsolete .NET payload: $obsolete"
            }
        }
    } else {
        $required += @('libSkiaSharp.so','libHarfBuzzSharp.so')
        foreach ($obsolete in @('MediaUnseal.dll','MediaUnseal.deps.json',
                'MediaUnseal.runtimeconfig.json','libcoreclr.so','libhostfxr.so','libhostpolicy.so')) {
            if (Test-Path -LiteralPath (Join-Path $stage $obsolete)) {
                throw "Linux package contains obsolete .NET payload: $obsolete"
            }
        }
    }
    foreach ($file in $required) {
        if (-not (Test-Path -LiteralPath (Join-Path $stage $file))) { throw "Missing $file in $stage" }
    }
    foreach ($culture in @('de','en','es','fr','it','ja','ko','pt','zh-CN')) {
        if (-not (Test-Path -LiteralPath (Join-Path $stage (Join-Path $culture 'MediaUnseal.resources.dll')))) { throw "Missing localization $culture" }
    }
    $archiveBase = "MediaUnseal-$Version-" + $releaseTarget.Rid
    if ($releaseTarget.Suffix -eq 'win') {
        $root = "$archiveBase-portable"
        $archive = Join-Path $packages ("$root.zip")
        if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
        [System.IO.Compression.ZipFile]::CreateFromDirectory($stage,$archive,[System.IO.Compression.CompressionLevel]::Optimal,$true)
    } else {
        $root = $archiveBase
        $archive = Join-Path $packages ("$root.tar.gz")
        if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
        $fileStream = [System.IO.File]::Create($archive)
        try {
            $gzip = [System.IO.Compression.GZipStream]::new($fileStream,[System.IO.Compression.CompressionLevel]::Optimal,$true)
            try {
                $writer = [System.Formats.Tar.TarWriter]::new($gzip,$true)
                try {
                    foreach ($item in Get-ChildItem -LiteralPath $stage -Recurse -File) {
                        $relative = [System.IO.Path]::GetRelativePath($stage,$item.FullName).Replace('\','/')
                        $entry = [System.Formats.Tar.PaxTarEntry]::new([System.Formats.Tar.TarEntryType]::RegularFile,"$root/$relative")
                        $entry.Mode = if ($relative -in @('MediaUnseal','MediaUnseal.Updater','mucli','createdump')) { 493 } else { 420 }
                        $entry.DataStream = [System.IO.File]::OpenRead($item.FullName)
                        try { $writer.WriteEntry($entry) }
                        finally { $entry.DataStream.Dispose() }
                    }
                } finally { $writer.Dispose() }
            } finally { $gzip.Dispose() }
        } finally { $fileStream.Dispose() }
    }
    Get-Item -LiteralPath $archive | Select-Object FullName,Length
    Get-FileHash -LiteralPath $archive -Algorithm SHA256 | Select-Object Hash
}


