namespace ApolloOverlay;

public partial class MainWindow
{
    internal void ShowRollbackForValidation(InstalledBuild build)
    {
        if (!diagnostic)
            throw new InvalidOperationException("Rollback presentation fixtures require diagnostic mode.");
        rollbackReview = build;
        updateReview = null;
        OpenView(new(OverlayRoute.UpdateReview, SelectedId: "apply-rollback"));
    }

    internal void ShowPlayniteFolderForValidation(string folder)
    {
        if (!diagnostic)
            throw new InvalidOperationException("Folder presentation fixtures require diagnostic mode.");
        OpenView(new(OverlayRoute.PlayniteBrowser, SelectedId: "playnite-loading", BrowserFolder: folder));
    }

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
