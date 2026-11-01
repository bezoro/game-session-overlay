using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace ApolloOverlay;

public partial class MainWindow
{
    private CancellationTokenSource? diagnosticsCopyCancellation;
    private bool DiagnosticsAvailable => CommandLoggingFixture || !diagnostic && !Controller.Platform.IsPreview;
    internal Action<string> DiagnosticsClipboardWriter { private get; set; } = WriteDiagnosticsClipboard;
    internal Task DiagnosticsCopyTask { get; private set; } = Task.CompletedTask;

    private static void WriteDiagnosticsClipboard(string text)
    {
        var data = new Forms.DataObject();
        data.SetText(text, Forms.TextDataFormat.UnicodeText);
        // Fail promptly if another process owns the clipboard. Never overwrite
        // later on a hidden retry or block navigation waiting for its release.
        Forms.Clipboard.SetDataObject(data, true, 0, 0);
    }

    internal void RequestDiagnosticsCopy(bool fromTray = false)
    {
        if (!DiagnosticsAvailable || exiting || diagnosticsCopyCancellation is not null)
            return;
        DiagnosticsCopyTask = CopyDiagnosticsAsync(fromTray);
    }

    private async Task CopyDiagnosticsAsync(bool fromTray)
    {
        using var cancellation = new CancellationTokenSource();
        diagnosticsCopyCancellation = cancellation;
        using var command = BeginCommand("copy-diagnostics", origin: fromTray ? "mouse" : null);
        try
        {
            SetEntryDescription("copy-diagnostics", "Preparing complete report…");
            var state = $"administrator={ApolloOverlay.Automation.AutomationPipeSecurity.Elevated()}, visible={IsVisible}, active={IsActive}, foreground={OwnForeground}, confirmed={focusConfirmed}, focus-cycle={focusCycle}, opening={opening}, owned-dialog={ownedDialog}, transition={TransitionPending}, busy={busy}, fading-out={fadingOut}, input-method={inputMethod}, entry-point={(fromTray ? "tray" : "system")}, connected-controllers={connectedControllers}, "
                + $"guide-enabled={Controller.Settings.GuideXInputEnabled}, guide-backend=legacy-xinput, guide-active={guideBackend?.Active == true}, guide-valid={guideBackend?.SnapshotValid == true}, guide-connections={guideBackend?.Connections ?? 0}, guide-held={guideBackend?.Held == true}, guide-hold-limit-ms={Controller.Settings.GuideOpenMaxHoldMs}, deadzone={Controller.Settings.Deadzone}";
            var report = await Controller.Diagnostics.ExportAsync(state, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (exiting)
                return;
            DiagnosticsClipboardWriter(report.Text);
            command?.Complete(report.Incomplete ? ResultKind.Partial : ResultKind.Completed, "diagnostics-copied");
            var message = report.Incomplete ? "All retained logs copied. The report identifies logging gaps. Paste into chat." : "All retained logs copied. Paste into chat.";
            if (fromTray)
                tray?.NotifyInformation(message);
            else
                FeedbackText(message);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            command?.Complete(ResultKind.Cancelled, "diagnostics-copy-cancelled");
        }
        catch (Exception error) when (error is ExternalException or System.Threading.ThreadStateException or InvalidOperationException
            or System.IO.IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException or NotSupportedException)
        {
            command?.Fail(error, "diagnostics-copy-failed");
            if (!exiting)
            {
                const string message = "Could not copy complete diagnostics. Check the clipboard and log folder, then try again.";
                if (fromTray)
                    tray?.NotifyResult(message);
                else
                    FeedbackText(message);
            }
        }
        catch (Exception error)
        {
            command?.Fail(error, "diagnostics-copy-unexpected");
            throw;
        }
        finally
        {
            if (ReferenceEquals(diagnosticsCopyCancellation, cancellation))
                diagnosticsCopyCancellation = null;
            if (!exiting)
                SetEntryDescription("copy-diagnostics", "All retained logs · paste into chat");
        }
    }
}
