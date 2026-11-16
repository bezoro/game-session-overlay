using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace ApolloOverlay;

public partial class MainWindow
{
    private readonly OverlayViewState view = new();
    private long nextConfirmationScroll;
    private sealed record Entry(
        string Id, Button Button, Action Run, bool Available, string Reason,
        Border? FocusHost = null, Border? LayoutHost = null);
    private readonly List<Entry> entries = [];
    private NavState navigation = new("session-card");
    private string lastContentSelection = "session-card";
    private void SaveView() => view.Remember(navigation.Id, ContentScroll.VerticalOffset);
    private void SyncView()
    {
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

    internal void OpenOptions(WindowRef target) => OpenView(new(OverlayRoute.TargetOptions, target, "application-audio"));
    private void ChooseSession() => OpenView(new(OverlayRoute.Apps, SelectedId: Controller.Apps.FirstOrDefault() is { } w ? WindowId(w) : "refresh-apps", ChooseSession: true));
    internal void Page(int index) => NavigateTab((OverlayTab)((index + 3) % 3));
    private void NavigateTab(OverlayTab tab, bool acknowledge = true) => RunSemanticCommand("navigate-tab", () => PageCore(tab, acknowledge));
    private void PageCore(OverlayTab tab, bool acknowledge)
    {
        if (TransitionPending || Confirming || updateCommitting)
            return;
        if (updateBusy)
            CancelUpdateInteraction();
        if (acknowledge && tab != view.Tab && interfaceActionDepth == 0)
            RequestCue(FeedbackCue.Navigation);
        if (view.Current.Route == OverlayRoute.PlayniteBrowser)
            CancelPlayniteScan();
        SaveView();
        view.SwitchTab(tab);
        SyncView();
        RefreshRows();
        ContentScroll.ScrollToVerticalOffset(view.Current.ScrollOffset);
    }

    private void OpenSubpage(OverlayRoute route, string first) => OpenView(new(route, view.Current.Target, first));
    private void UpdateSelectionMotion()
    {
        var context = InputContext;
        var animate = IsVisible && context.Confirmed && context.Active && context.OwnForeground && !context.OwnedDialog && !context.Exiting
            && InteractionMotion.Allowed && !Controller.Settings.ReducedMotion;
        Button? selected = null;
        if (animate)
            selected = entries.FirstOrDefault(entry => entry.Available && entry.Id == navigation.Id)?.Button;
        if (ReferenceEquals(selectionMotionTarget, selected))
            return;
        StopSelectionMotion();
        if (selected is not null)
            InteractionMotion.SetIsSelected(selected, true);
        selectionMotionTarget = selected;
    }

    private Button? selectionMotionTarget;
    private void StopSelectionMotion()
    {
        if (selectionMotionTarget is not { } selected)
            return;
        InteractionMotion.SetIsSelected(selected, false);
        selectionMotionTarget = null;
    }

    private void SetSelection(bool restoreFocus = true)
    {
        UpdateSessionActionHint();
        UpdateSelectionMotion();
        if (navigation.Id != "exit" && !IsFooterAction(navigation.Id) && entries.Any(entry => entry.Id == navigation.Id))
            lastContentSelection = navigation.Id;
        foreach (var entry in entries)
        {
            var selected = entry.Id == navigation.Id;
            if (entry.Id == "session-card")
            {
                entry.Button.SetResourceReference(BorderBrushProperty, selected ? ThemeSupport.Focus : ThemeSupport.Stroke);
                entry.Button.SetResourceReference(BackgroundProperty, selected ? ThemeSupport.Control : ThemeSupport.Card);
                AutomationProperties.SetItemStatus(entry.Button, selected ? "Selected" : "");
                continue;
            }

            entry.Button.BorderThickness = new(2);
            entry.Button.SetResourceReference(BorderBrushProperty, selected ? ThemeSupport.Focus : ThemeSupport.Stroke);
            if (selected && !entry.Available && entry.FocusHost is not null)
                entry.Button.BorderBrush = Brushes.Transparent;
            entry.Button.SetResourceReference(BackgroundProperty, selected ? ThemeSupport.FocusBackground : ThemeSupport.Control);
            if (entry.Id == "close")
                entry.Button.Background = Brushes.Transparent;
            if (view.Current.Route == OverlayRoute.Hub && entry.Id == "apps")
            {
                if (selected)
                    entry.Button.SetResourceReference(BackgroundProperty, ThemeSupport.Card);
                else
                    entry.Button.Background = Brushes.Transparent;
                entry.Button.BorderThickness = new(2);
            }

            entry.Button.Effect = null;
            if (entry.Button.Content is ActionButtonContent { SelectionBadge: { } badge } grid)
            {
                var showBadge = selected && entry.Available && inputMethod == "controller" && !entry.Id.StartsWith("window:", StringComparison.Ordinal) && grid.ColumnDefinitions[0].Width.Value > 0;
                badge.Visibility = showBadge ? Visibility.Visible : Visibility.Hidden;
                grid.Icon.Visibility = showBadge ? Visibility.Hidden : grid.ColumnDefinitions[0].Width.Value > 0 ? Visibility.Visible : Visibility.Collapsed;
            }

            if (!selected && (entry.Id == "close" || view.Current.Route == OverlayRoute.Hub && entry.Id == "apps"))
                entry.Button.BorderBrush = Brushes.Transparent;
            if (entry.FocusHost is { } host)
            {
                host.BorderThickness = new(2);
                host.CornerRadius = new(10);
                if (selected && !entry.Available)
                    host.SetResourceReference(Border.BackgroundProperty, ThemeSupport.FocusBackground);
                else
                    host.Background = Brushes.Transparent;
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

    private bool IsFooterAction(string id) => id is "save-pending" or "discard-pending" || (view.Current.Route != OverlayRoute.OperationResult && (id is "operation-details" or "dismiss-operation"));
    internal void Move(NavDirection direction)
    {
        if (!ValidateNavigationOwnership() || busy || TransitionPending)
            return;
        if (Confirming)
        {
            MoveConfirmation(direction is NavDirection.Up or NavDirection.Left ? -1 : 1);
            return;
        }

        var reader = view.Current.Route switch
        {
            OverlayRoute.OperationResult => operationText,
            OverlayRoute.Details => detailsText,
            OverlayRoute.UpdateReview => updateNotesText,
            _ => null
        };
        if (reader?.IsKeyboardFocusWithin == true)
        {
            if (direction == NavDirection.Up)
                reader.LineUp();
            else if (direction == NavDirection.Down)
                reader.LineDown();
            else
            {
                var movingLeft = direction == NavDirection.Left;
                var next = view.Current.Route switch
                {
                    OverlayRoute.OperationResult => movingLeft ? "operation-text" : "dismiss-operation",
                    OverlayRoute.UpdateReview => movingLeft ? "update-notes" : preparedUpdate is null ? "prepare-update" : "apply-update",
                    _ => movingLeft ? "window-information" : "switch-detail"
                };
                navigation = new(next);
                SetSelection();
            }

            return;
        }

        if (view.Current.Route == OverlayRoute.Hub)
        {
            var next = (navigation.Id, direction) switch
            {
                ("session-card", NavDirection.Down) => entries.FirstOrDefault(entry => SessionHubSettings.Catalog.Contains(entry.Id))?.Id ?? "apps",
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
        if (view.Current.Route == OverlayRoute.WindowsAudio && navigation.Id == "volume" && direction is NavDirection.Left or NavDirection.Right)
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
        if (!ValidateNavigationOwnership() || busy || TransitionPending)
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
        navigation = Navigation.Sequential(navigation, entries.Select(e => e.Id).ToArray(), step);
        SetSelection();
        RequestCue(FeedbackCue.Navigation);
    }

    internal void ActivateEntry()
    {
        if (!ValidateNavigationOwnership() || TransitionPending)
            return;
        var entry = entries.FirstOrDefault(e => e.Id == navigation.Id);
        if (entry is null)
            return;
        if (CancelDownloadSelected && !Confirming && entry.Available)
        {
            RunInterfaceAction(entry.Button, () => CancelUpdateInteraction(), FeedbackCue.Cancellation);
            return;
        }
        if (Confirming && dispatchSource is null)
            return;
        if (Navigation.CanActivate(busy, entry.Available))
        {
            RunInterfaceAction(entry.Button, entry.Run, entry.Id == "test-feedback" ? null : entry.Id is "back" or "back-header" or "confirm-cancel" ? FeedbackCue.Cancellation : FeedbackCue.Confirmation);
        }
        else if (!entry.Available)
        {
            LogCommandResult(entry.Id, ResultKind.Unavailable, "entry-unavailable");
            FeedbackText("Unavailable · " + entry.Reason, important: true);
            RequestCue(FeedbackCue.Unavailable);
        }
    }

    internal void Details() => RunSemanticCommand("window-options", DetailsCore);
    private void DetailsCore()
    {
        if (!ValidateNavigationOwnership() || busy || TransitionPending || Confirming || view.Current.Route is not (OverlayRoute.Hub or OverlayRoute.Apps or OverlayRoute.Details))
            return;
        if (HubCardSelected)
        {
            if (ResumableCardTarget is { } card)
                RunInterfaceAction(SessionCardButton, () => OpenOptions(card), FeedbackCue.Confirmation);
            return;
        }

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
    private void Back(bool trustedDismiss) => RunSemanticCommand("back", () => BackCore(trustedDismiss));
    private void BackCore(bool trustedDismiss)
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
            CancelCloseConfirmation();
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
            if (!closePending)
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

        if (view.Tab != OverlayTab.Hub)
        {
            NavigateTab(OverlayTab.Hub, acknowledge: false);
            return;
        }

        HidePanel(true);
    }

    internal void RevealSelection() => SetSelection();
    private static FrameworkElement FocusTarget(Entry entry) => !entry.Available && entry.FocusHost is { } reason ? reason : entry.Button;
    private void WireFixedActions()
    {
        HomeTab.Click += (_, _) => ClickTab(() => OpenPrimaryTab(0));
        AppsTab.Click += (_, _) => ClickTab(() => OpenPrimaryTab(1));
        SettingsTab.Click += (_, _) => ClickTab(() => OpenPrimaryTab(2));
        SettingsGear.Click += (_, _) => ActivatePointerEntry(SettingsGear);
        UpdateAvailableButton.Click += (_, _) => ActivatePointerEntry(UpdateAvailableButton);
        UpdateAvailableButton.Content = new IconLabel(this, "updates", "Update available", 12, 14);
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
        ConfirmCloseButton.Content = new IconLabel(this, "confirm-close", "Close window");
        ConfirmCloseButton.PreviewMouseLeftButtonDown += (_, e) =>
        {
            // WPF's release click requires a press on this button. Ignore the
            // continuation of a double-click that may have opened the dialog.
            if (e.ClickCount != 1 || !CloseKeysReleased())
                e.Handled = true;
        };
        ConfirmCloseButton.Click += (_, _) => ActivatePointerEntry(ConfirmCloseButton);
        SessionCardButton.Click += (_, _) => ActivatePointerEntry(SessionCardButton);
        SessionCardButton.PreviewMouseUp += (_, args) =>
        {
            if (args.ChangedButton != System.Windows.Input.MouseButton.Right)
                return;
            if (!ValidateNavigationOwnership() || busy || TransitionPending || Confirming || view.Current.Route != OverlayRoute.Hub)
                return;
            SetInputMethod("mouse");
            navigation = new("session-card");
            SetSelection(restoreFocus: false);
            args.Handled = true;
            var previousSource = dispatchSource;
            dispatchSource = -1;
            try
            {
                Details();
            }
            finally
            {
                dispatchSource = previousSource;
            }
        };
        SessionCardButton.GotKeyboardFocus += (_, _) =>
        {
            if (navigation.Id != "session-card")
            {
                navigation = new("session-card");
                SetSelection();
            }
        };

        CancelCloseButton.Click += (_, _) => ActivatePointerEntry(CancelCloseButton);
        SaveSettingsButton.Click += (_, _) => ActivatePointerEntry(SaveSettingsButton);
        DiscardSettingsButton.Click += (_, _) => ActivatePointerEntry(DiscardSettingsButton);
        DismissOperationButton.Click += (_, _) => ActivatePointerEntry(DismissOperationButton);
        OperationDetailsButton.Click += (_, _) => ActivatePointerEntry(OperationDetailsButton);
        foreach (var fixedEntry in new[]
        {
            (UpdateAvailableButton, "update-available"),
            (OperationDetailsButton, "operation-details"),
            (DismissOperationButton, "dismiss-operation"),
            (SaveSettingsButton, "save-pending"),
            (DiscardSettingsButton, "discard-pending"),
            (ConfirmCloseButton, "confirm-close"),
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
