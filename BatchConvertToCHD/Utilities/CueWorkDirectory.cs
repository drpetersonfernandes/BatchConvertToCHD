using System.IO;

namespace BatchConvertToCHD.Utilities;

/// <summary>
/// Prepares an isolated ASCII work directory for a cue/toc descriptor when chdman cannot be handed
/// the original file as-is: non-UTF-8 cue text, non-ASCII names or paths, or referenced names that
/// needed zero-padding correction. The work directory contains a canonicalized cue plus every
/// referenced file under safe ASCII names (trackNN.ext), so chdman sees a self-contained cue set.
/// </summary>
internal static class CueWorkDirectory
{
    /// <summary>
    /// Creates the work directory and copies the cue set into it, or returns a result with null
    /// paths when the descriptor can be converted directly. On failure, the work directory is
    /// removed and the exception is rethrown.
    /// </summary>
    /// <param name="cuePath">Path of the .cue or .toc descriptor.</param>
    /// <param name="tempDirPrefix">Prefix used for the work directory name (e.g. "BatchConvertToCHD_Temp_").</param>
    /// <param name="mp3Decoder">Optional MP3 decoder; when provided, MP3 audio tracks are decoded to WAV in the work directory instead of being copied.</param>
    /// <param name="onLog">Optional logging callback.</param>
    /// <param name="token">Cancellation token.</param>
    internal static async Task<CueWorkDirectoryResult> PrepareAsync(
        string cuePath, string tempDirPrefix, IMp3Decoder? mp3Decoder = null, Action<string>? onLog = null, CancellationToken token = default)
    {
        var result = await CueNormalizer.NormalizeAsync(cuePath, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        if (result.UnresolvedNames.Count > 0)
        {
            return new CueWorkDirectoryResult(null, null, result.UnresolvedNames);
        }

        var isUtf8 = string.Equals(result.SourceEncoding.WebName, "utf-8", StringComparison.OrdinalIgnoreCase);
        var hasMp3Tracks = mp3Decoder is not null && result.References.Any(static r => string.Equals(r.TrackType, "MP3", StringComparison.Ordinal));
        var needsWorkDir = !isUtf8 || result.NeedsRewrite || result.ReferencesChanged || hasMp3Tracks ||
                           cuePath.Any(static c => c > 127) ||
                           result.References.Any(static r => r.ReferencedName.Any(static c => c > 127));
        if (!needsWorkDir)
        {
            return new CueWorkDirectoryResult(null, null, []);
        }

        var workDir = PathUtils.GetBestTempDirectory(cuePath, cuePath, tempDirPrefix);
        Directory.CreateDirectory(workDir);

        try
        {
            // Assign a unique ASCII work name (trackNN.ext) to every referenced file.
            // MP3 tracks are decoded to trackNN.wav so chdman can consume them.
            var workNames = new Dictionary<string, string>(StringComparer.Ordinal); // FullPath -> work name
            var workTypes = new Dictionary<string, string>(StringComparer.Ordinal); // FullPath -> replacement track type
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < result.References.Count; i++)
            {
                var reference = result.References[i];
                string workName;
                if (mp3Decoder is not null && string.Equals(reference.TrackType, "MP3", StringComparison.Ordinal))
                {
                    workName = $"track{i + 1:D2}.wav";
                    workTypes[reference.FullPath] = "WAVE";
                }
                else
                {
                    var extension = Path.GetExtension(reference.ResolvedFullPath);
                    workName = $"track{i + 1:D2}{extension}";
                }

                var baseName = workName;
                var suffix = 1;
                while (!usedNames.Add(workName))
                {
                    workName = $"{Path.GetFileNameWithoutExtension(baseName)}_{suffix++}{Path.GetExtension(baseName)}";
                }

                workNames[reference.FullPath] = workName;
            }

            // Copy (or decode) every referenced file into the work directory.
            foreach (var reference in result.References)
            {
                var workName = workNames[reference.FullPath];
                onLog?.Invoke($"Preparing {Path.GetFileName(reference.ResolvedFullPath)} for conversion...");
                if (mp3Decoder is not null && string.Equals(reference.TrackType, "MP3", StringComparison.Ordinal))
                {
                    await mp3Decoder.DecodeAsync(reference.ResolvedFullPath, Path.Combine(workDir, workName), onLog, token).ConfigureAwait(false);
                }
                else
                {
                    await CopyWithRetryAsync(reference.ResolvedFullPath, Path.Combine(workDir, workName), token).ConfigureAwait(false);
                }
            }

            var normalized = await CueNormalizer.NormalizeAsync(cuePath, token, Transform).ConfigureAwait(false);
            var workCue = Path.Combine(workDir, "game.cue");
            await CueNormalizer.WriteCanonicalCueAsync(workCue, normalized, token).ConfigureAwait(false);
            return new CueWorkDirectoryResult(workCue, workDir, []);

            // Rewrite the cue so its FILE lines reference the ASCII work names.
            (string Name, string? TrackType)? Transform(CueFileReference reference)
            {
                if (!workNames.TryGetValue(reference.FullPath, out var workName))
                {
                    return null;
                }

                return workTypes.TryGetValue(reference.FullPath, out var workType)
                    ? (workName, workType)
                    : (workName, reference.TrackType);
            }
        }
        catch
        {
            try
            {
                if (Directory.Exists(workDir)) Directory.Delete(workDir, true);
            }
            catch
            {
                // ignored
            }

            throw;
        }
    }

    private static async Task CopyWithRetryAsync(string source, string dest, CancellationToken token)
    {
        const int maxAttempts = 4;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            token.ThrowIfCancellationRequested();

            try
            {
                await Task.Run(() => File.Copy(source, dest, true), token).ConfigureAwait(false);
                return;
            }
            catch (IOException) when (attempt < maxAttempts - 1)
            {
                await Task.Delay(300 * (attempt + 1), token).ConfigureAwait(false);
            }
        }
    }
}
