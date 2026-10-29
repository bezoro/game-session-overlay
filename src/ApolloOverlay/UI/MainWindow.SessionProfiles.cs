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
        Paragraph("Guarde monitor, tamanho do texto e saída de áudio para cada ambiente.");
        if (sessionProfiles.RecoveryNotice is { } notice)
            Paragraph(notice);
        foreach (var profile in sessionProfiles.Items)
        {
            var chosen = profile;
            AddRow("profile:" + profile.Id, profile.Name, (sessionProfiles.ActiveId == profile.Id ? "Último aplicado · " : profile.GameKey == Controller.Session.Anchor?.ProcessName ? "Sugerido para este jogo · " : "") + profile.ScalePercent + "% · " + DisplayName(profile.DisplayId) + (profile.OutputName is { } output ? " · " + output : ""), () => ApplySessionProfile(chosen), !Controller.SettingsDirty);
            AddRow("profile-edit:" + profile.Id, "Ajustar " + profile.Name, "Monitor, texto e áudio deste perfil", () =>
            {
                editingProfile = chosen.Id;
                profileDraft = chosen;
                OpenFeature(OverlayRoute.ProfileEdit, "profile-name");
            });
        }

        if (Controller.SettingsDirty)
            Paragraph("Salve ou descarte os ajustes pendentes antes de aplicar um perfil.");
        AddRow("profile-new", "Criar perfil", "Guardar outro ambiente ou uma configuração para um jogo", () =>
        {
            var settings = Controller.Settings;
            editingProfile = Guid.NewGuid().ToString("N");
            profileDraft = new(editingProfile, "Novo perfil", settings.DisplayId, settings.UiScalePercent, settings.PanelSide);
            OpenFeature(OverlayRoute.ProfileEdit, "profile-name");
        });
        if (profileBefore is not null)
            AddRow("profile-undo", "Desfazer último perfil", "Restaura somente valores que ainda correspondem ao perfil aplicado", UndoSessionProfile, !Controller.SettingsDirty);
        AddRow("back", "Voltar", "Painel da sessão", Back);
    }

    private string DisplayName(string? id) => id is null ? "Monitor ativo" : Controller.Displays.FirstOrDefault(display => display.Id == id) is { } display ? $"Monitor {Array.IndexOf(Controller.Displays.ToArray(), display) + 1}" : "Monitor desconectado";
    private void BuildProfileEditor()
    {
        var draft = profileDraft ?? sessionProfiles.Items.First(profile => profile.Id == editingProfile);
        profileDraft = draft;
        AddRow("profile-name", "Nome · " + draft.Name, "Editar pelo controle ou teclado", () => OpenKeyboard(true));
        AddRow("profile-presentation", "Interface · " + (draft.Presentation == "Desktop" ? "Computador" : "TV"), "Painel compacto ou leitura ampliada", () =>
        {
            profileDraft = draft with
            {
                Presentation = draft.Presentation == "Desktop" ? "TV" : "Desktop"
            };
            RefreshRows();
        });
        AddRow("profile-game", draft.GameKey is null ? "Jogo · sem associação" : "Jogo · " + draft.GameKey, "Sugerir este perfil quando o jogo estiver selecionado", () =>
        {
            profileDraft = draft with
            {
                GameKey = draft.GameKey is null ? Controller.Session.Anchor?.ProcessName : null
            };
            RefreshRows();
        }, draft.GameKey is not null || Controller.Session.Anchor is not null);
        AddRow("profile-monitor", "Monitor · " + DisplayName(draft.DisplayId), "Tela da overlay, sem alterar a resolução do jogo", () =>
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
        AddRow("profile-scale", "Texto · " + draft.ScalePercent + "%", "100 / 115 / 130 / 150%", () =>
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
        AddRow("profile-side", "Painel · " + (draft.Side == "Right" ? "direita" : "esquerda"), "Posição neste ambiente", () =>
        {
            profileDraft = draft with
            {
                Side = draft.Side == "Right" ? "Left" : "Right"
            };
            RefreshRows();
        });
        AddRow("profile-output", "Áudio · " + (draft.OutputName ?? "Manter saída atual"), "Alternar entre as saídas conectadas", () =>
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
        AddRow("profile-volume", draft.VolumePercent is { } volume ? $"Volume · {volume}%" : "Volume · manter atual", "Incluir o volume atual neste perfil ou preservar o volume em uso", () =>
        {
            profileDraft = draft with
            {
                VolumePercent = draft.VolumePercent is null && audio.Current.Available ? (int)Math.Round(audio.Current.Volume * 100) : null
            };
            RefreshRows();
        });
        AddRow("profile-current", "Usar disposição atual", "Copiar monitor, escala e lado atuais", () =>
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
        AddRow("profile-save", "Salvar perfil", "Guardar este ambiente sem aplicá-lo agora", () =>
        {
            if (Controller.Platform.IsPreview)
            {
                RecordResult(new(ResultKind.Simulated, "Prévia: o perfil não foi gravado."), "profile-save");
                return;
            }

            try
            {
                sessionProfiles.Save(profileDraft!);
                RecordResult(new(ResultKind.Completed, "Perfil salvo."), "profile-save");
                Back();
            }
            catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                RecordResult(new(ResultKind.Failed, "Não foi possível salvar o perfil. Os perfis anteriores foram preservados."), "profile-save");
            }
        });
        AddRow("back", "Voltar", "Alterações do perfil só são guardadas ao salvar", Back);
    }

    private void ApplySessionProfile(SessionProfile profile)
    {
        if (Controller.SettingsDirty)
        {
            FeedbackText("Salve ou descarte os ajustes antes de aplicar um perfil.");
            return;
        }

        StartAudioAction("profile-apply", async token =>
        {
            if (profile.DisplayId is { } id && !Controller.RefreshDisplays().Any(display => display.Id == id))
                return new(ResultKind.Unavailable, "O monitor deste perfil está desconectado. Ajuste o perfil antes de aplicar.");
            var before = Controller.Settings;
            var state = await systemAudio.RefreshAsync(token);
            if (profile.OutputId is { } output && !state.Outputs.Any(device => device.Id == output))
                return new(ResultKind.Unavailable, "A saída deste perfil está desconectada. Seus ajustes atuais foram mantidos.");
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
                        return new(ResultKind.Partial, "A saída foi aplicada, mas o volume não foi confirmado. Confira o áudio antes de continuar.");
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
                return new(ResultKind.Partial, "O ambiente foi aplicado, mas não pôde ser salvo por completo. Confira os ajustes pendentes.");
            }

            return new(diagnostic || Controller.Platform.IsPreview ? ResultKind.Simulated : ResultKind.Completed, "Perfil " + profile.Name + " aplicado.");
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
            return new(skipped ? ResultKind.Partial : diagnostic || Controller.Platform.IsPreview ? ResultKind.Simulated : ResultKind.Completed, skipped ? "Disposição restaurada. O áudio mudou depois do perfil e foi preservado." : "Disposição anterior restaurada sem substituir ajustes posteriores.");
        }, RefreshRows);
    }
}
