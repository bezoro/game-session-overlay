namespace ApolloOverlay;

public partial class MainWindow
{
    private StartupStatus startupStatus = new(false, false, false, "Checking Windows startup registration…");
    private bool startupStatusFresh;
    private long startupStatusGeneration;
    private long startupStatusReadAt;
    private Task? startupStatusRead;
    internal Func<StartupStatus>? StartupStatusReadFixture
    {
        private get; set;
    }
    internal Task StartupStatusTask => startupStatusRead ?? Task.CompletedTask;

    private void InvalidateStartupStatus()
    {
        startupStatusFresh = false;
        startupStatusGeneration++;
        if (!exiting)
            RefreshRows();
    }

    private async Task<ActionResult> ApplyStartupChange(Func<ActionResult> change, CancellationToken token)
    {
        try
        {
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                return change();
            }, token);
        }
        finally
        {
            // Failed observation can follow a partial native change. Revoke cached
            // evidence for every attempt, including cancellation and uncertainty.
            InvalidateStartupStatus();
        }
    }

    private void EnsureStartupStatus()
    {
        if (startupStatusRead is not null || exiting)
            return;
        if (startupStatusFresh && Environment.TickCount64 - startupStatusReadAt < 5000)
            return;
        startupStatusRead = ReadStartupStatus(startupStatusGeneration);
    }

    private async Task ReadStartupStatus(long generation)
    {
        await Task.Yield();
        // There is at most one native reader. A slow scheduler retains that slot
        // until it returns; rebuilding rows cannot queue additional blocked workers.
        var reader = StartupStatusReadFixture ?? StartupRegistration.ReadStatus;
        try
        {
            var observed = await Task.Run(reader);
            if (exiting || generation != startupStatusGeneration)
                return;
            startupStatus = observed;
            startupStatusFresh = true;
            startupStatusReadAt = Environment.TickCount64;
            if (IsVisible && view.Current.Route == OverlayRoute.System)
                RefreshRows();
        }
        catch (Exception error) when (StartupRegistration.RegistrationError(error))
        {
            if (!exiting && generation == startupStatusGeneration)
            {
                startupStatus = new(false, false, false, "Windows startup registration could not be read.");
                startupStatusFresh = true;
                startupStatusReadAt = Environment.TickCount64;
                if (IsVisible && view.Current.Route == OverlayRoute.System)
                    RefreshRows();
            }
        }
        finally
        {
            startupStatusRead = null;
            if (!exiting && !startupStatusFresh && IsVisible && view.Current.Route == OverlayRoute.System)
                EnsureStartupStatus();
        }
    }

    private void AddStartup()
    {
        var preview = StartupStatusReadFixture is null && (diagnostic || Controller.Platform.IsPreview);
        if (!preview)
            EnsureStartupStatus();
        var state = preview
            ? new StartupStatus(false, false, false, "Installed startup registration is not changed in preview mode.")
            : startupStatus;
        AddRow("startup", "Start with Windows · " + (state.Enabled ? "On" : "Off"), state.Description, () =>
            StartSessionAction("startup", token => ApplyStartupChange(() => StartupRegistration.SetEnabled(!state.Enabled, token), token)),
            state.Available && startupStatusFresh);
        AddAdministratorMode(state);
    }
}
