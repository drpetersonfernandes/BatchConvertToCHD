using System.Reflection;
using BatchConvertToCHD.Services;

namespace BatchConvertToCHD.Tests;

public class UpdateServiceTests
{
    [Fact]
    public void ConstructorStoresApplicationName()
    {
        var service = new UpdateService("MyApp");
        var field = typeof(UpdateService).GetField("_applicationName", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        Assert.Equal("MyApp", field.GetValue(service));
    }

    [Theory]
    [InlineData("v1.0.0", "1.0.0")]
    [InlineData("v1.2.3-beta", "1.2.3-beta")]
    [InlineData("release1.5.0", "1.5.0")]
    [InlineData("version2.0", "2.0")]
    [InlineData("V3.0.1", "3.0.1")]
    [InlineData("no_version_here", "")]
    [InlineData("", "")]
    [InlineData("   v4.5.6   ", "4.5.6")]
    [InlineData("1.0", "1.0")]
    [InlineData("v2024.1", "2024.1")]
    public void ParseVersionFromTagExtractsCorrectly(string input, string expected)
    {
        var method = typeof(UpdateService).GetMethod("ParseVersionFromTag", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method.Invoke(null, [input]) as string;
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("v")]
    [InlineData("release")]
    [InlineData("version")]
    public void ParseVersionFromTagPrefixOnlyReturnsEmpty(string prefixOnly)
    {
        var method = typeof(UpdateService).GetMethod("ParseVersionFromTag", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method.Invoke(null, [prefixOnly]) as string;
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void ParseVersionFromTagNullReturnsEmpty()
    {
        var method = typeof(UpdateService).GetMethod("ParseVersionFromTag", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method.Invoke(null, [null]) as string;
        Assert.Equal(string.Empty, result);
    }
}
