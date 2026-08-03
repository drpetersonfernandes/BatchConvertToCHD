using System.Text;
using BatchConvertToCHD.Utilities;

namespace BatchConvertToCHD.Tests;

public class CueWorkDirectoryTests : IDisposable
{
    private readonly string _tempDir;

    static CueWorkDirectoryTests()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public CueWorkDirectoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"CueWorkDirectoryTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            /* ignore */
        }

        GC.SuppressFinalize(this);
    }

    private string CreateFile(string name, string content, Encoding? encoding = null)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content, encoding ?? Encoding.UTF8);
        return path;
    }

    private sealed class FakeMp3Decoder : IMp3Decoder
    {
        public List<(string Mp3Path, string WavPath)> Calls { get; } = [];

        public Task DecodeAsync(string mp3Path, string wavPath, Action<string>? onLog, CancellationToken token)
        {
            Calls.Add((mp3Path, wavPath));
            File.WriteAllText(wavPath, "wav-content");
            return Task.CompletedTask;
        }
    }

    private static async Task<(CueWorkDirectoryResult Result, string? WorkDir)> PrepareAsync(string cuePath, IMp3Decoder? decoder = null)
    {
        var result = await CueWorkDirectory.PrepareAsync(cuePath, "TestPrefix_", decoder, null, CancellationToken.None);
        return (result, result.WorkDir);
    }

    [Fact]
    public async Task PrepareAsyncCanonicalAsciiCueNeedsNoWorkDir()
    {
        CreateFile("track1.bin", "dummy");
        var cuePath = CreateFile("game.cue", "FILE \"track1.bin\" BINARY\r\n  TRACK 01 MODE2/2352\r\n    INDEX 01 00:00:00");

        var (result, workDir) = await PrepareAsync(cuePath);

        Assert.Null(result.WorkCuePath);
        Assert.Null(workDir);
        Assert.Empty(result.UnresolvedNames);
    }

    [Fact]
    public async Task PrepareAsyncKoreanCp949CueCreatesAsciiWorkDirWithAllFiles()
    {
        const string koreanName = "진설 사무라이 스피리츠 무사도열전.bin";
        var sourceBinPath = CreateFile(koreanName, "bin-content");
        var cuePath = CreateFile(
            "game.cue",
            $"FILE \"{koreanName}\" BINARY\r\n  TRACK 01 MODE2/2352\r\n    INDEX 01 00:00:00",
            Encoding.GetEncoding(949));

        var (result, workDir) = await PrepareAsync(cuePath);

        try
        {
            Assert.NotNull(workDir);
            Assert.NotNull(result.WorkCuePath);
            Assert.True(Directory.Exists(workDir));

            var files = Directory.GetFiles(workDir).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
            Assert.Equal(["game.cue", "track01.bin"], files);

            var workCue = await File.ReadAllTextAsync(result.WorkCuePath, Encoding.UTF8);
            Assert.Contains("FILE \"track01.bin\" BINARY", workCue, StringComparison.Ordinal);

            var copiedBin = Path.Combine(workDir, "track01.bin");
            Assert.Equal(await File.ReadAllTextAsync(sourceBinPath), await File.ReadAllTextAsync(copiedBin));
        }
        finally
        {
            if (workDir is not null)
            {
                try
                {
                    Directory.Delete(workDir, true);
                }
                catch
                {
                    /* ignore */
                }
            }
        }
    }

    [Fact]
    public async Task PrepareAsyncZeroPaddingMismatchCreatesWorkDirWithResolvedName()
    {
        CreateFile("Game (Track 2).bin", "dummy");
        var cuePath = CreateFile(
            "game.cue",
            "FILE \"Game (Track 02).bin\" BINARY\r\n  TRACK 01 MODE2/2352\r\n    INDEX 01 00:00:00");

        var (result, workDir) = await PrepareAsync(cuePath);

        try
        {
            Assert.NotNull(workDir);
            Assert.NotNull(result.WorkCuePath);
            Assert.Equal(["game.cue", "track01.bin"], Directory.GetFiles(workDir).Select(Path.GetFileName).OrderBy(static f => f, StringComparer.Ordinal).ToList());

            var workCue = await File.ReadAllTextAsync(result.WorkCuePath, Encoding.UTF8);
            Assert.Contains("FILE \"track01.bin\" BINARY", workCue, StringComparison.Ordinal);
        }
        finally
        {
            if (workDir is not null)
            {
                try
                {
                    Directory.Delete(workDir, true);
                }
                catch
                {
                    /* ignore */
                }
            }
        }
    }

    [Fact]
    public async Task PrepareAsyncMissingReferenceReturnsNoWorkDirAndReportsUnresolved()
    {
        var cuePath = CreateFile("game.cue", "FILE \"missing.bin\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00");

        var (result, workDir) = await PrepareAsync(cuePath);

        Assert.Null(result.WorkCuePath);
        Assert.Null(workDir);
        Assert.Contains("missing.bin", result.UnresolvedNames, StringComparer.Ordinal);
    }

    [Fact]
    public async Task PrepareAsyncMp3TrackDecodesToWavInWorkDir()
    {
        var mp3Path = CreateFile("track1.mp3", "mp3-content");
        var cuePath = CreateFile(
            "game.cue",
            "FILE \"track1.mp3\" MP3\r\n  TRACK 01 AUDIO\r\n    INDEX 01 00:00:00");
        var decoder = new FakeMp3Decoder();

        var (result, workDir) = await PrepareAsync(cuePath, decoder);

        try
        {
            Assert.NotNull(workDir);
            Assert.NotNull(result.WorkCuePath);
            Assert.Equal(["game.cue", "track01.wav"], Directory.GetFiles(workDir).Select(Path.GetFileName).OrderBy(static f => f, StringComparer.Ordinal).ToList());

            var workCue = await File.ReadAllTextAsync(result.WorkCuePath, Encoding.UTF8);
            Assert.Contains("FILE \"track01.wav\" WAVE", workCue, StringComparison.Ordinal);
            Assert.DoesNotContain("track1.mp3", workCue, StringComparison.Ordinal);

            var call = Assert.Single(decoder.Calls);
            Assert.Equal(mp3Path, call.Mp3Path);
            Assert.EndsWith("track01.wav", call.WavPath, StringComparison.Ordinal);
        }
        finally
        {
            if (workDir is not null)
            {
                try
                {
                    Directory.Delete(workDir, true);
                }
                catch
                {
                    /* ignore */
                }
            }
        }
    }

    [Fact]
    public async Task PrepareAsyncMp3TrackWithoutDecoderRunsDirectConversion()
    {
        CreateFile("track1.mp3", "mp3-content");
        var cuePath = CreateFile(
            "game.cue",
            "FILE \"track1.mp3\" MP3\r\n  TRACK 01 AUDIO\r\n    INDEX 01 00:00:00");

        var (result, workDir) = await PrepareAsync(cuePath);

        // Without a decoder the cue is canonical ASCII, so no work directory is prepared and
        // chdman's own "Unhandled track type MP3" error surfaces to the user.
        Assert.Null(result.WorkCuePath);
        Assert.Null(workDir);
        Assert.Empty(result.UnresolvedNames);
    }

    [Fact]
    public async Task PrepareAsyncKeepsWaveAndAiffTracksAsIs()
    {
        // WAVE/AIFF tracks are already supported by chdman — only MP3 gets decoded.
        CreateFile("Game (Track 2).bin", "dummy");
        CreateFile("track2.wav", "wav-data");
        CreateFile("track3.aiff", "aiff-data");
        var cuePath = CreateFile(
            "game.cue",
            "FILE \"Game (Track 02).bin\" BINARY\r\n  TRACK 01 MODE2/2352\r\n    INDEX 01 00:00:00\r\n" +
            "FILE \"track2.wav\" WAVE\r\n  TRACK 02 AUDIO\r\n    INDEX 01 00:00:00\r\n" +
            "FILE \"track3.aiff\" AIFF\r\n  TRACK 03 AUDIO\r\n    INDEX 01 00:00:00");

        var (result, workDir) = await PrepareAsync(cuePath, new FakeMp3Decoder());

        try
        {
            Assert.NotNull(workDir);
            Assert.Equal(
                ["game.cue", "track01.bin", "track02.wav", "track03.aiff"],
                Directory.GetFiles(workDir).Select(Path.GetFileName).OrderBy(static f => f, StringComparer.Ordinal).ToList());

            var workCue = await File.ReadAllTextAsync(result.WorkCuePath!, Encoding.UTF8);
            Assert.Contains("FILE \"track01.bin\" BINARY", workCue, StringComparison.Ordinal);
            Assert.Contains("FILE \"track02.wav\" WAVE", workCue, StringComparison.Ordinal);
            Assert.Contains("FILE \"track03.aiff\" AIFF", workCue, StringComparison.Ordinal);
        }
        finally
        {
            if (workDir is not null)
            {
                try
                {
                    Directory.Delete(workDir, true);
                }
                catch
                {
                    /* ignore */
                }
            }
        }
    }

    [Fact]
    public async Task Mp3ToWavDecoderDecodesCraftedSilenceMp3()
    {
        // Craft a stream of MPEG-1 Layer III frames (128 kbps, 44.1 kHz, stereo) filled with silence.
        const int frameSize = 417;
        const int frameCount = 100;
        var frames = new byte[frameSize * frameCount];
        var header = new byte[] { 0xFF, 0xFB, 0x90, 0x00 };
        for (var i = 0; i < frameCount; i++)
        {
            Array.Copy(header, 0, frames, i * frameSize, header.Length);
        }

        var mp3Path = Path.Combine(_tempDir, "silence.mp3");
        await File.WriteAllBytesAsync(mp3Path, frames);
        var wavPath = Path.Combine(_tempDir, "silence.wav");

        var decoder = new Mp3ToWavDecoder();
        await decoder.DecodeAsync(mp3Path, wavPath, null, CancellationToken.None);

        Assert.True(File.Exists(wavPath));
        Assert.True(new FileInfo(wavPath).Length > 44, "WAV file should have a RIFF header plus samples");

        await using var fs = File.OpenRead(wavPath);
        var head = new byte[4];
        Assert.Equal(4, fs.Read(head, 0, 4));
        Assert.Equal("RIFF", Encoding.ASCII.GetString(head));
    }
}
