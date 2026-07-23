using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Security;
using BatchConvertToCHD.Utilities;
using Serilog;
using SharpCompress.Archives;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Archives.Rar;
using SharpCompress.Common;

namespace BatchConvertToCHD.Services;

/// <summary>
/// Handles decompression and extraction of compressed disc image and archive files.
/// Supports CSO (via maxcso), ZIP (System.IO.Compression with 7za fallback),
/// 7z and RAR (via SharpCompress with 7za fallback).
/// Implements <see cref="IDisposable"/> for deterministic cleanup.
/// </summary>
public class ArchiveService : IDisposable
{
    private readonly string _maxCsoPath;
    private readonly bool _isMaxCsoAvailable;
    private readonly string _sevenZipExePath;
    private readonly bool _isSevenZipAvailable;
    private static readonly ILogger Logger = Log.ForContext<ArchiveService>();

    /// <summary>
    /// Initializes a new instance of the <see cref="ArchiveService"/> class.
    /// </summary>
    /// <param name="maxCsoPath">The full path to the maxcso executable.</param>
    /// <param name="isMaxCsoAvailable">Whether the maxcso executable was found on disk.</param>
    /// <param name="sevenZipExePath">The full path to the 7za executable.</param>
    /// <param name="isSevenZipAvailable">Whether the 7za executable was found on disk.</param>
    public ArchiveService(string maxCsoPath, bool isMaxCsoAvailable, string sevenZipExePath, bool isSevenZipAvailable)
    {
        _maxCsoPath = maxCsoPath;
        _isMaxCsoAvailable = isMaxCsoAvailable;
        _sevenZipExePath = sevenZipExePath;
        _isSevenZipAvailable = isSevenZipAvailable;
    }

    /// <summary>
    /// Decompresses a CSO (Compressed ISO) file to a temporary ISO file using maxcso.
    /// </summary>
    /// <param name="originalCsoPath">The full path to the source CSO file.</param>
    /// <param name="tempOutputIsoPath">The full path where the decompressed ISO should be written.</param>
    /// <param name="tempDirectoryRoot">The root temporary directory where the output will be placed.</param>
    /// <param name="onLog">Callback for logging progress messages.</param>
    /// <param name="token">Cancellation token to abort the operation.</param>
    /// <returns>
    /// A tuple containing success status, the output ISO file path, the temp directory root,
    /// and an error message string (empty on success).
    /// </returns>
    public async Task<(bool Success, string FilePath, string TempDir, string ErrorMessage)> ExtractCsoAsync(
        string originalCsoPath,
        string tempOutputIsoPath,
        string tempDirectoryRoot,
        Action<string> onLog,
        CancellationToken token)
    {
        if (!_isMaxCsoAvailable)
            return (false, string.Empty, tempDirectoryRoot, "maxcso.exe is not available.");

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
            await process.WaitForExitAsync(token);

            if (process.ExitCode != 0 || !File.Exists(tempOutputIsoPath))
                return (false, string.Empty, tempDirectoryRoot, $"MaxCSO failed. Exit code: {process.ExitCode}. Output: {outputBuilder}");

            onLog($"Successfully decompressed {csoFileName}");
            return (true, tempOutputIsoPath, tempDirectoryRoot, string.Empty);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(true); } catch { /* ignore */ }

            throw;
        }
    }

    /// <summary>
    /// Extracts a compressed archive (ZIP, 7z, or RAR) to a temporary directory.
    /// Uses System.IO.Compression for ZIP with 7za fallback, SharpCompress for 7z and RAR.
    /// </summary>
    /// <param name="originalArchivePath">The full path to the source archive file.</param>
    /// <param name="tempDirectoryRoot">The root directory where extracted files will be placed.</param>
    /// <param name="onLog">Callback for logging progress messages.</param>
    /// <param name="token">Cancellation token to abort the operation.</param>
    /// <returns>
    /// A tuple containing success status, the list of extracted primary file paths,
    /// the temp directory root, and an error message string (empty on success).
    /// </returns>
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
            return (false, [], tempDirectoryRoot, $"File not found: {originalArchivePath}");
        }

        try
        {
            token.ThrowIfCancellationRequested();

            var spaceError = CheckTempDiskSpace(originalArchivePath, tempDirectoryRoot, archiveFileName);
            if (spaceError != null)
            {
                onLog($"ERROR: {spaceError}");
                return (false, [], tempDirectoryRoot, spaceError);
            }

            Directory.CreateDirectory(tempDirectoryRoot);
            onLog($"Extracting {archiveFileName} to: {tempDirectoryRoot}");

            if (extension.Equals(FileExtensions.Zip, StringComparison.OrdinalIgnoreCase))
                await ExtractZipWith7ZaFallbackAsync(originalArchivePath, tempDirectoryRoot, onLog, token);
            else if (extension.Equals(FileExtensions.SevenZip, StringComparison.OrdinalIgnoreCase))
                await ExtractSevenZipArchiveAsync(originalArchivePath, tempDirectoryRoot, onLog, token);
            else if (extension.Equals(FileExtensions.Rar, StringComparison.OrdinalIgnoreCase))
                await Task.Run(() => ExtractRarArchive(originalArchivePath, tempDirectoryRoot, onLog, token), token);
            else
                return (false, [], tempDirectoryRoot, $"Unsupported archive type: {extension}");

            token.ThrowIfCancellationRequested();

            var foundFiles = await Task.Run(() =>
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
                return Directory.GetFiles(tempDirectoryRoot, "*.*", options)
                    .Where(static f => FileExtensions.PrimaryTargetExtensionsSet.Contains(Path.GetExtension(f)))
                    .ToList();
            }, token);

            return foundFiles.Count > 0
                ? (true, foundFiles, tempDirectoryRoot, string.Empty)
                : (false, new List<string>(), tempDirectoryRoot, "No supported primary files found in archive.");
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidDataException ex)
        {
            if (ex.Message.Contains("unsupported compression method", StringComparison.OrdinalIgnoreCase))
            {
                return (false, [], tempDirectoryRoot,
                    "The archive file uses a compression method that is not supported by the built-in ZIP extractor (e.g., Deflate64, LZMA, PPMd). Try re-compressing the archive with standard Deflate compression, or extract it manually with 7-Zip or WinRAR and add the extracted files for conversion.");
            }
            return (false, [], tempDirectoryRoot, $"The archive file may be corrupted or incomplete and could not be extracted. Try re-downloading or re-copying the file, then attempt the conversion again. Details: {ex.Message}");
        }
        catch (IncompleteArchiveException ex) { return (false, [], tempDirectoryRoot, $"The archive file appears to be incomplete and could not be fully extracted. Try re-downloading or re-copying the file, then attempt the conversion again. Details: {ex.Message}"); }
        catch (Exception ex) when (ex.GetType().FullName == "SharpCompress.Compressors.LZMA.DataErrorException") { return (false, [], tempDirectoryRoot, $"The archive file may be corrupted and could not be extracted. Try re-downloading or re-copying the file, then attempt the conversion again. Details: {ex.Message}"); }
        catch (CryptographicException ex) { return (false, [], tempDirectoryRoot, $"Archive is encrypted/password-protected and cannot be processed. Please extract it manually and add the extracted files. Details: {ex.Message}"); }
        catch (InvalidFormatException ex) { return (false, [], tempDirectoryRoot, $"The archive file may be corrupted or in an unsupported format and could not be extracted. Try re-downloading or re-copying the file, then attempt the conversion again. Details: {ex.Message}"); }
        catch (ArchiveOperationException ex) { return (false, [], tempDirectoryRoot, $"The archive file may be corrupted or unsupported and could not be extracted. Try re-downloading or re-copying the file, then attempt the conversion again. Details: {ex.Message}"); }
        catch (IndexOutOfRangeException) { return (false, [], tempDirectoryRoot, "The archive file may be corrupted or incomplete and could not be extracted. Try re-downloading or re-copying the file, then attempt the conversion again."); }
        catch (IOException ex) when (IsDiskFullException(ex))
        {
            var driveRoot = Path.GetPathRoot(Path.GetFullPath(tempDirectoryRoot))?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ?? "temp drive";
            var archiveSizeGb = new FileInfo(originalArchivePath).Length / (1024.0 * 1024.0 * 1024.0);
            return (false, [], tempDirectoryRoot, $"Not enough disk space on {driveRoot}. The archive ({archiveFileName}, {archiveSizeGb:F1} GB uncompressed) cannot be extracted. Free up space on {driveRoot} or change your system TEMP directory to a drive with more space.");
        }
        catch (IOException ex) when (!IsDiskFullException(ex))
        {
            var errorMsg = ex.Message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase)
                ? $"The archive file '{archiveFileName}' is locked by another application. Close any programs that may be using the file (e.g., antivirus, file explorer, another instance) and try again. Details: {ex.Message}"
                : $"File access error while extracting '{archiveFileName}': {ex.Message}";
            return (false, [], tempDirectoryRoot, errorMsg);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error extracting archive: {FileName}", archiveFileName);
            return (false, [], tempDirectoryRoot, $"Error extracting archive: {ex.Message}");
        }
    }

    private async Task ExtractZipWith7ZaFallbackAsync(string archivePath, string outputDirectory, Action<string> onLog, CancellationToken token)
    {
        var archiveFileName = Path.GetFileName(archivePath);
        try
        {
            await Task.Run(() => ExtractZipArchive(archivePath, outputDirectory, token), token);
        }
        catch (Exception ex) when (_isSevenZipAvailable && ex is not OperationCanceledException)
        {
            onLog($"Built-in zip extractor failed for {archiveFileName} ({ex.Message}). Falling back to 7za.exe...");
            await ExtractWith7ZaAsync(archivePath, outputDirectory, onLog, token);
        }
    }

    private static void ExtractZipArchive(string archivePath, string outputDirectory, CancellationToken token)
    {
        var fullOutputDirectory = Path.GetFullPath(outputDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var directExtractionSuccess = false;
        try { ExtractZipWithOpenRead(archivePath, outputDirectory, fullOutputDirectory, token);
            directExtractionSuccess = true; }
        catch (IOException ex) when (!IsDiskFullException(ex))
        {
            Logger.Debug(ex, "Direct ZIP extraction failed, will fall back to temp-copy extraction");
        }

        if (directExtractionSuccess) return;

        var tempCopyPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}{FileExtensions.Zip}");
        try { File.Copy(archivePath, tempCopyPath, true);
            ExtractZipWithOpenRead(tempCopyPath, outputDirectory, fullOutputDirectory, token); }
        finally { TryDeleteFile(tempCopyPath); }
    }

    private static void ExtractZipWithOpenRead(string archivePath, string outputDirectory, string fullOutputDirectory, CancellationToken token)
    {
        const int maxRetries = 3;
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                using var archive = ZipFile.OpenRead(archivePath);
                foreach (var entry in archive.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    if (string.IsNullOrEmpty(entry.Name)) continue;

                    var destinationPath = Path.Combine(outputDirectory, entry.FullName);
                    var directory = Path.GetDirectoryName(destinationPath);
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) Directory.CreateDirectory(directory);
                    if (!Path.GetFullPath(destinationPath).StartsWith(fullOutputDirectory, StringComparison.OrdinalIgnoreCase))
                        throw new SecurityException("Attempted to extract file outside of the target directory.");

                    entry.ExtractToFile(destinationPath, true);
                }

                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < maxRetries)
            {
                Thread.Sleep(attempt * 1000);
            }
        }
    }

    private async Task ExtractSevenZipArchiveAsync(string archivePath, string outputDirectory, Action<string> onLog, CancellationToken token)
    {
        try { ExtractArchiveWithFallback(archivePath, outputDirectory, onLog, FileExtensions.SevenZip, static stream => SevenZipArchive.OpenArchive(stream), token); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_isSevenZipAvailable)
            {
                onLog($"SharpCompress extraction failed ({ex.Message}). Trying 7za.exe fallback...");
                await ExtractWith7ZaAsync(archivePath, outputDirectory, onLog, token);
            }
            else
            {
                throw;
            }
        }
    }

    private async Task ExtractWith7ZaAsync(string archivePath, string outputDirectory, Action<string> onLog, CancellationToken token)
    {
        onLog($"Extracting with 7za.exe: {Path.GetFileName(archivePath)}");

        using var process = new Process();
        var outputBuilder = new StringBuilder();
        var outputLock = new object();

        try
        {
            process.StartInfo = new ProcessStartInfo
            {
                FileName = _sevenZipExePath,
                Arguments = $"x \"{archivePath}\" -o\"{outputDirectory}\" -y",
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true, ErrorDialog = false
            };
            process.OutputDataReceived += (_, args) =>
            {
                if (args.Data != null) { lock (outputLock) { outputBuilder.AppendLine(args.Data); } }
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data != null) { lock (outputLock) { outputBuilder.AppendLine(args.Data); } }
            };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(token);

            string outputText;
            lock (outputLock) { outputText = outputBuilder.ToString(); }

            if (process.ExitCode != 0)
            {
                if (process.ExitCode == 2 || outputText.Contains("Is not archive", StringComparison.OrdinalIgnoreCase) ||
                    outputText.Contains("Cannot open", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"7za.exe: archive is invalid or corrupt ({Path.GetFileName(archivePath)}). Output: {outputText}");

                throw new InvalidOperationException($"7za.exe extraction failed with exit code {process.ExitCode}. Output: {outputText}");
            }

            onLog($"Successfully extracted {Path.GetFileName(archivePath)} with 7za.exe");
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(true); } catch { /* ignore */ }

            throw;
        }
    }

    private static void ExtractRarArchive(string archivePath, string outputDirectory, Action<string> onLog, CancellationToken token)
    {
        ExtractArchiveWithFallback(archivePath, outputDirectory, onLog, FileExtensions.Rar, static stream => RarArchive.OpenArchive(stream), token);
    }

    /// <summary>
    /// Extracts archive entries using the specified archive opener function, with a fallback
    /// strategy that copies the archive to a temp file before extraction if the direct
    /// stream-based extraction fails.
    /// </summary>
    /// <typeparam name="TArchive">The SharpCompress archive type.</typeparam>
    /// <param name="archivePath">Full path to the archive file.</param>
    /// <param name="outputDirectory">Directory where extracted files will be written.</param>
    /// <param name="onLog">Callback for logging messages.</param>
    /// <param name="tempExtension">File extension to use for the temporary copy.</param>
    /// <param name="openArchive">Factory function that opens an archive from a stream.</param>
    /// <param name="token">Cancellation token to abort the operation.</param>
    internal static void ExtractArchiveWithFallback<TArchive>(
        string archivePath, string outputDirectory, Action<string> onLog, string tempExtension,
        Func<Stream, TArchive> openArchive, CancellationToken token) where TArchive : IArchive, IDisposable
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
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            onLog($"Direct extraction failed ({ex.Message}). Skipping fallback because source file is missing.");
            throw;
        }
        catch (Exception ex)
        {
            onLog($"Direct extraction failed ({ex.Message}). Attempting fallback with local copy...");

            if (ex is InvalidDataException ||
                ex is IncompleteArchiveException ||
                ex is CryptographicException ||
                ex is ArchiveOperationException ||
                ex is InvalidFormatException ||
                ex is IndexOutOfRangeException ||
                ex is NullReferenceException ||
                ex.GetType().FullName == "SharpCompress.Compressors.LZMA.DataErrorException")
            {
                throw;
            }

            Logger.Error(ex, "Direct extraction failed");
        }

        if (directExtractionSuccess) return;

        var sanitizedArchivePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}{tempExtension}");
        try
        {
            File.Copy(archivePath, sanitizedArchivePath, true);
            using var stream = File.OpenRead(sanitizedArchivePath);
            using var archive = openArchive(stream);
            ExtractArchiveEntries(archive, outputDirectory, fullOutputDirectory, token);
        }
        finally { TryDeleteFile(sanitizedArchivePath); }
    }

    private static void ExtractArchiveEntries(IArchive archive, string outputDirectory, string fullOutputDirectory, CancellationToken token)
    {
        foreach (var entry in archive.Entries.Where(static e => !e.IsDirectory))
        {
            token.ThrowIfCancellationRequested();
            if (entry.Key == null) continue;

            var destinationPath = Path.Combine(outputDirectory, entry.Key);
            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) Directory.CreateDirectory(directory);
            if (!Path.GetFullPath(destinationPath).StartsWith(fullOutputDirectory, StringComparison.OrdinalIgnoreCase))
                throw new SecurityException("Attempted to extract file outside of the target directory.");

            WriteEntryWithRetry(entry, destinationPath);
        }
    }

    private static void WriteEntryWithRetry(IArchiveEntry entry, string destinationPath)
    {
        const int maxRetries = 3;
        for (var attempt = 1;; attempt++)
        {
            try { entry.WriteToFile(destinationPath);
                return; }
            catch (IOException) when (attempt < maxRetries) { Thread.Sleep(attempt * 1000); }
        }
    }

    private static void TryDeleteFile(string filePath)
    {
        try { File.Delete(filePath); } catch { /* ignore */ }
    }

    private static bool IsDiskFullException(Exception ex)
    {
        return ex is IOException { HResult: -2147024784 or -2147024783 };
    }

    private static string? CheckTempDiskSpace(string originalArchivePath, string tempDirectoryRoot, string archiveFileName)
    {
        try
        {
            var driveRoot = Path.GetPathRoot(Path.GetFullPath(tempDirectoryRoot));
            if (string.IsNullOrEmpty(driveRoot)) return null;

            var drive = new DriveInfo(driveRoot);
            if (!drive.IsReady) return null;

            var availableSpace = drive.AvailableFreeSpace;

            long estimatedUncompressedSize;
            try { estimatedUncompressedSize = EstimateArchiveUncompressedSize(originalArchivePath); }
            catch { estimatedUncompressedSize = new FileInfo(originalArchivePath).Length; }

            var safetyMargin = Math.Max(estimatedUncompressedSize / 10, 100L * 1024 * 1024);
            var requiredSpace = estimatedUncompressedSize + safetyMargin;

            if (availableSpace < requiredSpace)
            {
                var requiredGb = requiredSpace / (1024.0 * 1024.0 * 1024.0);
                var availableGb = availableSpace / (1024.0 * 1024.0 * 1024.0);
                return $"Not enough disk space on {driveRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)}. Need at least {requiredGb:F1} GB to extract {archiveFileName}, but only {availableGb:F1} GB available. Free up space or change your system TEMP directory to a drive with more space.";
            }
        }
        catch
        {
            // ignored
        }

        return null;
    }

    private static long EstimateArchiveUncompressedSize(string archivePath)
    {
        var extension = Path.GetExtension(archivePath);
        if (extension.Equals(FileExtensions.Zip, StringComparison.OrdinalIgnoreCase))
        {
            using var archive = ZipFile.OpenRead(archivePath);
            return archive.Entries.Sum(static e => e.Length);
        }

        return new FileInfo(archivePath).Length;
    }

    /// <summary>
    /// Releases resources used by the <see cref="ArchiveService"/>.
    /// </summary>
    public void Dispose()
    {
    }
}
