using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Velopack.Exceptions;

namespace ApolloOverlay;

public partial class MainWindow
{
    private readonly IUpdateSource updateSource;
    private IReadOnlyList<UpdateCandidate> updateCandidates = [];
    private UpdateCandidate? updateReview;
    private InstalledBuild? rollbackReview;
    private PreparedUpdate? preparedUpdate;
    private CancellationTokenSource? updateCancellation;
    private bool updateBusy, updateCommitting, updateCloseAuthorized;
    private int updateHistoryPage;
    private long updateGeneration, nextUpdateProgress;
    private string updateChannel = AppBuild.Current.Channel == "stable" ? "stable" : "testing";
    private string updateStatus = "Check for versions published after project validation.";
    private TextBlock? updateProgressLabel;
    private TextBox? updateNotesText;
    internal Task UpdateTask
    {
        get;
        private set;
    } = Task.CompletedTask;

    private void OpenUpdates() => OpenView(new(OverlayRoute.Updates, SelectedId: "check-updates"));
    private void BuildUpdates()
    {
        FeatureHeading(AppBuild.Current.Display, "Installed version · " + (AppBuild.Current.Channel == "stable" ? "Stable" : "Testing"), "updates");
        FeatureFact("Build", AppBuild.Current.BuildId);
        FeatureFact("Commit", AppBuild.Current.ShortCommit);
        updateProgressLabel = Paragraph(updateStatus);
        AutomationProperties.SetLiveSetting(updateProgressLabel, AutomationLiveSetting.Polite);
        AddRow("check-updates", "Check for updates", "Check versions published on GitHub", () => UpdateTask = CheckUpdatesAsync(), updateSource is not PreviewUpdateSource);
        AddRow("update-channel", "Channel · " + (updateChannel == "testing" ? "Testing" : "Stable"), updateChannel == "testing" ? "Validated preview builds · includes changes under evaluation" : "Versions published from the main branch", () =>
        {
            using var command = BeginCommand("update-channel");
            var nextChannel = updateChannel == "testing" ? "stable" : "testing";
            try
            {
                if (!diagnostic && !Controller.Platform.IsPreview)
                    UpdateChannelSettings.Write(Controller.Diagnostics.Root, nextChannel);
            }
            catch (Exception e) when (UpdateFailure(e))
            {
                command?.Fail(e, "channel-write-failed");
                ReportUpdateFailure("Could not save the channel.", e);
                return;
            }

            updateChannel = nextChannel;
            updateGeneration++;
            updateCandidates = [];
            updateStatus = "Channel changed. Check for available versions.";
            command?.Complete(Controller.Platform.IsPreview ? ResultKind.Simulated : ResultKind.Completed, "channel-changed");
            RefreshRows();
        });
        if (!VelopackInstallation.IsInstalled)
            Paragraph("To enable updates, install Game Session Overlay using Setup.");
        if (updateCandidates.Count > 0)
        {
            Section("Available versions");
            foreach (var candidate in updateCandidates)
            {
                var selected = candidate;
                var current = candidate.BuildId == AppBuild.Current.BuildId;
                AddRow("release:" + candidate.BuildId, candidate.Version + (current ? " · installed" : ""), candidate.PublishedAt.ToLocalTime().ToString("MMM d, yyyy HH:mm") + " · commit " + candidate.Commit[..7] + " · " + (candidate.Size / 1024d / 1024d).ToString("N0") + " MB", () => ReviewRelease(selected), !current);
            }
        }

        Section("Restore an earlier version");
        try
        {
            var history = (diagnostic || Controller.Platform.IsPreview ? Array.Empty<InstalledBuild>() : VelopackInstallation.History()).Where(h => !h.Current && !h.LastStartFailed).OrderByDescending(h => h.InstalledAt).ToArray();
            if (history.Length == 0)
                Paragraph("Previously installed versions will appear here. Local rollback works offline.");
            const int pageSize = 8;
            var pages = Math.Max(1, (history.Length + pageSize - 1) / pageSize);
            updateHistoryPage = Math.Clamp(updateHistoryPage, 0, pages - 1);
            if (pages > 1)
                Paragraph($"Local history · page {updateHistoryPage + 1} of {pages}", 14);
            foreach (var item in history.Skip(updateHistoryPage * pageSize).Take(pageSize))
            {
                var selected = item;
                AddRow("rollback:" + item.BuildId, "Restore " + item.Version, "Available on this computer · " + item.InstalledAt.ToLocalTime().ToString("MMM d, yyyy HH:mm"), () =>
                {
                    rollbackReview = selected;
                    updateReview = null;
                    OpenView(new(OverlayRoute.UpdateReview, SelectedId: "apply-rollback"));
                });
            }

            if (pages > 1)
            {
                AddRow("history-previous", "Newer versions", "Previous page of local history", () =>
                {
                    updateHistoryPage--;
                    RefreshRows();
                }, updateHistoryPage > 0);
                AddRow("history-next", "Older versions", "Next page of local history", () =>
                {
                    updateHistoryPage++;
                    RefreshRows();
                }, updateHistoryPage + 1 < pages);
            }
        }
        catch (Exception e) when (UpdateFailure(e))
        {
            LogUpdateFault("update-history-read", e, "history-unavailable");
            Paragraph("The local version history could not be read. The current version was preserved.");
        }

        AddRow("back", "Back", "Returns to app settings", Back);
    }

    private void ReviewRelease(UpdateCandidate candidate)
    {
        updateReview = candidate;
        rollbackReview = null;
        if (preparedUpdate?.Build.BuildId != candidate.BuildId)
        {
            if (preparedUpdate is { } obsolete)
                updateSource.Discard(obsolete);
            preparedUpdate = null;
        }

        updateStatus = preparedUpdate is null
            ? "Ready to download. The installed version has not changed."
            : "Download verified. The installed version has not changed.";
        SetUpdateStatus(updateStatus);
        OpenView(new(OverlayRoute.UpdateReview, SelectedId: preparedUpdate is null ? "prepare-update" : "apply-update"));
    }

    private void BuildUpdateReview()
    {
        updateNotesText = null;
        if (rollbackReview is { } old)
        {
            FeatureHeading("Restore " + old.Version, "The current version stays in local history. Compatible saved settings are restored when available.", "apply-rollback");
            updateProgressLabel = Paragraph("The earlier version is available on this computer.");
            AddRow("apply-rollback", "Restore and restart", old.Version + " · retained on this computer", () => BeginUpdateCommit(async () =>
            {
                var target = await VelopackInstallation.PrepareRollbackAsync(old.BuildId);
                await VelopackInstallation.ScheduleApplyAsync(target);
            }, "update-rollback"), !Controller.SettingsDirty && !diagnostic && !Controller.Platform.IsPreview);
        }
        else if (updateReview is { } candidate)
        {
            FeatureHeading(preparedUpdate is null ? "Update available" : "Update downloaded", candidate.Version + " · " + (candidate.Channel == "stable" ? "Stable" : "Testing"), "updates");
            FeatureFact("Installed", AppBuild.Current.Display);
            FeatureFact("Update commit", candidate.Commit[..7]);
            FeatureFact("Download", (candidate.Size / 1024d / 1024d).ToString("N1") + " MB");
            updateProgressLabel = Paragraph(preparedUpdate is null ? updateStatus : "Download verified. The installed version has not changed.");
            AutomationProperties.SetLiveSetting(updateProgressLabel, AutomationLiveSetting.Polite);
            if (preparedUpdate is null)
                AddRow("prepare-update", "Download update", UpdateActionContext(candidate), () => UpdateTask = DownloadUpdateAsync(candidate), updateSource is not PreviewUpdateSource);
            else
                AddRow("apply-update", "Install and restart", UpdateActionContext(candidate) + " · verified", () =>
                {
                    var staged = preparedUpdate;
                    if (staged is null || staged.Build.BuildId != candidate.BuildId)
                        return;
                    BeginUpdateCommit(() => VelopackInstallation.ScheduleApplyAsync(staged.Native ?? throw new InvalidDataException("Check and download the selected version again.")));
                }, !Controller.SettingsDirty && !diagnostic && !Controller.Platform.IsPreview && VelopackInstallation.IsInstalled);
            if (!VelopackInstallation.IsInstalled)
                Paragraph("Use the Setup installer to enable updates on this computer.");
            if (candidate.Notes.Length > 0)
            {
                AddRow("update-notes", "Read changes", "A / Enter to read · arrows scroll · ← / → returns to actions", () => updateNotesText?.Focus());
                updateNotesText = new TextBox
                {
                    Text = candidate.Notes,
                    IsReadOnly = true,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    MinHeight = 110,
                    MaxHeight = 170,
                    FontSize = 16,
                    Padding = new(12),
                    Margin = new(0, 8, 0, 8)
                };
                updateNotesText.SetResourceReference(BackgroundProperty, ThemeSupport.Card);
                updateNotesText.SetResourceReference(ForegroundProperty, ThemeSupport.Text);
                AutomationProperties.SetName(updateNotesText, "Changes in version " + candidate.Version);
                AddVisual(updateNotesText);
            }
        }

        if (Controller.SettingsDirty)
            Paragraph("Save or discard your settings before restarting to change versions.");
        AddRow("back", "Back", "Leaving this page does not start an installation", Back);
    }

    private async Task CheckUpdatesAsync()
    {
        using var command = BeginCommand("update-check");
        if (updateBusy || busy || diagnostic && updateSource is PreviewUpdateSource)
        {
            command?.Complete(ResultKind.Unavailable, "check-blocked");
            return;
        }
        var generation = ++updateGeneration;
        using var cancellation = new CancellationTokenSource();
        updateCancellation = cancellation;
        busy = updateBusy = true;
        ClearPreviousUpdateResult();
        updateStatus = "Checking versions on GitHub…";
        SetUpdateStatus(updateStatus);
        try
        {
            var releases = await updateSource.CheckAsync(updateChannel, cancellation.Token);
            if (exiting || generation != updateGeneration)
            {
                command?.Complete(ResultKind.Cancelled, "catalog-result-superseded");
                return;
            }
            updateCandidates = releases;
            updateStatus = releases.Count == 0 ? "No versions published to this channel. Builds appear only after passing validation." : releases[0].BuildId == AppBuild.Current.BuildId ? "You are using the latest version of this channel." : "Versions are available. Choose one to read the changes and update.";
            command?.Complete(ResultKind.Completed, releases.Count == 0 ? "catalog-empty" : "catalog-checked");
            RequestCue(FeedbackCue.Completion);
        }
        catch (OperationCanceledException)
        {
            command?.Complete(ResultKind.Cancelled, "check-cancelled");
            if (!exiting)
                updateStatus = "Check cancelled. The installed version was preserved.";
        }
        catch (Exception e) when (UpdateFailure(e))
        {
            command?.Fail(e, "catalog-check-failed");
            if (!exiting)
                ReportUpdateFailure("Could not check for updates.", e);
        }
        catch (Exception e) when (e is not (OutOfMemoryException or AccessViolationException))
        {
            command?.Fail(e, "unexpected-check-failure");
            throw;
        }
        finally
        {
            if (ReferenceEquals(updateCancellation, cancellation))
            {
                updateCancellation = null;
                busy = updateBusy = false;
                if (cancellation.IsCancellationRequested && !exiting)
                    updateStatus = "Check cancelled. The installed version was preserved.";
            }

            if (!exiting)
            {
                SetUpdateStatus(updateStatus);
                UpdatePersistentState();
                if (view.Current.Route == OverlayRoute.Updates)
                    RefreshRows();
            }
        }
    }

    private async Task DownloadUpdateAsync(UpdateCandidate candidate)
    {
        using var command = BeginCommand("update-download");
        if (updateBusy || busy)
        {
            command?.Complete(ResultKind.Unavailable, "download-blocked");
            return;
        }
        var generation = ++updateGeneration;
        using var cancellation = new CancellationTokenSource();
        updateCancellation = cancellation;
        busy = updateBusy = true;
        ClearPreviousUpdateResult();
        updateStatus = "Preparing download…";
        SetUpdateStatus(updateStatus);
        try
        {
            var progress = new Progress<UpdateProgress>(value =>
            {
                if (exiting || generation != updateGeneration || updateCancellation != cancellation)
                    return;
                var now = Environment.TickCount64;
                if (value.Total > 0 && now < nextUpdateProgress)
                    return;
                nextUpdateProgress = now + 100;
                SetUpdateStatus(value.Phase + (value.Total > 0 ? " · " + Math.Clamp(value.Bytes * 100 / value.Total, 0, 100) + "%" : ""));
            });
            var prepared = await updateSource.StageAsync(candidate, progress, cancellation.Token);
            if (exiting || generation != updateGeneration || cancellation.IsCancellationRequested)
            {
                updateSource.Discard(prepared);
                command?.Complete(ResultKind.Cancelled, "prepared-package-discarded");
                return;
            }

            preparedUpdate = prepared;
            updateStatus = "Download verified. Select Install and restart to finish.";
            navigation = new("apply-update");
            command?.Complete(ResultKind.Completed, "download-verified");
            RequestCue(FeedbackCue.Completion);
        }
        catch (OperationCanceledException)
        {
            command?.Complete(ResultKind.Cancelled, "download-cancelled");
            if (!exiting)
                updateStatus = "Download cancelled. The installed version was preserved.";
        }
        catch (Exception e) when (UpdateFailure(e))
        {
            command?.Fail(e, "download-failed");
            if (!exiting)
                ReportUpdateFailure("Could not prepare the update.", e);
        }
        catch (Exception e) when (e is not (OutOfMemoryException or AccessViolationException))
        {
            command?.Fail(e, "unexpected-download-failure");
            throw;
        }
        finally
        {
            if (ReferenceEquals(updateCancellation, cancellation))
            {
                updateCancellation = null;
                busy = updateBusy = false;
                if (cancellation.IsCancellationRequested && !exiting)
                    updateStatus = "Download cancelled. The installed version was preserved.";
            }

            if (!exiting)
            {
                SetUpdateStatus(updateStatus);
                UpdatePersistentState();
                if (view.Current.Route == OverlayRoute.UpdateReview)
                    RefreshRows();
            }
        }
    }

    private void BeginUpdateCommit(Func<Task> prepare, string command = "update-apply")
    {
        if (Controller.SettingsDirty)
        {
            using var rejected = BeginCommand(command);
            rejected?.Complete(ResultKind.Unavailable, "unsaved-settings");
            FeedbackText("Save or discard your settings before changing versions.");
            RequestCue(FeedbackCue.Unavailable);
            return;
        }

        QueueHandoff(() => UpdateTask = CommitUpdateAsync(prepare, command));
    }

    private async Task CommitUpdateAsync(Func<Task> prepare, string commandId = "update-apply", string? origin = null, string? correlation = null)
    {
        using var command = BeginCommand(commandId, correlation: correlation, origin: origin);
        busy = updateBusy = updateCommitting = true;
        foreach (var button in new[]
        {
            HomeTab,
            AppsTab,
            SettingsTab,
            SettingsGear,
            BackButton
        }

        )
            button.IsEnabled = false;
        SetUpdateStatus("Preparing to restart. Please wait…");
        try
        {
            await prepare();
            command?.Complete(ResultKind.Requested, "restart-scheduled");
            if (!exiting)
            {
                updateCloseAuthorized = true;
                Close();
            }
        }
        catch (Exception e) when (UpdateFailure(e))
        {
            command?.Fail(e, "restart-scheduling-failed");
            if (!exiting)
                ReportUpdateFailure("Could not restart into the selected version. The current installation was preserved.", e);
        }
        catch (Exception e) when (e is not (OutOfMemoryException or AccessViolationException))
        {
            command?.Fail(e, "unexpected-restart-failure");
            throw;
        }
        finally
        {
            busy = updateBusy = updateCommitting = false;
            foreach (var button in new[]
            {
                HomeTab,
                AppsTab,
                SettingsTab,
                SettingsGear,
                BackButton
            }

            )
                button.IsEnabled = true;
            if (!exiting)
            {
                UpdatePersistentState();
                RefreshRows();
            }
        }
    }

    private void SetUpdateStatus(string message)
    {
        updateStatus = message;
        if (updateProgressLabel is not null)
            updateProgressLabel.Text = message;
        FeedbackText(message);
    }

    private static string UpdateActionContext(UpdateCandidate candidate) => candidate.Version + " · "
        + (candidate.Channel == "stable" ? "Stable" : "Testing");

    private bool CancelUpdateInteraction()
    {
        if (!updateBusy)
            return false;
        using var command = BeginCommand("update-cancel");
        if (updateCommitting)
        {
            command?.Complete(ResultKind.Unavailable, "commit-already-started");
            FeedbackText("The version change is being prepared. Wait for the restart.");
            RequestCue(FeedbackCue.Unavailable);
            return true;
        }

        if (interfaceActionDepth == 0)
            RequestCue(FeedbackCue.Cancellation);
        updateGeneration++;
        // Cancellation can synchronously run the awaiting operation's finally on this UI
        // context. Publish the pending state first so it cannot overwrite the terminal one.
        SetUpdateStatus("Cancelling. The installed version will be preserved…");
        updateCancellation?.Cancel();
        command?.Complete(ResultKind.Requested, "cancellation-requested");
        return true;
    }

    private void LogUpdateFault(string commandId, Exception error, string reason, string? correlation = null)
    {
        using var command = BeginCommand(commandId, origin: "system", correlation: correlation);
        command?.Fail(error, reason);
    }

    private static bool UpdateFailure(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or HttpRequestException or JsonException or InvalidOperationException or ArgumentException or Win32Exception or TimeoutException or System.Security.SecurityException or ChecksumFailedException or AcquireLockFailedException or NotInstalledException;
    private void ClearPreviousUpdateResult()
    {
        // A new attempt supersedes only the result owned by this feature.
        // Another action may still have a result the user needs to read.
        if (Controller.Operation is not { Action: "update" })
            return;
        Controller.DismissOperation();
        UpdatePersistentState();
    }

    private void ReportUpdateFailure(string message, Exception error)
    {
        var reason = error switch
        {
            ChecksumFailedException => "The downloaded package failed its integrity check. Installation did not start. Download the update again.",
            AcquireLockFailedException => "Another update is in progress. Wait for it to finish and try again.",
            NotInstalledException => "Installation not found. Repair the overlay using Setup before updating.",
            UnauthorizedAccessException => "Check that the repository is public and GitHub is available.",
            InvalidDataException or TimeoutException => error.Message,
            _ => "Check your connection, disk space and folder permissions. Try again."
        };
        updateStatus = message + " " + reason;
        SetUpdateStatus(updateStatus);
        RetainOperation("Unconfirmed", updateStatus, "update");
        RequestCue(FeedbackCue.Unavailable);
    }

    internal void ShowUpdateHistoryWarning()
    {
        using var command = BeginCommand("update-history-retention", origin: "system");
        command?.Complete(ResultKind.Unavailable, "current-package-not-retained");
        updateStatus = "Could not retain the current package for rollback. Repair the installation using Setup before updating.";
    }

    internal void ScheduleUpdateNotice() => ShowUpdateRecoveryNotice();
    internal void ShowUpdateRecoveryNotice()
    {
        if (!VelopackInstallation.IsInstalled)
            return;
        if (VelopackInstallation.ReadNotice() is { } notice)
        {
            using var command = BeginCommand("update-settings-recovery", origin: "system");
            command?.Complete(notice.StartsWith("Earlier version restored", StringComparison.Ordinal) ? ResultKind.Completed : ResultKind.Unknown, "settings-recovery-notice");
            updateStatus = notice;
            if (Controller.Operation is null or { Action: "update" })
            {
                RetainOperation(notice.StartsWith("Earlier version restored", StringComparison.Ordinal) ? "Completed" : "Unconfirmed", notice, "update");
                FeedbackText(notice);
            }
        }
    }

    private void DisposeUpdates()
    {
        DisposeAgentUpdates();
        updateGeneration++;
        updateCancellation?.Cancel();
        if (!updateCommitting && preparedUpdate is { } unused)
            updateSource.Discard(unused);
        updateSource.Dispose();
    }
}

internal sealed class PreviewUpdateSource : IUpdateSource
{
    public Task<IReadOnlyList<UpdateCandidate>> CheckAsync(string channel, CancellationToken cancellation) => Task.FromResult<IReadOnlyList<UpdateCandidate>>([]);
    public Task<PreparedUpdate> StageAsync(UpdateCandidate candidate, IProgress<UpdateProgress>? progress, CancellationToken cancellation) => Task.FromException<PreparedUpdate>(new InvalidOperationException("Preview does not install updates."));
    public void Dispose()
    {
    }
}
