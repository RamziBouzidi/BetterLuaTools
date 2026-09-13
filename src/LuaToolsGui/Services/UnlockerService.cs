using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using LuaToolsGui.Models;

namespace LuaToolsGui.Services;

/// <summary>
/// Manages the mutually-exclusive Steam unlocker selection. OpenSteamTools is supplied manually;
/// this service only detects its local files and registers LuaTools' Lua path.
/// </summary>
public class UnlockerService(SteamService steam, SettingsService settings, GithubProxy gh)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    public IReadOnlyList<ModeDefinition> Modes { get; } =
    [
        new(UnlockerMode.Ost, "OpenSteamTools",
            Description: Resources.Strings.Mode_Desc_Ost),

        new(UnlockerMode.Custom, Resources.Strings.Mode_Name_Custom,
            Description: Resources.Strings.Mode_Desc_Custom),
    ];

    private ModeDefinition Def(UnlockerMode mode) => Modes.First(m => m.Mode == mode);

    /// <summary>The currently-active mode (the last one installed/selected), or null if none yet.</summary>
    public UnlockerMode? SelectedMode =>
        Enum.TryParse(settings.SelectedMode, out UnlockerMode m) ? m : null;

    /// <summary>Short display name of the active mode for status UI; null if none selected/detected yet.</summary>
    public string? SelectedModeDisplayName =>
        SelectedMode is { } m ? Def(m).DisplayName : null;

    /// <summary>
    /// Make sure the active OST install is watching <c>config/stplug-in</c>, so luas written there
    /// hot-reload instead of needing a Steam restart.
    /// </summary>
    /// <remarks>
    /// OST re-reads any directory listed in <c>opensteamtool.toml</c>'s <c>[lua] paths</c>, so this
    /// registration keeps manually installed OpenSteamTool setups compatible with LuaTools.
    ///
    /// Safe to call repeatedly — the underlying edit is targeted, comment-preserving and append-only, and
    /// no-ops when the path is already present. Skipped for <c>Custom</c>, whose unlocker we know nothing
    /// about, and when no mode is selected.
    /// </remarks>
    public void EnsureLuaPathRegistered()
    {
        if (SelectedMode != UnlockerMode.Ost) return;
        if (steam.EffectivePath is not { } root) return;
        try { EnsureOpenSteamToolLuaPath(root); } catch { /* config tweak is best-effort */ }
    }

    // ── State query ─────────────────────────────────────────────────

    /// <summary>Query the locally supplied unlocker files. No network request is made for OST.</summary>
    public async Task<ModeState> GetStateAsync(UnlockerMode mode, bool forceRefresh = false, CancellationToken ct = default)
    {
        bool active = SelectedMode == mode;

        if (mode == UnlockerMode.Custom)
            return new ModeState(mode, ModeStatus.UserManaged, active, null);

        string? root = steam.EffectivePath;
        if (root is null || !steam.IsValid)
            return new ModeState(mode, ModeStatus.Unknown, active, null);

        string[] required = ["OpenSteamTool.dll", "dwmapi.dll", "xinput1_4.dll"];
        bool installed = required.All(file => File.Exists(Path.Combine(root, file)));
        return new ModeState(mode, installed ? ModeStatus.UpToDate : ModeStatus.NotInstalled, active, null);
    }

    // ── First-run auto-detect ────────────────────────────────────────

    /// <summary>Detect the manually supplied OpenSteamTool files using local presence only.</summary>
    public async Task<UnlockerMode?> DetectActiveModeAsync(CancellationToken ct = default)
    {
        string? root = steam.EffectivePath;
        if (root is null || !steam.IsValid) return null;

        string[] required = ["OpenSteamTool.dll", "dwmapi.dll", "xinput1_4.dll"];
        if (!required.All(file => File.Exists(Path.Combine(root, file)))) return null;

        settings.SelectedMode = UnlockerMode.Ost.ToString();
        await Task.CompletedTask;
        return UnlockerMode.Ost;
    }

    // ── OpenSteamTool config ─────────────────────────────────────────

    private const string OstLuaPath = "config/stplug-in";

    /// <summary>
    /// Ensure &lt;Steam&gt;/opensteamtool.toml's [lua] paths array contains "config/stplug-in" so our luas
    /// are loaded. Creates the file/section/array if missing; appends without removing existing paths.
    /// Targeted text edit (preserves comments and other sections). Commented-out lines are ignored.
    /// </summary>
    private static void EnsureOpenSteamToolLuaPath(string steamRoot)
    {
        string tomlPath = Path.Combine(steamRoot, "opensteamtool.toml");

        // No file → create a minimal one.
        if (!File.Exists(tomlPath))
        {
            File.WriteAllText(tomlPath, $"[lua]\npaths = [\"{OstLuaPath}\"]\n");
            return;
        }

        var lines = File.ReadAllLines(tomlPath).ToList();

        // Find the active (uncommented) [lua] section header and the bounds of that section.
        int luaHeader = lines.FindIndex(l => IsActiveTableHeader(l, "lua"));
        if (luaHeader < 0)
        {
            // No active [lua] section → append one.
            if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
            lines.Add("[lua]");
            lines.Add($"paths = [\"{OstLuaPath}\"]");
            File.WriteAllLines(tomlPath, lines);
            return;
        }

        // Section runs until the next active table header (or EOF).
        int sectionEnd = lines.FindIndex(luaHeader + 1, IsActiveAnyTableHeader);
        if (sectionEnd < 0) sectionEnd = lines.Count;

        // Look for an active `paths` key within the section. The array may span multiple lines.
        int pathsStart = -1;
        for (int i = luaHeader + 1; i < sectionEnd; i++)
        {
            string t = lines[i].TrimStart();
            if (t.StartsWith('#')) continue;                       // commented → ignore
            if (Regex.IsMatch(t, @"^paths\s*=")) { pathsStart = i; break; }
        }

        if (pathsStart < 0)
        {
            // [lua] exists but no active paths key → insert one right under the header.
            lines.Insert(luaHeader + 1, $"paths = [\"{OstLuaPath}\"]");
            File.WriteAllLines(tomlPath, lines);
            return;
        }

        // Find where the array closes (']'), scanning from pathsStart (handles multi-line arrays).
        int pathsEnd = pathsStart;
        while (pathsEnd < sectionEnd && !lines[pathsEnd].Contains(']')) pathsEnd++;
        if (pathsEnd >= sectionEnd) pathsEnd = sectionEnd - 1; // malformed/unclosed. Best effort

        string block = string.Join("\n", lines.GetRange(pathsStart, pathsEnd - pathsStart + 1));

        // Already present (compare the path token, slashes normalized)? Nothing to do.
        if (Regex.IsMatch(block, @"[""']\s*" + Regex.Escape(OstLuaPath).Replace("/", @"[/\\]+") + @"\s*[""']",
                RegexOptions.IgnoreCase))
            return;

        // Insert our entry just before the closing ']' on the line that has it.
        int closeLine = pathsEnd;
        string line = lines[closeLine];
        int bracket = line.LastIndexOf(']');

        // Insert our entry just before the ']'. Add a comma after existing content unless the array
        // is empty (text before ']' ends right after the opening '[').
        string before = line[..bracket].TrimEnd();
        bool arrayEmpty = Regex.IsMatch(before, @"\[\s*$");
        string newBefore = arrayEmpty
            ? before + $" \"{OstLuaPath}\""
            : before + $", \"{OstLuaPath}\"";
        lines[closeLine] = newBefore + line[bracket..];

        File.WriteAllLines(tomlPath, lines);
    }

    /// <summary>True if the line is an active (uncommented) [name] table header.</summary>
    private static bool IsActiveTableHeader(string line, string name)
    {
        string t = line.TrimStart();
        return !t.StartsWith('#') && Regex.IsMatch(t, $@"^\[\s*{Regex.Escape(name)}\s*\]");
    }

    /// <summary>True if the line is any active (uncommented) [..] table header.</summary>
    private static bool IsActiveAnyTableHeader(string line)
    {
        string t = line.TrimStart();
        return !t.StartsWith('#') && Regex.IsMatch(t, @"^\[[^\[].*\]");
    }

    // ── CloudRedirect add-on (a feature of the OpenSteamTool Nightly build) ──────────
    // Not a mutually-exclusive mode: it drops cloud_redirect.dll into the Steam root and toggles
    // [cloud] enabled in opensteamtool.toml. Only meaningful when the OST mode is active.

    private const string CloudRedirectDll = "cloud_redirect.dll";
    private (GithubRelease release, DateTime fetchedAt)? _crReleaseCache;

    private async Task<GithubRelease?> FetchCloudRedirectReleaseAsync(bool forceRefresh, CancellationToken ct)
    {
        if (!forceRefresh && _crReleaseCache is { } c && DateTime.UtcNow - c.fetchedAt < CacheTtl)
            return c.release;

        string url = $"https://api.github.com/repos/{AppConfig.CloudRedirectRepo}/releases/latest";
        try
        {
            using var res = await gh.SendAsync(url, ct);
            if (res is null || !res.IsSuccessStatusCode) return null;
            var release = JsonSerializer.Deserialize<GithubRelease>(await res.Content.ReadAsStringAsync(ct), JsonOpts);
            if (release is not null) _crReleaseCache = (release, DateTime.UtcNow);
            return release;
        }
        catch { return null; }
    }

    /// <summary>Add-on state from disk (dll present + [cloud] enabled) plus, when <paramref name="checkUpdate"/>
    /// and installed, whether a newer cloud_redirect.dll is published.</summary>
    public async Task<CloudRedirectAddonState> GetCloudRedirectStateAsync(
        bool checkUpdate, bool forceRefresh = false, CancellationToken ct = default)
    {
        string? root = steam.EffectivePath;
        if (root is null || !steam.IsValid)
            return new CloudRedirectAddonState(false, false, false, null);

        string dll = Path.Combine(root, CloudRedirectDll);
        bool installed = File.Exists(dll);
        bool enabled = ReadOpenSteamToolCloudEnabled(root);

        bool updateAvailable = false;
        string? latest = null;
        if (checkUpdate && installed)
        {
            var release = await FetchCloudRedirectReleaseAsync(forceRefresh, ct);
            if (release is not null)
            {
                latest = release.TagName;
                string? wanted = AssetDigest(release, CloudRedirectDll);
                if (wanted is not null && !AssetHash.OfFile(dll).Equals(wanted, StringComparison.OrdinalIgnoreCase))
                    updateAvailable = true;
            }
        }
        return new CloudRedirectAddonState(installed, enabled, updateAvailable, latest);
    }

    /// <summary>Enable: download cloud_redirect.dll if missing (verified), then set [cloud] enabled = true.
    /// Takes effect on the next Steam launch.</summary>
    public async Task<ModeInstallResult> EnableCloudRedirectAsync(IProgress<double?>? progress = null, CancellationToken ct = default)
    {
        string? root = steam.EffectivePath;
        if (root is null || !steam.IsValid)
            return ModeInstallResult.Fail(Resources.Strings.Err_SteamNotFound);

        if (!File.Exists(Path.Combine(root, CloudRedirectDll)))
        {
            var dl = await DownloadCloudRedirectDllAsync(root, progress, ct);
            if (!dl.Success) return dl;
        }
        try { SetOpenSteamToolCloudEnabled(root, true); }
        catch (Exception ex) { return ModeInstallResult.Fail(ex.Message); }
        return ModeInstallResult.Ok();
    }

    /// <summary>Disable: flip [cloud] enabled = false (keeps the dll on disk).</summary>
    public ModeInstallResult DisableCloudRedirect()
    {
        string? root = steam.EffectivePath;
        if (root is null || !steam.IsValid)
            return ModeInstallResult.Fail(Resources.Strings.Err_SteamNotFound);
        try { SetOpenSteamToolCloudEnabled(root, false); return ModeInstallResult.Ok(); }
        catch (Exception ex) { return ModeInstallResult.Fail(ex.Message); }
    }

    /// <summary>Update: replace cloud_redirect.dll with the latest (verified). Fails with a "close Steam"
    /// message if Steam holds the existing dll open.</summary>
    public async Task<ModeInstallResult> UpdateCloudRedirectAsync(IProgress<double?>? progress = null, CancellationToken ct = default)
    {
        string? root = steam.EffectivePath;
        if (root is null || !steam.IsValid)
            return ModeInstallResult.Fail(Resources.Strings.Err_SteamNotFound);
        return await DownloadCloudRedirectDllAsync(root, progress, ct);
    }

    private async Task<ModeInstallResult> DownloadCloudRedirectDllAsync(string root, IProgress<double?>? progress, CancellationToken ct)
    {
        var release = await FetchCloudRedirectReleaseAsync(forceRefresh: true, ct);
        if (release is null) return ModeInstallResult.Fail(Resources.Strings.Err_GithubUnreachable);
        var asset = FindAsset(release, CloudRedirectDll);
        if (asset is null) return ModeInstallResult.Fail(string.Format(Resources.Strings.Err_ReleaseMissingFile, CloudRedirectDll));

        string staging = Path.Combine(Path.GetTempPath(), "LuaToolsGui", "cloud", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            string tmp = Path.Combine(staging, CloudRedirectDll);
            await DownloadToFileAsync(asset.DownloadUrl, tmp, progress, ct);
            if (AssetHash.ParseDigest(asset.Digest) is { } want && !AssetHash.OfFile(tmp).Equals(want, StringComparison.OrdinalIgnoreCase))
                return ModeInstallResult.Fail(string.Format(Resources.Strings.Err_VerifyFailedFile, CloudRedirectDll));

            try
            {
                string dest = Path.Combine(root, CloudRedirectDll);
                File.Copy(tmp, dest, overwrite: true);
                StampNow(dest);
            }
            catch
            {
                // Steam has the loaded dll locked: surface a close-Steam message (same as mode install).
                return ModeInstallResult.Fail(string.Format(Resources.Strings.Err_WriteFailedFile, CloudRedirectDll));
            }
            return ModeInstallResult.Ok();
        }
        catch (OperationCanceledException) { return ModeInstallResult.Fail(Resources.Strings.Err_Cancelled); }
        catch (Exception ex) { return ModeInstallResult.Fail(ex.Message); }
        finally { try { Directory.Delete(staging, recursive: true); } catch { /* best effort */ } }
    }

    /// <summary>Ensure opensteamtool.toml has an active [cloud] section with enabled = true|false. Mirrors
    /// EnsureOpenSteamToolLuaPath's targeted, comment-preserving editing.</summary>
    private static void SetOpenSteamToolCloudEnabled(string steamRoot, bool enabled)
    {
        string tomlPath = Path.Combine(steamRoot, "opensteamtool.toml");
        string val = enabled ? "true" : "false";

        if (!File.Exists(tomlPath))
        {
            File.WriteAllText(tomlPath, $"[cloud]\nenabled = {val}\n");
            return;
        }

        var lines = File.ReadAllLines(tomlPath).ToList();

        int header = lines.FindIndex(l => IsActiveTableHeader(l, "cloud"));
        if (header < 0)
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
            lines.Add("[cloud]");
            lines.Add($"enabled = {val}");
            File.WriteAllLines(tomlPath, lines);
            return;
        }

        int sectionEnd = lines.FindIndex(header + 1, IsActiveAnyTableHeader);
        if (sectionEnd < 0) sectionEnd = lines.Count;

        for (int i = header + 1; i < sectionEnd; i++)
        {
            string t = lines[i].TrimStart();
            if (t.StartsWith('#')) continue;                       // commented → ignore
            if (Regex.IsMatch(t, @"^enabled\s*="))
            {
                string indent = lines[i][..(lines[i].Length - lines[i].TrimStart().Length)];
                lines[i] = $"{indent}enabled = {val}";
                File.WriteAllLines(tomlPath, lines);
                return;
            }
        }

        // [cloud] exists but no active enabled key → insert one under the header.
        lines.Insert(header + 1, $"enabled = {val}");
        File.WriteAllLines(tomlPath, lines);
    }

    /// <summary>Read opensteamtool.toml's active [cloud] enabled value (false if the file/section/key is
    /// absent).</summary>
    private static bool ReadOpenSteamToolCloudEnabled(string steamRoot)
    {
        string tomlPath = Path.Combine(steamRoot, "opensteamtool.toml");
        if (!File.Exists(tomlPath)) return false;

        var lines = File.ReadAllLines(tomlPath);
        int header = Array.FindIndex(lines, l => IsActiveTableHeader(l, "cloud"));
        if (header < 0) return false;

        for (int i = header + 1; i < lines.Length; i++)
        {
            if (IsActiveAnyTableHeader(lines[i])) break;            // next section → done
            string t = lines[i].TrimStart();
            if (t.StartsWith('#')) continue;                       // commented → ignore
            var m = Regex.Match(t, @"^enabled\s*=\s*(\w+)");
            if (m.Success) return m.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static string? AssetDigest(GithubRelease release, string assetName) =>
        AssetHash.ParseDigest(release.Assets.FirstOrDefault(
            asset => asset.Name.Equals(assetName, StringComparison.OrdinalIgnoreCase))?.Digest);

    private static GithubAsset? FindAsset(GithubRelease release, string assetName) =>
        release.Assets.FirstOrDefault(asset =>
            asset.Name.Equals(assetName, StringComparison.OrdinalIgnoreCase));

    // Asset download routed via GithubProxy: direct, then mirrors (for blocked/throttled regions).
    private Task DownloadToFileAsync(string url, string destPath, IProgress<double?>? progress, CancellationToken ct) =>
        gh.DownloadAsync(url, destPath, progress, ct);

    private static void StampNow(string path)
    {
        try
        {
            var now = DateTime.Now;
            File.SetCreationTime(path, now);
            File.SetLastWriteTime(path, now);
        }
        catch { /* cosmetic */ }
    }
}
