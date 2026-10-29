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
        }, true, valid ? target!.Title : "Choose a window in Apps");
        RegisterFixed("session-options", SessionOptionsButton, () =>
        {
            if (target is not null)
                OpenOptions(target);
            else
                ChooseSession();
        }, true, "Session window options");
        AutomationProperties.SetName(SessionCardButton, valid ? target!.Title + " · Resume window" : "Choose window");
        AutomationProperties.SetHelpText(SessionCardButton, valid ? "A / Enter resumes this window. X opens options." : "Choose a window in Apps");
        var windowCount = Controller.Apps.Count;
        var windowSummary = windowCount switch
        {
            0 => "No windows found",
            1 => "1 window available",
            _ => $"{windowCount} windows available"
        };
        var favorites = SessionHubSettings.Favorites(Controller.Settings);
        if (favorites.Length > 0)
        {
            Section("Quick access");
            var first = Rows.RowDefinitions.Count;
            for (var i = 0; i < favorites.Length; i++)
                BuildFavorite(favorites[i], first + i / 2, i % 2);
        }

        AddRow("apps", "Open apps", windowSummary, () => Page(1));
        BuildSessionDashboard();

        var previous = Controller.Recent.Previous(Controller.Platform.Valid);
        var recents = Controller.Recent.Items.Where(w => !w.Same(Controller.Recent.OpeningForeground) && !w.Same(previous)).Take(4).ToArray();
        if (previous is not null || recents.Length > 0)
            Section("Recent");
        if (previous is not null)
            AddRow("previous-window", "Previous window", previous.Title, () => RunEffect(() => Controller.Switch(previous), () => Controller.Platform.Valid(previous)));
        foreach (var recent in recents)
            AddWindowRow(recent, "recent:", false);
        if (!Controller.Settings.SetupSeen)
            AddRow("setup-invitation", "Customize panel", "Quick setup in three steps", () => OpenView(new(OverlayRoute.Setup, SelectedId: "setup-next")));
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
                AddRow(id, "Playnite", Controller.Settings.PlaynitePath is null ? "Configure in System" : "Open library", () => RunEffect((token, keep) => Controller.Launch(true, token, keep), focusTarget: Controller.LaunchTarget(true)), Controller.Settings.PlaynitePath is not null || Controller.Apps.Any(a => a.ProcessName.Equals("Playnite.FullscreenApp", StringComparison.OrdinalIgnoreCase)), row: row, column: column, compactGrid: true);
                break;
            case "steam":
                AddRow(id, narrow ? "Big Picture" : "Steam Big Picture", Controller.SteamAvailable ? "Open library" : "Steam not found", () => RunEffect((token, keep) => Controller.Launch(false, token, keep), focusTarget: Controller.LaunchTarget(false)), Controller.SteamAvailable, row: row, column: column, compactGrid: true);
                break;
            case "gamebar":
                AddRow(id, "Game Bar", target is null ? "Choose a window" : "Xbox · session window", () => Shortcut("Win+G"), target is not null, row: row, column: column, compactGrid: true);
                break;
            case "steam-overlay":
                AddRow(id, menu ? "Steam Big Picture menu" : "Steam Overlay", menu ? "Big Picture" : steamGame ? Controller.HasSteamLaunchObservation(steamContext) ? "Launched by Steam · " + Controller.Settings.SteamShortcut : "Request " + Controller.Settings.SteamShortcut : "Unavailable in this window", () =>
                {
                    if (steamContext is null)
                        return;
                    if (menu)
                        RunEffect((token, keep) => Controller.Platform.SteamMenu(steamContext, token, keep), () => Controller.Platform.CanSteamMenu(steamContext), steamContext);
                    else
                        QueueSteam(steamContext, () => Controller.ReturnTarget?.Same(steamContext) == true && Controller.SteamOverlayEligible(steamContext));
                }, menu || steamGame, row: row, column: column, compactGrid: true);
                break;
            case "taskview":
                AddRow(id, "Task View", "Open windows", () => RunEffect(Controller.Platform.TaskView), row: row, column: column, compactGrid: true);
                break;
            case "audio":
                AddRow(id, narrow ? "Audio" : "Windows audio", audio.Current.Available ? audio.Current.Muted ? "Output muted" : audio.Current.Volume.ToString("P0") : "Output unavailable", () => Subpage("audio", "volume"), row: row, column: column, compactGrid: true);
                break;
        }
    }

    private void AddWindowRow(WindowRef target, string prefix, bool chooser)
    {
        var id = prefix + WindowId(target);
        var eligible = !chooser || !target.Protected && !target.Launcher;
        var button = AddRow(id, target.Title, eligible ? target.ProcessName + (chooser ? " · Use for this session" : " · Switch window") : "This window cannot be the session reference", () =>
        {
            if (chooser)
            {
                if (!Controller.Platform.Valid(target))
                {
                    FeedbackText("The window changed; refresh the list.");
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
        AutomationProperties.SetName(more, "Options for " + target.Title);
        AutomationProperties.SetHelpText(more, "App audio, minimize, details and confirmed closing");
        more.Click += (_, _) => ActivatePointerEntry(more);
        Grid.SetRow(more, Grid.GetRow(button));
        Grid.SetColumn(more, 1);
        Rows.Children.Add(more);
        RegisterFixed("options:" + id, more, () => OpenOptions(target), true, "Options for " + target.Title);
    }

    private void BuildApps()
    {
        if (view.Current.ChooseSession)
            Paragraph("Choose the window you want to resume from the panel.");
        foreach (var app in Controller.Apps)
            AddWindowRow(app, "", view.Current.ChooseSession);
        if (Controller.Apps.Count == 0)
            Paragraph("No windows found. Refresh the list to check again.", 20);
        AddRow("refresh-apps", "Refresh windows", "Check again", async () =>
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
            AddRow("clear-session", "Clear session window", "Clears the window selected on the session card", () =>
            {
                Controller.ClearSelection();
                Page(0);
                navigation = new("session-card");
                SetSelection(false);
            });
            AddRow("back", "Back", "Keeps the previous selection", Back);
        }
    }

    private void BuildOptions()
    {
        var target = view.Current.Target;
        if (target is null)
        {
            AddRow("choose-session", "Choose window", "Open Apps", ChooseSession);
            return;
        }

        var valid = Controller.Platform.Valid(target);
        Paragraph(target.Title, 20);
        if (!valid)
            Paragraph("This window is no longer available. Choose another window.");
        AddRow("application-mute", MuteActionTitle(), valid ? AppAudioDescription() : "This window is no longer available.", () => MuteApplication(target), valid && AudioFor(target) && appAudio.CanSetMute);
        AddRow("minimize", "Minimize window", valid && target.CanClose ? "The panel stays open" : "Window protected or unavailable", () => RunInPlace(target, token => Controller.Platform.Minimize(target, token), "minimize"), valid && target.CanClose);
        AddRow("choose-session", "Choose another window", "Choose what to resume", ChooseSession);
        AddRow("details", "Window details", "View full details", () => OpenView(new(OverlayRoute.Details, target, "switch-detail")));
        AddRow("close", "Close window…", target.CanClose && valid ? "Confirm with a fresh 1.5-second hold" : "Window protected or unavailable", () => Confirm(target), valid && target.CanClose, true);
        AddRow("back", "Back", "Back", Back);
    }

    private void BuildDetails()
    {
        var target = view.Current.Target;
        if (target is null)
        {
            AddRow("back", "Back", "No window selected", Back);
            return;
        }

        Paragraph(target.Title, 22);
        AddRow("window-information", "Full title and details", "Enter / A to read · arrows scroll · ← / → returns to actions", () => detailsText?.Focus());
        var information = $"{target.Title}\n\n{target.ProcessName} · PID {target.Pid}\nCreation identity {target.Started} · generation {target.Generation}";
        if (AudioFor(target))
            information += $"\n\nApp audio · {AudioBadge()}\n{appAudio.IdentifiedSessions} sessions identified · {appAudio.SkippedOrFailedSessions} unverified\n{appAudio.Reason}";
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
        AutomationProperties.SetName(detailsText, "Full window title and identity");
        AutomationProperties.SetHelpText(detailsText, "Arrows scroll. Right returns to actions; Esc returns to options.");
        AddVisual(detailsText);
        AddRow("switch-detail", "Switch to this window", "Resumes only this exact window", () => RunEffect(() => Controller.Switch(target), () => Controller.Platform.Valid(target)), Controller.Platform.Valid(target));
        AddRow("select-session", "Use for this session", "Updates the card without switching Windows focus", () =>
        {
            Controller.Select(target);
            Back();
            RefreshRows();
        }, !target.Launcher && !target.Protected && Controller.Platform.Valid(target));
        AddRow("close-detail", "Close this window…", "Fresh long press · 1.5 s", () => Confirm(target), target.CanClose && Controller.Platform.Valid(target), true);
        AddRow("back", "Back", "Returns to the previous options", Back);
    }

    private void BuildWindowsAudio()
    {
        var state = audio.Current;
        Paragraph("Windows volume and session audio controls.");
        AddRow("volume-down", "Lower volume", "− 5% · default Windows output", () => AdjustVolume(-.05f), state.Available);
        var volume = AddRow("volume", "Volume · " + (state.Available ? state.Volume.ToString("P0") : "unavailable"), "+ 5% · ← / → adjusts", () => AdjustVolume(.05f), state.Available);
        if (state.Available)
            AddSessionSlider(volume, state.Volume, value => AudioAction(audio.Set(value, expectedEndpoint: audio.Current.EndpointId)));
        AddRow("mute", state.Muted ? "Unmute" : "Mute", "Mutes or unmutes the Windows output", () => AudioAction(audio.Set(muted: !audio.Current.Muted)), state.Available);
        AddRow("microphone", MicrophoneTitle, systemAudio.Current.MicrophoneName, ToggleMicrophone, systemAudio.Current.MicrophoneMuted is not null);
        AddRow("session-mixer", "App mixer", "Separate game, voice and music audio", () => OpenFeature(OverlayRoute.Mixer, "mixer-refresh"));
        AddRow("audio-devices", "Audio output", systemAudio.Current.OutputName, () => OpenFeature(OverlayRoute.AudioDevices, "device-refresh"));
        AddRow("back", "Back", state.Status, Back);
    }

    private void BuildResult()
    {
        if (Controller.Operation is not { } result)
        {
            AddRow("back", "Back", "No retained result", Back);
            return;
        }

        AddRow("operation-text", "Read full message", "Arrows to scroll · B / Esc to go back", () => operationText?.Focus());
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
        AutomationProperties.SetName(operationText, "Full result · " + result.State);
        AddVisual(operationText);
        AddRow("dismiss-operation", "Dismiss result", "Removes only this message", DismissOperation);
        AddRow("back", "Back to panel", "Keeps the result available", Back);
    }
}
