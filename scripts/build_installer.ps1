[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [string]$Tag = "v0.1.0",
    [string]$RuntimeIdentifier = "win-x64",
    [string]$Owner = "VolcharaVasiliy",
    [string]$Repository = "Berezka",
    [string]$ReleaseArchivePath,
    [string]$DownloadUrl,
    [string]$SevenZipExe = "F:\DevTools\ZipTools\7zip\7z.exe",
    [string]$SevenZipDll = "F:\DevTools\ZipTools\7zip\7z.dll",
    [string]$Aria2ZipUrl = "https://github.com/aria2/aria2/releases/download/release-1.37.0/aria2-1.37.0-win-64bit-build1.zip"
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $PSCommandPath
if ([string]::IsNullOrWhiteSpace($ProjectRoot))
{
    $ProjectRoot = Split-Path -Parent $scriptDir
}

function Ensure-FileExists
{
    param([string]$Path, [string]$Label)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf))
    {
        throw "$Label not found: $Path"
    }
}

function Reset-Directory
{
    param([string]$Path)

    if (Test-Path -LiteralPath $Path)
    {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }

    New-Item -ItemType Directory -Path $Path | Out-Null
}

$installerProjectRoot = Join-Path $ProjectRoot "Berezka.Installer"
$installerProjectPath = Join-Path $installerProjectRoot "Berezka.Installer.csproj"
$installerToolsRoot = Join-Path $installerProjectRoot "Tools"
$installerCacheRoot = Join-Path $ProjectRoot ".installer-cache"
$downloadCacheRoot = Join-Path $installerCacheRoot "downloads"
$releaseRoot = Join-Path $ProjectRoot "release"
$installerPublishRoot = Join-Path $releaseRoot "installer-publish"
$manifestPath = Join-Path $installerProjectRoot "installer-manifest.json"
$finalInstallerPath = Join-Path $releaseRoot "Berezka.Setup-$Tag-$RuntimeIdentifier.exe"

Ensure-FileExists -Path $installerProjectPath -Label "Installer project"
Ensure-FileExists -Path $SevenZipExe -Label "7z executable"
Ensure-FileExists -Path $SevenZipDll -Label "7z library"

if ([string]::IsNullOrWhiteSpace($ReleaseArchivePath))
{
    $candidateArchives = Get-ChildItem -Path $releaseRoot -File -Filter "berezka-$Tag-$RuntimeIdentifier*_7z_lzma2_mx5_solid.7z"
    if ($candidateArchives.Count -ne 1)
    {
        throw "Expected exactly one release archive for tag $Tag in $releaseRoot."
    }

    $ReleaseArchivePath = $candidateArchives[0].FullName
}

Ensure-FileExists -Path $ReleaseArchivePath -Label "Release archive"

$releaseArchiveItem = Get-Item -LiteralPath $ReleaseArchivePath
$releaseArchiveHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $ReleaseArchivePath).Hash

if ([string]::IsNullOrWhiteSpace($DownloadUrl))
{
    $DownloadUrl = "https://github.com/$Owner/$Repository/releases/download/$Tag/$($releaseArchiveItem.Name)"
}

New-Item -ItemType Directory -Force -Path $downloadCacheRoot | Out-Null
Reset-Directory -Path $installerToolsRoot

$sevenZipToolRoot = Join-Path $installerToolsRoot "7zip"
New-Item -ItemType Directory -Force -Path $sevenZipToolRoot | Out-Null
Copy-Item -LiteralPath $SevenZipExe -Destination (Join-Path $sevenZipToolRoot "7z.exe") -Force
Copy-Item -LiteralPath $SevenZipDll -Destination (Join-Path $sevenZipToolRoot "7z.dll") -Force

$aria2ZipPath = Join-Path $downloadCacheRoot ([System.IO.Path]::GetFileName($Aria2ZipUrl))
if (-not (Test-Path -LiteralPath $aria2ZipPath -PathType Leaf))
{
    Invoke-WebRequest -Uri $Aria2ZipUrl -OutFile $aria2ZipPath
}

$aria2ExtractRoot = Join-Path $installerCacheRoot "aria2-extract"
Reset-Directory -Path $aria2ExtractRoot
& $SevenZipExe x $aria2ZipPath "-o$aria2ExtractRoot" -y | Out-Null
if ($LASTEXITCODE -ne 0)
{
    throw "Failed to extract aria2 package."
}

$aria2Exe = Get-ChildItem -Path $aria2ExtractRoot -Recurse -Filter "aria2c.exe" | Select-Object -First 1
if (-not $aria2Exe)
{
    throw "aria2c.exe was not found inside $aria2ZipPath"
}

$aria2ToolRoot = Join-Path $installerToolsRoot "aria2"
New-Item -ItemType Directory -Force -Path $aria2ToolRoot | Out-Null
Copy-Item -LiteralPath $aria2Exe.FullName -Destination (Join-Path $aria2ToolRoot "aria2c.exe") -Force

$manifest = [ordered]@{
    productName = "Berezka"
    versionTag = $Tag
    downloadUrl = $DownloadUrl
    archiveName = $releaseArchiveItem.Name
    archiveSizeBytes = $releaseArchiveItem.Length
    archiveSha256 = $releaseArchiveHash
    mainExecutableRelativePath = "Berezka.App.exe"
    shortcutName = "Berezka"
    defaultInstallSubdirectory = "Berezka"
    releasePageUrl = "https://github.com/$Owner/$Repository/releases/tag/$Tag"
} | ConvertTo-Json

Set-Content -LiteralPath $manifestPath -Value $manifest -Encoding UTF8

Reset-Directory -Path $installerPublishRoot
if (Test-Path -LiteralPath $finalInstallerPath)
{
    Remove-Item -LiteralPath $finalInstallerPath -Force
}

dotnet publish $installerProjectPath `
    -c Release `
    -r $RuntimeIdentifier `
    --self-contained true `
    /p:PublishSingleFile=true `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    /p:DebugType=None `
    /p:DebugSymbols=false `
    -o $installerPublishRoot

if ($LASTEXITCODE -ne 0)
{
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$publishedInstaller = Join-Path $installerPublishRoot "Berezka.Setup.exe"
Ensure-FileExists -Path $publishedInstaller -Label "Published installer"
Copy-Item -LiteralPath $publishedInstaller -Destination $finalInstallerPath -Force

Write-Host "Installer ready:"
Write-Host $finalInstallerPath
