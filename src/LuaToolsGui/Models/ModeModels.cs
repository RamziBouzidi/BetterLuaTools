using System.Text.Json.Serialization;

namespace LuaToolsGui.Models;

/// <summary>
/// The Steam fixes. Mutually exclusive: only one is active at a time.
///
/// The member names are what land in the settings file, and they are deliberately new: no
/// value written by an older build ("SteamTools", "OpenSteamTools", "OpenSteamToolsNightly",
/// "CloudRedirect") parses into this enum. That is what makes <see cref="Services.ModeMigration"/>
/// inherently one-shot. An unparseable value is by definition pre-migration, so no marker flag is
/// needed. Reusing "OpenSteamTools" here would have silently reinterpreted every old stable-OST
/// user as being on the nightly channel.
/// </summary>
public enum UnlockerMode
{
    Ost,     // OpenSteamTools: files supplied manually by the user
    Custom,  // the user manages their own unlocker; the app places nothing
}

/// <summary>State of a mode's files vs. the latest published build.</summary>
public enum ModeStatus
{
    Unknown,        // offline / GitHub unreachable / Steam not located
    NotInstalled,
    UpToDate,
    UpdateAvailable,
    UserManaged,    // Custom mode: nothing to check. Distinct from Unknown, which means "couldn't tell".
}

/// <summary>
/// Static description of one unlocker backend.
/// </summary>
public sealed record ModeDefinition(
    UnlockerMode Mode,
    string DisplayName,
    string Description);

// ── GitHub release API DTOs ─────────────────────────────────────────
public sealed class GithubRelease
{
    [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
    // published_at is reliable for ordering; the /releases list array itself sorts by created_at
    // (which can be identical across tags created at once), so sort by this to find the latest.
    [JsonPropertyName("published_at")] public DateTimeOffset? PublishedAt { get; set; }
    [JsonPropertyName("assets")] public List<GithubAsset> Assets { get; set; } = [];
}

public sealed class GithubAsset
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("browser_download_url")] public string DownloadUrl { get; set; } = "";
    [JsonPropertyName("digest")] public string? Digest { get; set; } // "sha256:<hex>"

    /// <summary>Asset size in bytes. Lets a tool download report real byte counts instead of a bare
    /// fraction, since GithubProxy.DownloadAsync only reports 0..1.</summary>
    [JsonPropertyName("size")] public long Size { get; set; }
}

/// <summary>Queried state for one mode. What a Mode-page card binds to.</summary>
public sealed record ModeState(
    UnlockerMode Mode,
    ModeStatus Status,
    bool IsActive,           // is this the currently-selected (active) mode
    string? LatestVersion);  // resolved release tag (for display)

/// <summary>State of the CloudRedirect add-on (a feature of OpenSteamTool), derived
/// from disk (cloud_redirect.dll presence + [cloud] enabled in opensteamtool.toml) and the latest
/// CloudRedirect release.</summary>
public sealed record CloudRedirectAddonState(
    bool Installed,          // cloud_redirect.dll is present in the Steam root
    bool Enabled,            // opensteamtool.toml has [cloud] enabled = true
    bool UpdateAvailable,    // on-disk dll differs from the latest release asset
    string? LatestVersion);  // latest CloudRedirect release tag (for display), null if unknown

/// <summary>Outcome of installing/switching to a mode. Mirrors LuaInstaller.InstallResult.</summary>
public sealed record ModeInstallResult(bool Success, string? Error, IReadOnlyList<string> Failed)
{
    public static ModeInstallResult Ok() => new(true, null, []);
    public static ModeInstallResult Fail(string error) => new(false, error, []);
}
