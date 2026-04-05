using System.IO;
using System.Text;

namespace BatchConvertToCHD.Utilities;

public static class GameFileParser
{
    private static readonly char[] Separator = [' ', '\t'];

    public static async Task<List<string>> GetReferencedFilesFromCueAsync(string cuePath, Action<string> onLog, CancellationToken token)
    {
        var referencedFiles = new List<string>();
        var cueDir = Path.GetDirectoryName(cuePath) ?? string.Empty;
        try
        {
            var lines = await File.ReadAllLinesAsync(cuePath, token);
            token.ThrowIfCancellationRequested();
            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();
                if (!trimmedLine.StartsWith("FILE ", StringComparison.OrdinalIgnoreCase)) continue;

                string fileName;
                var firstQuote = trimmedLine.IndexOf('"');
                var lastQuote = trimmedLine.LastIndexOf('"');

                if (firstQuote != -1 && lastQuote > firstQuote)
                {
                    fileName = trimmedLine.Substring(firstQuote + 1, lastQuote - firstQuote - 1);
                }
                else
                {
                    // Unquoted fallback: split with limit to preserve filename+spaces+type
                    var parts = trimmedLine.Split(Separator, 2, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) continue;

                    // parts[1] is now "filename TYPE" — strip the trailing file type keyword
                    var rest = parts[1].TrimEnd();
                    var lastSpace = rest.LastIndexOf(' ');
                    if (lastSpace > 0)
                    {
                        // Known CUE file type keywords that follow the filename
                        var afterFilename = rest[(lastSpace + 1)..];
                        if (afterFilename.Equals("BINARY", StringComparison.OrdinalIgnoreCase) ||
                            afterFilename.Equals("WAVE", StringComparison.OrdinalIgnoreCase) ||
                            afterFilename.Equals("MP3", StringComparison.OrdinalIgnoreCase) ||
                            afterFilename.Equals("AIFF", StringComparison.OrdinalIgnoreCase) ||
                            afterFilename.Equals("MOTOROLA", StringComparison.OrdinalIgnoreCase))
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

                referencedFiles.Add(Path.Combine(cueDir, fileName));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            onLog($"Error parsing CUE file {Path.GetFileName(cuePath)}: {ex.Message}");
            throw; // Re-throw to be handled by caller/bug reporter
        }

        return referencedFiles;
    }

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
                if (string.IsNullOrWhiteSpace(trimmedLine)) continue;

                var firstQuote = trimmedLine.IndexOf('"');
                var lastQuote = trimmedLine.LastIndexOf('"');

                if (firstQuote != -1 && lastQuote > firstQuote)
                {
                    var fileName = trimmedLine.Substring(firstQuote + 1, lastQuote - firstQuote - 1);
                    referencedFiles.Add(Path.Combine(gdiDir, fileName));
                }
                else
                {
                    // GDI format: <track> <filename> <lba> <sector_size> <offset>
                    // The last 3 fields are always numeric — count backwards to isolate the filename
                    var parts = trimmedLine.Split(Separator, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 5) continue;

                    string fileName;
                    if (parts.Length > 5)
                    {
                        // Filename contains spaces; reconstruct from parts[4..^3]
                        var fileNameParts = parts[4..^3];
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
            onLog($"Error parsing GDI file {Path.GetFileName(gdiPath)}: {ex.Message}");
            throw;
        }

        return referencedFiles;
    }

    public static async Task<List<string>> GetReferencedFilesFromTocAsync(string tocPath, Action<string> onLog, CancellationToken token)
    {
        var referencedFiles = new List<string>();
        var tocDir = Path.GetDirectoryName(tocPath) ?? string.Empty;
        try
        {
            var lines = await File.ReadAllLinesAsync(tocPath, token);
            token.ThrowIfCancellationRequested();
            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();
                if (!trimmedLine.StartsWith("FILE ", StringComparison.OrdinalIgnoreCase)) continue;

                string fileName;
                var firstQuote = trimmedLine.IndexOf('"');
                var lastQuote = trimmedLine.LastIndexOf('"');

                if (firstQuote != -1 && lastQuote > firstQuote)
                {
                    fileName = trimmedLine.Substring(firstQuote + 1, lastQuote - firstQuote - 1);
                }
                else
                {
                    // Unquoted fallback: split with limit to preserve filename+spaces+type
                    var parts = trimmedLine.Split(Separator, 2, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) continue;

                    // parts[1] is now "filename TYPE" — strip the trailing file type keyword
                    var rest = parts[1].TrimEnd();
                    var lastSpace = rest.LastIndexOf(' ');
                    if (lastSpace > 0)
                    {
                        // Known TOC/CUE file type keywords that follow the filename
                        var afterFilename = rest[(lastSpace + 1)..];
                        if (afterFilename.Equals("BINARY", StringComparison.OrdinalIgnoreCase) ||
                            afterFilename.Equals("WAVE", StringComparison.OrdinalIgnoreCase) ||
                            afterFilename.Equals("MP3", StringComparison.OrdinalIgnoreCase) ||
                            afterFilename.Equals("AIFF", StringComparison.OrdinalIgnoreCase) ||
                            afterFilename.Equals("MOTOROLA", StringComparison.OrdinalIgnoreCase))
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

                referencedFiles.Add(Path.Combine(tocDir, fileName));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            onLog($"Error parsing TOC file {Path.GetFileName(tocPath)}: {ex.Message}");
            throw;
        }

        return referencedFiles;
    }
}
