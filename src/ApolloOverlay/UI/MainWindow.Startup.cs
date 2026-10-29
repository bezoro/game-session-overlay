namespace ApolloOverlay;

public partial class MainWindow
{
    private void AddStartup()
    {
        var state = diagnostic || Controller.Platform.IsPreview
            ? new StartupStatus(false, false, false, "Installed startup registration is not changed in preview mode.")
            : StartupRegistration.ReadStatus();
        AddRow("startup", "Start with Windows · " + (state.Enabled ? "On" : "Off"), state.Description, () =>
            StartSessionAction("startup", token => Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                return StartupRegistration.SetEnabled(!state.Enabled);
            }, token), RefreshRows), state.Available);
    }
}
