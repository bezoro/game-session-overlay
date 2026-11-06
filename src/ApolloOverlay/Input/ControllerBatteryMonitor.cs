namespace ApolloOverlay;

/// <summary>Owns a separate read-only device service; native reads never lock the UI's input service.</summary>
internal sealed class ControllerBatteryMonitor : IDisposable
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private readonly IControllerDevices devices;
    private readonly IControllerIdentitySource identities;
    private readonly Action<ControllerBatteryAlert> notify;
    private readonly Action<string, Exception?> report;
    private readonly CancellationTokenSource cancellation = new();
    private readonly TimeSpan interval;
    private int disposed;
    internal Task Completion
    {
        get;
    }

    internal ControllerBatteryMonitor(IControllerDevices devices, IControllerIdentitySource identities,
        Action<ControllerBatteryAlert> notify, Action<string, Exception?> report, TimeSpan? interval = null)
    {
        this.devices = devices;
        this.identities = identities;
        this.notify = notify;
        this.report = report;
        this.interval = interval ?? PollInterval;
        if (this.interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval));
        Completion = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(interval);
        var policy = new ControllerBatteryAlerts();
        var previous = new string?[4];
        string? failure = null;
        try
        {
            do
            {
                try
                {
                    var samples = new ControllerDeviceSnapshot[4];
                    for (uint slot = 0; slot < 4; slot++)
                    {
                        if (Volatile.Read(ref disposed) != 0)
                            return;
                        samples[slot] = devices.Read(slot);
                    }
                    IReadOnlyList<ControllerIdentity> metadata = [];
                    var metadataFailed = false;
                    if (samples.Count(sample => sample.Connected) == 1)
                    {
                        try
                        {
                            metadata = identities.Read();
                        }
                        catch (Exception error) when (error is not (OutOfMemoryException or AccessViolationException))
                        {
                            if (Volatile.Read(ref disposed) != 0)
                                return;
                            metadataFailed = true;
                            if (failure != "identity-read-failed")
                                report("identity-read-failed", error);
                            failure = "identity-read-failed";
                        }
                    }

                    if (Volatile.Read(ref disposed) != 0)
                        return;
                    if (!metadataFailed && failure is not null)
                    {
                        report("read-recovered", null);
                        failure = null;
                    }
                    var known = samples.Count(sample => sample.Connected) == 1 && metadata.Count == 1 ? metadata[0] : null;
                    foreach (var sample in samples)
                    {
                        var identity = sample.Connected ? known : null;
                        // Reasons use bounded machine-readable fields, not device IDs or names.
                        var observation = $"observation/slot={sample.Slot}/connected={sample.Connected}/type={sample.BatteryType}/level={sample.BatteryLevel}/native={sample.NativeResult}"
                            + $"/percent={identity?.BatteryPercent}/charging={identity?.Charging}/wireless={identity?.Wireless}/identities={metadata.Count}";
                        if (observation != previous[sample.Slot])
                        {
                            previous[sample.Slot] = observation;
                            report(observation, null);
                        }
                    }
                    foreach (var alert in policy.Observe(samples, metadata, Environment.TickCount64))
                    {
                        if (Volatile.Read(ref disposed) != 0)
                            return;
                        notify(alert);
                    }
                }
                catch (Exception error) when (error is not (OutOfMemoryException or AccessViolationException))
                {
                    if (Volatile.Read(ref disposed) != 0)
                        return;
                    if (failure != "read-failed")
                        report("read-failed", error);
                    failure = "read-failed";
                }
            } while (await timer.WaitForNextTickAsync(cancellation.Token));
        }
        catch (OperationCanceledException) when (Volatile.Read(ref disposed) != 0)
        {
        }
        finally
        {
            devices.Dispose();
            cancellation.Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException) { }
    }
}
