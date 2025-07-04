﻿using System.Text.Json.Serialization;

namespace BatchConvertToCHD;

/// <summary>
/// Represents the structure of a GitHub release JSON response.
/// </summary>
public sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = string.Empty; // Explicitly maps to "tag_name"

    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; set; } = string.Empty; // Explicitly maps to "html_url"
}