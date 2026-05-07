using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;

namespace BatchConvertToCHD.Services;

/// <summary>
/// Service responsible for recording application usage statistics.
/// </summary>
public class StatsService
{
    private readonly string _apiUrl;
    private readonly string _apiKey;
    private readonly string _applicationId;

    /// <summary>
    /// Initializes a new instance of the <see cref="StatsService"/> class.
    /// </summary>
    /// <param name="apiUrl">The URL of the stats API endpoint.</param>
    /// <param name="apiKey">The API key for authentication.</param>
    /// <param name="applicationId">The unique identifier for the application.</param>
    public StatsService(string apiUrl, string apiKey, string applicationId)
    {
        _apiUrl = apiUrl;
        _apiKey = apiKey;
        _applicationId = applicationId;
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

            // Send request with Authorization header
            using var request = new HttpRequestMessage(HttpMethod.Post, _apiUrl);
            request.Headers.Add("Authorization", $"Bearer {_apiKey}");
            request.Content = JsonContent.Create(payload);

            await AppHttpClient.Client.SendAsync(request);
        }
        catch
        {
            // Silently fail to not interrupt application startup
        }
    }
}