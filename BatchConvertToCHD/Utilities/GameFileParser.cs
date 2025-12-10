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
            var lines = await File.ReadAllLinesAsync(cuePath, Encoding.UTF8, token);
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
                    var parts = trimmedLine.Split(Separator, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) continue;

                    fileName = parts[1];
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
                    var parts = trimmedLine.Split(Separator, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 5) continue;

                    referencedFiles.Add(Path.Combine(gdiDir, parts[4]));
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
}
