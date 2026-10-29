using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ApolloOverlay;

internal static class MotionAndControllerTests
{
    internal static void Run(Action<string, Action> test)
    {
        test("selection breathing and sheen preserve geometry and stop on focus change and hide", () =>
        {
            using var host = new Host();
            var w = host.Window;
            var button = (Button)w.FindName("SessionCardButton");
            button.ApplyTemplate();
            var breath = (Border)button.Template.FindName("SelectionBreath", button);
            var sheen = (Border)button.Template.FindName("SelectionSheen", button);
            var label = (TextBlock)w.FindName("TargetLabel");
            var before = label.TranslatePoint(new Point(), w);
            Require(InteractionMotion.GetIsSelected(button) == InteractionMotion.Allowed, "Visible selection did not follow the motion policy");
            Require(breath.HasAnimatedProperties == InteractionMotion.Allowed && sheen.Background.RelativeTransform.HasAnimatedProperties == InteractionMotion.Allowed, "Selection effects did not start");
            Pump(180);
            Require(label.TranslatePoint(new Point(), w) == before, "Breathing changed text layout");
            w.Sequential(1);
            Pump(10);
            Require(!breath.HasAnimatedProperties && !sheen.Background.RelativeTransform.HasAnimatedProperties, "Previous selection kept its animation clocks");
            Pick(w, "session-card");
            w.Hide();
            Pump(10);
            Require(!InteractionMotion.GetIsSelected(button) && !breath.HasAnimatedProperties && !sheen.Background.RelativeTransform.HasAnimatedProperties, "Hidden overlay kept selection animation clocks alive");
        });
        test("press animation returns to neutral and obsolete motion preferences do not suppress it", () =>
        {
            using var host = new Host();
            var w = host.Window;
            w.Controller.Change(w.Controller.Settings with
            {
                ReducedMotion = true
            });
            w.Page(2);
            Require(!w.ActionIds.Contains("motion"), "Removed reduced-motion setting remained in the interface");
            var button = (Button)w.FindName("SettingsGear");
            InteractionMotion.Press(button);
            if (InteractionMotion.Allowed)
            {
                var scale = ((TransformGroup)button.RenderTransform).Children.OfType<ScaleTransform>().Single();
                Require(scale.HasAnimatedProperties, "Press did not animate");
                Pump(280);
                Require(!scale.HasAnimatedProperties && scale.ScaleX == 1 && scale.ScaleY == 1, "Press scale remained stuck");
                InteractionMotion.Press(button);
                w.Hide();
                Require(!scale.HasAnimatedProperties, "Hidden press retained its clocks");
            }
        });
        test("controller badge uses model icon and measured battery without guessing XInput percentages", () =>
        {
            using var host = new Host();
            var w = host.Window;
            host.Source.Items = [new("xbox", "Xbox One", ControllerFamily.Xbox, true, 76, false)];
            Connect(w, 0);
            Complete(w.ControllerBadgeTask);
            var label = (TextBlock)w.FindName("ControllerModelLabel");
            var battery = (TextBlock)w.FindName("ControllerBatteryLabel");
            var icon = (VectorIcon)w.FindName("ControllerModelIcon");
            Require(label.Text == "Xbox One" && battery.Text == "76%" && icon.Data is not null, "Model or measured charge not presented");
            var xbox = icon.Data!.ToString();
            var peer = AutomationProperties.GetName((DependencyObject)w.FindName("ControllerInfoBar"));
            Require(peer.Contains("76 percent") && peer.Contains("Windows"), "Badge has no accessible charge or source");
            Disconnect(w, 1);
            host.Source.Items = [new("ps5", "PlayStation 5", ControllerFamily.PlayStation5, true, null, true)];
            Connect(w, 2);
            Complete(w.ControllerBadgeTask);
            Connect(w, 3);
            Complete(w.ControllerBadgeTask);
            Require(label.Text == "PlayStation 5" && battery.Text == "—" && icon.Data.ToString() != xbox, "PlayStation fallback guessed a charge or reused the Xbox icon");
        });
        test("controller badge keeps a long model and charging icon inside a compact header", () =>
        {
            using var host = new Host();
            var w = host.Window;
            w.Width = 360;
            w.Height = 480;
            host.Source.Items = [new("long", "Wireless controller with a deliberately long manufacturer name", ControllerFamily.PlayStation5, true, 24, true)];
            Connect(w, 0);
            Complete(w.ControllerBadgeTask);
            w.UpdateLayout();
            var badge = (FrameworkElement)w.FindName("ControllerInfoBar");
            var gear = (FrameworkElement)w.FindName("SettingsGear");
            var power = (VectorIcon)w.FindName("ControllerPowerGlyph");
            var label = (TextBlock)w.FindName("ControllerModelLabel");
            var right = badge.TranslatePoint(new(badge.ActualWidth, 0), w).X;
            var gearLeft = gear.TranslatePoint(new(), w).X;
            Require(right <= gearLeft + 1 && label.ActualWidth > 0 && label.TextTrimming == TextTrimming.CharacterEllipsis, "Long controller model overlapped the system button or disappeared");
            Require(power.Visibility == Visibility.Visible && power.Data is not null, "Charging state has no vector icon");
        });
        test("controller badge does not assign ambiguous models and makes no hidden metadata requests", () =>
        {
            using var host = new Host();
            var w = host.Window;
            host.Source.Items = [new("one", "Xbox One", ControllerFamily.Xbox, true, 90, false), new("two", "PlayStation 4", ControllerFamily.PlayStation4, true, 40, false)];
            Connect(w, 0);
            Complete(w.ControllerBadgeTask);
            Require(((TextBlock)w.FindName("ControllerModelLabel")).Text == "2 controllers" && !((TextBlock)w.FindName("ControllerBatteryLabel")).Text.Contains('%'), "Ambiguous devices were assigned to an XInput slot");
            var reads = host.Source.Reads;
            w.Hide();
            Connect(w, 10000);
            Complete(w.ControllerBadgeTask);
            Require(host.Source.Reads == reads, "Hidden overlay queried Windows controller metadata");
        });
        test("controller metadata recognizes Sony generations and rejects invalid capacity reports", () =>
        {
            Require(WindowsControllerIdentitySource.Describe(0x054c, 0x09cc, "Wireless").Family == ControllerFamily.PlayStation4
                && WindowsControllerIdentitySource.Describe(0x054c, 0x0ce6, "Wireless").Family == ControllerFamily.PlayStation5, "Sony generations were guessed from the generic display name");
            Require(WindowsControllerIdentitySource.Percent(520, 1000) == 52
                && WindowsControllerIdentitySource.Percent(1, 0) is null
                && WindowsControllerIdentitySource.Percent(120, 100) is null
                && WindowsControllerIdentitySource.Percent(null, null) is null, "Missing or inconsistent capacities produced a false percentage");
        });
        foreach (var width in new[]
        {
            360,
            572
        }

        )
        {
            var size = width;
            test($"vector icons retain source padding and fit selected buttons at {size}", () =>
            {
                using var host = new Host();
                var w = host.Window;
                w.Width = size;
                w.Height = size == 360 ? 480 : 940;
                Pick(w, "settings");
                w.UpdateLayout();
                var gear = (Button)w.FindName("SettingsGear");
                var icon = (VectorIcon)gear.Content;
                var bounds = icon.TransformToAncestor(gear).TransformBounds(new Rect(new Size(icon.ActualWidth, icon.ActualHeight)));
                var left = gear.Padding.Left + gear.BorderThickness.Left;
                var right = gear.ActualWidth - gear.Padding.Right - gear.BorderThickness.Right;
                Require(bounds.Left >= left - .5 && bounds.Right <= right + .5 && Math.Abs(bounds.Left - (gear.ActualWidth - bounds.Right)) < 1, "Selected system icon was clipped or off-center");
                foreach (var vector in PremiumInterfaceTests.Descendants<VectorIcon>(w))
                {
                    if (vector.Data is null || vector.Visibility != Visibility.Visible)
                        continue;
                    var source = new Rect(new Point(), vector.SourceSize);
                    source.Inflate(.1, .1);
                    Require(source.Contains(vector.Data.Bounds), "Vector exceeds its declared source frame");
                }

                Require(ReferenceEquals(icon.Data, w.FindResource("Icon.settings")), "System icon is not the licensed source vector");
            });
        }

        test("controller presentation prefers measured charge over categorical and USB estimates", () =>
        {
            var xinput = new ControllerDeviceSnapshot(0, 1, true, ControllerBatteryType.Alkaline, ControllerBatteryLevel.Low, ControllerVibrationCapability.Unknown, 0, "");
            var measured = ControllerBadgeModel.Create(1, [new("one", "Xbox One", ControllerFamily.Xbox, true, 76, false)], xinput, "");
            Require(measured.BatteryText == "76%" && !measured.LowBattery && Math.Abs(measured.BatteryFraction - .76) < .001, "Categorical XInput overrode a measured charge");
            var wired = ControllerBadgeModel.Create(1, [new("usb", "Xbox", ControllerFamily.Xbox, false, null, false)], xinput, "");
            Require(wired.Power == ControllerPowerState.Wired && wired.BatteryText == "Battery N/A" && wired.BatteryFraction == 0 && !wired.LowBattery, "USB power guessed a battery charge");
        });
        test("unknown controller battery is not displayed as an empty battery", () =>
        {
            using var host = new Host();
            var window = host.Window;
            host.Source.Items = [];
            Connect(window, 0);
            Complete(window.ControllerBadgeTask);
            Require(((TextBlock)window.FindName("ControllerBatteryLabel")).Text == "Battery N/A", "Unknown charge has no clear availability label");
            Require(((FrameworkElement)window.FindName("ControllerBatteryMeter")).Visibility == Visibility.Collapsed, "Unknown charge looks like zero battery");
            Require(((VectorIcon)window.FindName("ControllerPowerGlyph")).Visibility == Visibility.Visible, "Unavailable charge has no information icon");
        });
        test("Windows controller metadata projection loads through a read-only query", () =>
        {
            var read = Task.Run(() => new WindowsControllerIdentitySource().Read());
            Complete(read);
            Require(read.Result.Count <= 8 && read.Result.All(item => item.Id.Length > 0), "Metadata query returned an unbounded or unidentified device");
        });
    }

    private static void Connect(MainWindow w, long now) => w.UpdateControllerTelemetry([new(true, 0), default, default, default], 0, now);
    private static void Disconnect(MainWindow w, long now) => w.UpdateControllerTelemetry([default, default, default, default], null, now);
    private static void Pick(MainWindow w, string id)
    {
        for (var i = 0; i <= w.ActionIds.Count && w.SelectedAction != id; i++)
            w.Sequential(1);
        Require(w.SelectedAction == id, "Selection unavailable");
        w.UpdateLayout();
    }

    private static void Require(bool value, string message)
    {
        if (!value)
            throw new Exception(message);
    }

    private static void Complete(Task task) => ReviewFixTests.Complete(task);
    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(ms)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private sealed class IdentityFixture : IControllerIdentitySource
    {
        internal IReadOnlyList<ControllerIdentity> Items = [];
        internal int Reads;
        public IReadOnlyList<ControllerIdentity> Read()
        {
            Interlocked.Increment(ref Reads);
            return Items;
        }
    }

    private sealed class Host : IDisposable
    {
        private readonly SynchronizationContext? previous = SynchronizationContext.Current;
        private readonly string root = Path.Combine(Path.GetTempPath(), "OverlayMotionBadge", Guid.NewGuid().ToString("N"));
        internal readonly IdentityFixture Source = new();
        internal readonly MainWindow Window;
        internal Host()
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var app = Application.Current ?? new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
            if (app.TryFindResource(ThemeSupport.Card) is null)
                ThemeSupport.ForceForValidation(app, false);
            Window = new(renderOnly: true, platform: new PreviewPlatform(), diagnostics: new(root), controllerIdentitySource: Source);
            Window.PhysicalKeyReader = _ => 0;
            Window.DpiReader = _ => 96;
            Window.Left = -12000;
            Window.Top = -12000;
            Window.ShowDiagnostic();
            Window.UpdateLayout();
        }

        public void Dispose()
        {
            try
            {
                Window.Close();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
        }
    }
}
