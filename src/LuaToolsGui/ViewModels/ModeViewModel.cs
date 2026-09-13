using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaToolsGui.Models;
using LuaToolsGui.Services;

namespace LuaToolsGui.ViewModels;

/// <summary>One card on the Mode page. A single unlocker backend's name, status, and action button.</summary>
public partial class ModeCardViewModel(UnlockerMode mode, string title, string description) : ObservableObject
{
    public UnlockerMode Mode { get; } = mode;
    public string Title { get; } = title;
    public string Description { get; } = description;

    [ObservableProperty] private string _statusText = Resources.Strings.Mode_Checking;
    [ObservableProperty] private bool _isActive;

    /// <summary>OST is the nightly channel, so it carries the amber warning.</summary>
    public bool IsExperimental => Mode == UnlockerMode.Ost;

    /// <summary>OpenSteamTools carries native CloudRedirect support.</summary>
    public bool SupportsCloudRedirect => Mode == UnlockerMode.Ost;
}

/// <summary>
/// "Mode" page: OpenSteamTools / Custom. Mutually exclusive, one active at a time.
/// Checks status on page open; each card installs/switches after a Steam-shutdown confirmation, then
/// relaunches Steam so the new mode takes effect.
/// </summary>
public partial class ModeViewModel : ObservableObject
{
    private readonly UnlockerService _unlocker;
    private readonly ToastService _toast;
    private readonly CloudRedirectService _cloudRedirect;

    public ObservableCollection<ModeCardViewModel> Cards { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotBusy))]
    [NotifyPropertyChangedFor(nameof(CanUseCloudRedirect))]
    private bool _isBusy;
    public bool NotBusy => !IsBusy;

    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isProgressIndeterminate;

    public ModeViewModel(UnlockerService unlocker, ToastService toast,
        CloudRedirectService cloudRedirect)
    {
        _unlocker = unlocker;
        _toast = toast;
        _cloudRedirect = cloudRedirect;
    }

    /// <summary>CloudRedirect "Manage" (add-on panel): download (cache) the CloudRedirect GUI and launch it.</summary>
    [RelayCommand]
    private async Task ManageCloudRedirect()
    {
        if (IsBusy) return;
        IsBusy = true;
        IsProgressIndeterminate = true;
        Progress = 0;
        try
        {
            var prog = new Progress<double?>(p =>
            {
                IsProgressIndeterminate = p is null;
                if (p is not null) Progress = p.Value * 100;
            });

            bool ok = await _cloudRedirect.LaunchAsync(prog);
            if (!ok)
                _toast.Show(Resources.Strings.Mode_CloudRedirect_Manage,
                    Resources.Strings.Mode_CloudRedirect_LaunchFailed, error: true);
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    // ── CloudRedirect add-on (bottom panel; usable when OST is active) ───
    private const string CloudRedirectTitle = "CloudRedirect"; // product name, not localized

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCloudRedirectManage))]
    [NotifyPropertyChangedFor(nameof(ShowCloudRedirectUpdate))]
    [NotifyPropertyChangedFor(nameof(CanUseCloudRedirect))]
    private bool _cloudRedirectUnlocked;

    /// <summary>Buttons on the add-on panel are usable only when unlocked (Nightly active) and idle.</summary>
    public bool CanUseCloudRedirect => CloudRedirectUnlocked && !IsBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CloudRedirectToggleText))]
    [NotifyPropertyChangedFor(nameof(ShowCloudRedirectManage))]
    private bool _cloudRedirectEnabled;

    [ObservableProperty] private bool _cloudRedirectInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCloudRedirectUpdate))]
    private bool _cloudRedirectUpdateAvailable;

    [ObservableProperty] private string _cloudRedirectStatusText = "";

    public string CloudRedirectToggleText => CloudRedirectEnabled
        ? Resources.Strings.Mode_CloudRedirect_Disable
        : Resources.Strings.Mode_CloudRedirect_Enable;
    public bool ShowCloudRedirectUpdate => CloudRedirectUnlocked && CloudRedirectUpdateAvailable;
    public bool ShowCloudRedirectManage => CloudRedirectUnlocked && CloudRedirectEnabled;

    /// <summary>Refresh the add-on panel state. Reads dll/toml from disk (cheap); checks GitHub for an
    /// update only when unlocked (Nightly active), respecting forceRefresh.</summary>
    private async Task RefreshCloudRedirectAsync(bool forceRefresh)
    {
        CloudRedirectUnlocked = _unlocker.SelectedMode == UnlockerMode.Ost;
        var s = await _unlocker.GetCloudRedirectStateAsync(checkUpdate: CloudRedirectUnlocked, forceRefresh);
        CloudRedirectInstalled = s.Installed;
        CloudRedirectEnabled = s.Enabled;
        CloudRedirectUpdateAvailable = s.UpdateAvailable;

        CloudRedirectStatusText = !CloudRedirectUnlocked ? Resources.Strings.Mode_CloudRedirect_Locked
            : !s.Installed ? Resources.Strings.Mode_CloudRedirect_Status_NotInstalled
            : s.UpdateAvailable ? Resources.Strings.Mode_CloudRedirect_Status_UpdateAvailable
            : s.Enabled ? Resources.Strings.Mode_CloudRedirect_Status_Enabled
            : Resources.Strings.Mode_CloudRedirect_Status_Disabled;
    }

    /// <summary>Enable/disable the add-on (edits opensteamtool.toml; first enable also downloads the dll).
    /// Lightweight, no Steam close; the change applies on the next Steam launch.</summary>
    [RelayCommand]
    private async Task ToggleCloudRedirect()
    {
        if (IsBusy || !CloudRedirectUnlocked) return;
        bool enabling = !CloudRedirectEnabled;
        IsBusy = true;
        IsProgressIndeterminate = true;
        Progress = 0;
        try
        {
            var prog = new Progress<double?>(p =>
            {
                IsProgressIndeterminate = p is null;
                if (p is not null) Progress = p.Value * 100;
            });

            var result = enabling
                ? await _unlocker.EnableCloudRedirectAsync(prog)
                : _unlocker.DisableCloudRedirect();

            if (result.Success)
                _toast.Show(CloudRedirectTitle, enabling
                    ? Resources.Strings.Mode_CloudRedirect_Toast_Enabled
                    : Resources.Strings.Mode_CloudRedirect_Toast_Disabled);
            else
                _toast.Show(CloudRedirectTitle, result.Error ?? "", error: true);

            await LoadAsync();
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    /// <summary>Replace cloud_redirect.dll with the latest. Reports "close Steam" if it's locked.</summary>
    [RelayCommand]
    private async Task UpdateCloudRedirect()
    {
        if (IsBusy || !CloudRedirectUnlocked) return;
        IsBusy = true;
        IsProgressIndeterminate = true;
        Progress = 0;
        try
        {
            var prog = new Progress<double?>(p =>
            {
                IsProgressIndeterminate = p is null;
                if (p is not null) Progress = p.Value * 100;
            });

            var result = await _unlocker.UpdateCloudRedirectAsync(prog);
            if (result.Success)
                _toast.Show(CloudRedirectTitle, Resources.Strings.Mode_CloudRedirect_Toast_Updated);
            else
                _toast.Show(CloudRedirectTitle, result.Error ?? "", error: true);

            await LoadAsync();
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    /// <summary>Rebuild the card list while preserving existing cards and their bound state.</summary>
    private void SyncCards()
    {
        var visible = _unlocker.Modes;

        // Remove cards no longer visible.
        for (int i = Cards.Count - 1; i >= 0; i--)
            if (!visible.Any(d => d.Mode == Cards[i].Mode))
                Cards.RemoveAt(i);

        // Add newly-visible cards in definition order.
        foreach (var def in visible)
            if (!Cards.Any(c => c.Mode == def.Mode))
            {
                int idx = visible.IndexOf(def);
                idx = Math.Min(idx, Cards.Count);
                Cards.Insert(idx, new ModeCardViewModel(def.Mode, def.DisplayName, def.Description));
            }
    }

    /// <summary>
    /// Page open / refresh. OpenSteamTool status is checked from the local Steam folder only.
    /// </summary>
    private bool _detectionAttempted;

    public async Task LoadAsync(bool forceRefresh = false)
    {
        // First time with no mode selected: try to auto-detect an existing install by hashing the
        // on-disk DLLs against published releases, and adopt the match as active.
        if (!_detectionAttempted && _unlocker.SelectedMode is null)
        {
            _detectionAttempted = true;
            await _unlocker.DetectActiveModeAsync();
        }

        // Re-evaluate which cards are visible (hidden modes appear only when revealed).
        SyncCards();

        var active = _unlocker.SelectedMode;
        foreach (var card in Cards)
        {
            if (card.Mode == active)
            {
                card.StatusText = Resources.Strings.Mode_Checking;
                Apply(card, await _unlocker.GetStateAsync(card.Mode, forceRefresh));
            }
            else
            {
                // No status check is needed for inactive modes.
                Apply(card, new ModeState(card.Mode, ModeStatus.NotInstalled, IsActive: false, null));
            }
        }

        // Bottom CloudRedirect add-on panel (locked unless OST is the active mode).
        await RefreshCloudRedirectAsync(forceRefresh);
    }

    private DateTime _lastCheck;

    [RelayCommand]
    private async Task CheckForUpdates()
    {
        // 30s cooldown: a forced refresh hits GitHub, and the unauthenticated API allows only 60/hr.
        if (IsBusy || DateTime.UtcNow - _lastCheck < TimeSpan.FromSeconds(30)) return;
        _lastCheck = DateTime.UtcNow;
        await LoadAsync(forceRefresh: true);
    }

    private void Apply(ModeCardViewModel card, ModeState s)
    {
        // Only the ACTIVE mode shows real install/update status. Inactive modes share filenames with
        // the active one (different contents), so their hash check is meaningless. Show them simply
        // as the switch target instead of a misleading "Update available".
        if (s.Status == ModeStatus.UserManaged)
            // Custom: there's nothing to be up to date WITH, so the status never varies and the only
            // action is switching to it. Checked before IsActive so it never reads "Not active".
            card.StatusText = Resources.Strings.Mode_UserManaged;
        else if (s.Status == ModeStatus.Unknown)
            card.StatusText = Resources.Strings.Mode_StatusUnavailable;
        else if (!s.IsActive)
            card.StatusText = Resources.Strings.Mode_NotActive;
        else
            card.StatusText = s.Status switch
            {
                ModeStatus.NotInstalled => Resources.Strings.Mode_NotInstalled,
                ModeStatus.UpToDate => Resources.Strings.Mode_UpToDate,
                ModeStatus.UpdateAvailable => Resources.Strings.Mode_UpdateAvailable,
                _ => Resources.Strings.Mode_StatusUnavailable,
            };

        card.IsActive = s.IsActive;
    }

}
