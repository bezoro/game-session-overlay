namespace ApolloOverlay;

/// <summary>Orders receipt snapshots off the dispatcher, coalescing superseded progress checkpoints.</summary>
internal sealed class UpdateReceiptWriter(Action<byte[]> write)
{
    private sealed class PendingWrite(byte[] bytes, bool checkpoint)
    {
        internal byte[] Bytes = bytes;
        internal readonly bool Checkpoint = checkpoint;
        internal readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly object gate = new();
    private readonly Queue<PendingWrite> queue = new();
    private PendingWrite? lastQueued;
    private Task tail = Task.CompletedTask;
    private bool running;

    internal Task WriteAsync(byte[] bytes, bool checkpoint = false)
    {
        lock (gate)
        {
            if (checkpoint && lastQueued is { Checkpoint: true } pending)
            {
                pending.Bytes = bytes;
                return pending.Completion.Task;
            }
            var next = new PendingWrite(bytes, checkpoint);
            queue.Enqueue(next);
            lastQueued = next;
            tail = next.Completion.Task;
            if (!running)
            {
                running = true;
                _ = Task.Run(Drain);
            }
            return tail;
        }
    }

    private void Drain()
    {
        while (true)
        {
            PendingWrite next;
            lock (gate)
            {
                if (!queue.TryDequeue(out next!))
                {
                    running = false;
                    return;
                }
                if (ReferenceEquals(lastQueued, next))
                    lastQueued = null;
            }
            try
            {
                write(next.Bytes);
                next.Completion.TrySetResult();
            }
            catch (Exception error) when (error is not (OutOfMemoryException or AccessViolationException))
            {
                next.Completion.TrySetException(error);
            }
        }
    }

    internal Task Drained
    {
        get
        {
            lock (gate)
                return tail;
        }
    }
}
