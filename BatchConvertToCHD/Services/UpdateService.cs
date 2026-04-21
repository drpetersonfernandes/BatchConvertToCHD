using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using BatchConvertToCHD.Models;
using System.Security.Authentication;
using System.Net.Security;

namespace BatchConvertToCHD.Services;

public class UpdateService(string applicationName)
{
    private readonly string _applicationName = applicationName;
    private const string GitHubApiLatestReleaseUrl = "https://api.github.com/repos/drpetersonfernandes/BatchConvertToCHD/releases/latest";
    private static readonly JsonSerializerOptions JsonSerializerOptions = new() { PropertyNameCaseInsensitive = true };

    // HttpClient should be reused across the application lifetime to avoid socket exhaustion
    // Configure with SocketsHttpHandler to ensure TLS 1.2 and 1.3 are enabled (important for older Windows versions)
    private static readonly HttpClient HttpClient = new(new SocketsHttpHandler
    {
        SslOptions = new SslClientAuthenticationOptions
        {
            // SslProtocols.None allows the OS to decide, but on Win7 TLS 1.2 is often disabled by default.
            // Explicitly including Tls12 and Tls13 ensures they are available if the OS supports them.
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
        }
    });

    public async Task CheckForNewVersionAsync(Action<string> onLog, Action<string> onStatusUpdate, Func<string, Exception?, Task> onBugReport)
    {
        try
        {
            onLog("Checking for updates on GitHub...");

            using var request = new HttpRequestMessage(HttpMethod.Get, GitHubApiLatestReleaseUrl);
            request.Headers.UserAgent.ParseAdd(_applicationName);

            var response = await HttpClient.SendAsync(request);

            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                var responseBody403 = await response.Content.ReadAsStringAsync();
                if (responseBody403.Contains("rate limit exceeded", StringComparison.OrdinalIgnoreCase))
                {
                    onLog("GitHub API rate limit exceeded. Skipping update check.");
                    onStatusUpdate("Update check skipped (rate limit)");
                    return;
                }
            }

            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStringAsync();
            var latestRelease = JsonSerializer.Deserialize<GitHubRelease>(responseBody, JsonSerializerOptions);
            if (latestRelease == null || latestRelease.Draft || latestRelease.Prerelease || string.IsNullOrWhiteSpace(latestRelease.TagName))
            {
                onLog("Latest release is invalid, draft, or prerelease. Skipping.");
                return;
            }

            var currentVersion = Assembly.GetExecutingAssembly().GetName().Version;
            var remoteVersionString = ParseVersionFromTag(latestRelease.TagName);

            if (currentVersion == null || !Version.TryParse(remoteVersionString, out var remoteVersion))
            {
                onLog($"Could not compare versions. Current: {currentVersion}, Remote: {remoteVersionString}");
                return;
            }

            // Normalize versions to ensure consistent comparison (handle 2-part vs 4-part versions)
            // If Build or Revision is -1 (undefined), default to 0 to avoid ArgumentOutOfRangeException
            var normalizedCurrent = new Version(
                currentVersion.Major,
                currentVersion.Minor,
                currentVersion.Build < 0 ? 0 : currentVersion.Build,
                currentVersion.Revision < 0 ? 0 : currentVersion.Revision);
            var normalizedRemote = new Version(
                remoteVersion.Major,
                remoteVersion.Minor,
                remoteVersion.Build < 0 ? 0 : remoteVersion.Build,
                remoteVersion.Revision < 0 ? 0 : remoteVersion.Revision);

            onLog($"Current version: {normalizedCurrent}");
            onLog($"Latest version: {normalizedRemote}");

            if (normalizedRemote > normalizedCurrent)
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    var releaseNotes = string.IsNullOrWhiteSpace(latestRelease.Body)
                        ? "No release notes available."
                        : latestRelease.Body.Replace(@"\r\n", "\n").Replace("\\n", "\n");

                    var result = MessageBox.Show(
                        $"A new version ({remoteVersion}) of {_applicationName} is available!\n\nRelease Notes:\n{releaseNotes}\n\nWould you like to download it?",
                        "New Version Available", MessageBoxButton.YesNo, MessageBoxImage.Information);

                    if (result == MessageBoxResult.Yes)
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo(latestRelease.HtmlUrl) { UseShellExecute = true });
                        }
                        catch (Exception urlEx)
                        {
                            onLog($"Failed to open browser: {urlEx.Message}");

                            // Copy URL to clipboard
                            try
                            {
                                Clipboard.SetText(latestRelease.HtmlUrl);
                            }
                            catch (Exception clipboardEx)
                            {
                                onLog($"Failed to copy URL to clipboard: {clipboardEx.Message}");
                            }

                            // Show URL in message box so user can manually access it
                            MessageBox.Show(
                                $"Unable to open browser automatically. The update URL has been copied to your clipboard.\n\nURL: {latestRelease.HtmlUrl}\n\nPlease paste it into your browser manually.",
                                "Browser Launch Failed",
                                MessageBoxButton.OK,
                                MessageBoxImage.Information);
                        }
                    }
                });
                onStatusUpdate($"Update available: v{remoteVersion}");
            }
            else
            {
                onLog("Application is up to date.");
                onStatusUpdate("Application is up to date");
            }
        }
        catch (HttpRequestException ex)
        {
            onLog($"Update check failed (Network/SSL): {ex.Message}");
            onStatusUpdate("Update check failed (network)");
        }
        catch (Exception ex)
        {
            onLog($"Update check failed: {ex.Message}");
            onStatusUpdate("Update check failed");
            await onBugReport("Error checking for updates", ex);
        }
    }

    private static string ParseVersionFromTag(string tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName)) return string.Empty;

        var tag = tagName.Trim();
        var prefixes = new[] { "release", "version", "v" };
        foreach (var prefix in prefixes)
        {
            if (tag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                tag = tag.Substring(prefix.Length);
                break;
            }
        }

        while (tag.Length > 0 && !char.IsDigit(tag[0]))
        {
            tag = tag.Substring(1);
        }

        return tag;
    }
}
