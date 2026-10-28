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
    private DispatcherTimer? updateNoticeTimer;
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
        if (!diagnostic && !Controller.Platform.IsPreview)
        {
            AddRow("connect-updates", updateSource.HasCredential ? "Reconectar GitHub" : "Conectar GitHub",
                "Repositório privado · conexão inicial com acesso de leitura", ConnectUpdates);
            if (updateSource.HasCredential) AddRow("disconnect-updates", "Desconectar GitHub", "Remove a credencial deste usuário do Windows", () =>
            {
                try { updateSource.Disconnect(); updateCandidates = []; updateStatus = "GitHub desconectado. A versão instalada e o histórico foram mantidos."; RefreshRows(); }
                catch (Exception e) when (UpdateFailure(e)) { ReportUpdateFailure("Não foi possível remover a credencial.", e); }
            });
        }
        if (!ManagedInstall.IsManagedChild)
            AddRow("install-overlay", "Instalar neste computador", "Pasta permanente, atalho e inicialização oculta · preserva seus ajustes", PrepareCurrentInstall, !diagnostic && !Controller.Platform.IsPreview);
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
            var history = (diagnostic || Controller.Platform.IsPreview ? Array.Empty<InstalledBuild>() : ManagedInstall.History()).Where(h => !h.Current && !h.LastStartFailed).OrderByDescending(h => h.InstalledAt).ToArray();
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
            AddRow("apply-rollback", "Voltar e reiniciar", "Retoma uma versão preservada neste computador", () => BeginUpdateCommit(() => ManagedInstall.PrepareRollback(old.BuildId)), !Controller.SettingsDirty && !diagnostic && !Controller.Platform.IsPreview);
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
                AddRow("apply-update", "Instalar e reiniciar", "Mantém a versão atual no histórico · testa a inicialização da nova versão", () =>
                {
                    var staged = preparedUpdate;
                    if (staged is null || staged.Build.BuildId != candidate.BuildId) return;
                    BeginUpdateCommit(() => ManagedInstall.PrepareTransition(staged.Directory, staged.Build.BuildId, staged.Build.Version, staged.Build.Commit));
                }, !Controller.SettingsDirty && !diagnostic && !Controller.Platform.IsPreview && ManagedInstall.IsManagedChild);
            if (!ManagedInstall.IsManagedChild) Paragraph("Instale esta versão pelo botão Instalar neste computador antes de aplicar atualizações.");
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

    private void BeginUpdateCommit(Action prepare)
    {
        if (Controller.SettingsDirty) { FeedbackText("Salve ou descarte seus ajustes antes de trocar de versão."); RequestCue(FeedbackCue.Unavailable); return; }
        QueueHandoff(() => UpdateTask = CommitUpdateAsync(prepare));
    }

    private void PrepareCurrentInstall() => BeginUpdateCommit(ManagedInstall.PrepareInstallCurrent);

    private async Task CommitUpdateAsync(Action prepare)
    {
        busy = updateBusy = updateCommitting = true;
        foreach(var button in new[]{HomeTab,AppsTab,SettingsTab,SettingsGear,BackButton})button.IsEnabled=false;
        SetUpdateStatus("Preparando reinício. Aguarde…");
        try { await Task.Run(prepare); if (!exiting) { updateCloseAuthorized=true; Close(); } }
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
        var reason = error is UnauthorizedAccessException ? "Conecte o GitHub com acesso de leitura ao repositório privado." : error is InvalidDataException or TimeoutException ? error.Message : "Confira a conexão, o espaço em disco e as permissões da pasta. Tente novamente.";
        updateStatus = message + " " + reason; SetUpdateStatus(updateStatus); RetainOperation("Não confirmado", updateStatus, "update"); RequestCue(FeedbackCue.Unavailable);
    }

    private async void ConnectUpdates()
    {
        if (ownedDialog || busy || exiting) return;
        ownedDialog = true; feedbackService.Suspend(); input.Cancel();
        string? token = null;
        try
        {
            var dialog = new Window { Title = "Conectar GitHub", Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, Width = 500, SizeToContent = SizeToContent.Height, MaxHeight = SystemParameters.WorkArea.Height, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true, FontFamily = FontFamily, FontSize = 16 };
            dialog.SetResourceReference(BackgroundProperty, ThemeSupport.Surface); dialog.SetResourceReference(ForegroundProperty, ThemeSupport.Text);
            var panel = new StackPanel { Margin = new(24) };
            panel.Children.Add(new TextBlock { Text = "Conectar ao repositório privado", FontSize = 23, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = "Esta conexão inicial requer teclado. Cole um token do GitHub com acesso apenas a bezoro/apollo-tv-overlay e Contents em modo Read-only. A credencial fica protegida para seu usuário do Windows. As próximas atualizações podem ser feitas pelo controle.", TextWrapping = TextWrapping.Wrap, Margin = new(0,12,0,16) });
            var password = new PasswordBox { MaxLength = 255, MinHeight = 42, Padding = new(8) };
            AutomationProperties.SetName(password, "Token do GitHub com leitura do repositório"); panel.Children.Add(password);
            var connect = new Button { Content = "Conectar", MinHeight = 44, Margin = new(0,16,0,8), IsDefault = true };
            var cancel = new Button { Content = "Cancelar", MinHeight = 44, IsCancel = true };
            connect.Click += (_, _) => { if (password.Password.Length == 0) return; token = password.Password; password.Clear(); dialog.DialogResult = true; };
            panel.Children.Add(connect); panel.Children.Add(cancel); dialog.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            dialog.ContentRendered += (_, _) => password.Focus();
            if (dialog.ShowDialog() != true || token is null) return;
            SetUpdateStatus("Verificando acesso ao GitHub…");
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await updateSource.ConnectAsync(token, cancellation.Token);
            updateStatus = "GitHub conectado. Use Buscar atualizações para consultar as versões.";
        }
        catch (OperationCanceledException) { updateStatus = "A conexão com o GitHub não foi concluída. Tente novamente."; }
        catch (Exception e) when (UpdateFailure(e)) { ReportUpdateFailure("Não foi possível conectar o GitHub.", e); }
        finally
        {
            token = null; ownedDialog = false;
            if (!exiting) { RevokeFocus("update-account-return"); var cycle = focusCycle; var focus = AcquireFocus(cycle); OpeningTask = focus; if (await focus && CurrentCycle(cycle)) { RefreshRows(); SetSelection(false); } }
        }
    }

    internal void ScheduleUpdateNotice()
    {
        if(!ManagedInstall.IsManagedChild)return;
        ShowUpdateRecoveryNotice();
        updateNoticeTimer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(3)};
        updateNoticeTimer.Tick+=(_,_)=>{updateNoticeTimer.Stop();if(!exiting)ShowUpdateRecoveryNotice();};
        updateNoticeTimer.Start();
    }

    internal void ShowUpdateRecoveryNotice()
    {
        if(!ManagedInstall.IsManagedChild)return;
        try { if (ManagedInstall.ReadNotice() is {} notice) {
            updateStatus=notice;
            if(Controller.Operation is null or {Action:"update"}) {
                var completed=notice.StartsWith("Atualização concluída",StringComparison.Ordinal)||notice.StartsWith("Instalação concluída",StringComparison.Ordinal)||notice.StartsWith("Versão anterior restaurada. Seus ajustes",StringComparison.Ordinal);
                RetainOperation(completed?"Concluído":"Não confirmado", notice, "update"); FeedbackText(notice);
            }
        } }
        catch (Exception e) when (UpdateFailure(e)) { }
    }

    private void DisposeUpdates() { updateNoticeTimer?.Stop(); updateGeneration++; updateCancellation?.Cancel(); if(!updateCommitting&&preparedUpdate is {} unused)updateSource.Discard(unused); updateSource.Dispose(); }
}

internal sealed class PreviewUpdateSource : IUpdateSource
{
    public bool HasCredential => false;
    public Task ConnectAsync(string token, CancellationToken cancellation) => Task.FromException(new InvalidOperationException("Conexão indisponível na prévia."));
    public void Disconnect() { }
    public Task<IReadOnlyList<UpdateCandidate>> CheckAsync(string channel, CancellationToken cancellation) => Task.FromResult<IReadOnlyList<UpdateCandidate>>([]);
    public Task<PreparedUpdate> StageAsync(UpdateCandidate candidate, IProgress<UpdateProgress>? progress, CancellationToken cancellation) => Task.FromException<PreparedUpdate>(new InvalidOperationException("A prévia não instala atualizações."));
    public void Dispose() { }
}
