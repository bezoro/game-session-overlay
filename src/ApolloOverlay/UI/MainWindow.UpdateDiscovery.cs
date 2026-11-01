using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;

namespace ApolloOverlay;

public partial class MainWindow
{
    private DispatcherTimer? automaticUpdateTimer;
    private UpdateDiscoverySchedule? automaticUpdateSchedule;
    private CancellationTokenSource? automaticUpdateCancellation;
    private UpdateCandidate? availableUpdate;
    private long automaticUpdateGeneration;
    internal bool AutomaticUpdateFixture
    {
        private get; set;
    }
    internal Func<long> UpdateDiscoveryClock { private get; set; } = static () => Environment.TickCount64;
    internal Task AutomaticUpdateTask { get; private set; } = Task.CompletedTask;

    internal void StartAutomaticUpdateDiscovery()
    {
        if (exiting || automaticUpdateTimer is not null || updateSource is PreviewUpdateSource
            || !AutomaticUpdateFixture && (diagnostic || Controller.Platform.IsPreview || !VelopackInstallation.IsInstalled))
            return;
        automaticUpdateSchedule = new(UpdateDiscoveryClock());
        automaticUpdateTimer = new(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(15)
        };
        automaticUpdateTimer.Tick += AutomaticUpdateTick;
        automaticUpdateTimer.Start();
    }

    private void AutomaticUpdateTick(object? sender, EventArgs args)
    {
        PollAutomaticUpdates();
        if (automaticUpdateTimer is not null && automaticUpdateSchedule is { } schedule)
            automaticUpdateTimer.Interval = TimeSpan.FromMilliseconds(automaticUpdateCancellation is null
                ? Math.Clamp(schedule.NextCheckAt - UpdateDiscoveryClock(), 1000, 60_000) : 60_000);
    }

    internal void PollAutomaticUpdates()
    {
        if (exiting || automaticUpdateSchedule is not { } schedule || automaticUpdateCancellation is not null
            || !schedule.IsDue(UpdateDiscoveryClock()))
            return;
        if (busy || updateBusy || updateCommitting || ownedDialog || TransitionPending || Confirming)
        {
            schedule.Defer(UpdateDiscoveryClock());
            return;
        }
        AutomaticUpdateTask = DiscoverUpdatesAsync();
    }

    private async Task DiscoverUpdatesAsync()
    {
        using var command = BeginCommand("automatic-update-check", origin: "system");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        automaticUpdateCancellation = cancellation;
        var generation = automaticUpdateGeneration;
        var foregroundGeneration = updateGeneration;
        var channel = updateChannel;
        var succeeded = false;
        try
        {
            var releases = await updateSource.CheckAsync(channel, cancellation.Token);
            if (exiting || cancellation.IsCancellationRequested || generation != automaticUpdateGeneration
                || channel != updateChannel || foregroundGeneration != updateGeneration || busy || updateBusy || updateCommitting
                || ownedDialog || TransitionPending || Confirming)
            {
                command?.Complete(ResultKind.Cancelled, "automatic-catalog-superseded/" + channel);
                return;
            }
            AcceptUpdateCatalog(releases);
            succeeded = true;
            command?.Complete(ResultKind.Completed, (releases.Count == 0 ? "automatic-catalog-empty" : availableUpdate is null
                ? "automatic-catalog-current" : "automatic-update-available") + "/" + channel);
        }
        catch (OperationCanceledException)
        {
            command?.Complete(ResultKind.Cancelled, "automatic-check-cancelled/" + channel);
        }
        catch (Exception error) when (error is not (OutOfMemoryException or AccessViolationException))
        {
            command?.Fail(error, "automatic-check-failed/" + channel);
        }
        finally
        {
            if (ReferenceEquals(automaticUpdateCancellation, cancellation))
                automaticUpdateCancellation = null;
            if (!exiting && !succeeded && generation == automaticUpdateGeneration)
                automaticUpdateSchedule?.Complete(UpdateDiscoveryClock(), succeeded: false);
        }
    }

    private void CancelAutomaticUpdateCheck()
    {
        automaticUpdateGeneration++;
        automaticUpdateSchedule?.Defer(UpdateDiscoveryClock());
        automaticUpdateCancellation?.Cancel();
    }

    private void UpdateDiscoveryChannelChanged()
    {
        CancelAutomaticUpdateCheck();
        availableUpdate = null;
        automaticUpdateSchedule?.Reset(UpdateDiscoveryClock());
        RefreshUpdateIndicator();
    }

    private void AcceptUpdateCatalog(IReadOnlyList<UpdateCandidate> releases)
    {
        updateCandidates = releases;
        availableUpdate = UpdateAvailability.Newer(releases, AppBuild.Current, updateChannel);
        automaticUpdateSchedule?.Complete(UpdateDiscoveryClock(), succeeded: true);
        RefreshUpdateIndicator();
        if (view.Current.Route == OverlayRoute.Updates && updateProgressLabel is not null && !updateBusy)
        {
            updateStatus = availableUpdate is { } next ? $"Version {next.Version} is available. Select Update available to see the download."
                : "No newer version is available in this channel.";
            updateProgressLabel.Text = updateStatus;
        }
    }

    private void RefreshUpdateIndicator(bool register = true)
    {
        HeaderIdentity.Text = "v" + AppBuild.Current.Version.Split('+')[0];
        HeaderIdentity.ToolTip = AppBuild.Current.Display + " · " + AppBuild.Current.BuildId + " · " + AppBuild.Current.Commit;
        var compact = narrow || compactChrome;
        HeaderBrand.FontSize = ResourceNumber(compact ? "SessionType.Caption" : "SessionType.CompactBody", compact ? 13 : 14);
        var stacked = compact && view.Depth > 0;
        HeaderVersionGroup.Orientation = stacked ? Orientation.Vertical : Orientation.Horizontal;
        UpdateAvailableButton.Margin = stacked ? new(0, 4, 0, 0) : new(6, 0, 0, 0);
        if (UpdateAvailableButton.Content is IconLabel label)
            label.Label.Text = compact ? "Update" : "Update available";
        HeaderBrand.Visibility = compact && (availableUpdate is not null || view.Current.Route != OverlayRoute.Hub)
            ? Visibility.Collapsed : Visibility.Visible;
        UpdateAvailableButton.Visibility = availableUpdate is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateAvailableButton.ToolTip = availableUpdate is { } next ? "Version " + next.Version + " · view changes and download" : null;
        AutomationProperties.SetHelpText(UpdateAvailableButton, UpdateAvailableButton.ToolTip as string ?? "");
        SetEntryDescription("updates", availableUpdate is { } release ? "Version " + release.Version + " available · view changes and download"
            : "Automatic checks every 15 minutes · update or restore an earlier version");
        if (!register)
            return;
        var existing = entries.FindIndex(entry => entry.Id == "update-available");
        if (availableUpdate is not null && existing < 0)
        {
            UpdateAvailableButton.Tag = "update-available";
            UpdateAvailableButton.IsEnabled = true;
            var position = entries.FindIndex(entry => entry.Id == "settings") + 1;
            entries.Insert(position, new("update-available", UpdateAvailableButton, OpenAvailableUpdate, true, "View the available update and download"));
            automationRevision++;
        }
        else if (availableUpdate is null && existing >= 0)
        {
            entries.RemoveAt(existing);
            automationRevision++;
            if (navigation.Id == "update-available")
            {
                navigation = new("settings");
                SetSelection();
            }
        }
    }

    private void OpenAvailableUpdate()
    {
        if (availableUpdate is { } candidate)
            ReviewRelease(candidate);
    }

    private void DisposeAutomaticUpdateDiscovery()
    {
        if (automaticUpdateTimer is not null)
        {
            automaticUpdateTimer.Stop();
            automaticUpdateTimer.Tick -= AutomaticUpdateTick;
            automaticUpdateTimer = null;
        }
        CancelAutomaticUpdateCheck();
    }
}
