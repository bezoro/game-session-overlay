namespace ApolloOverlay;

public partial class MainWindow
{
    public void SetRenderState(string state)
    {
        switch (state)
        {
            case "apps":
                Page(1);
                break;
            case "system":
                Page(2);
                break;
            case "audio":
                Subpage("audio", "volume");
                break;
            case "integrations":
                Subpage("integrations", "test-steam");
                break;
            case "details":
                if (Controller.Session.Anchor is { } selected)
                    OpenView(new(OverlayRoute.Details, selected, "switch-detail"));
                break;
            case "options":
                if (Controller.Session.Anchor is { } option)
                    OpenOptions(option);
                break;
            case "confirmation":
                if (Controller.Session.Anchor is { } t)
                    Confirm(t);
                break;
            case "long-error":
                FeedbackText("Unconfirmed · The window identity changed. Select it again in Apps. No additional command was sent.");
                break;
            case "steam-unavailable":
                navigation = new("steam-overlay");
                SetSelection();
                break;
        }
    }
}
