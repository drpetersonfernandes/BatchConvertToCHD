using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using BatchConvertToCHD.Models;

namespace BatchConvertToCHD.Services;

public class UpdateService(string applicationName)
{
    private readonly string _applicationName = applicationName;
    private const string GitHubApiLatestReleaseUrl = "https://api.github.com/repos/drpetersonfernandes/BatchConvertToCHD/releases/latest";
    private static readonly JsonSerializerOptions JsonSerializerOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task CheckForNewVersionAsync(Action<string> onLog, Action<string> onStatusUpdate, Func<string, Exception?, Task> onBugReport)
    {
        try
        {
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("User-Agent", _applicationName);

            onLog("Checking for updates on GitHub...");
            var response = await httpClient.GetStringAsync(GitHubApiLatestReleaseUrl);

            var latestRelease = JsonSerializer.Deserialize<GitHubRelease>(response, JsonSerializerOptions);
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

            onLog($"Current version: {currentVersion}");
            onLog($"Latest version: {remoteVersion}");

            if (remoteVersion > currentVersion)
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
                        Process.Start(new ProcessStartInfo(latestRelease.HtmlUrl) { UseShellExecute = true });
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
