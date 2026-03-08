[CmdletBinding()]
param(
    [string]$Version = "v0.1.0",
    [string]$RuntimeIdentifier = "win-x64"
)

$ErrorActionPreference = "Stop"

$projectRoot = "F:\Projects\elochka"
$appProject = Join-Path $projectRoot "Elochka.App\Elochka.App.csproj"
$releaseRoot = Join-Path $projectRoot "release"
$publishRoot = Join-Path $releaseRoot "publish"
$packageName = "elochka-$Version-$RuntimeIdentifier"
$packageRoot = Join-Path $releaseRoot $packageName
$archivePath = Join-Path $releaseRoot "$packageName`_7z_lzma2_mx5_solid.7z"
$zipExe = "F:\DevTools\ZipTools\7zip\7z.exe"
$pythonSource = "F:\DevTools\Python311"
$modelSource = "F:\Projects\elochka\Models\nllb-200-distilled-600m-ctranslate2"
$paddlexSource = "F:\Projects\elochka\.paddlex-cache\official_models"

function Reset-Directory {
    param([string]$Path)

    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }

    New-Item -ItemType Directory -Path $Path | Out-Null
}

function Copy-DirectoryContent {
    param(
        [string]$Source,
        [string]$Destination
    )

    if (-not (Test-Path -LiteralPath $Source)) {
        throw "Required path not found: $Source"
    }

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Copy-Item -Path (Join-Path $Source "*") -Destination $Destination -Recurse -Force
}

function Write-ReleaseSettings {
    param([string]$Path)

    $content = @"
[General]
HotKey=0
Font=Tahoma; 12pt
Color=0
Paused=0

[Translation]
Enabled=1
Provider=3
SourceLanguage=en
TargetLanguage=ru
Endpoint=
ApiKey=
FolderId=
YandexCredentialMode=0
OfflineModelPath=
OfflinePythonPath=
"@

    Set-Content -Path $Path -Value $content -Encoding UTF8
}

function Write-ReleaseReadme {
    param([string]$Path)

    $content = @"
# Elochka Release

## Что внутри
- `Elochka.App.exe` - основной исполняемый файл.
- `python\` - встроенный Python runtime для локального OCR и перевода.
- `offline-models\` - локальная модель перевода.
- `paddlex-cache\official_models\` - локальные OCR-модели PaddleOCR.

## Запуск
1. Распакуйте папку целиком в любое место на диске.
2. Запустите `Elochka.App.exe`.
3. При первом старте приложение создаст `settings.ini` рядом с exe, если файла ещё нет.

## Требования
- Windows 10 x64 или новее.
- Никакие отдельные установки Python/.NET не нужны.

## Замечания
- Не удаляйте подпапки `python`, `offline-models` и `paddlex-cache`.
- Приложение работает полностью локально в режиме `LocalNllb`.
"@

    Set-Content -Path $Path -Value $content -Encoding UTF8
}

Write-Host "Stopping running Elochka.App processes..."
Get-Process -Name "Elochka.App" -ErrorAction SilentlyContinue | Stop-Process -Force

Write-Host "Preparing release directories..."
New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
Reset-Directory -Path $publishRoot
Reset-Directory -Path $packageRoot

Write-Host "Publishing self-contained .NET app..."
dotnet publish $appProject `
    -c Release `
    -r $RuntimeIdentifier `
    --self-contained true `
    /p:PublishSingleFile=false `
    /p:PublishReadyToRun=true `
    /p:DebugType=None `
    /p:DebugSymbols=false `
    -o $publishRoot

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

Write-Host "Copying published app files..."
Copy-DirectoryContent -Source $publishRoot -Destination $packageRoot

Write-Host "Copying bundled Python runtime..."
$pythonDestination = Join-Path $packageRoot "python"
New-Item -ItemType Directory -Path $pythonDestination -Force | Out-Null

$pythonRootFiles = @(
    "python.exe",
    "pythonw.exe",
    "python3.dll",
    "python311.dll",
    "vcruntime140.dll",
    "vcruntime140_1.dll",
    "LICENSE.txt"
)

foreach ($fileName in $pythonRootFiles) {
    $sourceFile = Join-Path $pythonSource $fileName
    if (Test-Path -LiteralPath $sourceFile) {
        Copy-Item -LiteralPath $sourceFile -Destination (Join-Path $pythonDestination $fileName) -Force
    }
}

$pythonDirectories = @(
    "DLLs",
    "Lib",
    "Scripts"
)

foreach ($directoryName in $pythonDirectories) {
    Copy-DirectoryContent -Source (Join-Path $pythonSource $directoryName) -Destination (Join-Path $pythonDestination $directoryName)
}

Write-Host "Copying offline translation model..."
$offlineModelsRoot = Join-Path $packageRoot "offline-models"
Copy-DirectoryContent -Source $modelSource -Destination (Join-Path $offlineModelsRoot "nllb-200-distilled-600m-ctranslate2")

Write-Host "Copying PaddleOCR cached models..."
$paddlexDestination = Join-Path $packageRoot "paddlex-cache\official_models"
Copy-DirectoryContent -Source $paddlexSource -Destination $paddlexDestination
New-Item -ItemType Directory -Path (Join-Path $packageRoot "paddle-home") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $packageRoot "ocr-cache") -Force | Out-Null

Write-Host "Writing default runtime files..."
Write-ReleaseSettings -Path (Join-Path $packageRoot "settings.ini")
Write-ReleaseReadme -Path (Join-Path $packageRoot "README.txt")

if (Test-Path -LiteralPath $archivePath) {
    Remove-Item -LiteralPath $archivePath -Force
}

Write-Host "Creating archive..."
& $zipExe a -t7z $archivePath $packageRoot "-m0=lzma2" "-mx=5" "-ms=on" | Out-Null

if ($LASTEXITCODE -ne 0) {
    throw "7z packaging failed with exit code $LASTEXITCODE"
}

Write-Host "Release package ready:"
Write-Host $packageRoot
Write-Host $archivePath
