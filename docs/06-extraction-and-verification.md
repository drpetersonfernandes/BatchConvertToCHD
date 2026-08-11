# 6. Extraction & Verification (Technical)

This page covers the internals of the two CHD-consuming workflows. References are to `BatchConvertToCHD/MainWindow.xaml.cs` unless noted.

---

## 6.1 Extraction

### Entry & batch loop

`StartExtractionButton_ClickAsync` (`:693`) validates paths, reads options (subfolders, delete original), and calls `PerformBatchExtractionAsync` (`:1355`), which runs `CheckDiskSpace` (extraction mode: warns when output free space < total input size) and then loops per file with `ExtractChdAsync` (`:2123`).

### Format selection

`GetSelectedExtractCommandAsync` (`:2325`):

| UI choice | chdman command |
|-----------|----------------|
| Auto | `DetectChdExtractCommandAsync` (`:2370`) — metadata scan |
| CD (.cue) | `extractcd` |
| DVD (.iso) | `extractdvd` |
| GDI (.gdi) | `extractcd` |
| HDD (.img) | `extracthd` |

Metadata detection scans the CHD's metadata entries (CHDSharp): `dvd` → `extractdvd`; `gd-rom` → `extractcd`; `hard disk`/`hdd` → `extracthd`; default `extractcd`. Output extension: explicit per radio button, or for Auto derived from the detected command — and when Auto yields `extractcd` plus the metadata contains `gd-rom` (`IsGdiChdAsync`, `:2338`), the extension becomes `.gdi` instead of `.cue`.

### Output path

The subfolder structure of the input is preserved under the output folder (`GetSafeRelativePath`). Existing DVD/HDD outputs are deleted before extraction ("Overwriting: ... already exists in output folder.").

### Single-file extraction (DVD/HDD)

`ExtractChdToSingleFile` (`:2233`): a `FileStream` is created with `FileMode.Create`, and the CHD is streamed out in 4 MB buffers with a per-chunk `token.ThrowIfCancellationRequested()`. Cancellation deletes the partially extracted file.

### Multi-track extraction (CD/GDI)

`ExtractChdTracksToDirectory` (`:2252`):

1. Creates `_extract_temp_<guid>` **inside the target directory**.
2. Calls `chd.ExtractToDirectory(tempExtractDir, baseFileName)` (CHDSharp).
3. Moves each extracted file into the target dir, overwriting existing files.
4. On success the temp dir is deleted; on failure the temp dir is **kept** and a warning logs the number of remaining files ("Partial extraction: N file(s) remain in temp directory: ...") so the user can inspect/clean up.
5. Moves and destination-deletes go through `RetryingFileOperations.TryMoveAsync`/`TryDeleteAsync` (retry with backoff, ~45 s) so transient locks (antivirus/indexer) don't abort the whole disc extraction; a move that still fails after retries throws and the partial-extraction path handles the rest.

### CHD open failures

`ChdFile.Open` errors are logged with the CHDSharp message and the file is marked failed; the batch continues. Typical messages: "Not a valid CHD file" (bad magic), "Invalid or corrupt data" (structure broken), "Cannot open file" (locked/unreadable). These are user-data conditions — the app never crashes on them and they are excluded from bug reports (see [Bug Reporting System](09-bug-reporting.md)).

---

## 6.2 Verification

### Entry & batch loop

`StartVerificationButton_ClickAsync` (`:1110`) reads the move options, creates the `Success`/`Failed` folders up front when requested (`:2022–2030`), and calls `PerformBatchVerificationAsync` (`:2011`).

### VerifyChdAsync

`VerifyChdAsync` (`:3020`) opens the file read-only and calls `Chd.CheckFile(stream, fileName, true)` (CHDSharp, in-process — **no** chdman process). On success it logs `V{version} — SHA1: {hex}`; failures log `result.Error.GetMessage()` or the exception message. The per-file read speed is sampled via the read performance counter.

### Moving verified files

`MoveVerifiedFileAsync` (`:2076`):

- Destination: `inputFolder\Success` or `inputFolder\Failed`; with subfolder search the relative directory is preserved under the target folder.
- Existing destination files are deleted with `RetryingFileOperations.TryDeleteAsync` (result checked — a locked destination fails fast with a clear error instead of a misleading move failure).
- The move uses `RetryingFileOperations.TryMoveAsync` (10 attempts, backoff 500 ms → 8 s, ~45 s total) because the freshly verified file may still be held by antivirus or the indexer.
- On persistent failure, the exception is logged and reported via `ReportBugAsync` ("Failed to move file ..."), but the batch continues.

### Scan exclusions

The verification and extraction file lists exclude anything under a first-level `Success` or `Failed` subfolder when recursive search is on (`:884–896`, `:941–952`), so organized output isn't reprocessed.

---

## 6.3 Startup & Shutdown Housekeeping

- **Leftover temp directories** from crashed sessions are deleted at startup: `CleanupLeftoverTempDirectories` (`:304`) scans `PathUtils.GetPossibleTempBasePaths()` (system temp + any existing `X:\BatchConvertToCHD_Temp` folders on fixed drives) for `BatchConvertToCHD_Temp_*` entries.
- **Legacy files** next to the exe are removed by `LegacyCleanupService` (`logs`, `Resources`, `Screenshot` folders; `maxcso.exe`, `psxpackager.exe`).
- On `Dispose` (`:3552`) the app unregisters the F8 hotkey, cancels the operation token, disposes services, and calls `KillOrphanedProcesses` (`:3579`) to kill leftover `chdman`/`7za` processes before exiting.
