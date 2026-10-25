# SimOCR

[简体中文](README.md) | **English**

Screenshot → text and tables → Word / Excel. A Windows tray app: press `F1`, the screen freezes,
drag a rectangle around anything, and you get editable text, a **real** Excel table (numbers you can
sum and filter), a Word document, or a searchable PDF. Everything runs offline on your own machine.

[![Download for Windows](https://img.shields.io/badge/%E2%AC%87%EF%B8%8F_Download-Windows_installer-2EA043?style=for-the-badge&labelColor=1F883D)](https://github.com/Akitsuki4852/SimOCR/releases/latest)

> On the release page, open **Assets** and take `SimOCR-…-setup-win-x64.exe` (the version number
> changes; the `-setup-win-x64.exe` suffix does not). ~100 MB. No .NET, no Python, no admin rights.

## What it does

| You get | What it means |
| --- | --- |
| **A PNG on disk** | Press "Save" and the selection lands in your Downloads folder |
| **A pastable image** | Press "Copy", then `Ctrl+V` into Word / Slack / a browser |
| **Editable text** | Box some text → OCR → fix typos → copy it out |
| **A calculable table** | Box a table → rebuild it as a real Excel sheet, not a picture of one |
| **A pinned image** | Pin the selection on top of the screen to compare or copy from |

Here is a typical case — a purchase order like this one, exported to a table you can sum and filter
(synthetic sample shipped with the repo, no real customer data):

![Sample purchase order](docs/demo-table.png)

## Install

1. Download `SimOCR-…-setup-win-x64.exe` from [Releases](https://github.com/Akitsuki4852/SimOCR/releases/latest).
2. Double-click it. A small progress window appears; it installs per-user and starts the app.
   No .NET / Python / administrator rights required.
3. A tray icon appears. Press **`F1`**, drag a rectangle, then pick `TXT` / `DOCX` / `PDF` / `XLSX`,
   or pin / copy / save the image.

If `F1` is already taken by another app, SimOCR picks a working hotkey instead and tells you which
one (usually `Ctrl+Alt+A`). You can change it from the tray menu.

> **Unsigned build:** Windows SmartScreen will say "Windows protected your PC" on first run.
> Click **More info → Run anyway**. The installer is not code-signed yet; this is a known TODO.

## Privacy

All recognition happens locally: the OCR models ship inside the installer and the app makes **no
network calls** (no telemetry, no account, no upload). See `SECURITY.md` for the threat model.

## Build from source

Requires **Windows 10/11 x64** and the **.NET 10 SDK**. The OCR models (~21 MB) and the app-local
VC++ runtime DLLs are committed, so a fresh clone only needs a NuGet restore on the first build.

```powershell
git clone https://github.com/Akitsuki4852/SimOCR.git
cd SimOCR
dotnet build SimOCR.slnx -c Release
dotnet test  tests/SimOCR.Tests/SimOCR.Tests.csproj -c Release
```

Producing the installer / portable zip: `build/publish.ps1`. Architecture, test layout, self-test
switches and the release checklist are documented in [README_DEV.md](README_DEV.md) (Chinese only).
Contributions are welcome — see [CONTRIBUTING.md](CONTRIBUTING.md) and
[CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md); report vulnerabilities per [SECURITY.md](SECURITY.md).

## Limitations

- **Windows x64 only** (WPF + Win32 P/Invoke). The UI is Chinese; OCR targets simplified Chinese.
- OCR is PP-OCRv5 (PaddleOCR, Apache-2.0). Recognition quality depends on screenshot resolution —
  zoom in before capturing if a table comes out wrong.
- Table reconstruction is rule-based and configurable through YAML profiles; it is not a
  general-purpose layout model.

## License

[Apache-2.0](LICENSE). Third-party components and models are listed in [NOTICE](NOTICE).
The bundled Microsoft VC++ runtime DLLs remain under Microsoft's redistributable terms.
