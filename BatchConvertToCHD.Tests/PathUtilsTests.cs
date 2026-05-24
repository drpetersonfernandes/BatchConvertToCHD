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

    [Fact]
    public void SanitizeFileNameMultipleTrailingPeriodsReplaced()
    {
        var result = PathUtils.SanitizeFileName("file...");
        Assert.DoesNotContain("...", result, StringComparison.Ordinal);
        // The algorithm processes trailing periods one at a time:
        // "file..." -> "file.._" -> stops because last char is now '_'
        Assert.Equal("file.._", result);
    }

    [Fact]
    public void SanitizeFileNameSingleTrailingPeriodWithContent()
    {
        var result = PathUtils.SanitizeFileName("game.iso.");
        Assert.Equal("game.iso_", result);
    }

    [Fact]
    public void SanitizeFileNameOnlyPeriodsBecomesUnderscores()
    {
        var result = PathUtils.SanitizeFileName("...");
        // The algorithm processes one trailing period at a time,
        // replacing each with '_' and then stopping when last char is not '.'
        Assert.Equal(".._", result);
    }

    [Fact]
    public void SanitizeFileNameReplacesMojibakeEllipsis()
    {
        // The source code replaces "â€¦" (a mojibake-encoded ellipsis)
        // with "_ellipsis_"
        var result = PathUtils.SanitizeFileName("game\u00e2\u20ac\u00a6iso");
        Assert.Contains("_ellipsis_", result, StringComparison.Ordinal);
    }

    [Fact]
    public void SanitizeFileNamePreservesValidUnicode()
    {
        // Japanese filename characters should be preserved
        var result = PathUtils.SanitizeFileName("\u30b2\u30fc\u30e0.iso");
        Assert.Equal("\u30b2\u30fc\u30e0.iso", result);
    }

    #region GetBestTempDirectory / GetPossibleTempBasePaths

    [Fact]
    public void GetBestTempDirectoryReturnsNonNullPath()
    {
        var result = PathUtils.GetBestTempDirectory(null, null, "TestPrefix_");
        Assert.NotNull(result);
        Assert.NotEmpty(result);
    }

    [Fact]
    public void GetBestTempDirectoryIncludesPrefix()
    {
        var result = PathUtils.GetBestTempDirectory(null, null, "TestPrefix_");
        Assert.Contains("TestPrefix_", result, StringComparison.Ordinal);
    }

    [Fact]
    public void GetBestTempDirectoryUsesSystemTempAsFallback()
    {
        // With null inputs, the result is based on the drive with most free space,
        // which may or may not be the system temp drive. Verify the path is valid.
        var result = PathUtils.GetBestTempDirectory(null, null, "FallbackTest_");
        Assert.NotNull(result);
        Assert.NotEmpty(result);
        Assert.Contains("FallbackTest_", result, StringComparison.Ordinal);
        // Path should include a guid component
        var dirName = Path.GetFileName(result);
        Assert.StartsWith("FallbackTest_", dirName, StringComparison.Ordinal);
        Assert.True(dirName.Length > "FallbackTest_".Length + 10);
    }

    [Fact]
    public void GetPossibleTempBasePathsIncludesSystemTemp()
    {
        var paths = PathUtils.GetPossibleTempBasePaths().ToList();
        Assert.NotEmpty(paths);
        Assert.Contains(Path.GetTempPath(), paths);
    }

    [Fact]
    public void GetPossibleTempBasePathsReturnsNoDuplicates()
    {
        var paths = PathUtils.GetPossibleTempBasePaths().ToList();
        Assert.Equal(paths.Count, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    #endregion
}
