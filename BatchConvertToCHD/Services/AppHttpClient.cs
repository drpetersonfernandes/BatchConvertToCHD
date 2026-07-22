using System.Net.Http;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Serilog;

namespace BatchConvertToCHD.Services;

public static class AppHttpClient
{
    private static SocketsHttpHandler? _handler;
    private static HttpClient? _client;
    private static readonly object Lock = new();
    private static readonly ILogger Logger = Log.ForContext(typeof(AppHttpClient));

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
                                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                                RemoteCertificateValidationCallback = ServerCertificateValidationCallback
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

    private static bool ServerCertificateValidationCallback(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors sslPolicyErrors)
    {
        if (sslPolicyErrors == SslPolicyErrors.None)
            return true;

        if (sslPolicyErrors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
        {
            var subject = (certificate as X509Certificate2)?.Subject ?? certificate?.Subject ?? "unknown";
            Logger.Warning("SSL certificate name mismatch for {Subject}. The server certificate does not match the expected hostname. This may be caused by a proxy or firewall intercepting the connection. Allowing the connection to proceed.", subject);
        }
        else
        {
            Logger.Warning("SSL certificate validation error: {Errors}. Allowing the connection to proceed.", sslPolicyErrors);
        }

        return true;
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
