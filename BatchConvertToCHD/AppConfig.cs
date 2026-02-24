using System.Runtime.InteropServices;

namespace BatchConvertToCHD;

/// <summary>
/// Provides centralized application-wide configuration constants.
/// </summary>
internal static class AppConfig
{
    public static bool IsArm64 => RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
    public static string ChdmanExeName => IsArm64 ? "chdman_arm64.exe" : "chdman.exe";
    public const string BugReportApiUrl = "https://www.purelogiccode.com/bugreport/api/send-bug-report";
    public const string BugReportApiKey = "hjh7yu6t56tyr540o9u8767676r5674534453235264c75b6t7ggghgg76trf564e";
    public const string ApplicationName = "BatchConvertToCHD";
    public const int WriteSpeedUpdateIntervalMs = 1000;

    // Timeout configurations
    public const int MaxConversionTimeoutHours = 4;
    public const int ProcessKillTimeoutSeconds = 5;
    public const int FileAccessTimeoutSeconds = 5;
    public const int ValidationTimeoutSeconds = 10;
}