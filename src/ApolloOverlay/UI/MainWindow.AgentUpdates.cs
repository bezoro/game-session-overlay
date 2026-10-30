using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using ApolloOverlay.Automation;

namespace ApolloOverlay;

public partial class MainWindow
{
    private sealed record AgentUpdateReceipt(int SchemaVersion, int OriginPid, long OriginStarted, AutomationUpdateState State, string TargetCommit, string TargetChannel);
    private sealed record AgentUpdateApplyPin(string RequestId, string JobId, long Generation, PreparedUpdate Prepared, string Signature, long ExpiresUtc);
    private static readonly JsonSerializerOptions AgentReceiptJson = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private AutomationUpdateState? agentUpdateState;
    private AgentUpdateApplyPin? agentUpdateApplyPin;
    private string agentTargetCommit = "", agentTargetChannel = "";
    private bool agentUpdateDisposed;
    private long agentReceiptAt;
    private string AgentReceiptPath => Path.Combine(Controller.Diagnostics.Root, "agent-update-receipt.json");
    private bool AgentSourceAvailable => updateSource is not PreviewUpdateSource;
    private bool AgentRealInstall => !diagnostic && !Controller.Platform.IsPreview && VelopackInstallation.IsInstalled;

    internal Task<AutomationReply> AgentUpdate(AutomationRequest request, CancellationToken token, Func<bool> continuing)
    {
        if (!Dispatcher.CheckAccess())
            return Dispatcher.InvokeAsync(() => AgentUpdate(request, token, continuing)).Task.Unwrap();
        var snapshot = AgentSnapshot(readOnly: request.DryRun);
        AutomationReply Reply(string outcome, string reason, bool terminal = true) => new()
        {
            Outcome = outcome,
            Reason = reason,
            RequestId = request.RequestId ?? "",
            Terminal = terminal,
            Update = AgentSnapshot(request.DryRun)
        };
        if (request.RequestId is null || request.Channel is null || request.BuildId is null || request.JobId is null)
            return Task.FromResult(Reply("Invalid", "Invalid identity fields."));
        if (request.Verb == "update-status")
            return Task.FromResult(Reply(request.DryRun ? "DryRun" : "Completed", "Update status."));
        if (request.Verb is not ("update-check" or "update-download" or "update-rollback" or "update-cancel" or "update-apply"))
            return Task.FromResult(Reply("Unsupported", "Unknown update command."));
        if (exiting || agentUpdateDisposed)
            return Task.FromResult(Reply("Unavailable", "The overlay is closing."));
        if (request.Channel.Length > 0 && request.Channel is not ("stable" or "testing"))
            return Task.FromResult(Reply("Invalid", "Invalid channel. Use stable or testing."));
        if (request.RequestId.Length is 0 or > 128 || request.BuildId.Length > 160 || request.JobId.Length > 128)
            return Task.FromResult(Reply("Invalid", "Invalid request identity."));
        if (request.Verb == "update-cancel")
        {
            if (snapshot.JobId.Length == 0 || request.JobId != snapshot.JobId)
                return Task.FromResult(Reply("Conflict", "Provide the exact JobId of the operation to cancel."));
            if (updateCommitting || snapshot.Phase == "Restarting")
                return Task.FromResult(Reply("Busy", "The version change has started; cancellation is not guaranteed after commit."));
            if (!updateBusy || updateCancellation is null || snapshot.Terminal)
                return Task.FromResult(Reply("Unavailable", "This operation is not in progress."));
            if (request.DryRun)
                return Task.FromResult(Reply("DryRun", "The job can be cancelled; no input was changed."));
            if (!AgentClientAlive(token, continuing))
                return Task.FromResult(Reply("Cancelled", "The client is no longer connected."));
            updateGeneration++;
            agentUpdateApplyPin = null;
            SetAgentState("Cancelled", "Operation cancelled. The installed version was preserved.", true, persist: true);
            SetUpdateStatus(agentUpdateState!.Message);
            updateCancellation.Cancel();
            return Task.FromResult(Reply("Cancelled", agentUpdateState.Message));
        }

        if (busy || updateBusy || updateCommitting || ownedDialog || TransitionPending || Confirming)
            return Task.FromResult(Reply("Busy", "Another operation, confirmation or dialog is in progress."));
        if (request.Verb == "update-apply")
        {
            var error = ValidateAgentApply(request);
            if (error is not null)
                return Task.FromResult(Reply(error.Value.Outcome, error.Value.Reason));
            if (request.DryRun)
                return Task.FromResult(Reply("DryRun", "Package ready and identified; no installation has started."));
            if (!AgentClientAlive(token, continuing))
                return Task.FromResult(Reply("Cancelled", "The client is no longer connected."));
            agentUpdateApplyPin = new(request.RequestId, agentUpdateState!.JobId, updateGeneration, preparedUpdate!, AgentPreparedSignature(preparedUpdate!), DateTimeOffset.UtcNow.AddSeconds(15).ToUnixTimeMilliseconds());
            return Task.FromResult(Reply("UpdateApplyRequested", "Package verified. The version change is waiting for confirmation from the connected client.", false));
        }

        if (!AgentSourceAvailable)
            return Task.FromResult(Reply("Unavailable", "The default preview does not check for or install updates."));
        var channel = request.Channel.Length == 0 ? updateChannel : request.Channel;
        UpdateCandidate? candidate = null;
        InstalledBuild? previous = null;
        if (request.Verb == "update-download")
        {
            var matches = updateCandidates.Where(c => c.BuildId == request.BuildId && c.Channel == channel).Take(2).ToArray();
            candidate = matches.Length == 1 ? matches[0] : null;
            if (candidate is null || request.BuildId.Length == 0)
                return Task.FromResult(Reply("Unavailable", "This build is not in the current channel catalog. Run update-check again."));
            if (candidate.BuildId == AppBuild.Current.BuildId)
                return Task.FromResult(Reply("Unavailable", "This version is already running."));
            var targetVersion = candidate.Asset?.Version;
            if (targetVersion is null && !Velopack.SemanticVersion.TryParse(candidate.Version, out targetVersion))
                return Task.FromResult(Reply("Invalid", "The selected build does not have a valid semantic version."));
            if (!request.AllowDowngrade
                && Velopack.SemanticVersion.TryParse(VelopackInstallation.CurrentVersion ?? AppBuild.Current.Version, out var currentVersion)
                && targetVersion < currentVersion)
                return Task.FromResult(Reply("Conflict", "The selected version is older than the installed one. Explicitly choose its BuildId with AllowDowngrade or use update-rollback."));
        }

        if (request.Verb == "update-rollback")
        {
            if (!AgentRealInstall)
                return Task.FromResult(Reply("Unavailable", "Rollback requires an installation created by Setup."));
            try
            {
                previous = VelopackInstallation.History().SingleOrDefault(h => h.BuildId == request.BuildId && !h.Current && !h.LastStartFailed);
            }
            catch (Exception error) when (UpdateFailure(error))
            {
                LogUpdateFault("agent-update-history-read", error, "history-unavailable", request.RequestId);
                return Task.FromResult(Reply("Unavailable", "The local version history could not be read."));
            }

            if (previous is null)
                return Task.FromResult(Reply("Unavailable", "The exact build is not in the local installation history."));
        }

        if (request.DryRun)
            return Task.FromResult(Reply("DryRun", "Operation available; no query, download or write has started."));
        if (!AgentClientAlive(token, continuing))
            return Task.FromResult(Reply("Cancelled", "The client is no longer connected."));
        var priorState = agentUpdateState;
        var priorCommit = agentTargetCommit;
        var priorChannel = agentTargetChannel;
        agentUpdateApplyPin = null;
        agentTargetCommit = candidate?.Commit ?? previous?.Commit ?? "";
        agentTargetChannel = candidate?.Channel ?? (previous is null ? "" : channel);
        var phase = request.Verb == "update-check" ? "Checking" : "Downloading";
        agentUpdateState = new()
        {
            JobId = request.RequestId,
            Phase = phase,
            Terminal = false,
            TargetBuildId = candidate?.BuildId ?? previous?.BuildId ?? "",
            TargetVersion = candidate?.Version ?? previous?.Version ?? "",
            Message = request.Verb == "update-check" ? "Checking published versions…" : request.Verb == "update-rollback" ? "Verifying the retained version for rollback…" : "Preparing to download the selected version…"
        };
        try
        {
            PersistAgentReceipt();
        }
        catch (Exception error) when (UpdateFailure(error))
        {
            LogUpdateFault("agent-update-receipt", error, "receipt-write-failed", request.RequestId);
            agentUpdateState = priorState;
            agentTargetCommit = priorCommit;
            agentTargetChannel = priorChannel;
            return Task.FromResult(Reply("Unavailable", "Could not retain the operation receipt. Check permissions and available disk space."));
        }

        updateChannel = channel;
        if (request.Verb == "update-check")
            updateCandidates = [];
        if (preparedUpdate is { } obsolete)
        {
            updateSource.Discard(obsolete);
            preparedUpdate = null;
        }

        var cancellation = new CancellationTokenSource();
        updateCancellation = cancellation;
        var generation = ++updateGeneration;
        busy = updateBusy = true;
        ClearPreviousUpdateResult();
        SetUpdateStatus(agentUpdateState.Message);
        UpdatePersistentState();
        UpdateTask = RunAgentUpdateJob(request.Verb, candidate, previous, channel, generation, cancellation, request.RequestId);
        return Task.FromResult(Reply("Requested", "Operation started. Query update-status with the returned JobId.", false));
    }

    internal async Task CommitAgentUpdate(AutomationRequest request, CancellationToken token, Func<bool> continuing)
    {
        if (!Dispatcher.CheckAccess())
        {
            await Dispatcher.InvokeAsync(() => CommitAgentUpdate(request, token, continuing)).Task.Unwrap();
            return;
        }

        using var command = BeginCommand("agent-update-commit", origin: "cli", correlation: request.RequestId);
        var pin = agentUpdateApplyPin;
        if (pin is null || pin.RequestId != request.RequestId)
        {
            command?.Complete(ResultKind.Unavailable, "apply-pin-unavailable");
            return;
        }
        agentUpdateApplyPin = null;
        if (!AgentClientAlive(token, continuing))
        {
            command?.Complete(ResultKind.Cancelled, "client-disconnected-before-commit");
            return;
        }
        var error = ValidateAgentApply(request);
        if (error is not null || !AgentApplyPinMatches(pin.ExpiresUtc, pin.JobId, agentUpdateState?.JobId, pin.Generation, updateGeneration, pin.Prepared, preparedUpdate, pin.Signature))
        {
            SetAgentState("Failed", error?.Reason ?? "The package or context changed before confirmation. Prepare the version again.", true, persist: true);
            SetUpdateStatus(agentUpdateState!.Message);
            command?.Complete(ResultKind.Unavailable, "prepared-context-changed");
            return;
        }

        SetAgentState("Restarting", "Version change started by Velopack. Completion will be confirmed by the identity of the next process.", false);
        try
        {
            PersistAgentReceipt();
        }
        catch (Exception failure) when (UpdateFailure(failure))
        {
            command?.Fail(failure, "receipt-write-failed");
            SetAgentState("Failed", "Could not retain the version-change receipt. Installation did not start.", true);
            SetUpdateStatus(agentUpdateState!.Message);
            return;
        }

        var scheduled = false;
        try
        {
            await CommitUpdateAsync(async () =>
            {
                await VelopackInstallation.ScheduleApplyAsync(pin.Prepared.Native!);
                scheduled = true;
            }, "update-apply", "cli", request.RequestId);
        }
        catch (Exception failure) when (UpdateFailure(failure))
        {
            command?.Fail(failure, "restart-scheduling-failed");
            SetUpdateStatus("Could not start the version change. " + failure.Message);
        }

        command?.Complete(scheduled ? ResultKind.Requested : ResultKind.Failed, scheduled ? "restart-scheduled" : "restart-not-scheduled");
        if (!scheduled && !exiting)
            SetAgentState("Failed", updateStatus, true, persist: true);
    }

    private (string Outcome, string Reason)? ValidateAgentApply(AutomationRequest request)
    {
        if (Controller.SettingsDirty)
            return ("Conflict", "Save or discard your settings before changing versions.");
        if (!AgentRealInstall)
            return ("Unavailable", "Applying requires an installation created by Setup; previews and tests do not install packages.");
        if (busy || updateBusy || updateCommitting || ownedDialog || TransitionPending || Confirming)
            return ("Busy", "Another operation or confirmation is in progress.");
        if (agentUpdateState is not { Phase: "Ready" } state || preparedUpdate?.Native is null || !AgentReadyIdentityMatches(state, preparedUpdate, request.BuildId, request.JobId))
            return ("Conflict", "Provide the exact BuildId of a prepared package and its corresponding JobId.");
        if (request.Channel.Length > 0 && preparedUpdate.Build.Channel != request.Channel)
            return ("Conflict", "The channel does not match the prepared package.");
        return null;
    }

    private async Task RunAgentUpdateJob(string verb, UpdateCandidate? candidate, InstalledBuild? previous, string channel, long generation, CancellationTokenSource cancellation, string requestId)
    {
        using var command = BeginCommand(verb, origin: "cli", correlation: requestId);
        await Task.Yield();
        bool Current() => !exiting && !agentUpdateDisposed && generation == updateGeneration && ReferenceEquals(updateCancellation, cancellation) && !cancellation.IsCancellationRequested;
        try
        {
            if (!Current())
            {
                command?.Complete(ResultKind.Cancelled, "job-context-lost");
                return;
            }
            if (verb == "update-check")
            {
                var catalog = await updateSource.CheckAsync(channel, cancellation.Token);
                if (!Current())
                {
                    command?.Complete(ResultKind.Cancelled, "catalog-result-superseded");
                    return;
                }
                updateCandidates = catalog;
                SetAgentState("Catalog", catalog.Count == 0 ? "No versions have been published to this channel." : "Catalog checked. Choose the exact BuildId to download.", true, persist: true);
                command?.Complete(ResultKind.Completed, catalog.Count == 0 ? "catalog-empty" : "catalog-checked");
            }
            else if (verb == "update-download")
            {
                var progress = new Progress<UpdateProgress>(value => AgentUpdateProgress(value, generation, cancellation));
                var prepared = await updateSource.StageAsync(candidate!, progress, cancellation.Token);
                if (!Current())
                {
                    updateSource.Discard(prepared);
                    command?.Complete(ResultKind.Cancelled, "prepared-package-discarded");
                    return;
                }

                if (prepared.Build.BuildId != candidate!.BuildId || prepared.Build.Version != candidate.Version || prepared.Build.Commit != candidate.Commit || prepared.Build.Channel != channel)
                {
                    updateSource.Discard(prepared);
                    throw new InvalidDataException("The prepared package differs from the exact build requested.");
                }

                preparedUpdate = prepared;
                agentTargetCommit = prepared.Build.Commit;
                agentTargetChannel = prepared.Build.Channel;
                SetAgentState("Ready", "Package verified. Explicitly apply it to install and restart; no restart has started.", true, progress: 100, persist: true);
                command?.Complete(ResultKind.Completed, "download-verified");
            }
            else
            {
                var target = await VelopackInstallation.PrepareRollbackAsync(previous!.BuildId, percent => AgentUpdateProgress(new("Verifying rollback", percent, 100), generation, cancellation), cancellation.Token);
                if (!Current())
                {
                    command?.Complete(ResultKind.Cancelled, "rollback-result-superseded");
                    return;
                }
                var build = await LocalUpdateHistory.ReadBuildInfoAsync(target.PackagePath, cancellation.Token);
                if (!Current())
                {
                    command?.Complete(ResultKind.Cancelled, "rollback-result-superseded");
                    return;
                }
                if (build.BuildId != previous.BuildId || build.Version != previous.Version || build.Commit != previous.Commit)
                    throw new InvalidDataException("The local package differs from the exact build requested.");
                preparedUpdate = new(target.PackagePath, build, target);
                agentTargetCommit = build.Commit;
                agentTargetChannel = build.Channel;
                SetAgentState("Ready", "Rollback verified. Explicitly apply it to return to the retained version.", true, progress: 100, persist: true);
                command?.Complete(ResultKind.Completed, "rollback-verified");
            }
        }
        catch (OperationCanceledException)
        {
            command?.Complete(ResultKind.Cancelled, "job-cancelled");
            if (Current() || ReferenceEquals(updateCancellation, cancellation) && !exiting && !agentUpdateDisposed && agentUpdateState?.Phase != "Cancelled")
                SetAgentState("Cancelled", "Operation cancelled. The installed version was preserved.", true, persist: true);
        }
        catch (Exception error) when (error is not (OutOfMemoryException or AccessViolationException))
        {
            command?.Fail(error, "update-job-failed");
            if (Current())
            {
                ReportUpdateFailure("Could not complete the update operation.", error);
                SetAgentState("Failed", updateStatus, true, persist: true);
            }
        }
        finally
        {
            if (ReferenceEquals(updateCancellation, cancellation))
            {
                updateCancellation = null;
                busy = updateBusy = false;
            }

            cancellation.Dispose();
            if (!exiting && !agentUpdateDisposed)
            {
                SetUpdateStatus(agentUpdateState?.Message ?? updateStatus);
                UpdatePersistentState();
            }
        }
    }

    private void AgentUpdateProgress(UpdateProgress value, long generation, CancellationTokenSource cancellation)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted)
                Dispatcher.BeginInvoke(() => AgentUpdateProgress(value, generation, cancellation));
            return;
        }

        if (exiting
            || agentUpdateDisposed
            || generation != updateGeneration
            || updateCancellation != cancellation
            || cancellation.IsCancellationRequested
            || agentUpdateState?.Phase != "Downloading")
            return;
        var percent = value.Total > 0 ? (int)Math.Clamp((double)value.Bytes / value.Total * 100, 0, 100) : agentUpdateState.Progress;
        SetAgentState("Downloading", value.Phase + (value.Total > 0 ? " · " + percent + "%" : ""), false, percent, Environment.TickCount64 - agentReceiptAt >= 500);
        SetUpdateStatus(agentUpdateState!.Message);
    }

    private static bool AgentClientAlive(CancellationToken token, Func<bool> continuing)
    {
        if (token.IsCancellationRequested)
            return false;
        try
        {
            return continuing();
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException)
        {
            return false;
        }
    }

    private static string AgentPreparedSignature(PreparedUpdate value) => string.Join("|", value.Build.BuildId, value.Build.Version, value.Build.Commit, value.Build.Channel, value.PackagePath, value.Native?.Asset.FileName, value.Native?.Asset.SHA256, value.Native?.Asset.Size);
    internal static bool AgentReadyIdentityMatches(AutomationUpdateState state, PreparedUpdate prepared, string buildId, string jobId) => buildId.Length > 0 && jobId.Length > 0 && state.TargetBuildId == buildId && prepared.Build.BuildId == buildId && state.JobId == jobId;
    internal static bool AgentApplyPinMatches(
        long expiresUtc,
        string pinnedJobId,
        string? currentJobId,
        long pinnedGeneration,
        long currentGeneration,
        PreparedUpdate pinned,
        PreparedUpdate? current,
        string signature
    ) => expiresUtc >= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        && pinnedJobId == currentJobId
        && pinnedGeneration == currentGeneration
        && ReferenceEquals(pinned, current)
        && signature == AgentPreparedSignature(pinned);
    private void SetAgentState(string phase, string message, bool terminal, int? progress = null, bool persist = false)
    {
        agentUpdateState = (agentUpdateState ?? new()) with
        {
            Phase = phase,
            Message = message.Length > 2048 ? message[..2048] : message,
            Terminal = terminal,
            Progress = Math.Clamp(progress ?? agentUpdateState?.Progress ?? 0, 0, 100)
        };
        if (persist)
            try
            {
                PersistAgentReceipt();
            }
            catch (Exception error) when (UpdateFailure(error))
            {
                LogUpdateFault("agent-update-receipt", error, "receipt-write-failed", agentUpdateState?.JobId);
            }
    }

    private AutomationUpdateState AgentSnapshot(bool readOnly = false)
    {
        var state = agentUpdateState ?? ReadAgentReceipt(captureTarget: !readOnly);
        if (!readOnly && agentUpdateState is null)
            agentUpdateState = state;
        AutomationUpdateRelease[] history = [];
        if (AgentRealInstall)
            try
            {
                history = VelopackInstallation.History().Where(h => !h.Current && !h.LastStartFailed).Take(32).Select(h => new AutomationUpdateRelease(h.BuildId, h.Version, h.Commit, "")).ToArray();
            }
            catch (Exception error) when (UpdateFailure(error))
            {
            }

        var current = AppBuild.Current;
        return state with
        {
            Version = current.Version,
            BuildId = current.BuildId,
            Commit = current.Commit,
            Channel = current.Channel,
            SelectedChannel = updateChannel,
            Installed = AgentRealInstall,
            SettingsDirty = Controller.SettingsDirty,
            Available = updateCandidates.Take(32).Select(c => new AutomationUpdateRelease(c.BuildId, c.Version, c.Commit, c.Channel, c.Size)).ToArray(),
            History = history
        };
    }

    private AutomationUpdateState ReadAgentReceipt(bool captureTarget)
    {
        if ((diagnostic || Controller.Platform.IsPreview) && updateSource is PreviewUpdateSource || !File.Exists(AgentReceiptPath))
            return new()
            {
                Message = "No update operation has been started by an agent."
            };
        try
        {
            LocalUpdateHistory.RejectLink(AgentReceiptPath);
            if (new FileInfo(AgentReceiptPath).Length > 49152)
                throw new InvalidDataException("Receipt exceeds the limit.");
            var receipt = JsonSerializer.Deserialize<AgentUpdateReceipt>(File.ReadAllBytes(AgentReceiptPath), AgentReceiptJson);
            if (receipt?.SchemaVersion != 1
                || receipt.State is not { } valid
                || !AgentReceiptText(valid.Message, 2048)
                || !AgentReceiptText(valid.JobId, 128)
                || !AgentReceiptText(valid.TargetBuildId, 160)
                || !AgentReceiptText(valid.TargetVersion, 160)
                || !AgentReceiptText(receipt.TargetCommit, 128)
                || !AgentReceiptText(receipt.TargetChannel, 32)
                || valid.Phase is not ("Idle" or "Checking" or "Catalog" or "Downloading" or "Ready" or "Failed" or "Cancelled" or "Restarting" or "Completed" or "Interrupted")
                || valid.Progress is < 0 or > 100)
                throw new InvalidDataException("Invalid receipt.");
            if (captureTarget)
            {
                agentTargetCommit = receipt.TargetCommit;
                agentTargetChannel = receipt.TargetChannel;
            }

            var state = receipt.State;
            var anotherProcess = receipt.OriginPid != Environment.ProcessId || receipt.OriginStarted != Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
            return ResolveAgentUpdateReceipt(state, new(state.TargetVersion, state.TargetBuildId, receipt.TargetCommit, receipt.TargetChannel), AppBuild.Current, AgentRealInstall, anotherProcess);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        {
            LogUpdateFault("agent-update-receipt", error, "receipt-read-failed");
            return new()
            {
                Phase = "Interrupted",
                Terminal = true,
                Message = "The previous receipt could not be read. The running version was not changed."
            };
        }
    }

    private static bool AgentReceiptText(string? value, int maximum) => value is not null && value.Length <= maximum && !value.Contains('\0');
    internal static AutomationUpdateState ResolveAgentUpdateReceipt(AutomationUpdateState state, AppBuildInfo target, AppBuildInfo running, bool installed, bool anotherProcess)
    {
        if (anotherProcess && (state.Phase is "Ready" or "Restarting") && installed && target == running)
            return state with
            {
                Phase = "Completed",
                Terminal = true,
                Progress = 100,
                Message = "The exact requested build is running as a Velopack installation."
            };
        if (anotherProcess && (!state.Terminal || state.Phase is "Ready" or "Restarting"))
            return state with
            {
                Phase = "Interrupted",
                Terminal = true,
                Message = "The process restarted before the requested version was confirmed. Check and prepare the version again."
            };
        return state;
    }

    private void PersistAgentReceipt()
    {
        var state = AgentSnapshot(readOnly: true);
        var receipt = new AgentUpdateReceipt(1, Environment.ProcessId, Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks, state, agentTargetCommit, agentTargetChannel);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(receipt, AgentReceiptJson);
        if (bytes.Length > 49152)
            throw new InvalidDataException("The update receipt exceeded the allowed size.");
        LocalUpdateHistory.AtomicBytes(AgentReceiptPath, bytes);
        agentReceiptAt = Environment.TickCount64;
    }

    private void DisposeAgentUpdates()
    {
        if (agentUpdateDisposed)
            return;
        agentUpdateDisposed = true;
        agentUpdateApplyPin = null;
        if (agentUpdateState is { Terminal: false, Phase: not "Restarting" })
            SetAgentState("Interrupted", "The overlay closed before the operation completed. No update was applied automatically.", true, persist: true);
        updateGeneration++;
    }
}
