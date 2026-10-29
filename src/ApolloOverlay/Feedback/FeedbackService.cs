using System.IO;
using System.Media;

namespace ApolloOverlay;

internal interface IFeedbackSound : IDisposable
{
    void Play(FeedbackCue cue);
    void Stop();
}

internal interface IFeedbackScheduler
{
    IDisposable Schedule(int milliseconds, Action action);
}

internal sealed class FeedbackScheduler : IFeedbackScheduler
{
    public IDisposable Schedule(int milliseconds, Action action) => new Timer(_ => action(), null, milliseconds, Timeout.Infinite);
}

/// <summary>Retained, low-amplitude PCM cues. This never changes any endpoint or application volume.</summary>
internal sealed class PcmFeedbackSound : IFeedbackSound
{
    private readonly Dictionary<FeedbackCue, (MemoryStream Stream, SoundPlayer Player)> cues = [];
    public void Play(FeedbackCue cue)
    {
        if (!cues.TryGetValue(cue, out var resource))
        {
            var stream = new MemoryStream(FeedbackTone.Wave(cue), writable: false);
            stream.Position = 0;
            var player = new SoundPlayer(stream);
            player.Load();
            resource = (stream, player);
            cues.Add(cue, resource);
        }

        resource.Player.Play();
    }

    public void Stop()
    {
        foreach (var resource in cues.Values)
            resource.Player.Stop();
    }

    public void Dispose()
    {
        Stop();
        foreach (var resource in cues.Values)
        {
            resource.Player.Dispose();
            resource.Stream.Dispose();
        }

        cues.Clear();
    }
}

public sealed class FeedbackService : IFeedbackService
{
    private readonly object gate = new();
    private readonly IControllerDevices devices;
    private readonly IFeedbackSound sound;
    private readonly IFeedbackScheduler scheduler;
    private readonly Func<long> clock;
    private FeedbackContext context;
    private bool soundEnabled, hapticsEnabled, disposed;
    private long lease;
    private (uint Slot, long Epoch)? pulse;
    private IDisposable? timer;
    private readonly Dictionary<FeedbackCue, long> last = [];
    public event Action<FeedbackStatus>? Changed;
    public FeedbackStatus Status
    {
        get;
        private set;
    } = new(true, false, "Optional feedback disabled.");

    public FeedbackService(IControllerDevices devices) : this(devices, new PcmFeedbackSound(), new FeedbackScheduler(), () => Environment.TickCount64)
    {
    }

    internal FeedbackService(IControllerDevices devices, IFeedbackSound sound, IFeedbackScheduler scheduler, Func<long> clock)
    {
        this.devices = devices;
        this.sound = sound;
        this.scheduler = scheduler;
        this.clock = clock;
    }

    private void Report(bool? soundAvailable = null, bool? hapticsAvailable = null, string? reason = null)
    {
        Status = new(soundAvailable ?? Status.SoundAvailable, hapticsAvailable ?? Status.HapticsAvailable, reason ?? Status.Reason);
        Changed?.Invoke(Status);
    }

    public void Configure(bool sound, bool haptics)
    {
        lock (gate)
        {
            if (disposed)
                return;
            if (soundEnabled && !sound)
                StopSound();
            if (hapticsEnabled && !haptics)
                StopPulse();
            soundEnabled = sound;
            hapticsEnabled = haptics;
        }
    }

    public void SetContext(FeedbackContext value)
    {
        lock (gate)
        {
            if (disposed)
                return;
            if (context != value)
            {
                StopPulse();
                if (!value.Visible || !value.ConfirmedForeground)
                    StopSound();
                last.Clear();
            }

            context = value;
        }
    }

    private void StopSound()
    {
        try
        {
            sound.Stop();
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            Report(soundAvailable: false, reason: "Sound feedback output unavailable.");
        }
    }

    private void StopPulse()
    {
        lease++;
        timer?.Dispose();
        timer = null;
        if (pulse is not { } active)
            return;
        pulse = null;
        var result = devices.SetVibration(active.Slot, active.Epoch, 0, 0);
        if (!result.Accepted)
            Report(hapticsAvailable: false, reason: result.Reason);
    }

    public FeedbackDispatch Request(FeedbackCue cue, FeedbackSource source)
    {
        lock (gate)
        {
            if (disposed || !context.Visible || !context.ConfirmedForeground)
                return new(false, false, "Feedback suspended while the panel is inactive.");
            if (!soundEnabled && !hapticsEnabled)
                return new(false, false, "Enable sound or haptics before testing.");
            var now = clock();
            var interval = cue == FeedbackCue.Unavailable ? 250 : 80;
            if (last.TryGetValue(cue, out var previous) && now >= previous && now - previous < interval)
                return new(false, false, "Feedback was rate-limited to prevent repetition.");
            var played = false;
            var vibrated = false;
            if (soundEnabled)
            {
                try
                {
                    sound.Play(cue);
                    played = true;
                    Report(soundAvailable: true, reason: "Sound feedback requested.");
                }
                catch (Exception e) when (e is InvalidOperationException or IOException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
                {
                    Report(soundAvailable: false, reason: "The sound could not be played. The panel remains available.");
                }
            }

            if (hapticsEnabled && source.Kind == FeedbackSourceKind.Controller && source.Slot is { } slot && context.ActiveSlot == slot && source.ConnectionEpoch == context.ConnectionEpoch)
            {
                var device = devices.Read(slot);
                if (device.Connected && device.ConnectionEpoch == source.ConnectionEpoch && device.Vibration == ControllerVibrationCapability.Available)
                {
                    StopPulse();
                    var result = devices.SetVibration(slot, device.ConnectionEpoch, 2800, 1600);
                    vibrated = result.Accepted;
                    Report(hapticsAvailable: vibrated, reason: result.Reason);
                    if (vibrated)
                    {
                        pulse = (slot, device.ConnectionEpoch);
                        var issued = ++lease;
                        timer = scheduler.Schedule(cue == FeedbackCue.Navigation ? 25 : cue == FeedbackCue.Test ? 50 : 45, () =>
                        {
                            lock (gate)
                            {
                                if (disposed || issued != lease)
                                    return;
                                StopPulse();
                            }
                        });
                    }
                }
                else
                    Report(hapticsAvailable: false, reason: device.Vibration == ControllerVibrationCapability.Unavailable ? "This controller does not report haptic support." : "The active controller or its haptic support is unconfirmed.");
            }

            if (played || vibrated)
                last[cue] = now;
            return new(played, vibrated, Status.Reason);
        }
    }

    public void Suspend()
    {
        lock (gate)
        {
            context = default;
            StopPulse();
            StopSound();
            last.Clear();
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            context = default;
            StopPulse();
            StopSound();
            sound.Dispose();
            Changed = null;
        }
    }
}
