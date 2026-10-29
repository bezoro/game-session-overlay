using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using ApolloOverlay;

namespace Bezoro.NativeProof;

internal sealed class ProofWindows : IDisposable
{
    internal readonly Window Underlay, Overlay;
    internal readonly Border Marker = new()
    {
        Width = 30,
        Height = 30,
        Background = Brushes.LimeGreen,
        HorizontalAlignment = HorizontalAlignment.Right
    };
    internal readonly long Identity = OwnedNative.StartTicks;
    internal int ActiveRenderHandlers
    {
        get;
        private set;
    }
    internal bool HotkeyRegistered
    {
        get;
        private set;
    }
    internal int HotkeyError
    {
        get;
        private set;
    }
    internal int InputEvents
    {
        get;
        private set;
    }
    internal int Page
    {
        get;
        private set;
    }
    internal int Selection
    {
        get;
        private set;
    }

    internal Action? Exit;
    private readonly Button[] buttons = new Button[4];
    private readonly TextBlock heading = new()
    {
        Text = "Test window controls",
        FontSize = 26,
        FontWeight = FontWeights.SemiBold,
        Foreground = Brushes.White,
        TextWrapping = TextWrapping.Wrap
    };
    private readonly DispatcherTimer timer;
    private readonly ChordLatch chord;
    private PadInterpreter[] interpreters = [new(), new(), new(), new()];
    private int? inputSlot;
    private bool disposed;
    private readonly HwndSource source;
    private readonly TextBlock context = new()
    {
        Text = "Owned test app · no game session",
        FontSize = 16,
        Foreground = Brushes.LightGray,
        TextWrapping = TextWrapping.Wrap
    };
    internal nint UnderlayHandle => OwnedNative.Handle(Underlay);
    internal nint OverlayHandle => OwnedNative.Handle(Overlay);

    internal ProofWindows(ProofConfig config)
    {
        chord = new(config.Chord, config.HoldMs);
        var area = SystemParameters.WorkArea;
        Underlay = new Window
        {
            Title = "Native proof — owned test surface",
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            Width = Math.Min(1040, area.Width - 80),
            Height = Math.Min(830, area.Height - 80),
            Left = area.Left + 40,
            Top = area.Top + 40,
            ShowInTaskbar = true
        };
        SetPattern(false);
        var underGrid = new Grid();
        underGrid.Children.Add(new TextBlock { Text = "OWNED TEST SURFACE", FontSize = 23, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new(20) });
        Underlay.Content = underGrid;
        Underlay.KeyDown += OnKey;
        Underlay.Closed += (_, _) => Exit?.Invoke();
        Overlay = new Window
        {
            Title = "Native proof — overlay",
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Topmost = true,
            ShowInTaskbar = false,
            Width = Math.Min(480, Underlay.Width * .48),
            Height = Underlay.Height - 70,
            Left = Underlay.Left + 42,
            Top = Underlay.Top + 35
        };
        var surface = new Border
        {
            Margin = new(18),
            CornerRadius = new(24),
            Background = new SolidColorBrush(Color.FromArgb(222, 22, 27, 32)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(80, 225, 240, 245)),
            BorderThickness = new(1),
            Padding = new(24),
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 14,
                ShadowDepth = 3,
                Opacity = .65
            }
        };
        var grid = new Grid();
        foreach (var length in new[]
        {
            GridLength.Auto,
            GridLength.Auto,
            new GridLength(1, GridUnitType.Star),
            GridLength.Auto
        }

        )
            grid.RowDefinitions.Add(new()
            {
                Height = length
            });
        var title = new Grid();
        title.ColumnDefinitions.Add(new()
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        title.ColumnDefinitions.Add(new()
        {
            Width = GridLength.Auto
        });
        title.Children.Add(heading);
        Grid.SetColumn(Marker, 1);
        title.Children.Add(Marker);
        grid.Children.Add(title);
        context.Margin = new(0, 20, 0, 20);
        Grid.SetRow(context, 1);
        grid.Children.Add(context);
        var actions = new Grid();
        actions.ColumnDefinitions.Add(new());
        actions.ColumnDefinitions.Add(new());
        actions.RowDefinitions.Add(new());
        actions.RowDefinitions.Add(new());
        var labels = new[]
        {
            "Return",
            "Mark frame",
            "Switch tab",
            "Exit proof"
        };
        for (var i = 0; i < 4; i++)
        {
            var index = i;
            var button = new Button
            {
                Content = labels[i],
                Margin = new(5),
                Padding = new(10),
                FontSize = 19,
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromRgb(44, 51, 59)),
                BorderThickness = new(3),
                FocusVisualStyle = null,
                MinHeight = 72
            };
            button.Click += (_, _) =>
            {
                Selection = index;
                PaintSelection();
                Dispatch();
            };
            buttons[i] = button;
            Grid.SetRow(button, i / 2);
            Grid.SetColumn(button, i % 2);
            actions.Children.Add(button);
        }

        Grid.SetRow(actions, 2);
        grid.Children.Add(actions);
        var legend = new TextBlock
        {
            Text = "A select  ·  B return\nLB/RB tabs  ·  arrows / Tab navigate\nCtrl+Alt+Space or LB+RB+Start\nOwned test app · Guide not registered",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 15,
            Foreground = Brushes.LightGray,
            Margin = new(0, 18, 0, 0)
        };
        Grid.SetRow(legend, 3);
        grid.Children.Add(legend);
        surface.Child = grid;
        Overlay.Content = surface;
        Overlay.PreviewKeyDown += OnKey;
        Overlay.Deactivated += (_, _) => CancelInput();
        Underlay.Show();
        new WindowInteropHelper(Overlay).EnsureHandle();
        source = HwndSource.FromHwnd(UnderlayHandle);
        source.AddHook(Hook);
        HotkeyRegistered = OwnedNative.RegisterHotKey(UnderlayHandle, 7161, config.Modifiers, config.Key);
        HotkeyError = HotkeyRegistered ? 0 : Marshal.GetLastWin32Error();
        timer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        timer.Tick += Poll;
        timer.Start();
        PaintSelection();
    }

    internal void SetPattern(bool alternate)
    {
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(alternate ? Color.FromRgb(94, 67, 91) : Color.FromRgb(27, 59, 73)), null, new RectangleGeometry(new Rect(0, 0, 64, 64))));
        drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(alternate ? Color.FromRgb(155, 109, 85) : Color.FromRgb(73, 121, 127)), null, new RectangleGeometry(new Rect(0, 0, 32, 32))));
        drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(alternate ? Color.FromRgb(155, 109, 85) : Color.FromRgb(73, 121, 127)), null, new RectangleGeometry(new Rect(32, 32, 32, 32))));
        Underlay.Background = new DrawingBrush(drawing)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 64, 64),
            ViewportUnits = BrushMappingMode.Absolute
        };
    }

    internal bool Show()
    {
        if (!OwnedNative.IsOwned(UnderlayHandle, Identity) || !OwnedNative.IsOwned(OverlayHandle, Identity))
            return false;
        Overlay.Show();
        timer.Interval = TimeSpan.FromMilliseconds(30);
        Overlay.Activate();
        OwnedNative.SetForegroundWindow(OverlayHandle);
        PaintSelection();
        return OwnedNative.GetForegroundWindow() == OverlayHandle;
    }

    internal bool Hide()
    {
        var hadForeground = OwnedNative.GetForegroundWindow() == OverlayHandle;
        Overlay.Hide();
        timer.Interval = TimeSpan.FromMilliseconds(100);
        CancelInput();
        if (hadForeground && OwnedNative.IsOwned(UnderlayHandle, Identity))
        {
            Underlay.Activate();
            OwnedNative.SetForegroundWindow(UnderlayHandle);
        }

        return OwnedNative.GetForegroundWindow() == UnderlayHandle;
    }

    internal void CancelInput()
    {
        chord.Reset();
        interpreters = [new(), new(), new(), new()];
        inputSlot = null;
    }

    private nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0312 && wParam == 7161)
        {
            if (Overlay.IsVisible)
                Hide();
            else if (OwnedNative.GetForegroundWindow() == UnderlayHandle)
                Show();
            handled = true;
        }

        return 0;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (Overlay.IsVisible)
                Hide();
            else
                Exit?.Invoke();
            e.Handled = true;
            return;
        }

        if (!Overlay.IsVisible)
            return;
        var direction = e.Key switch
        {
            Key.Up => NavDirection.Up,
            Key.Down => NavDirection.Down,
            Key.Left => NavDirection.Left,
            Key.Right => NavDirection.Right,
            _ => NavDirection.None
        };
        if (direction != NavDirection.None)
        {
            Selection = ProofNavigation.Move(Selection, direction);
            PaintSelection();
            e.Handled = true;
        }
        else if (e.Key == Key.Tab)
        {
            Selection = (Selection + ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 3 : 1)) % 4;
            PaintSelection();
            e.Handled = true;
        }
        else if (e.Key is Key.Enter or Key.Space)
        {
            if (!e.IsRepeat)
                Dispatch();
            e.Handled = true;
        }
        else if (e.Key is Key.PageUp or Key.PageDown)
        {
            SetPage();
            e.Handled = true;
        }
    }

    private void Poll(object? sender, EventArgs args)
    {
        var frames = Enumerable.Range(0, 4).Select(i => Native.ReadPad((uint)i)).ToArray();
        var now = Environment.TickCount64;
        if (chord.Update(frames, now))
        {
            InputEvents++;
            if (Overlay.IsVisible)
                Hide();
            else if (OwnedNative.GetForegroundWindow() == UnderlayHandle)
                Show();
        }

        if (!Overlay.IsVisible || OwnedNative.GetForegroundWindow() != OverlayHandle)
            return;
        if (inputSlot is { } old && !frames[old].Connected)
        {
            CancelInput();
            return;
        }

        var events = frames.Select((frame, i) => interpreters[i].Update(frame, now)).ToArray();
        if (inputSlot == null)
        {
            for (var i = 0; i < 4; i++)
                if (frames[i].Connected && (frames[i].Buttons != 0 || Math.Abs((int)frames[i].X) > 10000 || Math.Abs((int)frames[i].Y) > 10000))
                {
                    inputSlot = i;
                    break;
                }
        }

        if (inputSlot is not { } slot)
            return;
        var input = events[slot];
        if (input.Direction != 0)
        {
            Selection = ProofNavigation.Move(Selection, (NavDirection)input.Direction);
            PaintSelection();
            InputEvents++;
        }

        if ((input.Pressed & PadButtons.B) != 0)
        {
            InputEvents++;
            Hide();
            return;
        }

        if ((input.Pressed & (PadButtons.LB | PadButtons.RB)) != 0)
        {
            InputEvents++;
            SetPage();
        }

        if ((input.Pressed & PadButtons.A) != 0)
        {
            InputEvents++;
            Dispatch();
        }
    }

    private void SetPage()
    {
        Page = 1 - Page;
        heading.Text = Page == 0 ? "Test window controls" : "Proof diagnostics";
    }

    private void PaintSelection()
    {
        for (var i = 0; i < 4; i++)
            buttons[i].BorderBrush = i == Selection ? Brushes.LimeGreen : Brushes.Transparent;
        if (Overlay.IsVisible && OwnedNative.GetForegroundWindow() == OverlayHandle)
            buttons[Selection].Focus();
    }

    internal void Dispatch()
    {
        switch (Selection)
        {
            case 0:
                Hide();
                break;
            case 1:
                Marker.Background = Marker.Background == Brushes.LimeGreen ? Brushes.DeepSkyBlue : Brushes.LimeGreen;
                break;
            case 2:
                SetPage();
                break;
            case 3:
                Exit?.Invoke();
                break;
        }
    }

    internal async Task<double> NextFrame(Action change)
    {
        var ready = new TaskCompletionSource<double>();
        var clock = Stopwatch.StartNew();
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            CompositionTarget.Rendering -= handler;
            ActiveRenderHandlers--;
            ready.TrySetResult(clock.Elapsed.TotalMilliseconds);
        };
        CompositionTarget.Rendering += handler;
        ActiveRenderHandlers++;
        try
        {
            change();
            return await ready.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            if (!ready.Task.IsCompleted)
            {
                CompositionTarget.Rendering -= handler;
                ActiveRenderHandlers--;
            }
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        timer.Stop();
        timer.Tick -= Poll;
        source.RemoveHook(Hook);
        if (HotkeyRegistered)
            OwnedNative.UnregisterHotKey(UnderlayHandle, 7161);
        HotkeyRegistered = false;
        Exit = null;
        Overlay.Close();
        Underlay.Close();
    }
}
