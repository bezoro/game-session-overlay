using System.IO;
using System.Windows;

namespace ApolloOverlay;

public partial class MainWindow
{
    private AppearanceSettings? appearanceDraft;
    private AppearanceSettings? appearanceSafePreview;
    private bool appearancePreviewLight;
    private PaletteRole? appearanceColorRole;
    private string appearanceColorValue = "";
    private AppearanceSettings? appearanceColorProposal;
    private AppearanceSettings? appearanceColorPaintBefore;
    private bool appearanceColorAccepted;

    internal bool AppearanceDirty => appearanceDraft is { } draft
        && (!AppearanceDraftValid(out _) || AppearanceCatalog.Normalize(draft) != AppearanceCatalog.Normalize(Controller.Settings.Appearance));
    private bool AppearanceContext => view.Current.Route is OverlayRoute.Appearance or OverlayRoute.ColorEdit
        || view.Current.Route == OverlayRoute.Keyboard && keyboardPurpose == TextEntryPurpose.ColorHex;

    internal void OpenAppearance()
    {
        if (busy || TransitionPending || Confirming)
            return;
        appearanceDraft = AppearanceCatalog.Normalize(Controller.Settings.Appearance);
        appearanceSafePreview = appearanceDraft;
        appearancePreviewLight = ThemeSupport.UsesLightPalette(appearanceDraft);
        appearanceColorRole = null;
        appearanceColorProposal = null;
        OpenView(new(OverlayRoute.Appearance, SelectedId: "appearance-mode"));
        ApplyAppearanceTheme();
    }

    private void AppearanceNavigationChanged()
    {
        if (!AppearanceContext)
        {
            appearanceDraft = null;
            appearanceSafePreview = null;
            appearanceColorRole = null;
            appearanceColorProposal = null;
        }
        else if (view.Current.Route == OverlayRoute.Appearance)
        {
            if (appearanceColorRole is not null && !appearanceColorAccepted)
                appearanceSafePreview = appearanceColorPaintBefore;
            appearanceColorRole = null;
            appearanceColorProposal = null;
            appearanceColorPaintBefore = null;
            appearanceColorAccepted = false;
        }
        ApplyAppearanceTheme();
    }

    private void ApplyAppearanceTheme()
    {
        var settings = Controller.Settings.Appearance;
        if (AppearanceContext && appearanceDraft is { } draft)
        {
            var proposed = appearanceColorProposal ?? draft;
            var palette = AppearanceCatalog.Resolve(proposed, appearancePreviewLight);
            appearanceSafePreview ??= AppearanceCatalog.Normalize(Controller.Settings.Appearance);
            if (AppearanceCatalog.TryValidate(palette, out _))
                appearanceSafePreview = appearancePreviewLight
                    ? appearanceSafePreview with
                    {
                        Preset = proposed.Preset,
                        Light = palette
                    }
                    : appearanceSafePreview with
                    {
                        Preset = proposed.Preset,
                        Dark = palette
                    };
            settings = appearanceSafePreview with
            {
                Mode = appearancePreviewLight ? "Light" : "Dark"
            };
        }
        ThemeSupport.ApplyAppearance(Application.Current, settings);
    }

    private bool AppearanceDraftValid(out string reason)
    {
        reason = "Choose an appearance first.";
        if (appearanceDraft is not { } draft)
            return false;
        foreach (var light in new[] { false, true })
            if (!AppearanceCatalog.TryValidate(AppearanceCatalog.Resolve(draft, light), out reason))
            {
                reason = (light ? "Light palette: " : "Dark palette: ") + reason;
                return false;
            }
        return true;
    }

    private void SetAppearanceDraft(AppearanceSettings draft)
    {
        appearanceDraft = draft;
        ApplyAppearanceTheme();
        RefreshRows();
    }

    private void SaveAppearanceDraft()
    {
        if (appearanceDraft is not { } draft)
            return;
        try
        {
            var result = Controller.SaveAppearance(draft);
            if (result.Kind is ResultKind.Completed or ResultKind.Simulated)
                appearanceDraft = AppearanceCatalog.Normalize(Controller.Settings.Appearance);
            RecordResult(result, "appearance-save");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            RecordResult(new(ResultKind.Failed, "Colors could not be saved. Your preview is still available; retry or discard it."), "appearance-save");
        }
        ApplyAppearanceTheme();
        RefreshRows();
    }

    private void DiscardAppearanceDraft()
    {
        appearanceDraft = AppearanceCatalog.Normalize(Controller.Settings.Appearance);
        appearanceSafePreview = appearanceDraft;
        appearancePreviewLight = ThemeSupport.UsesLightPalette(Controller.Settings.Appearance);
        appearanceColorRole = null;
        appearanceColorProposal = null;
        ApplyAppearanceTheme();
        RefreshRows();
    }

    internal void OpenAppearanceColor(PaletteRole role)
    {
        if (appearanceDraft is not { } draft || busy || TransitionPending || Confirming)
            return;
        appearanceColorRole = role;
        appearanceColorPaintBefore = appearanceSafePreview;
        appearanceColorAccepted = false;
        appearanceColorValue = AppearanceCatalog.Resolve(draft, appearancePreviewLight).Get(role);
        appearanceColorProposal = null;
        OpenView(new(OverlayRoute.ColorEdit, SelectedId: "color-red"));
    }

    private bool TryColorProposal(out AppearanceSettings? proposal, out string reason)
    {
        proposal = null;
        reason = "Choose a color to edit.";
        if (appearanceDraft is not { } draft || appearanceColorRole is not { } role
            || !AppearanceCatalog.TryColor(appearanceColorValue, out var canonical))
            return false;
        proposal = AppearanceCatalog.WithColor(draft, appearancePreviewLight, role, canonical);
        return AppearanceCatalog.TryValidate(AppearanceCatalog.Resolve(proposal, appearancePreviewLight), out reason);
    }

    private void ApplyAppearanceColor()
    {
        TryColorProposal(out var proposal, out var reason);
        if (proposal is null)
        {
            FeedbackText(reason, important: true);
            return;
        }
        appearanceDraft = proposal;
        appearanceColorAccepted = true;
        appearanceColorProposal = null;
        Back();
    }
}
