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
            return Task.FromResult(Reply("Invalid", "Campos de identificação inválidos."));
        if (request.Verb == "update-status")
            return Task.FromResult(Reply(request.DryRun ? "DryRun" : "Completed", "Estado das atualizações."));
        if (request.Verb is not ("update-check" or "update-download" or "update-rollback" or "update-cancel" or "update-apply"))
            return Task.FromResult(Reply("Unsupported", "Comando de atualização desconhecido."));
        if (exiting || agentUpdateDisposed)
            return Task.FromResult(Reply("Unavailable", "A overlay está encerrando."));
        if (request.Channel.Length > 0 && request.Channel is not ("stable" or "testing"))
            return Task.FromResult(Reply("Invalid", "Canal inválido. Use stable ou testing."));
        if (request.RequestId.Length is 0 or > 128 || request.BuildId.Length > 160 || request.JobId.Length > 128)
            return Task.FromResult(Reply("Invalid", "Identificação da solicitação inválida."));
        if (request.Verb == "update-cancel")
        {
            if (snapshot.JobId.Length == 0 || request.JobId != snapshot.JobId)
                return Task.FromResult(Reply("Conflict", "Informe o JobId exato da operação que deve ser cancelada."));
            if (updateCommitting || snapshot.Phase == "Restarting")
                return Task.FromResult(Reply("Busy", "A troca de versão já foi iniciada; não há promessa de cancelamento após o commit."));
            if (!updateBusy || updateCancellation is null || snapshot.Terminal)
                return Task.FromResult(Reply("Unavailable", "Esta operação não está em andamento."));
            if (request.DryRun)
                return Task.FromResult(Reply("DryRun", "O job pode ser cancelado; nenhuma entrada foi alterada."));
            if (!AgentClientAlive(token, continuing))
                return Task.FromResult(Reply("Cancelled", "O cliente não está mais conectado."));
            updateGeneration++;
            agentUpdateApplyPin = null;
            SetAgentState("Cancelled", "Operação cancelada. A versão instalada foi preservada.", true, persist: true);
            SetUpdateStatus(agentUpdateState!.Message);
            updateCancellation.Cancel();
            return Task.FromResult(Reply("Cancelled", agentUpdateState.Message));
        }

        if (busy || updateBusy || updateCommitting || ownedDialog || TransitionPending || Confirming)
            return Task.FromResult(Reply("Busy", "Outra operação, confirmação ou diálogo está em andamento."));
        if (request.Verb == "update-apply")
        {
            var error = ValidateAgentApply(request);
            if (error is not null)
                return Task.FromResult(Reply(error.Value.Outcome, error.Value.Reason));
            if (request.DryRun)
                return Task.FromResult(Reply("DryRun", "Pacote pronto e identificado; nenhuma instalação foi iniciada."));
            if (!AgentClientAlive(token, continuing))
                return Task.FromResult(Reply("Cancelled", "O cliente não está mais conectado."));
            agentUpdateApplyPin = new(request.RequestId, agentUpdateState!.JobId, updateGeneration, preparedUpdate!, AgentPreparedSignature(preparedUpdate!), DateTimeOffset.UtcNow.AddSeconds(15).ToUnixTimeMilliseconds());
            return Task.FromResult(Reply("UpdateApplyRequested", "Pacote validado. A troca de versão aguarda a confirmação do canal conectado.", false));
        }

        if (!AgentSourceAvailable)
            return Task.FromResult(Reply("Unavailable", "A prévia padrão não consulta nem instala atualizações."));
        var channel = request.Channel.Length == 0 ? updateChannel : request.Channel;
        UpdateCandidate? candidate = null;
        InstalledBuild? previous = null;
        if (request.Verb == "update-download")
        {
            var matches = updateCandidates.Where(c => c.BuildId == request.BuildId && c.Channel == channel).Take(2).ToArray();
            candidate = matches.Length == 1 ? matches[0] : null;
            if (candidate is null || request.BuildId.Length == 0)
                return Task.FromResult(Reply("Unavailable", "A versão não pertence ao catálogo atual deste canal. Consulte update-check novamente."));
            if (candidate.BuildId == AppBuild.Current.BuildId)
                return Task.FromResult(Reply("Unavailable", "Esta versão já está em execução."));
            var targetVersion = candidate.Asset?.Version;
            if (targetVersion is null && !Velopack.SemanticVersion.TryParse(candidate.Version, out targetVersion))
                return Task.FromResult(Reply("Invalid", "A versão selecionada não tem uma identificação semântica válida."));
            if (!request.AllowDowngrade && Velopack.SemanticVersion.TryParse(VelopackInstallation.CurrentVersion ?? AppBuild.Current.Version, out var currentVersion) && targetVersion < currentVersion)
                return Task.FromResult(Reply("Conflict", "A versão selecionada é anterior à instalada. Escolha explicitamente o BuildId com AllowDowngrade ou use update-rollback."));
        }

        if (request.Verb == "update-rollback")
        {
            if (!AgentRealInstall)
                return Task.FromResult(Reply("Unavailable", "A reversão exige uma instalação real pelo Setup."));
            try
            {
                previous = VelopackInstallation.History().SingleOrDefault(h => h.BuildId == request.BuildId && !h.Current && !h.LastStartFailed);
            }
            catch (Exception error) when (UpdateFailure(error))
            {
                return Task.FromResult(Reply("Unavailable", "O histórico local não pôde ser lido."));
            }

            if (previous is null)
                return Task.FromResult(Reply("Unavailable", "A versão exata não está no histórico local de instalações."));
        }

        if (request.DryRun)
            return Task.FromResult(Reply("DryRun", "Operação disponível; nenhuma consulta, download ou escrita foi iniciada."));
        if (!AgentClientAlive(token, continuing))
            return Task.FromResult(Reply("Cancelled", "O cliente não está mais conectado."));
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
            Message = request.Verb == "update-check" ? "Buscando versões publicadas…" : request.Verb == "update-rollback" ? "Verificando a versão preservada para reversão…" : "Preparando download da versão selecionada…"
        };
        try
        {
            PersistAgentReceipt();
        }
        catch (Exception error) when (UpdateFailure(error))
        {
            agentUpdateState = priorState;
            agentTargetCommit = priorCommit;
            agentTargetChannel = priorChannel;
            return Task.FromResult(Reply("Unavailable", "Não foi possível preservar o recibo da operação. Confira as permissões e o espaço em disco."));
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
        UpdateTask = RunAgentUpdateJob(request.Verb, candidate, previous, channel, generation, cancellation);
        return Task.FromResult(Reply("Requested", "Operação iniciada. Consulte update-status usando o JobId retornado.", false));
    }

    internal async Task CommitAgentUpdate(AutomationRequest request, CancellationToken token, Func<bool> continuing)
    {
        if (!Dispatcher.CheckAccess())
        {
            await Dispatcher.InvokeAsync(() => CommitAgentUpdate(request, token, continuing)).Task.Unwrap();
            return;
        }

        var pin = agentUpdateApplyPin;
        if (pin is null || pin.RequestId != request.RequestId)
            return;
        agentUpdateApplyPin = null;
        if (!AgentClientAlive(token, continuing))
            return;
        var error = ValidateAgentApply(request);
        if (error is not null || !AgentApplyPinMatches(pin.ExpiresUtc, pin.JobId, agentUpdateState?.JobId, pin.Generation, updateGeneration, pin.Prepared, preparedUpdate, pin.Signature))
        {
            SetAgentState("Failed", error?.Reason ?? "O pacote ou o contexto mudou antes da confirmação. Prepare a versão novamente.", true, persist: true);
            SetUpdateStatus(agentUpdateState!.Message);
            return;
        }

        SetAgentState("Restarting", "Troca de versão iniciada pelo Velopack. A conclusão será confirmada pela identidade do próximo processo.", false);
        try
        {
            PersistAgentReceipt();
        }
        catch (Exception failure) when (UpdateFailure(failure))
        {
            SetAgentState("Failed", "Não foi possível preservar o recibo da troca de versão. A instalação não foi iniciada.", true);
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
            });
        }
        catch (Exception failure) when (UpdateFailure(failure))
        {
            SetUpdateStatus("Não foi possível iniciar a troca de versão. " + failure.Message);
        }

        if (!scheduled && !exiting)
            SetAgentState("Failed", updateStatus, true, persist: true);
    }

    private (string Outcome, string Reason)? ValidateAgentApply(AutomationRequest request)
    {
        if (Controller.SettingsDirty)
            return ("Conflict", "Salve ou descarte os ajustes antes de trocar de versão.");
        if (!AgentRealInstall)
            return ("Unavailable", "Aplicar exige uma instalação real pelo Setup; prévias e testes não instalam pacotes.");
        if (busy || updateBusy || updateCommitting || ownedDialog || TransitionPending || Confirming)
            return ("Busy", "Outra operação ou confirmação está em andamento.");
        if (agentUpdateState is not { Phase: "Ready" } state || preparedUpdate?.Native is null || !AgentReadyIdentityMatches(state, preparedUpdate, request.BuildId, request.JobId))
            return ("Conflict", "Informe o BuildId exato de um pacote pronto e seu JobId correspondente.");
        if (request.Channel.Length > 0 && preparedUpdate.Build.Channel != request.Channel)
            return ("Conflict", "O canal não corresponde ao pacote preparado.");
        return null;
    }

    private async Task RunAgentUpdateJob(string verb, UpdateCandidate? candidate, InstalledBuild? previous, string channel, long generation, CancellationTokenSource cancellation)
    {
        await Task.Yield();
        bool Current() => !exiting && !agentUpdateDisposed && generation == updateGeneration && ReferenceEquals(updateCancellation, cancellation) && !cancellation.IsCancellationRequested;
        try
        {
            if (!Current())
                return;
            if (verb == "update-check")
            {
                var catalog = await updateSource.CheckAsync(channel, cancellation.Token);
                if (!Current())
                    return;
                updateCandidates = catalog;
                SetAgentState("Catalog", catalog.Count == 0 ? "Nenhuma versão publicada neste canal." : "Catálogo consultado. Escolha o BuildId exato para baixar.", true, persist: true);
            }
            else if (verb == "update-download")
            {
                var progress = new Progress<UpdateProgress>(value => AgentUpdateProgress(value, generation, cancellation));
                var prepared = await updateSource.StageAsync(candidate!, progress, cancellation.Token);
                if (!Current())
                {
                    updateSource.Discard(prepared);
                    return;
                }

                if (prepared.Build.BuildId != candidate!.BuildId || prepared.Build.Version != candidate.Version || prepared.Build.Commit != candidate.Commit || prepared.Build.Channel != channel)
                {
                    updateSource.Discard(prepared);
                    throw new InvalidDataException("O pacote preparado difere da versão exata solicitada.");
                }

                preparedUpdate = prepared;
                agentTargetCommit = prepared.Build.Commit;
                agentTargetChannel = prepared.Build.Channel;
                SetAgentState("Ready", "Pacote verificado. Aplique explicitamente para instalar e reiniciar; nenhum reinício foi iniciado.", true, progress: 100, persist: true);
            }
            else
            {
                var target = await VelopackInstallation.PrepareRollbackAsync(previous!.BuildId, percent => AgentUpdateProgress(new("Verificando reversão", percent, 100), generation, cancellation), cancellation.Token);
                if (!Current())
                    return;
                var build = await LocalUpdateHistory.ReadBuildInfoAsync(target.PackagePath, cancellation.Token);
                if (!Current())
                    return;
                if (build.BuildId != previous.BuildId || build.Version != previous.Version || build.Commit != previous.Commit)
                    throw new InvalidDataException("O pacote local difere da versão exata solicitada.");
                preparedUpdate = new(target.PackagePath, build, target);
                agentTargetCommit = build.Commit;
                agentTargetChannel = build.Channel;
                SetAgentState("Ready", "Reversão verificada. Aplique explicitamente para voltar à versão preservada.", true, progress: 100, persist: true);
            }
        }
        catch (OperationCanceledException)
        {
            if (Current() || ReferenceEquals(updateCancellation, cancellation) && !exiting && !agentUpdateDisposed && agentUpdateState?.Phase != "Cancelled")
                SetAgentState("Cancelled", "Operação cancelada. A versão instalada foi preservada.", true, persist: true);
        }
        catch (Exception error) when (error is not (OutOfMemoryException or AccessViolationException))
        {
            if (Current())
            {
                ReportUpdateFailure("Não foi possível concluir a operação de atualização.", error);
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

        if (exiting || agentUpdateDisposed || generation != updateGeneration || updateCancellation != cancellation || cancellation.IsCancellationRequested || agentUpdateState?.Phase != "Downloading")
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
    internal static bool AgentApplyPinMatches(long expiresUtc, string pinnedJobId, string? currentJobId, long pinnedGeneration, long currentGeneration, PreparedUpdate pinned, PreparedUpdate? current, string signature) => expiresUtc >= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() && pinnedJobId == currentJobId && pinnedGeneration == currentGeneration && ReferenceEquals(pinned, current) && signature == AgentPreparedSignature(pinned);
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
                Controller.Diagnostics.Log("agent-update-receipt", ResultKind.Failed);
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
                Message = "Nenhuma operação de atualização iniciada pelo agente."
            };
        try
        {
            LocalUpdateHistory.RejectLink(AgentReceiptPath);
            if (new FileInfo(AgentReceiptPath).Length > 49152)
                throw new InvalidDataException("Recibo excede o limite.");
            var receipt = JsonSerializer.Deserialize<AgentUpdateReceipt>(File.ReadAllBytes(AgentReceiptPath), AgentReceiptJson);
            if (receipt?.SchemaVersion != 1 || receipt.State is not { } valid || !AgentReceiptText(valid.Message, 2048) || !AgentReceiptText(valid.JobId, 128) || !AgentReceiptText(valid.TargetBuildId, 160) || !AgentReceiptText(valid.TargetVersion, 160) || !AgentReceiptText(receipt.TargetCommit, 128) || !AgentReceiptText(receipt.TargetChannel, 32) || valid.Phase is not ("Idle" or "Checking" or "Catalog" or "Downloading" or "Ready" or "Failed" or "Cancelled" or "Restarting" or "Completed" or "Interrupted") || valid.Progress is < 0 or > 100)
                throw new InvalidDataException("Recibo inválido.");
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
            return new()
            {
                Phase = "Interrupted",
                Terminal = true,
                Message = "O recibo anterior não pôde ser lido. A versão em execução não foi alterada."
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
                Message = "A versão exata solicitada está em execução como instalação do Velopack."
            };
        if (anotherProcess && (!state.Terminal || state.Phase is "Ready" or "Restarting"))
            return state with
            {
                Phase = "Interrupted",
                Terminal = true,
                Message = "O processo foi reiniciado antes da confirmação da versão solicitada. Consulte e prepare a versão novamente."
            };
        return state;
    }

    private void PersistAgentReceipt()
    {
        var state = AgentSnapshot(readOnly: true);
        var receipt = new AgentUpdateReceipt(1, Environment.ProcessId, Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks, state, agentTargetCommit, agentTargetChannel);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(receipt, AgentReceiptJson);
        if (bytes.Length > 49152)
            throw new InvalidDataException("O recibo da atualização excedeu o limite permitido.");
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
            SetAgentState("Interrupted", "A overlay foi encerrada antes de concluir a operação. Nenhuma atualização foi aplicada automaticamente.", true, persist: true);
        updateGeneration++;
    }
}
