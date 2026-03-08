param(
    [string]$PythonExe = "F:\DevTools\Python311\python.exe",
    [string]$SourceDir = "F:\Projects\elochka\.hf-source\opus-mt-en-ru",
    [string]$ModelDir = "F:\Projects\elochka\Models\opus-mt-en-ru-ctranslate2",
    [string]$CacheDir = "F:\Projects\elochka\.hf-home",
    [string]$PipCacheDir = "F:\DevTools\pip-cache"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $PythonExe))
{
    throw "Python runtime not found: $PythonExe"
}

$converterExe = "F:\DevTools\Python311\Scripts\ct2-transformers-converter.exe"
if (-not (Test-Path -LiteralPath $converterExe))
{
    throw "CTranslate2 converter not found: $converterExe"
}

New-Item -ItemType Directory -Force -Path $SourceDir | Out-Null
New-Item -ItemType Directory -Force -Path $ModelDir | Out-Null
New-Item -ItemType Directory -Force -Path $CacheDir | Out-Null
New-Item -ItemType Directory -Force -Path $PipCacheDir | Out-Null

$env:PIP_CACHE_DIR = $PipCacheDir
$env:HF_HOME = $CacheDir
$env:HF_HUB_DISABLE_SYMLINKS_WARNING = "1"

& $PythonExe -m pip install --disable-pip-version-check ctranslate2 sentencepiece transformers huggingface-hub protobuf sacremoses
& $PythonExe -m pip install --disable-pip-version-check torch --index-url https://download.pytorch.org/whl/cpu

if (-not (Test-Path -LiteralPath (Join-Path $SourceDir "pytorch_model.bin")))
{
    $bootstrapPath = Join-Path $env:TEMP "elochka_bootstrap_local_mt.py"
    $bootstrapScript = @"
from huggingface_hub import snapshot_download

snapshot_download(
    repo_id="Helsinki-NLP/opus-mt-en-ru",
    local_dir=r"$SourceDir",
    cache_dir=r"$CacheDir",
)
print("source model ready")
"@

    Set-Content -LiteralPath $bootstrapPath -Value $bootstrapScript -Encoding UTF8

    try
    {
        & $PythonExe $bootstrapPath
    }
    finally
    {
        Remove-Item -LiteralPath $bootstrapPath -Force -ErrorAction SilentlyContinue
    }
}

if (Test-Path -LiteralPath $ModelDir)
{
    Remove-Item -LiteralPath $ModelDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $ModelDir | Out-Null

& $converterExe `
    --model $SourceDir `
    --output_dir $ModelDir `
    --copy_files README.md generation_config.json tokenizer_config.json vocab.json source.spm .gitattributes target.spm `
    --quantization int8 `
    --force

if (-not (Test-Path -LiteralPath (Join-Path $ModelDir "model.bin")))
{
    throw "Converted model is incomplete: $ModelDir"
}

Write-Host "Local OPUS-MT environment is ready."
