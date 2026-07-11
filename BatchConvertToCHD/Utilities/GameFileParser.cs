using System.IO;
using System.Text;
using Serilog;

namespace BatchConvertToCHD.Utilities;

/// <summary>
/// Provides methods for parsing game file formats (CUE, GDI, TOC) to extract referenced files.
/// </summary>
public static class GameFileParser
{
    private static readonly char[] Separator = [' ', '\t'];
    private static readonly ILogger Logger = Log.ForContext(typeof(GameFileParser));

    /// <summary>
    /// Extracts referenced file paths from a CUE sheet file.
    /// </summary>
    /// <param name="cuePath">Path to the CUE file to parse.</param>
    /// <param name="onLog">Callback for logging messages.</param>
    /// <param name="token">Cancellation token to cancel the operation.</param>
    /// <returns>A list of file paths referenced by the CUE sheet.</returns>
    public static Task<List<string>> GetReferencedFilesFromCueAsync(string cuePath, Action<string> onLog, CancellationToken token)
    {
        return ParseFileReferenceLinesAsync(cuePath, onLog, "CUE", token);
    }

    /// <summary>
    /// Extracts referenced file paths from a GDI (Dreamcast GD-ROM) file.
    /// </summary>
    /// <param name="gdiPath">Path to the GDI file to parse.</param>
    /// <param name="onLog">Callback for logging messages.</param>
    /// <param name="token">Cancellation token to cancel the operation.</param>
    /// <returns>A list of file paths referenced by the GDI file.</returns>
    public static async Task<List<string>> GetReferencedFilesFromGdiAsync(string gdiPath, Action<string> onLog, CancellationToken token)
    {
        var referencedFiles = new List<string>();
        var gdiDir = Path.GetDirectoryName(gdiPath) ?? string.Empty;
        try
        {
            var lines = await File.ReadAllLinesAsync(gdiPath, Encoding.UTF8, token);
            token.ThrowIfCancellationRequested();
            for (var i = 1; i < lines.Length; i++)
            {
                var trimmedLine = lines[i].Trim();
                if (string.IsNullOrWhiteSpace(trimmedLine))
                {
                    continue;
                }

                var firstQuote = trimmedLine.IndexOf('"');
                var lastQuote = trimmedLine.LastIndexOf('"');

                if (firstQuote != -1 && lastQuote > firstQuote)
                {
                    var fileName = trimmedLine.Substring(firstQuote + 1, lastQuote - firstQuote - 1);
                    referencedFiles.Add(Path.Combine(gdiDir, fileName));
                }
                else
                {
                    var parts = trimmedLine.Split(Separator, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 5)
                    {
                        continue;
                    }

                    string fileName;
                    if (parts.Length > 6)
                    {
                        var fileNameParts = parts[4..^1];
                        fileName = string.Join(' ', fileNameParts);
                    }
                    else
                    {
                        fileName = parts[4];
                    }

                    referencedFiles.Add(Path.Combine(gdiDir, fileName));
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Could not parse GDI file: {FileName}", Path.GetFileName(gdiPath));
            onLog($"[WARNING] Could not parse GDI file: {Path.GetFileName(gdiPath)}. Error: {ex.Message}");
        }

        return referencedFiles;
    }

    /// <summary>
    /// Extracts referenced file paths from a TOC (Table of Contents) file.
    /// </summary>
    /// <param name="tocPath">Path to the TOC file to parse.</param>
    /// <param name="onLog">Callback for logging messages.</param>
    /// <param name="token">Cancellation token to cancel the operation.</param>
    /// <returns>A list of file paths referenced by the TOC file.</returns>
    public static Task<List<string>> GetReferencedFilesFromTocAsync(string tocPath, Action<string> onLog, CancellationToken token)
    {
        return ParseFileReferenceLinesAsync(tocPath, onLog, "TOC", token);
    }

    private static async Task<List<string>> ParseFileReferenceLinesAsync(
        string filePath, Action<string> onLog, string fileType, CancellationToken token)
    {
        var referencedFiles = new List<string>();
        var directory = Path.GetDirectoryName(filePath) ?? string.Empty;
        try
        {
            var lines = await File.ReadAllLinesAsync(filePath, token);
            token.ThrowIfCancellationRequested();
            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();
                if (!trimmedLine.StartsWith("FILE ", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string fileName;
                var firstQuote = trimmedLine.IndexOf('"');
                var lastQuote = trimmedLine.LastIndexOf('"');

                if (firstQuote != -1 && lastQuote > firstQuote)
                {
                    fileName = trimmedLine.Substring(firstQuote + 1, lastQuote - firstQuote - 1);
                }
                else
                {
                    var parts = trimmedLine.Split(Separator, 2, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2)
                    {
                        continue;
                    }

                    var rest = parts[1].TrimEnd();
                    var lastSpace = rest.LastIndexOf(' ');
                    if (lastSpace > 0)
                    {
                        var afterFilename = rest[(lastSpace + 1)..];
                        if (afterFilename.Equals("BINARY", StringComparison.OrdinalIgnoreCase) ||
                            afterFilename.Equals("WAVE", StringComparison.OrdinalIgnoreCase) ||
                            afterFilename.Equals("MP3", StringComparison.OrdinalIgnoreCase) ||
                            afterFilename.Equals("AIFF", StringComparison.OrdinalIgnoreCase) ||
                            afterFilename.Equals("MOTOROLA", StringComparison.OrdinalIgnoreCase) ||
                            afterFilename.Equals("AUDIO", StringComparison.OrdinalIgnoreCase))
                        {
                            fileName = rest[..lastSpace];
                        }
                        else
                        {
                            fileName = rest;
                        }
                    }
                    else
                    {
                        fileName = rest;
                    }
                }

                referencedFiles.Add(Path.Combine(directory, fileName));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Could not parse {FileType} file: {FileName}", fileType, Path.GetFileName(filePath));
            onLog($"[WARNING] Could not parse {fileType} file: {Path.GetFileName(filePath)}. Error: {ex.Message}");
        }

        return referencedFiles;
    }
}
