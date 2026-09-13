using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaToolsGui.Models;
using LuaToolsGui.Services;

namespace LuaToolsGui.ViewModels;

/// <summary>
/// First-run welcome overlay. Shown once (gated by <see cref="CacheService.OnboardingComplete"/>) over the
/// whole app: offers Discord sign-in, "apply recommended settings" (OpenSteamTools + FastFetch) and
/// "install the plugin", then applies the chosen actions on "Let's go!" and dismisses.
/// </summary>
public partial class OnboardingViewModel : ObservableObject
{
    private readonly AuthService _auth;
    private readonly CacheService _cache;
    private readonly SettingsService _settings;
    private readonly UnlockerService _unlocker;
    private readonly SteamService _steam;
    private readonly PluginInstallerService _installer;
    private readonly ToastService _toast;

    public OnboardingViewModel(AuthService auth, CacheService cache, SettingsService settings,
        UnlockerService unlocker, SteamService steam, PluginInstallerService installer, ToastService toast)
    {
        _auth = auth;
        _cache = cache;
        _settings = settings;
        _unlocker = unlocker;
        _steam = steam;
        _installer = installer;
        _toast = toast;
        _auth.AuthStateChanged += () => IsSignedIn = _auth.IsSignedIn;
        IsSignedIn = _auth.IsSignedIn;
    }

    /// <summary>Whether the overlay is visible. Set true by App on a fresh first launch.</summary>
    [ObservableProperty] private bool _isOpen;

    /// <summary>Set by App: refresh the Home dashboard after onboarding applies its actions (so the mode
    /// and plugin status tiles reflect the fresh install).</summary>
    public Func<Task>? RefreshHome { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGuest))]
    private bool _isSignedIn;
    public bool IsGuest => !IsSignedIn;

    [ObservableProperty] private bool _isSigningIn;

    // The two yes/no choices: both default to "yes".
    [ObservableProperty] private bool _applyRecommended = true;
    [ObservableProperty] private bool _installPlugin = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotBusy))]
    private bool _isBusy;
    public bool NotBusy => !IsBusy;

    /// <summary>Sign-in error text (shown under the sign-in button); null when none.</summary>
    [ObservableProperty] private string? _statusLine;

    [RelayCommand]
    private void SetApplyRecommended(bool value) => ApplyRecommended = value;

    [RelayCommand]
    private void SetInstallPlugin(bool value) => InstallPlugin = value;

    [RelayCommand]
    private async Task SignIn()
    {
        if (IsSigningIn) return;
        IsSigningIn = true;
        StatusLine = null;
        try { await _auth.SignInAsync(); }
        catch (Exception ex) { StatusLine = ex.Message; }
        finally { IsSigningIn = false; }
    }

    /// <summary>"Let's go!". Mark onboarding done, close the overlay immediately, then apply the chosen
    /// actions in the background. Closing first means a long-running (Steam-restarting) install can never
    /// leave the welcome dialog stuck.</summary>
    [RelayCommand]
    private void Finish()
    {
        if (IsBusy) return;
        bool applyRecommended = ApplyRecommended;
        bool installPlugin = InstallPlugin;

        _cache.OnboardingComplete = true; // seen, never show again
        IsOpen = false;                    // dismiss now, before any slow work

        if (applyRecommended || installPlugin)
            _ = ApplyChoicesAsync(applyRecommended, installPlugin);
    }

    /// <summary>Applies the recommended-settings / plugin choices in the background (best-effort, with
    /// toasts). Runs after the overlay is already closed.</summary>
    private async Task ApplyChoicesAsync(bool applyRecommended, bool installPlugin)
    {
        IsBusy = true;
        _toast.Show(Resources.Strings.Onboarding_Title, Resources.Strings.Onboarding_Applying);
        try
        {
            // The unlocker files are supplied manually, so applying the recommendation only selects OST.
            // Stop/restart Steam only when the separate LuaTools plugin actually needs installation.
            if (installPlugin)
                await Task.Run(_steam.StopSteam);

            if (applyRecommended)
            {
                _settings.FastFetch = true;
                _settings.SelectedMode = UnlockerMode.Ost.ToString();
                _unlocker.EnsureLuaPathRegistered();
            }

            if (installPlugin)
            {
                var (ok, error) = await _installer.InstallAsync(null);
                if (!ok)
                    _toast.Show(Resources.Strings.Onboarding_Title, error ?? "", error: true);
            }

            if (installPlugin)
                await Task.Run(_steam.StartSteam);
        }
        catch (Exception ex)
        {
            _toast.Show(Resources.Strings.Onboarding_Title, ex.Message, error: true);
        }
        finally
        {
            IsBusy = false;
            // Refresh the Home dashboard so the mode + plugin status tiles reflect what we just installed.
            if (RefreshHome is not null)
                try { await RefreshHome(); } catch { /* best effort */ }
        }
    }
}
