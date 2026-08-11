# 1. Project Overview

**Batch Convert to CHD** is a high-performance Windows desktop utility designed to streamline the conversion of various disk image formats into the **Compressed Hunks of Data (CHD)** format — the format used by MAME, and increasingly by emulation frontends for PlayStation, Dreamcast, and other systems.

Developed by [Pure Logic Code](https://www.purelogiccode.com), the application combines a modern WPF-UI dashboard with battle-tested MAME tooling (`chdman`) and pure-C# libraries (CHDSharp, CCDSharp, CSOSharp, PBPSharp) for a fully local, offline-capable conversion experience.

---

## 1.1 Key Features

### Modern Side-by-Side Dashboard
- **Dual-pane interface** — settings and file list on the left, real-time terminal-style log on the right.
- **Interactive file selection** — automatically scans folders; the user picks exactly which files to process via a detailed list with checkboxes.
- **Chunked file loading** — directory scans with thousands of files are loaded in chunks of 100 items at background priority to keep the UI responsive (`MainWindow.xaml.cs:820–975`).
- **Resizable layout** — built-in grid splitter between file explorer and log view.

### Multi-Architecture Support
- **Native x64 & ARM64** — `AppConfig.IsArm64` selects `chdman_arm64.exe`/`7za_arm64.exe` on ARM64 hardware, `chdman.exe`/`7za.exe` elsewhere (`AppConfig.cs:17–29`).

### Intelligent Conversion & Extraction
- **Automated batch processing** — convert entire directories with real-time progress, immediate cancellation, and per-file timeouts.
- **Recursive structure preservation** — the output folder mirrors the input folder's directory hierarchy (`PathUtils.GetSafeRelativePath`).
- **Robust extraction** — CHD → `.cue` (CD), `.iso` (DVD), `.gdi` (Dreamcast/Naomi), `.img` (HDD), with automatic metadata-based command detection via CHDSharp.
- **Archive integration** — `.zip`, `.7z`, `.rar` are extracted and processed transparently (SharpCompress, with a `7za.exe` fallback).
- **CloneCD support** — `.ccd` sets are parsed by CCDSharp and converted via an auto-generated CUE/BIN.
- **CSO decompression** — `.cso`/`.ciso` via CSOSharp (deflate/zlib and LZ4).
- **PBP extraction** — PlayStation `.pbp` via PBPSharp; PSP-homebrew-style files (no PlayStation disc image) are detected and skipped with a clear message.
- **Smart CUE normalization** — encoding detection (UTF-8, Shift-JIS, Korean CP949, Cyrillic CP1251, Latin-1, …), UTF-8 BOM stripping, case-insensitive and zero-padding-tolerant reference resolution, canonicalization into a self-contained work directory.
- **Archive dependency validation** — cue/GDI/TOC entries extracted from archives are validated up front; entries with missing referenced files are skipped with a warning instead of failing inside chdman.
- **MP3 audio track support** — cue/MP3 sets are decoded to chdman-compatible WAV (44.1 kHz, 16-bit, stereo) automatically, with a built-in decoder fallback.
- **bin-only archives** — archives containing only `.bin` files get an auto-generated MODE2/2352 cue (with MODE1/2352 fallback) and convert automatically.

### Integrity, Safety & Verification
- **Safe deletion** — source files (and dependencies such as `.bin`, `.sub`) are only deleted after confirmed success.
- **Batch verification** — checksums and structural integrity of existing CHD files via CHDSharp.
- **Automated organization** — optionally move verified/failed files into `Success`/`Failed` subfolders; these folders are excluded from subsequent scans.
- **Empty-folder cleanup** — empty subdirectories are removed after files are moved or deleted.
- **Dependency check at startup** — the user is notified if `chdman.exe` is missing.
- **File-system monitoring** — the input folder is watched during batch processing to explain why a file went missing mid-operation.
- **Corrupt-image early warning** — ISO sizes that don't match any standard sector layout are flagged before conversion.
- **Resilient file operations** — deletions and moves retry with backoff (~45 s) against transient locks (antivirus, indexer) and clear read-only attributes when needed.

### Performance & UI
- **Real-time telemetry** — disk write/read speeds and elapsed time during operations.
- **High-performance logging** — Serilog with UI log truncation at 100,000 characters.
- **WPF-UI theming** — dark Fluent theme with Mica backdrop and rounded corners on Windows 11.

### Updates & Stability
- **Automatic update checks** — GitHub releases are checked at startup; the user is offered the download page.
- **Automated bug reporting** — warning-and-above log events are forwarded to the PureLogicCode BugReport API (see [Bug Reporting System](09-bug-reporting.md)).

---

## 1.2 Supported Formats

| Category | Formats |
|----------|---------|
| **Standard images** | `.iso`, `.cue` (+`.bin`), `.img`, `.ccd` (+`.img`), `.raw`, `.toc` |
| **Console-specific** | `.gdi` (Dreamcast), `.pbp` (PlayStation) |
| **Compressed** | `.cso` (Compressed ISO) |
| **Archives** | `.zip`, `.7z`, `.rar` |
| **Output** | `.chd` |

The full input set is defined in `FileExtensions.AllSupportedInputExtensionsForConversion` (`Utilities/FileExtensions.cs:39–42`): `.cue`, `.iso`, `.img`, `.gdi`, `.toc`, `.raw`, `.ccd`, `.zip`, `.7z`, `.rar`, `.cso`, `.pbp`. All extension checks are case-insensitive.

---

## 1.3 Technical Logic (Command Selection)

The application implements priority-based logic to pick the right `chdman` command (`MainWindow.xaml.cs:2464–2475`):

1. **`.iso` (DVD images)** → `createdvd`
2. **`.cue` / `.gdi` / `.toc` (multi-track images)** → `createcd`
3. **`.img` (hard disk images)** → `createhd`, unless an accompanying `.cue` exists → `createcd`
4. **`.raw` (raw data)** → `createraw` (with an explicit unit size `-us 2352`)
5. **`.pbp`** → extracted to CUE/BIN via PBPSharp, then `createcd`
6. **`.ccd`** → converted to CUE/BIN via CCDSharp, then `createcd`

The user can override 1–4 via **Force CD** / **Force DVD** checkboxes. PBP always extracts first.

---

## 1.4 Project History Highlights

- Migrated from external `chdman`-based verification to the pure C# **CHDSharp** library.
- Replaced `maxcso` and `psxpackager` executables with the in-house **CSOSharp** and **PBPSharp** libraries.
- Added CloneCD support via **CCDSharp**.
- Introduced CUE normalization, MP3 decoding, archive dependency validation, and a file watcher for missing-file diagnostics.
- Version 3.4.0 is the current release (AssemblyVersion `3.4.0`).
