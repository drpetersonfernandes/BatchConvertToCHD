using BatchConvertToCHD.Utilities;

namespace BatchConvertToCHD.Tests;

public class RetryingFileOperationsTests : IDisposable
{
    private readonly string _tempDir;

    public RetryingFileOperationsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"RetryingFileOperationsTests_{Guid.NewGuid():N}");
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
    public async Task TryDeleteAsyncExistingFileDeletesAndReturnsTrue()
    {
        var path = Path.Combine(_tempDir, "game.bin");
        await File.WriteAllTextAsync(path, "data");

        var deleted = await RetryingFileOperations.TryDeleteAsync(path, CancellationToken.None);

        Assert.True(deleted);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task TryDeleteAsyncMissingFileReturnsTrue()
    {
        var deleted = await RetryingFileOperations.TryDeleteAsync(Path.Combine(_tempDir, "missing.bin"), CancellationToken.None);

        Assert.True(deleted);
    }

    [Fact]
    public async Task TryDeleteAsyncReadOnlyFileClearsAttributeAndDeletes()
    {
        var path = Path.Combine(_tempDir, "readonly.bin");
        await File.WriteAllTextAsync(path, "data");
        File.SetAttributes(path, FileAttributes.ReadOnly);

        try
        {
            var deleted = await RetryingFileOperations.TryDeleteAsync(
                path, CancellationToken.None,
                backoffMsProvider: static _ => 1);

            Assert.True(deleted);
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }
        }
    }

    [Fact]
    public async Task TryDeleteAsyncLockedFileRetriesThenGivesUp()
    {
        var path = Path.Combine(_tempDir, "locked.bin");
        await File.WriteAllTextAsync(path, "data");
        var retries = 0;

        // Hold an exclusive lock for the whole call so every attempt fails.
        await using var lockStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var deleted = await RetryingFileOperations.TryDeleteAsync(
            path, CancellationToken.None,
            _ => { retries++; },
            static _ => 1);

        Assert.False(deleted);
        Assert.Equal(RetryingFileOperations.MaxDeleteAttempts - 1, retries);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task TryDeleteAsyncLockedFileSucceedsAfterLockReleased()
    {
        var path = Path.Combine(_tempDir, "released.bin");
        await File.WriteAllTextAsync(path, "data");
        var attempts = 0;
        var lockStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        try
        {
            var deleted = await RetryingFileOperations.TryDeleteAsync(
                path, CancellationToken.None,
                _ =>
                {
                    attempts++;
                    if (attempts == 2)
                    {
                        // Release the lock so the next attempt succeeds.
                        lockStream.Dispose();
                    }
                },
                static _ => 1);

            Assert.True(deleted);
            Assert.False(File.Exists(path));
        }
        finally
        {
            lockStream.Dispose();
        }
    }
}
