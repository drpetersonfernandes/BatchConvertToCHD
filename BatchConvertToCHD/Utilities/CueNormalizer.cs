using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace BatchConvertToCHD.Utilities;

/// <summary>
/// Parses a CUE sheet with encoding detection, resolves every referenced file against the filesystem
/// (exact match, then case-insensitive, then zero-padding-tolerant like "(Track 2)" vs "(Track 02)"),
/// and produces a canonical UTF-8 rewrite of the cue that chdman can consume reliably.
/// </summary>
internal static class CueNormalizer
{
    private static readonly Regex TrackNumberRegex = new(
        @"(?<prefix>.*\(Track\s+)(?<num>\d+)(?<suffix>\).*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.NonBacktracking);

    private static readonly string[] KnownTrackTypes = ["BINARY", "WAVE", "MP3", "AIFF", "MOTOROLA", "AUDIO"];

    /// <summary>
    /// Normalizes the cue at <paramref name="cuePath"/>.
    /// </summary>
    /// <param name="cuePath">Path of the .cue or .toc descriptor to normalize.</param>
    /// <param name="token">Cancellation token.</param>
    /// <param name="transform">Optional transform applied to each resolved FILE line.</param>
    internal static async Task<CueNormalizationResult> NormalizeAsync(
        string cuePath, CancellationToken token, CueFileLineTransform? transform = null)
    {
        var (lines, encoding, hasBom) = await GameFileParser.ReadLinesWithDetectedEncodingAsync(cuePath, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        var directory = Path.GetDirectoryName(cuePath) ?? string.Empty;
        var references = new List<CueFileReference>();
        var unresolved = new List<string>();
        var canonicalLines = new List<string>(lines.Length);
        var needsRewrite = false;
        var referencesChanged = false;

        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();
            if (!trimmedLine.StartsWith("FILE ", StringComparison.OrdinalIgnoreCase) ||
                !GameFileParser.TryGetFileNameFromFileLine(trimmedLine, out var referencedName) ||
                referencedName is null)
            {
                canonicalLines.Add(line);
                continue;
            }

            var trackType = GetTrackType(trimmedLine);
            var reference = ResolveReference(directory, referencedName, trackType);
            references.Add(reference);

            if (!reference.IsResolved)
            {
                unresolved.Add(referencedName);
            }

            var lineName = reference.ResolvedName ?? referencedName;
            var lineType = reference.TrackType;
            if (reference.IsResolved && transform is not null)
            {
                var transformed = transform(reference);
                if (transformed is not null)
                {
                    lineName = transformed.Value.Name;
                    lineType = transformed.Value.TrackType ?? reference.TrackType;
                    referencesChanged = true;
                }
            }

            if (reference.WasNameCorrected)
            {
                referencesChanged = true;
            }

            var canonicalLine = BuildCanonicalFileLine(lineName, lineType);
            if (!string.Equals(canonicalLine, trimmedLine, StringComparison.Ordinal))
            {
                needsRewrite = true;
            }

            canonicalLines.Add(canonicalLine);
        }

        return new CueNormalizationResult(encoding, hasBom, references, unresolved, canonicalLines, needsRewrite, referencesChanged);
    }

    /// <summary>
    /// Writes the canonical cue content to <paramref name="outputPath"/> as UTF-8 (no BOM, CRLF line endings).
    /// </summary>
    /// <param name="outputPath">Destination file path for the canonical cue.</param>
    /// <param name="result">The normalization result whose canonical content is written.</param>
    /// <param name="token">Cancellation token.</param>
    internal static async Task WriteCanonicalCueAsync(string outputPath, CueNormalizationResult result, CancellationToken token)
    {
        await File.WriteAllTextAsync(outputPath, result.CanonicalCueText, new UTF8Encoding(false), token).ConfigureAwait(false);
    }

    private static CueFileReference ResolveReference(string directory, string referencedName, string? trackType)
    {
        var fullPath = Path.Combine(directory, referencedName);
        var fileDirectory = Path.GetDirectoryName(fullPath) ?? directory;
        string[] files;
        try
        {
            files = Directory.Exists(fileDirectory) ? Directory.GetFiles(fileDirectory) : [];
        }
        catch (Exception)
        {
            files = [];
        }

        string? resolved = null;
        var wasNameCorrected = false;
        if (files.Length > 0)
        {
            var fileName = Path.GetFileName(fullPath);
            var match = files.FirstOrDefault(f => string.Equals(Path.GetFileName(f), fileName, StringComparison.Ordinal))
                        ?? files.FirstOrDefault(f => string.Equals(Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                match = FindPadTolerantMatch(files, fileName);
                wasNameCorrected = match is not null;
            }

            if (match is not null)
            {
                resolved = Path.GetRelativePath(directory, match);
            }
        }

        return new CueFileReference(referencedName, resolved, fullPath, trackType, wasNameCorrected);
    }

    private static string? FindPadTolerantMatch(string[] files, string fileName)
    {
        var match = TrackNumberRegex.Match(fileName);
        if (!match.Success)
        {
            return null;
        }

        if (!int.TryParse(match.Groups["num"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var trackNumber))
        {
            return null;
        }

        var variants = new[]
        {
            trackNumber.ToString(CultureInfo.InvariantCulture),
            trackNumber.ToString("D2", CultureInfo.InvariantCulture),
            trackNumber.ToString("D3", CultureInfo.InvariantCulture)
        };

        foreach (var variant in variants)
        {
            var candidateName = match.Groups["prefix"].Value + variant + match.Groups["suffix"].Value;
            var found = files.FirstOrDefault(f => string.Equals(Path.GetFileName(f), candidateName, StringComparison.OrdinalIgnoreCase));
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static string BuildCanonicalFileLine(string name, string? trackType)
    {
        return trackType is null ? $"FILE \"{name}\"" : $"FILE \"{name}\" {trackType}";
    }

    private static string? GetTrackType(string trimmedFileLine)
    {
        var firstQuote = trimmedFileLine.IndexOf('"');
        var lastQuote = trimmedFileLine.LastIndexOf('"');

        string tail;
        if (firstQuote != -1 && lastQuote > firstQuote)
        {
            tail = trimmedFileLine[(lastQuote + 1)..].Trim();
        }
        else
        {
            tail = trimmedFileLine;
        }

        // The track type is the first known type token anywhere in the tail — some descriptors
        // (e.g. cdrdao TOCs) append extra columns after the type.
        foreach (var token in tail.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (KnownTrackTypes.Contains(token, StringComparer.OrdinalIgnoreCase))
            {
                return token.ToUpperInvariant();
            }
        }

        return null;
    }
}
