using System.Diagnostics;
using System.IO;
using System.Text;
using SevenZip;

namespace BatchConvertToCHD.Services;

public class ArchiveService(string maxCsoPath, bool isMaxCsoAvailable)
{
    private readonly string _maxCsoPath = maxCsoPath;
    private readonly bool _isMaxCsoAvailable = isMaxCsoAvailable;
    private static readonly string[] PrimaryTargetExtensions = { ".cue", ".iso", ".img", ".cdi", ".gdi", ".toc", ".raw" };
    private const int WriteSpeedUpdateIntervalMs = 1000;

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

            // Monitor speed
            var lastSpeedCheckTime = DateTime.UtcNow;
            long lastFileSize = 0;

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

                try
                {
                    if (await Task.Run(() => File.Exists(tempOutputIsoPath), token))
                    {
                        var currentFileSize = await Task.Run(() => new FileInfo(tempOutputIsoPath).Length, token);
                        var currentTime = DateTime.UtcNow;
                        var timeDelta = currentTime - lastSpeedCheckTime;
                        if (timeDelta.TotalSeconds > 0)
                        {
                            var bytesDelta = currentFileSize - lastFileSize;
                            var speed = (bytesDelta / timeDelta.TotalSeconds) / (1024.0 * 1024.0);
                            onSpeedUpdate(speed);
                        }

                        lastFileSize = currentFileSize;
                        lastSpeedCheckTime = currentTime;
                    }
                }
                catch
                {
                    /* ignore monitoring errors */
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
}
