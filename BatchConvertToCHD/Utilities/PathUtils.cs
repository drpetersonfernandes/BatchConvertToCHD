using System.IO;
using System.Text;

namespace BatchConvertToCHD.Utilities;

/// <summary>
/// Provides utility methods for path manipulation and validation.
/// </summary>
public static class PathUtils
{
    // Cache invalid filename chars to avoid repeated allocation
    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

    /// <summary>
    /// Sanitizes a file name by replacing invalid characters with underscores.
    /// Also removes trailing periods which are problematic on Windows.
    /// </summary>
    /// <param name="name">The file name to sanitize.</param>
    /// <returns>A sanitized file name safe for use in the file system.</returns>
    public static string SanitizeFileName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            // Replace invalid filename chars with underscore
            if (Array.IndexOf(InvalidFileNameChars, c) >= 0)
            {
                sb.Append('_');
            }
            else
            {
                sb.Append(c);
            }
        }

        // Remove trailing periods (problematic on Windows)
        while (sb.Length > 0 && sb[^1] == '.')
        {
            sb.Length--;
            if (sb.Length > 0)
                sb.Append('_');
        }

        var sanitizedName = sb.ToString();

        // Replace common problematic Unicode ellipsis characters
        sanitizedName = sanitizedName.Replace("…", "_ellipsis_")
            .Replace("â€¦", "_ellipsis_");

        return sanitizedName;
    }

    /// <summary>
    /// Generates a safe temporary file name based on the original file name.
    /// </summary>
    /// <param name="originalFileNameWithExtension">The original file name with extension.</param>
    /// <param name="desiredExtensionWithoutDot">The desired extension without the dot (e.g., "iso").</param>
    /// <param name="tempDirectory">The temporary directory path.</param>
    /// <returns>A full path to a safe temporary file.</returns>
    public static string GetSafeTempFileName(string originalFileNameWithExtension, string desiredExtensionWithoutDot, string tempDirectory)
    {
        var sanitizedName = SanitizeFileName(Path.GetFileNameWithoutExtension(originalFileNameWithExtension));
        var safeBaseName = string.IsNullOrEmpty(sanitizedName) ? Guid.NewGuid().ToString("N") : sanitizedName;
        return Path.Combine(tempDirectory, safeBaseName + "." + desiredExtensionWithoutDot);
    }

    /// <summary>
    /// Computes a relative path from <paramref name="relativeTo"/> to <paramref name="path"/>,
    /// falling back to "." when the paths are on different drives/roots (which
    /// <see cref="Path.GetRelativePath"/> does not support and will throw for).
    /// </summary>
    public static string GetSafeRelativePath(string relativeTo, string path)
    {
        try
        {
            var root1 = Path.GetPathRoot(relativeTo);
            var root2 = Path.GetPathRoot(path);
            if (string.Equals(root1, root2, StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetRelativePath(relativeTo, path);
            }
        }
        catch
        {
            // If GetPathRoot or GetRelativePath itself throws for any reason
        }

        return ".";
    }

    /// <summary>
    /// Returns the full path to a new unique temporary directory, selecting the drive
    /// with the most available free space from among the input file's drive,
    /// the output folder's drive, the system temp drive, and any other fixed drives.
    /// When <paramref name="requiredBytes"/> is specified, prefers a drive that has
    /// enough free space for the operation, even if it is not the drive with the most
    /// total free space. Falls back to the system temp path if no suitable alternative is found.
    /// </summary>
    /// <param name="inputFilePath">Path to the input file (used to prioritize its drive).</param>
    /// <param name="outputFolderPath">Path to the output folder (used to prioritize its drive).</param>
    /// <param name="tempDirPrefix">Prefix for the temporary directory name (e.g., "BatchConvertToCHD_Temp_").</param>
    /// <param name="requiredBytes">Minimum free space required in bytes. When &gt; 0, a drive meeting this requirement is preferred over the drive with the most total free space.</param>
    /// <returns>The full path to a new temporary directory that has not yet been created on disk.</returns>
    public static string GetBestTempDirectory(string? inputFilePath, string? outputFolderPath, string tempDirPrefix, long requiredBytes = 0)
    {
        const long minFreeBytes = 1024L * 1024 * 1024; // 1 GB minimum to consider a drive viable

        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddCandidateRoot(inputFilePath);
        AddCandidateRoot(outputFolderPath);

        var systemTempRoot = Path.GetPathRoot(Path.GetTempPath());
        if (!string.IsNullOrEmpty(systemTempRoot))
            candidates.Add(systemTempRoot);

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive is { IsReady: true, DriveType: DriveType.Fixed })
                    candidates.Add(drive.RootDirectory.FullName);
            }
            catch
            {
                // ignored
            }
        }

        string? bestRoot = null;
        long bestFree = 0;
        string? bestRootMeetingRequirement = null;
        long bestFreeMeetingRequirement = 0;

        foreach (var root in candidates)
        {
            try
            {
                var drive = new DriveInfo(root);
                if (drive.IsReady && drive.DriveType != DriveType.Network)
                {
                    if (drive.AvailableFreeSpace > bestFree)
                    {
                        bestFree = drive.AvailableFreeSpace;
                        bestRoot = root;
                    }

                    if (requiredBytes > 0 && drive.AvailableFreeSpace >= requiredBytes &&
                        drive.AvailableFreeSpace > bestFreeMeetingRequirement)
                    {
                        bestFreeMeetingRequirement = drive.AvailableFreeSpace;
                        bestRootMeetingRequirement = root;
                    }
                }
            }
            catch
            {
                // ignored
            }
        }

        // Prefer a drive that meets the space requirement; fall back to best overall
        var selectedRoot = bestRootMeetingRequirement ?? bestRoot;
        var selectedFree = bestRootMeetingRequirement != null ? bestFreeMeetingRequirement : bestFree;

        var guid = Guid.NewGuid().ToString("N");
        string basePath;

        if (selectedRoot != null && selectedFree >= minFreeBytes &&
            !string.Equals(selectedRoot, systemTempRoot, StringComparison.OrdinalIgnoreCase))
        {
            basePath = Path.Combine(
                selectedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                "BatchConvertToCHD_Temp");
        }
        else
        {
            basePath = Path.GetTempPath();
        }

        return Path.Combine(basePath, $"{tempDirPrefix}{guid}");

        void AddCandidateRoot(string? path)
        {
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(root))
                    candidates.Add(root);
            }
            catch
            {
                // ignored
            }
        }
    }

    /// <summary>
    /// Collects all base paths that may contain BatchConvertToCHD temp directories,
    /// for use by startup cleanup. Includes the system temp path and the
    /// BatchConvertToCHD_Temp folder on the root of every ready fixed drive.
    /// </summary>
    public static IEnumerable<string> GetPossibleTempBasePaths()
    {
        var paths = new List<string> { Path.GetTempPath() };

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive is { IsReady: true, DriveType: DriveType.Fixed })
                {
                    var altPath = Path.Combine(
                        drive.RootDirectory.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        "BatchConvertToCHD_Temp");
                    if (Directory.Exists(altPath))
                        paths.Add(altPath);
                }
            }
            catch
            {
                // ignored
            }
        }

        return paths;
    }

    /// <summary>
    /// Validates and normalizes a directory path. Returns null if invalid.
    /// </summary>
    public static string? ValidateAndNormalizePath(string? path, string pathName, Action<string> onError, Action<string> onLog)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                onError($"Please select a {pathName}.");
                return null;
            }

            var normalizedPath = Path.GetFullPath(path);

            if (!Directory.Exists(normalizedPath))
            {
                onLog($"ERROR: {pathName} does not exist: {normalizedPath}");
                onError($"The {pathName} does not exist or is not accessible:\n\n{normalizedPath}\n\nPlease verify the path and try again.");
                return null;
            }

            onLog($"Validated {pathName}: {normalizedPath}");
            return normalizedPath;
        }
        catch (Exception ex)
        {
            onLog($"ERROR: Invalid path for {pathName}: {path}. {ex.Message}");
            onError($"The {pathName} path is invalid:\n\n{path}\n\nError: {ex.Message}");

            if (App.SharedBugReportService != null)
            {
                _ = App.SharedBugReportService.SendBugReportAsync($"Invalid path for {pathName}", ex);
            }

            return null;
        }
    }
}