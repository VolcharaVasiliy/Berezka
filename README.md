# Elochka Rewrite

Windows screen-region OCR translator focused on game overlays.

## Current stack
- WinForms tray app on .NET 7
- OCR: PaddleOCR via bundled Python worker
- Translation: local NLLB-200 distilled 600M via CTranslate2

## Project layout
- `Elochka.App/` - application code
- `scripts/` - local bootstrap and release scripts
- `release/` - packaged builds and archives

## Build
```powershell
dotnet build F:\Projects\elochka\Elochka.sln
```

## Portable release
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File F:\Projects\elochka\scripts\build_release.ps1
```

## Notes
- Large local runtime assets such as bundled Python, OCR cache, and offline models are not stored in git.
- The packaged release archive contains the runnable self-contained build.
