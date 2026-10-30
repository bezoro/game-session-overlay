using System.Runtime.InteropServices;

namespace ApolloOverlay;
/// <summary>One caller owns each native operation. In-flight cancellation never claims a write was undone.</summary>
public sealed class SystemAudioDevices(ISystemAudioBackend? backend = null)
{
    private readonly ISystemAudioBackend backend = backend ?? new CoreSystemAudioBackend();
    private readonly SemaphoreSlim gate = new(1, 1);
    public SystemAudioSnapshot Current
    {
        get;
        private set;
    } = SystemAudioSnapshot.Empty;

    public void InvalidateObservation() => Current = Current with
    {
        MicrophoneMuted = null,
        ConsoleOutputId = "",
        MediaOutputId = "",
        Status = "Refreshing audio devices…"
    };
    public async Task<SystemAudioSnapshot> RefreshAsync(CancellationToken cancellation = default)
    {
        if (!await gate.WaitAsync(0, cancellation))
            return Current;
        var work = Task.Run(backend.Read, CancellationToken.None);
        try
        {
            Current = await work.WaitAsync(TimeSpan.FromSeconds(2), cancellation);
            return Current;
        }
        catch (TimeoutException)
        {
            return Current = SystemAudioSnapshot.Empty with
            {
                Status = "Audio took too long to respond. Try refreshing again."
            };
        }
        catch (Exception error) when (error is COMException or InvalidCastException or InvalidOperationException or UnauthorizedAccessException)
        {
            return Current = SystemAudioSnapshot.Empty with
            {
                Status = "Audio devices unavailable. Try refreshing the list."
            };
        }
        finally
        {
            ReleaseWhenDrained(work);
        }
    }

    public Task<ActionResult> SetMicrophoneMutedAsync(string expectedId, bool muted, CancellationToken cancellation = default) => Mutate(keep => backend.SetMicrophoneMuted(expectedId, muted, keep), cancellation);
    public Task<ActionResult> SelectOutputAsync(string expectedConsole, string expectedMedia, string output, CancellationToken cancellation = default) => RestoreOutputsAsync(expectedConsole, expectedMedia, output, output, cancellation);
    public Task<ActionResult> RestoreOutputsAsync(string expectedConsole, string expectedMedia, string console, string media, CancellationToken cancellation = default) => Mutate(keep => backend.SetOutputs(expectedConsole, expectedMedia, console, media, keep), cancellation);
    private async Task<ActionResult> Mutate(Func<Func<bool>, ActionResult> action, CancellationToken cancellation)
    {
        if (!await gate.WaitAsync(0, cancellation))
            return new(ResultKind.Unavailable, "Another audio operation is in progress.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(3000);
        Task<(ActionResult Result, SystemAudioSnapshot Snapshot)>? work = null;
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            work = Task.Run(() =>
            {
                var result = action(() => !deadline.IsCancellationRequested);
                return (result, deadline.IsCancellationRequested ? Current : backend.Read());
            }, CancellationToken.None);
            var observed = await work.WaitAsync(deadline.Token);
            Current = observed.Snapshot;
            return observed.Result;
        }
        catch (OperationCanceledException)
        {
            if (work is not null)
                InvalidateObservation();
            return new(work is not null ? ResultKind.Unknown : ResultKind.Cancelled, work is not null ? "Operation interrupted. Check the device before trying again." : "No audio change was sent.");
        }
        catch (Exception error) when (error is COMException or InvalidCastException or InvalidOperationException or UnauthorizedAccessException)
        {
            if (work is not null)
                InvalidateObservation();
            return new(work is not null ? ResultKind.Unknown : ResultKind.Unavailable, "The change could not be confirmed. Refresh the list before trying again.");
        }
        finally
        {
            if (work is null)
                gate.Release();
            else
                ReleaseWhenDrained(work);
        }
    }

    private void ReleaseWhenDrained(Task work)
    {
        if (work.IsCompleted)
        {
            _ = work.Exception;
            gate.Release();
            return;
        }

        _ = work.ContinueWith(completed =>
        {
            _ = completed.Exception;
            gate.Release();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
