using System.Net.Http;
using System.Security.Authentication;
using System.Net.Security;

namespace BatchConvertToCHD.Services;

public static class AppHttpClient
{
    private static SocketsHttpHandler? _handler;
    private static HttpClient? _client;
    private static readonly object Lock = new();

    public static HttpClient Client
    {
        get
        {
            if (_client == null)
            {
                lock (Lock)
                {
                    if (_client == null)
                    {
                        _handler = new SocketsHttpHandler
                        {
                            SslOptions = new SslClientAuthenticationOptions
                            {
                                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                            },
                            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
                        };
                        _client = new HttpClient(_handler);
                        _client.DefaultRequestHeaders.Add("Accept", "application/json");
                    }
                }
            }

            lock (Lock)
            {
                return _client;
            }
        }
    }

    public static void Dispose()
    {
        lock (Lock)
        {
            _client?.Dispose();
            _client = null;
            _handler?.Dispose();
            _handler = null;
        }
    }
}
