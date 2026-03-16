using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;

namespace BatchConvertToCHD.Services;

/// <summary>
/// Service responsible for recording application usage statistics.
/// </summary>
public class StatsService : IDisposable
{
    private readonly HttpClient _httpClient = new();
    private readonly string _apiUrl;
    private readonly string _apiKey;
    private readonly string _applicationId;

    public StatsService(string apiUrl, string apiKey, string applicationId)
    {
        _apiUrl = apiUrl;
        _apiKey = apiKey;
        _applicationId = applicationId;

        // Authorization header as specified in the API documentation
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
    }

    /// <summary>
    /// Records application usage statistics by sending a POST request to the Stats API.
    /// </summary>
    public async Task RecordUsageAsync()
    {
        try
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";

            var payload = new
            {
                applicationId = _applicationId, version
            };

            await _httpClient.PostAsJsonAsync(_apiUrl, payload);
        }
        catch
        {
            // Silently fail to not interrupt application startup
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }
}