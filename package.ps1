# Packages the read-only cooldown overlay as a self-contained Windows executable ZIP.
# Modeled on trade-watch/package.ps1 (staging + verification + atomic replace), minimal.
# Run with pwsh (Linux or Windows) or Windows PowerShell:
#   pwsh -File .\package.ps1
#   pwsh -File .\package.ps1 -SkipPublish
[CmdletBinding()]
param(
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"

$projectRoot = (Resolve-Path -LiteralPath $PSScriptRoot).Path
$projectPath = Join-Path $projectRoot "src/RefugeCooldownOverlay.App/RefugeCooldownOverlay.App.csproj"
$updaterProjectPath = Join-Path $projectRoot "src/RoAuras.Updater/RoAuras.Updater.csproj"
$publishParent = Join-Path $projectRoot "publish"
$publishPath = Join-Path $publishParent "win-x64"
$publishBuildPath = Join-Path $publishParent ".win-x64-publish"
$updaterPublishPath = Join-Path $publishParent ".roauras-updater-publish"
$transferPath = Join-Path $projectRoot "transfer"
$packageName = "roauras-win-x64"
$packagePath = Join-Path $transferPath $packageName
$archivePath = Join-Path $transferPath "$packageName.zip"
$archiveDirectory = Join-Path $transferPath "archive"
$archiveTempPath = Join-Path $transferPath ".$packageName.zip.tmp"
$appExeName = "RoAuras.exe"
$updaterExeName = "RoAurasUpdater.exe"
$updateConfigName = "roauras-update.json"
$dataFiles = @(
    "skill-catalog.json",
    "skill-mapping-evidence.json",
    "overlay-settings.json"
)
$updateConfig = @'
{
  "manifestUrl": "https://raw.githubusercontent.com/notengoideanise/roauras/main/roauras-update.json",
  "version": "0.1.2"
}
'@
$packageDocuments = @()

function Get-Sha256 {
    param([Parameter(Mandatory)][string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-RelativeFilePath {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$Path
    )

    return [IO.Path]::GetRelativePath($Root, $Path).Replace('\', '/')
}

function Get-FileManifest {
    param([Parameter(Mandatory)][string]$Root)

    $manifest = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    $files = @(Get-ChildItem -LiteralPath $Root -File -Recurse -Force | Sort-Object FullName)
    foreach ($file in $files) {
        $manifest.Add((Get-RelativeFilePath -Root $Root -Path $file.FullName), [PSCustomObject]@{
                Length = $file.Length
                Hash   = Get-Sha256 -Path $file.FullName
            })
    }

    return ,$manifest
}

function Get-PeInfo {
    param([Parameter(Mandatory)][string]$Path)

    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 0x40 -or $bytes[0] -ne 0x4d -or $bytes[1] -ne 0x5a) {
        throw "'$Path' is not a PE file with an MZ header."
    }

    $peOffset = [BitConverter]::ToInt32($bytes, 0x3c)
    if ($peOffset -lt 0 -or $peOffset + 24 -gt $bytes.Length) {
        throw "'$Path' has an invalid PE header offset."
    }

    if ($bytes[$peOffset] -ne 0x50 -or $bytes[$peOffset + 1] -ne 0x45 -or
        $bytes[$peOffset + 2] -ne 0 -or $bytes[$peOffset + 3] -ne 0) {
        throw "'$Path' is missing the PE signature."
    }

    $machine = [BitConverter]::ToUInt16($bytes, $peOffset + 4)
    $magic = [BitConverter]::ToUInt16($bytes, $peOffset + 24)
    return [PSCustomObject]@{
        Bytes                = $bytes
        Machine              = $machine
        Magic                = $magic
        SubsystemOffset      = $peOffset + 24 + 0x44
        Subsystem            = [BitConverter]::ToUInt16($bytes, $peOffset + 24 + 0x44)
    }
}

function Set-PeWindowsGuiSubsystem {
    param([Parameter(Mandatory)][string]$Path)

    $pe = Get-PeInfo -Path $Path
    if ($pe.Machine -ne 0x8664 -or $pe.Magic -ne 0x20b) {
        throw "'$Path' must be an x64 PE32+ executable before changing its subsystem."
    }

    if ($pe.Subsystem -eq 2) {
        return $pe.Subsystem
    }
    if ($pe.Subsystem -ne 3) {
        throw "'$Path' has unexpected PE subsystem $($pe.Subsystem); refusing to rewrite it."
    }

    $pe.Bytes[$pe.SubsystemOffset] = 2
    $pe.Bytes[$pe.SubsystemOffset + 1] = 0
    [IO.File]::WriteAllBytes($Path, $pe.Bytes)
    return 2
}

function Assert-PeWindowsGuiX64 {
    param([Parameter(Mandatory)][string]$Path)

    $pe = Get-PeInfo -Path $Path
    if ($pe.Machine -ne 0x8664) {
        throw "'$Path' is not x64 PE (machine 0x$('{0:X4}' -f $pe.Machine))."
    }
    if ($pe.Magic -ne 0x20b) {
        throw "'$Path' is not PE32+ (optional-header magic 0x$('{0:X4}' -f $pe.Magic))."
    }
    if ($pe.Subsystem -ne 2) {
        throw "'$Path' is not a Windows GUI executable (subsystem $($pe.Subsystem); expected 2)."
    }

    return $pe.Subsystem
}

function Copy-TreeContents {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination
    )

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    $children = @(Get-ChildItem -LiteralPath $Source -Force | Sort-Object Name)
    foreach ($child in $children) {
        Copy-Item -LiteralPath $child.FullName -Destination (Join-Path $Destination $child.Name) -Recurse -Force
    }
}

function New-DeterministicZip {
    param(
        [Parameter(Mandatory)][string]$SourceDirectory,
        [Parameter(Mandatory)][string]$DestinationZip,
        [Parameter(Mandatory)][string]$RootName
    )

    if (Test-Path -LiteralPath $DestinationZip) {
        Remove-Item -LiteralPath $DestinationZip -Force
    }

    $fixedTimestamp = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
    $fileStream = [IO.File]::Open($DestinationZip, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $archive = [IO.Compression.ZipArchive]::new($fileStream, [IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        $directories = @(Get-ChildItem -LiteralPath $SourceDirectory -Directory -Recurse -Force | Sort-Object FullName)
        foreach ($directory in $directories) {
            $relativePath = Get-RelativeFilePath -Root $SourceDirectory -Path $directory.FullName
            $entry = $archive.CreateEntry("$RootName/$relativePath/")
            $entry.LastWriteTime = $fixedTimestamp
        }

        $files = @(Get-ChildItem -LiteralPath $SourceDirectory -File -Recurse -Force | Sort-Object FullName)
        foreach ($file in $files) {
            $relativePath = Get-RelativeFilePath -Root $SourceDirectory -Path $file.FullName
            $entry = $archive.CreateEntry("$RootName/$relativePath", [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = $fixedTimestamp
            $inputStream = [IO.File]::OpenRead($file.FullName)
            $outputStream = $entry.Open()
            try {
                $inputStream.CopyTo($outputStream)
            }
            finally {
                $outputStream.Dispose()
                $inputStream.Dispose()
            }
        }
    }
    finally {
        $archive.Dispose()
        $fileStream.Dispose()
    }
}

function Get-ZipManifest {
    param([Parameter(Mandatory)][string]$Path)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $fileStream = [IO.File]::OpenRead($Path)
    $archive = [IO.Compression.ZipArchive]::new($fileStream, [IO.Compression.ZipArchiveMode]::Read, $true)
    try {
        $manifest = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
        foreach ($entry in $archive.Entries) {
            if (-not $entry.FullName.EndsWith('/')) {
                if ($manifest.ContainsKey($entry.FullName)) {
                    throw "ZIP contains duplicate file entry '$($entry.FullName)'."
                }

                $stream = $entry.Open()
                $sha256 = [Security.Cryptography.SHA256]::Create()
                try {
                    $entryHash = ([BitConverter]::ToString($sha256.ComputeHash($stream))).Replace('-', '').ToLowerInvariant()
                }
                finally {
                    $sha256.Dispose()
                    $stream.Dispose()
                }

                $manifest.Add($entry.FullName, [PSCustomObject]@{
                        Length = $entry.Length
                        Hash   = $entryHash
                    })
            }
        }

        return ,$manifest
    }
    finally {
        $archive.Dispose()
        $fileStream.Dispose()
    }
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "dotnet is required to publish the cooldown overlay."
}

New-Item -ItemType Directory -Path $transferPath -Force | Out-Null
foreach ($path in @($projectPath, $projectRoot, $transferPath)) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Required path does not exist: $path"
    }
}
foreach ($dataFile in $dataFiles) {
    $dataPath = Join-Path $projectRoot "data/$dataFile"
    if (-not (Test-Path -LiteralPath $dataPath -PathType Leaf)) {
        throw "Required data file does not exist: $dataPath"
    }
}

if (-not $SkipPublish) {
    if (Test-Path -LiteralPath $publishBuildPath) {
        Remove-Item -LiteralPath $publishBuildPath -Recurse -Force
    }
    New-Item -ItemType Directory -Path $publishParent -Force | Out-Null

    $publishArguments = @(
        "publish",
        $projectPath,
        "-c", "Release",
        "-r", "win-x64",
        "--self-contained", "true",
        "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:EnableWindowsTargeting=true",
        "-o", $publishBuildPath
    )
    & dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    # .NET 6 cannot customize a Windows apphost while publishing from Linux
    # (NETSDK1074), so WinExe otherwise remains a console-subsystem PE. The
    # published apphost has no signature or resource checksum; changing only
    # IMAGE_OPTIONAL_HEADER.Subsystem is the deterministic cross-publish fix
    # (same approach as trade-watch/package.ps1).
    $publishedAppPath = Join-Path $publishBuildPath $appExeName
    if (-not (Test-Path -LiteralPath $publishedAppPath -PathType Leaf)) {
        throw "dotnet publish did not produce $appExeName."
    }

    $null = Set-PeWindowsGuiSubsystem -Path $publishedAppPath

    if (Test-Path -LiteralPath $updaterPublishPath) { Remove-Item -LiteralPath $updaterPublishPath -Recurse -Force }
    & dotnet publish $updaterProjectPath -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableWindowsTargeting=true -o $updaterPublishPath
    if ($LASTEXITCODE -ne 0) { throw "updater publish failed with exit code $LASTEXITCODE." }
    $publishedUpdaterPath = Join-Path $updaterPublishPath $updaterExeName
    if (-not (Test-Path -LiteralPath $publishedUpdaterPath -PathType Leaf)) { throw "updater publish did not produce $updaterExeName." }
    $null = Set-PeWindowsGuiSubsystem -Path $publishedUpdaterPath
}

$publishSourcePath = if ($SkipPublish) { $publishPath } else { $publishBuildPath }
if (-not (Test-Path -LiteralPath $publishSourcePath)) {
    throw "Publish output is missing: $publishSourcePath"
}

$publishManifest = Get-FileManifest -Root $publishSourcePath
if ($publishManifest.Count -eq 0) {
    throw "Publish output is empty: $publishSourcePath"
}
if (-not $publishManifest.ContainsKey($appExeName)) {
    throw "Publish output is missing $appExeName."
}

$appPath = Join-Path $publishSourcePath $appExeName
$appSubsystem = Assert-PeWindowsGuiX64 -Path $appPath

if (-not $SkipPublish) {
    if (Test-Path -LiteralPath $publishPath) {
        Remove-Item -LiteralPath $publishPath -Recurse -Force
    }
    Move-Item -LiteralPath $publishBuildPath -Destination $publishPath
}

if (Test-Path -LiteralPath $packagePath) {
    Remove-Item -LiteralPath $packagePath -Recurse -Force
}
New-Item -ItemType Directory -Path $packagePath -Force | Out-Null
Copy-TreeContents -Source $publishPath -Destination $packagePath
$updaterSourcePath = Join-Path $updaterPublishPath $updaterExeName
if (-not (Test-Path -LiteralPath $updaterSourcePath)) { throw "Updater publish output is missing $updaterExeName." }
Copy-Item -LiteralPath $updaterSourcePath -Destination (Join-Path $packagePath $updaterExeName) -Force
Set-Content -LiteralPath (Join-Path $packagePath $updateConfigName) -Value $updateConfig -Encoding utf8
foreach ($dataFile in $dataFiles) {
    Copy-Item -LiteralPath (Join-Path $projectRoot "data/$dataFile") -Destination (Join-Path $packagePath $dataFile) -Force
}

# Skill icon art: packaged under icons/ keyed by catalog iconFile.
$iconsSource = Join-Path $projectRoot "data/icons"
if (-not (Test-Path -LiteralPath $iconsSource -PathType Container)) {
    throw "Icon directory is missing: $iconsSource (run tools/build_skill_catalog.py)"
}
$iconsDestination = Join-Path $packagePath "icons"
New-Item -ItemType Directory -Path $iconsDestination -Force | Out-Null
Copy-TreeContents -Source $iconsSource -Destination $iconsDestination
# No build sources or intermediate artifacts may ship.
$packageManifest = Get-FileManifest -Root $packagePath
foreach ($relativePath in $packageManifest.Keys) {
    if ($relativePath -match '(^|/)(obj|bin)(/|$)' -or $relativePath -match '\.(cs|csproj|sln|py|ahk)$') {
        throw "Package contains a build/source artifact: '$relativePath'."
    }
}
foreach ($required in @($dataFiles + $appExeName + $updaterExeName + $updateConfigName)) {
    if (-not $packageManifest.ContainsKey($required)) {
        throw "Package is missing required file '$required'."
    }
}
foreach ($relativePath in $publishManifest.Keys) {
    if (-not $packageManifest.ContainsKey($relativePath)) {
        throw "Publish file was not copied into the package: '$relativePath'."
    }

    $publishFile = $publishManifest[$relativePath]
    $packageFile = $packageManifest[$relativePath]
    if ($publishFile.Length -ne $packageFile.Length -or $publishFile.Hash -ne $packageFile.Hash) {
        throw "Publish file changed while staging the package: '$relativePath'."
    }
}

New-DeterministicZip -SourceDirectory $packagePath -DestinationZip $archiveTempPath -RootName $packageName
$zipManifest = Get-ZipManifest -Path $archiveTempPath
$expectedZipFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($relativePath in $packageManifest.Keys) {
    [void]$expectedZipFiles.Add("$packageName/$relativePath")
}
$actualZipFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($entryName in $zipManifest.Keys) {
    [void]$actualZipFiles.Add($entryName)
}
if (-not $expectedZipFiles.SetEquals($actualZipFiles)) {
    $missing = @($expectedZipFiles | Where-Object { -not $actualZipFiles.Contains($_) })
    $extra = @($actualZipFiles | Where-Object { -not $expectedZipFiles.Contains($_) })
    $missingText = if ($missing.Count -gt 0) { $missing -join ", " } else { "(none)" }
    $extraText = if ($extra.Count -gt 0) { $extra -join ", " } else { "(none)" }
    throw "ZIP file set differs from the staged package. Missing: $missingText. Extra: $extraText."
}
foreach ($relativePath in $packageManifest.Keys) {
    $entryName = "$packageName/$relativePath"
    $entry = $zipManifest[$entryName]
    $expectedFile = $packageManifest[$relativePath]
    if ($entry.Length -ne $expectedFile.Length -or $entry.Hash -ne $expectedFile.Hash) {
        throw "ZIP hash mismatch for '$entryName'."
    }
}

$archiveHash = Get-Sha256 -Path $archiveTempPath
$backupPath = $null
if (Test-Path -LiteralPath $archivePath) {
    $oldArchiveHash = Get-Sha256 -Path $archivePath
    if ($oldArchiveHash -ne $archiveHash) {
        New-Item -ItemType Directory -Path $archiveDirectory -Force | Out-Null
        $backupName = "$packageName-previous-$oldArchiveHash.zip"
        $backupPath = Join-Path $archiveDirectory $backupName
        if (-not (Test-Path -LiteralPath $backupPath)) {
            Copy-Item -LiteralPath $archivePath -Destination $backupPath
            $backupHash = Get-Sha256 -Path $backupPath
            Set-Content -LiteralPath "$backupPath.sha256" -Value "$backupHash  $backupName" -Encoding ascii
        }
        elseif ((Get-Sha256 -Path $backupPath) -ne $oldArchiveHash) {
            throw "Existing backup has the wrong hash: $backupPath"
        }
    }
}

if (Test-Path -LiteralPath $archivePath) {
    Remove-Item -LiteralPath $archivePath -Force
}
Move-Item -LiteralPath $archiveTempPath -Destination $archivePath
$archiveSidecar = "$archivePath.sha256"
Set-Content -LiteralPath $archiveSidecar -Value "$archiveHash  $(Split-Path -Leaf $archivePath)" -Encoding ascii

Write-Host "Published: $publishPath"
Write-Host ("Publish files validated: {0}" -f $publishManifest.Count)
Write-Host ("{0} PE machine: 0x{1:X4} (x64), subsystem: {2} (Windows GUI)" -f $appExeName, 0x8664, $appSubsystem)
Write-Host "Package staging: $packagePath"
Write-Host "Package ZIP: $archivePath"
Write-Host "Package SHA-256: $archiveHash"
Write-Host ("ZIP completeness: {0} files; every staged file hash matches" -f $packageManifest.Count)
if ($backupPath) {
    Write-Host "Previous ZIP backup: $backupPath"
}
Write-Host "Windows startup: not tested in this Linux environment"
