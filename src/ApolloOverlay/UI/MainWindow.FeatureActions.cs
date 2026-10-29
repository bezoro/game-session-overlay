using System.IO;

namespace ApolloOverlay;

public partial class MainWindow
{
    private void StartSessionAction(string id, Func<CancellationToken, Task<ActionResult>> action, Action? completed = null)
    {
        if (!NavigationAllowed || busy || TransitionPending)
            return;
        QueueHandoff(() => OperationTask = RunSessionAction(id, action, completed));
    }

    private void StartAudioAction(string id, Func<CancellationToken, Task<ActionResult>> action, Action? completed = null) => StartSessionAction(id, async token =>
    {
        await audioObservation.WaitAsync(token);
        token.ThrowIfCancellationRequested();
        return await action(token);
    }, completed);
    private async Task RunSessionAction(string id, Func<CancellationToken, Task<ActionResult>> action, Action? completed)
    {
        if (exiting || busy)
            return;
        var generation = ++operationGeneration;
        busy = true;
        var request = new CancellationTokenSource();
        pending = request;
        RetainOperation("Pending", "Applying your choice…", id);
        ActionResult result;
        try
        {
            result = await action(request.Token);
        }
        catch (OperationCanceledException)
        {
            result = new(ResultKind.Unknown, "Operation interrupted. Check the state before trying again.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
        {
            result = new(ResultKind.Failed, "Could not complete the operation. Check the device and try again.");
        }
        finally
        {
            request.Dispose();
            if (ReferenceEquals(pending, request))
                pending = null;
            busy = false;
        }

        if (exiting || generation != operationGeneration)
            return;
        RecordResult(result, id);
        UpdateSessionFeatureLabels();
        UpdatePersistentState();
        if (result.Kind is ResultKind.Completed or ResultKind.Simulated)
            completed?.Invoke();
        if (IsVisible && IsActive && OwnForeground && !focusConfirmed)
        {
            CancelOpenAnimation();
            OpenPanel();
        }
    }
}
