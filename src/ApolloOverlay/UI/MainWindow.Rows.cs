using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ApolloOverlay;

public partial class MainWindow
{
    private TextBox? detailsText;
    private TextBlock? controllerStatusParagraph;

    private TextBox? operationText;
    private bool narrow;
    private void SetEntryText(string id, string title)
    {
        if (entries.FirstOrDefault(e => e.Id == id)?.Button is not { } button)
            return;
        if (button.Content is ActionButtonContent content)
            content.Title.Text = title;
        AutomationProperties.SetName(button, title);
        UpdateEntryAutomation(button);
    }

    private void SetEntryDescription(string id, string description, string? inputPrompt = null)
    {
        var index = entries.FindIndex(e => e.Id == id);
        if (index < 0)
            return;
        var entry = entries[index];
        if (entry.Button.Content is ActionButtonContent content)
        {
            if (inputPrompt is not null)
                RenderHint(content.Description, inputPrompt);
            else
            {
                content.Description.Text = ActionDescription(id, description, entry.Available);
                AutomationProperties.SetName(content.Description, description);
            }
            content.Description.Visibility = content.AllowsDescription && description.Length > 0 && (id == "audio" || !(CompactAction(id) || QuickAccessAction(id)) || !entry.Available) ? Visibility.Visible : Visibility.Collapsed;
        }

        AutomationProperties.SetHelpText(entry.Button, description);
        UpdateEntryAutomation(entry.Button);
        entries[index] = entry with
        {
            Reason = description
        };
    }

    private static void UpdateEntryAutomation(Button button)
    {
        if (button.Resources["DescriptionTarget"] is not Border description)
            return;
        var help = AutomationProperties.GetHelpText(button);
        AutomationProperties.SetName(description, AutomationProperties.GetName(button) + ". " + help);
        AutomationProperties.SetHelpText(description, help);
    }

    private void FeedbackText(string text, bool important = false)
    {
        StatusLabel.Text = text;
        UpdateStatusPresentation(important);
    }

    private void FeedbackFocusUnavailable(string text)
    {
        FeedbackText(text, important: true);
        UpdateHints();
        if (!compactChrome || exiting || !IsVisible || Dispatcher.HasShutdownStarted)
            return;
        StatusLabel.TextWrapping = TextWrapping.Wrap;
        StatusLabel.TextTrimming = TextTrimming.None;
        var cycle = focusCycle;
        // Layout and selection may still be settling. Do not reveal an old failure
        // after a later open, recovered focus, dismissal or replacement message.
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            if (exiting || !IsVisible || !compactChrome || focusCycle != cycle || focusConfirmed || StatusLabel.Text != text)
                return;
            StatusLabel.BringIntoView();
        });
    }

    private void UpdatePersistentState()
    {
        PlaceCompactStatus();
        RefreshOperationNotice(Environment.TickCount64);
        if (Rows.Children.Count > 0 && !Confirming)
        {
            var previous = entries.Select(e => e.Id).ToArray();
            var selectedFooter = IsFooterAction(navigation.Id) && previous.Contains(navigation.Id);
            entries.RemoveAll(e => e.Id is "save-pending" or "discard-pending" or "operation-details" || (e.Id == "dismiss-operation" && subpage != "operation"));
            if (operationNotice.AllowsActions && subpage != "operation")
            {
                entries.Add(new("operation-details", OperationDetailsButton, OpenOperation, !busy && !TransitionPending, "Read the full result"));
                entries.Add(new("dismiss-operation", DismissOperationButton, DismissOperation, !busy && !TransitionPending, "Dismiss result"));
            }

            if (Controller.SettingsDirty)
            {
                entries.Add(new("save-pending", SaveSettingsButton, Save, true, "Save changes"));
                entries.Add(new("discard-pending", DiscardSettingsButton, () =>
                {
                    Controller.Discard();
                    ApplySettings();
                    RefreshRows();
                }, true, "Restore saved settings"));
            }

            if (selectedFooter && !entries.Any(e => e.Id == navigation.Id))
            {
                navigation = navigation with
                {
                    Id = entries.Any(e => e.Id == lastContentSelection) ? lastContentSelection : Navigation.Preserve(navigation.Id, previous, entries.Select(e => e.Id).ToArray())
                };
                SetSelection();
            }
        }

        SetEntryDescription("exit", Controller.SettingsDirty ? "Unsaved changes will be lost" : "Stops background services and removes the tray icon");
        DirtyStrip.Visibility = Controller.SettingsDirty && !Confirming ? Visibility.Visible : Visibility.Collapsed;
        DirtyLabel.Text = Controller.Platform.IsPreview ? "Preview · changes are not saved" : "Unsaved changes";
        SaveSettingsButton.IsEnabled = !busy && !TransitionPending;
        DiscardSettingsButton.IsEnabled = !busy && !TransitionPending;
    }

    internal void RefreshRows()
    {
        automationRevision++;
        NoteAutomationTargets();
        HeaderIdentity.Text = "v" + AppBuild.Current.Version.Split('+')[0];
        HeaderBrand.Visibility = (narrow || compactChrome) && view.Current.Route != OverlayRoute.Hub ? Visibility.Collapsed : Visibility.Visible;
        HeaderBrand.FontSize = ResourceNumber(compactChrome || narrow ? "SessionType.Caption" : "SessionType.CompactBody", compactChrome || narrow ? 13 : 14);
        HeaderIdentity.ToolTip = AppBuild.Current.Display + " · " + AppBuild.Current.BuildId + " · " + AppBuild.Current.Commit;
        if (Confirming)
        {
            UpdatePersistentState();
            return;
        }

        var previous = entries.Select(e => e.Id).ToArray();
        Rows.Children.Clear();
        Rows.RowDefinitions.Clear();
        Rows.ColumnDefinitions.Clear();
        Rows.ColumnDefinitions.Add(new());
        Rows.ColumnDefinitions.Add(new());
        entries.Clear();
        operationText = null;
        entries.Add(new("settings", SettingsGear, () => Page(2), true, "Open System"));
        ContextCard.Visibility = view.Current.Route == OverlayRoute.Hub ? Visibility.Visible : Visibility.Collapsed;
        var target = Controller.Session.Anchor;
        var valid = target is not null && Controller.Platform.Valid(target);
        _ = UpdateSessionArtwork();
        SessionKicker.Visibility = Visibility.Collapsed;
        ApplySessionCardDensity();
        TargetLabel.Text = target?.Title ?? "No window selected";
        TargetLabel.ToolTip = target?.Title;
        AutomationProperties.SetName(ContextCard, target?.Title ?? "No window selected");
        SessionEvidence.Text = target?.ProcessName ?? "Apps";
        ContextStatus.Text = target is null ? "No selection" : valid ? "Available" : "Window unavailable";
        ContextStatusDot.Opacity = valid ? 1 : .3;
        ContextIcon.Source = target is null ? null : Controller.Platform.Icon(target);
        ContextIcon.Visibility = ContextIcon.Source is null ? Visibility.Collapsed : Visibility.Visible;
        ContextGlyph.Visibility = ContextIcon.Source is null ? Visibility.Visible : Visibility.Collapsed;
        ContextStatus.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
        ContextStatusDot.Visibility = Visibility.Collapsed;
        SessionEvidence.Visibility = compactChrome || target is not null && string.Concat(target.Title.Where(char.IsLetterOrDigit)).Equals(string.Concat(target.ProcessName.Where(char.IsLetterOrDigit)), StringComparison.OrdinalIgnoreCase) ? Visibility.Collapsed : Visibility.Visible;
        SessionAudioLabel.Text = target is null ? "Select a window to view its audio" : !valid ? "Window unavailable" : AudioFor(target) ? AudioBadge() : "Status unconfirmed";
        ControllerStatusLabel.Text = controllerStatus.Replace(" · waiting for input", "");
        BackButton.Visibility = view.Depth > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (view.Depth > 0)
            RegisterFixed("back-header", BackButton, Back, true, "Return to the previous view");
        foreach (var pair in new[]
        {
            (HomeTab, 0),
            (AppsTab, 1),
            (SettingsTab, 2)
        }

        )
        {
            var selected = pair.Item2 == 2 ? view.Current.Route == OverlayRoute.WindowsAudio : pair.Item2 == page && view.Current.Route != OverlayRoute.WindowsAudio;
            pair.Item1.BorderThickness = new(0, 0, 0, 2);
            if (selected)
                pair.Item1.SetResourceReference(BorderBrushProperty, ThemeSupport.Focus);
            else
                pair.Item1.BorderBrush = Brushes.Transparent;
            pair.Item1.Background = Brushes.Transparent;
            pair.Item1.SetResourceReference(ForegroundProperty, selected ? ThemeSupport.Text : ThemeSupport.SecondaryText);
            AutomationProperties.SetName(pair.Item1, (pair.Item2 switch
            {
                0 => "Session",
                1 => "Apps",
                _ => "Audio"
            }) + (selected ? " · selected" : ""));
            AutomationProperties.SetItemStatus(pair.Item1, selected ? "Selected" : "Not selected");
        }

        PageTitle.Text = view.Current.Route switch
        {
            OverlayRoute.WindowsAudio => "Windows audio",
            OverlayRoute.Details => "Window details",
            OverlayRoute.TargetOptions => "Window options",
            OverlayRoute.Integrations => "Integrations",
            OverlayRoute.Customize => "Quick access",
            OverlayRoute.Setup => "Make it yours",
            OverlayRoute.OperationResult => "Command result",
            OverlayRoute.Apps => view.Current.ChooseSession ? "Choose window" : "Apps",
            OverlayRoute.System => "System",
            OverlayRoute.PlayniteBrowser => "Find Playnite",
            OverlayRoute.Updates => compactChrome ? "Updates" : "Version and updates",
            OverlayRoute.UpdateReview => (compactChrome, rollbackReview is null) switch
            {
                (true, true) => "Review update",
                (true, false) => "Restore version",
                (false, true) => "Update overlay",
                _ => "Restore an earlier version"
            },
            _ => "Session controls"
        };
        if (SessionFeatureTitle is { } featureTitle)
            PageTitle.Text = featureTitle;
        BuildCurrentView();
        UpdateHints();
        renderedFeatureShape = FeatureViewShape();
        UpdatePersistentState();
        navigation = navigation with
        {
            Id = Navigation.Preserve(navigation.Id, previous, entries.Select(e => e.Id).ToArray())
        };
        SaveView();
        TrackApplicationAudio();
        revealPending = true;
        SetSelection();
        automationHost?.ObserveSnapshot(AutomationSnapshot());
    }

    private string GuideDescription() => Controller.Platform.IsPreview ? "Preview · Guide connection is not queried" : !Controller.Settings.GuideXInputEnabled ? "Disabled · double-click the tray icon to open" : guideBackend?.Active == true ? guideBackend.Status : guideConnection?.Status ?? "Controller or Guide input unavailable";
    private bool QuickAccessAction(string id) => view.Current.Route == OverlayRoute.Hub && SessionHubSettings.Catalog.Contains(id);
    private bool CompactAction(string id) => view.Current.Route == OverlayRoute.OperationResult
        || narrow && (view.Current.Route == OverlayRoute.Hub && SessionHubSettings.Catalog.Contains(id) || view.Current.Route == OverlayRoute.Media && id is "media-previous" or "media-next");
    private string ActionDescription(string id, string description, bool available) => QuickAccessAction(id) && !available ? id switch
    {
        "playnite" => "Set up in System",
        "steam-overlay" => "Unavailable here",
        _ => description
    } : description;
    private Button MakeButton(string id, string title, string description, Action action, bool available, bool danger)
    {
        var glyph = ActionIcon(id.StartsWith("favorite:", StringComparison.Ordinal) ? id[9..] : id);
        var grid = new ActionButtonContent(glyph, title, ActionDescription(id, description, available), showIcon: true);
        grid.Title.SetResourceReference(TextBlock.FontSizeProperty, "SessionType.Section");
        grid.Description.SetResourceReference(TextBlock.FontSizeProperty, "SessionType.Caption");
        grid.Description.Margin = new(0, 4, 0, 0);
        if (QuickAccessAction(id) && id != "audio" && available)
            grid.Description.Visibility = Visibility.Collapsed;
        AutomationProperties.SetName(grid.Description, description);
        var badge = new Border
        {
            CornerRadius = new(18),
            Width = 30,
            Height = 30,
            HorizontalAlignment = HorizontalAlignment.Left,
            Visibility = Visibility.Hidden,
            Child = InputPrompt.CreateGlyph(this, "A", 26)
        };
        Grid.SetColumn(badge, 0);
        grid.Children.Add(badge);
        var compactAction = CompactAction(id);
        if (compactAction)
        {
            grid.Title.SetResourceReference(TextBlock.FontSizeProperty, "SessionType.CompactBody");
            grid.Title.TextWrapping = TextWrapping.Wrap;
            grid.Title.TextTrimming = TextTrimming.CharacterEllipsis;
            grid.Title.LineHeight = 19;
            grid.Title.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            grid.Title.MaxHeight = 38;
            grid.Description.SetResourceReference(TextBlock.FontSizeProperty, "SessionType.Caption");
            grid.Description.Margin = new(0, 2, 0, 0);
            grid.Description.TextWrapping = TextWrapping.NoWrap;
            grid.Description.TextTrimming = TextTrimming.CharacterEllipsis;
            grid.Description.Visibility = description.Length > 0 && (!available || id == "audio") ? Visibility.Visible : Visibility.Collapsed;
            grid.ColumnDefinitions[0].Width = new GridLength(26);
            grid.Icon.Width = grid.Icon.Height = 20;
            badge.Width = badge.Height = 22;
            badge.Child = InputPrompt.CreateGlyph(this, "A", 20);
        }
        if (id == "apps")
        {
            var count = new Border
            {
                CornerRadius = new(15),
                Padding = new(9, 4, 9, 4),
                Margin = new(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = Controller.Apps.Count.ToString(),
                    FontSize = 17
                }
            };
            count.SetResourceReference(BackgroundProperty, ThemeSupport.Card);
            Grid.SetColumn(count, 2);
            grid.Children.Add(count);
            grid.Resources["CountBadge"] = count;
        }

        var button = new Button
        {
            Content = grid,
            Tag = id,
            MinHeight = compactAction ? 52 : id == "return" ? 72 : 56,
            IsEnabled = available,
            Padding = compactAction ? new(8, 8, 8, 8) : new(12, 8, 12, 8)
        };
        if (page == 0 && subpage == "" && id == "apps")
        {
            button.Background = Brushes.Transparent;
            button.BorderThickness = new(0);
            button.Margin = new(0, 2, 0, 2);
        }

        if (id == "return")
        {
            grid.Description.TextWrapping = TextWrapping.NoWrap;
            grid.Description.TextTrimming = TextTrimming.CharacterEllipsis;
        }

        if (id == "close")
            grid.Description.Visibility = Visibility.Collapsed;
        if (danger)
            button.SetResourceReference(ForegroundProperty, ThemeSupport.Critical);
        AutomationProperties.SetName(button, title);
        AutomationProperties.SetHelpText(button, description);
        button.Resources["ABadge"] = badge;
        button.GotKeyboardFocus += (_, focus) =>
        {
            if (navigation.Id != id)
            {
                navigation = new(id);
                SetSelection(restoreFocus: ReferenceEquals(focus.NewFocus, button));
            }
        };
        button.Click += (_, _) =>
        {
            ActivatePointerEntry(button);
        };
        entries.Add(new(id, button, action, available, description));
        return button;
    }

    private FrameworkElement ActionIcon(string id) => ActionIconCatalog.Create(this, id,
        muted: audio.Current.Muted, playing: CurrentMedia?.Playing == true,
        rightSide: Controller.Settings.PanelSide == "Right");

    internal string Feedback => StatusLabel.Text;
}
