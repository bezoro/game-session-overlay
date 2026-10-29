using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace ApolloOverlay;

public partial class MainWindow
{
    private void BuildIntegrations()
    {
        AddRow("test-steam", "Solicitar overlay Steam", (Controller.HasSteamLaunchObservation(Controller.Session.Anchor) && Controller.SteamEvidence != ModuleEvidence.Loaded ? "Iniciado pela Steam · " : "") + SteamPermission.Reason(Controller.SteamEvidence), () => Shortcut(Controller.Settings.SteamShortcut), Controller.SteamOverlayEligible(Controller.Session.Anchor));
        AddGuideToggle();
        AddRow("external-close", "Fechar overlays externos", "Indisponível: visibilidade externa não verificada", () =>
        {
        }, false);
        AddRow("back", "Voltar", "Duplo clique na bandeja abre o painel", Back);
    }

    private void BuildSystem()
    {
        Section("Aplicativo");
        AddRow("updates", "Versão e atualizações", AppBuild.Current.Display + " · atualizar ou voltar de versão", OpenUpdates);
        Section("Exibição e aparência");
        AddDisplay();
        AddRow("side", "Lado · " + (Controller.Settings.PanelSide == "Right" ? "Direito" : "Esquerdo"), "Escolha o lado do painel", () => Change(Controller.Settings with { PanelSide = Controller.Settings.PanelSide == "Left" ? "Right" : "Left" }));
        AddScale();
        Section("Controle");
        controllerStatusParagraph = Paragraph(controllerStatus);
        AddRow("deadzone", "Deadzone · " + Controller.Settings.Deadzone, "Ajustar sensibilidade do analógico", () => Change(Controller.Settings with { Deadzone = Controller.Settings.Deadzone < 12000 ? 12000 : Controller.Settings.Deadzone < 18000 ? 18000 : Controller.Settings.Deadzone < 24000 ? 24000 : 12000 }));
        AddGuideToggle();
        AddGuideLimit();
        Section("Do seu jeito");
        AddRow("customize", "Personalizar acesso rápido", $"{SessionHubSettings.Favorites(Controller.Settings).Length} ações visíveis · ordem e visibilidade", () => OpenView(new(OverlayRoute.Customize, SelectedId: "favorite:playnite")));
        AddFeedback();
        AddRow("setup", "Guia de configuração", "Três passos opcionais · pode sair a qualquer momento", () => OpenView(new(OverlayRoute.Setup, SelectedId: "setup-next")));
        Section("Integrações");
        AddRow("configure-playnite", "Caminho do Playnite", Controller.Settings.PlaynitePath is null ? "Escolher executável…" : "Instalação configurada", ConfigurePlaynite);
        AddRow("shortcut", "Atalho Steam · " + Controller.Settings.SteamShortcut, "A alterna Shift+Tab / Ctrl+Tab", () => Change(Controller.Settings with { SteamShortcut = Controller.Settings.SteamShortcut == "Shift+Tab" ? "Ctrl+Tab" : "Shift+Tab" }));
        AddRow("integrations", "Integrações e limites", "Guide, Steam e funções externas", () => Subpage("integrations", "test-steam"));
        AddRow("save", "Salvar ajustes", "Salvar neste computador", Save);
        AddRow("exit", "Encerrar overlay", Controller.SettingsDirty ? "Alterações não salvas serão perdidas" : "Encerra observadores e remove o ícone da bandeja", () => QueueHandoff(Close));
    }

    private void AddDisplay() => AddRow("display", "Monitor · " + (Controller.Settings.DisplayId ?? "janela ativa"), "Automático ou escolha uma tela", CycleDisplay);
    private void AddScale() => AddRow("ui-scale", "Escala da interface · " + Controller.Settings.UiScalePercent + "%", "100 / 115 / 130 / 150% · texto maior, conteúdo com rolagem", () =>
    {
        var scales = new[]
        {
            100,
            115,
            130,
            150
        };
        Change(Controller.Settings with
        {
            UiScalePercent = scales[(Array.IndexOf(scales, Controller.Settings.UiScalePercent) + 1) % scales.Length]
        });
    });
    private void AddGuideToggle() => AddRow("guide", "Botão Xbox / Guide · " + (Controller.Settings.GuideXInputEnabled ? "ligado" : "desligado"), GuideDescription(), () => Change(Controller.Settings with { GuideXInputEnabled = !Controller.Settings.GuideXInputEnabled }), !diagnostic && !Controller.Platform.IsPreview);
    private void AddGuideLimit()
    {
        var button = AddRow("guide-limit", GuideLimitTitle(), "← / → ajusta · 0,5–3,0 s · Salvar para manter", () =>
        {
        });
        button.MinHeight = 112;
        var texts = ((ActionButtonContent)button.Content).Texts;
        var slider = new Slider
        {
            Minimum = 500,
            Maximum = 3000,
            TickFrequency = 100,
            SmallChange = 100,
            LargeChange = 100,
            IsSnapToTickEnabled = true,
            Focusable = true,
            Value = Controller.Settings.GuideOpenMaxHoldMs,
            Margin = new(0, 9, 0, 0)
        };
        AutomationProperties.SetName(slider, "Limite de pressão do Guide para abrir, em milissegundos");
        slider.PreviewKeyDown += (_, e) =>
        {
            var delta = e.Key switch
            {
                System.Windows.Input.Key.Left or System.Windows.Input.Key.Down => -100,
                System.Windows.Input.Key.Right or System.Windows.Input.Key.Up => 100,
                System.Windows.Input.Key.PageDown => -500,
                System.Windows.Input.Key.PageUp => 500,
                _ => 0
            };
            if (delta != 0)
            {
                slider.Value = Math.Clamp(slider.Value + delta, 500, 3000);
                e.Handled = true;
            }
            else if (e.Key is System.Windows.Input.Key.Home or System.Windows.Input.Key.End)
            {
                slider.Value = e.Key == System.Windows.Input.Key.Home ? 500 : 3000;
                e.Handled = true;
            }
        };
        slider.ValueChanged += (_, _) =>
        {
            Controller.Change(Controller.Settings with
            {
                GuideOpenMaxHoldMs = (int)Math.Round(slider.Value / 100) * 100
            });
            SetEntryText("guide-limit", GuideLimitTitle());
            UpdatePersistentState();
            FeedbackText("Alteração em memória · Salvar ajustes para manter.");
        };
        texts.Children.Add(slider);
    }

    private void AddFeedback()
    {
        AddRow("sound-feedback", "Sons · " + (Controller.Settings.SoundFeedbackEnabled ? "ligados" : "desligados"), "Navegar, confirmar e voltar · prévia ao ligar", () => Change(Controller.Settings with { SoundFeedbackEnabled = !Controller.Settings.SoundFeedbackEnabled }));
        AddRow("haptic-feedback", "Vibração · " + (Controller.Settings.HapticFeedbackEnabled ? "ligada" : "desligada"), "Somente o controle ativo · pulsos curtos", () => Change(Controller.Settings with { HapticFeedbackEnabled = !Controller.Settings.HapticFeedbackEnabled }));
        AddRow("test-feedback", "Testar retorno", FeedbackDescription(), TestFeedback, Controller.Settings.SoundFeedbackEnabled || Controller.Settings.HapticFeedbackEnabled);
    }

    private void BuildCustomize()
    {
        Paragraph("Escolha suas ações e use as setas para mudar a ordem.");
        var visible = SessionHubSettings.Favorites(Controller.Settings);
        var ordered = visible.Concat(SessionHubSettings.Catalog.Where(id => !visible.Contains(id))).ToArray();
        foreach (var id in ordered)
        {
            var name = FavoriteName(id);
            var shown = visible.Contains(id);
            var button = AddRow("favorite:" + id, name, shown ? "Visível · selecionar para ocultar" : "Oculto · selecionar para mostrar", () => Change(SessionHubSettings.Toggle(Controller.Settings, id)));
            button.Margin = new(0, 4, 104, 4);
            var row = Grid.GetRow(button);
            AddReorder(id, name, row, -1, shown && Array.IndexOf(visible, id) > 0);
            AddReorder(id, name, row, 1, shown && Array.IndexOf(visible, id) < visible.Length - 1);
        }

        AddRow("restore-favorites", "Restaurar ordem padrão", "Mostra as seis ações originais", () => Change(Controller.Settings with { FavoriteQuickActions = null }));
        AddRow("back", "Voltar", "Alterações continuam pendentes até Salvar", Back);
    }

    private void AddReorder(string id, string name, int row, int delta, bool available)
    {
        var entryId = (delta < 0 ? "earlier:" : "later:") + id;
        var label = "Mover " + name + (delta < 0 ? " antes" : " depois");
        var button = new Button
        {
            Content = delta < 0 ? "↑" : "↓",
            FontSize = 21,
            Width = 44,
            Height = 44,
            MinHeight = 44,
            Padding = new(4),
            Margin = new(0),
            IsEnabled = available,
            Tag = entryId
        };
        AutomationProperties.SetName(button, label);
        AutomationProperties.SetHelpText(button, available ? "Muda somente a ordem das ações visíveis" : "Sem posição disponível nesta direção");
        var holder = new Border
        {
            Child = button,
            Width = 44,
            Height = 44,
            Focusable = true,
            Tag = "description:" + entryId,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new(0, 0, delta < 0 ? 54 : 4, 0)
        };
        AutomationProperties.SetName(holder, label + ". Sem posição disponível nesta direção");
        button.Resources["DescriptionTarget"] = holder;
        void Move()
        {
            navigation = new("favorite:" + id);
            Change(SessionHubSettings.Move(Controller.Settings, id, delta));
        }

        button.Click += (_, _) => ActivatePointerEntry(button);
        button.GotKeyboardFocus += (_, _) => navigation = new(entryId);
        Grid.SetRow(holder, row);
        Grid.SetColumn(holder, 1);
        Rows.Children.Add(holder);
        RegisterFixed(entryId, button, Move, available, AutomationProperties.GetHelpText(button));
    }

    private static string FavoriteName(string id) => id switch
    {
        "playnite" => "Playnite",
        "steam" => "Steam Big Picture",
        "gamebar" => "Game Bar",
        "steam-overlay" => "Overlay Steam",
        "taskview" => "Visão de tarefas",
        _ => "Áudio do Windows"
    };
    private void BuildSetup()
    {
        var step = view.Current.SetupStep;
        Section($"CONFIGURAÇÃO · {step + 1} DE 3");
        if (step == 0)
        {
            Paragraph("Seu painel, sempre à mão", 23);
            controllerStatusParagraph = Paragraph(controllerStatus);
            Paragraph("Pressione e solte o botão Xbox / Guide para abrir. A leitura depende de um controle compatível. Também é possível abrir pela bandeja.");
            AddGuideToggle();
        }
        else if (step == 1)
        {
            Paragraph("Conforto na sua tela", 23);
            Paragraph("Escolha o monitor, o lado e o tamanho. A prévia muda agora; salvar continua sendo sua escolha.");
            AddDisplay();
            AddRow("side", "Lado · " + Controller.Settings.PanelSide, "Esquerdo / Direito", () => Change(Controller.Settings with { PanelSide = Controller.Settings.PanelSide == "Left" ? "Right" : "Left" }));
            AddScale();
        }
        else
        {
            Paragraph("Bibliotecas e retorno", 23);
            AddRow("configure-playnite", "Configurar Playnite", Controller.Settings.PlaynitePath ?? "Escolher executável", ConfigurePlaynite);
            AddFeedback();
        }

        if (step > 0)
            AddRow("setup-back", "Passo anterior", "Preserva seus ajustes", () => SetSetupStep(step - 1));
        AddRow("setup-next", step < 2 ? "Continuar" : "Concluir", step < 2 ? "Próximo passo" : "Marca o guia como visto; Salvar mantém os ajustes", () =>
        {
            if (step < 2)
                SetSetupStep(step + 1);
            else
                FinishSetup();
        });
        AddRow("setup-skip", "Pular guia", "Volta ao painel; nada é salvo automaticamente", FinishSetup);
    }

    private void SetSetupStep(int step)
    {
        view.Remember(navigation.Id, ContentScroll.VerticalOffset);
        view.Replace(view.Current with
        {
            SetupStep = step,
            SelectedId = "setup-next",
            ScrollOffset = 0
        });
        navigation = new("setup-next");
        RefreshRows();
        ContentScroll.ScrollToTop();
    }

    private void FinishSetup()
    {
        Controller.Change(Controller.Settings with
        {
            SetupSeen = true
        });
        Back();
        RefreshRows();
    }
}
