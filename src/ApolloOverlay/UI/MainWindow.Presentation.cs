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
        Opacity = 1;
    }

    private void PrepareOpenAnimation()
    {
        CancelOpenAnimation();
        if (OpenAnimationAllowed)
            Opacity = 0;
    }

    internal void AnimateOpen()
    {
        if (!OpenAnimationAllowed)
        {
            CancelOpenAnimation();
            return;
        }

        Opacity = 1;
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)) { FillBehavior = FillBehavior.Stop });
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
            SetSelection(false, false);
        ThemeSupport.ApplySystemPalette(Application.Current);
        GlassSurface.Opacity = 1;
        GlassSurface.Effect = SystemParameters.HighContrast ? null : new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 18,
            ShadowDepth = 4,
            Opacity = .18
        };
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

    private async void HideNow(bool restore, WindowRef? returnTarget = null, bool preserveSteam = false, bool preserveEffect = false, bool preservePending = false)
    {
        if (!preserveSteam)
            CancelSteamOwner("Panel hidden; previous request cancelled.");
        if (!IsVisible || fadingOut)
            return;
        var wasForeground = Native.GetForegroundWindow() == new WindowInteropHelper(this).Handle;
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
        if (!preserveSteam && (ForceFadeFixture || !diagnostic && OpenAnimationAllowed))
        {
            fadingOut = true;
            var animation = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(180))
            {
                FillBehavior = FillBehavior.HoldEnd,
                EasingFunction = new CubicEase
                {
                    EasingMode = EasingMode.EaseOut
                }
            };
            BeginAnimation(OpacityProperty, animation);
            await Task.Delay(180);
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
        if (Confirming)
        {
            ModeLabel.Text = inputMethod == "controller" ? "Hold A · B cancel · right stick scroll" : inputMethod == "mouse" ? "Hold to confirm · Cancel stops" : "Hold Enter / Space · Esc cancel · PgUp/PgDn scroll";
            return;
        }

        var sessionTarget = Controller.Session.Anchor;
        SessionHint.Text = (sessionTarget is null || !Controller.Platform.Valid(sessionTarget)) ? inputMethod switch
        {
            "controller" => "A · Choose window",
            "mouse" => "Click to choose a window",
            _ => "Enter · Choose window"
        } : inputMethod switch
        {
            "controller" => "A · Resume   X · Options",
            "mouse" => "Click to resume · ·· Options",
            _ => "Enter · Resume   X · Options"
        };
        var details = page == 1 || subpage == "details";
        ModeLabel.Text = inputMethod switch
        {
            "mouse" => "Click to select · drag to adjust",
            "controller" => "A select · B back" + (details ? " · X options" : "") + "\nLB/RB tabs · D-pad move" + (Controller.Settings.GuideXInputEnabled && guideBackend?.Active == true ? " · Guide hide" : ""),
            _ => "Enter select · Esc back" + (details ? " · X options" : "") + "\nArrows / Tab move · PgUp/PgDn tabs · tray: open"
        };
        if (compactChrome)
            ModeLabel.Text = inputMethod switch
            {
                "mouse" => "Click to select · tray: open",
                "controller" => "A select · B back" + (details ? " · X options" : "") + "\nLB/RB tabs · D-pad move",
                _ => "Enter select · Esc back\nArrows / Tab move · PgUp/PgDn tabs"
            };
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
        StatusLabel.Visibility = compactChrome && !important ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateScrollContinuation() => ScrollContinuation.Visibility =
        !Confirming && ContentScroll.ViewportHeight > 0 && ContentScroll.ScrollableHeight - ContentScroll.VerticalOffset > 1
            ? Visibility.Visible
            : Visibility.Hidden;

    private void ApplySessionCardDensity()
    {
        var compact = compactChrome || narrow;
        TargetLabel.FontSize = compact ? 23 : 28;
        TargetLabel.MaxHeight = compact ? 32 : 40;
        TargetLabel.Margin = new(0, 0, 0, compact ? 2 : 4);
        SessionEvidence.FontSize = compact ? 12 : 14;
        SessionHint.FontSize = compact ? 12 : 14;
        SessionHint.Margin = new(0, compact ? 4 : 6, 0, 0);
        SessionIconColumn.Width = new(compact ? 48 : 66);
        SessionIconHost.Height = compact ? 40 : 54;
        SessionIconHost.Margin = new(0, 0, compact ? 8 : 12, 0);
        ContextGlyph.FontSize = compact ? 22 : 25;
        ContextIcon.Width = ContextIcon.Height = compact ? 32 : 42;
        SessionCardButton.Padding = compact ? new(10, 10, 10, 10) : new(14, 12, 14, 12);
        ContextCard.Margin = new(0, 0, 0, compact ? 8 : 12);
        // On the compact home screen the active Session tab supplies the page title.
        // Give the game, rather than duplicate chrome, the main typographic emphasis.
        PageTitle.Visibility = compactChrome && view.Current.Route == OverlayRoute.Hub ? Visibility.Collapsed : Visibility.Visible;
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
        HeaderBrand.Visibility = compact || narrow || view.Depth > 0 ? Visibility.Collapsed : Visibility.Visible;
        ControllerStatusLabel.Visibility = Visibility.Collapsed;
        DirtyLabel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        HeaderArea.Margin = new(0, 0, 0, compact ? 6 : 12);
        HeaderIdentityRow.Margin = new(0, 0, 0, compact ? 3 : 7);
        HeaderIdentityBadge.Padding = compact ? new(4, 2, 4, 2) : new(6, 3, 6, 3);
        HeaderIdentity.FontSize = compact ? 10 : 12;
        HeaderIdentityRow.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        PageTitle.FontSize = compact ? 20 : 25;
        ControllerInfoBar.Margin = new(0, compact ? 3 : 5, 0, 0);
        DirtyStrip.Margin = new(0, compact ? 8 : 12, 0, 0);
        DirtyStrip.Padding = new(12, compact ? 6 : 8, 12, compact ? 6 : 8);
        TabArea.Margin = new(0, 0, 0, compact ? 8 : 16);
        Sidebar.Padding = compact ? new(20, 14, 20, 14) : new(24, 22, 24, 22);
        FooterArea.Margin = new(0, compact ? 6 : 10, 0, 0);
        FooterDivider.Margin = new(0, 0, 0, compact ? 4 : 8);
        SharedControlNotice.FontSize = compact ? 12 : 14;
        StatusLabel.FontSize = compact ? 12 : 14;
        StatusLabel.Margin = new(0, compact ? 2 : 4, 0, 0);
        ModeLabel.FontSize = compact ? 12 : 14;
        ModeLabel.LineHeight = compact ? 17 : 19;
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
                _ = UpdateSessionArtwork();
        };
        Rows.LayoutUpdated += (_, _) =>
        {
            if (revealPending && Rows.IsArrangeValid && ContentScroll.ViewportHeight > 0)
            {
                revealPending = false;
                if (Keyboard.FocusedElement is FrameworkElement editor && editor is TextBox or Slider && Rows.IsAncestorOf(editor))
                    editor.BringIntoView();
                else
                    SetSelection(false);
            }
        };
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
}
