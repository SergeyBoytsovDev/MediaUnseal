param([string]$RelativePath = 'DEPENDENCIES.md', [string]$Version)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$project = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $Version) { $Version = ([xml](Get-Content (Join-Path $project 'MediaUnseal.csproj') -Raw)).Project.PropertyGroup.Version }
if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') { throw 'Invalid version.' }
$packages = Join-Path $project 'bin\release-packages'
if ([System.IO.Path]::IsPathRooted($RelativePath) -or
    $RelativePath.Replace('/', '\').Split('\') -contains '..') {
    throw 'Release entry must be a relative path within the staged package.'
}
$winStage = Join-Path (Join-Path $packages '_stage-win') $RelativePath
$linuxStage = Join-Path (Join-Path $packages '_stage-linux') $RelativePath
$zip = Join-Path $packages ("MediaUnseal-$Version-win-x64-portable.zip")
$tar = Join-Path $packages ("MediaUnseal-$Version-linux-x64.tar.gz")
$entryPath = $RelativePath.Replace('\', '/')
$zipEntry = ("MediaUnseal-$Version-win-x64-portable/") + $entryPath
$tarEntry = ("MediaUnseal-$Version-linux-x64/") + $entryPath

foreach ($path in @($winStage, $linuxStage, $zip, $tar)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Missing release input: $path" }
}

$zipTemporary = "$zip.new"
$tarTemporary = "$tar.new"
try {
    if ($RelativePath -eq 'DEPENDENCIES.md') {
        $document = Join-Path $project 'DEPENDENCIES.md'
        Copy-Item -LiteralPath $document -Destination $winStage -Force
        Copy-Item -LiteralPath $document -Destination $linuxStage -Force
    }

    Copy-Item -LiteralPath $zip -Destination $zipTemporary -Force
    $zipArchive = [System.IO.Compression.ZipFile]::Open($zipTemporary, [System.IO.Compression.ZipArchiveMode]::Update)
    try {
        $old = $zipArchive.GetEntry($zipEntry)
        if ($null -eq $old) { throw "Missing ZIP entry: $zipEntry" }
        $old.Delete()
        $replacement = $zipArchive.CreateEntry($zipEntry, [System.IO.Compression.CompressionLevel]::Optimal)
        $inputStream = [System.IO.File]::OpenRead($winStage)
        $outputStream = $replacement.Open()
        try { $inputStream.CopyTo($outputStream) }
        finally { $outputStream.Dispose(); $inputStream.Dispose() }
    }
    finally { $zipArchive.Dispose() }

    $source = [System.IO.File]::OpenRead($tar)
    $target = [System.IO.File]::Create($tarTemporary)
    try {
        $inputGzip = [System.IO.Compression.GZipStream]::new($source, [System.IO.Compression.CompressionMode]::Decompress)
        $outputGzip = [System.IO.Compression.GZipStream]::new($target, [System.IO.Compression.CompressionLevel]::Optimal)
        try {
            $reader = [System.Formats.Tar.TarReader]::new($inputGzip)
            $writer = [System.Formats.Tar.TarWriter]::new($outputGzip)
            try {
                $found = $false
                while ($null -ne ($entry = $reader.GetNextEntry())) {
                    $sourceStream = $entry.DataStream
                    if ($entry.Name -eq $tarEntry) {
                        $sourceStream = [System.IO.File]::OpenRead($linuxStage)
                        $found = $true
                    }
                    $buffer = $null
                    try {
                        if ($null -ne $sourceStream) {
                            $buffer = [System.IO.MemoryStream]::new()
                            $sourceStream.CopyTo($buffer)
                            $buffer.Position = 0
                            $entry.DataStream = $buffer
                        }
                        $writer.WriteEntry($entry)
                    }
                    finally {
                        if ($null -ne $buffer) { $buffer.Dispose() }
                        if ($entry.Name -eq $tarEntry) { $sourceStream.Dispose() }
                    }
                }
                if (-not $found) { throw "Missing TAR entry: $tarEntry" }
            }
            finally { $writer.Dispose(); $reader.Dispose() }
        }
        finally { $outputGzip.Dispose(); $inputGzip.Dispose() }
    }
    finally { $target.Dispose(); $source.Dispose() }

    $checkZip = [System.IO.Compression.ZipFile]::OpenRead($zipTemporary)
    try { if ($null -eq $checkZip.GetEntry($zipEntry)) { throw 'Updated ZIP is incomplete.' } }
    finally { $checkZip.Dispose() }
    & tar -tzf $tarTemporary | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Updated TAR is invalid.' }

    Move-Item -LiteralPath $zipTemporary -Destination $zip -Force
    Move-Item -LiteralPath $tarTemporary -Destination $tar -Force
}
finally {
    foreach ($temporary in @($zipTemporary, $tarTemporary)) {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
}
