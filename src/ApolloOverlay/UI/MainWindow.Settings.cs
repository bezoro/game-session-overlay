using System.IO;
using System.Windows.Controls;
using Microsoft.Win32;

namespace ApolloOverlay;

public partial class MainWindow
{
    internal Func<Task<string?>>? PlayniteDialogFixture
    {
        private get;
        set;
    }
    internal Task DialogTask
    {
        get;
        private set;
    } = Task.CompletedTask;

    private UserSettings appliedSettings;
    private void CycleDisplay()
    {
        var ids = new string?[]
        {
            null
        }.Concat(Controller.Displays.Select(d => (string?)d.Id)).ToArray();
        var index = Array.IndexOf(ids, Controller.Settings.DisplayId);
        Change(Controller.Settings with
        {
            DisplayId = ids[(index + 1) % ids.Length]
        });
    }

    private string GuideLimitTitle() => "Guide open limit · " + (Controller.Settings.GuideOpenMaxHoldMs / 1000d).ToString("0.0", System.Globalization.CultureInfo.GetCultureInfo("en-US")) + " s";
    private bool AdjustGuideLimit(NavDirection direction)
    {
        if (navigation.Id != "guide-limit" || direction is not (NavDirection.Left or NavDirection.Right))
            return false;
        if (entries.FirstOrDefault(e => e.Id == "guide-limit")?.Button.Content is ActionButtonContent content && content.Texts.Children.OfType<Slider>().FirstOrDefault() is { } slider)
            slider.Value = Math.Clamp(slider.Value + (direction == NavDirection.Left ? -100 : 100), 500, 3000);
        return true;
    }

    private void ApplySettings()
    {
        var previous = appliedSettings;
        var settings = Controller.Settings;
        appliedSettings = settings;
        ApplyAppearanceTheme();
        if (settings.SoundFeedbackEnabled != previous.SoundFeedbackEnabled || settings.HapticFeedbackEnabled != previous.HapticFeedbackEnabled)
            feedbackService.Suspend();
        feedbackService.Configure(settings.SoundFeedbackEnabled, settings.HapticFeedbackEnabled);
        if (!OpenAnimationAllowed)
            CancelOpenAnimation();
        if (settings.Deadzone != previous.Deadzone)
        {
            input = new(settings);
            input.Cancel();
        }

        if (settings.GuideXInputEnabled != previous.GuideXInputEnabled)
            ConfigureGuide();
        if (IsVisible && (settings.DisplayId != previous.DisplayId || settings.UiScalePercent != previous.UiScalePercent || settings.PanelSide != previous.PanelSide))
            Place();
    }

    private void Change(UserSettings settings)
    {
        var before = Controller.Settings;
        var soundJustEnabled = settings.SoundFeedbackEnabled && !Controller.Settings.SoundFeedbackEnabled;
        Controller.Change(settings);
        LogSettingsChanges(before, Controller.Settings);
        LogCommandResult("settings-change", ResultKind.Completed, "draft-changed");
        ApplySettings();
        RefreshRows();
        if (soundJustEnabled)
            RequestCue(FeedbackCue.Confirmation);
        FeedbackText("Unsaved change · Save settings to keep it.");
    }

    private void Save()
    {
        using var command = BeginCommand("settings-save");
        var fromFooter = navigation.Id == "save-pending";
        var returnTo = lastContentSelection;
        try
        {
            Controller.Save();
            command?.Complete(Controller.Platform.IsPreview ? ResultKind.Simulated : ResultKind.Completed,
                Controller.Platform.IsPreview ? "preview-not-saved" : "settings-persisted");
            RetainOperation(Controller.Platform.IsPreview ? "Simulated" : "Completed", Controller.Status);
            if (fromFooter && !Controller.SettingsDirty)
                navigation = new(entries.Any(entry => entry.Id == returnTo && !IsFooterAction(entry.Id)) ? returnTo : "settings");
            FeedbackText(Controller.Status);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            command?.Fail(e, "settings-save-failed");
            RetainOperation("Failed", "Could not save; previous settings preserved. Try saving again or discard your changes.");
            FeedbackText("Could not save; previous settings preserved.");
        }

        RefreshRows();
    }

    private void ConfigurePlaynite()
    {
        if (PlayniteDialogFixture is not null)
        {
            DialogTask = ConfigurePlayniteCore();
            return;
        }

        OpenView(new(OverlayRoute.PlayniteBrowser, SelectedId: "playnite-loading"));
    }

    private async Task ConfigurePlayniteCore()
    {
        using var command = BeginCommand("playnite-picker");
        if (Controller.Platform.IsPreview && PlayniteDialogFixture is null)
        {
            FeedbackText("Preview: no settings changed.");
            return;
        }

        ownedDialog = true;
        RevokeFocus("dialog");
        var cycle = focusCycle;
        try
        {
            string? path;
            if (PlayniteDialogFixture is { } fixture)
                path = await fixture();
            else
            {
                var dialog = new OpenFileDialog
                {
                    Title = "Choose Playnite.FullscreenApp.exe",
                    Filter = "Playnite Fullscreen|Playnite.FullscreenApp.exe",
                    CheckFileExists = true
                };
                path = dialog.ShowDialog(this) == true ? dialog.FileName : null;
            }

            if (path is not null && CurrentCycle(cycle))
            {
                Change(Controller.Settings with
                {
                    PlaynitePath = path
                });
                command?.Complete(ResultKind.Completed, "playnite-path-selected");
            }
            else
                command?.Complete(ResultKind.Cancelled, "picker-dismissed-or-superseded");
        }
        catch (OperationCanceledException)
        {
            command?.Complete(ResultKind.Cancelled, "picker-cancelled");
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            command?.Fail(e, "picker-failed");
            if (CurrentCycle(cycle))
                FeedbackText("Could not select Playnite.");
        }
        finally
        {
            ownedDialog = false;
            if (CurrentCycle(cycle))
            {
                var foreground = FocusForeground();
                RevokeFocus("dialog-return");
                var resumed = focusCycle;
                var focus = AcquireFocus(resumed, foreground);
                OpeningTask = focus;
                if (await focus && CurrentCycle(resumed))
                {
                    if (view.Current.Route == OverlayRoute.PlayniteBrowser)
                        RefreshRows();
                    SetSelection();
                }
            }
        }
    }
}
