namespace ApolloOverlay;

public partial class MainWindow
{
    private SessionProfiles sessionProfiles = null!;
    private string editingProfile = "tv";
    private SessionProfile? profileDraft;
    private UserSettings? profileBefore, profileApplied;
    private SystemAudioSnapshot? profileAudioBefore, profileAudioApplied;
    private AudioSnapshot? profileMasterBefore, profileMasterApplied;
    private string profileActiveBefore = "", profileActiveApplied = "", profilePresentationBefore = "TV";
    private bool DesktopPresentation => sessionProfiles?.AppliedPresentation == "Desktop";

    private void BuildSessionProfiles()
    {
        FeatureHeading("Choose a setup", "Apply a saved display and audio setup, or edit it before use.", "session-profiles");
        if (sessionProfiles.RecoveryNotice is { } notice)
            Paragraph(notice);
        foreach (var profile in sessionProfiles.Items)
        {
            var chosen = profile;
            AddRow("profile:" + profile.Id, profile.Name, (sessionProfiles.ActiveId == profile.Id ? "Last applied · " : profile.GameKey == Controller.Session.Anchor?.ProcessName ? "Suggested for this game · " : "") + profile.ScalePercent + "% · " + DisplayName(profile.DisplayId) + (profile.OutputName is { } output ? " · " + output : ""), () => ApplySessionProfile(chosen), !Controller.SettingsDirty);
            AddRow("profile-edit:" + profile.Id, "Edit " + profile.Name, "Display, text size and audio for this profile", () =>
            {
                editingProfile = chosen.Id;
                profileDraft = chosen;
                OpenFeature(OverlayRoute.ProfileEdit, "profile-name");
            });
        }

        if (Controller.SettingsDirty)
            Paragraph("Save or discard pending settings before applying a profile.");
        AddRow("profile-new", "Create profile", "Save another setup or a configuration for a game", () =>
        {
            var settings = Controller.Settings;
            editingProfile = Guid.NewGuid().ToString("N");
            profileDraft = new(editingProfile, "New profile", settings.DisplayId, settings.UiScalePercent, settings.PanelSide);
            OpenFeature(OverlayRoute.ProfileEdit, "profile-name");
        });
        if (profileBefore is not null)
            AddRow("profile-undo", "Undo last profile", "Restores only values that still match the applied profile", UndoSessionProfile, !Controller.SettingsDirty);
        AddRow("back", "Back", "Session panel", Back);
    }

    private string DisplayName(string? id) => id is null ? "Active display" : Controller.Displays.FirstOrDefault(display => display.Id == id) is { } display ? $"Display {Array.IndexOf(Controller.Displays.ToArray(), display) + 1}" : "Display disconnected";
    private void BuildProfileEditor()
    {
        var draft = profileDraft ?? sessionProfiles.Items.First(profile => profile.Id == editingProfile);
        profileDraft = draft;
        FeatureHeading(draft.Name, "Saving keeps this profile for later. It does not apply the setup.", "profile-name");
        Section("Profile");
        AddRow("profile-name", "Name · " + draft.Name, "Edit with a controller or keyboard", () => OpenKeyboard(true));
        AddRow("profile-presentation", "Interface · " + (draft.Presentation == "Desktop" ? "Desktop" : "TV"), "Compact panel or larger text", () =>
        {
            profileDraft = draft with
            {
                Presentation = draft.Presentation == "Desktop" ? "TV" : "Desktop"
            };
            RefreshRows();
        });
        AddRow("profile-game", draft.GameKey is null ? "Game · not linked" : "Game · " + draft.GameKey, "Suggest this profile when the game is selected", () =>
        {
            profileDraft = draft with
            {
                GameKey = draft.GameKey is null ? Controller.Session.Anchor?.ProcessName : null
            };
            RefreshRows();
        }, draft.GameKey is not null || Controller.Session.Anchor is not null);
        Section("Display");
        AddRow("profile-monitor", "Display · " + DisplayName(draft.DisplayId), "Overlay display, without changing the game resolution", () =>
        {
            var ids = new string?[]
            {
                null
            }.Concat(Controller.Displays.Select(display => (string?)display.Id)).ToArray();
            profileDraft = draft with
            {
                DisplayId = ids[(Array.IndexOf(ids, draft.DisplayId) + 1) % ids.Length]
            };
            RefreshRows();
        });
        AddRow("profile-scale", "Text · " + draft.ScalePercent + "%", "100 / 115 / 130 / 150%", () =>
        {
            var scales = new[]
            {
                100,
                115,
                130,
                150
            };
            profileDraft = draft with
            {
                ScalePercent = scales[(Array.IndexOf(scales, draft.ScalePercent) + 1) % scales.Length]
            };
            RefreshRows();
        });
        AddRow("profile-side", "Panel · " + (draft.Side == "Right" ? "right" : "left"), "Position in this setup", () =>
        {
            profileDraft = draft with
            {
                Side = draft.Side == "Right" ? "Left" : "Right"
            };
            RefreshRows();
        });
        Section("Audio");
        AddRow("profile-output", "Audio · " + (draft.OutputName ?? "Keep current output"), "Cycle through connected outputs", () =>
        {
            var outputs = systemAudio.Current.Outputs;
            var index = outputs.ToList().FindIndex(output => output.Id == draft.OutputId);
            var next = index + 1 >= outputs.Count ? null : outputs[index + 1];
            profileDraft = draft with
            {
                OutputId = next?.Id,
                OutputName = next?.Name
            };
            RefreshRows();
        });
        AddRow("profile-volume", draft.VolumePercent is { } volume ? $"Volume · {volume}%" : "Volume · keep current", "Include the current volume in this profile or preserve the volume in use", () =>
        {
            profileDraft = draft with
            {
                VolumePercent = draft.VolumePercent is null && audio.Current.Available ? (int)Math.Round(audio.Current.Volume * 100) : null
            };
            RefreshRows();
        });
        Section("Save for later");
        AddRow("profile-current", "Use current layout", "Copy the current display, size and side", () =>
        {
            var settings = Controller.Settings;
            profileDraft = draft with
            {
                DisplayId = settings.DisplayId,
                ScalePercent = settings.UiScalePercent,
                Side = settings.PanelSide
            };
            RefreshRows();
        });
        AddRow("profile-save", "Save profile", "Save this setup without applying it now", () =>
        {
            if (Controller.Platform.IsPreview)
            {
                RecordResult(new(ResultKind.Simulated, "Preview: the profile was not saved."), "profile-save");
                return;
            }

            try
            {
                sessionProfiles.Save(profileDraft!);
                RecordResult(new(ResultKind.Completed, "Profile saved."), "profile-save");
                Back();
            }
            catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                RecordResult(new(ResultKind.Failed, "Could not save the profile. Existing profiles were preserved."), "profile-save");
            }
        });
        AddRow("back", "Back", "Profile changes are kept only when you save", Back);
    }

    private void ApplySessionProfile(SessionProfile profile)
    {
        if (Controller.SettingsDirty)
        {
            FeedbackText("Save or discard settings before applying a profile.");
            return;
        }

        StartAudioAction("profile-apply", async token =>
        {
            if (profile.DisplayId is { } id && !Controller.RefreshDisplays().Any(display => display.Id == id))
                return new(ResultKind.Unavailable, "This profile's display is disconnected. Edit the profile before applying it.");
            var before = Controller.Settings;
            var state = await systemAudio.RefreshAsync(token);
            if (profile.OutputId is { } output && !state.Outputs.Any(device => device.Id == output))
                return new(ResultKind.Unavailable, "This profile's audio output is disconnected. Your current settings were preserved.");
            profileBefore = before;
            profileApplied = before;
            profileAudioBefore = state;
            profileAudioApplied = state;
            profileMasterBefore = profileMasterApplied = null;
            profileActiveBefore = sessionProfiles.ActiveId;
            profilePresentationBefore = sessionProfiles.AppliedPresentation;
            profileActiveApplied = profile.Id;
            if (profile.OutputId is { } selected)
            {
                var changed = await systemAudio.SelectOutputAsync(state.ConsoleOutputId, state.MediaOutputId, selected, token);
                profileAudioApplied = systemAudio.Current;
                if (changed.Kind is not (ResultKind.Completed or ResultKind.Simulated))
                    return changed;
                if (!diagnostic && !Controller.Platform.IsPreview)
                    audio.SetVisible(true);
            }

            token.ThrowIfCancellationRequested();
            if (profile.VolumePercent is { } volume && audio.Current.Available)
            {
                if (!diagnostic && !Controller.Platform.IsPreview)
                {
                    profileMasterBefore = audio.Read();
                    var changed = audio.Set(volume / 100f, expectedEndpoint: profile.OutputId ?? audio.Current.EndpointId);
                    profileMasterApplied = audio.Read();
                    if (changed.Kind != ResultKind.Completed)
                        return new(ResultKind.Partial, "The output was applied, but volume was not confirmed. Check the audio before continuing.");
                }
            }

            token.ThrowIfCancellationRequested();
            profileApplied = SessionProfiles.ApplyLayout(before, profile);
            Controller.Change(profileApplied);
            ApplySettings();
            try
            {
                Controller.Save();
                if (!Controller.Platform.IsPreview)
                    sessionProfiles.SetActive(profile.Id);
                Place();
            }
            catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return new(ResultKind.Partial, "The setup was applied, but could not be fully saved. Check your pending settings.");
            }

            return new(diagnostic || Controller.Platform.IsPreview ? ResultKind.Simulated : ResultKind.Completed, "Profile " + profile.Name + " applied.");
        }, RefreshRows);
    }

    private void UndoSessionProfile()
    {
        if (profileBefore is not { } before || profileApplied is not { } applied || Controller.SettingsDirty)
            return;
        StartAudioAction("profile-undo", async token =>
        {
            var current = await systemAudio.RefreshAsync(token);
            var skipped = false;
            if (profileMasterBefore is { } oldVolume && profileMasterApplied is { } expectedVolume)
            {
                var observed = audio.Read();
                var decision = AudioRestore.Decide(oldVolume, expectedVolume, observed);
                if (decision.Volume)
                {
                    var restored = audio.Set(oldVolume.Volume, expectedEndpoint: oldVolume.EndpointId);
                    skipped = restored.Kind != ResultKind.Completed;
                }
                else
                    skipped = true;
            }

            if (profileAudioBefore is { } previous
                && profileAudioApplied is { } expected
                && (previous.ConsoleOutputId != expected.ConsoleOutputId
                || previous.MediaOutputId != expected.MediaOutputId))
            {
                if (current.ConsoleOutputId == expected.ConsoleOutputId && current.MediaOutputId == expected.MediaOutputId)
                {
                    var restored = await systemAudio.RestoreOutputsAsync(expected.ConsoleOutputId, expected.MediaOutputId, previous.ConsoleOutputId, previous.MediaOutputId, token);
                    skipped |= restored.Kind is not (ResultKind.Completed or ResultKind.Simulated);
                }
                else
                    skipped = true;
            }

            token.ThrowIfCancellationRequested();
            var layout = SessionProfiles.RestoreUnchangedLayout(before, applied, Controller.Settings);
            Controller.Change(layout);
            ApplySettings();
            Controller.Save();
            if (!Controller.Platform.IsPreview && sessionProfiles.ActiveId == profileActiveApplied)
                sessionProfiles.SetActive(profileActiveBefore, profilePresentationBefore);
            Place();
            profileBefore = null;
            profileApplied = null;
            profileMasterBefore = profileMasterApplied = null;
            return new(skipped ? ResultKind.Partial : diagnostic || Controller.Platform.IsPreview ? ResultKind.Simulated : ResultKind.Completed, skipped ? "Layout restored. Audio changed after the profile was applied and was preserved." : "Previous layout restored without replacing later changes.");
        }, RefreshRows);
    }
}
