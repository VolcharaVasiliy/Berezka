param(
    [string]$PythonExe = "F:\DevTools\Python311\python.exe",
    [string]$PaddleHome = "F:\Projects\elochka\.paddle-home",
    [string]$PaddlexCacheHome = "F:\Projects\elochka\.paddlex-cache",
    [string]$BootstrapDir = "F:\Projects\elochka\.tmp",
    [string]$PipCacheDir = "F:\DevTools\pip-cache"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $PythonExe))
{
    throw "Python runtime not found: $PythonExe"
}

New-Item -ItemType Directory -Force -Path $PaddleHome | Out-Null
New-Item -ItemType Directory -Force -Path $PaddlexCacheHome | Out-Null
New-Item -ItemType Directory -Force -Path $BootstrapDir | Out-Null
New-Item -ItemType Directory -Force -Path $PipCacheDir | Out-Null

$env:PIP_CACHE_DIR = $PipCacheDir
$env:PADDLE_HOME = $PaddleHome
$env:PADDLE_PDX_CACHE_HOME = $PaddlexCacheHome
$env:PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK = "True"

& $PythonExe -m pip install --disable-pip-version-check --upgrade --force-reinstall paddlepaddle==3.2.0 paddleocr==3.3.3

$bootstrapPath = Join-Path $BootstrapDir "elochka_bootstrap_paddle_ocr.py"
$bootstrapScript = @"
from paddleocr import PaddleOCR

ocr = PaddleOCR(
    lang="ru",
    use_doc_orientation_classify=False,
    use_doc_unwarping=False,
    use_textline_orientation=False,
)
print("paddle ocr ready")
"@

Set-Content -LiteralPath $bootstrapPath -Value $bootstrapScript -Encoding UTF8

try
{
    & $PythonExe $bootstrapPath
    if ($LASTEXITCODE -ne 0)
    {
        throw "PaddleOCR bootstrap failed with exit code $LASTEXITCODE."
    }
}
finally
{
    Remove-Item -LiteralPath $bootstrapPath -Force -ErrorAction SilentlyContinue
}

Write-Host "Local PaddleOCR environment is ready."
