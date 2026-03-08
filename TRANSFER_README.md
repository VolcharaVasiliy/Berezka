# Elochka Transfer

## What Is Included
- `Projects\elochka` - full source tree, scripts, models, OCR cache, current build output, logs, and solution files.
- `DevTools\Python311` - portable Python runtime with the installed `ctranslate2`, `transformers`, `paddleocr`, and `paddlepaddle` packages used by the app.

## Recommended Restore Layout
- Extract the archive to the root of `F:\`.
- After extraction, the main paths should be:
  - `F:\Projects\elochka`
  - `F:\DevTools\Python311`

## Run
- App executable:
  - `F:\Projects\elochka\Elochka.App\bin\Debug\net7.0-windows10.0.19041.0\Elochka.App.exe`

## Build
- Solution:
  - `F:\Projects\elochka\Elochka.sln`
- Example:
```powershell
dotnet build F:\Projects\elochka\Elochka.sln
```

## If You Extract Elsewhere
- Set these environment variables or adjust `settings.ini`:
  - `ELOCHKA_PYTHON`
  - `ELOCHKA_OFFLINE_MODEL`
  - `ELOCHKA_PADDLE_HOME`
  - `ELOCHKA_PADDLEX_CACHE_HOME`

## Notes
- The app expects Windows.
- The current local translation model is `NLLB-200 distilled 600M`.
- The current OCR stack is `PaddleOCR` on the bundled portable Python.
