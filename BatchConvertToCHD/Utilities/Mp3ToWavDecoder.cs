using System.IO;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace BatchConvertToCHD.Utilities;

/// <summary>
/// MP3 → WAV decoder backed by Windows Media Foundation (NAudio.MediaFoundationReader).
/// chdman cannot read MP3 audio tracks in cue sheets, so MP3 tracks are decoded to WAV
/// before conversion.
/// </summary>
internal sealed class Mp3ToWavDecoder : IMp3Decoder
{
    /// <inheritdoc />
    /// <param name="mp3Path">Path of the MP3 file to decode.</param>
    /// <param name="wavPath">Destination path for the decoded 16-bit PCM WAV file.</param>
    /// <param name="onLog">Optional logging callback.</param>
    /// <param name="token">Cancellation token.</param>
    public Task DecodeAsync(string mp3Path, string wavPath, Action<string>? onLog, CancellationToken token)
    {
        return Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            onLog?.Invoke($"MP3: Decoding {Path.GetFileName(mp3Path)} to WAV (required for chdman)...");

            // Media Foundation Startup/Shutdown flips a static flag in NAudio without locking,
            // so concurrent decodes (parallel conversions) must be serialized.
            lock (MediaFoundationLock)
            {
                MediaFoundationApi.Startup();
                try
                {
                    using var reader = new MediaFoundationReader(mp3Path);
                    // Force 16-bit PCM output — some Media Foundation codecs produce IEEE float,
                    // which chdman cannot consume in cue WAVE tracks.
                    WaveFileWriter.CreateWaveFile16(wavPath, reader.ToSampleProvider());
                }
                finally
                {
                    MediaFoundationApi.Shutdown();
                }
            }

            token.ThrowIfCancellationRequested();
        }, token);
    }

    private static readonly Lock MediaFoundationLock = new();
}
