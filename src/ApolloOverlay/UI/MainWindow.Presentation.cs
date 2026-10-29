using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ApolloOverlay;

public partial class MainWindow
{
    internal Func<nint, uint> DpiReader
    {
        private get;
        set;
    } = Native.GetDpiForWindow;

    private bool revealPending, compactChrome;
    private bool fadingOut, fadeInterrupted;
    private long fadeGeneration;
    private int fadePadConnections, fadeGuideConnections;
    internal bool ForceFadeFixture
    {
        private get;
        set;
    }
    internal Func<PadFrame[]>? FadeFramesFixture
    {
        private get;
        set;
    }
    internal bool FadePending => fadingOut;

    internal void BeginFadeFixture() => HideNow(false);
    private bool placing;
    private bool importantStatus;
    private string automaticScaleDescription = "Automatically adjusted to the monitor and Windows scaling";
    private bool OpenAnimationAllowed => InteractionMotion.Allowed;

    private void CancelOpenAnimation()
    {
        BeginAnimation(OpacityProperty, null);
        Opacity = opening && !focusConfirmed ? 0 : 1;
    }

    private void PrepareOpenAnimation()
    {
        CancelOpenAnimation();
        Opacity = 0;
    }

    internal void AnimateOpen(double from = 0)
    {
        if (!OpenAnimationAllowed)
        {
            CancelOpenAnimation();
            return;
        }

        Opacity = 1;
        BeginAnimation(OpacityProperty, new DoubleAnimation(Math.Clamp(from, 0, 1), 1, ResourceDuration("SessionMotion.Panel", 200))
        {
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (exiting || Dispatcher.HasShutdownStarted)
            return;
        if (Dispatcher.CheckAccess())
            UpdateAppearance();
        else
            Dispatcher.BeginInvoke(UpdateAppearance);
    }

    private void UpdateAppearance()
    {
        if (!OpenAnimationAllowed)
        {
            CancelOpenAnimation();
            foreach (var entry in entries)
                InteractionMotion.Stop(entry.Button);
        }
        else if (IsVisible)
            SetSelection(restoreFocus: false);
        ApplyAppearanceTheme();
        GlassSurface.Opacity = 1;
        if (SystemParameters.HighContrast)
            GlassSurface.Effect = null;
        else
            GlassSurface.SetResourceReference(EffectProperty, "SessionPanelShadow");
    }

    internal void Place()
    {
        if (placing)
            return;
        placing = true;
        try
        {
            var screens = Controller.RefreshDisplays();
            if (screens.Count == 0)
                return;
            var foreground = Controller.ReturnTarget;
            var anchor = Controller.Session.Anchor;
            var target = foreground is { Protected: false } ? foreground : anchor is not null && Controller.Platform.Valid(anchor) ? anchor : foreground;
            var monitor = target is null ? 0 : Native.MonitorFromWindow(target.Handle, 2);
            var info = new Native.MonitorInfo
            {
                Size = System.Runtime.InteropServices.Marshal.SizeOf<Native.MonitorInfo>(),
                Device = ""
            };
            var active = monitor != 0 && Native.GetMonitorInfo(monitor, ref info) ? screens.FirstOrDefault(d => d.Id == info.Device) : null;
            var chosen = screens.FirstOrDefault(d => d.Id == Controller.Settings.DisplayId) ?? active ?? screens.FirstOrDefault(d => d.Primary) ?? screens[0];
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != 0)
                Native.SetWindowPos(hwnd, 0, chosen.X + 16, chosen.Y + 16, 0, 0, 0x15);
            var dpi = DpiReader(hwnd);
            var presentation = SidebarLayout.Calculate(chosen, dpi, Controller.Settings.UiScalePercent, Controller.Settings.PanelSide, DesktopPresentation);
            if (pointerDown)
                CancelHold();
            ((FrameworkElement)Content).LayoutTransform = new ScaleTransform(presentation.ContentScale, presentation.ContentScale);
            var placement = presentation.Bounds;
            automaticScaleDescription = $"Windows {presentation.DpiScale:P0} · sized to the monitor work area";
            SetEntryDescription("ui-scale", automaticScaleDescription);
            Width = placement.Width / presentation.DpiScale;
            Height = placement.Height / presentation.DpiScale;
            if (hwnd != 0)
                Native.SetWindowPos(hwnd, (nint)(-1), placement.X, placement.Y, placement.Width, placement.Height, 0x10);
        }
        finally
        {
            placing = false;
        }
    }

    private void HidePanel(bool restore, bool trustedDismiss = false)
    {
        activationCancellation?.Cancel();
        if (handoffDismiss)
            ReaffirmDismiss(trustedDismiss);
        var target = Controller.ReturnTarget;
        QueueHandoff(() => HideNow(restore, target), () => target is null ? Controller.ReturnTarget is null : Controller.ReturnTarget?.Same(target) == true && Controller.Platform.Valid(target), dismiss: true);
    }

    private bool FadeInputsNeutral(IReadOnlyList<PadFrame> frames)
    {
        if (frames.Count != 4
            || frames.Any(f => f.ReadFailed
            || f.Connected
            && (f.Buttons != 0
            || Math.Abs((int)f.X) >= Controller.Settings.Deadzone * 2 / 3
            || Math.Abs((int)f.Y) >= Controller.Settings.Deadzone * 2 / 3
            || Math.Abs((int)f.RX) >= Controller.Settings.Deadzone * 2 / 3
            || Math.Abs((int)f.RY) >= Controller.Settings.Deadzone * 2 / 3
            || f.LT >= 30
            || f.RT >= 30)))
            return false;
        var connected = 0;
        for (var i = 0; i < frames.Count; i++)
            if (frames[i].Connected)
                connected |= 1 << i;
        if ((fadePadConnections & ~connected) != 0)
            return false;
        fadePadConnections |= connected;
        if (guideBackend is { Active: true } backend)
        {
            if (!backend.SnapshotValid || backend.Held || (fadeGuideConnections & ~backend.Connections) != 0)
                return false;
            fadeGuideConnections |= backend.Connections;
        }
        else if (fadeGuideConnections != 0)
            return false;
        return PhysicalKeysNeutral();
    }

    private async void HideNow(bool restore, WindowRef? returnTarget = null, bool preserveSteam = false, bool preserveEffect = false, bool preservePending = false, bool immediate = false)
    {
        if (!preserveSteam)
            CancelSteamOwner("Panel hidden; previous request cancelled.");
        if (!IsVisible || fadingOut)
            return;
        var wasForeground = Native.GetForegroundWindow() == new WindowInteropHelper(this).Handle;
        var closingOpacity = Math.Clamp(Opacity, 0, 1);
        RevokeFocus("hide");
        CancelHandoff();
        heldKeys.Clear();
        CancelHold();
        if (!preserveSteam && !preservePending)
            pending?.Cancel();
        if (!preserveEffect)
            effectCancellation?.Cancel();
        input.Cancel();
        Sidebar.BeginAnimation(OpacityProperty, null);
        var generation = ++fadeGeneration;
        fadeInterrupted = false;
        fadePadConnections = 0;
        fadeGuideConnections = 0;
        if (lastInputSample is { } baseline)
        {
            for (var i = 0; i < Math.Min(4, baseline.Pads.Count); i++)
                if (baseline.Pads[i].Connected)
                    fadePadConnections |= 1 << i;
            fadeGuideConnections = baseline.GuideConnections;
        }

        // Steam requires the window to disappear before its focus handshake. Diagnostics stay synchronous.
        if (!immediate && !preserveSteam && closingOpacity > 0 && (ForceFadeFixture || !diagnostic && OpenAnimationAllowed))
        {
            fadingOut = true;
            var duration = ResourceDuration("SessionMotion.Panel", 200);
            var animation = new DoubleAnimation(closingOpacity, 0, duration)
            {
                FillBehavior = FillBehavior.HoldEnd,
                EasingFunction = new CubicEase
                {
                    EasingMode = EasingMode.EaseIn
                }
            };
            BeginAnimation(OpacityProperty, animation);
            await Task.Delay(duration.TimeSpan);
            if (exiting || generation != fadeGeneration)
                return;
            // Recheck at the handoff itself: the timer may have been delayed on the UI thread.
            guideBackend?.PollForOverlay(IsVisible, focusCycle, Environment.TickCount64, Controller.Settings.GuideOpenMaxHoldMs);
            fadeInterrupted |= !FadeInputsNeutral(FadeFramesFixture?.Invoke() ?? Enumerable.Range(0, 4).Select(i => Native.ReadPad((uint)i)).ToArray());
            fadingOut = false;
        }

        var stillForeground = Native.GetForegroundWindow() == new WindowInteropHelper(this).Handle;
        if (stillForeground && fadeInterrupted)
        {
            OpenPanel();
            FeedbackText("Input detected while closing. Release the controls and try again.");
            return;
        }

        Hide();
        CancelOpenAnimation();
        audio.SetVisible(false);
        UpdatePolling();
        if (restore && wasForeground && stillForeground && !fadeInterrupted && PhysicalKeysNeutral() && returnTarget is { } target && Controller.Platform.Valid(target))
        {
            var result = await Controller.Platform.Switch(target);
            if (result.Kind != ResultKind.Completed && result.Kind != ResultKind.Simulated && !exiting)
            {
                if (!Controller.Platform.IsPreview)
                    Controller.Diagnostics.Log("focus-return", result.Kind);
                FeedbackText(result.Message);
            }
        }
    }

    private void UpdateHints()
    {
        UpdateContextualHints();
        UpdateScrollContinuation();
        var context = InputContext;
        if (context.Visible && !context.Confirmed && !context.OwnedDialog)
        {
            RenderHint(SessionHint, "Focus not confirmed");
            System.Windows.Automation.AutomationProperties.SetHelpText(SessionCardButton, InputPrompt.Read(SessionHint));
            RenderHint(ModeLabel, InputHint("{Guide} / {B} hide · reopen to retry", "{Esc} hides · tray opens the panel", "{MouseLeft} the panel to retry · tray opens"));
            return;
        }
        if (Confirming)
        {
            RenderHint(ModeLabel, InputHint("Hold {A} · {B} cancel · {RS} scroll", "Hold {Enter} / {Space} · {Esc} cancel · {PgUp}/{PgDn} scroll", "Hold {MouseLeft} to confirm · Cancel stops"));
            return;
        }

        var sessionTarget = Controller.Session.Anchor;
        RenderHint(SessionHint, (sessionTarget is null || !Controller.Platform.Valid(sessionTarget)) ? inputMethod switch
        {
            "controller" => "{A} Choose window",
            "mouse" => "{MouseLeft} Choose window",
            _ => "{Enter} Choose window"
        } : inputMethod switch
        {
            "controller" => "{A} Resume",
            "mouse" => "{MouseLeft} Resume",
            _ => "{Enter} Resume"
        });
        var details = page == 1 || subpage == "details" || view.Current.Route == OverlayRoute.Hub && sessionTarget is not null && Controller.Platform.Valid(sessionTarget);
        var prompt = inputMethod switch
        {
            "mouse" => "{MouseLeft} Select · {MouseScroll} Scroll" + (details ? " · {MouseRight} Options" : ""),
            "controller" => "{A} select · {B} back" + (details ? " · {X} options" : "") + "\n{LB}/{RB} tabs · {Dpad} move" + (Controller.Settings.GuideXInputEnabled && guideBackend?.Active == true ? " · {Guide} hide" : ""),
            _ => "{Enter} select · {Esc} back" + (details ? " · {KeyX} options" : "") + "\n{Arrows} / {Tab} move · {PgUp}/{PgDn} tabs"
        };
        RenderHint(ModeLabel, prompt);
    }

    private void PlaceCompactStatus()
    {
        // Auxiliary status belongs with the footer, never between the tabs and game.
        Panel destination = compactChrome && Confirming ? ConfirmationInfo : FooterArea;
        if (ReferenceEquals(StatusLabel.Parent, destination))
            return;
        if (StatusLabel.Parent is Panel previous)
            previous.Children.Remove(StatusLabel);
        if (ReferenceEquals(destination, FooterArea))
            destination.Children.Insert(2, StatusLabel);
        else
            destination.Children.Add(StatusLabel);
    }

    private void UpdateStatusPresentation(bool important)
    {
        importantStatus = important;
        StatusLabel.TextWrapping = compactChrome && important ? TextWrapping.Wrap : TextWrapping.NoWrap;
        StatusLabel.TextTrimming = compactChrome && important ? TextTrimming.None : TextTrimming.CharacterEllipsis;
        StatusLabel.MaxHeight = compactChrome && important ? 60 : double.PositiveInfinity;
        StatusLabel.Visibility = string.IsNullOrWhiteSpace(StatusLabel.Text) || compactChrome && !important ? Visibility.Collapsed : Visibility.Visible;
    }

    private const double ScrollFadeHeight = 24;
    private readonly LinearGradientBrush scrollFade = new(
        [new GradientStop(Colors.Black, 0), new GradientStop(Colors.Black, .94), new GradientStop(Colors.Transparent, 1)],
        new Point(0, 0), new Point(0, 1));

    private void UpdateScrollContinuation()
    {
        var more = !Confirming && ContentScroll.ViewportHeight > 0 && ContentScroll.ScrollableHeight - ContentScroll.VerticalOffset > 1;
        ScrollContinuation.Visibility = more ? Visibility.Visible : Visibility.Hidden;
        var selected = entries.FirstOrDefault(entry => entry.Id == navigation.Id);
        var target = selected is null ? null : FocusTarget(selected);
        var clearanceFits = target is null || !ContentScroll.IsAncestorOf(target) || target.ActualHeight + ScrollFadeHeight + 4 <= ContentScroll.ViewportHeight;
        if (more && clearanceFits && !SystemParameters.HighContrast)
        {
            scrollFade.GradientStops[1].Offset = 1 - Math.Min(.25, ScrollFadeHeight / ContentScroll.ViewportHeight);
            ContentScroll.OpacityMask = scrollFade;
        }
        else
            ContentScroll.OpacityMask = null;
    }

    private void RevealTarget(FrameworkElement target)
    {
        if (ContentScroll.IsAncestorOf(target) && target.ActualWidth > 0 && target.ActualHeight > 0
            && target.ActualHeight + ScrollFadeHeight + 4 <= ContentScroll.ViewportHeight)
            target.BringIntoView(new Rect(0, -2, target.ActualWidth, target.ActualHeight + ScrollFadeHeight + 4));
        else
            target.BringIntoView();
        UpdateScrollContinuation();
    }

    private void ApplySessionCardDensity()
    {
        var compact = compactChrome || narrow;
        TargetLabel.FontSize = compact ? 24 : ResourceNumber("SessionType.Title", 26);
        TargetLabel.LineHeight = compact ? 28 : 32;
        TargetLabel.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        TargetLabel.MaxHeight = TargetLabel.LineHeight * 2;
        TargetLabel.Margin = new(0, 0, 0, compact ? 2 : 4);
        SessionEvidence.FontSize = ResourceNumber("SessionType.Caption", 13);
        SessionHint.FontSize = ResourceNumber(compact ? "SessionType.CompactBody" : "SessionType.Body", compact ? 14 : 15);
        SessionHint.Margin = new(0);
        SessionIconColumn.Width = new(compact ? 48 : 66);
        SessionIconHost.Height = compact ? 40 : 54;
        SessionIconHost.Margin = new(0, 0, compact ? 8 : 12, 0);
        SessionIdentity.Margin = compact ? new(12, 12, 12, 8) : new(16, 14, 16, 12);
        ContextGlyph.Width = ContextGlyph.Height = compact ? 22 : 25;
        ContextIcon.Width = ContextIcon.Height = compact ? 32 : 42;
        SessionCardButton.Padding = new(12, 8, 12, 8);
        SessionCardButton.Margin = compact ? new(8, 0, 8, 8) : new(12, 0, 12, 12);
        ContextCard.Margin = new(0, 0, 0, compact ? 8 : 12);
        // Root tabs provide their own label. Nested views keep a distinct heading.
        PageTitle.Visibility = view.Current.Route == OverlayRoute.Hub || view.Depth == 0 && view.Current.Route == OverlayRoute.Apps
            ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateCompactChrome(double height)
    {
        var compact = height > 0 && height < 650;
        if (compact == compactChrome)
            return;
        compactChrome = compact;
        if (pointerDown)
            CancelHold();
        HeaderIdentity.Visibility = Visibility.Visible;
        HeaderBrand.Visibility = compact || narrow ? Visibility.Collapsed : Visibility.Visible;
        ControllerStatusLabel.Visibility = Visibility.Collapsed;
        DirtyLabel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        HeaderArea.Margin = new(0, 0, 0, compact ? 8 : 12);
        HeaderIdentityRow.Margin = new(0);
        HeaderIdentityBadge.Padding = compact ? new(4, 2, 4, 2) : new(6, 3, 6, 3);
        HeaderIdentity.FontSize = 12;
        HeaderIdentityRow.HorizontalAlignment = HorizontalAlignment.Stretch;
        PageTitle.FontSize = compact ? 24 : ResourceNumber("SessionType.Title", 26);
        PageTitle.Margin = new(0, 12, 0, 0);
        ControllerInfoBar.Margin = new(0, 8, 0, 0);
        DirtyStrip.Margin = new(0, compact ? 8 : 12, 0, 0);
        DirtyStrip.Padding = new(12, compact ? 6 : 8, 12, compact ? 6 : 8);
        TabArea.Margin = new(0, 0, 0, compact ? 8 : 12);
        Sidebar.Padding = compact ? new(16, 12, 16, 12) : new(24, 20, 24, 20);
        FooterArea.Margin = new(0, compact ? 6 : 10, 0, 0);
        FooterDivider.Margin = new(0, 0, 0, compact ? 4 : 8);
        SharedControlNotice.FontSize = ResourceNumber("SessionType.Caption", 13);
        SharedControlNotice.FontWeight = FontWeights.Normal;
        SharedControlNotice.SetResourceReference(TextBlock.ForegroundProperty, ThemeSupport.SecondaryText);
        StatusLabel.FontSize = ResourceNumber("SessionType.Caption", 13);
        StatusLabel.Margin = new(0, compact ? 2 : 4, 0, 0);
        ModeLabel.FontSize = ResourceNumber("SessionType.Caption", 13);
        ModeLabel.LineHeight = compact ? 20 : 22;
        ModeLabel.Margin = new(0, compact ? 3 : 5, 0, 0);
        if (!compact)
        {
            StatusLabel.TextWrapping = TextWrapping.NoWrap;
            StatusLabel.TextTrimming = TextTrimming.CharacterEllipsis;
        }

        // Header, navigation, notices and Save/Discard keep their fixed grid rows.
        ApplySessionCardDensity();
        UpdateStatusPresentation(importantStatus);
        PlaceCompactStatus();
        revealPending = true;
        UpdateHints();
        _ = UpdateSessionArtwork();
    }

    internal Color SurfaceColor => ((SolidColorBrush)FindResource(ThemeSupport.Surface)).Color;
    internal Color SelectionColor => ((SolidColorBrush)FindResource(ThemeSupport.FocusBackground)).Color;

    private void WireAdaptiveLayout()
    {
        ((FrameworkElement)Content).SizeChanged += (_, e) => UpdateCompactChrome(e.NewSize.Height);
        ContentScroll.ScrollChanged += (_, args) =>
        {
            UpdateScrollContinuation();
            if (args.ViewportHeightChange != 0)
            {
                // Status and input hints can resize the viewport after initial selection.
                revealPending = true;
                RevealSelectionAfterLayout();
                _ = UpdateSessionArtwork();
            }
        };
        Rows.LayoutUpdated += (_, _) => RevealSelectionAfterLayout();
        Rows.SizeChanged += (_, e) =>
        {
            var compact = e.NewSize.Width < 390;
            if (compact != narrow)
            {
                narrow = compact;
                if (!Confirming && view.Current.Route == OverlayRoute.Hub)
                {
                    var offset = ContentScroll.VerticalOffset;
                    RefreshRows();
                    ContentScroll.ScrollToVerticalOffset(offset);
                }
            }
        };
    }

    private void RevealSelectionAfterLayout()
    {
        if (!revealPending || exiting || !Rows.IsArrangeValid || ContentScroll.ViewportHeight <= 0)
            return;
        revealPending = false;
        if (Keyboard.FocusedElement is FrameworkElement editor && editor is TextBox or Slider && Rows.IsAncestorOf(editor))
            RevealTarget(editor);
        else
            SetSelection();
    }
}
