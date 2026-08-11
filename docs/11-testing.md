# 11. Testing

The solution contains a single test project, `BatchConvertToCHD.Tests` (xUnit, `net10.0-windows`), with **~570 passing tests** across 32 test files (plus the shared `FakeHttpMessageHandler` helper).

## 11.1 Running the Tests

```bash
dotnet test CSharp_BatchConvertToCHD.sln -c Release
# or, faster, without rebuilding:
dotnet test BatchConvertToCHD.Tests/BatchConvertToCHD.Tests.csproj --no-build
```

Requirements: the tests are run on Windows (the app project is `net10.0-windows`). Some tests need the app's output directory to contain `chdman.exe` (it is copied by the build).

## 11.2 How Tests Are Structured

- Plain xUnit `[Fact]` / `[Theory]` + `[InlineData]`.
- Filesystem-dependent tests create a GUID temp directory per test class (`Path.GetTempPath() + $"{ClassName}_{Guid:N}"`) and clean it up in `Dispose`.
- HTTP-dependent tests inject an `HttpClient` backed by `FakeHttpMessageHandler` (the only shared helper): a `Func<HttpRequestMessage, HttpResponseMessage>` or a convenience `(HttpStatusCode, string content, string contentType)` constructor, plus a static `WithAsyncHandler` helper.
- Internals are tested because `BatchConvertToCHD.csproj` grants `InternalsVisibleTo("BatchConvertToCHD.Tests")`.
- **Integration tests** are tagged `[Trait("Category", "Integration")]` and read real sample files from fixed absolute directories (`D:\Emulators\...`). They **early-return when the samples are absent**, so on machines without the sample folders they are effectively skipped (reported as passed).

## 11.3 Coverage by File

### Application-level tests

| File | Focus |
|------|-------|
| `AppConfigTests.cs` | Arm64 detection, chdman/7za exe names, API URLs/keys, app name, interval/timeout constants |
| `AppHttpClientTests.cs` | Singleton behavior, Accept header, TLS 1.2+1.3, dispose semantics, thread safety |
| `ArchiveServiceTests.cs` | ZIP extraction (real ZIPs built in-test), corrupt/unsupported/missing archives, bin-only archives → auto-cue, `ExtractCsoAsync` failure/cancellation, 7za fallback matrix, multi-part RAR / network detection, disk-full errors |
| `BinCueGeneratorTests.cs` | Auto-cue marker, cue content, mode alternation, read/rewrite |
| `BugReportApiSinkTests.cs` | Sink forwards Warning/Error/Fatal, ignores Debug/Info |
| `BugReportServiceTests.cs` | Report formatting (inner exceptions, depth), HTTP method/header/body, success/failure mapping, the full exclusion-pattern list (incl. case-insensitivity), no-HTTP-call for excluded messages |
| `CancellationHandlingTests.cs` | `IsCancellationException`, `IsDiskSpaceException`, `IsCorruptionException`, `IsCrcErrorException` and their mutual exclusivity |
| `CueNormalizerTests.cs` | Encoding detection (CP949/CP1251/CP932/UTF-8/UTF-32LE BOM), canonicalization, zero-padding resolution, unresolved names, MP3 transform hook, canonical write format |
| `CueWorkDirectoryTests.cs` | Work-dir creation rules, in-place BOM fast path, MP3→WAV decoding (fake + real NAudio decoders), **end-to-end tests running real `chdman.exe`** (BOM regression, cue/bin/mp3, cue/iso/mp3; skipped when chdman is absent) |
| `FileExtensionsTests.cs` | All extension constants and sets via reflection, cross-consistency, no duplicates |
| `FileItemTests.cs` | INotifyPropertyChanged, `DisplaySize` formatting (0 B … 1.5 TB) |
| `FileWatcherServiceTests.cs` | Start/Stop/Dispose, `GetContextForMissingFile` diagnostics with a real `FileSystemWatcher`, history eviction at 1000 entries, buffer-overflow clearing |
| `GameFileParserTests.cs` | cue/gdi/toc referenced-file extraction (quoted/unquoted/spaces/multi-file), encoding detection |
| `GitHubReleaseTests.cs` | Model defaults, JSON (de)serialization |
| `IsoSectorValidatorTests.cs` | Sector-size alignment warnings; descriptors/empty/missing not validated |
| `MainWindowHelperTests.cs` | `StripUtf8BomIfPresentAsync`, `SelectChdmanErrorLine` (skips progress lines, picks last real error) |
| `PathUtilsTests.cs` | `SanitizeFileName`, `GetSafeTempFileName`, path validation, relative paths, best-temp-directory selection |
| `PbpExtractionResultTests.cs` | Result-model defaults/setters |
| `RetryingFileOperationsTests.cs` | `TryDeleteAsync`/`TryMoveAsync` with real file locks (`FileShare.None`), read-only attribute clearing, retry-then-give-up, success-after-lock-release, missing-source/missing-destination semantics |
| `StatsServiceTests.cs` | POST method/URL/Bearer header/body, no-throw on 429/401/400/500/network errors |
| `UpdateServiceTests.cs` | Version parsing/normalization theories, new/older/minor/major comparisons, draft/prerelease skip, rate-limit and 5xx handling (no bug report), bug-report paths, invalid tags |

### Library tests (CSOSharp / PBPSharp)

| File | Focus |
|------|-------|
| `CsoFileTests.cs` | Open-error mapping, v1/v2 open, dispose behavior, block reads |
| `CsoStreamTests.cs` | Full stream contract: seek/read/zero-length, cross-block reads, throw semantics |
| `CsoHeaderTests.cs` | Header constants, v1/v2 validity, total blocks, index offset shift |
| `CsoFileIntegrationTests.cs` | Real `.cso` files: **byte-for-byte block comparison vs. paired `.iso`**, full extraction equality, stream parity |
| `PbpFileTests.cs` | Open errors, header/SFO/disc parsing, **PbpError enum ordinal assertions**, synthetic PBP+SFO builders |
| `PbpHeaderTests.cs` | Magic, size (0x28), defaults, validity |
| `SfoDataTests.cs` / `SfoEntryTests.cs` / `TocEntryTests.cs` | SFO lookups (incl. type mismatch), entry formats, TOC/track types |
| `CueSheetWriterTests.cs` | Generated CUE content: data/audio tracks, INDEX 00 with 150-frame lead-in, zero-clamp, padding |
| `PbpFileIntegrationTests.cs` | Real `.pbp` files: header/SFO/TOC, `ExtractToBinCue` byte-equality vs. original BIN, normalized CUE equality |

> **Gap**: there are currently **no CCDSharp unit tests** — the test project does not reference CCDSharp (`BatchConvertToCHD.Tests.csproj:36–38`); the only touch-point is the `"CCDSharp: Conversion error"` exclusion pattern. CCDSharp behavior is exercised indirectly only if a real `.ccd` file flows through the app.

## 11.4 Writing New Tests — Quick Conventions

1. File-scoped namespace `BatchConvertToCHD.Tests`; `using Xunit` is global.
2. For filesystem tests, mirror the GUID-temp-dir + `IDisposable` pattern.
3. For HTTP tests, use `FakeHttpMessageHandler` and pass the `HttpClient` to the internal constructor overloads (`StatsService`, `BugReportService`, `UpdateService`, `AppHttpClient`).
4. For chdman-dependent tests, early-return when `chdman.exe` is absent from `AppContext.BaseDirectory`.
5. Run the full suite before pushing; a green run is expected to stay at 0 failures.
