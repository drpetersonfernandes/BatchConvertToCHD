using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Security;
using BatchConvertToCHD.Utilities;
using SharpCompress.Archives;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Archives.Rar;

namespace BatchConvertToCHD.Services;

/// <summary>
/// Service for extracting various archive formats (ZIP, 7Z, RAR, CSO) used in the conversion process.
/// </summary>
public class ArchiveService : IDisposable
{
    private readonly string _maxCsoPath;
    private readonly bool _isMaxCsoAvailable;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArchiveService"/> class.
    /// </summary>
    /// <param name="maxCsoPath">The path to the maxcso executable for CSO decompression.</param>
    /// <param name="isMaxCsoAvailable">Whether maxcso is available on the system.</param>
    public ArchiveService(string maxCsoPath, bool isMaxCsoAvailable)
    {
        _maxCsoPath = maxCsoPath;
        _isMaxCsoAvailable = isMaxCsoAvailable;
    }

    /// <summary>
    /// Extracts a CSO (Compressed ISO) file to a temporary ISO file.
    /// </summary>
    /// <param name="originalCsoPath">The path to the CSO file to extract.</param>
    /// <param name="tempOutputIsoPath">The path where the extracted ISO should be saved.</param>
    /// <param name="tempDirectoryRoot">The root temporary directory for extraction.</param>
    /// <param name="onLog">Callback for logging messages.</param>
    /// <param name="onSpeedUpdate">Callback for speed updates during extraction.</param>
    /// <param name="token">Cancellation token to cancel the operation.</param>
    /// <returns>A tuple containing success status, file path, temp directory, and error message.</returns>
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
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                ErrorDialog = false
            };

            var outputBuilder = new StringBuilder();
            process.OutputDataReceived += (_, args) =>
            {
                if (args.Data != null)
                {
                    outputBuilder.AppendLine(args.Data);
                }
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data == null)
                {
                    return;
                }

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
                if (!process.HasExited)
                {
                    process.Kill(true);
                }
            }
            catch
            {
                /* ignore */
            }

            throw;
        }
    }

    /// <summary>
    /// Extracts an archive file (ZIP, 7Z, or RAR) to a temporary directory.
    /// </summary>
    /// <param name="originalArchivePath">The path to the archive file to extract.</param>
    /// <param name="tempDirectoryRoot">The root temporary directory for extraction.</param>
    /// <param name="onLog">Callback for logging messages.</param>
    /// <param name="token">Cancellation token to cancel the operation.</param>
    /// <returns>A tuple containing success status, list of extracted file paths, temp directory, and error message.</returns>
    public async Task<(bool Success, List<string> FilePaths, string TempDir, string ErrorMessage)> ExtractArchiveAsync(
        string originalArchivePath,
        string tempDirectoryRoot,
        Action<string> onLog,
        CancellationToken token)
    {
        var extension = Path.GetExtension(originalArchivePath);
        var archiveFileName = Path.GetFileName(originalArchivePath);

        if (!File.Exists(originalArchivePath))
        {
            onLog($"WARNING: File not found, skipping extraction: {originalArchivePath}");
            return (false, new List<string>(), tempDirectoryRoot, $"File not found: {originalArchivePath}");
        }

        try
        {
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(tempDirectoryRoot);
            onLog($"Extracting {archiveFileName} to: {tempDirectoryRoot}");

            if (extension.Equals(FileExtensions.Zip, StringComparison.OrdinalIgnoreCase))
            {
                await Task.Run(() => ExtractZipArchive(originalArchivePath, tempDirectoryRoot, token), token);
            }
            else if (extension.Equals(FileExtensions.SevenZip, StringComparison.OrdinalIgnoreCase))
            {
                await Task.Run(() => ExtractSevenZipArchive(originalArchivePath, tempDirectoryRoot, onLog, token), token);
            }
            else if (extension.Equals(FileExtensions.Rar, StringComparison.OrdinalIgnoreCase))
            {
                await Task.Run(() => ExtractRarArchive(originalArchivePath, tempDirectoryRoot, onLog, token), token);
            }
            else
            {
                return (false, new List<string>(), tempDirectoryRoot, $"Unsupported archive type: {extension}");
            }

            token.ThrowIfCancellationRequested();

            var foundFiles = await Task.Run(() =>
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true
                };

                return Directory.GetFiles(tempDirectoryRoot, "*.*", options)
                    .Where(static f => FileExtensions.PrimaryTargetExtensionsSet.Contains(Path.GetExtension(f)))
                    .ToList();
            }, token);

            return foundFiles.Count > 0
                ? (true, foundFiles, tempDirectoryRoot, string.Empty)
                : (false, new List<string>(), tempDirectoryRoot, "No supported primary files found in archive.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Report bug if service is available
            try
            {
                if (App.SharedBugReportService != null)
                {
                    _ = App.SharedBugReportService.SendBugReportAsync("Error extracting archive", ex);
                }
            }
            catch
            {
                // Ignore errors in bug reporting to avoid infinite loops
            }

            return (false, new List<string>(), tempDirectoryRoot, $"Error extracting archive: {ex.Message}");
        }
    }

    private static void ExtractZipArchive(string archivePath, string outputDirectory, CancellationToken token)
    {
        var fullOutputDirectory = Path.GetFullPath(outputDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(entry.Name))
            {
                continue; // Skip directories
            }

            var destinationPath = Path.Combine(outputDirectory, entry.FullName);
            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (!Path.GetFullPath(destinationPath).StartsWith(fullOutputDirectory, StringComparison.OrdinalIgnoreCase))
            {
                throw new SecurityException("Attempted to extract file outside of the target directory.");
            }

            entry.ExtractToFile(destinationPath, true);
        }
    }

    private static void ExtractSevenZipArchive(string archivePath, string outputDirectory, Action<string> onLog, CancellationToken token)
    {
        ExtractArchiveWithFallback(
            archivePath,
            outputDirectory,
            onLog,
            FileExtensions.SevenZip,
            static stream => SevenZipArchive.OpenArchive(stream),
            token);
    }

    private static void ExtractRarArchive(string archivePath, string outputDirectory, Action<string> onLog, CancellationToken token)
    {
        ExtractArchiveWithFallback(
            archivePath,
            outputDirectory,
            onLog,
            FileExtensions.Rar,
            static stream => RarArchive.OpenArchive(stream),
            token);
    }

    /// <summary>
    /// Extracts an archive with fallback logic for handling extraction failures.
    /// If direct extraction fails, copies the archive to a temp location and retries.
    /// </summary>
    private static void ExtractArchiveWithFallback<TArchive>(
        string archivePath,
        string outputDirectory,
        Action<string> onLog,
        string tempExtension,
        Func<Stream, TArchive> openArchive,
        CancellationToken token) where TArchive : IArchive, IDisposable
    {
        var fullOutputDirectory = Path.GetFullPath(outputDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var directExtractionSuccess = false;

        try
        {
            using var stream = File.OpenRead(archivePath);
            using var archive = openArchive(stream);
            ExtractArchiveEntries(archive, outputDirectory, fullOutputDirectory, token);
            directExtractionSuccess = true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            onLog($"Direct extraction failed ({ex.Message}). Skipping fallback because source file is missing.");
            throw;
        }
        catch (Exception ex)
        {
            onLog($"Direct extraction failed ({ex.Message}). Attempting fallback with local copy...");
            // Report bug if service is available
            try
            {
                if (App.SharedBugReportService != null)
                {
                    _ = App.SharedBugReportService.SendBugReportAsync("Direct extraction failed", ex);
                }
            }
            catch
            {
                // Ignore errors in bug reporting to avoid infinite loops
            }
        }

        if (directExtractionSuccess)
        {
            return;
        }

        // Fallback: Copy to temp location and extract from there
        var sanitizedArchivePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}{tempExtension}");
        try
        {
            File.Copy(archivePath, sanitizedArchivePath, true);
            using var stream = File.OpenRead(sanitizedArchivePath);
            using var archive = openArchive(stream);
            ExtractArchiveEntries(archive, outputDirectory, fullOutputDirectory, token);
        }
        finally
        {
            TryDeleteFile(sanitizedArchivePath);
        }
    }

    /// <summary>
    /// Extracts entries from an archive with security checks.
    /// </summary>
    private static void ExtractArchiveEntries(IArchive archive, string outputDirectory, string fullOutputDirectory, CancellationToken token)
    {
        foreach (var entry in archive.Entries.Where(static e => !e.IsDirectory))
        {
            token.ThrowIfCancellationRequested();

            if (entry.Key == null)
            {
                continue;
            }

            var destinationPath = Path.Combine(outputDirectory, entry.Key);
            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (!Path.GetFullPath(destinationPath).StartsWith(fullOutputDirectory, StringComparison.OrdinalIgnoreCase))
            {
                throw new SecurityException("Attempted to extract file outside of the target directory.");
            }

            entry.WriteToFile(destinationPath);
        }
    }

    /// <summary>
    /// Attempts to delete a file, ignoring any errors.
    /// </summary>
    private static void TryDeleteFile(string filePath)
    {
        try
        {
            File.Delete(filePath);
        }
        catch
        {
            /* ignore */
        }
    }

    /// <summary>
    /// Releases all resources used by the <see cref="ArchiveService"/>.
    /// </summary>
    public void Dispose()
    {
        // No unmanaged resources to dispose
        GC.SuppressFinalize(this);
    }
}
