using System.IO.Compression;
using BatchConvertToCHD.Services;
using SharpCompressZipArchive = SharpCompress.Archives.Zip.ZipArchive;

namespace BatchConvertToCHD.Tests;

public class ArchiveServiceTests : IDisposable
{
    private readonly string _tempDir;

    public ArchiveServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ArchiveServiceTests_{Guid.NewGuid():N}");
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
            // ignore cleanup errors
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ExtractArchiveAsyncMissingFileReturnsFailureAndLogsWarning()
    {
        var service = new ArchiveService("maxcso.exe", false);
        var missingPath = Path.Combine(_tempDir, "missing.7z");
        var tempDir = Path.Combine(_tempDir, "extract");
        var logs = new List<string>();

        var result = await service.ExtractArchiveAsync(missingPath, tempDir, logs.Add, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains(logs, static msg => msg.Contains("WARNING", StringComparison.OrdinalIgnoreCase) && msg.Contains("missing", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("File not found", result.ErrorMessage);
    }

    [Fact]
    public async Task ExtractArchiveAsyncUnsupportedExtensionReturnsFailure()
    {
        var service = new ArchiveService("maxcso.exe", false);
        var filePath = Path.Combine(_tempDir, "test.tar");
        File.WriteAllText(filePath, "dummy");
        var tempDir = Path.Combine(_tempDir, "extract");

        var result = await service.ExtractArchiveAsync(filePath, tempDir, static _ => { }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Unsupported archive type", result.ErrorMessage);
    }

    [Fact]
    public async Task ExtractArchiveAsyncValidZipExtractsFiles()
    {
        var service = new ArchiveService("maxcso.exe", false);
        var zipPath = Path.Combine(_tempDir, "test.zip");
        var tempDir = Path.Combine(_tempDir, "extract");
        Directory.CreateDirectory(tempDir);

        // Create a zip with an ISO file inside
        await using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("game.iso");
            await using (var stream = entry.Open())
            {
                stream.WriteByte(0x01);
            }
        }

        var result = await service.ExtractArchiveAsync(zipPath, tempDir, static _ => { }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(result.FilePaths);
        Assert.EndsWith("game.iso", result.FilePaths[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExtractArchiveAsyncValidZipNoPrimaryFilesReturnsFailure()
    {
        var service = new ArchiveService("maxcso.exe", false);
        var zipPath = Path.Combine(_tempDir, "test.zip");
        var tempDir = Path.Combine(_tempDir, "extract");
        Directory.CreateDirectory(tempDir);

        // Create a zip with a txt file (not a primary target)
        await using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("readme.txt");
            await using (var stream = entry.Open())
            {
                stream.WriteByte(0x01);
            }
        }

        var result = await service.ExtractArchiveAsync(zipPath, tempDir, static _ => { }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("No supported primary files found", result.ErrorMessage);
    }

    [Fact]
    public async Task ExtractCsoAsyncMaxCsoNotAvailableReturnsFailure()
    {
        var service = new ArchiveService("maxcso.exe", false);
        var tempIso = Path.Combine(_tempDir, "out.iso");

        var result = await service.ExtractCsoAsync("input.cso", tempIso, _tempDir, static _ => { }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("maxcso.exe is not available", result.ErrorMessage);
    }

    [Fact]
    public void DisposeDoesNotThrow()
    {
        var service = new ArchiveService("maxcso.exe", false);
        var exception = Record.Exception(service.Dispose);
        Assert.Null(exception);
    }

    [Fact]
    public void ExtractArchiveWithFallbackMissingFileThrowsFileNotFoundWithoutFallback()
    {
        var missingPath = Path.Combine(_tempDir, $"missing_{Guid.NewGuid():N}.7z");
        var outputDir = Path.Combine(_tempDir, "out");
        Directory.CreateDirectory(outputDir);
        var logs = new List<string>();

        var ex = Record.Exception(() =>
        {
            ArchiveService.ExtractArchiveWithFallback<SharpCompressZipArchive>(
                missingPath,
                outputDir,
                logs.Add,
                ".7z",
                static _ => throw new InvalidOperationException("Should not reach here"),
                CancellationToken.None);
        });

        Assert.NotNull(ex);
        Assert.IsType<FileNotFoundException>(ex);
        Assert.Contains(logs, static msg => msg.Contains("Skipping fallback", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExtractArchiveAsyncInvalidSevenZipReturnsFailure()
    {
        var service = new ArchiveService("maxcso.exe", false);
        var sevenZPath = Path.Combine(_tempDir, "invalid.7z");
        File.WriteAllText(sevenZPath, "not a valid 7z archive");
        var tempDir = Path.Combine(_tempDir, "extract");

        var result = await service.ExtractArchiveAsync(sevenZPath, tempDir, static _ => { }, CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task ExtractArchiveAsyncInvalidRarReturnsFailure()
    {
        var service = new ArchiveService("maxcso.exe", false);
        var rarPath = Path.Combine(_tempDir, "invalid.rar");
        File.WriteAllText(rarPath, "not a valid rar archive");
        var tempDir = Path.Combine(_tempDir, "extract");

        var result = await service.ExtractArchiveAsync(rarPath, tempDir, static _ => { }, CancellationToken.None);

        Assert.False(result.Success);
    }
}
