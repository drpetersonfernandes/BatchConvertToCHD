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
}
