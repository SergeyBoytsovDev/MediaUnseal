#Requires -Version 7.0
param([string]$Version)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$project = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $Version) { $Version = ([xml](Get-Content (Join-Path $project 'MediaUnseal.csproj') -Raw)).Project.PropertyGroup.Version }
if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') { throw 'Invalid version.' }
$packages = Join-Path $project 'bin\release-packages'
New-Item -ItemType Directory -Path $packages -Force | Out-Null
$runtimeVersion = '10.0.14'
foreach ($target in @(@{Rid='win-x64';Suffix='win';Apphost='MediaUnseal.exe';Helper='MediaUnseal.Updater.exe'}, @{Rid='linux-x64';Suffix='linux';Apphost='MediaUnseal';Helper='MediaUnseal.Updater'})) {
    $publish = Join-Path $packages ("publish-" + $target.Rid + "-" + $Version)
    $stage = Join-Path $packages $(if ($target.Suffix -eq 'win') { "MediaUnseal-$Version-win-x64-portable" } else { "_stage-linux-$Version" })
    $expectedParent = [System.IO.Path]::GetFullPath($packages).TrimEnd('\') + '\'
    foreach ($path in @($publish,$stage)) {
        $resolved = [System.IO.Path]::GetFullPath($path)
        if (-not $resolved.StartsWith($expectedParent,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe stage path.' }
        if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    }
    & dotnet publish (Join-Path $project 'MediaUnseal.csproj') -c Release -r $target.Rid --self-contained true -o $publish
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $($target.Rid)" }
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    Get-ChildItem -LiteralPath $publish -Force | Where-Object { $_.Extension -ne '.pdb' } | Copy-Item -Destination $stage -Recurse -Force
    foreach ($file in @('LICENSE.md','DEPENDENCIES.md','THIRD-PARTY-NOTICES.md')) {
        Copy-Item -LiteralPath (Join-Path $project $file) -Destination $stage -Force
    }
    $licenseDir = Join-Path $stage ("licenses\dotnet-" + $runtimeVersion)
    New-Item -ItemType Directory -Path $licenseDir -Force | Out-Null
    $pack = Join-Path $env:USERPROFILE (".nuget\packages\microsoft.netcore.app.runtime." + $target.Rid + "\" + $runtimeVersion)
    Copy-Item -LiteralPath (Join-Path $pack 'LICENSE.TXT') -Destination (Join-Path $licenseDir 'RUNTIME-LICENSE.TXT')
    Copy-Item -LiteralPath (Join-Path $pack 'THIRD-PARTY-NOTICES.TXT') -Destination (Join-Path $licenseDir 'RUNTIME-THIRD-PARTY-NOTICES.TXT')
    if ($target.Suffix -eq 'win') {
        Copy-Item -LiteralPath (Join-Path $project 'packaging\licenses\WINDOWS-DOTNET-LIBRARY-LICENSE.txt') -Destination $licenseDir
    }
    foreach ($file in @($target.Apphost,$target.Helper,'MediaUnseal.dll','MediaUnseal.deps.json','hostfxr.dll')) {
        if ($file -eq 'hostfxr.dll' -and $target.Suffix -eq 'linux') { continue }
        if (-not (Test-Path -LiteralPath (Join-Path $stage $file))) { throw "Missing $file in $stage" }
    }
    foreach ($culture in @('de','en','es','fr','it','ja','ko','pt','zh-CN')) {
        if (-not (Test-Path -LiteralPath (Join-Path $stage (Join-Path $culture 'MediaUnseal.resources.dll')))) { throw "Missing localization $culture" }
    }
    $archiveBase = "MediaUnseal-$Version-" + $target.Rid
    if ($target.Suffix -eq 'win') {
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
                        $entry.Mode = if ($relative -in @('MediaUnseal','MediaUnseal.Updater','createdump')) { 493 } else { 420 }
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


