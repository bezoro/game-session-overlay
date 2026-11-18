using System.IO;
using ApolloOverlay.Automation;

namespace ApolloOverlay;

public partial class MainWindow
{
    internal async Task<AutomationReply> ExecuteAutomationSettings(AutomationRequest request, CancellationToken token, Func<bool> continuing)
    {
        var current = Controller.Settings;
        var change = request.SettingsChange;
        AutomationReply Reply(string outcome, string reason)
        {
            var settings = Controller.Settings;
            return AutomationReply.Error(outcome, reason, request.RequestId) with
            {
                Settings = new(settings.SoundFeedbackEnabled, settings.HapticFeedbackEnabled, settings.PanelSide,
                    settings.UiScalePercent, settings.Deadzone, settings.GuideOpenMaxHoldMs, Controller.SettingsDirty)
            };
        }

        if (request.ActionId == "settings-get")
            return change is null ? Reply("Completed", "Current overlay settings.") : Reply("Rejected", "Reading settings takes no changes.");
        if (request.ActionId != "settings-set" || change is null || change == new AutomationSettingsChange())
            return Reply("Rejected", "Provide at least one settings change.");
        if (change.PanelSide is not (null or "Left" or "Right") || change.ScalePercent is not (null or 100 or 115 or 130 or 150)
            || change.Deadzone is < 8000 or > 28000 || change.GuideHoldMs is int limit && !UserSettings.ValidGuideLimit(limit))
            return Reply("Rejected", "Invalid panel side, scale, deadzone or Guide hold limit.");
        if (Controller.SettingsDirty || AppearanceDirty)
            return Reply("Busy", "Save or discard pending settings first.");
        if (token.IsCancellationRequested || !continuing())
            return Reply("Cancelled", "Settings request cancelled before saving.");
        if (request.DryRun)
            return Reply("DryRun", "Settings change is valid.");
        var next = current with
        {
            SoundFeedbackEnabled = change.SoundEnabled ?? current.SoundFeedbackEnabled,
            HapticFeedbackEnabled = change.HapticsEnabled ?? current.HapticFeedbackEnabled,
            PanelSide = change.PanelSide ?? current.PanelSide,
            UiScalePercent = change.ScalePercent ?? current.UiScalePercent,
            Deadzone = change.Deadzone ?? current.Deadzone,
            GuideOpenMaxHoldMs = change.GuideHoldMs ?? current.GuideOpenMaxHoldMs
        };
        if (Controller.Platform.IsPreview)
            return Reply("Simulated", "Settings change simulated.");
        if (SessionHubSettings.Equivalent(current, next))
            return Reply("Completed", "The requested settings are already active.");
        try
        {
            await Controller.SaveAutomationSettings(next, token, continuing);
        }
        catch (OperationCanceledException)
        {
            return Reply("Cancelled", "Settings request cancelled before saving.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return Reply("Failed", "Settings could not be saved.");
        }
        if (!exiting && !closeRequested)
            ApplySettings();
        return Reply("Completed", "Settings saved.") with
        {
            Dispatched = true
        };
    }
}
