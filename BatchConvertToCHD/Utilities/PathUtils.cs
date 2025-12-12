using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace BatchConvertToCHD.Utilities;

public static class PathUtils
{
    public static string SanitizeFileName(string name)
    {
        var invalidChars = Regex.Escape(new string(Path.GetInvalidFileNameChars()));
        var invalidRegStr = string.Format(CultureInfo.InvariantCulture, @"([{0}]*\.+$)|([{0}]+)", invalidChars);
        var sanitizedName = Regex.Replace(name, invalidRegStr, "_");

        // Further replace common problematic characters
        sanitizedName = sanitizedName.Replace("…", "_ellipsis_")
            .Replace("â€¦", "_ellipsis_");
        return sanitizedName;
    }

    public static string GetSafeTempFileName(string originalFileNameWithExtension, string desiredExtensionWithoutDot, string tempDirectory)
    {
        var safeBaseName = Guid.NewGuid().ToString("N");
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

    public static void CheckForMarkOfTheWeb(string filePath, string fileName, Action<string> onLog, Action<string, string> onAlert)
    {
        try
        {
            var zoneIdentifierPath = filePath + ":Zone.Identifier";
            if (File.Exists(zoneIdentifierPath))
            {
                onLog($"WARNING: {fileName} has a 'Mark of the Web' (downloaded from internet). This may block execution.");
                onLog($"To fix: Right-click {fileName} → Properties → Check 'Unblock' → Apply");

                onAlert(
                    $"{fileName} appears to be downloaded from the internet and may be blocked by Windows.\n\n" +
                    $"To unblock it:\n1. Right-click {fileName}\n2. Select Properties\n3. Check 'Unblock' at the bottom\n4. Click Apply/OK",
                    "Security Warning");
            }
        }
        catch (Exception ex)
        {
            onLog($"Could not check Zone.Identifier for {fileName}: {ex.Message}");
        }
    }
}