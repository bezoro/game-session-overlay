using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace ApolloOverlay;

public partial class MainWindow
{
    private readonly OverlayViewState view = new();
    private long nextConfirmationScroll;
    private sealed record Entry(string Id, Button Button, Action Run, bool Available, string Reason);
    private readonly List<Entry> entries = [];
    private NavState navigation = new("session-card");
    private string lastContentSelection = "session-card";
    private int page;
    private string subpage = "";
    private WindowRef? detail;
    private void SaveView() => view.Remember(navigation.Id, ContentScroll.VerticalOffset);
    private void SyncView()
    {
        page = (int)view.Tab;
        detail = view.Current.Target;
        subpage = view.Current.Route switch
        {
            OverlayRoute.WindowsAudio => "audio",
            OverlayRoute.Details => "details",
            OverlayRoute.TargetOptions => "options",
            OverlayRoute.Integrations => "integrations",
            OverlayRoute.Customize => "customize",
            OverlayRoute.Setup => "setup",
            OverlayRoute.OperationResult => "operation",
            OverlayRoute.Confirmation => "confirmation",
            OverlayRoute.PlayniteBrowser => "playnite-browser",
            OverlayRoute.Updates => "updates",
            OverlayRoute.UpdateReview => "update-review",
            _ => ""
        };
        navigation = new(view.Current.SelectedId);
        AppearanceNavigationChanged();
    }

    private void OpenView(OverlayViewFrame next)
    {
        if (busy || TransitionPending || Confirming)
            return;
        SaveView();
        view.Open(next);
        SyncView();
        RefreshRows();
        ContentScroll.ScrollToVerticalOffset(next.ScrollOffset);
    }

    internal void OpenOptions(WindowRef target) => OpenView(new(OverlayRoute.TargetOptions, target, "application-mute"));
    private void ChooseSession() => OpenView(new(OverlayRoute.Apps, SelectedId: Controller.Apps.FirstOrDefault() is { } w ? WindowId(w) : "refresh-apps", ChooseSession: true));
    internal void Page(int index)
    {
        if (TransitionPending || Confirming || updateCommitting)
            return;
        if (updateBusy)
            CancelUpdateInteraction();
        if ((index + 3) % 3 != page && interfaceActionDepth == 0)
            RequestCue(FeedbackCue.Navigation);
        if (view.Current.Route == OverlayRoute.PlayniteBrowser)
            CancelPlayniteScan();
        SaveView();
        view.SwitchTab((OverlayTab)((index + 3) % 3));
        SyncView();
        RefreshRows();
        ContentScroll.ScrollToVerticalOffset(view.Current.ScrollOffset);
    }

    private void Subpage(string name, string first) => OpenView(new(name switch
    {
        "audio" => OverlayRoute.WindowsAudio,
        "details" => OverlayRoute.Details,
        "options" => OverlayRoute.TargetOptions,
        "integrations" => OverlayRoute.Integrations,
        "customize" => OverlayRoute.Customize,
        "setup" => OverlayRoute.Setup,
        _ => OverlayRoute.OperationResult
    }, detail, first));
    private void SetSelection(bool restoreFocus = true)
    {
        if (navigation.Id != "exit" && !IsFooterAction(navigation.Id) && entries.Any(entry => entry.Id == navigation.Id))
            lastContentSelection = navigation.Id;
        foreach (var entry in entries)
        {
            var selected = entry.Id == navigation.Id;
            InteractionMotion.SetIsSelected(entry.Button, selected && entry.Available && IsVisible && focusConfirmed && IsActive && OwnForeground && InteractionMotion.Allowed);
            var primary = entry.Id == "session-card";
            entry.Button.BorderThickness = new(selected ? 2 : 1);
            entry.Button.SetResourceReference(BorderBrushProperty, selected ? ThemeSupport.Focus : ThemeSupport.Stroke);
            if (selected && !entry.Available && entry.Button.Resources["DescriptionTarget"] is Border)
                entry.Button.BorderBrush = Brushes.Transparent;
            entry.Button.SetResourceReference(BackgroundProperty, selected ? ThemeSupport.FocusBackground : primary ? ThemeSupport.Card : ThemeSupport.Control);
            if (entry.Id == "close")
                entry.Button.Background = Brushes.Transparent;
            if (page == 0 && subpage == "" && entry.Id == "apps")
            {
                if (selected)
                    entry.Button.SetResourceReference(BackgroundProperty, ThemeSupport.Card);
                else
                    entry.Button.Background = Brushes.Transparent;
                entry.Button.BorderThickness = new(2);
            }

            entry.Button.Effect = null;
            if (entry.Button.Resources["ABadge"] is Border badge && entry.Button.Content is ActionButtonContent grid)
            {
                var showBadge = selected && entry.Available && inputMethod == "controller" && !entry.Id.StartsWith("window:", StringComparison.Ordinal) && grid.ColumnDefinitions[0].Width.Value > 0;
                badge.Visibility = showBadge ? Visibility.Visible : Visibility.Hidden;
                grid.Icon.Visibility = showBadge ? Visibility.Hidden : grid.ColumnDefinitions[0].Width.Value > 0 ? Visibility.Visible : Visibility.Collapsed;
            }

            if (!selected && (entry.Id == "close" || page == 0 && subpage == "" && entry.Id == "apps"))
                entry.Button.BorderBrush = Brushes.Transparent;
            if (entry.Button.Resources["DescriptionTarget"] is Border host)
            {
                host.BorderThickness = new(2);
                host.CornerRadius = new(primary ? 12 : 10);
                if (!primary)
                {
                    if (selected && !entry.Available)
                        host.SetResourceReference(Border.BackgroundProperty, ThemeSupport.FocusBackground);
                    else
                        host.Background = Brushes.Transparent;
                }
                if (selected && !entry.Available)
                    host.SetResourceReference(Border.BorderBrushProperty, ThemeSupport.Focus);
                else
                    host.BorderBrush = Brushes.Transparent;
            }

            AutomationProperties.SetItemStatus(entry.Button, selected ? (entry.Available ? "Selected" : "Selected · unavailable") : entry.Available ? "" : "Unavailable");
        }

        // Focus/admission can synchronously rebuild entries through WPF events.
        // Finish visual enumeration before any of those callbacks are possible.
        var selectedEntry = entries.FirstOrDefault(entry => entry.Id == navigation.Id);
        if (selectedEntry is null)
            return;
        SaveView();
        if (!restoreFocus || !NavigationAllowed)
            return;
        var target = FocusTarget(selectedEntry);
        target.Focus();
        if (entries.Any(entry => entry.Id == navigation.Id && ReferenceEquals(FocusTarget(entry), target)))
            RevealTarget(target);
    }

    private static string WindowId(WindowRef w) => $"window:{w.Handle}:{w.Pid}:{w.Started}:{w.Generation}";
    internal string SelectedAction => navigation.Id;
    internal IReadOnlyList<string> ActionIds => entries.Select(e => e.Id).ToArray();

    private bool IsFooterAction(string id) => id is "save-pending" or "discard-pending" || (subpage != "operation" && (id is "operation-details" or "dismiss-operation"));
    internal void Move(NavDirection direction)
    {
        if (!NavigationAllowed || busy || TransitionPending)
            return;
        if (Confirming)
        {
            MoveConfirmation(direction is NavDirection.Up or NavDirection.Left ? -1 : 1);
            return;
        }

        var reader = subpage == "operation" ? operationText : subpage == "details" ? detailsText : subpage == "update-review" ? updateNotesText : null;
        if (reader?.IsKeyboardFocusWithin == true)
        {
            if (direction == NavDirection.Up)
                reader.LineUp();
            else if (direction == NavDirection.Down)
                reader.LineDown();
            else
            {
                navigation = new(subpage == "operation" ? direction == NavDirection.Left ? "operation-text" : "dismiss-operation" : subpage == "update-review" ? direction == NavDirection.Left ? "update-notes" : preparedUpdate is null ? "prepare-update" : "apply-update" : direction == NavDirection.Left ? "window-information" : "switch-detail");
                SetSelection();
            }

            return;
        }

        if (view.Current.Route == OverlayRoute.Hub)
        {
            var next = (navigation.Id, direction) switch
            {
                ("session-volume", NavDirection.Down) => "windows-volume",
                ("windows-volume", NavDirection.Up) => "session-volume",
                ("windows-volume", NavDirection.Down) => "microphone",
                ("microphone", NavDirection.Up) => "windows-volume",
                ("microphone", NavDirection.Right) => "session-mixer",
                ("session-mixer", NavDirection.Left) => "microphone",
                ("session-mixer", NavDirection.Up) => "windows-volume",
                _ => null
            };
            if (next is not null && entries.Any(entry => entry.Id == next))
            {
                navigation = new(next);
                SetSelection();
                RequestCue(FeedbackCue.Navigation);
                return;
            }
        }

        if (AdjustAppearanceColor(direction) || AdjustSessionFeature(direction) || AdjustGuideLimit(direction))
            return;
        if (subpage == "audio" && navigation.Id == "volume" && direction is NavDirection.Left or NavDirection.Right)
        {
            AdjustVolume(direction == NavDirection.Left ? -.05f : .05f);
            return;
        }

        var before = navigation.Id;
        var layout = entries.Select((entry, index) =>
        {
            var element = FocusTarget(entry);
            if (element.IsArrangeValid && element.ActualHeight > 0)
            {
                var point = element.TranslatePoint(new Point(element.ActualWidth / 2, element.ActualHeight / 2), (UIElement)Content);
                return (entry.Id, point.X, point.Y);
            }

            return (entry.Id, 0d, index * 60d);
        }).ToArray();
        navigation = navigation with
        {
            Id = Navigation.Spatial(navigation.Id, layout, direction)
        };
        SetSelection();
        if (before != navigation.Id)
            RequestCue(FeedbackCue.Navigation);
    }

    internal void Sequential(int step)
    {
        if (!NavigationAllowed || busy || TransitionPending)
            return;
        if (Confirming)
        {
            MoveConfirmation(step);
            return;
        }

        var before = navigation.Id;
        navigation = Navigation.Sequential(navigation, entries.Select(e => e.Id).ToArray(), step);
        SetSelection();
        if (before != navigation.Id)
            RequestCue(FeedbackCue.Navigation);
    }

    private void MoveConfirmation(int step)
    {
        if (closeHold.Target is not { } target)
            return;
        closeHold.Open(target);
        keyboardDown = false;
        ReleasePointer();
        navigation = Navigation.Sequential(navigation, entries.Select(e => e.Id).ToArray(), step);
        SetSelection();
        RequestCue(FeedbackCue.Navigation);
    }

    internal void ActivateEntry()
    {
        if (!NavigationAllowed || TransitionPending)
            return;
        var entry = entries.FirstOrDefault(e => e.Id == navigation.Id);
        if (entry is null)
            return;
        if (Navigation.CanActivate(busy, Confirming, entry.Available))
        {
            RunInterfaceAction(entry.Button, entry.Run, entry.Id == "test-feedback" ? null : entry.Id is "back" or "back-header" or "confirm-cancel" ? FeedbackCue.Cancellation : FeedbackCue.Confirmation);
        }
        else if (!entry.Available)
        {
            FeedbackText("Unavailable · " + entry.Reason, important: true);
            RequestCue(FeedbackCue.Unavailable);
        }
    }

    internal void Details()
    {
        if (!NavigationAllowed || busy || TransitionPending || Confirming || view.Current.Route is not (OverlayRoute.Hub or OverlayRoute.Apps or OverlayRoute.Details))
            return;
        RequestCue(FeedbackCue.Confirmation);
        var target = Controller.Apps.FirstOrDefault(w => WindowId(w) == navigation.Id || "options:" + WindowId(w) == navigation.Id) ?? Controller.Recent.Items.FirstOrDefault(w => "recent:" + WindowId(w) == navigation.Id || "options:recent:" + WindowId(w) == navigation.Id) ?? view.Current.Target ?? Controller.Session.Anchor;
        if (target is null)
        {
            ChooseSession();
            return;
        }

        OpenOptions(target);
    }

    internal void Back() => Back(false);
    private void Back(bool trustedDismiss)
    {
        if (CancelUpdateInteraction())
            return;
        if (interfaceActionDepth == 0 && NavigationAllowed)
            RequestCue(FeedbackCue.Cancellation);
        if (view.Current.Route == OverlayRoute.PlayniteBrowser)
            CancelPlayniteScan();
        if (TransitionPending)
        {
            if (handoffDismiss)
            {
                ReaffirmDismiss(trustedDismiss);
                return;
            }

            CancelHandoff();
            input.Cancel();
            if (view.Current.Route == OverlayRoute.Hub)
                HidePanel(true);
            else
                FeedbackText("Transition cancelled.");
            return;
        }

        if (Confirming)
        {
            CancelHold();
            FeedbackText("Closing cancelled before sending.");
            return;
        }

        if (steamOwner is not null)
        {
            CancelSteamOwner("Cancelled by Back.");
            return;
        }

        if (busy)
        {
            effectCancellation?.Cancel();
            pending?.Cancel();
            RetainOperation("Unconfirmed", "Observation interrupted; the command already sent cannot be undone. Check the window.", dispatched: true);
            return;
        }

        if (view.Back())
        {
            SyncView();
            RefreshRows();
            ContentScroll.ScrollToVerticalOffset(view.Current.ScrollOffset);
            SetSelection();
            return;
        }

        if (page != 0)
        {
            Page(0);
            return;
        }

        HidePanel(true);
    }

    internal void RevealSelection() => SetSelection();
    private FrameworkElement FocusTarget(Entry entry) => !entry.Available && entry.Button.Resources["DescriptionTarget"] is FrameworkElement reason ? reason : entry.Button;
    private void WireFixedActions()
    {
        HomeTab.Click += (_, _) => ClickTab(HomeTab, 0);
        AppsTab.Click += (_, _) => ClickTab(AppsTab, 1);
        SettingsTab.Click += (_, _) => OpenPrimaryAudio();
        SettingsGear.Click += (_, _) => ActivatePointerEntry(SettingsGear);
        BackButton.Click += (_, _) => ActivatePointerEntry(BackButton);
        HomeTab.Content = new IconLabel(this, "session", "Session", 16, 16);
        AppsTab.Content = new IconLabel(this, "apps", "Apps", 16, 16);
        SettingsTab.Content = new IconLabel(this, "audio", "Audio", 16, 16);
        SaveSettingsButton.Content = new IconLabel(this, "save-pending", "Save");
        AutomationProperties.SetName(SaveSettingsButton, "Save changes");
        AutomationProperties.SetHelpText(SaveSettingsButton, "Save pending settings on this computer");
        AutomationProperties.SetName(DiscardSettingsButton, "Discard changes");
        AutomationProperties.SetHelpText(DiscardSettingsButton, "Restore the last saved settings");
        DiscardSettingsButton.Content = new IconLabel(this, "discard-pending", "Discard");
        OperationDetailsButton.Content = new IconLabel(this, "operation-details", "Details", 13, 14);
        CancelCloseButton.Content = new IconLabel(this, "back", "Cancel");
        HoldCloseButton.Content = new IconLabel(this, "confirm-hold", "Hold to close", 16, 20);
        SessionCardButton.Click += (_, _) => ActivatePointerEntry(SessionCardButton);
        ContextCard.PreviewMouseUp += (_, args) =>
        {
            if (args.ChangedButton != System.Windows.Input.MouseButton.Right)
                return;
            if (!NavigationAllowed || busy || TransitionPending || Confirming || view.Current.Route != OverlayRoute.Hub)
                return;
            SetInputMethod("mouse");
            navigation = new("session-card");
            SetSelection(restoreFocus: false);
            args.Handled = true;
            var target = Controller.Session.Anchor;
            RunInterfaceAction(SessionCardButton, () =>
            {
                if (target is null)
                    ChooseSession();
                else
                    OpenOptions(target);
            }, FeedbackCue.Confirmation);
        };
        SessionCardButton.GotKeyboardFocus += (_, _) =>
        {
            if (navigation.Id != "session-card")
            {
                navigation = new("session-card");
                SetSelection();
            }
        };

        ContextCard.Focusable = true;
        SessionCardButton.Resources["DescriptionTarget"] = ContextCard;
        CancelCloseButton.Click += (_, _) => ActivatePointerEntry(CancelCloseButton);
        SaveSettingsButton.Click += (_, _) => ActivatePointerEntry(SaveSettingsButton);
        DiscardSettingsButton.Click += (_, _) => ActivatePointerEntry(DiscardSettingsButton);
        DismissOperationButton.Click += (_, _) => ActivatePointerEntry(DismissOperationButton);
        OperationDetailsButton.Click += (_, _) => ActivatePointerEntry(OperationDetailsButton);
        foreach (var fixedEntry in new[]
        {
            (OperationDetailsButton, "operation-details"),
            (DismissOperationButton, "dismiss-operation"),
            (SaveSettingsButton, "save-pending"),
            (DiscardSettingsButton, "discard-pending"),
            (HoldCloseButton, "confirm-hold"),
            (CancelCloseButton, "confirm-cancel")
        }

        )
        {
            fixedEntry.Item1.Tag = fixedEntry.Item2;
            fixedEntry.Item1.GotKeyboardFocus += (_, _) =>
            {
                navigation = new(fixedEntry.Item2);
                SaveView();
            };
        }
    }
}
