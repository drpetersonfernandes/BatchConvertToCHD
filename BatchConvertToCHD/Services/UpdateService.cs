using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using BatchConvertToCHD.Models;

namespace BatchConvertToCHD.Services;

/// <summary>
/// Service for checking and notifying about application updates from GitHub releases.
/// </summary>
public class UpdateService(string applicationName)
{
    private readonly string _applicationName = applicationName;
    private readonly HttpClient _httpClient = AppHttpClient.Client;
    private static readonly JsonSerializerOptions JsonSerializerOptions = new() { PropertyNameCaseInsensitive = true };

    internal UpdateService(string applicationName, HttpClient httpClient)
        : this(applicationName)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// Checks GitHub for a newer version of the application and prompts the user to download if available.
    /// </summary>
    /// <param name="onLog">Callback for logging messages.</param>
    /// <param name="onStatusUpdate">Callback for status bar updates.</param>
    /// <param name="onBugReport">Callback for reporting errors.</param>
    public Task CheckForNewVersionAsync(Action<string> onLog, Action<string> onStatusUpdate, Func<string, Exception?, Task> onBugReport)
    {
        return CheckForNewVersionAsync(_httpClient, Assembly.GetExecutingAssembly().GetName().Version, onLog, onStatusUpdate, onBugReport);
    }

    internal async Task CheckForNewVersionAsync(HttpClient httpClient, Version? currentVersion, Action<string> onLog, Action<string> onStatusUpdate, Func<string, Exception?, Task> onBugReport)
    {
        try
        {
            onLog("Checking for updates on GitHub...");

            using var request = new HttpRequestMessage(HttpMethod.Get, AppConfig.GitHubApiLatestReleaseUrl);
            request.Headers.UserAgent.ParseAdd(_applicationName);

            var response = await httpClient.SendAsync(request);

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

            if (!response.IsSuccessStatusCode)
            {
                var statusCode = (int)response.StatusCode;
                if (statusCode is >= 500 and < 600)
                {
                    onLog($"Update check skipped: GitHub server error ({statusCode}).");
                    onStatusUpdate("Update check skipped (server error)");
                    return;
                }

                response.EnsureSuccessStatusCode();
            }

            var responseBody = await response.Content.ReadAsStringAsync();
            var latestRelease = JsonSerializer.Deserialize<GitHubRelease>(responseBody, JsonSerializerOptions);
            if (latestRelease == null || latestRelease.Draft || latestRelease.Prerelease || string.IsNullOrWhiteSpace(latestRelease.TagName))
            {
                onLog("Latest release is invalid, draft, or prerelease. Skipping.");
                return;
            }

            var remoteVersionString = ParseVersionFromTag(latestRelease.TagName);

            if (!TryNormalizeVersions(currentVersion, remoteVersionString, out var normalizedCurrent, out var normalizedRemote))
            {
                onLog($"Could not compare versions. Current: {currentVersion}, Remote: {remoteVersionString}");
                return;
            }

            onLog($"Current version: {normalizedCurrent}");
            onLog($"Latest version: {normalizedRemote}");

            if (normalizedRemote > normalizedCurrent)
            {
                if (Application.Current != null)
                {
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        var releaseNotes = string.IsNullOrWhiteSpace(latestRelease.Body)
                            ? "No release notes available."
                            : latestRelease.Body.Replace(@"\r\n", "\n").Replace("\\n", "\n");

                        var result = MessageBox.Show(
                            $"A new version ({remoteVersionString}) of {_applicationName} is available!\n\nRelease Notes:\n{releaseNotes}\n\nWould you like to download it?",
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
                                _ = onBugReport("Failed to open browser", urlEx);

                                try
                                {
                                    Clipboard.SetText(latestRelease.HtmlUrl);
                                }
                                catch (Exception clipboardEx)
                                {
                                    onLog($"Failed to copy URL to clipboard: {clipboardEx.Message}");
                                    _ = onBugReport("Failed to copy URL to clipboard", clipboardEx);
                                }

                                MessageBox.Show(
                                    $"Unable to open browser automatically. The update URL has been copied to your clipboard.\n\nURL: {latestRelease.HtmlUrl}\n\nPlease paste it into your browser manually.",
                                    "Browser Launch Failed",
                                    MessageBoxButton.OK,
                                    MessageBoxImage.Information);
                            }
                        }
                    });
                }

                onStatusUpdate($"Update available: v{remoteVersionString}");
            }
            else
            {
                onLog("Application is up to date.");
                onStatusUpdate("Application is up to date");
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode == null)
        {
            onLog($"Update check failed (Network/SSL): {ex.Message}");
            onStatusUpdate("Update check failed (network)");
        }
        catch (HttpRequestException ex)
        {
            onLog($"Update check failed: {ex.Message}");
            onStatusUpdate("Update check failed");
            await onBugReport("Update check failed", ex);
        }
        catch (Exception ex)
        {
            onLog($"Update check failed: {ex.Message}");
            onStatusUpdate("Update check failed");
            await onBugReport("Error checking for updates", ex);
        }
    }

    /// <summary>
    /// Normalizes versions to ensure consistent comparison (handles 2-part vs 4-part versions).
    /// If Build or Revision is -1 (undefined), defaults to 0 to avoid ArgumentOutOfRangeException.
    /// Returns false if either version cannot be parsed.
    /// </summary>
    internal static bool TryNormalizeVersions(Version? current, string remoteTag, out Version? normalizedCurrent, out Version? normalizedRemote)
    {
        normalizedCurrent = null;
        normalizedRemote = null;

        if (current == null || !Version.TryParse(remoteTag, out var remoteVersion))
        {
            return false;
        }

        normalizedCurrent = new Version(
            current.Major,
            current.Minor,
            current.Build < 0 ? 0 : current.Build,
            current.Revision < 0 ? 0 : current.Revision);
        normalizedRemote = new Version(
            remoteVersion.Major,
            remoteVersion.Minor,
            remoteVersion.Build < 0 ? 0 : remoteVersion.Build,
            remoteVersion.Revision < 0 ? 0 : remoteVersion.Revision);

        return true;
    }

    internal static string ParseVersionFromTag(string tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName))
        {
            return string.Empty;
        }

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
