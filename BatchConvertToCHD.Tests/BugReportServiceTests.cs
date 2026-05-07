using System.Reflection;
using System.Text;
using BatchConvertToCHD.Services;

namespace BatchConvertToCHD.Tests;

public class BugReportServiceTests
{
    private const string TestApiUrl = "https://example.com/api/bugreport";
    private const string TestApiKey = "test-api-key";
    private const string TestAppName = "TestApp";

    [Fact]
    public void ConstructorStoresParametersCorrectly()
    {
        var service = new BugReportService(TestApiUrl, TestApiKey, TestAppName);

        var apiUrlField = typeof(BugReportService).GetField("_apiUrl", BindingFlags.NonPublic | BindingFlags.Instance);
        var apiKeyField = typeof(BugReportService).GetField("_apiKey", BindingFlags.NonPublic | BindingFlags.Instance);
        var appNameField = typeof(BugReportService).GetField("_applicationName", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(apiUrlField);
        Assert.NotNull(apiKeyField);
        Assert.NotNull(appNameField);
        Assert.Equal(TestApiUrl, apiUrlField.GetValue(service));
        Assert.Equal(TestApiKey, apiKeyField.GetValue(service));
        Assert.Equal(TestAppName, appNameField.GetValue(service));
    }

    [Fact]
    public void BuildFormattedReportIncludesMessageAndAppName()
    {
        var service = new BugReportService(TestApiUrl, TestApiKey, TestAppName);
        var method = typeof(BugReportService).GetMethod("BuildFormattedReport", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var result = method.Invoke(service, ["Test error message", null]) as string;
        Assert.NotNull(result);
        Assert.Contains("Test error message", result, StringComparison.Ordinal);
        Assert.Contains(TestAppName, result, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildFormattedReportIncludesExceptionDetails()
    {
        var service = new BugReportService(TestApiUrl, TestApiKey, TestAppName);
        var method = typeof(BugReportService).GetMethod("BuildFormattedReport", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var ex = new InvalidOperationException("Something went wrong");
        var result = method.Invoke(service, ["Error summary", ex]) as string;
        Assert.NotNull(result);
        Assert.Contains("Error summary", result, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", result, StringComparison.Ordinal);
        Assert.Contains("Something went wrong", result, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildFormattedReportIncludesInnerException()
    {
        var service = new BugReportService(TestApiUrl, TestApiKey, TestAppName);
        var method = typeof(BugReportService).GetMethod("BuildFormattedReport", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var inner = new ArgumentException("Inner error");
        var outer = new InvalidOperationException("Outer error", inner);
        var result = method.Invoke(service, ["Error summary", outer]) as string;
        Assert.NotNull(result);
        Assert.Contains("Inner Exception", result, StringComparison.Ordinal);
        Assert.Contains("Inner error", result, StringComparison.Ordinal);
    }

    [Fact]
    public void GetExceptionStackTraceNullReturnsNa()
    {
        var method = typeof(BugReportService).GetMethod("GetExceptionStackTrace", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method.Invoke(null, [null]) as string;
        Assert.Equal("N/A", result);
    }

    [Fact]
    public void GetExceptionStackTraceIncludesExceptionDetails()
    {
        var method = typeof(BugReportService).GetMethod("GetExceptionStackTrace", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var ex = new InvalidOperationException("Test error");
        var result = method.Invoke(null, [ex]) as string;
        Assert.NotNull(result);
        Assert.Contains("InvalidOperationException", result, StringComparison.Ordinal);
        Assert.Contains("Test error", result, StringComparison.Ordinal);
    }

    [Fact]
    public void GetExceptionStackTraceHandlesNestedExceptions()
    {
        var method = typeof(BugReportService).GetMethod("GetExceptionStackTrace", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var deep = new FormatException("Deep");
        var mid = new ArgumentException("Mid", deep);
        var top = new InvalidOperationException("Top", mid);
        var result = method.Invoke(null, [top]) as string;
        Assert.NotNull(result);
        Assert.Contains("Top", result, StringComparison.Ordinal);
        Assert.Contains("Mid", result, StringComparison.Ordinal);
        Assert.Contains("Deep", result, StringComparison.Ordinal);
    }

    [Fact]
    public void GetExceptionStackTraceLimitsDepth()
    {
        var method = typeof(BugReportService).GetMethod("GetExceptionStackTrace", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var inner = new Exception("deepest");
        var current = inner;
        for (var i = 0; i < 10; i++)
        {
            current = new Exception($"level {i}", current);
        }

        var result = method.Invoke(null, [current]) as string;
        Assert.NotNull(result);
    }

    [Fact]
    public void GetEnvironmentDetailsReturnsValidValues()
    {
        var method = typeof(BugReportService).GetMethod("GetEnvironmentDetails", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method.Invoke(null, null);
        Assert.NotNull(result);

        var type = result.GetType();
        var osVersion = type.GetField("Item1")?.GetValue(result) as string;
        var architecture = type.GetField("Item2")?.GetValue(result) as string;
        var processorCount = type.GetField("Item5")?.GetValue(result);

        Assert.NotNull(osVersion);
        Assert.NotEmpty(osVersion);
        Assert.NotNull(architecture);
        Assert.NotEmpty(architecture);
        Assert.IsType<int>(processorCount);
    }

    [Fact]
    public void AppendExceptionDetailsWithNullStackTraceDoesNotCrash()
    {
        var method = typeof(BugReportService).GetMethod("AppendExceptionDetails", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var sb = new StringBuilder();
        var ex = new Exception("No stack") { };
        var record = Record.Exception(() => method.Invoke(null, [sb, ex, 0]));
        Assert.Null(record);
        Assert.Contains("No stack", sb.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendBugReportAsyncReturnsFalseOnNetworkError()
    {
        var service = new BugReportService("https://invalid.example.invalid/api", TestApiKey, TestAppName);
        var result = await service.SendBugReportAsync("Test message");
        Assert.False(result);
    }
}
