using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using ApolloOverlay;

internal static class AudioClarityTests
{
    internal static void Run(Action<string, Action> test)
    {
        test("unavailable Windows audio offers recovery instead of active adjustment prompts in every input mode", () =>
        {
            using var host = CreateHost(new Devices());
            var window = host.Window;
            window.SetRenderState("audio");
            foreach (var mode in new[] { "controller", "keyboard", "mouse" })
            {
                UseInput(window, mode);
                foreach (var id in new[] { "volume-down", "volume", "mute" })
                {
                    var button = WpfTestElements.Button(window, id);
                    var description = InputPrompt.Read(((ActionButtonContent)button.Content).Description);
                    Require(!button.IsEnabled && !description.Contains("5%") && !description.Contains("adjusts"), "Unavailable control advertised an active adjustment: " + id + "/" + mode);
                }
                var volumeHelp = AutomationProperties.GetHelpText(WpfTestElements.Button(window, "volume"));
                Require(volumeHelp.Contains("Audio devices") && volumeHelp.Contains("not been queried"), "Volume recovery lost its action or underlying unavailable reason");
            }
            var back = WpfTestElements.Button(window, "back");
            Require(AutomationProperties.GetHelpText(back) == "Return to the session panel", "Back still presents unrelated audio state");
        });
        test("unavailable mixer recovery survives input-source changes", () =>
        {
            using var host = CreateHost(new Devices());
            var window = host.Window;
            window.SetRenderState("audio");
            Click(WpfTestElements.Button(window, "session-mixer"));
            WpfTestDispatcher.Complete(window.FeatureTask);
            var app = WpfTestElements.Descendants<Button>(window).First(button => button.Tag is string id && id.StartsWith("mix:", StringComparison.Ordinal));
            Click(app);
            WpfTestDispatcher.Complete(window.FeatureTask);
            foreach (var mode in new[] { "controller", "keyboard", "mouse" })
            {
                UseInput(window, mode);
                var volume = WpfTestElements.Button(window, "mixer-volume");
                var text = InputPrompt.Read(((ActionButtonContent)volume.Content).Description);
                Require(!volume.IsEnabled && text.Contains("Refresh apps") && !text.Contains("adjust"), "Input adaptation restored a misleading mixer instruction: " + mode);
            }
        });
        test("master-audio availability changes preserve selection and editor identity while updating action admission", () =>
        {
            using var host = CreateHost(new Devices(), TestFocusMode.Native);
            var window = host.Window;
            window.SetRenderState("audio");
            var button = WpfTestElements.Button(window, "volume");
            var content = (ActionButtonContent)button.Content;
            var slider = content.EditorSlider ?? throw new Exception("Volume has no retained editor");
            var focusHost = window.FindActionHost(button);
            var available = new AudioSnapshot("owned-fixture", .45f, false, "Observed output");
            window.UpdateAudioEntries(available);
            Pick(window, "volume");
            Click(button);
            WpfTestDispatcher.Complete(window.OperationTask);
            Require(window.Controller.Operation is { Action: "audio" }, "An available row did not admit its action");
            // The render-only audio owner remains stopped; this test verifies UI admission,
            // selection and retained controls, not a physical endpoint mutation.
            var previousOperation = window.Controller.Operation;
            Require(window.SelectedAction == "volume", "PRECONDITION: admitted audio action moved selection before the availability update");
            Require(button.IsKeyboardFocusWithin, "PRECONDITION: the available volume action does not own actual keyboard focus");
            window.UpdateAudioEntries(new(null, 0, false, "Owned output was disconnected."));
            Require(!button.IsEnabled, "Unavailable volume retained enabled action state");
            Require(!slider.IsEnabled, "Unavailable volume retained enabled slider state");
            Require(slider.Visibility == Visibility.Collapsed, "Unavailable volume retained its adjustment slider");
            Require(window.SelectedAction == "volume", "Loss of availability moved selection to " + window.SelectedAction);
            Require(ReferenceEquals(WpfTestElements.Button(window, "volume"), button)
                && ReferenceEquals(window.FindActionHost(button), focusHost), "Loss of availability replaced the action or recovery host");
            Require(focusHost?.IsKeyboardFocusWithin == true, "Disabled selection did not transfer owned keyboard focus to its recovery host");
            Click(button);
            Require(ReferenceEquals(previousOperation, window.Controller.Operation) && window.Feedback.Contains("disconnected"), "An unavailable row admitted its old action or lost its actual reason");
            window.UpdateAudioEntries(available with
            {
                Volume = .65f
            });
            Require(button.IsEnabled && slider.IsEnabled && slider.Visibility == Visibility.Visible && Math.Abs(slider.Value - .65) < .001
                && window.SelectedAction == "volume" && ReferenceEquals(WpfTestElements.Button(window, "volume"), button)
                && ReferenceEquals(((ActionButtonContent)button.Content).EditorSlider, slider), "Reconnection replaced the editor, changed selection or retained disabled state");
            Click(button);
            WpfTestDispatcher.Complete(window.OperationTask);
            Require(window.Controller.Operation is { Action: "audio" } && !ReferenceEquals(previousOperation, window.Controller.Operation), "A recovered row retained stale unavailable action routing");
        });
        test("audio availability cannot restore an obsolete selection after a synchronous view replacement", () =>
        {
            using var host = CreateHost(new Devices());
            var window = host.Window;
            window.SetRenderState("audio");
            window.UpdateAudioEntries(new("owned-fixture", .45f, false, "Observed output"));
            var button = WpfTestElements.Button(window, "volume");
            Pick(window, "volume");
            var replaced = false;
            button.IsEnabledChanged += (_, args) =>
            {
                if (args.NewValue is false)
                {
                    replaced = true;
                    window.Page(1);
                }
            };
            window.UpdateAudioEntries(new(null, 0, false, "Owned output was disconnected."));
            Require(replaced && window.ActionIds.Contains("refresh-apps") && !window.ActionIds.Contains("volume")
                && window.SelectedAction != "volume", "Availability recovery overwrote a newer view with its old audio selection");
        });
        test("duplicate device subtitles collapse initially and after live updates without discarding accessibility help", () =>
        {
            var devices = new Devices
            {
                State = new([new("output", "Output unavailable")], "output", "output", "microphone", "Microphone · on", false, "Observed devices")
            };
            using var host = CreateHost(devices);
            var window = host.Window;
            window.RefreshSessionFeatures();
            WpfTestDispatcher.Complete(window.FeatureTask);
            window.RefreshRows();
            var microphone = WpfTestElements.Button(window, "microphone");
            var output = WpfTestElements.Button(window, "windows-volume");
            var body = (ActionButtonContent)microphone.Content;
            Require(body.Description.Visibility == Visibility.Collapsed, "Initial device subtitle repeats its title");
            Require(AutomationProperties.GetHelpText(microphone) == "Microphone · on", "Hidden duplicate discarded accessible device metadata");
            Require(InputPrompt.Read(((ActionButtonContent)output.Content).Description).Contains("Audio devices"), "Unavailable output has no useful recovery description");
            devices.State = devices.State with
            {
                MicrophoneName = "Desk microphone"
            };
            window.RefreshSessionFeatures();
            WpfTestDispatcher.Complete(window.FeatureTask);
            Require(ReferenceEquals(WpfTestElements.Button(window, "microphone"), microphone) && body.Description.Visibility == Visibility.Visible
                && InputPrompt.Read(body.Description) == "Desk microphone", "A distinct live device name stayed hidden or rebuilt the action");
            devices.State = devices.State with
            {
                MicrophoneMuted = true,
                MicrophoneName = "Microphone · muted"
            };
            window.RefreshSessionFeatures();
            WpfTestDispatcher.Complete(window.FeatureTask);
            Require(body.Description.Visibility == Visibility.Collapsed && AutomationProperties.GetHelpText(microphone) == "Microphone · muted", "A live title/state update restored duplicate visual metadata");
            Require(AutomationProperties.GetName(window.FindActionHost(microphone)!) == "Microphone · muted", "Focus recovery announces the same state twice");
        });
    }

    private static WpfTestHost CreateHost(Devices devices, TestFocusMode focusMode = TestFocusMode.Synthetic)
    {
        var host = WpfTestHost.Create(diagnostics => new MainWindow(renderOnly: true,
            platform: new PreviewPlatform(new(101, 42, 100, "Owned audio window", "fixture")),
            diagnostics: diagnostics, systemAudioBackend: devices), focusMode,
            initialize: window => window.PhysicalKeyReader = _ => 0);
        try
        {
            host.Refresh();
            if (focusMode == TestFocusMode.Native)
            {
                host.Window.OpenPanel();
                WpfTestDispatcher.Complete(host.Window.OpeningTask);
                Require(host.Window.IsVisible && host.Window.IsActive
                    && Native.GetForegroundWindow() == new WindowInteropHelper(host.Window).Handle,
                    "PRECONDITION: the owned audio fixture does not have actual native foreground");
            }
            else
                host.Window.Show();
            return host;
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    private static void UseInput(MainWindow window, string mode)
    {
        if (mode == "controller")
            AdaptiveInterfaceTests.UseController(window);
        else if (mode == "mouse")
        {
            window.ObservePointerPosition(new(0, 0));
            window.ObservePointerPosition(new(100, 100));
        }
        else
        {
            foreach (var route in new[] { Keyboard.PreviewKeyDownEvent, Keyboard.PreviewKeyUpEvent })
                window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount, Key.F12) { RoutedEvent = route });
        }
    }

    private static void Pick(MainWindow window, string id)
    {
        for (var attempts = 0; attempts < 80 && window.SelectedAction != id; attempts++)
            window.Sequential(1);
        Require(window.SelectedAction == id, "Could not select owned action " + id);
    }
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class Devices : ISystemAudioBackend
    {
        internal SystemAudioSnapshot State = SystemAudioSnapshot.Empty;
        public SystemAudioSnapshot Read() => State;
        public ActionResult SetMicrophoneMuted(string expectedId, bool muted, Func<bool> continuing) => throw new InvalidOperationException("Presentation tests must not mutate a microphone");
        public ActionResult SetOutputs(string expectedConsole, string expectedMedia, string console, string media, Func<bool> continuing) => throw new InvalidOperationException("Presentation tests must not change audio outputs");
    }
}
