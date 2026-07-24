using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using Serilog;

namespace BatchConvertToCHD.Services;

public class StatsService
{
    private readonly string _apiUrl;
    private readonly string _apiKey;
    private readonly string _applicationId;
    private readonly HttpClient _httpClient;
    private static readonly ILogger Logger = Log.ForContext<StatsService>();

    public StatsService(string apiUrl, string apiKey, string applicationId)
        : this(apiUrl, apiKey, applicationId, AppHttpClient.Client)
    {
    }

    internal StatsService(string apiUrl, string apiKey, string applicationId, HttpClient httpClient)
    {
        _apiUrl = apiUrl;
        _apiKey = apiKey;
        _applicationId = applicationId;
        _httpClient = httpClient;
    }

    public async Task RecordUsageAsync()
    {
        try
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";

            var payload = new
            {
                applicationId = _applicationId, version
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, _apiUrl);
            request.Headers.Add("Authorization", $"Bearer {_apiKey}");
            request.Content = JsonContent.Create(payload);

            var response = await _httpClient.SendAsync(request);
            if (response.StatusCode == (System.Net.HttpStatusCode)429)
            {
                Logger.Debug("Usage statistics rate-limited (HTTP 429) - this is expected behavior");
            }
            else if (!response.IsSuccessStatusCode)
            {
                Logger.Information("Failed to record usage statistics: HTTP {StatusCode}", (int)response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to record usage statistics (network error)");
        }
    }
}
