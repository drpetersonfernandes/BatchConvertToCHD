using System.Text;
using BatchConvertToCHD.Utilities;

namespace BatchConvertToCHD.Tests;

public class GameFileParserTests : IDisposable
{
    private readonly string _tempDir;

    public GameFileParserTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"GameFileParserTests_{Guid.NewGuid():N}");
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

    [Fact]
    public async Task GetReferencedFilesFromCueAsyncQuotedFileReturnsReferencedFiles()
    {
        var cuePath = Path.Combine(_tempDir, "game.cue");
        const string content = "FILE \"track1.bin\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00";
        await File.WriteAllTextAsync(cuePath, content);

        var result = await GameFileParser.GetReferencedFilesFromCueAsync(cuePath, static _ => { }, CancellationToken.None);

        Assert.Single(result);
        Assert.Equal(Path.Combine(_tempDir, "track1.bin"), result[0]);
    }

    [Fact]
    public async Task GetReferencedFilesFromCueAsyncUnquotedFileReturnsReferencedFiles()
    {
        var cuePath = Path.Combine(_tempDir, "game.cue");
        const string content = "FILE track1.bin BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00";
        await File.WriteAllTextAsync(cuePath, content);

        var result = await GameFileParser.GetReferencedFilesFromCueAsync(cuePath, static _ => { }, CancellationToken.None);

        Assert.Single(result);
        Assert.Equal(Path.Combine(_tempDir, "track1.bin"), result[0]);
    }

    [Fact]
    public async Task GetReferencedFilesFromCueAsyncMissingFileThrows()
    {
        var cuePath = Path.Combine(_tempDir, "missing.cue");
        await Assert.ThrowsAsync<FileNotFoundException>(async () =>
        {
            await GameFileParser.GetReferencedFilesFromCueAsync(cuePath, static _ => { }, CancellationToken.None);
        });
    }

    [Fact]
    public async Task GetReferencedFilesFromGdiAsyncQuotedFileReturnsReferencedFiles()
    {
        var gdiPath = Path.Combine(_tempDir, "game.gdi");
        const string content = "3\n1 0 4 2352 track01.bin 0\n2 45000 4 2352 \"track 02.raw\" 0\n3 50000 4 2352 track03.bin 0";
        await File.WriteAllTextAsync(gdiPath, content, Encoding.UTF8);

        var result = await GameFileParser.GetReferencedFilesFromGdiAsync(gdiPath, static _ => { }, CancellationToken.None);

        Assert.Equal(3, result.Count);
        Assert.Equal(Path.Combine(_tempDir, "track01.bin"), result[0]);
        Assert.Equal(Path.Combine(_tempDir, "track 02.raw"), result[1]);
        Assert.Equal(Path.Combine(_tempDir, "track03.bin"), result[2]);
    }

    [Fact]
    public async Task GetReferencedFilesFromGdiAsyncSpacedFilenameReturnsReferencedFiles()
    {
        var gdiPath = Path.Combine(_tempDir, "game.gdi");
        const string content = "2\n1 0 4 2352 track one.bin 0\n2 45000 4 2352 track two.bin 0";
        await File.WriteAllTextAsync(gdiPath, content, Encoding.UTF8);

        var result = await GameFileParser.GetReferencedFilesFromGdiAsync(gdiPath, static _ => { }, CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal(Path.Combine(_tempDir, "track one.bin"), result[0]);
        Assert.Equal(Path.Combine(_tempDir, "track two.bin"), result[1]);
    }

    [Fact]
    public async Task GetReferencedFilesFromGdiAsyncMissingFileThrows()
    {
        var gdiPath = Path.Combine(_tempDir, "missing.gdi");
        await Assert.ThrowsAsync<FileNotFoundException>(async () =>
        {
            await GameFileParser.GetReferencedFilesFromGdiAsync(gdiPath, static _ => { }, CancellationToken.None);
        });
    }

    [Fact]
    public async Task GetReferencedFilesFromTocAsyncQuotedFileReturnsReferencedFiles()
    {
        var tocPath = Path.Combine(_tempDir, "game.toc");
        const string content = "FILE \"track1.bin\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00";
        await File.WriteAllTextAsync(tocPath, content);

        var result = await GameFileParser.GetReferencedFilesFromTocAsync(tocPath, static _ => { }, CancellationToken.None);

        Assert.Single(result);
        Assert.Equal(Path.Combine(_tempDir, "track1.bin"), result[0]);
    }

    [Fact]
    public async Task GetReferencedFilesFromTocAsyncMissingFileThrows()
    {
        var tocPath = Path.Combine(_tempDir, "missing.toc");
        await Assert.ThrowsAsync<FileNotFoundException>(async () =>
        {
            await GameFileParser.GetReferencedFilesFromTocAsync(tocPath, static _ => { }, CancellationToken.None);
        });
    }
}
