using System.Diagnostics;
using System.IO;
using System.Text;
using SharpCompress.Archives;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Archives.Rar;

namespace BatchConvertToCHD.Services;

public class ArchiveService : IDisposable
{
    private readonly string _maxCsoPath;
    private readonly bool _isMaxCsoAvailable;
    private static readonly string[] PrimaryTargetExtensions = [".cue", ".iso", ".img", ".cdi", ".gdi", ".toc", ".raw"];

    public ArchiveService(string maxCsoPath, bool isMaxCsoAvailable)
    {
        _maxCsoPath = maxCsoPath;
        _isMaxCsoAvailable = isMaxCsoAvailable;
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
        using var process = new Process();

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

            // Wait for process to exit - WaitForExitAsync is efficient and handles cancellation
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
                    await Task.Run(() => ExtractSevenZipArchive(originalArchivePath, tempDirectoryRoot, onLog), token);
                    break;
                case ".rar":
                    await Task.Run(() => ExtractRarArchive(originalArchivePath, tempDirectoryRoot, onLog), token);
                    break;
                default:
                    return (false, string.Empty, tempDirectoryRoot, $"Unsupported archive type: {extension}");
            }

            token.ThrowIfCancellationRequested();

            var foundFile = await Task.Run(() =>
                Directory.GetFiles(tempDirectoryRoot, "*.*", SearchOption.AllDirectories)
                    .FirstOrDefault(static f => PrimaryTargetExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())), token);

            return foundFile != null
                ? (true, foundFile, tempDirectoryRoot, string.Empty)
                : (false, string.Empty, tempDirectoryRoot, "No supported primary files found in archive.");
        }
        catch (Exception ex)
        {
            return (false, string.Empty, tempDirectoryRoot, $"Error extracting archive: {ex.Message}");
        }
    }

    private static void ExtractSevenZipArchive(string archivePath, string outputDirectory, Action<string> onLog)
    {
        var directExtractionSuccess = false;
        try
        {
            using var stream = File.OpenRead(archivePath);
            using var archive = SevenZipArchive.OpenArchive(stream);
            foreach (var entry in archive.Entries.Where(static e => !e.IsDirectory))
            {
                if (entry.Key != null)
                {
                    var destinationPath = Path.Combine(outputDirectory, entry.Key);
                    var directory = Path.GetDirectoryName(destinationPath);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    entry.WriteToFile(destinationPath);
                }
            }

            directExtractionSuccess = true;
        }
        catch (Exception ex)
        {
            onLog($"Direct extraction failed ({ex.Message}). Attempting fallback with local copy...");
        }

        if (!directExtractionSuccess)
        {
            // Sanitize path for extraction
            var sanitizedArchivePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.7z");
            try
            {
                File.Copy(archivePath, sanitizedArchivePath, true);
                using var stream = File.OpenRead(sanitizedArchivePath);
                using var archive = SevenZipArchive.OpenArchive(stream);
                foreach (var entry in archive.Entries.Where(static e => !e.IsDirectory))
                {
                    if (entry.Key != null)
                    {
                        var destinationPath = Path.Combine(outputDirectory, entry.Key);
                        var directory = Path.GetDirectoryName(destinationPath);
                        if (!string.IsNullOrEmpty(directory))
                        {
                            Directory.CreateDirectory(directory);
                        }

                        entry.WriteToFile(destinationPath);
                    }
                }
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
    }

    private static void ExtractRarArchive(string archivePath, string outputDirectory, Action<string> onLog)
    {
        var directExtractionSuccess = false;
        try
        {
            using var stream = File.OpenRead(archivePath);
            using var archive = RarArchive.OpenArchive(stream);
            foreach (var entry in archive.Entries.Where(static e => !e.IsDirectory))
            {
                if (entry.Key != null)
                {
                    var destinationPath = Path.Combine(outputDirectory, entry.Key);
                    var directory = Path.GetDirectoryName(destinationPath);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    entry.WriteToFile(destinationPath);
                }
            }

            directExtractionSuccess = true;
        }
        catch (Exception ex)
        {
            onLog($"Direct extraction failed ({ex.Message}). Attempting fallback with local copy...");
        }

        if (!directExtractionSuccess)
        {
            // Sanitize path for extraction
            var sanitizedArchivePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.rar");
            try
            {
                File.Copy(archivePath, sanitizedArchivePath, true);
                using var stream = File.OpenRead(sanitizedArchivePath);
                using var archive = RarArchive.OpenArchive(stream);
                foreach (var entry in archive.Entries.Where(static e => !e.IsDirectory))
                {
                    if (entry.Key != null)
                    {
                        var destinationPath = Path.Combine(outputDirectory, entry.Key);
                        var directory = Path.GetDirectoryName(destinationPath);
                        if (!string.IsNullOrEmpty(directory))
                        {
                            Directory.CreateDirectory(directory);
                        }

                        entry.WriteToFile(destinationPath);
                    }
                }
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
    }

    public void Dispose()
    {
        // No unmanaged resources to dispose
        GC.SuppressFinalize(this);
    }
}
