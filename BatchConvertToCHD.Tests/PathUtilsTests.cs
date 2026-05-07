using BatchConvertToCHD.Utilities;

namespace BatchConvertToCHD.Tests;

public class PathUtilsTests
{
    [Theory]
    [InlineData("game.iso", "game.iso")]
    [InlineData("file:name.txt", "file_name.txt")]
    [InlineData("name\0.txt", "name_.txt")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void SanitizeFileNameReplacesInvalidChars(string? input, string expected)
    {
        var result = PathUtils.SanitizeFileName(input ?? string.Empty);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void SanitizeFileNameRemovesTrailingPeriods()
    {
        var result = PathUtils.SanitizeFileName("file.");
        Assert.Equal("file_", result);
    }

    [Fact]
    public void SanitizeFileNameReplacesEllipsis()
    {
        var result = PathUtils.SanitizeFileName("game…iso");
        Assert.Equal("game_ellipsis_iso", result);
    }

    [Fact]
    public void GetSafeTempFileNameReturnsCorrectPath()
    {
        var tempDir = Path.GetTempPath();
        var result = PathUtils.GetSafeTempFileName("game.iso", "chd", tempDir);
        Assert.Equal(Path.Combine(tempDir, "game.chd"), result);
    }

    [Fact]
    public void GetSafeTempFileNameUsesGuidWhenEmptyName()
    {
        var tempDir = Path.GetTempPath();
        var result = PathUtils.GetSafeTempFileName(".iso", "chd", tempDir);
        Assert.True(Path.GetFileNameWithoutExtension(result).Length > 0);
        Assert.Equal(".chd", Path.GetExtension(result));
    }

    [Fact]
    public void ValidateAndNormalizePathEmptyPathReturnsNull()
    {
        string? capturedError = null;
        var result = PathUtils.ValidateAndNormalizePath("", "test folder", msg => { capturedError = msg; }, static _ => { });
        Assert.Null(result);
        Assert.NotNull(capturedError);
    }

    [Fact]
    public void ValidateAndNormalizePathNonExistentDirectoryReturnsNull()
    {
        string? capturedError = null;
        var nonExistent = Path.Combine(Path.GetTempPath(), $"NonExistent_{Guid.NewGuid():N}");
        var result = PathUtils.ValidateAndNormalizePath(nonExistent, "test folder", msg => { capturedError = msg; }, static _ => { });
        Assert.Null(result);
        Assert.NotNull(capturedError);
    }

    [Fact]
    public void ValidateAndNormalizePathValidDirectoryReturnsNormalizedPath()
    {
        var tempDir = Path.GetTempPath();
        var result = PathUtils.ValidateAndNormalizePath(tempDir, "temp", static _ => { }, static _ => { });
        Assert.NotNull(result);
        Assert.Equal(Path.GetFullPath(tempDir), result);
    }

    [Fact]
    public void ValidateAndNormalizePathNullPathReturnsNull()
    {
        string? capturedError = null;
        var result = PathUtils.ValidateAndNormalizePath(null, "test folder", msg => { capturedError = msg; }, static _ => { });
        Assert.Null(result);
        Assert.NotNull(capturedError);
    }

    [Fact]
    public void ValidateAndNormalizePathInvalidCharsReturnsNull()
    {
        string? capturedError = null;
        var result = PathUtils.ValidateAndNormalizePath("\0invalid", "invalid path", msg => { capturedError = msg; }, static _ => { });
        Assert.Null(result);
        Assert.NotNull(capturedError);
    }

    [Fact]
    public void ValidateAndNormalizePathWhitespaceOnlyReturnsNull()
    {
        string? capturedError = null;
        var result = PathUtils.ValidateAndNormalizePath("   ", "test folder", msg => { capturedError = msg; }, static _ => { });
        Assert.Null(result);
        Assert.NotNull(capturedError);
    }

    [Fact]
    public void GetSafeRelativePathSameRootReturnsRelativePath()
    {
        var root = Path.GetPathRoot(Path.GetTempPath()) ?? @"C:\";
        var path1 = Path.Combine(root, "dir1", "subdir");
        var path2 = Path.Combine(root, "dir1", "subdir", "sub2", "file.txt");

        var result = PathUtils.GetSafeRelativePath(path1, path2);
        Assert.NotEqual(".", result);
        Assert.Contains("sub2", result, StringComparison.Ordinal);
    }

    [Fact]
    public void GetSafeRelativePathDifferentRootReturnsDot()
    {
        var result = PathUtils.GetSafeRelativePath(@"C:\dir1", @"D:\dir2");
        Assert.Equal(".", result);
    }

    [Fact]
    public void GetSafeRelativePathInvalidPathReturnsDot()
    {
        var result = PathUtils.GetSafeRelativePath(string.Empty, @"C:\test");
        Assert.Equal(".", result);
    }

    [Fact]
    public void GetSafeTempFileNamePreservesExtension()
    {
        var tempDir = Path.GetTempPath();
        var result = PathUtils.GetSafeTempFileName("game.cue", "iso", tempDir);
        Assert.EndsWith(".iso", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetSafeTempFileNameSanitizesInput()
    {
        var tempDir = Path.GetTempPath();
        var result = PathUtils.GetSafeTempFileName("game:test.iso", "chd", tempDir);
        var fileName = Path.GetFileNameWithoutExtension(result);
        Assert.DoesNotContain(":", fileName, StringComparison.Ordinal);
    }

    [Fact]
    public void SanitizeFileNameAllInvalidCharsReplaced()
    {
        const string input = "a<b>c:d\"e/f\\g|h?i*j";
        var result = PathUtils.SanitizeFileName(input);
        Assert.DoesNotContain("<", result, StringComparison.Ordinal);
        Assert.DoesNotContain(">", result, StringComparison.Ordinal);
        Assert.DoesNotContain(":", result, StringComparison.Ordinal);
        Assert.DoesNotContain("\"", result, StringComparison.Ordinal);
        Assert.DoesNotContain("/", result, StringComparison.Ordinal);
        Assert.DoesNotContain("\\", result, StringComparison.Ordinal);
        Assert.DoesNotContain("|", result, StringComparison.Ordinal);
        Assert.DoesNotContain("?", result, StringComparison.Ordinal);
        Assert.DoesNotContain("*", result, StringComparison.Ordinal);
    }
}
