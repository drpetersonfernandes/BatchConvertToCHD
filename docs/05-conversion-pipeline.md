# 5. Conversion Pipeline (Technical)

This page is a deep dive into the conversion machinery. All references are to `BatchConvertToCHD/MainWindow.xaml.cs` unless noted.

---

## 5.1 Entry Point & Batch Orchestration

`StartConversionButton_ClickAsync` (`:1025`) validates both folder paths (`PathUtils.ValidateAndNormalizePath`), reads the option flags, renews the cancellation token source, disables the UI, and calls `PerformBatchConversionAsync` (`:1292`).

`PerformBatchConversionAsync` does, in order:

1. **Executable validation** — `ValidateExecutableAccessAsync` (`:345`): file exists, is `.exe`, is not locked exclusively, and (when not running as admin) is not read-only. `ValidateChdmanCompatibilityAsync` (`:424`) runs `chdman help` and gives specific guidance for old-Windows "not a valid application" errors (Win32 error 193) and access-denied (error 5).
2. **Sorting** — when "process smaller files first" is set, files are ordered by ascending size (`:1300–1313`).
3. **Disk space check** — `CheckDiskSpace` (`:3292`): warns when the output drive's free space is below 50 % of the total input size for conversion (100 % for extraction), and separately checks the temp drive when it differs from the output drive.
4. **Per-file loop** — `ProcessSingleFileForConversionAsync` (`:1395`), with Interlocked ok/failed counters and progress/speed updates.

## 5.2 Per-File Routing

`ProcessSingleFileForConversionAsync` (`:1395`) decides the output path (source subfolder structure preserved via `PathUtils.GetSafeRelativePath` + `SanitizeFileName`), then dispatches by extension:

| Extension | Handler | What it does |
|-----------|---------|--------------|
| `.cso` | `ProcessCsoFileForConversionAsync` (`:1510`) | `_archiveService.ExtractCsoAsync` decompresses to a temp `.iso`, then converts. |
| `.zip`/`.7z`/`.rar` | `ProcessArchiveFileForConversionAsync` (`:1539`) | Extracts to a temp dir; filters `.img` files that belong to a `.ccd` set; maps auto-cue outputs; validates cue/gdi/toc dependencies; converts each supported file. |
| `.pbp` | `ProcessPbpFileForConversionAsync` (`:1642`) | `ExtractPbpToCueBinAsync` (`:2918`) via PBPSharp; `PbpError.InvalidPsarHeader` → informational skip; converts each disc cue. |
| `.ccd` | `ProcessCcdFileForConversionAsync` (`:1702`) | `CcdConverter.Parse` + `ConvertToCueBin` into a temp dir, then converts the cue. |
| everything else | direct path | `ValidateDependentFilesAsync` (`:1779`) → `TryDirectConversionAsync` (`:1844`). |

Missing input file: logs "File not found, skipping:" and asks `FileWatcherService.GetContextForMissingFile` for a diagnostic (deleted / renamed / created-then-gone / outside watch / drive disconnected).

### Retry-via-temp-copy fallback

If the direct conversion of the original file fails, `TryRetryConversionViaTempCopyAsync` (`:1863`) copies the input (plus referenced files for cue/gdi/toc via `GameFileParser`) into a temp directory and converts there. This handles network paths and file-locking quirks. It also:

- pre-checks temp-drive free space (`:1917–1935`),
- strips a UTF-8 BOM from cue/toc copies in place (`:1949–1955`; chdman's cue parser chokes on BOMs — see [GameFileParser](08-utilities-reference.md#gamefileparser)),
- copies with `CopyFileWithRetryAsync` (`:3201`; 5 attempts, exponential backoff from 500 ms).

### Result handling

`HandleConversionResultAsync` (`:1980`): on success, optionally deletes originals (see below) and prunes now-empty subfolders (`TryDeleteEmptySubfolderAsync`, `:3430`); on failure, deletes the partial output CHD and keeps the source. Temp dirs are always cleaned in a `finally` block (`:1491–1498`).

**Deleting originals** — `DeleteOriginalGameFilesAsync` (`:3134`): for `.cue`/`.gdi`/`.toc` it also deletes every referenced data file (`GameFileParser`); for `.ccd` it deletes the `.img`/`.sub`/`.cdt` companions. All deletions go through `RetryingFileOperations.TryDeleteAsync` via `TryDeleteFileAsync` (`:3362`), which additionally kills stray chdman processes after the second failed attempt (`KillChdmanProcesses`, `:3380`).

## 5.3 ConvertToChdAsync — the chdman Wrapper

`ConvertToChdAsync` (`:2456`) is the single funnel for every chdman invocation.

### Command & argument selection

```csharp
command = forceCd || hasCue || (!forceDvd && !isIso && !isImg && !isRaw) ? "createcd"
        : forceDvd || isIso                                          ? "createdvd"
        : isImg                                                      ? "createhd"
        :                                                              "createraw";
```

- `hasCue = isImg && File.Exists(Path.ChangeExtension(input, ".cue"))` — an `.img` with a sibling `.cue` is treated as a CD image.
- Base args: `{command} -i "<in>" -o "<out>" -f -np {cores}`.
- **`.raw` inputs get `-us 2352`** (`:2478–2481`) — chdman's `createraw` requires an explicit unit size when no parent CHD is supplied ("Unit size must be specified if no output parent CHD is supplied").
- `-np` (processors) comes from a UI/core setting.

### Pre-flight validations

1. **Sector-size warning for DVD** (`:2494–2501`): `IsoSectorValidator.GetSectorSizeWarning` flags sizes not divisible by 2352/2048/2336/2324, but conversion proceeds — the hard gate is the post-failure check (some legitimate images use non-standard layouts).
2. **Cue work-dir preparation** for `.cue`/`.toc` (`:2503–2527`): `PrepareCueWorkDirAsync` (`:2417`) → `CueWorkDirectory.PrepareAsync` (see [Utilities](08-utilities-reference.md#cueworkdirectory)). If MP3 tracks exist and decoding failed, conversion is aborted with a clear message instead of handing chdman an MP3 cue.
3. **ASCII temp work dir** (`:2508–2546`): if the input or output filename contains non-ASCII characters, the input is copied into a GUID-named temp dir and the output is written there too; after success the output is moved to the real destination with `RetryingFileOperations.TryMoveAsync`.

### Process execution

- `ProcessStartInfo` with redirected stdout/stderr, `UseShellExecute=false`, `CreateNoWindow=true`.
- Output handlers classify lines: "Compression complete"/"final ratio" → success lines; `% complete`/`Compressing`/`Output bytes`/`Compression ratio` → filtered as progress; everything else → `[CHDMAN]` log lines.
- The stderr buffer accumulates **all** stderr lines (including progress, which chdman streams to stderr).
- **Timeout**: when enabled, a linked CTS with `CancelAfter(timeoutMinutes)` aborts the wait; the process is killed and the file marked failed with a `TIMEOUT:` log.
- On cancellation/timeout the process is killed (`process.Kill(true)`), waited up to 5 s, and temp cleanup is deferred 300 ms so file handles are released.

### Exit-code handling

- Success = exit code 0 and no cancellation.
- **createdvd fallback**: if the error output contains "Unrecognized track type" and the command was `createcd` without user-forced CD, the app recurses with `forceDvd=true` (`:2694–2699`).
- **Valid-output tolerance**: a non-zero exit that still produced a >0-byte output file is treated as success (`:2701–2716`).
- **Sector-size hard check** (`:2761–2794`): for non-descriptor inputs, if the file size is not divisible by any of 2352/2048/2336/2324, the conversion fails with "file size ... is not divisible by any standard sector size ... The file may be corrupt or truncated."
- **Disk-space detection** (`IsDiskSpaceError`, `:3281`): keywords "not enough space", "not enough disk space", "disk full", "no space left", "insufficient disk space".
- **Error line selection** (`SelectChdmanErrorLine`, `:2860`): scans the stderr buffer from the **last** line upward, skipping progress lines (`% complete`, `Compressing,`, `Converting,`, `Output bytes`, `Compression ratio`, `ratio=`), and returns the last real error line. This fixed the class of bugs where the first line of stderr was a progress line ("Compressing, 0.0% complete... (ratio=100.0%)").
- **"couldn't find bin file" diagnostics**: when the selected error line contains that phrase, a capped, sorted directory listing of the input folder is logged (`GetDirectoryDiagnostics`, `:2890`).

## 5.4 Cue Normalization & Work Directories

Two cooperating mechanisms ensure chdman never sees malformed cues:

1. **`CueNormalizer.NormalizeAsync`** — detects the file encoding (BOMs → strict UTF-8 → legacy codepages scored by resolvable references), strips BOMs, unquotes/rewrites `FILE` lines, resolves references case-insensitively and with zero-padding tolerance (`(Track 2)` ↔ `(Track 02)`), and produces a canonical UTF-8 (no BOM) CRLF cue.
2. **`CueWorkDirectory.PrepareAsync`** — when the cue needs rewriting (BOM, non-UTF-8, non-ASCII names, MP3 tracks, corrected names), builds an isolated ASCII work directory with the canonical cue and every referenced file under safe `trackNN.ext` names; MP3 tracks are decoded to WAV. BOM-only cues with ASCII names use an **in-place fast path**: a `game.cue` referencing bins via relative paths, avoiding multi-hundred-MB copies.

Details in [Utilities Reference](08-utilities-reference.md#cuenormalizer-and-cueworkdirectory).

## 5.5 Exception Classifiers

Centralized classification used across the pipeline (`:3227–3290`):

| Helper | Matches |
|--------|---------|
| `IsCancellationException` | `OperationCanceledException` |
| `IsDiskSpaceException` | `IOException` HResult `-2147024784` (ERROR_DISK_FULL) or `-2147024783` (ERROR_SEM_TIMEOUT) |
| `IsCrcErrorException` | `IOException` HResult `-2147024809` (ERROR_CRC) or message containing "cyclic redundancy check"/"data error" |
| `IsCorruptionException` | `InvalidDataException`, `IndexOutOfRangeException`, `NullReferenceException`, `CryptographicException`, or SharpCompress archive-corruption types (IncompleteArchive, ArchiveOperation, InvalidFormat, LZMA DataError) |
| `IsDiskSpaceError` (string) | chdman output keywords listed above |

## 5.6 Archive Processing (Summary)

See [Services Reference → ArchiveService](07-services-reference.md#archive-service) for the full extraction semantics. Highlights relevant to the pipeline:

- Pre-extraction disk-space estimate (`CheckTempDiskSpace`): ZIP entry sizes are summed; safety margin = estimate + max(estimate/10, 100 MB).
- Zip-slip protection: every extracted path must stay under the output directory.
- Post-extraction scan for primary targets (`.cue/.iso/.img/.gdi/.toc/.raw/.ccd`); if none and bare `.bin` files exist, `BinCueGenerator` produces a MODE2/2352 auto-cue for the largest bin (auto-cues are retried once with MODE1/2352 on failure — `MainWindow.xaml.cs:1617–1627`).
- Error categorization maps SharpCompress/7za failures to actionable messages (missing RAR volume, encrypted archive, unsupported compression method, disk full, locked file, network unavailable).
