using System.Diagnostics;
using System.IO;
using System.Text;
using SevenZip;

namespace BatchConvertToCHD.Services;

public class ArchiveService : IDisposable
{
    private readonly string _maxCsoPath;
    private readonly bool _isMaxCsoAvailable;
    private static readonly string[] PrimaryTargetExtensions = { ".cue", ".iso", ".img", ".cdi", ".gdi", ".toc", ".raw" };

    // Performance counter for write speed monitoring
    private PerformanceCounter? _writeBytesCounter;
    private PerformanceCounter? _readBytesCounter;
    private const int WriteSpeedUpdateIntervalMs = 1000;

    public ArchiveService(string maxCsoPath, bool isMaxCsoAvailable)
    {
        _maxCsoPath = maxCsoPath;
        _isMaxCsoAvailable = isMaxCsoAvailable;
        InitializePerformanceCounter();
        InitializeReadPerformanceCounter();
    }

    private void InitializePerformanceCounter()
    {
        try
        {
            // Create a performance counter for disk write operations
            _writeBytesCounter = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total");
        }
        catch (Exception)
        {
            // Silently fail - write speed monitoring is optional
            _writeBytesCounter = null;
        }
    }

    private void InitializeReadPerformanceCounter()
    {
        try
        {
            // Create a performance counter for disk read operations
            _readBytesCounter = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total");
        }
        catch (Exception)
        {
            _readBytesCounter = null;
        }
    }

    public async Task<(bool Success, string FilePath, string TempDir, string ErrorMessage)> ExtractCsoAsync(
        string originalCsoPath,
        string tempOutputIsoPath,
        string tempDirectoryRoot,
        Action<string> onLog,
        Action<double> onSpeedUpdate,
        CancellationToken token)
    {
        if (!_isMaxCsoAvailable)
        {
            return (false, string.Empty, tempDirectoryRoot, "maxcso.exe is not available.");
        }

        var csoFileName = Path.GetFileName(originalCsoPath);
        var process = new Process();

        try
        {
            token.ThrowIfCancellationRequested();
            onLog($"Decompressing {csoFileName} to temporary ISO: {tempOutputIsoPath}");

            process.StartInfo = new ProcessStartInfo
            {
                FileName = _maxCsoPath,
                Arguments = $"--decompress \"{originalCsoPath}\" -o \"{tempOutputIsoPath}\"",
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
            };

            var outputBuilder = new StringBuilder();
            process.OutputDataReceived += (_, args) =>
            {
                if (args.Data != null) outputBuilder.AppendLine(args.Data);
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data == null) return;

                outputBuilder.AppendLine(args.Data);
                onLog($"[MAXCSO STDERR] {args.Data}");
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            while (!process.HasExited)
            {
                if (token.IsCancellationRequested)
                {
                    try
                    {
                        if (!process.HasExited) process.Kill(true);
                    }
                    catch
                    {
                        /* ignore */
                    }

                    token.ThrowIfCancellationRequested();
                }

                await Task.Delay(WriteSpeedUpdateIntervalMs, token);
                if (process.HasExited || token.IsCancellationRequested) break;

                if (_writeBytesCounter != null)
                {
                    var writeBytesPerSec = _writeBytesCounter.NextValue();
                    onSpeedUpdate(writeBytesPerSec / 1048576.0); // Convert to MB/s

                    // Also monitor read speed during CSO decompression
                    // Note: We call NextValue() to keep the counter updated, but don't need the value here
                    _readBytesCounter?.NextValue();
                }
            }

            await process.WaitForExitAsync(token);

            if (process.ExitCode != 0 || !File.Exists(tempOutputIsoPath))
            {
                return (false, string.Empty, tempDirectoryRoot, $"MaxCSO failed. Exit code: {process.ExitCode}. Output: {outputBuilder}");
            }

            onLog($"Successfully decompressed {csoFileName}");
            return (true, tempOutputIsoPath, tempDirectoryRoot, string.Empty);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited) process.Kill(true);
            }
            catch
            {
                /* ignore */
            }

            throw;
        }
        finally
        {
            process?.Dispose();
            _readBytesCounter?.Dispose();
            _writeBytesCounter?.Dispose();
        }
    }

    public async Task<(bool Success, string FilePath, string TempDir, string ErrorMessage)> ExtractArchiveAsync(
        string originalArchivePath,
        string tempDirectoryRoot,
        Action<string> onLog,
        CancellationToken token)
    {
        var extension = Path.GetExtension(originalArchivePath).ToLowerInvariant();
        var archiveFileName = Path.GetFileName(originalArchivePath);

        try
        {
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(tempDirectoryRoot);
            onLog($"Extracting {archiveFileName} to: {tempDirectoryRoot}");

            switch (extension)
            {
                case ".zip":
                    await Task.Run(() => System.IO.Compression.ZipFile.ExtractToDirectory(originalArchivePath, tempDirectoryRoot, true), token);
                    break;
                case ".7z":
                case ".rar":
                    var directExtractionSuccess = false;
                    try
                    {
                        await Task.Run(() =>
                        {
                            using var extractor = new SevenZipExtractor(originalArchivePath);
                            extractor.ExtractArchive(tempDirectoryRoot);
                        }, token);
                        directExtractionSuccess = true;
                    }
                    catch (Exception ex)
                    {
                        onLog($"Direct extraction failed ({ex.Message}). Attempting fallback with local copy...");
                    }

                    if (!directExtractionSuccess)
                    {
                        // Sanitize path for 7ZipSharp
                        var sanitizedArchivePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}{extension}");
                        try
                        {
                            File.Copy(originalArchivePath, sanitizedArchivePath, true);
                            await Task.Run(() =>
                            {
                                using var extractor = new SevenZipExtractor(sanitizedArchivePath);
                                extractor.ExtractArchive(tempDirectoryRoot);
                            }, token);
                        }
                        finally
                        {
                            try
                            {
                                File.Delete(sanitizedArchivePath);
                            }
                            catch
                            {
                                /* ignore */
                            }
                        }
                    }

                    break;
                default:
                    return (false, string.Empty, tempDirectoryRoot, $"Unsupported archive type: {extension}");
            }

            token.ThrowIfCancellationRequested();

            var foundFile = await Task.Run(() =>
                Directory.GetFiles(tempDirectoryRoot, "*.*", SearchOption.AllDirectories)
                    .FirstOrDefault(f => PrimaryTargetExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())), token);

            return foundFile != null
                ? (true, foundFile, tempDirectoryRoot, string.Empty)
                : (false, string.Empty, tempDirectoryRoot, "No supported primary files found in archive.");
        }
        catch (Exception ex)
        {
            return (false, string.Empty, tempDirectoryRoot, $"Error extracting archive: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _readBytesCounter?.Dispose();
        _writeBytesCounter?.Dispose();
        GC.SuppressFinalize(this);
    }
}