# 8. Utilities Reference

All classes live in `BatchConvertToCHD/Utilities/` (and `Models/` where noted).

---

## 8.1 PathUtils

`internal static class PathUtils` (`PathUtils.cs:12`)

| Member | Behavior |
|--------|----------|
| `SanitizeFileName(name)` | Replaces invalid filename chars with `_`; collapses a trailing period to `_`; falls back to a GUID when the result is empty/all underscores. |
| `GetSafeTempFileName(original, desiredExt, tempDir)` | Sanitized base name + desired extension (leading dot stripped), combined under `tempDir`. |
| `GetSafeRelativePath(relativeTo, path)` | `Path.GetRelativePath` when both paths share a root; otherwise `"."` (same folder). Used to preserve the directory structure in outputs. |
| `GetBestTempDirectory(inputFilePath, outputFolderPath, tempDirPrefix, requiredBytes)` | Selects the best temp root: candidates = input-file root, output-folder root, system temp root, and every ready fixed drive. Requires ≥ 1 GiB free; when `requiredBytes > 0` prefers a drive with enough free space (most free among those); probes writability (create+delete a `writetest_<guid>` dir); falls back to system temp with an informational log. Base path is `{root}\BatchConvertToCHD_Temp` unless it equals the system temp root. Final: `{base}\{tempDirPrefix}{guid}`. |
| `GetPossibleTempBasePaths()` | System temp plus every existing `X:\BatchConvertToCHD_Temp` on fixed drives — used by startup cleanup. |
| `ValidateAndNormalizePath(path, pathName, onLog, onError)` | `GetFullPath` + existence check with friendly errors. |

---

## 8.2 CueNormalizer & CueWorkDirectory

### CueNormalizer (`CueNormalizer.cs`)

`internal static class CueNormalizer` — produces a canonical, chdman-safe cue.

- `NormalizeAsync(cuePath, token, transform?)`:
  1. Reads lines with `GameFileParser.ReadLinesWithDetectedEncodingAsync` (encoding + BOM detection).
  2. Processes only lines starting with `FILE ` (case-insensitive); everything else passes through verbatim.
  3. Resolves each reference: **exact case-sensitive** match → **case-insensitive** match → **zero-padding-tolerant** match (`(Track 2)` ↔ `(Track 02)`/`(Track 002)` via `TrackNumberRegex`).
  4. Applies an optional transform (used to rewrite MP3 → WAV names and track types).
  5. Compares the canonical line with the original; flags `NeedsRewrite`/`ReferencesChanged`.
- `WriteCanonicalCueAsync` — writes **UTF-8 without BOM**, CRLF line endings.
- `GetTrackType` — token after the last quote, matched against `[BINARY, WAVE, MP3, AIFF, MOTOROLA, AUDIO]` (case-insensitive), upper-cased; tolerates cdrdao TOC extra columns.
- Result model `CueNormalizationResult` carries `SourceEncoding`, `HasBom`, `References`, `UnresolvedNames`, `CanonicalLines`, `NeedsRewrite`, `ReferencesChanged`, `CanonicalCueText`.
- Reference model `CueFileReference(ReferencedName, ResolvedName, FullPath, TrackType, WasNameCorrected)`.

### CueWorkDirectory (`CueWorkDirectory.cs`)

`internal static class CueWorkDirectory` — builds a self-contained ASCII work directory when the cue can't be handed to chdman as-is.

- `PrepareAsync(cuePath, tempDirPrefix, mp3Decoder?, onLog?, token)` → `CueWorkDirectoryResult(WorkCuePath, WorkDir, UnresolvedNames)`:
  - No work needed (UTF-8, no BOM, ASCII names, no corrections, no MP3) → `(null, null, [])`.
  - **BOM-only fast path**: writes a BOM-free canonical `game.cue` into the work dir that references bins **in place via relative paths** — no bin copies. Declined when any bin is on another drive.
  - Full path: copies every referenced file under safe `trackNN.ext` names (MP3 tracks decoded to `trackNN.wav` via the MP3 decoder, track type rewritten to `WAVE`), then writes the canonical cue.
  - Unresolved references → returned in `UnresolvedNames` (caller skips conversion).
  - On failure the work dir is deleted and the exception rethrown.
- `TryWriteInPlaceWorkCueAsync` — the fast path above; `CopyWithRetryAsync` copies bins with up to 4 attempts (300 ms × attempt backoff).

### Why this exists

chdman's cue parser does **not** skip a UTF-8 BOM — the first token becomes `"\uFEFFFILE"` and chdman reports `couldn't find bin file []` even when every bin exists. Non-UTF-8 text (Korean/Cyrillic), non-ASCII names/paths, and zero-padding name mismatches produce the same class of failure. Normalization + work directories eliminate all of them.

---

## 8.3 GameFileParser

`internal static class GameFileParser` (`GameFileParser.cs:11`)

- `GetReferencedFilesFromCueAsync` / `FromGdiAsync` / `FromTocAsync` — extract referenced file names from descriptors:
  - **cue/toc**: lines starting with `FILE `; quoted or unquoted names; the last space-delimited token is stripped when it is a known track type.
  - **gdi**: skips line 0 (track-count header); quoted names between first/last quote; unquoted lines need ≥ 5 whitespace parts, with names spanning parts 4..end when > 6 parts (spaces in filenames).
- `ReadLinesWithDetectedEncodingAsync` — BOM detection (UTF-8 → **UTF-32LE before UTF-16LE** → UTF-16LE → UTF-16BE), then strict UTF-8, then legacy codepages `[932, 949, 936, 1251, 866, 1252]` ("ordered by likelihood for game rips") scored +10 per `FILE` line whose name resolves to an existing file; ties broken by declared order; last resort `Encoding.Default`.
- Used by the conversion pipeline for dependency validation, by `CueNormalizer`, and by `DeleteOriginalGameFilesAsync`.

---

## 8.4 BinCueGenerator

`internal static class BinCueGenerator` (`BinCueGenerator.cs:13`)

Generates cue files for **bin-only archives** (no descriptor in the archive).

- Constants: `Mode2 = "MODE2/2352"`, `Mode1 = "MODE1/2352"`, auto-cue marker `".autocue"`.
- `GetAutoCuePath(binPath)` → `{bin}.autocue.cue`; `IsAutoCue(path)` → filename ends with `.autocue.cue`.
- `BuildCueContent(binFileName, mode)` → single-track `FILE ... BINARY / TRACK 01 {mode} / INDEX 01 00:00:00`.
- `ReadTrackModeAsync(cuePath)` — scans `TRACK ` lines for a `/` and returns the mode token after the last space; default MODE2/2352.
- `RewriteCueAsync(cuePath, mode)` — rewrites the whole auto-cue with a new mode.
- `GetAlternateMode(mode)` — MODE2 ↔ MODE1 swap.
- Auto-cue outputs map to `Game.chd` (not `Game.autocue.chd`), and a failed auto-cue conversion is retried once with the alternate track mode (`MainWindow.xaml.cs:1579–1627`).

---

## 8.5 IsoSectorValidator

`internal static class IsoSectorValidator` (`IsoSectorValidator.cs:14`)

- `StandardSectorSizes = [2352, 2048, 2336, 2324]` — raw CD, DVD/data, Mode 2 XA, Mode 2 Form 1.
- `GetSectorSizeWarning(path)` — `null` for `.cue`/`.gdi`/`.toc` descriptors and for missing/unreadable files; otherwise warns when the size isn't divisible by any standard size. Used as an early warning (conversion still proceeds; the hard check happens after chdman fails).

---

## 8.6 Mp3ToWavDecoder & IMp3Decoder

`internal sealed class Mp3ToWavDecoder : IMp3Decoder` (`Mp3ToWavDecoder.cs:16`)

- `DecodeAsync(mp3Path, wavPath, onLog?, token)` — decodes an MP3 to a 16-bit PCM WAV; throws when undecodable.
- **Primary path**: NAudio Media Foundation (`MediaFoundationReader`), serialized under a static `Lock` because `MediaFoundationApi.Startup/Shutdown` are not thread-safe.
- **Fallback path**: `Mp3FileReader` (ACM codec) for Windows N / Server Core without Media Foundation.
- `NormalizeForChdman` — resamples to exactly **44 100 Hz** (`WdlResamplingSampleProvider`) and converts mono → stereo (`MonoToStereoSampleProvider`); `WaveFileWriter.CreateWaveFile16` forces 16-bit PCM (some MF codecs emit IEEE float, which chdman can't read).
- Both decoders failing → `InvalidDataException` (with the MF exception as inner).

---

## 8.7 RetryingFileOperations

`internal static class RetryingFileOperations` (`RetryingFileOperations.cs:10`)

File operations that survive transient locks (antivirus, indexer, explorer):

- `MaxDeleteAttempts = 10`; backoff `[500, 1000, 2000, 4000, 6000, 8000, 8000, ...]` ms — ≈ 45 s total.
- `TryDeleteAsync(path, token, onRetry?, backoffMsProvider?)`:
  - `FileNotFoundException`/`DirectoryNotFoundException` → `true` (already gone).
  - `IOException` → retry with backoff; `false` after the last attempt.
  - `UnauthorizedAccessException` → clears the **ReadOnly attribute once**, retries, then fails.
- `TryMoveAsync(source, dest, token, onRetry?, backoffMsProvider?)`:
  - `FileNotFoundException` → `true` (source already gone — nothing to move).
  - `IOException` (including `DirectoryNotFoundException`) → retry with backoff; `false` after the last attempt. A failed move is **never** reported as success — the source file still exists.
  - `UnauthorizedAccessException` → fail fast (ACL problems won't resolve).
- Used by: `TryDeleteFileAsync`/`TryDeleteDirectoryAsync`, `MoveVerifiedFileAsync`, `ExtractChdTracksToDirectory`, the ASCII-output move in `ConvertToChdAsync`, and destination-deletion before moves.

---

## 8.8 FileExtensions

`internal static class FileExtensions` (`FileExtensions.cs:11`)

All constants are lowercase; every lookup is case-insensitive (`StringComparer.OrdinalIgnoreCase`).

| Constant | Value | Constant | Value |
|----------|-------|----------|-------|
| `Cue` | `.cue` | `Zip` | `.zip` |
| `Iso` | `.iso` | `SevenZip` | `.7z` |
| `Img` | `.img` | `Rar` | `.rar` |
| `Gdi` | `.gdi` | `Cso` | `.cso` |
| `Toc` | `.toc` | `Pbp` | `.pbp` |
| `Raw` | `.raw` | `Bin` | `.bin` |
| `Ccd` | `.ccd` | `Sub` | `.sub` |
| `Chd` | `.chd` | | |

Sets (with `...Set` case-insensitive twins):

- `AllSupportedInputExtensionsForConversion` = `[.cue, .iso, .img, .gdi, .toc, .raw, .ccd, .zip, .7z, .rar, .cso, .pbp]`
- `ArchiveExtensions` = `[.zip, .7z, .rar]`
- `PrimaryTargetExtensions` (extraction targets from archives) = `[.cue, .iso, .img, .gdi, .toc, .raw, .ccd]`

Note: `.bin` and `.sub` are sidecar formats (not standalone inputs), `.chd` is an output; the `.cdt` sibling of CCD sets is referenced literally in `MainWindow.xaml.cs:1753/3165` (no constant).

---

## 8.9 Models

### FileItem (`Models/FileItem.cs`)

Bindable row for the file list DataGrids: `FileName` (relative path when searching subfolders), `FullPath`, `FileSize` (long), `IsSelected` (INotifyPropertyChanged). `DisplaySize` formats bytes with binary units `B/KB/MB/GB/TB` (`{size:0.##} {suffix}`).

### PbpExtractionResult (`Models/PbpExtractionResult.cs`)

`Success`, `CueFilePaths` (list), `OutputFolder`, `ErrorCode` (`PbpError?` — distinguishes "not a PlayStation disc image" from real failures), `Error` (human-readable failure description preserved from PBPSharp).
