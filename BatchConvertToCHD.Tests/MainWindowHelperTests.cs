namespace BatchConvertToCHD.Tests;

/// <summary>
/// Tests for MainWindow helper methods used by the conversion pipeline.
/// </summary>
public class MainWindowHelperTests : IDisposable
{
    private readonly string _tempDir;

    public MainWindowHelperTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"MainWindowHelperTests_{Guid.NewGuid():N}");
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
    public async Task StripUtf8BomIfPresentAsync_RemovesBom()
    {
        var path = Path.Combine(_tempDir, "bom.cue");
        await File.WriteAllTextAsync(path, "FILE \"track1.bin\" BINARY", System.Text.Encoding.UTF8);

        Assert.Equal(0xEF, (await File.ReadAllBytesAsync(path))[0]);

        await MainWindow.StripUtf8BomIfPresentAsync(path, CancellationToken.None);

        var bytes = await File.ReadAllBytesAsync(path);
        Assert.False(bytes is [0xEF, 0xBB, 0xBF, ..],
            "BOM must be removed");
        Assert.StartsWith("FILE \"track1.bin\" BINARY", System.Text.Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StripUtf8BomIfPresentAsync_LeavesBomFreeFileUntouched()
    {
        var path = Path.Combine(_tempDir, "plain.cue");
        const string content = "FILE \"track1.bin\" BINARY";
        await File.WriteAllTextAsync(path, content, new System.Text.UTF8Encoding(false));

        await MainWindow.StripUtf8BomIfPresentAsync(path, CancellationToken.None);

        Assert.Equal(content, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task StripUtf8BomIfPresentAsync_MissingFileIsIgnored()
    {
        // Best-effort helper: must not throw for a missing file.
        await MainWindow.StripUtf8BomIfPresentAsync(Path.Combine(_tempDir, "nope.cue"), CancellationToken.None);
    }

    [Fact]
    public void SelectChdmanErrorLine_SkipsProgressLinesAndReturnsLastRealError()
    {
        const string errorText = "Compressing, 0.0% complete... (ratio=100.0%)\r\n" +
                                 "Output bytes: 1234\r\n" +
                                 "ERROR: couldn't find bin file [track1.bin]";

        var line = MainWindow.SelectChdmanErrorLine(errorText);

        Assert.Equal("ERROR: couldn't find bin file [track1.bin]", line);
    }

    [Fact]
    public void SelectChdmanErrorLine_ProgressOnly_ReturnsLastLine()
    {
        const string errorText = "Compressing, 10.0% complete... (ratio=95.0%)\n" +
                                 "Converting, 20.0% complete...";

        var line = MainWindow.SelectChdmanErrorLine(errorText);

        Assert.Equal("Converting, 20.0% complete...", line);
    }

    [Fact]
    public void SelectChdmanErrorLine_SingleErrorLineIsReturned()
    {
        var line = MainWindow.SelectChdmanErrorLine("Unit size must be specified if no output parent CHD is supplied");

        Assert.Equal("Unit size must be specified if no output parent CHD is supplied", line);
    }

    [Fact]
    public void SelectChdmanErrorLine_EmptyInputReturnsEmpty()
    {
        Assert.Equal(string.Empty, MainWindow.SelectChdmanErrorLine(string.Empty));
        Assert.Equal(string.Empty, MainWindow.SelectChdmanErrorLine(" \r\n \n "));
    }
}
