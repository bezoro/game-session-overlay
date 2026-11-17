using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ApolloOverlay;
using ApolloOverlay.Automation;

internal static class AutomaticUpdateTests
{
    private static UpdateCandidate Release(string build = "automatic-next", string version = "9.9.9", string? channel = null) =>
        new(0, version, build, new string('a', 40), channel ?? AppBuild.Current.Channel, DateTimeOffset.UnixEpoch,
            "Improved session controls and update discovery.", 0, "fixture-full.nupkg", 12345, new string('b', 64));
    private static UpdateCandidate VisualRelease()
    {
        var current = Version.Parse(AppBuild.Current.Version.Split('-', '+')[0]);
        var version = $"{current.Major}.{current.Minor + 1}.0-preview.1.1";
        return Release("visual-next-preview", version) with
        {
            Commit = "9f83c6b2a14d507e9cb82a6d8f745b1020d9c3e6",
            PackageName = $"GameSessionOverlay-{version}-full.nupkg",
            Size = 92_903_834,
            Notes = "New releases now appear automatically in the overlay. Background checks preserve your current selection and session controls.\n\nThis preview also refines the warm charcoal theme, update guidance, and controller navigation."
        };
    }

    private static UpdateCandidate EarlierPreview()
    {
        var installed = AppBuild.Current.Version.Split('+')[0];
        var core = installed.Split('-')[0];
        var version = installed == core ? core + "-preview.109.1+2e3a75a" : core + "-0";
        return Release("fixture-earlier-preview", version, "testing") with
        {
            Commit = "2e3a75a" + new string('a', 33),
            PublishedAt = new DateTimeOffset(2026, 11, 13, 16, 0, 0, TimeSpan.Zero),
            Size = 92_903_834
        };
    }

    internal static void Run(Action<string, Action> test)
    {
        test("automatic update discovery is inert in ordinary preview and default-source fixtures", () =>
        {
            foreach (var defaultSource in new[] { false, true })
            {
                using var host = new Host(optIn: false, defaultSource: defaultSource);
                host.Window.AutomaticUpdateFixture = defaultSource;
                host.Window.StartAutomaticUpdateDiscovery();
                host.Now = 10_000_000;
                host.Window.PollAutomaticUpdates();
                Require(host.Source.Checks == 0 && host.Window.AutomaticUpdateTask.IsCompletedSuccessfully
                    && host.Badge.Visibility == Visibility.Collapsed, "An ordinary preview launched a real or injected update query");
            }
        });
        test("automatic update discovery starts after fifteen seconds and repeats after fifteen minutes", () =>
        {
            using var host = new Host();
            host.Poll(14_999);
            Require(host.Source.Checks == 0, "Discovery ran during the startup grace period");
            host.Poll(15_000);
            host.FinishAutomatic();
            Require(host.Source.Checks == 1 && host.Source.Stages == 0, "Startup discovery did not stay a single metadata-only query");
            host.Poll(914_999);
            Require(host.Source.Checks == 1, "Discovery ignored its successful-check cadence");
            host.Poll(915_000);
            host.FinishAutomatic();
            Require(host.Source.Checks == 2 && host.Logs().Count(IsAutomaticCompletion) == 2, "Periodic discovery or its complete command log was lost");
        });
        test("automatic discovery failures stay quiet and retry after the configured backoff", () =>
        {
            using var host = new Host();
            host.Source.Failure = new IOException("private-fixture-transport-message");
            var status = host.StatusText;
            var selected = host.Window.SelectedAction;
            var cues = host.Feedback.Requests;
            var operation = host.Window.Controller.Operation;
            host.Poll(15_000);
            host.FinishAutomatic();
            Require(host.Window.SelectedAction == selected && host.StatusText == status && host.Feedback.Requests == cues
                && ReferenceEquals(host.Window.Controller.Operation, operation) && !host.Window.AutomationSnapshot().Busy,
                "A background failure changed selection, feedback, operation state, or global availability");
            var logs = host.Logs();
            Require(logs.Any(row => Field(row, "Command") == "automatic-update-check" && Field(row, "Outcome") == "Failed")
                && !string.Join("", logs.Select(row => row.GetRawText())).Contains("private-fixture-transport-message", StringComparison.Ordinal),
                "The background error was not logged safely");
            host.Poll(314_999);
            Require(host.Source.Checks == 1, "Failure retried before the five-minute backoff");
            host.Source.Failure = null;
            host.Poll(315_000);
            host.FinishAutomatic();
            Require(host.Source.Checks == 2 && host.StatusText == status, "A failed background query could not recover quietly");
        });
        test("pending hidden update discovery preserves input focus selection feedback and agent job state", () =>
        {
            using var host = new Host();
            var pending = host.Source.DelayNext();
            var selected = host.Window.SelectedAction;
            var actions = host.Window.ActionIds.ToArray();
            var keyboard = Keyboard.FocusedElement;
            var foreground = Native.GetForegroundWindow();
            var feedback = host.StatusText;
            var operation = host.Window.Controller.Operation;
            var cues = host.Feedback.Requests;
            var before = host.Status();
            host.Poll(15_000);
            Require(!host.Window.AutomaticUpdateTask.IsCompleted && !host.Window.AutomationSnapshot().Busy
                && host.Window.SelectedAction == selected && actions.SequenceEqual(host.Window.ActionIds)
                && !host.Window.IsVisible && Native.GetForegroundWindow() == foreground && ReferenceEquals(keyboard, Keyboard.FocusedElement),
                "A pending background query blocked navigation, changed actions, or acquired focus");
            pending.SetResult([Release()]);
            host.FinishAutomatic();
            var after = host.Status();
            Require(host.Badge.Visibility == Visibility.Visible && after.Available.Single().BuildId == "automatic-next"
                && after.JobId == before.JobId && after.Phase == before.Phase && after.Terminal == before.Terminal,
                "Automatic discovery replaced the CLI job instead of publishing a catalog");
            Require(host.Source.Stages == 0 && !host.Window.IsVisible && Native.GetForegroundWindow() == foreground
                && host.Window.SelectedAction == selected && host.StatusText == feedback && host.Feedback.Requests == cues
                && ReferenceEquals(host.Window.Controller.Operation, operation), "Discovery downloaded, activated, or emitted intrusive feedback");
        });
        test("automatic discovery preserves the selected visible WPF control when a newer version arrives", () =>
        {
            using var host = new Host();
            host.Open(572, 940);
            var selected = host.Window.SelectedAction;
            var focus = Keyboard.FocusedElement;
            var feedback = host.StatusText;
            var cues = host.Feedback.Requests;
            host.Source.Releases = [Release()];
            host.Poll(15_000);
            host.FinishAutomatic();
            Require(host.Window.SelectedAction == selected && ReferenceEquals(focus, Keyboard.FocusedElement)
                && host.StatusText == feedback && host.Feedback.Requests == cues
                && host.Window.IsActive && Native.GetForegroundWindow() == new WindowInteropHelper(host.Window).Handle,
                "Publishing the update indicator moved visible focus or interrupted the current action");
            var order = host.Window.ActionIds.ToArray();
            host.Window.RefreshRows();
            Require(order.SequenceEqual(host.Window.ActionIds), "The header update action changed keyboard order after rebuilding the same view");
        });
        test("available update button opens its download review without staging until explicitly requested", () =>
        {
            using var host = new Host();
            host.Source.Releases = [Release()];
            host.Poll(15_000);
            host.FinishAutomatic();
            host.Open(572, 940);
            Click(host.Badge);
            Require(host.Window.AutomationSnapshot().Route == "UpdateReview" && host.Window.ActionIds.Contains("prepare-update")
                && host.Source.Stages == 0, "The real indicator button did not open a read-only download review");
            Click(WpfTestElements.Button(host.Window, "prepare-update"));
            WpfTestDispatcher.Complete(host.Window.UpdateTask);
            Require(host.Source.Stages == 1 && host.Window.ActionIds.Contains("apply-update") && !host.Window.TransitionPending,
                "Explicit download did not remain staged without restarting");
        });
        test("channel changes cancel discovery and reject a late obsolete catalog", () =>
        {
            using var host = new Host();
            var pending = host.Source.DelayNext();
            host.Poll(15_000);
            var old = host.Window.AutomaticUpdateTask;
            var token = host.Source.LastToken;
            var channel = host.Status().SelectedChannel == "testing" ? "stable" : "testing";
            Invoke(host.Window, "OpenUpdates");
            Click(WpfTestElements.Button(host.Window, "update-channel"));
            Require(token.IsCancellationRequested && host.Status().SelectedChannel == channel, "The old channel query was not cancelled");
            pending.SetResult([Release()]);
            WpfTestDispatcher.Complete(old);
            Require(host.Status().Available.Length == 0 && host.Badge.Visibility == Visibility.Collapsed, "An obsolete channel result restored its catalog or indicator");
            host.Window.PollAutomaticUpdates();
            host.FinishAutomatic();
            Require(host.Source.LastChannel == channel && host.Source.Checks == 2, "The newly selected channel did not get its immediate metadata check");
        });
        test("closing the overlay cancels discovery and a late source result cannot resurrect it", () =>
        {
            using var host = new Host();
            var pending = host.Source.DelayNext();
            host.Poll(15_000);
            var token = host.Source.LastToken;
            host.Window.Close();
            Require(token.IsCancellationRequested && host.Source.Disposed, "Shutdown left update discovery or its source active");
            pending.SetResult([Release()]);
            host.FinishAutomatic();
            host.Poll(10_000_000);
            Require(!host.Window.IsVisible && host.Badge.Visibility == Visibility.Collapsed && host.Source.Checks == 1 && host.Source.Stages == 0,
                "A late source completion reopened the overlay, republished an indicator, or restarted discovery");
        });
        test("local cancellation of a CLI-owned transfer preserves its exact terminal receipt and discards a late payload", () =>
        {
            using var host = new Host();
            var release = Release();
            host.Source.Releases = [release];
            host.Poll(15_000);
            host.FinishAutomatic();
            host.Open(572, 940);
            Click(host.Badge);
            host.Source.StageCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var request = Request("update-download", release.BuildId);
            Require(host.Send(request).Outcome == "Requested", "The identified CLI transfer did not start");
            WpfTestDispatcher.Complete(host.Window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background).Task);
            Require(host.Source.Stages == 1 && !host.Window.UpdateTask.IsCompleted,
                "The CLI transfer did not reach its pending source");
            var primary = WpfTestElements.Button(host.Window, "prepare-update");
            Require(System.Windows.Automation.AutomationProperties.GetName(primary) == "Cancel download",
                "The current CLI-owned download was not represented by the cancel action");
            Click(primary);
            var state = host.Status();
            Require(host.Source.StageToken.IsCancellationRequested && state is { Phase: "Cancelled", Terminal: true }
                && state.JobId == request.RequestId, "Local cancellation lost or falsely completed the exact CLI job receipt");
            host.Source.StageCompletion.SetResult(new("fixture-never-installed.nupkg",
                new(release.Version, release.BuildId, release.Commit, release.Channel)));
            WpfTestDispatcher.Complete(host.Window.UpdateTask);
            Require(host.Status() is { Phase: "Cancelled", Terminal: true } && host.Source.Discards == 1
                && !host.Window.TransitionPending && ReadField(host.Window, "preparedUpdate") is null,
                "Late CLI staging retained a payload or replaced terminal cancellation");
        });
        foreach (var cli in new[] { false, true })
            test("explicit update checks fence obsolete automatic results and preserve the prepared package pin cli=" + cli, () =>
            {
                using var host = new Host();
                var pending = host.Source.DelayNext();
                host.Poll(15_000);
                var automatic = host.Window.AutomaticUpdateTask;
                var automaticToken = host.Source.LastToken;
                var fresh = Release("explicit-next", "9.9.10");
                host.Source.Releases = [fresh];
                if (cli)
                {
                    Require(host.Send(Request("update-check")).Outcome == "Requested", "The explicit CLI check was rejected");
                    WpfTestDispatcher.Complete(host.Window.UpdateTask);
                }
                else
                    WpfTestDispatcher.Complete((Task)Invoke(host.Window, "CheckUpdatesAsync")!);
                Require(automaticToken.IsCancellationRequested && host.Status().Available.Single().BuildId == fresh.BuildId,
                    "The explicit check did not cancel automatic discovery or publish its own catalog");
                Require(host.Send(Request("update-download", fresh.BuildId)).Outcome == "Requested", "The freshly checked exact build could not be staged");
                WpfTestDispatcher.Complete(host.Window.UpdateTask);
                var state = host.Status();
                var prepared = ReadField(host.Window, "preparedUpdate");
                var generation = ReadField(host.Window, "updateGeneration");
                var pinType = typeof(MainWindow).GetNestedType("AgentUpdateApplyPin", BindingFlags.NonPublic)!;
                var pin = Activator.CreateInstance(pinType, "fixture-apply", state.JobId, generation, prepared,
                    "fixture-preservation-marker", DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds());
                GetField("agentUpdateApplyPin").SetValue(host.Window, pin);
                pending.SetResult([Release("obsolete-automatic", "10.0.0")]);
                WpfTestDispatcher.Complete(automatic);
                var after = host.Status();
                Require(after.Available.Single().BuildId == fresh.BuildId && after.JobId == state.JobId && after.Phase == "Ready"
                    && ReferenceEquals(prepared, ReadField(host.Window, "preparedUpdate"))
                    && ReferenceEquals(pin, ReadField(host.Window, "agentUpdateApplyPin")) && Equals(generation, ReadField(host.Window, "updateGeneration")),
                    "A late automatic batch replaced the explicit catalog, prepared payload, job, or apply pin");
            });
        test("manual update checks distinguish an earlier published version from an update", () =>
        {
            using var host = new Host();
            var window = host.Window;
            var earlier = EarlierPreview();
            var newRelease = Release(channel: "testing");
            Invoke(window, "OpenUpdates");
            if (host.Status().SelectedChannel != earlier.Channel)
                Click(WpfTestElements.Button(window, "update-channel"));
            host.FinishAutomatic();
            host.Source.Releases = [earlier];
            Click(WpfTestElements.Button(window, "check-updates"));
            WpfTestDispatcher.Complete(window.UpdateTask);
            var catalog = (TextBlock)ReadField(window, "updateProgressLabel")!;
            Require(catalog.Text == "No newer version is available in this channel." && host.Badge.Visibility == Visibility.Collapsed,
                $"An earlier preview was presented as an available update: {catalog.Text}");
            var row = WpfTestElements.Button(window, "release:" + earlier.BuildId);
            Require(row.IsEnabled && AutomationProperties.GetName(row).EndsWith(" · earlier version", StringComparison.Ordinal),
                "The earlier version was not clearly labeled and selectable");
            Click(row);
            Require(((TextBlock)window.FindName("PageTitle")).Text == "Review version"
                && ViewHasText(window, "Earlier version") && host.Source.Stages == 0,
                "Review did not identify the earlier version or downloaded without an explicit action");
            var download = WpfTestElements.Button(window, "prepare-update");
            Require(AutomationProperties.GetName(download) == "Download earlier version", "The action did not identify the earlier download");
            host.Source.StageCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Click(download);
            Require(AutomationProperties.GetName(download) == "Cancel download", "The pending earlier download could not be cancelled");
            Click(download);
            host.Source.StageCompletion.SetResult(new("fixture-never-installed.nupkg",
                new(earlier.Version, earlier.BuildId, earlier.Commit, earlier.Channel)));
            WpfTestDispatcher.Complete(window.UpdateTask);
            Require(host.Source.StageToken.IsCancellationRequested && host.Source.Discards == 1
                && !window.ActionIds.Contains("apply-update")
                && AutomationProperties.GetName(WpfTestElements.Button(window, "prepare-update")) == "Download earlier version",
                "Cancelling an earlier download retained its payload or lost the earlier download label");
            host.Source.StageCompletion = null;
            Click(WpfTestElements.Button(window, "prepare-update"));
            WpfTestDispatcher.Complete(window.UpdateTask);
            Require(ViewHasText(window, "Earlier version downloaded") && host.Source.Stages == 2
                && window.ActionIds.Contains("apply-update") && !window.TransitionPending,
                "A verified earlier download was mislabeled or crossed the restart boundary");
            Click(WpfTestElements.Button(window, "back"));
            Require(((TextBlock)ReadField(window, "updateProgressLabel")!).Text == catalog.Text,
                "Returning from the review lost the catalog result");

            host.Source.Releases = [earlier, newRelease];
            Click(WpfTestElements.Button(window, "check-updates"));
            WpfTestDispatcher.Complete(window.UpdateTask);
            Require(((TextBlock)ReadField(window, "updateProgressLabel")!).Text.StartsWith("Versions are available.", StringComparison.Ordinal)
                && host.Badge.Visibility == Visibility.Visible, "The check missed a genuinely newer version listed after the earlier preview");
            Click(WpfTestElements.Button(window, "release:" + newRelease.BuildId));
            Require(ViewHasText(window, "Update available")
                && AutomationProperties.GetName(WpfTestElements.Button(window, "prepare-update")) == "Download update",
                "A genuine update lost its usual review and download labels");
        });
        test("passive update discovery preserves the existing catalog controls and scroll position", () =>
        {
            using var host = new Host();
            var window = host.Window;
            var earlier = EarlierPreview();
            Invoke(window, "OpenUpdates");
            if (host.Status().SelectedChannel != earlier.Channel)
                Click(WpfTestElements.Button(window, "update-channel"));
            host.FinishAutomatic();
            var installed = earlier with
            {
                BuildId = AppBuild.Current.BuildId,
                Version = AppBuild.Current.Version
            };
            var initialCatalog = Enumerable.Range(1, 8).Select(i => earlier with { BuildId = $"earlier-{i}" }).Prepend(installed).ToArray();
            host.Source.Releases = initialCatalog;
            Click(WpfTestElements.Button(window, "check-updates"));
            WpfTestDispatcher.Complete(window.UpdateTask);

            var (surface, root, scroll) = RenderUpdateSurface(window, 360, 480);
            scroll.ScrollToVerticalOffset(500);
            surface.UpdateLayout();
            var offset = scroll.VerticalOffset;
            var selection = window.SelectedAction;
            var check = UpdateButton(root, "check-updates");
            var status = (TextBlock)ReadField(window, "updateProgressLabel")!;
            Require(offset > 0 && selection == "check-updates" && status.Text.Contains("latest version", StringComparison.Ordinal),
                "The fixture did not start with the current build in the catalog or could not scroll away from the selected action");

            host.Source.Releases = initialCatalog[1..];
            host.Poll(915_000);
            host.FinishAutomatic();
            surface.UpdateLayout();
            Require(ReferenceEquals(check, UpdateButton(root, "check-updates"))
                && ReferenceEquals(status, ReadField(window, "updateProgressLabel")), "A background result replaced existing catalog controls");
            Require(window.SelectedAction == selection && Math.Abs(scroll.VerticalOffset - offset) < .1,
                $"A background result moved selection from {selection} to {window.SelectedAction} or scrolled from {offset} to {scroll.VerticalOffset}");
            Require(host.Source.Checks >= 2 && host.Badge.Visibility == Visibility.Collapsed
                && status.Text == "No newer version is available in this channel.",
                $"The background result did not publish availability. Checks={host.Source.Checks}; badge={host.Badge.Visibility}; status={status.Text}");
        });
        test("earlier published version renders at wide and compact WPF sizes", () =>
        {
            foreach (var (width, height) in new[] { (572, 940), (360, 480) })
            {
                using var host = new Host();
                var earlier = EarlierPreview();
                var window = host.Window;
                Invoke(window, "OpenUpdates");
                if (host.Status().SelectedChannel != earlier.Channel)
                    Click(WpfTestElements.Button(window, "update-channel"));
                host.FinishAutomatic();
                host.Source.Releases = [earlier];
                Click(WpfTestElements.Button(window, "check-updates"));
                WpfTestDispatcher.Complete(window.UpdateTask);

                var (surface, root, scroll) = RenderUpdateSurface(window, width, height);
                var row = UpdateButton(root, "release:" + earlier.BuildId);
                scroll.ScrollToTop();
                Capture(surface, $"earlier-version-dark-{width}x{height}-catalog", "offscreen=True");
                if (height == 480)
                {
                    var heading = WpfTestElements.Descendants<TextBlock>(root).Single(block => block.Text == "Published versions");
                    ScrollToCapture(surface, scroll, heading);
                    Capture(surface, $"earlier-version-dark-{width}x{height}-catalog-release", "offscreen=True");
                }
                Click(row);
                surface.UpdateLayout();
                scroll.ScrollToTop();
                Capture(surface, $"earlier-version-dark-{width}x{height}-review", "offscreen=True");
                if (height == 480)
                {
                    var status = (TextBlock)ReadField(window, "updateProgressLabel")!;
                    ScrollToCapture(surface, scroll, status);
                    Capture(surface, $"earlier-version-dark-{width}x{height}-review-download", "offscreen=True");
                }
            }
        });
        test("automatic update indicator has real foreground-owned wide compact and download WPF captures", () =>
        {
            using (var idle = new Host())
            {
                idle.Open(572, 940);
                Capture(idle.Window, "automatic-update-dark-572x940-current");
            }
            foreach (var width in new[] { 572, 360 })
            {
                using var host = new Host();
                var release = VisualRelease();
                Require(UpdateAvailability.Newer([release], AppBuild.Current, release.Channel) is not null,
                    "The representative visual release is not newer than the running capture build");
                host.Source.Releases = [release];
                host.Poll(15_000);
                host.FinishAutomatic();
                host.Open(width, width == 360 ? 480 : 940);
                if (width == 360)
                    Require(((TextBlock)host.Window.FindName("HeaderBrand")).Visibility == Visibility.Collapsed,
                        "The compact update header retained a truncated brand fragment");
                Capture(host.Window, $"automatic-update-dark-{width}x{(width == 360 ? 480 : 940)}-available");
                if (width != 572)
                    continue;
                Click(host.Badge);
                WpfTestDispatcher.Complete(Task.Delay(200));
                RequireNoRepeatedUpdateStatus(host.Window);
                Capture(host.Window, "automatic-update-dark-572x940-download");
                Require(host.Source.Stages == 0, "Review capture downloaded or installed a package");
                host.Source.StageCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                Click(WpfTestElements.Button(host.Window, "prepare-update"));
                Require(host.Source.Stages == 1 && !host.Window.UpdateTask.IsCompleted && host.Source.StageProgress is not null,
                    "The active-download capture did not reach a real pending staging operation");
                host.Source.StageProgress!.Report(new("Downloading update", 57, 100));
                WpfTestDispatcher.Complete(Task.Delay(200));
                Require(((TextBlock)ReadField(host.Window, "updateProgressLabel")!).Text.Contains("57%", StringComparison.Ordinal)
                    && host.Window.AutomationSnapshot().Busy && !host.Window.TransitionPending,
                    "Download progress was not published or crossed the restart boundary");
                Require(System.Windows.Automation.AutomationProperties.GetName(WpfTestElements.Button(host.Window, "prepare-update")) == "Cancel download",
                    "The progress capture still presented a download action while cancellation was the real action");
                RequireNoRepeatedUpdateStatus(host.Window);
                Capture(host.Window, "automatic-update-dark-572x940-progress");
            }
            using var audio = new Host();
            audio.Open(572, 940);
            Click((Button)audio.Window.FindName("SettingsTab"));
            WpfTestDispatcher.Complete(Task.Delay(200));
            Capture(audio.Window, "automatic-update-dark-572x940-audio");
        });
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private static bool ViewHasText(MainWindow window, string text) =>
        WpfTestElements.Descendants<TextBlock>((DependencyObject)window.Content).Any(block => block.Text == text);
    private static Button UpdateButton(FrameworkElement root, string id) =>
        WpfTestElements.Descendants<Button>(root).Single(button => Equals(button.Tag, id));

    private static void ScrollToCapture(FrameworkElement surface, ScrollViewer scroll, FrameworkElement target)
    {
        surface.UpdateLayout();
        var top = target.TranslatePoint(new Point(), scroll).Y;
        scroll.ScrollToVerticalOffset(scroll.VerticalOffset + top - 8);
        surface.UpdateLayout();
    }

    private static (Border Surface, FrameworkElement Root, ScrollViewer Scroll) RenderUpdateSurface(MainWindow window, int width, int height)
    {
        var root = (FrameworkElement)window.Content;
        window.Content = null;
        root.Resources.MergedDictionaries.Add(window.Resources);
        root.SetResourceReference(TextElement.ForegroundProperty, ThemeSupport.Text);
        root.SetValue(TextElement.FontFamilyProperty, window.FontFamily);
        root.SetValue(TextElement.FontSizeProperty, window.FontSize);
        var surface = new Border { Child = root };
        surface.Measure(new Size(width, height));
        surface.Arrange(new Rect(0, 0, width, height));
        surface.UpdateLayout();
        window.RefreshRows();
        surface.UpdateLayout();
        var scroll = (ScrollViewer)window.FindName("ContentScroll");
        Require(!window.IsVisible && scroll.ViewportHeight > 0, "The render-only fixture has no usable content viewport");
        return (surface, root, scroll);
    }
    private static object? Invoke(MainWindow window, string name, params object?[] args) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
    private static FieldInfo GetField(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static object? ReadField(MainWindow window, string name) => GetField(name).GetValue(window);
    private static string Field(JsonElement value, string name) => value.TryGetProperty(name, out var property) ? property.GetString() ?? "" : "";
    private static bool IsAutomaticCompletion(JsonElement value) => Field(value, "Command") == "automatic-update-check" && Field(value, "Event") == "finished";
    private static AutomationRequest Request(string verb, string build = "") => new() { Verb = verb, RequestId = Guid.NewGuid().ToString("N"), BuildId = build };
    private static void Require(bool value, string message)
    {
        if (!value)
            throw new Exception(message);
    }

    private static void RequireNoRepeatedUpdateStatus(MainWindow window)
    {
        var detail = (TextBlock)ReadField(window, "updateProgressLabel")!;
        var footer = (TextBlock)window.FindName("StatusLabel");
        Require(detail.IsVisible && !string.IsNullOrWhiteSpace(detail.Text), "The update status paragraph was not visible");
        Require(!footer.IsVisible || footer.Text != detail.Text,
            "The update status was repeated in both the page and global footer");
    }

    private static void Capture(MainWindow window, string name)
    {
        window.UpdateLayout();
        Require(window.IsVisible && window.IsActive && window.Opacity > .95
            && Native.GetForegroundWindow() == new WindowInteropHelper(window).Handle,
            "The actual WPF capture was not revealed with confirmed native foreground ownership");
        Capture(window, name, "foreground=True");
    }

    private static void Capture(FrameworkElement surface, string name, string mode)
    {
        surface.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth), (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var directory = Path.GetFullPath("artifacts/check-native-v1");
        Directory.CreateDirectory(directory);
        using var output = File.Create(Path.Combine(directory, name + ".png"));
        encoder.Save(output);
        Console.WriteLine($"CAPTURE {name}; actual={surface.ActualWidth}x{surface.ActualHeight}; {mode}; fixture=True; installed={VelopackInstallation.IsInstalled}");
    }

    private sealed class Host : IDisposable
    {
        private readonly SynchronizationContext? previous = SynchronizationContext.Current;
        private readonly LocalDiagnostics diagnostics = new(Path.Combine(Path.GetTempPath(), "BezoroAutomaticUpdates", Guid.NewGuid().ToString("N")));
        private bool closed;
        internal readonly SourceFake Source = new();
        internal readonly FeedbackFake Feedback = new();
        internal MainWindow Window
        {
            get;
        }
        internal long Now;
        internal Button Badge => (Button)Window.FindName("UpdateAvailableButton");
        internal string StatusText => ((TextBlock)Window.FindName("StatusLabel")).Text;
        internal Host(bool optIn = true, bool defaultSource = false)
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            ThemeSupport.ForceForValidation(app, false);
            Window = new(renderOnly: true, platform: new PreviewPlatform(new(101, 42, 100, "The Witcher 3", "witcher3")),
                diagnostics: diagnostics, updates: defaultSource ? null : Source, feedback: Feedback);
            Window.CommandLoggingFixture = true;
            Window.AutomaticUpdateFixture = optIn;
            Window.UpdateDiscoveryClock = () => Now;
            Window.PhysicalKeyReader = _ => 0;
            Window.InputContextReader = () => new(true, true, true, true);
            Window.Closed += (_, _) => closed = true;
            Window.Controller.CaptureReturn();
            WpfTestDispatcher.Complete(Window.Controller.Refresh());
            Window.RefreshRows();
            Window.StartAutomaticUpdateDiscovery();
        }
        internal void Poll(long now)
        {
            Now = now;
            Window.PollAutomaticUpdates();
        }
        internal void FinishAutomatic() => WpfTestDispatcher.Complete(Window.AutomaticUpdateTask);
        internal AutomationReply Send(AutomationRequest request)
        {
            var task = Window.AgentUpdate(request, CancellationToken.None, () => true);
            WpfTestDispatcher.Complete(task);
            return task.Result;
        }
        internal AutomationUpdateState Status() => Send(Request("update-status")).Update!;
        internal void Open(int width, int height)
        {
            Window.OpenPanel();
            WpfTestDispatcher.Complete(Window.OpeningTask);
            // Fixed capture viewports must override WPF's native tracking limit
            // with a matching minimum as well as a maximum.
            Window.MinWidth = width;
            Window.MinHeight = height;
            Window.MaxWidth = width;
            Window.MaxHeight = height;
            Window.Width = width;
            Window.Height = height;
            ((FrameworkElement)Window.Content).LayoutTransform = Transform.Identity;
            Window.UpdateLayout();
            WpfTestDispatcher.Complete(Task.Delay(350));
            ((ScrollViewer)Window.FindName("ContentScroll")).ScrollToTop();
            Window.UpdateLayout();
            Require(Math.Abs(Window.ActualWidth - width) < .1 && Math.Abs(Window.ActualHeight - height) < .1,
                $"The WPF capture fixture requested {width}x{height} but measured {Window.ActualWidth}x{Window.ActualHeight}");
        }
        internal JsonElement[] Logs()
        {
            Require(diagnostics.Commands.FlushAsync().GetAwaiter().GetResult(), "The fixture command log did not drain");
            return Directory.EnumerateFiles(diagnostics.Commands.DirectoryPath, "*.jsonl").SelectMany(JournalFiles.ReadLines).Select(line =>
            {
                using var json = JsonDocument.Parse(line);
                return json.RootElement.Clone();
            }).ToArray();
        }
        public void Dispose()
        {
            try
            {
                if (!closed)
                    Window.Close();
                Source.CancelPending();
                FinishAutomatic();
                WpfTestDispatcher.Complete(Window.UpdateTask);
                WpfTestDispatcher.Complete(Window.AgentReceiptTask);
                WpfTestDispatcher.Complete(Window.ClosingTask);
                diagnostics.Dispose();
                if (Directory.Exists(diagnostics.Root))
                    Directory.Delete(diagnostics.Root, true);
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }
    }

    private sealed class SourceFake : IUpdateSource
    {
        private readonly Queue<TaskCompletionSource<IReadOnlyList<UpdateCandidate>>> queued = new();
        private readonly List<TaskCompletionSource<IReadOnlyList<UpdateCandidate>>> pending = [];
        internal int Checks, Stages, Discards;
        internal bool Disposed;
        internal string LastChannel = "";
        internal CancellationToken LastToken;
        internal Exception? Failure;
        internal IReadOnlyList<UpdateCandidate> Releases = [];
        internal TaskCompletionSource<PreparedUpdate>? StageCompletion;
        internal IProgress<UpdateProgress>? StageProgress;
        internal CancellationToken StageToken;
        internal TaskCompletionSource<IReadOnlyList<UpdateCandidate>> DelayNext()
        {
            var value = new TaskCompletionSource<IReadOnlyList<UpdateCandidate>>(TaskCreationOptions.RunContinuationsAsynchronously);
            queued.Enqueue(value);
            pending.Add(value);
            return value;
        }
        public Task<IReadOnlyList<UpdateCandidate>> CheckAsync(string channel, CancellationToken cancellation)
        {
            Checks++;
            LastChannel = channel;
            LastToken = cancellation;
            if (Failure is { } error)
                return Task.FromException<IReadOnlyList<UpdateCandidate>>(error);
            // Intentionally allow late completion after cancellation to exercise stale-result fences.
            return queued.TryDequeue(out var delayed) ? delayed.Task : Task.FromResult(Releases);
        }
        public Task<PreparedUpdate> StageAsync(UpdateCandidate candidate, IProgress<UpdateProgress>? progress, CancellationToken cancellation)
        {
            Stages++;
            StageProgress = progress;
            StageToken = cancellation;
            return StageCompletion?.Task ?? Task.FromResult(new PreparedUpdate("fixture-never-installed.nupkg", new(candidate.Version, candidate.BuildId, candidate.Commit, candidate.Channel)));
        }
        internal void CancelPending()
        {
            foreach (var value in pending)
                value.TrySetCanceled();
            StageCompletion?.TrySetCanceled();
        }
        public void Dispose() => Disposed = true;
        public void Discard(PreparedUpdate prepared) => Discards++;
    }

    private sealed class FeedbackFake : IFeedbackService
    {
        internal int Requests;
        public event Action<FeedbackStatus>? Changed { add { } remove { } }
        public FeedbackStatus Status => new(false, false, "Fixture feedback");
        public void Configure(bool sound, bool haptics)
        {
        }
        public void SetContext(FeedbackContext context)
        {
        }
        public FeedbackDispatch Request(FeedbackCue cue, FeedbackSource source)
        {
            Requests++;
            return new(false, false, "Fixture feedback");
        }
        public void Suspend()
        {
        }
        public void Dispose()
        {
        }
    }
}
