# 13. Troubleshooting

Common messages, their meaning, and what to do.

## 13.1 Conversion Errors

| Message (as shown in the log) | Meaning | Action |
|--------------------------------|---------|--------|
| `Failed to convert '<file>': ERROR: couldn't find bin file [<path>]` | chdman cannot find a track file referenced by the cue (missing, wrong name, or on a different drive). The app logs a directory listing to help. | Verify the `.bin` files are next to the cue with matching names. Non-ASCII names/BOMs are normally handled automatically; re-scan the folder. |
| `Failed to convert '<file>': file size (N bytes) is not divisible by any standard sector size (2048/2324/2336/2352). The file may be corrupt or truncated.` | The image size doesn't match CD/DVD sector geometry. | Re-download or re-rip the image; verify with the original disc. |
| `Failed to convert '<file>': Unit size must be specified if no output parent CHD is supplied` | A `.raw` input was converted without a unit size. | Should not occur in current versions (raw inputs get `-us 2352`); if it does, use a `.cue` descriptor instead. |
| `Failed to convert '<file>': Compressing, 0.0% complete...` | (Old versions) a chdman progress line was shown instead of the real error. | Update the app; the real error line is now selected from the end of chdman's output. |
| `Failed to convert '<file>': Error creating CHD file (...): Unknown error` | chdman could not create the output file (drive issues, permissions, full disk). | Check the output drive is writable, has free space, and the path isn't overlong. |
| `TIMEOUT: Conversion of '<file>' exceeded N minute(s). Marking as failed.` | The per-file time limit fired. | Increase the limit (max 4 hours) or convert fewer/larger files at once. |
| `Retrying with createdvd (unrecognized track type)...` | A CD attempt failed; the app retries as DVD. | Usually succeeds automatically. If it fails again, force CD/DVD manually. |
| `chdman exited with code N but produced a valid output file...` | Non-zero exit but a valid output; treated as success. | Informational — nothing to do. |

## 13.2 Archive Errors

| Message | Meaning | Action |
|---------|---------|--------|
| `... multi-part RAR with a missing volume ...` | A multi-part `.rar` is missing one or more parts. | Download all `.partNN.rar` volumes into the same folder. |
| `... Archive is encrypted ...` | The archive is password-protected. | Password-protected archives are not supported; extract manually first. |
| `... compression method that is not supported ...` | The ZIP uses Deflate64/LZMA/PPMd, which the extractor can't read. | Re-zip with standard Deflate, or extract manually first. |
| `... archive file may be corrupted or incomplete ...` | The archive failed CRC/structure checks. | Re-download the archive. |
| `No supported primary files found in archive.` | The archive contains no convertible image/descriptor (and no bare `.bin`). | Check the archive contents. |
| `... archive file appears to be incomplete ...` / `... could not validate referenced files ...` | Archive entries reference data files that aren't in the archive (split-bin sets, CRC-skipped entries). | Get the complete archive set; the app skips the entry with a warning instead of failing hard. |

## 13.3 CHD Extraction & Verification

| Message | Meaning | Action |
|---------|---------|--------|
| `Failed to open '<file>.chd': Not a valid CHD file` | The file isn't a CHD (bad magic). | The file is corrupt or misnamed; re-acquire it. |
| `Failed to open '<file>.chd': Invalid or corrupt data` | CHD structure is broken. | Re-acquire the file; verify it with `chdman verify`. |
| `Failed to open '<file>.chd': Cannot open file` | The file is locked/unreadable. | Close any program holding the file (emulator, antivirus scan) and retry. |
| `Partial extraction: N file(s) remain in temp directory: <dir>` | A multi-track extraction failed partway; the temp dir is kept for inspection. | Check the listed `_extract_temp_*` folder, delete leftovers, and retry with a valid CHD. |
| `Failed to move file <path>: The process cannot access the file because it is being used by another process` | The move was blocked by a lock. | Current versions retry for ~45 s; if it still fails, close file-holding programs and retry. |

## 13.4 Environment & Startup

| Message | Meaning | Action |
|---------|---------|--------|
| `chdman.exe not found at '<path>'` | chdman is missing or was moved. | Keep `chdman.exe`/`chdman_arm64.exe` in the app folder. |
| Status bar CHDMAN indicator red | Same as above. | See previous row. |
| `Selected temp root "X:\" is not writable, falling back to system temp` | The preferred temp drive can't be written (e.g. `E:\` is a card reader / locked). | Informational; the app uses the system temp instead. Free space on `C:` matters then. |
| `Another instance of BatchConvertToCHD is already running.` | Single-instance mutex. | The first instance is still running; close it first. |
| `Update check skipped: GitHub API rate limit exceeded.` | GitHub API 403/429 (shared IP). | Wait and restart; no action needed. |
| `Failed to record usage statistics: HTTP 429` | Stats endpoint rate-limited. | Expected; silently ignored (Debug log only). |

## 13.5 Data & Safety Questions

**Are my originals deleted automatically?** Only when **"Delete originals after a successful conversion"** is enabled, and only after the CHD was produced successfully. Cue-set deletions also remove referenced `.bin`/`.sub` files; CCD deletions remove `.img`/`.sub`/`.cdt`.

**What happens to temp files on crash?** Leftover `BatchConvertToCHD_Temp_*` folders are deleted at next startup.

**Where are the logs?** `%LocalAppData%\BatchConvertToCHD\logs` (daily files, 7 days retained). Click the **AppData** button in the title bar.

**Does the app phone home?** It sends: anonymous usage stats (application name + version, once per launch), bug reports for warning-level events (see [Bug Reporting System](09-bug-reporting.md)), and GitHub update checks. No personal data is collected (the bug report includes the Windows user name as `userInfo`).

**Why do some bugs keep showing the same message?** Corrupt input files (bad CHDs, incomplete archives) are user-data conditions — the app now excludes those messages from bug reports; the in-app log remains the source of truth for them.
