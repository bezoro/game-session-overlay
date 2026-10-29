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
            if (dpi == 0)
                dpi = 96;
            var scale = dpi / 96d;
            if (pointerDown)
                CancelHold();
            var density = DesktopPresentation ? Controller.Settings.UiScalePercent / 100d : SidebarLayout.EffectiveScale(chosen, dpi, Controller.Settings.UiScalePercent);
            ((FrameworkElement)Content).LayoutTransform = new ScaleTransform(density, density);
            var placement = SidebarLayout.Place(chosen, dpi, Controller.Settings.UiScalePercent, Controller.Settings.PanelSide);
            if (DesktopPresentation)
            {
                var compactWidth = (int)Math.Min(480 * scale * density, chosen.Width - 32 * scale);
                var compactHeight = (int)Math.Min(760 * scale * density, chosen.Height - 32 * scale);
                placement = new(Controller.Settings.PanelSide == "Right" ? chosen.X + chosen.Width - (int)(16 * scale) - compactWidth : chosen.X + (int)(16 * scale), chosen.Y + (int)(16 * scale), compactWidth, compactHeight);
            }

            Width = placement.Width / scale;
            Height = placement.Height / scale;
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
            CancelSteamOwner("Painel ocultado; solicitação anterior cancelada.");
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
            FeedbackText("Entrada detectada ao fechar. Solte os controles e tente novamente.");
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
        if (Confirming)
        {
            ModeLabel.Text = inputMethod == "controller" ? "A segurar · B cancelar · analógico direito rolar" : inputMethod == "mouse" ? "Segure para confirmar · Cancelar interrompe" : "Enter / Espaço segurar · Esc cancelar · PgUp/PgDn rolar";
            return;
        }

        var sessionTarget = Controller.Session.Anchor;
        SessionHint.Text = (sessionTarget is null || !Controller.Platform.Valid(sessionTarget)) ? inputMethod switch
        {
            "controller" => "A · Escolher janela",
            "mouse" => "Clique para escolher janela",
            _ => "Enter · Escolher janela"
        } : inputMethod switch
        {
            "controller" => "A · Retomar   X · Opções",
            "mouse" => "Clique para retomar · ·· Opções",
            _ => "Enter · Retomar   X · Opções"
        };
        var details = page == 1 || subpage == "details";
        ModeLabel.Text = inputMethod switch
        {
            "mouse" => "Clique para selecionar · arraste para ajustar",
            "controller" => "A selecionar · B voltar" + (details ? " · X opções" : "") + "\nLB/RB abas · direcional mover" + (Controller.Settings.GuideXInputEnabled && guideBackend?.Active == true ? " · Guide ocultar" : ""),
            _ => "Enter selecionar · Esc voltar" + (details ? " · X opções" : "") + "\nPgUp/PgDn abas · Setas / Tab mover · bandeja: abrir"
        };
        if (compactChrome)
            ModeLabel.Text = inputMethod switch
            {
                "mouse" => "Clique selecionar · bandeja: abrir",
                "controller" => "A selecionar · B voltar" + (details ? " · X opções" : "") + "\nLB/RB abas · direcional mover",
                _ => "Enter selecionar · Esc voltar\nTab / Setas mover · PgUp/PgDn abas"
            };
    }

    private void PlaceCompactStatus()
    {
        // In a short window, keep the controller warning and instructions pinned;
        // auxiliary status can scroll with the page or confirmation text instead.
        Panel destination = compactChrome ? (Confirming ? ConfirmationInfo : ContentStack) : FooterArea;
        if (ReferenceEquals(StatusLabel.Parent, destination))
            return;
        if (StatusLabel.Parent is Panel previous)
            previous.Children.Remove(StatusLabel);
        if (ReferenceEquals(destination, FooterArea))
            destination.Children.Insert(2, StatusLabel);
        else if (ReferenceEquals(destination, ContentStack))
            destination.Children.Insert(Math.Min(1, destination.Children.Count), StatusLabel);
        else
            destination.Children.Add(StatusLabel);
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
        HeaderArea.Margin = new(0, 0, 0, compact ? 8 : 12);
        DirtyStrip.Margin = new(0, compact ? 8 : 12, 0, 0);
        DirtyStrip.Padding = new(12, compact ? 6 : 8, 12, compact ? 6 : 8);
        TabArea.Margin = new(0, 0, 0, compact ? 8 : 16);
        Sidebar.Padding = compact ? new(20, 14, 20, 14) : new(24, 22, 24, 22);
        FooterArea.Margin = new(0, compact ? 6 : 10, 0, 0);
        FooterDivider.Margin = new(0, 0, 0, compact ? 4 : 8);
        if (!compact)
        {
            StatusLabel.TextWrapping = TextWrapping.NoWrap;
            StatusLabel.TextTrimming = TextTrimming.CharacterEllipsis;
        }

        // Tabs and Save/Discard stay fixed. The header and status move into the
        // existing scroller so selected cards have room without shrinking text.
        if (compact && HeaderArea.Parent is Grid headerGrid)
        {
            headerGrid.Children.Remove(HeaderArea);
            ContentStack.Children.Insert(0, HeaderArea);
        }
        else if (!compact && HeaderArea.Parent is StackPanel headerStack)
        {
            headerStack.Children.Remove(HeaderArea);
            ((Grid)Sidebar.Child).Children.Add(HeaderArea);
        }

        if (compact && OperationPanel.Parent is Grid grid)
        {
            grid.Children.Remove(OperationPanel);
            ContentStack.Children.Add(OperationPanel);
        }
        else if (!compact && OperationPanel.Parent is StackPanel stack)
        {
            stack.Children.Remove(OperationPanel);
            ((Grid)Sidebar.Child).Children.Add(OperationPanel);
        }

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
