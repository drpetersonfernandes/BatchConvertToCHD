using System.Text.Json.Serialization;

namespace BatchConvertToCHD.Models;

/// <summary>
/// Represents the structure of a GitHub release JSON response.
/// </summary>
public sealed class GitHubRelease
{
    /// <summary>
    /// Gets or sets the tag name of the release (e.g., "v2.11.0").
    /// Maps to the JSON property "tag_name".
    /// </summary>
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the HTML URL of the release page on GitHub.
    /// Maps to the JSON property "html_url".
    /// </summary>
    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the display name of the release.
    /// Maps to the JSON property "name".
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the body/description text of the release (release notes).
    /// Maps to the JSON property "body".
    /// </summary>
    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether this release is a pre-release.
    /// Maps to the JSON property "prerelease".
    /// </summary>
    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this release is a draft.
    /// Maps to the JSON property "draft".
    /// </summary>
    [JsonPropertyName("draft")]
    public bool Draft { get; set; }
}