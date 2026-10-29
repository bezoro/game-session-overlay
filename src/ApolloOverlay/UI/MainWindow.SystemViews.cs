using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace ApolloOverlay;

public partial class MainWindow
{
    private void BuildIntegrations()
    {
        AddRow("test-steam", "Request Steam Overlay", (Controller.HasSteamLaunchObservation(Controller.Session.Anchor) && Controller.SteamEvidence != ModuleEvidence.Loaded ? "Launched by Steam · " : "") + SteamPermission.Reason(Controller.SteamEvidence), () => Shortcut(Controller.Settings.SteamShortcut), Controller.SteamOverlayEligible(Controller.Session.Anchor));
        AddGuideToggle();
        AddRow("external-close", "Close external overlays", "Unavailable: external overlay visibility is unverified", () =>
        {
        }, false);
        AddRow("back", "Back", "Double-click the tray icon to open the panel", Back);
    }

    private void BuildSystem()
    {
        Section("Application");
        AddRow("updates", "Version and updates", AppBuild.Current.Display + " · update or restore an earlier version", OpenUpdates);
        AddStartup();
        if (Controller.Operation is { } result)
            AddRow("last-operation", "Last result", result.State + " · " + result.Message, OpenOperation);
        Section("Display and appearance");
        AddDisplay();
        AddRow("side", "Side · " + (Controller.Settings.PanelSide == "Right" ? "Right" : "Left"), "Choose which side to use", () => Change(Controller.Settings with { PanelSide = Controller.Settings.PanelSide == "Left" ? "Right" : "Left" }));
        AddScale();
        AddRow("appearance", "Appearance", "Theme presets and every overlay color", OpenAppearance);
        Section("Controller");
        controllerStatusParagraph = Paragraph(controllerStatus);
        AddRow("deadzone", "Deadzone · " + (Controller.Settings.Deadzone * 100d / short.MaxValue).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%", "Ignore movement near the stick center", () => Change(Controller.Settings with { Deadzone = Controller.Settings.Deadzone < 12000 ? 12000 : Controller.Settings.Deadzone < 18000 ? 18000 : Controller.Settings.Deadzone < 24000 ? 24000 : 12000 }));
        AddGuideToggle();
        AddGuideLimit();
        Section("Feedback");
        AddFeedback();
        Section("Your panel");
        AddRow("customize", "Customize quick access", $"{SessionHubSettings.Favorites(Controller.Settings).Length} visible actions · order and visibility", () => OpenView(new(OverlayRoute.Customize, SelectedId: "favorite:playnite")));
        AddRow("setup", "Setup guide", "Three optional steps · leave at any time", () => OpenView(new(OverlayRoute.Setup, SelectedId: "setup-next")));
        Section("Integrations");
        AddRow("configure-playnite", "Playnite location", Controller.Settings.PlaynitePath is null ? "Choose executable…" : "Installation configured", ConfigurePlaynite);
        AddRow("shortcut", "Steam shortcut · " + Controller.Settings.SteamShortcut, "A switches Shift+Tab / Ctrl+Tab", () => Change(Controller.Settings with { SteamShortcut = Controller.Settings.SteamShortcut == "Shift+Tab" ? "Ctrl+Tab" : "Shift+Tab" }));
        AddRow("integrations", "Integrations and limits", "Guide, Steam and external features", () => Subpage("integrations", "test-steam"));
        Section("Settings and background services");
        AddRow("save", "Save settings", "Keep your changes on this computer", Save);
        AddRow("exit", "Quit overlay", Controller.SettingsDirty ? "Unsaved changes will be lost" : "Stops background services and removes the tray icon", () => QueueHandoff(Close));
    }

    private void AddDisplay() => AddRow("display", "Display · " + (Controller.Settings.DisplayId ?? "active window"), "Automatic or choose a display", CycleDisplay);
    private void AddScale() => AddRow("ui-scale", Controller.Settings.UiScalePercent == 100 ? "Size · automatic" : "Size · +" + (Controller.Settings.UiScalePercent - 100) + "%", automaticScaleDescription, () =>
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
    private void AddGuideToggle()
    {
        var button = AddRow("guide", "Xbox / Guide button · " + (Controller.Settings.GuideXInputEnabled ? "On" : "Off"), GuideDescription(), () => Change(Controller.Settings with { GuideXInputEnabled = !Controller.Settings.GuideXInputEnabled }), !diagnostic && !Controller.Platform.IsPreview);
        AddToggleIndicator(button, Controller.Settings.GuideXInputEnabled);
    }
    private void AddGuideLimit()
    {
        var button = AddRow("guide-limit", GuideLimitTitle(), "← / → adjusts · 0.5–3.0 s", () =>
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
            Style = (Style)FindResource("SessionVolumeSlider"),
            Value = Controller.Settings.GuideOpenMaxHoldMs,
            Margin = new(0, 9, 0, 0)
        };
        AutomationProperties.SetName(slider, "Guide opening hold limit, in milliseconds");
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
            FeedbackText("Unsaved change · Save settings to keep it.");
        };
        texts.Children.Add(slider);
    }

    private void AddFeedback()
    {
        var sound = AddRow("sound-feedback", "Sounds · " + (Controller.Settings.SoundFeedbackEnabled ? "On" : "Off"), "Quiet navigation, confirmation and back cues", () => Change(Controller.Settings with { SoundFeedbackEnabled = !Controller.Settings.SoundFeedbackEnabled }));
        AddToggleIndicator(sound, Controller.Settings.SoundFeedbackEnabled);
        var haptic = AddRow("haptic-feedback", "Vibration · " + (Controller.Settings.HapticFeedbackEnabled ? "On" : "Off"), "Active controller only · short pulses", () => Change(Controller.Settings with { HapticFeedbackEnabled = !Controller.Settings.HapticFeedbackEnabled }));
        AddToggleIndicator(haptic, Controller.Settings.HapticFeedbackEnabled);
        AddRow("test-feedback", "Test feedback", FeedbackDescription(), TestFeedback, Controller.Settings.SoundFeedbackEnabled || Controller.Settings.HapticFeedbackEnabled);
    }

    private void AddToggleIndicator(Button button, bool enabled)
    {
        if (button.Content is not ActionButtonContent content)
            return;
        var indicator = new Border
        {
            Width = 36,
            Height = 20,
            CornerRadius = new(10),
            Margin = new(8, 0, 0, 0),
            IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Center
        };
        indicator.SetResourceReference(Border.BackgroundProperty, enabled ? ThemeSupport.ToggleOn : ThemeSupport.ToggleOff);
        var thumb = new System.Windows.Shapes.Ellipse
        {
            Width = 14,
            Height = 14,
            Margin = new(3),
            HorizontalAlignment = enabled ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        thumb.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, enabled ? ThemeSupport.ToggleThumbOn : ThemeSupport.ToggleThumbOff);
        indicator.Child = thumb;
        Grid.SetColumn(indicator, 2);
        content.Children.Add(indicator);
    }

    private void BuildCustomize()
    {
        Paragraph("Choose your actions and use the arrows to reorder them.");
        var visible = SessionHubSettings.Favorites(Controller.Settings);
        var ordered = visible.Concat(SessionHubSettings.Catalog.Where(id => !visible.Contains(id))).ToArray();
        foreach (var id in ordered)
        {
            var name = FavoriteName(id);
            var shown = visible.Contains(id);
            var button = AddRow("favorite:" + id, name, shown ? "Visible · select to hide" : "Hidden · select to show", () => Change(SessionHubSettings.Toggle(Controller.Settings, id)));
            button.Margin = new(0, 4, 112, 4);
            var row = Grid.GetRow(button);
            AddReorder(id, name, row, -1, shown && Array.IndexOf(visible, id) > 0);
            AddReorder(id, name, row, 1, shown && Array.IndexOf(visible, id) < visible.Length - 1);
        }

        AddRow("restore-favorites", "Restore default order", "Show the six default actions", () => Change(Controller.Settings with { FavoriteQuickActions = null }));
        AddRow("back", "Back", "Changes remain pending until you save", Back);
    }

    private void AddReorder(string id, string name, int row, int delta, bool available)
    {
        var entryId = (delta < 0 ? "earlier:" : "later:") + id;
        var label = "Move " + name + (delta < 0 ? " earlier" : " later");
        var icon = ActionIconCatalog.Create(this, "back", 18);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.RenderTransformOrigin = new(.5, .5);
        icon.RenderTransform = new System.Windows.Media.RotateTransform(delta < 0 ? 90 : -90);
        var button = new Button
        {
            Content = icon,
            Width = 44,
            Height = 44,
            MinWidth = 44,
            MinHeight = 44,
            MaxWidth = 44,
            MaxHeight = 44,
            Padding = new(4),
            Margin = new(0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsEnabled = available,
            Tag = entryId
        };
        AutomationProperties.SetName(button, label);
        AutomationProperties.SetHelpText(button, available ? "Changes only the order of visible actions" : "No position available in this direction");
        var holder = new Border
        {
            Child = button,
            // The focus proxy owns a two-DIP frame around the actual 44-DIP button.
            Width = 48,
            Height = 48,
            Focusable = true,
            FocusVisualStyle = null,
            Tag = "description:" + entryId,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new(0, 0, delta < 0 ? 56 : 0, 0)
        };
        AutomationProperties.SetName(holder, label + ". No position available in this direction");
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
        "steam-overlay" => "Steam Overlay",
        "taskview" => "Task View",
        _ => "Windows audio"
    };
    private void BuildSetup()
    {
        var step = view.Current.SetupStep;
        Section($"SETUP · {step + 1} OF 3");
        if (step == 0)
        {
            Paragraph("Your panel, always within reach", 23);
            controllerStatusParagraph = Paragraph(controllerStatus);
            Paragraph("Press and release Xbox / Guide to open. A compatible controller is required. You can also open from the tray.");
            AddGuideToggle();
        }
        else if (step == 1)
        {
            Paragraph("Comfort on your screen", 23);
            Paragraph("Choose the display, side and size. Preview changes immediately; save when you are ready.");
            AddDisplay();
            AddRow("side", "Side · " + Controller.Settings.PanelSide, "Left / Right", () => Change(Controller.Settings with { PanelSide = Controller.Settings.PanelSide == "Left" ? "Right" : "Left" }));
            AddScale();
        }
        else
        {
            Paragraph("Libraries and feedback", 23);
            AddRow("configure-playnite", "Configure Playnite", Controller.Settings.PlaynitePath ?? "Choose executable", ConfigurePlaynite);
            AddFeedback();
        }

        if (step > 0)
            AddRow("setup-back", "Previous step", "Keeps your settings", () => SetSetupStep(step - 1));
        AddRow("setup-next", step < 2 ? "Continue" : "Finish", step < 2 ? "Next step" : "Marks setup as seen; Save keeps your settings", () =>
        {
            if (step < 2)
                SetSetupStep(step + 1);
            else
                FinishSetup();
        });
        AddRow("setup-skip", "Skip setup", "Returns to the panel; nothing is saved automatically", FinishSetup);
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
