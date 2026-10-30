using ApolloOverlay.Automation;

namespace ApolloOverlay;

public partial class MainWindow
{
    private string? commandCorrelation;
    private string? commandOrigin;
    internal bool CommandLoggingFixture
    {
        get; set;
    }
    private bool CommandLoggingEnabled => CommandLoggingFixture || !diagnostic && !Controller.Platform.IsPreview;
    private string CommandSource => commandOrigin ?? (inputMethod is "controller" or "keyboard" or "mouse" ? inputMethod : "system");

    // Character keys are editing input, not independently journaled commands.
    private static bool IsCharacterCommand(string id) => id.StartsWith("key:", StringComparison.Ordinal);
    private static string DiagnosticCommandId(string id)
    {
        var separator = id.IndexOf(':');
        return separator < 0 ? id : id[..separator];
    }

    private CommandLogOperation? BeginCommand(string id, WindowRef? target = null, string? correlation = null, string? origin = null)
    {
        if (!CommandLoggingEnabled || IsCharacterCommand(id))
            return null;
        return Controller.Diagnostics.Commands.Begin(DiagnosticCommandId(id), origin ?? CommandSource,
            view.Current.Route.ToString(), target is null ? null : TargetId(target), correlation ?? commandCorrelation);
    }

    private void LogCommandResult(string id, ResultKind outcome, string? reason = null, Exception? error = null)
    {
        if (CommandLoggingEnabled && !IsCharacterCommand(id))
            Controller.Diagnostics.Commands.Record(DiagnosticCommandId(id), outcome, CommandSource,
                reason, error, commandCorrelation);
    }

    private AutomationDiagnosticsStatus? LogDiagnosticsSnapshot()
    {
        if (!CommandLoggingEnabled)
            return null;
        var journal = Controller.Diagnostics.Commands;
        var status = journal.Status;
        return new(journal.DirectoryPath, status.BufferedEvents, status.DroppedEvents, status.WriteFailures,
            status.UntrackedOperations, status.ActiveOperations, status.DrainTimedOut,
            status.LastErrorType, status.LastErrorHResult);
    }

    private void RunSemanticCommand(string id, Action action)
    {
        if (interfaceActionDepth != 0)
        {
            action();
            return;
        }
        var previous = commandCorrelation;
        commandCorrelation ??= Guid.NewGuid().ToString("N");
        using var operation = BeginCommand(id);
        try
        {
            action();
            operation?.Complete(ResultKind.Requested, "interface-handler-returned");
        }
        catch (Exception error)
        {
            operation?.Fail(error, "interface-handler-threw");
            throw;
        }
        finally
        {
            commandCorrelation = previous;
        }
    }

    // Capture the CLI origin while invoking the handler, then restore dispatcher
    // state immediately. Deferred handoffs retain their own captured context.
    private Task<AutomationReply> ExecuteLoggedAutomation(
        AutomationRequest request, CancellationToken token,
        Action<AutomationReply> phase, Func<bool> continuing)
    {
        var previousCorrelation = commandCorrelation;
        var previousOrigin = commandOrigin;
        commandCorrelation = request.RequestId;
        commandOrigin = "cli";
        CommandLogOperation? command = null;
        try
        {
            var id = entries.Any(entry => entry.Id == request.ActionId)
                || request.ActionId is "focus" or "steam-overlay" or "test-steam" or "session-options"
                ? request.ActionId : request.ActionId.StartsWith("route:", StringComparison.Ordinal) ? "route" : "unsupported-command";
            command = BeginCommand(id);
            return FinishAutomationCommand(command, AutomationExecute(request, token, phase, continuing));
        }
        catch (Exception error)
        {
            command?.Fail(error, "automation-handler-threw");
            command?.Dispose();
            throw;
        }
        finally
        {
            commandCorrelation = previousCorrelation;
            commandOrigin = previousOrigin;
        }
    }

    private static async Task<AutomationReply> FinishAutomationCommand(
        CommandLogOperation? command, Task<AutomationReply> task)
    {
        using (command)
            try
            {
                var reply = await task;
                var outcome = reply.Outcome is "Pending" or "Requested" ? ResultKind.Requested
                    : reply.Outcome == "DryRun" ? ResultKind.Simulated
                    : reply.Outcome == "Snapshot" ? ResultKind.Completed
                    : Enum.TryParse<ResultKind>(reply.Outcome, out var parsed) ? parsed : ResultKind.Unavailable;
                command?.Complete(outcome, "automation-handler-result");
                return reply;
            }
            catch (Exception error)
            {
                command?.Fail(error, "automation-handler-failed");
                throw;
            }
    }

    private void LogSettingsChanges(UserSettings before, UserSettings after)
    {
        void Changed(string id, bool changed, string reason = "changed")
        {
            if (changed)
                LogCommandResult("setting-" + id, ResultKind.Completed, reason);
        }
        Changed("sound-feedback", before.SoundFeedbackEnabled != after.SoundFeedbackEnabled, after.SoundFeedbackEnabled ? "enabled" : "disabled");
        Changed("haptic-feedback", before.HapticFeedbackEnabled != after.HapticFeedbackEnabled, after.HapticFeedbackEnabled ? "enabled" : "disabled");
        Changed("guide", before.GuideXInputEnabled != after.GuideXInputEnabled, after.GuideXInputEnabled ? "enabled" : "disabled");
        Changed("guide-limit", before.GuideOpenMaxHoldMs != after.GuideOpenMaxHoldMs, "milliseconds-" + after.GuideOpenMaxHoldMs);
        Changed("scale", before.UiScalePercent != after.UiScalePercent, "percent-" + after.UiScalePercent);
        Changed("deadzone", before.Deadzone != after.Deadzone, "value-" + after.Deadzone);
        Changed("panel-side", before.PanelSide != after.PanelSide, after.PanelSide);
        Changed("display", before.DisplayId != after.DisplayId);
        Changed("playnite-location", before.PlaynitePath != after.PlaynitePath);
        Changed("opening-shortcut", before.Hotkey != after.Hotkey || before.Chord != after.Chord || before.ChordHoldMs != after.ChordHoldMs);
        Changed("quick-access", !SessionHubSettings.Favorites(before).SequenceEqual(SessionHubSettings.Favorites(after)));
    }
}
