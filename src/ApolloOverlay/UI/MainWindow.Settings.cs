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

    private string GuideLimitTitle() => "Guide · opening hold limit " + (Controller.Settings.GuideOpenMaxHoldMs / 1000d).ToString("0.0", System.Globalization.CultureInfo.GetCultureInfo("en-US")) + " s";
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
        var soundJustEnabled = settings.SoundFeedbackEnabled && !Controller.Settings.SoundFeedbackEnabled;
        Controller.Change(settings);
        ApplySettings();
        RefreshRows();
        if (soundJustEnabled)
            RequestCue(FeedbackCue.Confirmation);
        FeedbackText("Unsaved change · Save settings to keep it.");
    }

    private void Save()
    {
        var fromFooter = navigation.Id == "save-pending";
        var returnTo = lastContentSelection;
        try
        {
            Controller.Save();
            RetainOperation(Controller.Platform.IsPreview ? "Simulated" : "Completed", Controller.Status);
            if (fromFooter && !Controller.SettingsDirty)
                navigation = new(entries.Any(entry => entry.Id == returnTo && !IsFooterAction(entry.Id)) ? returnTo : "settings");
            FeedbackText(Controller.Status);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
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
                Change(Controller.Settings with
                {
                    PlaynitePath = path
                });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            if (CurrentCycle(cycle))
                FeedbackText("Could not select Playnite.");
        }
        finally
        {
            ownedDialog = false;
            if (CurrentCycle(cycle))
            {
                RevokeFocus("dialog-return");
                var resumed = focusCycle;
                var focus = AcquireFocus(resumed);
                OpeningTask = focus;
                if (await focus && CurrentCycle(resumed))
                {
                    if (view.Current.Route == OverlayRoute.PlayniteBrowser)
                        RefreshRows();
                    SetSelection(false);
                }
            }
        }
    }
}
