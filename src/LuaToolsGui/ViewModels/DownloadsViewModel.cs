using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaToolsGui.Services;
using LuaToolsGui.Services.Downloads;

namespace LuaToolsGui.ViewModels;

/// <summary>
/// The Downloads page. A thin projection over <see cref="DownloadQueue"/>: it holds no download state
/// of its own, so the queue stays the single source of truth for every entry point (Add, Fixes, the
/// store plugin and the protocol handler).
/// </summary>
public partial class DownloadsViewModel : ObservableObject
{
    private readonly DownloadQueue _queue;
    private readonly ManifestJobFactory _jobs;
    private readonly ToastService _toast;

    public DownloadsViewModel(DownloadQueue queue, ManifestJobFactory jobs, ToastService toast)
    {
        _queue = queue;
        _jobs = jobs;
        _toast = toast;

        _queue.Items.CollectionChanged += (_, _) => RaiseCounts();
        _queue.History.CollectionChanged += (_, _) => RaiseCounts();
        _queue.StateChanged += RaiseCounts;
    }

    /// <summary>Bound directly by the view: <c>Queue.Items</c> and <c>Queue.History</c>.</summary>
    public DownloadQueue Queue => _queue;

    /// <summary>Set by App: jump to the page that owns an item's pending confirmation.</summary>
    public Action<DownloadItem>? RevealItem { get; set; }

    public bool HasItems => _queue.Items.Count > 0;
    public bool HasHistory => _queue.History.Count > 0;
    public bool IsEmpty => !HasItems;

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(HasHistory));
        OnPropertyChanged(nameof(IsEmpty));
    }

    // ── Commands ─────────────────────────────────────────────────────

    /// <summary>
    /// Cancel an item. A depot download that has already written to disk asks what to do with the files
    /// first — cancelling leaves them behind otherwise, and they are not small.
    /// </summary>
    /// <remarks>
    /// Only depot downloads prompt: they are the only kind that writes a folder rather than a staged file
    /// the queue already cleans up. And only when the downloader actually used that folder, which
    /// <see cref="DepotDownloaderService.HasDownloadedContent"/> establishes from its own marker — the
    /// output directory is user-chosen and may contain unrelated files.
    /// </remarks>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task Cancel(DownloadItem item)
    {
        string? outDir = item.Job.OutputPath;
        if (item.Job.Kind is not DownloadKind.Depot
            || !DepotDownloaderService.HasDownloadedContent(outDir))
        {
            _queue.Cancel(item);
            return;
        }

        // Yes = stop and delete (the default), No = stop and keep, Cancel = keep downloading.
        // Escape lands on Cancel, so the accidental keypress is the harmless one even though the
        // destructive option is what Enter selects.
        var choice = MessageBox.Show(
            string.Format(Resources.Strings.Depot_Cancel_Body, ByteFormat.Size(item.BytesRead), outDir),
            Resources.Strings.Depot_Cancel_Title,
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Yes);

        if (choice == MessageBoxResult.Cancel) return; // leave the download running

        _queue.Cancel(item);
        if (choice != MessageBoxResult.Yes) return;

        // The kill is asynchronous and the downloader holds handles on everything it pre-allocated, so
        // deleting before the item settles would just fail on a locked file.
        await item.Completion;
        if (!DepotDownloaderService.TryDeleteCreatedFiles(outDir, item.CreatedFiles))
            _toast.Show(Resources.Strings.Depot_Cancel_Title,
                string.Format(Resources.Strings.Depot_Cancel_DeleteFailed, outDir), error: true);
    }

    [RelayCommand]
    private void Retry(DownloadItem item) => _queue.Retry(item);

    /// <summary>Depot downloads only — see <see cref="DownloadItem.CanPause"/>.</summary>
    [RelayCommand]
    private void Pause(DownloadItem item) => _queue.Pause(item);

    [RelayCommand]
    private void Resume(DownloadItem item) => _queue.Resume(item);

    [RelayCommand]
    private void Remove(DownloadItem item) => _queue.Remove(item);

    // ── Row actions (right-click) ────────────────────────────────────
    // Two commands per action because RelayCommand is typed and the queue and the history list bind
    // different row types. Both funnel into the same pair of helpers so the behaviour can't diverge.

    [RelayCommand]
    private void CopyAppId(DownloadItem item) => CopyId(item.AppId);

    [RelayCommand]
    private void CopyHistoryAppId(DownloadHistoryEntry entry) => CopyId(entry.AppId);

    [RelayCommand]
    private void ShowInFolder(DownloadItem item) => Show(item.RevealPath);

    [RelayCommand]
    private void ShowHistoryInFolder(DownloadHistoryEntry entry) => Show(entry.Record.RevealPath);

    private void CopyId(long appId)
    {
        if (!SteamService.CopyToClipboard(appId.ToString()))
            _toast.Show(Resources.Strings.Common_CopyAppId, Resources.Strings.Err_ClipboardBusy, error: true);
    }

    /// <summary>
    /// Open the install location. The path is recorded when the job finishes, so by the time a row can be
    /// right-clicked it is either real or absent — but the user can still have deleted it since.
    /// </summary>
    private void Show(string? path)
    {
        if (!SteamService.ShowInExplorer(path))
            _toast.Show(Resources.Strings.Common_ShowInFolder,
                string.Format(Resources.Strings.Err_PathMissing, path ?? ""), error: true);
    }

    [RelayCommand]
    private void MoveUp(DownloadItem item) => _queue.Move(item, -1);

    [RelayCommand]
    private void MoveDown(DownloadItem item) => _queue.Move(item, +1);

    [RelayCommand]
    private void ClearHistory()
    {
        // Deliberately NOT async: MessageBox.Show already blocks and returns a result, and an async
        // command would become an AsyncRelayCommand, which disables itself while running.
        var choice = MessageBox.Show(
            string.Format(Resources.Strings.Downloads_ClearHistory_Confirm, _queue.History.Count),
            Resources.Strings.Downloads_Action_ClearHistory,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No); // Enter must not wipe the list

        if (choice == MessageBoxResult.Yes) _queue.ClearHistory();
    }

    /// <summary>
    /// Remove one history row. No confirmation: it deletes a record, not a download, and prompting per
    /// row would be tedious. The bulk Clear history above does confirm, because it cannot be undone.
    /// </summary>
    [RelayCommand]
    private void RemoveHistoryEntry(DownloadHistoryEntry entry) => _queue.RemoveHistory(entry);

    /// <summary>Jump to the page that can resolve this item (e.g. the pending overwrite confirmation).</summary>
    [RelayCommand]
    private void Review(DownloadItem item)
    {
        item.Job.OnReveal?.Invoke();
        RevealItem?.Invoke(item);
    }
}
