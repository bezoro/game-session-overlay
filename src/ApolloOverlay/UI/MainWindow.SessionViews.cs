using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace ApolloOverlay;

public partial class MainWindow
{
    private void BuildCurrentView()
    {
        controllerStatusParagraph = null;
        if (BuildSessionFeatureView())
            return;
        switch (view.Current.Route)
        {
            case OverlayRoute.Updates:
                BuildUpdates();
                break;
            case OverlayRoute.UpdateReview:
                BuildUpdateReview();
                break;
            case OverlayRoute.OperationResult:
                BuildResult();
                break;
            case OverlayRoute.WindowsAudio:
                BuildWindowsAudio();
                break;
            case OverlayRoute.TargetOptions:
                BuildOptions();
                break;
            case OverlayRoute.Details:
                BuildDetails();
                break;
            case OverlayRoute.Integrations:
                BuildIntegrations();
                break;
            case OverlayRoute.Customize:
                BuildCustomize();
                break;
            case OverlayRoute.Setup:
                BuildSetup();
                break;
            case OverlayRoute.PlayniteBrowser:
                BuildPlayniteBrowser();
                break;
            case OverlayRoute.Apps:
                BuildApps();
                break;
            case OverlayRoute.System:
                BuildSystem();
                break;
            default:
                BuildHub();
                break;
        }
    }

    private void BuildHub()
    {
        var target = Controller.Session.Anchor;
        var valid = target is not null && Controller.Platform.Valid(target);
        RegisterFixed("session-card", SessionCardButton, () =>
        {
            if (target is null || !valid)
                ChooseSession();
            else
                RunEffect(() => Controller.Switch(target), () => Controller.Platform.Valid(target));
        }, true, valid ? target!.Title : "Escolher uma janela em Aplicativos");
        RegisterFixed("session-options", SessionOptionsButton, () =>
        {
            if (target is not null)
                OpenOptions(target);
            else
                ChooseSession();
        }, true, "Opções da janela da sessão");
        AutomationProperties.SetName(SessionCardButton, valid ? target!.Title + " · Retomar janela" : "Escolher janela");
        AutomationProperties.SetHelpText(SessionCardButton, valid ? "A / Enter retoma esta janela. X abre opções." : "Escolher uma janela em Aplicativos");
        BuildSessionDashboard();
        var windowCount = Controller.Apps.Count;
        var windowSummary = windowCount switch
        {
            0 => "Nenhuma janela encontrada",
            1 => "1 janela disponível",
            _ => $"{windowCount} janelas disponíveis"
        };
        AddRow("apps", "Aplicativos abertos", windowSummary, () => Page(1));
        var favorites = SessionHubSettings.Favorites(Controller.Settings);
        if (favorites.Length > 0)
        {
            Section("Acesso rápido");
            var first = Rows.RowDefinitions.Count;
            for (var i = 0; i < favorites.Length; i++)
                BuildFavorite(favorites[i], narrow ? null : first + i / 2, i % 2);
        }

        var previous = Controller.Recent.Previous(Controller.Platform.Valid);
        var recents = Controller.Recent.Items.Where(w => !w.Same(Controller.Recent.OpeningForeground) && !w.Same(previous)).Take(4).ToArray();
        if (previous is not null || recents.Length > 0)
            Section("Recentes");
        if (previous is not null)
            AddRow("previous-window", "Janela anterior", previous.Title, () => RunEffect(() => Controller.Switch(previous), () => Controller.Platform.Valid(previous)));
        foreach (var recent in recents)
            AddWindowRow(recent, "recent:", false);
        if (!Controller.Settings.SetupSeen)
            AddRow("setup-invitation", "Personalizar painel", "Configuração rápida em três passos", () => OpenView(new(OverlayRoute.Setup, SelectedId: "setup-next")));
    }

    private void BuildFavorite(string id, int? row, int column)
    {
        var target = Controller.Session.Anchor;
        var steamContext = Controller.ReturnTarget;
        var menu = Controller.Platform.CanSteamMenu(steamContext);
        var steamGame = Controller.SteamOverlayEligible(steamContext);
        switch (id)
        {
            case "playnite":
                AddRow(id, "Playnite", Controller.Settings.PlaynitePath is null ? "Configure em Sistema" : "Abrir biblioteca", () => RunEffect((token, keep) => Controller.Launch(true, token, keep), focusTarget: Controller.LaunchTarget(true)), Controller.Settings.PlaynitePath is not null || Controller.Apps.Any(a => a.ProcessName.Equals("Playnite.FullscreenApp", StringComparison.OrdinalIgnoreCase)), row: row, column: column);
                break;
            case "steam":
                AddRow(id, "Steam Big Picture", Controller.SteamAvailable ? "Abrir biblioteca" : "Steam não encontrada", () => RunEffect((token, keep) => Controller.Launch(false, token, keep), focusTarget: Controller.LaunchTarget(false)), Controller.SteamAvailable, row: row, column: column);
                break;
            case "gamebar":
                AddRow(id, "Game Bar", target is null ? "Escolha uma janela" : "Xbox · janela da sessão", () => Shortcut("Win+G"), target is not null, row: row, column: column);
                break;
            case "steam-overlay":
                AddRow(id, menu ? "Menu do Steam Big Picture" : "Overlay Steam", menu ? "Big Picture" : steamGame ? Controller.HasSteamLaunchObservation(steamContext) ? "Iniciado pela Steam · " + Controller.Settings.SteamShortcut : "Solicitar " + Controller.Settings.SteamShortcut : "Não disponível nesta janela", () =>
                {
                    if (steamContext is null)
                        return;
                    if (menu)
                        RunEffect((token, keep) => Controller.Platform.SteamMenu(steamContext, token, keep), () => Controller.Platform.CanSteamMenu(steamContext), steamContext);
                    else
                        QueueSteam(steamContext, () => Controller.ReturnTarget?.Same(steamContext) == true && Controller.SteamOverlayEligible(steamContext));
                }, menu || steamGame, row: row, column: column);
                break;
            case "taskview":
                AddRow(id, "Visão de tarefas", "Janelas do Windows", () => RunEffect(Controller.Platform.TaskView), row: row, column: column);
                break;
            case "audio":
                AddRow(id, "Áudio do Windows", audio.Current.Available ? audio.Current.Muted ? "Saída sem som" : audio.Current.Volume.ToString("P0") : "Saída indisponível", () => Subpage("audio", "volume"), row: row, column: column);
                break;
        }
    }

    private void AddWindowRow(WindowRef target, string prefix, bool chooser)
    {
        var id = prefix + WindowId(target);
        var eligible = !chooser || !target.Protected && !target.Launcher;
        var button = AddRow(id, target.Title, eligible ? target.ProcessName + (chooser ? " · Usar nesta sessão" : " · Alternar janela") : "Esta janela não pode ser a referência da sessão", () =>
        {
            if (chooser)
            {
                if (!Controller.Platform.Valid(target))
                {
                    FeedbackText("A janela mudou; atualize a lista.");
                    return;
                }

                Controller.Select(target);
                Back();
                RefreshRows();
            }
            else
                RunEffect(() => Controller.Switch(target), () => Controller.Platform.Valid(target));
        }, eligible);
        var grid = (ActionButtonContent)button.Content;
        var texts = grid.Texts;
        texts.Margin = new(0, 0, 48, 0);
        if (Controller.Platform.Icon(target) is { } icon)
        {
            grid.ReplaceIcon(new Image { Source = icon, Width = 26, Height = 26, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center });
        }

        var more = new Button
        {
            Content = "···",
            Width = 44,
            MinHeight = 44,
            Padding = new(4),
            Margin = new(0, 8, 8, 8),
            HorizontalAlignment = HorizontalAlignment.Right,
            Tag = "options:" + id
        };
        AutomationProperties.SetName(more, "Opções de " + target.Title);
        AutomationProperties.SetHelpText(more, "Áudio do aplicativo, minimizar, detalhes e fechamento com confirmação");
        more.Click += (_, _) => ActivatePointerEntry(more);
        Grid.SetRow(more, Grid.GetRow(button));
        Grid.SetColumn(more, 1);
        Rows.Children.Add(more);
        RegisterFixed("options:" + id, more, () => OpenOptions(target), true, "Opções de " + target.Title);
    }

    private void BuildApps()
    {
        if (view.Current.ChooseSession)
            Paragraph("Escolha a janela que deseja retomar pelo painel.");
        foreach (var app in Controller.Apps)
            AddWindowRow(app, "", view.Current.ChooseSession);
        if (Controller.Apps.Count == 0)
            Paragraph("Nenhuma janela encontrada. Atualize a lista para procurar novamente.", 20);
        AddRow("refresh-apps", "Atualizar janelas", "Procurar novamente", async () =>
        {
            try
            {
                await Controller.Refresh();
                RefreshRows();
            }
            catch (OperationCanceledException)
            {
            }
        });
        if (view.Current.ChooseSession)
        {
            AddRow("clear-session", "Limpar janela da sessão", "Deixa o cartão sem uma janela selecionada", () =>
            {
                Controller.ClearSelection();
                Page(0);
                navigation = new("session-card");
                SetSelection(false);
            });
            AddRow("back", "Voltar", "Mantém a seleção anterior", Back);
        }
    }

    private void BuildOptions()
    {
        var target = view.Current.Target;
        if (target is null)
        {
            AddRow("choose-session", "Escolher janela", "Abrir Aplicativos", ChooseSession);
            return;
        }

        var valid = Controller.Platform.Valid(target);
        Paragraph(target.Title, 20);
        if (!valid)
            Paragraph("Esta janela não está mais disponível. Escolha outra janela.");
        AddRow("application-mute", MuteActionTitle(), valid ? AppAudioDescription() : "Esta janela não está mais disponível.", () => MuteApplication(target), valid && AudioFor(target) && appAudio.CanSetMute);
        AddRow("minimize", "Minimizar janela", valid && target.CanClose ? "O painel permanece aberto" : "Janela protegida ou indisponível", () => RunInPlace(target, token => Controller.Platform.Minimize(target, token), "minimize"), valid && target.CanClose);
        AddRow("choose-session", "Escolher outra janela", "Escolha o que deseja retomar", ChooseSession);
        AddRow("details", "Detalhes da janela", "Ver informações completas", () => OpenView(new(OverlayRoute.Details, target, "switch-detail")));
        AddRow("close", "Fechar janela…", target.CanClose && valid ? "Confirmar com nova pressão de 1,5 s" : "Janela protegida ou indisponível", () => Confirm(target), valid && target.CanClose, true);
        AddRow("back", "Voltar", "Voltar", Back);
    }

    private void BuildDetails()
    {
        var target = view.Current.Target;
        if (target is null)
        {
            AddRow("back", "Voltar", "Nenhuma janela selecionada", Back);
            return;
        }

        Paragraph(target.Title, 22);
        AddRow("window-information", "Título e informações completas", "Enter / A para ler · setas rolam · ← / → volta às ações", () => detailsText?.Focus());
        var information = $"{target.Title}\n\n{target.ProcessName} · PID {target.Pid}\nIdentidade de criação {target.Started} · geração {target.Generation}";
        if (AudioFor(target))
            information += $"\n\nÁudio do aplicativo · {AudioBadge()}\n{appAudio.IdentifiedSessions} sessões identificadas · {appAudio.SkippedOrFailedSessions} não verificadas\n{appAudio.Reason}";
        detailsText = new TextBox
        {
            Text = information,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MinHeight = 100,
            MaxHeight = 140,
            FontSize = 16,
            Padding = new(12),
            Margin = new(0, 8, 0, 12)
        };
        detailsText.SetResourceReference(BackgroundProperty, ThemeSupport.Card);
        detailsText.SetResourceReference(ForegroundProperty, ThemeSupport.Text);
        AutomationProperties.SetName(detailsText, "Título completo e identidade da janela");
        AutomationProperties.SetHelpText(detailsText, "Setas rolam. Direita retorna às ações; Esc volta às opções.");
        AddVisual(detailsText);
        AddRow("switch-detail", "Alternar para esta janela", "Retoma apenas esta identidade", () => RunEffect(() => Controller.Switch(target), () => Controller.Platform.Valid(target)), Controller.Platform.Valid(target));
        AddRow("select-session", "Usar nesta sessão", "Atualiza o cartão sem alternar o Windows", () =>
        {
            Controller.Select(target);
            Back();
            RefreshRows();
        }, !target.Launcher && !target.Protected && Controller.Platform.Valid(target));
        AddRow("close-detail", "Fechar esta janela…", "Nova pressão longa · 1,5 s", () => Confirm(target), target.CanClose && Controller.Platform.Valid(target), true);
        AddRow("back", "Voltar", "Retorna às opções anteriores", Back);
    }

    private void BuildWindowsAudio()
    {
        var state = audio.Current;
        Paragraph("Volume do Windows e controles de áudio da sessão.");
        AddRow("volume-down", "Diminuir volume", "− 5% · saída padrão do Windows", () => AdjustVolume(-.05f), state.Available);
        var volume = AddRow("volume", "Volume · " + (state.Available ? state.Volume.ToString("P0") : "indisponível"), "+ 5% · ← / → ajusta", () => AdjustVolume(.05f), state.Available);
        if (state.Available)
            AddSessionSlider(volume, state.Volume, value => AudioAction(audio.Set(value, expectedEndpoint: audio.Current.EndpointId)));
        AddRow("mute", state.Muted ? "Ativar som" : "Silenciar", "Silencia ou ativa a saída do Windows", () => AudioAction(audio.Set(muted: !audio.Current.Muted)), state.Available);
        AddRow("microphone", MicrophoneTitle, systemAudio.Current.MicrophoneName, ToggleMicrophone, systemAudio.Current.MicrophoneMuted is not null);
        AddRow("session-mixer", "Mixer de aplicativos", "Jogo, conversa e música separados", () => OpenFeature(OverlayRoute.Mixer, "mixer-refresh"));
        AddRow("audio-devices", "Saída de áudio", systemAudio.Current.OutputName, () => OpenFeature(OverlayRoute.AudioDevices, "device-refresh"));
        AddRow("back", "Voltar", state.Status, Back);
    }

    private void BuildResult()
    {
        if (Controller.Operation is not { } result)
        {
            AddRow("back", "Voltar", "Nenhum resultado retido", Back);
            return;
        }

        AddRow("operation-text", "Ler mensagem completa", "Setas para rolar · B / Esc para voltar", () => operationText?.Focus());
        operationText = new TextBox
        {
            Text = result.Message,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MinHeight = 120,
            MaxHeight = 160,
            FontSize = 16,
            Padding = new(12),
            Margin = new(0, 8, 0, 8)
        };
        operationText.SetResourceReference(BackgroundProperty, ThemeSupport.Card);
        operationText.SetResourceReference(ForegroundProperty, ThemeSupport.Text);
        AutomationProperties.SetName(operationText, "Resultado completo · " + result.State);
        AddVisual(operationText);
        AddRow("dismiss-operation", "Dispensar resultado", "Remove apenas esta mensagem", DismissOperation);
        AddRow("back", "Voltar ao painel", "Mantém o resultado disponível", Back);
    }
}
