using System.Runtime.InteropServices;

namespace BatchConvertToCHD.Tests;

public class AppConfigTests
{
    [Fact]
    public void IsArm64ReturnsBool()
    {
        var result = AppConfig.IsArm64;
        var expected = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ChdmanExeNameMatchesArchitecture()
    {
        if (AppConfig.IsArm64)
        {
            Assert.Equal("chdman_arm64.exe", AppConfig.ChdmanExeName);
        }
        else
        {
            Assert.Equal("chdman.exe", AppConfig.ChdmanExeName);
        }
    }

    [Fact]
    public void BugReportApiUrlIsNotEmpty()
    {
        Assert.False(string.IsNullOrEmpty(AppConfig.BugReportApiUrl));
    }

    [Fact]
    public void BugReportApiKeyIsNotEmpty()
    {
        Assert.False(string.IsNullOrEmpty(AppConfig.BugReportApiKey));
    }

    [Fact]
    public void ApplicationStatsApiUrlIsNotEmpty()
    {
        Assert.False(string.IsNullOrEmpty(AppConfig.ApplicationStatsApiUrl));
    }

    [Fact]
    public void ApplicationStatsApiKeyIsNotEmpty()
    {
        Assert.False(string.IsNullOrEmpty(AppConfig.ApplicationStatsApiKey));
    }

    [Fact]
    public void ApplicationNameIsCorrect()
    {
        Assert.Equal("BatchConvertToCHD", AppConfig.ApplicationName);
    }

    [Fact]
    public void WriteSpeedUpdateIntervalMsIsPositive()
    {
        Assert.True(AppConfig.WriteSpeedUpdateIntervalMs > 0);
    }

    [Fact]
    public void MaxConversionTimeoutHoursIsPositive()
    {
        Assert.True(AppConfig.MaxConversionTimeoutHours > 0);
    }

    [Fact]
    public void GitHubApiLatestReleaseUrlsAreConfiguredInPreferenceOrder()
    {
        var sources = AppConfig.GitHubApiLatestReleaseUrls;

        Assert.Equal(2, sources.Count);
        Assert.Equal(AppConfig.PrimaryGitHubApiLatestReleaseUrl, sources[0]);
        Assert.Equal(AppConfig.FallbackGitHubApiLatestReleaseUrl, sources[1]);

        // The primary source must be the new purelogiccode organization; the fallback is the
        // previous owner's repository.
        Assert.Contains("/purelogiccode/BatchConvertToCHD/", AppConfig.PrimaryGitHubApiLatestReleaseUrl, StringComparison.Ordinal);
        Assert.EndsWith("/releases/latest", AppConfig.PrimaryGitHubApiLatestReleaseUrl, StringComparison.Ordinal);
        Assert.Contains("/drpetersonfernandes/BatchConvertToCHD/", AppConfig.FallbackGitHubApiLatestReleaseUrl, StringComparison.Ordinal);
        Assert.EndsWith("/releases/latest", AppConfig.FallbackGitHubApiLatestReleaseUrl, StringComparison.Ordinal);

        foreach (var url in sources)
        {
            Assert.StartsWith("https://api.github.com/repos/", url, StringComparison.Ordinal);
        }
    }
}
