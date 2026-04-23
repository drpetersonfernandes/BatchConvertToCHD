using BatchConvertToCHD.Utilities;

namespace BatchConvertToCHD.Tests;

public class FileExtensionsTests
{
    [Theory]
    [InlineData(".zip", true)]
    [InlineData(".7z", true)]
    [InlineData(".rar", true)]
    [InlineData(".iso", false)]
    [InlineData(".cue", false)]
    public void ArchiveExtensionsSetContainsExpectedValues(string ext, bool expected)
    {
        Assert.Equal(expected, FileExtensions.ArchiveExtensionsSet.Contains(ext));
    }

    [Theory]
    [InlineData(".cue", true)]
    [InlineData(".iso", true)]
    [InlineData(".img", true)]
    [InlineData(".gdi", true)]
    [InlineData(".chd", false)]
    [InlineData(".zip", false)]
    public void PrimaryTargetExtensionsSetContainsExpectedValues(string ext, bool expected)
    {
        Assert.Equal(expected, FileExtensions.PrimaryTargetExtensionsSet.Contains(ext));
    }

    [Fact]
    public void AllSupportedInputExtensionsForConversionSetIsCaseInsensitive()
    {
        Assert.Contains(".ISO", FileExtensions.AllSupportedInputExtensionsForConversionSet);
        Assert.Contains(".7Z", FileExtensions.AllSupportedInputExtensionsForConversionSet);
        Assert.Contains(".ZIP", FileExtensions.AllSupportedInputExtensionsForConversionSet);
    }

    [Fact]
    public void ChdExtensionIsCorrect()
    {
        Assert.Equal(".chd", FileExtensions.Chd);
    }
}
