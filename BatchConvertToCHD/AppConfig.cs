using System.Runtime.InteropServices;
using System.Text;

namespace BatchConvertToCHD;

/// <summary>
/// Provides centralized application-wide configuration constants.
/// </summary>
internal static class AppConfig
{
    private static readonly byte[] KeySalt = "BatchConvertToCHD_v1"u8.ToArray();
    private static string? _decryptedApiKey;

    /// <summary>
    /// Gets a value indicating whether the current process architecture is ARM64.
    /// </summary>
    public static bool IsArm64 => RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

    /// <summary>
    /// Gets the appropriate chdman executable name based on the current architecture.
    /// Returns "chdman_arm64.exe" for ARM64 or "chdman.exe" for other architectures.
    /// </summary>
    public static string ChdmanExeName => IsArm64 ? "chdman_arm64.exe" : "chdman.exe";

    /// <summary>
    /// Gets the appropriate 7-Zip executable name based on the current architecture.
    /// Returns "7za_arm64.exe" for ARM64 or "7za.exe" for other architectures.
    /// </summary>
    public static string SevenZipExeName => IsArm64 ? "7za_arm64.exe" : "7za.exe";

    /// <summary>
    /// The filename of the PSXPackager executable used for PlayStation package handling.
    /// </summary>
    public const string PsxPackagerExeName = "psxpackager.exe";

    /// <summary>
    /// The API endpoint URL for submitting bug reports.
    /// </summary>
    public const string BugReportApiUrl = "https://www.purelogiccode.com/bugreport/api/send-bug-report";

    /// <summary>
    /// The encrypted API key shared by both endpoints.
    /// </summary>
    private const string EncryptedApiKey = "KgscVBE2WRpDUwYNJlp3eCtmAwl1V0NVX3UdW0BSRkFnW3d9d21FBHBXQABfdg1YAlIVEzMHJC9zaQJDJFRCVw0=";

    /// <summary>
    /// Gets the API key used to authenticate bug report submissions.
    /// </summary>
    public static string BugReportApiKey => GetApiKey();

    /// <summary>
    /// The API endpoint URL for recording application usage statistics.
    /// </summary>
    public const string ApplicationStatsApiUrl = "https://www.purelogiccode.com/ApplicationStats/stats";

    /// <summary>
    /// Gets the API key used to authenticate application stats submissions.
    /// </summary>
    public static string ApplicationStatsApiKey => GetApiKey();

    /// <summary>
    /// The GitHub API URL for fetching the latest release information.
    /// </summary>
    public const string GitHubApiLatestReleaseUrl = "https://api.github.com/repos/drpetersonfernandes/BatchConvertToCHD/releases/latest";

    /// <summary>
    /// The canonical name of this application, used for API calls, window titles, and mutex naming.
    /// </summary>
    public const string ApplicationName = "BatchConvertToCHD";

    /// <summary>
    /// The environment identifier sent with bug reports ("Production" or "Development").
    /// </summary>
#if DEBUG
    public const string BugReportEnvironment = "Development";
#else
    public const string BugReportEnvironment = "Production";
#endif

    /// <summary>
    /// The interval in milliseconds between write speed performance counter updates.
    /// </summary>
    public const int WriteSpeedUpdateIntervalMs = 1000;

    /// <summary>
    /// The maximum allowed conversion timeout in hours to prevent unreasonably long timeouts.
    /// </summary>
    public const int MaxConversionTimeoutHours = 4;

    private static string GetApiKey()
    {
        if (_decryptedApiKey == null)
        {
            var data = Convert.FromBase64String(EncryptedApiKey);
            for (var i = 0; i < data.Length; i++)
            {
                data[i] ^= KeySalt[i % KeySalt.Length];
            }

            _decryptedApiKey = Encoding.UTF8.GetString(data);
        }
        return _decryptedApiKey;
    }
}