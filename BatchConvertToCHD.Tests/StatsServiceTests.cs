using System.Reflection;
using BatchConvertToCHD.Services;

namespace BatchConvertToCHD.Tests;

public class StatsServiceTests
{
    private const string TestApiUrl = "https://example.com/api/stats";
    private const string TestApiKey = "test-api-key";
    private const string TestAppId = "test-app-id";

    [Fact]
    public void ConstructorStoresParametersCorrectly()
    {
        var service = new StatsService(TestApiUrl, TestApiKey, TestAppId);

        var apiUrlField = typeof(StatsService).GetField("_apiUrl", BindingFlags.NonPublic | BindingFlags.Instance);
        var apiKeyField = typeof(StatsService).GetField("_apiKey", BindingFlags.NonPublic | BindingFlags.Instance);
        var appIdField = typeof(StatsService).GetField("_applicationId", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(apiUrlField);
        Assert.NotNull(apiKeyField);
        Assert.NotNull(appIdField);
        Assert.Equal(TestApiUrl, apiUrlField.GetValue(service));
        Assert.Equal(TestApiKey, apiKeyField.GetValue(service));
        Assert.Equal(TestAppId, appIdField.GetValue(service));
    }

    [Fact]
    public async Task RecordUsageAsyncDoesNotThrowOnNetworkError()
    {
        var service = new StatsService("https://invalid.example.invalid/api", TestApiKey, TestAppId);
        var exception = await Record.ExceptionAsync(service.RecordUsageAsync);
        Assert.Null(exception);
    }
}
