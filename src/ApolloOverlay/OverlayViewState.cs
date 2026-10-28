namespace ApolloOverlay;

public enum OverlayTab
{
    Hub,
    Apps,
    System
}

public enum OverlayRoute
{
    Hub,
    Apps,
    System,
    TargetOptions,
    Details,
    WindowsAudio,
    Integrations,
    Customize,
    Setup,
    OperationResult,
    Confirmation,
    PlayniteBrowser,
    Updates,
    UpdateReview,
    Mixer,
    AudioDevices,
    Profiles,
    ProfileEdit,
    Media,
    Keyboard,
    Captures,
    CaptureDetail
}

public sealed record OverlayViewFrame(OverlayRoute Route, WindowRef? Target = null, string SelectedId = "", double ScrollOffset = 0, bool ChooseSession = false, int SetupStep = 0, string? BrowserFolder = null, int BrowserPage = 0);
public sealed class OverlayViewState
{
    private readonly List<OverlayViewFrame> parents = [];
    private readonly Dictionary<OverlayTab, OverlayViewFrame> tabs = [];
    public OverlayTab Tab
    {
        get; private set;
    }
    public OverlayViewFrame Current { get; private set; } = new(OverlayRoute.Hub, SelectedId: "session-card");
    public int Depth => parents.Count;

    public void Remember(string selected, double offset) => Current = Current with
    {
        SelectedId = selected,
        ScrollOffset = Math.Max(0, offset)
    };
    public void Open(OverlayViewFrame next)
    {
        if (parents.Count >= 12)
            parents.RemoveAt(0);
        parents.Add(Current);
        Current = next;
    }

    public bool Back()
    {
        if (parents.Count == 0)
            return false;
        Current = parents[^1];
        parents.RemoveAt(parents.Count - 1);
        return true;
    }

    public void Replace(OverlayViewFrame frame) => Current = frame;
    public void SwitchTab(OverlayTab tab)
    {
        if (Tab == tab && parents.Count == 0)
        {
            Current = Default(tab);
            tabs[tab] = Current;
            return;
        }

        var origin = parents.Count > 0 ? parents[0] : Current;
        tabs[Tab] = origin;
        parents.Clear();
        Tab = tab;
        Current = tabs.GetValueOrDefault(tab) ?? Default(tab);
    }

    private static OverlayViewFrame Default(OverlayTab tab) => new(tab switch
    {
        OverlayTab.Apps => OverlayRoute.Apps,
        OverlayTab.System => OverlayRoute.System,
        _ => OverlayRoute.Hub
    }, SelectedId: tab switch
    {
        OverlayTab.Apps => "refresh-apps",
        OverlayTab.System => "display",
        _ => "session-card"
    });
}
