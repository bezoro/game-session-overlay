using System.IO;

namespace ApolloOverlay;

public sealed partial class OverlayController
{
    // The dispatcher owns this reservation and both the saved and live settings.
    // The worker receives a private snapshot and only performs the atomic disk write.
    private bool settingsWriteInProgress;

    private void RequireSettingsWriterAvailable()
    {
        if (settingsWriteInProgress)
            throw new IOException("A settings change is being saved. Try again after it completes.");
    }

    internal async Task SaveAutomationSettings(UserSettings next, CancellationToken cancellation, Func<bool> continuing)
    {
        RequireSettingsWriterAvailable();
        var original = Settings;
        settingsWriteInProgress = true;
        try
        {
            await Task.Run(() =>
            {
                cancellation.ThrowIfCancellationRequested();
                if (!continuing())
                    throw new OperationCanceledException("The client disconnected before settings were saved.");
                Diagnostics.Save(next);
            }, cancellation);
            SavedSettings = next;
            // Preserve drafts made after admission, including edits to unrelated settings.
            var live = Settings;
            Settings = live with
            {
                SoundFeedbackEnabled = live.SoundFeedbackEnabled == original.SoundFeedbackEnabled ? next.SoundFeedbackEnabled : live.SoundFeedbackEnabled,
                HapticFeedbackEnabled = live.HapticFeedbackEnabled == original.HapticFeedbackEnabled ? next.HapticFeedbackEnabled : live.HapticFeedbackEnabled,
                PanelSide = live.PanelSide == original.PanelSide ? next.PanelSide : live.PanelSide,
                UiScalePercent = live.UiScalePercent == original.UiScalePercent ? next.UiScalePercent : live.UiScalePercent,
                Deadzone = live.Deadzone == original.Deadzone ? next.Deadzone : live.Deadzone,
                GuideOpenMaxHoldMs = live.GuideOpenMaxHoldMs == original.GuideOpenMaxHoldMs ? next.GuideOpenMaxHoldMs : live.GuideOpenMaxHoldMs
            };
        }
        finally
        {
            settingsWriteInProgress = false;
        }
    }
}
