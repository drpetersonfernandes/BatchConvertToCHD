namespace BatchConvertToCHD.Utilities;

/// <summary>
/// Centralized constants for file extensions used throughout the application.
/// </summary>
public static class FileExtensions
{
    /// <summary>
    /// String comparer for ordinal case-insensitive extension comparisons.
    /// </summary>
    private static readonly StringComparer ExtensionComparer = StringComparer.OrdinalIgnoreCase;

    // Disc image formats
    public const string Cue = ".cue";
    public const string Iso = ".iso";
    public const string Img = ".img";
    public const string Cdi = ".cdi";
    public const string Gdi = ".gdi";
    public const string Toc = ".toc";
    public const string Raw = ".raw";
    public const string Ccd = ".ccd";
    public const string Sub = ".sub";

    // Archive formats
    public const string Zip = ".zip";
    public const string SevenZip = ".7z";
    public const string Rar = ".rar";

    // Compressed disc image formats
    public const string Cso = ".cso";
    public const string Pbp = ".pbp";

    // Output format
    public const string Chd = ".chd";

    /// <summary>
    /// All supported input extensions for conversion.
    /// </summary>
    public static readonly string[] AllSupportedInputExtensionsForConversion =
    [
        Cue, Iso, Img, Cdi, Gdi, Toc, Raw, Zip, SevenZip, Rar, Cso, Pbp, Ccd
    ];

    /// <summary>
    /// HashSet of all supported input extensions for efficient case-insensitive lookups.
    /// </summary>
    public static readonly HashSet<string> AllSupportedInputExtensionsForConversionSet =
        new(AllSupportedInputExtensionsForConversion, ExtensionComparer);

    /// <summary>
    /// Archive file extensions.
    /// </summary>
    public static readonly string[] ArchiveExtensions =
    [
        Zip, SevenZip, Rar
    ];

    /// <summary>
    /// HashSet of archive extensions for efficient case-insensitive lookups.
    /// </summary>
    public static readonly HashSet<string> ArchiveExtensionsSet =
        new(ArchiveExtensions, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Primary target extensions for extraction from archives.
    /// </summary>
    public static readonly string[] PrimaryTargetExtensions =
    [
        Cue, Iso, Img, Cdi, Gdi, Toc, Raw
    ];

    /// <summary>
    /// HashSet of primary target extensions for efficient case-insensitive lookups.
    /// </summary>
    public static readonly HashSet<string> PrimaryTargetExtensionsSet =
        new(PrimaryTargetExtensions, ExtensionComparer);
}
