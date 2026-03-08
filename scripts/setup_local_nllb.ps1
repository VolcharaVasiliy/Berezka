param(
    [string]$PythonExe = "F:\DevTools\Python311\python.exe",
    [string]$SourceDir = "F:\Projects\elochka\.hf-source\nllb-200-distilled-600m",
    [string]$ModelDir = "F:\Projects\elochka\Models\nllb-200-distilled-600m-ctranslate2",
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

& $PythonExe -m pip install --disable-pip-version-check ctranslate2 sentencepiece transformers huggingface-hub protobuf safetensors
& $PythonExe -m pip install --disable-pip-version-check torch --index-url https://download.pytorch.org/whl/cpu

if (-not (Test-Path -LiteralPath (Join-Path $SourceDir "config.json")))
{
    $bootstrapPath = Join-Path $env:TEMP "elochka_bootstrap_local_nllb.py"
    $bootstrapScript = @"
from huggingface_hub import snapshot_download

snapshot_download(
    repo_id="facebook/nllb-200-distilled-600M",
    local_dir=r"$SourceDir",
    cache_dir=r"$CacheDir",
    allow_patterns=[
        "config.json",
        "generation_config.json",
        "pytorch_model.bin",
        "sentencepiece.bpe.model",
        "special_tokens_map.json",
        "tokenizer.json",
        "tokenizer_config.json",
    ],
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
    --copy_files generation_config.json sentencepiece.bpe.model special_tokens_map.json tokenizer.json tokenizer_config.json `
    --quantization int8 `
    --force

if ($LASTEXITCODE -ne 0)
{
    throw "NLLB conversion failed with exit code $LASTEXITCODE"
}

if (-not (Test-Path -LiteralPath (Join-Path $ModelDir "model.bin")))
{
    throw "Converted model is incomplete: $ModelDir"
}

Write-Host "Local NLLB environment is ready."
