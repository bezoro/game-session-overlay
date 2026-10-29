using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using ApolloOverlay;

internal static class AdaptiveInterfaceTests
{
    internal static void Run(Action<string, Action> test)
    {
        test("compact uncertain result remains pinned with its concise recovery and full details", () =>
        {
            var w = ReviewFixTests.Window();
            try
            {
                w.Width = 360;
                w.Height = 600;
                w.ShowDiagnostic();
                w.Controller.SetOperation("Unconfirmed", "The full window identity observation and recovery instructions remain available for reading.", summary: "Select window again.");
                w.RefreshRows();
                Arrange(w, 360, 600);
                var panel = (Border)w.FindName("OperationPanel");
                var scroll = (ScrollViewer)w.FindName("ContentScroll");
                var footer = (FrameworkElement)w.FindName("FooterArea");
                var root = (FrameworkElement)w.Content;
                var bounds = panel.TransformToAncestor(root).TransformBounds(new Rect(new Size(panel.ActualWidth, panel.ActualHeight)));
                Require(panel.Visibility == Visibility.Visible && !scroll.IsAncestorOf(panel), "Compact result can scroll out of view");
                Require(bounds.Bottom <= footer.TranslatePoint(new Point(), root).Y && bounds.Top >= 0, "Result overlaps footer or leaves the viewport");
                Require(((TextBlock)w.FindName("OperationLabel")).Text == "Select window again.", "Recovery summary is absent or replaced by the full paragraph");
                Click(w, "operation-details");
                Require(PremiumInterfaceTests.Descendants<TextBox>((DependencyObject)w.FindName("Rows")).Single().Text == w.Controller.Operation!.Message, "Concise notice lost the complete result");
            }
            finally
            {
                w.Close();
            }
        });
        test("routine acknowledgements never become navigation stops or move content", () =>
        {
            var w = ReviewFixTests.Window();
            try
            {
                w.ShowDiagnostic();
                Arrange(w);
                var scroll = (ScrollViewer)w.FindName("ContentScroll");
                var viewport = scroll.ViewportHeight;
                var selected = w.SelectedAction;
                var focused = Keyboard.FocusedElement;
                w.Controller.SetOperation("Completed", "Settings saved.");
                w.RefreshRows();
                Arrange(w);
                Require(w.SelectedAction == selected && ReferenceEquals(Keyboard.FocusedElement, focused), "Acknowledgement stole the selected action or native keyboard focus");
                Require(!w.ActionIds.Contains("operation-details") && !w.ActionIds.Contains("dismiss-operation"), "Routine result added footer navigation stops");
                Require(((Button)w.FindName("OperationDetailsButton")).Visibility == Visibility.Collapsed, "Routine result retained a details button");
                Require(((TextBlock)w.FindName("OperationLabel")).TextWrapping == TextWrapping.NoWrap, "Acknowledgement can grow into a multi-line card");
                Require(((Border)w.FindName("OperationPanel")).BorderThickness == new Thickness(0) && scroll.ViewportHeight == viewport, "Acknowledgement introduced a frame or shifted the content viewport");
                w.RefreshOperationNotice(Environment.TickCount64 + 5000);
                Require(((Border)w.FindName("OperationPanel")).Visibility == Visibility.Hidden && w.Controller.Operation is not null, "Expiry erased the result or kept its surface visible");
                w.RefreshRows();
                Require(((Border)w.FindName("OperationPanel")).Visibility == Visibility.Hidden, "A passive refresh resurrected an expired acknowledgement");
                w.Page(2);
                Click(w, "last-operation");
                Require(w.ActionIds.Contains("operation-text"), "Expired result is unavailable from System");
            }
            finally
            {
                w.Close();
            }
        });
        test("uncertain results remain readable and cannot be expired by an old acknowledgement", () =>
        {
            var w = ReviewFixTests.Window();
            try
            {
                w.Controller.SetOperation("Completed", "Old acknowledgement");
                w.RefreshRows();
                w.RefreshOperationNotice(Environment.TickCount64 + 5000);
                w.Controller.SetOperation("Unconfirmed", "Check the selected window before trying again.");
                var result = w.Controller.Operation;
                w.RefreshRows();
                w.RefreshOperationNotice(Environment.TickCount64 + 60000);
                Require(ReferenceEquals(result, w.Controller.Operation) && ((Border)w.FindName("OperationPanel")).Visibility == Visibility.Visible, "Expired acknowledgement hid or replaced a newer uncertain result");
                Require(w.ActionIds.Contains("operation-details") && w.ActionIds.Contains("dismiss-operation"), "Uncertain result lost its recovery controls");
                Click(w, "operation-details");
                Require(PremiumInterfaceTests.Descendants<TextBox>((DependencyObject)w.FindName("Rows")).Single().Text == result!.Message, "Full result was truncated or replaced");
            }
            finally
            {
                w.Close();
            }
        });
        test("notice fading preserves requested and pending semantics", () =>
        {
            var state = new OperationNoticeState();
            var now = 1000L;
            state.Update(new("Requested", "Request accepted", "fixture", null, true), now);
            Require(state.Visible && !state.AllowsActions && state.Opacity(now + 100) is > 0 and < 1, "Requested acknowledgement did not fade in");
            Require(state.Opacity(now + 5900) is > 0 and < 1, "Requested acknowledgement did not fade out");
            state.Update(state.Operation, now + 6000);
            Require(!state.Visible && state.Operation!.State == "Requested", "Requested outcome became completed or disappeared from retained state");
            state.Update(new("Pending", "Waiting for observation", "fixture", null, true), now + 6000);
            state.Update(state.Operation, now + 90000);
            Require(state.Visible && state.AllowsActions, "An ongoing observation was silently expired");
        });
        test("home prioritizes the session and customized quick access before utility controls", () =>
        {
            var w = ReviewFixTests.Window();
            try
            {
                w.Controller.Change(w.Controller.Settings with
                {
                    FavoriteQuickActions = ["steam", "audio", "playnite"]
                });
                w.RefreshRows();
                var ids = w.ActionIds.ToList();
                Require(ids.IndexOf("session-card") < ids.IndexOf("steam"), "Quick access precedes the game");
                Require(ids.IndexOf("steam") < ids.IndexOf("audio") && ids.IndexOf("audio") < ids.IndexOf("playnite"), "Customized quick access order changed");
                Require(ids.IndexOf("playnite") < ids.IndexOf("apps") && ids.IndexOf("apps") < ids.IndexOf("session-volume"), "Secondary tools interrupt the game-to-quick-access sequence");
            }
            finally
            {
                w.Close();
            }
        });
        test("input hints switch controller pointer and keyboard without replacing the selected editor", () =>
        {
            var w = ReviewFixTests.Window();
            try
            {
                w.ShowDiagnostic();
                UseController(w);
                Require(InputPrompt.Read((TextBlock)w.FindName("ModeLabel")).Contains("LB/RB"), "Controller activity did not update navigation prompts");
                Require(new[] { "A", "B", "LB", "RB", "Dpad" }.All(key => Glyphs((TextBlock)w.FindName("ModeLabel")).Contains(key)), "Controller instructions did not render button vectors");
                w.Page(2);
                var shortcut = ReviewFixTests.Button(w, "shortcut");
                Require(InputPrompt.Read(((ActionButtonContent)shortcut.Content).Description).StartsWith("A switches"), "Controller hint leaked keyboard-only instructions");
                var selected = w.SelectedAction;
                var focused = Keyboard.FocusedElement;
                w.ObservePointerPosition(new(100, 100));
                w.ObservePointerPosition(new(101, 101));
                Require(InputPrompt.Read((TextBlock)w.FindName("ModeLabel")).Contains("LB/RB"), "Pointer jitter replaced controller prompts");
                w.ObservePointerPosition(new(120, 100));
                Require(InputPrompt.Read((TextBlock)w.FindName("ModeLabel")).Contains("Left click") && InputPrompt.Read(((ActionButtonContent)shortcut.Content).Description).StartsWith("Left click"), "Deliberate pointer activity did not update contextual prompts");
                Require(Glyphs((TextBlock)w.FindName("ModeLabel")).Contains("MouseLeft") && !Glyphs((TextBlock)w.FindName("ModeLabel")).Contains("A"), "Mouse instructions retained controller glyphs");
                Require(w.SelectedAction == selected && ReferenceEquals(Keyboard.FocusedElement, focused) && ReferenceEquals(shortcut, ReviewFixTests.Button(w, "shortcut")), "Changing input mode moved selection, focus or rebuilt controls");
                var source = PresentationSource.FromVisual(w)!;
                w.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.LeftShift) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                Require(InputPrompt.Read((TextBlock)w.FindName("ModeLabel")).Contains("Enter") && InputPrompt.Read(((ActionButtonContent)shortcut.Content).Description).StartsWith("Enter"), "Keyboard activity did not restore keyboard prompts");
                Require(new[] { "Enter", "Esc", "Arrows", "Tab", "PgUp", "PgDn" }.All(key => Glyphs((TextBlock)w.FindName("ModeLabel")).Contains(key)), "Keyboard instructions did not render keycap vectors");
            }
            finally
            {
                w.Close();
            }
        });
        test("selection preserves operational feedback and does not duplicate action descriptions", () =>
        {
            var w = ReviewFixTests.Window();
            try
            {
                w.ShowDiagnostic();
                var status = w.Feedback;
                for (var index = 0; index < w.ActionIds.Count; index++)
                {
                    w.Sequential(1);
                    Require(w.Feedback == status, "Selecting an action replaced operational feedback with its description");
                }
                var playnite = ReviewFixTests.Button(w, "playnite");
                Require(playnite.ToolTip is null && AutomationProperties.GetName(playnite) == "Playnite" && AutomationProperties.GetHelpText(playnite).Length > 0, "Removing duplicate tooltips lost accessible action information");
            }
            finally
            {
                w.Close();
            }
        });
        foreach (var display in new[]
        {
            new DisplayRef("small", 0, 0, 1280, 720, true),
            new DisplayRef("desktop", -2560, -160, 2560, 1400, false),
            new DisplayRef("tv", 0, 0, 3840, 2160, true),
            new DisplayRef("portrait", -1440, 0, 1440, 2520, false),
            new DisplayRef("ultrawide", 0, 0, 5120, 1400, true)
        })
            test("automatic monitor presentation preserves readable content and work area " + display.Id, () =>
            {
                foreach (var dpi in new[] { 96d, 120, 144, 192, 240, 288 })
                    foreach (var percent in new[] { 100, 115, 130, 150 })
                        foreach (var desktop in new[] { false, true })
                        {
                            var plan = SidebarLayout.Calculate(display, dpi, percent, "Right", desktop);
                            var bounds = plan.Bounds;
                            Require(bounds.X >= display.X && bounds.Y >= display.Y && bounds.X + bounds.Width <= display.X + display.Width && bounds.Y + bounds.Height <= display.Y + display.Height, "Panel escaped the monitor work area");
                            var unit = plan.DpiScale * plan.ContentScale;
                            if (display.Width - 32 * plan.DpiScale >= 360 * unit)
                                Require(bounds.Width / unit >= 359, "Width percentage cap squeezed readable content below 360 DIPs");
                            Require(plan.ContentScale >= percent / 100d && plan.DpiScale == dpi / 96, "Automatic presentation lost Windows DPI or user size preference");
                        }
            });
        test("invalid monitor DPI falls back to finite bounded presentation", () =>
        {
            var display = new DisplayRef("fallback", 0, 0, 1920, 1040, true);
            foreach (var dpi in new[] { 0d, double.NaN, double.PositiveInfinity, -1 })
            {
                var plan = SidebarLayout.Calculate(display, dpi, 100, "Left");
                Require(plan.DpiScale == 1 && double.IsFinite(plan.ContentScale) && plan.Bounds.Width > 0 && plan.Bounds.Height > 0, "Unavailable DPI produced invalid geometry");
            }
        });
    }

    internal static void UseController(MainWindow window)
    {
        window.ReviewTick([new(true, 0), default, default, default]);
        window.ReviewTick([new(true, PadButtons.Start), default, default, default]);
        window.ReviewTick([new(true, 0), default, default, default]);
    }

    private static void Arrange(MainWindow window, int width = 572, int height = 940)
    {
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
    }

    private static void Click(MainWindow window, string id) => ReviewFixTests.Button(window, id).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private static string[] Glyphs(TextBlock label) => label.Inlines.OfType<InlineUIContainer>()
        .Select(inline => ((VectorIcon)inline.Child).Tag as string ?? "").ToArray();
    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
