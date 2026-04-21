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
    /// Validates and normalizes a directory path. Returns null if invalid.
    /// </summary>
    public static string? ValidateAndNormalizePath(string path, string pathName, Action<string> onError, Action<string> onLog)
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
            return null;
        }
    }
}