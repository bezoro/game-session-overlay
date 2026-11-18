using ApolloOverlay.Automation;

namespace ApolloOverlay;

public partial class MainWindow
{
    private Task? standaloneOperation;

    private static bool LegacyAudioAction(string id) => id is "volume" or "volume-down" or "mute";
    private static bool StandaloneAction(string id) =>
        id is "audio-get" or "audio-set" or "settings-get" or "settings-set" or "media-get" or "media-control";

    private static bool OperationPayloadMatches(AutomationRequest request) =>
        (request.AudioChange is null || request.ActionId == "audio-set")
        && (request.SettingsChange is null || request.ActionId == "settings-set")
        && (request.MediaCommand is null || request.ActionId == "media-control");

    private async Task<AutomationReply> ExecuteStandaloneAutomation(AutomationRequest request, CancellationToken token, Func<bool> continuing)
    {
        if (request.Intent != "activate" && request.ActionId != "volume" || request.TargetId.Length != 0)
            return AutomationReply.Error("Rejected", "This operation does not take a window target or that intent.");
        if (token.IsCancellationRequested || !continuing())
            return AutomationReply.Error("Cancelled", "The client is no longer connected.");

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        pending = cancellation;
        busy = true;
        var refreshSettings = false;
        try
        {
            var result = request.ActionId switch
            {
                "settings-get" or "settings-set" => ExecuteAutomationSettings(request, cancellation.Token, continuing),
                "media-get" or "media-control" => ExecuteAutomationMedia(request, cancellation.Token),
                _ => ExecuteAutomationAudio(request, cancellation.Token)
            };
            OperationTask = standaloneOperation = result;
            var reply = await result;
            refreshSettings = request.ActionId == "settings-set" && reply.Outcome == "Completed" && reply.Dispatched == true;
            return reply;
        }
        catch (OperationCanceledException)
        {
            return AutomationReply.Error("Cancelled", "Operation cancelled before dispatch.");
        }
        finally
        {
            if (ReferenceEquals(pending, cancellation))
                pending = null;
            standaloneOperation = null;
            busy = false;
            if (refreshSettings && !exiting && !closeRequested)
                RefreshRows();
        }
    }

    private async Task<AutomationReply> ExecuteAutomationAudio(AutomationRequest request, CancellationToken token)
    {
        var change = request.AudioChange;
        if (request.ActionId == "audio-set" && (change is null or { VolumePercent: null, Muted: null }
            || change.VolumePercent is < 0 or > 100))
            return AutomationReply.Error("Rejected", "Provide a volume from 0 to 100, a mute state, or both.");
        if (Controller.Platform.IsPreview || diagnostic)
            return AutomationReply.Error("Simulated", "Preview does not access Windows audio.");

        float? delta = request.ActionId switch
        {
            "volume-down" => -.05f,
            "volume" => request.Intent == "left" ? -.05f : .05f,
            _ => null
        };
        var observed = await audio.OperateAsync(change?.VolumePercent / 100f, change?.Muted, request.DryRun,
            token, delta, toggleMute: request.ActionId == "mute");
        var state = observed.Snapshot;
        return OperationReply(observed.Result, request.DryRun, observed.Dispatched) with
        {
            Audio = new(state.Available, state.Available ? (int)Math.Round(state.Volume * 100) : null,
                state.Available ? state.Muted : null, state.Status)
        };
    }

    private async Task<AutomationReply> ExecuteAutomationMedia(AutomationRequest request, CancellationToken token)
    {
        SessionMediaCommand? command = request.MediaCommand switch
        {
            "play" => SessionMediaCommand.Play,
            "pause" => SessionMediaCommand.Pause,
            "next" => SessionMediaCommand.Next,
            "previous" => SessionMediaCommand.Previous,
            _ => null
        };
        if (request.ActionId == "media-control" && command is null)
            return AutomationReply.Error("Rejected", "Use play, pause, next, or previous.");

        var observed = await sessionMedia.OperateCurrentAsync(command, request.DryRun, token);
        var current = observed.Snapshot.Items.FirstOrDefault(item => item.Id == observed.Snapshot.CurrentId);
        var state = current is null ? null : new AutomationMediaState(current.Title, current.Artist,
            current.Application, current.Playing, current.CanPrevious, current.CanNext);
        return OperationReply(observed.Result, request.DryRun, observed.Dispatched) with
        {
            Media = command is null ? state : null,
            MediaBefore = command is not null ? state : null
        };
    }

    private static AutomationReply OperationReply(ActionResult result, bool dryRun, bool dispatched) => new()
    {
        Outcome = dryRun && result.Kind == ResultKind.Completed ? "DryRun" : result.Kind.ToString(),
        Reason = dryRun && result.Kind == ResultKind.Completed ? "Operation available. No change was sent." : result.Message,
        Dispatched = dispatched
    };
}
