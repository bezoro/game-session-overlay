using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

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
    private string updateStatus = "Busque versões publicadas após os testes do projeto.";
    private TextBlock? updateProgressLabel;
    private TextBox? updateNotesText;
        internal Task UpdateTask { get; private set; } = Task.CompletedTask;

    private void OpenUpdates() => OpenView(new(OverlayRoute.Updates, SelectedId: "check-updates"));

    private void BuildUpdates()
    {
        Paragraph("Versão instalada", 14);
        Paragraph(AppBuild.Current.Display, 23);
        Paragraph("Build " + AppBuild.Current.BuildId + " · commit " + AppBuild.Current.ShortCommit, 14);
        updateProgressLabel = Paragraph(updateStatus);
        AutomationProperties.SetLiveSetting(updateProgressLabel, AutomationLiveSetting.Polite);
        AddRow("check-updates", "Buscar atualizações", "Consultar versões publicadas no GitHub", () => UpdateTask = CheckUpdatesAsync(), updateSource is not PreviewUpdateSource);
        AddRow("update-channel", "Canal · " + (updateChannel == "testing" ? "Testes" : "Estável"),
            updateChannel == "testing" ? "Novos builds aprovados nos testes · inclui mudanças em avaliação" : "Versões publicadas a partir da versão principal", () =>
            {
                var nextChannel=updateChannel == "testing" ? "stable" : "testing";
                try { if(!diagnostic&&!Controller.Platform.IsPreview)UpdateChannelSettings.Write(Controller.Diagnostics.Root,nextChannel); }
                catch(Exception e) when(UpdateFailure(e)) { ReportUpdateFailure("Não foi possível salvar o canal.",e); return; }
                updateChannel=nextChannel;
                updateGeneration++; updateCandidates = []; updateStatus = "Canal alterado. Busque as versões disponíveis.";
                RefreshRows();
            });
        if (!VelopackInstallation.IsInstalled)
            Paragraph("Para atualizar, instale a versão inicial usando o Setup do Game Session Overlay.");
        if (updateCandidates.Count > 0)
        {
            Section("Versões disponíveis");
            foreach (var candidate in updateCandidates)
            {
                var selected = candidate;
                var current = candidate.BuildId == AppBuild.Current.BuildId;
                AddRow("release:" + candidate.BuildId, candidate.Version + (current ? " · instalada" : ""),
                    candidate.PublishedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm") + " · commit " + candidate.Commit[..7] + " · " + (candidate.Size / 1024d / 1024d).ToString("N0") + " MB",
                    () => ReviewRelease(selected), !current);
            }
        }
        Section("Voltar a uma versão anterior");
        try
        {
            var history = (diagnostic || Controller.Platform.IsPreview ? Array.Empty<InstalledBuild>() : VelopackInstallation.History()).Where(h => !h.Current && !h.LastStartFailed).OrderByDescending(h => h.InstalledAt).ToArray();
            if (history.Length == 0) Paragraph("As versões já instaladas aparecerão aqui. A reversão local funciona sem internet.");
            const int pageSize=8;
            var pages=Math.Max(1,(history.Length+pageSize-1)/pageSize);
            updateHistoryPage=Math.Clamp(updateHistoryPage,0,pages-1);
            if(pages>1)Paragraph($"Histórico local · página {updateHistoryPage+1} de {pages}",14);
            foreach (var item in history.Skip(updateHistoryPage*pageSize).Take(pageSize))
            {
                var selected = item;
                AddRow("rollback:" + item.BuildId, "Voltar para " + item.Version, "Disponível neste computador · " + item.InstalledAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm"), () =>
                {
                    rollbackReview = selected; updateReview = null;
                    OpenView(new(OverlayRoute.UpdateReview, SelectedId: "apply-rollback"));
                });
            }
            if(pages>1) {
                AddRow("history-previous","Versões mais recentes","Página anterior do histórico local",()=>{updateHistoryPage--;RefreshRows();},updateHistoryPage>0);
                AddRow("history-next","Versões mais antigas","Próxima página do histórico local",()=>{updateHistoryPage++;RefreshRows();},updateHistoryPage+1<pages);
            }
        }
        catch (Exception e) when (UpdateFailure(e)) { Paragraph("O histórico local não pôde ser lido. A versão atual foi preservada."); }
        AddRow("back", "Voltar", "Retorna aos ajustes do aplicativo", Back);
    }

    private void ReviewRelease(UpdateCandidate candidate)
    {
        updateReview = candidate; rollbackReview = null;
        if (preparedUpdate?.Build.BuildId != candidate.BuildId) { if(preparedUpdate is {} obsolete)updateSource.Discard(obsolete); preparedUpdate = null; }
        OpenView(new(OverlayRoute.UpdateReview, SelectedId: preparedUpdate is null ? "prepare-update" : "apply-update"));
    }

    private void BuildUpdateReview()
    {
        updateNotesText = null;
        if (rollbackReview is {} old)
        {
            Paragraph("Voltar para " + old.Version, 23);
            Paragraph("A versão atual continuará no histórico. A reversão recupera os ajustes preservados para essa versão quando houver uma cópia compatível.");
            updateProgressLabel=Paragraph("A versão anterior está disponível neste computador.");
            AddRow("apply-rollback", "Voltar e reiniciar", "Retoma uma versão preservada neste computador", () => BeginUpdateCommit(async () => { var target=await VelopackInstallation.PrepareRollbackAsync(old.BuildId); await VelopackInstallation.ScheduleApplyAsync(target); }), !Controller.SettingsDirty && !diagnostic && !Controller.Platform.IsPreview);
        }
        else if (updateReview is {} candidate)
        {
            Paragraph("Atualizar para " + candidate.Version, 23);
            Paragraph("Instalada: " + AppBuild.Current.Display + "\nCommit da atualização: " + candidate.Commit[..7]);
            updateProgressLabel = Paragraph(preparedUpdate is null ? updateStatus : "Download verificado. A versão instalada ainda não foi alterada.");
            AutomationProperties.SetLiveSetting(updateProgressLabel, AutomationLiveSetting.Polite);
            if (preparedUpdate is null)
                AddRow("prepare-update", "Baixar atualização", "Você pode cancelar com B / Esc. A instalação atual será preservada.", () => UpdateTask = DownloadUpdateAsync(candidate), updateSource is not PreviewUpdateSource);
            else
                AddRow("apply-update", "Instalar e reiniciar", "Mantém a versão atual no histórico · atualização pelo Velopack", () =>
                {
                    var staged = preparedUpdate;
                    if (staged is null || staged.Build.BuildId != candidate.BuildId) return;
                    BeginUpdateCommit(() => VelopackInstallation.ScheduleApplyAsync(staged.Native??throw new InvalidDataException("Consulte e baixe novamente a versão escolhida.")));
                }, !Controller.SettingsDirty && !diagnostic && !Controller.Platform.IsPreview && VelopackInstallation.IsInstalled);
            if (!VelopackInstallation.IsInstalled) Paragraph("Use o instalador Setup para habilitar as atualizações neste computador.");
            if (candidate.Notes.Length > 0)
            {
                AddRow("update-notes", "Ler alterações", "A / Enter para ler · setas rolam · ← / → volta às ações", () => updateNotesText?.Focus());
                updateNotesText = new TextBox { Text = candidate.Notes, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 110, MaxHeight = 170, FontSize = 16, Padding = new(12), Margin = new(0,8,0,8) };
                updateNotesText.SetResourceReference(BackgroundProperty, ThemeSupport.Card); updateNotesText.SetResourceReference(ForegroundProperty, ThemeSupport.Text);
                AutomationProperties.SetName(updateNotesText, "Alterações da versão " + candidate.Version); AddVisual(updateNotesText);
            }
        }
        if (Controller.SettingsDirty) Paragraph("Salve ou descarte seus ajustes antes de reiniciar para trocar de versão.");
        AddRow("back", "Voltar", "Nenhuma instalação é iniciada ao sair desta página", Back);
    }

    private async Task CheckUpdatesAsync()
    {
        if (updateBusy || busy || diagnostic && updateSource is PreviewUpdateSource) return;
        var generation = ++updateGeneration;
        using var cancellation = new CancellationTokenSource(); updateCancellation = cancellation;
        busy = updateBusy = true; updateStatus = "Buscando versões no GitHub…"; SetUpdateStatus(updateStatus);
        try
        {
            var releases = await updateSource.CheckAsync(updateChannel, cancellation.Token);
            if (exiting || generation != updateGeneration) return;
            updateCandidates = releases;
            updateStatus = releases.Count == 0 ? "Nenhuma versão publicada neste canal. Builds só aparecem após passar nos testes." : releases[0].BuildId == AppBuild.Current.BuildId ? "Você está usando a versão mais recente deste canal." : "Há versões disponíveis. Escolha uma para ler as alterações e atualizar.";
            RequestCue(FeedbackCue.Completion);
        }
        catch (OperationCanceledException) { if (!exiting) updateStatus = "Busca cancelada. A versão instalada foi preservada."; }
        catch (Exception e) when (UpdateFailure(e)) { if (!exiting) ReportUpdateFailure("Não foi possível buscar atualizações.", e); }
        finally
        {
            if (ReferenceEquals(updateCancellation, cancellation)) { updateCancellation = null; busy = updateBusy = false; if(cancellation.IsCancellationRequested&&!exiting)updateStatus="Busca cancelada. A versão instalada foi preservada."; }
            if (!exiting) { UpdatePersistentState(); if (view.Current.Route == OverlayRoute.Updates) RefreshRows(); }
        }
    }

    private async Task DownloadUpdateAsync(UpdateCandidate candidate)
    {
        if (updateBusy || busy) return;
        var generation = ++updateGeneration;
        using var cancellation = new CancellationTokenSource(); updateCancellation = cancellation;
        busy = updateBusy = true; updateStatus = "Preparando download…"; SetUpdateStatus(updateStatus);
        try
        {
            var progress = new Progress<UpdateProgress>(value =>
            {
                if (exiting || generation != updateGeneration || updateCancellation != cancellation) return;
                var now = Environment.TickCount64;
                if (value.Total > 0 && now < nextUpdateProgress) return;
                nextUpdateProgress = now + 100;
                SetUpdateStatus(value.Phase + (value.Total > 0 ? " · " + Math.Clamp(value.Bytes * 100 / value.Total, 0, 100) + "%" : ""));
            });
            var prepared = await updateSource.StageAsync(candidate, progress, cancellation.Token);
            if (exiting || generation != updateGeneration || cancellation.IsCancellationRequested) { updateSource.Discard(prepared); return; }
            preparedUpdate = prepared; updateStatus = "Download verificado. Selecione Instalar e reiniciar para concluir.";
            navigation = new("apply-update"); RequestCue(FeedbackCue.Completion);
        }
        catch (OperationCanceledException) { if (!exiting) updateStatus = "Download cancelado. A versão instalada foi preservada."; }
        catch (Exception e) when (UpdateFailure(e)) { if (!exiting) ReportUpdateFailure("Não foi possível preparar a atualização.", e); }
        finally
        {
            if (ReferenceEquals(updateCancellation, cancellation)) { updateCancellation = null; busy = updateBusy = false; if(cancellation.IsCancellationRequested&&!exiting)updateStatus="Download cancelado. A versão instalada foi preservada."; }
            if (!exiting) { UpdatePersistentState(); if (view.Current.Route == OverlayRoute.UpdateReview) RefreshRows(); }
        }
    }

    private void BeginUpdateCommit(Func<Task> prepare)
    {
        if (Controller.SettingsDirty) { FeedbackText("Salve ou descarte seus ajustes antes de trocar de versão."); RequestCue(FeedbackCue.Unavailable); return; }
        QueueHandoff(() => UpdateTask = CommitUpdateAsync(prepare));
    }

    private async Task CommitUpdateAsync(Func<Task> prepare)
    {
        busy = updateBusy = updateCommitting = true;
        foreach(var button in new[]{HomeTab,AppsTab,SettingsTab,SettingsGear,BackButton})button.IsEnabled=false;
        SetUpdateStatus("Preparando reinício. Aguarde…");
        try { await prepare(); if (!exiting) { updateCloseAuthorized=true; Close(); } }
        catch (Exception e) when (UpdateFailure(e)) { if (!exiting) ReportUpdateFailure("Não foi possível reiniciar na versão escolhida. A instalação atual foi preservada.", e); }
        finally { busy = updateBusy = updateCommitting = false; foreach(var button in new[]{HomeTab,AppsTab,SettingsTab,SettingsGear,BackButton})button.IsEnabled=true; if (!exiting) { UpdatePersistentState(); RefreshRows(); } }
    }

    private void SetUpdateStatus(string message)
    {
        updateStatus = message;
        if (updateProgressLabel is not null) updateProgressLabel.Text = message;
        FeedbackText(message);
    }

    private bool CancelUpdateInteraction()
    {
        if (!updateBusy) return false;
        if (updateCommitting) { FeedbackText("A troca de versão está sendo preparada. Aguarde o reinício."); RequestCue(FeedbackCue.Unavailable); return true; }
        if (interfaceActionDepth==0) RequestCue(FeedbackCue.Cancellation); updateGeneration++; updateCancellation?.Cancel(); SetUpdateStatus("Cancelando. A versão instalada será preservada…"); return true;
    }

    private static bool UpdateFailure(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or HttpRequestException or JsonException or InvalidOperationException or ArgumentException or Win32Exception or TimeoutException or System.Security.SecurityException;
    private void ReportUpdateFailure(string message, Exception error)
    {
        var reason = error is UnauthorizedAccessException ? "Confira se o repositório está público e se o GitHub está disponível." : error is InvalidDataException or TimeoutException ? error.Message : "Confira a conexão, o espaço em disco e as permissões da pasta. Tente novamente.";
        updateStatus = message + " " + reason; SetUpdateStatus(updateStatus); RetainOperation("Não confirmado", updateStatus, "update"); RequestCue(FeedbackCue.Unavailable);
    }

    internal void ShowUpdateHistoryWarning(){updateStatus="Não foi possível preservar o pacote atual para reversão. Repare a instalação pelo Setup antes de atualizar.";}

    internal void ScheduleUpdateNotice()=>ShowUpdateRecoveryNotice();

    internal void ShowUpdateRecoveryNotice()
    {
        if(!VelopackInstallation.IsInstalled)return;
        if(VelopackInstallation.ReadNotice() is {} notice) {
            updateStatus=notice;
            if(Controller.Operation is null or {Action:"update"}) {
                RetainOperation(notice.StartsWith("Versão anterior restaurada",StringComparison.Ordinal)?"Concluído":"Não confirmado",notice,"update");
                FeedbackText(notice);
            }
        }
    }

    private void DisposeUpdates() { updateGeneration++; updateCancellation?.Cancel(); if(!updateCommitting&&preparedUpdate is {} unused)updateSource.Discard(unused); updateSource.Dispose(); }
}

internal sealed class PreviewUpdateSource : IUpdateSource
{
    public Task<IReadOnlyList<UpdateCandidate>> CheckAsync(string channel, CancellationToken cancellation) => Task.FromResult<IReadOnlyList<UpdateCandidate>>([]);
    public Task<PreparedUpdate> StageAsync(UpdateCandidate candidate, IProgress<UpdateProgress>? progress, CancellationToken cancellation) => Task.FromException<PreparedUpdate>(new InvalidOperationException("A prévia não instala atualizações."));
    public void Dispose() { }
}
