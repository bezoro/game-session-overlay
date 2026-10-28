using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GuideProbe;

internal sealed record Entry(string Source, string Edge, double ElapsedMs, int? Device = null, int? Slot = null, string? Button = null, ulong? NativeRelUs = null, double? DurationNativeMs = null, double? DurationArrivalMs = null, string? Status = null, int? Hresult = null, long? Count = null, long Seq = 0, int? Family = null, uint? SupportedInput = null, uint? SupportedSystemButtons = null, uint? InputKind = null, int? LabelIndex = null, int? Label = null, uint? MappedInputKinds = null);
[JsonSerializable(typeof(Entry))]
internal partial class LogJson : JsonSerializerContext;
internal readonly record struct RawGuide(nint Device, ulong Timestamp, uint Current, uint Previous, double ArrivalMs, bool DeviceEvent = false);
internal sealed class LimitedQueue<T>(int capacity)
{
    private readonly ConcurrentQueue<T> queue = new();
    private int count;
    private long dropped;
    internal long Dropped => Interlocked.Read(ref dropped);
    internal int Capacity => capacity;

    internal void MarkDropped() => Interlocked.Increment(ref dropped);
    internal bool Push(T item)
    {
        if (Interlocked.Increment(ref count) > capacity)
        {
            Interlocked.Decrement(ref count);
            Interlocked.Increment(ref dropped);
            return false;
        }

        queue.Enqueue(item);
        return true;
    }

    internal bool Pop(out T item)
    {
        if (queue.TryDequeue(out item!))
        {
            Interlocked.Decrement(ref count);
            return true;
        }

        return false;
    }
}

internal sealed class Pairing
{
    private sealed class State
    {
        internal ulong? Last, Down;
        internal double Arrival;
        internal bool Invalid;
    }

    private readonly Dictionary<int, State> devices = [];
    private ulong? origin;
    internal bool Overflow;
    internal Entry Observe(int device, RawGuide raw)
    {
        if (!devices.TryGetValue(device, out var state))
            devices[device] = state = new();
        origin ??= raw.Timestamp;
        ulong? relative = raw.Timestamp >= origin.Value ? raw.Timestamp - origin.Value : null;
        var regressed = state.Last is { } last && raw.Timestamp < last;
        state.Last = raw.Timestamp;
        var now = (raw.Current & 1) != 0;
        var previous = (raw.Previous & 1) != 0;
        var edge = now == previous ? "repeat" : now ? "down" : "up";
        string? status = null;
        double? duration = null, arrivalDuration = null;
        if (regressed)
        {
            state.Invalid = true;
            status = "native_clock_regressed";
        }

        if (edge == "down")
        {
            if (state.Down is not null)
            {
                state.Invalid = true;
                status = "duplicate_down";
            }
            else
            {
                state.Down = raw.Timestamp;
                state.Arrival = raw.ArrivalMs;
                state.Invalid = regressed;
            }
        }

        if (edge == "up")
        {
            if (state.Down is { } start && !state.Invalid && !regressed && !Overflow && raw.Timestamp >= start)
            {
                duration = (raw.Timestamp - start) / 1000d;
                arrivalDuration = raw.ArrivalMs - state.Arrival;
                status = "paired";
            }
            else
                status = Overflow ? "overflow_unreliable" : state.Down is null ? "orphan_up" : status ?? "incomplete";
            state.Down = null;
            state.Invalid = false;
        }

        if (Overflow)
        {
            duration = null;
            arrivalDuration = null;
            status = "overflow_unreliable";
        }

        return new("gameinput", edge, raw.ArrivalMs, device, Button: "Guide", NativeRelUs: relative, DurationNativeMs: duration, DurationArrivalMs: arrivalDuration, Status: status);
    }

    internal IEnumerable<Entry> Finish(double elapsed) => devices.Where(p => p.Value.Down is not null).Select(p => new Entry("gameinput", "incomplete", elapsed, p.Key, Button: "Guide", Status: "ended_while_pressed"));
}

internal sealed class Pads
{
    private readonly (bool Seen, bool Connected, ushort Buttons)[] slots = new (bool, bool, ushort)[4];
    private static readonly (ushort Bit, string Name)[] buttons = [(1, "DpadUp"), (2, "DpadDown"), (4, "DpadLeft"), (8, "DpadRight"), (16, "Start"), (32, "Back"), (64, "LeftThumb"), (128, "RightThumb"), (256, "LB"), (512, "RB"), (4096, "A"), (8192, "B"), (16384, "X"), (32768, "Y")];
    internal List<Entry> Observe(int slot, bool connected, ushort mask, double elapsed)
    {
        mask &= 0xF3FF;
        var old = slots[slot];
        slots[slot] = (true, connected, mask);
        var records = new List<Entry>();
        if (!old.Seen || connected != old.Connected)
            records.Add(new("xinput", connected ? "connected" : "disconnected", elapsed, Slot: slot, Status: "baseline; no GameInput association"));
        if (!old.Seen || !connected || !old.Connected)
            return records;
        foreach (var (bit, name) in buttons)
            if ((mask & bit) != (old.Buttons & bit))
                records.Add(new("xinput", (mask & bit) != 0 ? "down" : "up", elapsed, Slot: slot, Button: name));
        return records;
    }
}

internal readonly record struct Budget(int Seconds)
{
    internal bool Expired(double elapsedMs) => elapsedMs >= Seconds * 1000d;
}

internal sealed class Writer(Stream stream)
{
    private long seq, limited;
    internal long Written => seq;
    internal bool Limited => limited > 0;

    internal void Write(Entry item, bool mandatory = false)
    {
        if (seq >= 6000 && !mandatory)
        {
            limited++;
            return;
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(item with
        {
            Seq = ++seq
        }, LogJson.Default.Entry);
        stream.Write(bytes);
        stream.WriteByte(10);
    }

    internal void Flush() => stream.Flush();
    internal long Suppressed => limited;
}

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.SequenceEqual(new[] { "--self-test" }))
            return SelfTests.Run();
        if (args.Length == 3 && args[0] == "--raw-guide-check" && args[1] == "--output")
        {
            try
            {
                return DeviceCheck(args[2], true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Console.Error.WriteLine("Raw check file failure: " + e.GetType().Name);
                return 2;
            }
        }

        if (args.Length == 3 && args[0] == "--device-check" && args[1] == "--output")
        {
            try
            {
                return DeviceCheck(args[2]);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Console.Error.WriteLine("Device check file failure: " + e.GetType().Name);
                return 2;
            }
        }

        if (args.Length != 4 || args[0] != "--duration-seconds" || !int.TryParse(args[1], out var seconds) || seconds is < 90 or > 120 || args[2] != "--output")
        {
            Console.Error.WriteLine("Usage: GuideProbe --duration-seconds 90..120 --output NEW.jsonl | --self-test");
            return 2;
        }

        try
        {
            return Observe(seconds, args[3]);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine("Probe did not start or file output failed: " + e.GetType().Name);
            return 2;
        }
    }

    private static int DeviceCheck(string output, bool rawMode = false)
    {
        using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var writer = new Writer(stream);
        var watch = Stopwatch.StartNew();
        var queue = new LimitedQueue<RawGuide>(256);
        var registry = new Devices();
        var pairing = new Pairing();
        var samples = new CapabilitySamples();
        var rawCheck = rawMode ? new RawLabels() : null;
        var backend = new GameInput(queue, () => watch.Elapsed.TotalMilliseconds);
        var pads = new Pads();
        var cancelled = false;
        var started = false;
        var removed = false;
        var error = false;
        var full = false;
        long drops = 0;
        ConsoleCancelEventHandler cancel = (_, e) =>
        {
            e.Cancel = true;
            cancelled = true;
        };
        Console.CancelKeyPress += cancel;
        writer.Write(new("probe", "start", 0, Status: (rawMode ? "raw_guide_check_seconds=60; descriptor_first; " : "device_check_seconds=60; ") + " local_policy=0xC0 nonexclusive; blocking enumeration; no XInput association; human association required"));
        void Drain() => DeviceShutdown.Consume(queue, registry, pairing, samples, writer, rawCheck);
        try
        {
            started = backend.StartDeviceCheck();
            writer.Write(new("gameinput_devices", "enumeration", watch.Elapsed.TotalMilliseconds, Status: backend.EnumerationComplete ? "blocking_enumeration_completed" : "enumeration_failed_or_unavailable", Hresult: backend.DeviceRegistrationHr));
            writer.Write(new("gameinput", "availability", watch.Elapsed.TotalMilliseconds, Status: backend.Status, Hresult: backend.Hresult));
            var deadline = new Budget(60);
            while (!deadline.Expired(watch.Elapsed.TotalMilliseconds) && !Volatile.Read(ref cancelled))
            {
                if (queue.Dropped != drops)
                {
                    drops = queue.Dropped;
                    samples.Invalid = true;
                    pairing.Overflow = true;
                    writer.Write(new("probe", "overflow", watch.Elapsed.TotalMilliseconds, Count: drops), true);
                }

                if (writer.Limited)
                {
                    samples.Invalid = true;
                    pairing.Overflow = true;
                }

                Drain();
                if (rawCheck is not null)
                {
                    var selection = rawCheck.Selection();
                    if (!started || !backend.EnumerationComplete || selection.Status != "raw_candidate" || rawCheck.Invalid || samples.Invalid || queue.Dropped > 0 || writer.Limited)
                    {
                        writer.Write(new("raw_descriptor", "result", watch.Elapsed.TotalMilliseconds, selection.Device, Status: selection.Status, LabelIndex: selection.Index), true);
                        break;
                    }
                }

                if (started)
                {
                    foreach (var entry in samples.Poll(backend, watch.Elapsed.TotalMilliseconds))
                        writer.Write(entry);
                    if (rawCheck is not null)
                        foreach (var entry in rawCheck.Poll(backend, watch.Elapsed.TotalMilliseconds))
                            writer.Write(entry);
                }

                if (rawCheck?.Invalid == true)
                    break;
                for (var slot = 0; slot < 4; slot++)
                {
                    var connected = GameInput.ReadPad((uint)slot, out var mask);
                    foreach (var entry in pads.Observe(slot, connected, mask, watch.Elapsed.TotalMilliseconds))
                        writer.Write(entry);
                }

                writer.Flush();
                Thread.Sleep(20);
            }

            full = deadline.Expired(watch.Elapsed.TotalMilliseconds) && !cancelled;
        }
        catch (Exception e)
        {
            error = true;
            samples.Invalid = true;
            writer.Write(new("probe", "error", watch.Elapsed.TotalMilliseconds, Status: e.GetType().Name), true);
        }
        finally
        {
            removed = backend.StopAndUnregister();
            if (removed)
                DeviceShutdown.Complete(queue, registry, pairing, samples, writer, watch.Elapsed.TotalMilliseconds, rawCheck);
            var relevant = samples.RelevantCount;
            var result = rawCheck is null ? CapabilityOutcome.Decide(started, backend.EnumerationComplete, full, samples.Invalid || queue.Dropped > 0 || writer.Limited || !removed || error, samples.RelevantCount, false, samples.HasCommonReading, samples.AnyGuide) : rawCheck.Outcome(full, !started || !backend.EnumerationComplete || samples.Invalid || queue.Dropped > 0 || writer.Limited || !removed || error || cancelled);
            writer.Write(new("probe", "classification", watch.Elapsed.TotalMilliseconds, Status: result, Count: relevant), true);
            if (removed)
            {
                rawCheck?.Release();
                samples.Release();
                registry.Release();
                backend.Release();
            }

            writer.Write(new("probe", "end", watch.Elapsed.TotalMilliseconds, Status: "cancelled=" + cancelled + "; full_window=" + full + "; cleanup=" + (removed ? "complete" : "retained_until_exit") + "; suppressed=" + writer.Suppressed, Count: queue.Dropped), true);
            writer.Flush();
            Console.CancelKeyPress -= cancel;
        }

        return removed && !error ? 0 : 1;
    }

    private static int Observe(int seconds, string output)
    {
        using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var writer = new Writer(stream);
        var watch = Stopwatch.StartNew();
        var queue = new LimitedQueue<RawGuide>(256);
        var pairing = new Pairing();
        var registry = new Devices();
        var backend = new GameInput(queue, () => watch.Elapsed.TotalMilliseconds);
        var pads = new Pads();
        var cancel = false;
        var started = false;
        var removed = true;
        long overflow = 0;
        var exit = 0;
        ConsoleCancelEventHandler handler = (_, e) =>
        {
            e.Cancel = true;
            cancel = true;
        };
        Console.CancelKeyPress += handler;
        writer.Write(new("probe", "start", 0, Status: "duration_seconds=" + seconds + "; policy=0x80 BackgroundGuide only; native_us and arrival_ms are distinct; no slot association"));
        void Drain(bool complete = false)
        {
            for (var consumed = 0; (complete || consumed < queue.Capacity) && queue.Pop(out var raw); consumed++)
            {
                try
                {
                    var id = registry.Get(raw.Device);
                    if (id is null)
                        pairing.Overflow = true;
                    writer.Write(id is { } device ? pairing.Observe(device, raw) : new("gameinput", "incomplete", raw.ArrivalMs, Button: "Guide", Status: "no_device_identity"));
                }
                finally
                {
                    GameInput.ReleaseObject(raw.Device);
                }
            }
        }

        try
        {
            started = backend.Start();
            writer.Write(new("gameinput", "availability", watch.Elapsed.TotalMilliseconds, Status: backend.Status, Hresult: backend.Hresult));
            var budget = new Budget(seconds);
            while (!budget.Expired(watch.Elapsed.TotalMilliseconds) && !Volatile.Read(ref cancel))
            {
                if (queue.Dropped != overflow)
                {
                    overflow = queue.Dropped;
                    pairing.Overflow = true;
                    writer.Write(new("probe", "overflow", watch.Elapsed.TotalMilliseconds, Count: overflow), true);
                }

                if (writer.Limited)
                    pairing.Overflow = true;
                Drain();
                for (var slot = 0; slot < 4; slot++)
                {
                    var connected = GameInput.ReadPad((uint)slot, out var mask);
                    foreach (var record in pads.Observe(slot, connected, mask, watch.Elapsed.TotalMilliseconds))
                        writer.Write(record);
                }

                writer.Flush();
                Thread.Sleep(20);
            }
        }
        catch (Exception e)
        {
            exit = 1;
            writer.Write(new("probe", "error", watch.Elapsed.TotalMilliseconds, Status: e.GetType().Name), true);
        }
        finally
        {
            removed = backend.StopAndUnregister();
            if (removed)
            {
                if (queue.Dropped > overflow)
                {
                    pairing.Overflow = true;
                    writer.Write(new("probe", "overflow", watch.Elapsed.TotalMilliseconds, Count: queue.Dropped), true);
                }

                Drain(complete: true);
                foreach (var incomplete in pairing.Finish(watch.Elapsed.TotalMilliseconds))
                    writer.Write(incomplete, true);
                registry.Release();
                backend.Release();
            }

            writer.Write(new("probe", "end", watch.Elapsed.TotalMilliseconds, Status: (cancel ? "cancelled" : "deadline_or_error") + "; gameinput=" + (started ? "registered" : "partial") + "; cleanup=" + (removed ? "complete" : "retained_until_process_exit") + "; suppressed=" + writer.Suppressed, Count: queue.Dropped), true);
            writer.Flush();
            Console.CancelKeyPress -= handler;
        }

        return removed ? exit : 1;
    }
}

internal readonly record struct Capability(int Hresult, bool Valid, int Family, uint SupportedInput, uint SystemButtons)
{
    internal bool Relevant => Valid && (SupportedInput & 0x4000E) != 0;
    internal uint ReadingKind => (SupportedInput & 0x40000) != 0 ? 0x40000u : (SupportedInput & 4) != 0 ? 4u : 0;
}

internal readonly record struct CommonReading(int Hresult, bool Valid, ulong Timestamp, ulong Low, ulong High, int Count, string Status);
internal static class CapabilityOutcome
{
    internal static string Decide(bool registration, bool enumeration, bool full, bool invalid, int relevant, bool associated, bool commonReading, bool guide)
    {
        if (!registration || !enumeration || !full || invalid)
            return "inconclusive: partial_or_invalid_observation";
        if (relevant == 0)
            return "no_relevant_device_observed: completed_window_only_not_transport_verdict";
        if (!commonReading)
            return "inconclusive: common_reading_missing_or_unavailable";
        if (!associated)
            return "inconclusive: human_association_required";
        return guide ? "inconclusive: guide_advertised_delivery_not_proven" : "guide_unsupported: associated_device_advertised_capability_only";
    }
}

internal sealed class CapabilitySamples
{
    private sealed class Sample(nint handle)
    {
        internal readonly nint Handle = handle;
        internal Capability Info;
        internal bool Connected;
        internal CommonReading? Previous;
        internal ulong? Origin, EventOrigin;
        internal long Reads, Failures, Changes;
        internal string? LastStatus;
    }

    private readonly Dictionary<int, Sample> samples = [];
    internal bool Invalid;
    internal bool Contains(int id) => samples.ContainsKey(id);
    internal int RelevantCount => samples.Values.Count(s => s.Info.Relevant);
    internal bool HasCommonReading => samples.Values.Any(s => s.Reads > 0);
    internal bool AnyGuide => samples.Values.Any(s => s.Info.Relevant && (s.Info.SystemButtons & 1) != 0);

    internal unsafe List<Entry> Device(int id, RawGuide raw)
    {
        var result = new List<Entry>();
        if (!samples.TryGetValue(id, out var sample))
        {
            if (samples.Count >= 16)
            {
                Invalid = true;
                result.Add(new("gameinput_devices", "limit", raw.ArrivalMs, id));
                return result;
            }

            ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)raw.Device)[1])(raw.Device);
            samples[id] = sample = new(raw.Device);
        }

        var connected = (raw.Current & 1) != 0;
        sample.EventOrigin ??= raw.Timestamp;
        ulong? relative = raw.Timestamp >= sample.EventOrigin ? raw.Timestamp - sample.EventOrigin.Value : null;
        result.Add(new("gameinput_devices", connected ? "connected" : "disconnected", raw.ArrivalMs, id, NativeRelUs: relative));
        if (!connected)
        {
            sample.Connected = false;
            sample.Previous = null;
            Invalid = true;
            return result;
        }

        if (!sample.Connected)
        {
            sample.Previous = null;
            sample.Origin = null;
        }

        sample.Connected = true;
        sample.Info = GameInput.Info(sample.Handle);
        if (!sample.Info.Relevant || sample.Info.ReadingKind == 0)
            Invalid = true;
        result.Add(new("gameinput_devices", "capabilities", raw.ArrivalMs, id, Status: sample.Info.Valid ? "advertised_only; no inferred transport_or_XInput_slot" : "device_info_failed_or_invalid", Hresult: sample.Info.Hresult, Family: sample.Info.Valid ? sample.Info.Family : null, SupportedInput: sample.Info.Valid ? sample.Info.SupportedInput : null, SupportedSystemButtons: sample.Info.Valid ? sample.Info.SystemButtons : null));
        return result;
    }

    internal IEnumerable<Entry> Poll(GameInput backend, double elapsed)
    {
        foreach (var (id, sample) in samples)
        {
            if (!sample.Connected || !sample.Info.Relevant || sample.Info.ReadingKind == 0)
                continue;
            var read = backend.Read(sample.Handle, sample.Info.ReadingKind);
            if (!read.Valid)
            {
                Invalid = true;
                sample.Failures++;
                sample.Previous = null;
                if (sample.LastStatus != read.Status)
                {
                    yield return new("gameinput_common", "reading_unavailable", elapsed, id, Status: read.Status, Hresult: read.Hresult, InputKind: sample.Info.ReadingKind);
                    sample.LastStatus = read.Status;
                }

                continue;
            }

            sample.Reads++;
            sample.Origin ??= read.Timestamp;
            ulong? relative = read.Timestamp >= sample.Origin ? read.Timestamp - sample.Origin.Value : null;
            if (relative is null)
            {
                Invalid = true;
                sample.Previous = null;
            }

            if (sample.Previous is null)
            {
                yield return new("gameinput_common", "baseline", elapsed, id, NativeRelUs: relative, Status: "common_buttons_only; no XInput association", Hresult: read.Hresult, InputKind: sample.Info.ReadingKind);
            }
            else
            {
                var previous = sample.Previous.Value;
                if (read.Timestamp < previous.Timestamp)
                {
                    Invalid = true;
                    yield return new("gameinput_common", "clock_regressed", elapsed, id, NativeRelUs: relative);
                }

                for (var bit = 0; bit < read.Count; bit++)
                {
                    var now = bit < 64 ? (read.Low & (1UL << bit)) != 0 : (read.High & (1UL << (bit - 64))) != 0;
                    var old = bit < 64 ? (previous.Low & (1UL << bit)) != 0 : (previous.High & (1UL << (bit - 64))) != 0;
                    if (now == old)
                        continue;
                    sample.Changes++;
                    yield return new("gameinput_common", now ? "down" : "up", elapsed, id, Button: sample.Info.ReadingKind == 0x40000 ? GamepadButton(bit) : "Button" + bit, NativeRelUs: relative, Hresult: read.Hresult, InputKind: sample.Info.ReadingKind);
                }
            }

            sample.Previous = read;
            sample.LastStatus = read.Status;
        }
    }

    private static string GamepadButton(int bit) => bit switch
    {
        0 => "Menu",
        1 => "View",
        2 => "A",
        3 => "B",
        4 => "X",
        5 => "Y",
        6 => "DpadUp",
        7 => "DpadDown",
        8 => "DpadLeft",
        9 => "DpadRight",
        10 => "LB",
        11 => "RB",
        12 => "LeftThumb",
        13 => "RightThumb",
        _ => "Unknown"
    };
    internal IEnumerable<Entry> Summary(double elapsed) => samples.Select(p => new Entry("gameinput_devices", "summary", elapsed, p.Key, Status: "successful_reads=" + p.Value.Reads + "; failures=" + p.Value.Failures + "; changes=" + p.Value.Changes + "; connected=" + p.Value.Connected, Count: p.Value.Changes));
    internal void Release()
    {
        foreach (var sample in samples.Values)
            GameInput.ReleaseObject(sample.Handle);
        samples.Clear();
    }
}

// Active consumption yields after one capacity-sized batch; complete drains require quiesced callbacks.
internal static class DeviceShutdown
{
    internal static void Consume(LimitedQueue<RawGuide> queue, Devices registry, Pairing pairing, CapabilitySamples samples, Writer writer, RawLabels? rawCheck = null, bool complete = false)
    {
        for (var consumed = 0; (complete || consumed < queue.Capacity) && queue.Pop(out var raw); consumed++)
        {
            try
            {
                var id = registry.Get(raw.Device);
                if (id is null)
                {
                    samples.Invalid = true;
                    pairing.Overflow = true;
                    writer.Write(new("gameinput_devices", "invalid_identity", raw.ArrivalMs));
                    continue;
                }

                if (raw.DeviceEvent)
                {
                    foreach (var entry in samples.Device(id.Value, raw))
                        writer.Write(entry);
                    if (rawCheck is not null)
                        foreach (var entry in rawCheck.Device(id.Value, raw))
                            writer.Write(entry);
                }
                else
                {
                    writer.Write(pairing.Observe(id.Value, raw));
                    if (!samples.Contains(id.Value))
                        samples.Invalid = true;
                }
            }
            finally
            {
                GameInput.ReleaseObject(raw.Device);
            }
        }
    }

    internal static void Complete(LimitedQueue<RawGuide> queue, Devices registry, Pairing pairing, CapabilitySamples samples, Writer writer, double elapsed, RawLabels? rawCheck = null)
    {
        if (queue.Dropped > 0 || writer.Limited)
        {
            pairing.Overflow = true;
            samples.Invalid = true;
            rawCheck?.Invalidate();
        }

        Consume(queue, registry, pairing, samples, writer, rawCheck, complete: true);
        if (rawCheck is not null)
        {
            foreach (var entry in rawCheck.Finish(elapsed))
                writer.Write(entry, true);
        }

        foreach (var entry in pairing.Finish(elapsed))
            writer.Write(entry, true);
        foreach (var entry in samples.Summary(elapsed))
            writer.Write(entry, true);
    }
}

internal readonly record struct RawLabel(int Index, int Label, uint MappedInputKinds);
internal sealed record RawDescriptor(bool Valid, int Hresult, uint SupportedInput, RawLabel[] Labels, string Status);
internal readonly record struct RawSelection(string Status, int? Device = null, int? Index = null);
internal sealed class RawLabels
{
    private sealed class Sample(nint handle)
    {
        internal readonly nint Handle = handle;
        internal bool Connected;
        internal RawDescriptor Descriptor = new(false, 0, 0, [], "not_read");
        internal CommonReading? Previous;
        internal ulong? Origin;
        internal bool Ready;
    }

    private readonly Dictionary<int, Sample> samples = [];
    private readonly Pairing pairs = new();
    internal bool Invalid;
    internal int PairedCycles
    {
        get; private set;
    }

    private int edges;
    internal void Invalidate()
    {
        Invalid = true;
        pairs.Overflow = true;
    }

    internal unsafe List<Entry> Device(int id, RawGuide raw)
    {
        var entries = new List<Entry>();
        if (!samples.TryGetValue(id, out var sample))
        {
            if (samples.Count >= 16)
            {
                Invalidate();
                return [new("raw_descriptor", "limit", raw.ArrivalMs, id)];
            }

            ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)raw.Device)[1])(raw.Device);
            samples[id] = sample = new(raw.Device);
        }

        var connected = (raw.Current & 1) != 0;
        if (!connected)
        {
            sample.Connected = false;
            sample.Previous = null;
            sample.Descriptor = new(false, 0, 0, [], "disconnected");
            Invalidate();
            return [new("raw_descriptor", "disconnected", raw.ArrivalMs, id)];
        }

        // Never retain an old label/index across reconnect, even if the COM identity is unchanged.
        if (!sample.Connected)
        {
            sample.Previous = null;
            sample.Origin = null;
            sample.Ready = false;
        }

        sample.Connected = true;
        sample.Descriptor = GameInput.Descriptor(sample.Handle);
        if (!sample.Descriptor.Valid)
            Invalidate();
        entries.Add(new("raw_descriptor", "descriptor", raw.ArrivalMs, id, Status: sample.Descriptor.Status, Hresult: sample.Descriptor.Hresult, Count: sample.Descriptor.Labels.Length, SupportedInput: sample.Descriptor.SupportedInput));
        foreach (var label in sample.Descriptor.Labels)
            entries.Add(new("raw_descriptor", "label", raw.ArrivalMs, id, LabelIndex: label.Index, Label: label.Label, MappedInputKinds: label.MappedInputKinds));
        return entries;
    }

    internal RawSelection Selection()
    {
        if (Invalid)
            return new("inconclusive: descriptor_or_connection_invalid");
        var connected = samples.Where(p => p.Value.Connected).ToArray();
        if (connected.Length == 0)
            return new("inconclusive: no_relevant_descriptor_observed");
        var candidates = new List<(int Device, int Index)>();
        foreach (var (id, sample) in connected)
        {
            if (!sample.Descriptor.Valid)
                return new("inconclusive: invalid_descriptor");
            var labels = sample.Descriptor.Labels.Where(l => l.Label == 1).ToArray();
            if (labels.Length > 1)
                return new("inconclusive: duplicate_XboxGuide_label");
            if (labels.Length == 1)
            {
                if ((sample.Descriptor.SupportedInput & 4) == 0)
                    return new("inconclusive: ControllerButton_not_announced");
                candidates.Add((id, labels[0].Index));
            }
        }

        return candidates.Count switch
        {
            0 => new("raw_label_unavailable: XboxGuide_not_exposed"),
            1 => new("raw_candidate", candidates[0].Device, candidates[0].Index),
            _ => new("inconclusive: multiple_XboxGuide_candidates")
        };
    }

    internal IEnumerable<Entry> Poll(GameInput backend, double elapsed)
    {
        var selection = Selection();
        if (selection.Status != "raw_candidate")
            yield break;
        var id = selection.Device!.Value;
        var index = selection.Index!.Value;
        var sample = samples[id];
        var read = backend.Read(sample.Handle, 4); // Force the descriptor-backed raw route even when Gamepad is also advertised.
        if (!read.Valid || read.Count != sample.Descriptor.Labels.Length || index >= read.Count)
        {
            Invalidate();
            sample.Previous = null;
            yield return new("gameinput_raw_guide", "reading_invalid", elapsed, id, Status: !read.Valid ? read.Status : "descriptor_reading_count_mismatch", Hresult: read.Hresult, LabelIndex: index, InputKind: 4);
            yield break;
        }

        sample.Origin ??= read.Timestamp;
        ulong? relative = read.Timestamp >= sample.Origin ? read.Timestamp - sample.Origin.Value : null;
        if (relative is null || (sample.Previous is { } previous && read.Timestamp < previous.Timestamp))
        {
            Invalidate();
            sample.Previous = null;
            yield return new("gameinput_raw_guide", "clock_regressed", elapsed, id, LabelIndex: index);
            yield break;
        }

        var down = index < 64 ? (read.Low & (1UL << index)) != 0 : (read.High & (1UL << (index - 64))) != 0;
        if (sample.Previous is null)
        {
            yield return new("gameinput_raw_guide", "baseline", elapsed, id, Status: down ? "initially_pressed; no DOWN inferred" : "released baseline", NativeRelUs: relative, LabelIndex: index, InputKind: 4);
        }
        else
        {
            var old = sample.Previous.Value;
            var wasDown = index < 64 ? (old.Low & (1UL << index)) != 0 : (old.High & (1UL << (index - 64))) != 0;
            if (down != wasDown)
            {
                var edge = pairs.Observe(id, new(0, read.Timestamp, down ? 1u : 0u, wasDown ? 1u : 0u, elapsed));
                edges++;
                if (edge.Status == "paired")
                    PairedCycles++;
                yield return edge with
                {
                    Source = "gameinput_raw_guide",
                    Button = "XboxGuide",
                    NativeRelUs = relative,
                    LabelIndex = index,
                    InputKind = 4
                };
            }
        }

        sample.Previous = read;
        if (!sample.Ready)
        {
            sample.Ready = true;
            yield return new("gameinput_raw_guide", "ready", elapsed, id, Status: "unique_descriptor_and_valid_reading; human_markers_and_attempt_confirmation_required", LabelIndex: index, InputKind: 4);
        }
    }

    internal string Outcome(bool full, bool invalid, bool associated = false, bool attemptsConfirmed = false)
    {
        if (invalid || Invalid)
            return "inconclusive: raw_observation_partial_or_invalid";
        var selection = Selection();
        if (selection.Status.StartsWith("raw_label_unavailable", StringComparison.Ordinal))
            return selection.Status;
        if (selection.Status != "raw_candidate")
            return selection.Status;
        if (!full)
            return "inconclusive: raw_window_incomplete";
        if (!samples[selection.Device!.Value].Ready)
            return "inconclusive: raw_reading_missing";
        if (!associated || !attemptsConfirmed)
            return "inconclusive: human_association_and_attempt_confirmation_required; raw_pairs=" + PairedCycles;
        if (PairedCycles == 0 && edges > 0)
            return "inconclusive: unpaired_raw_edges";
        return PairedCycles > 0 ? "raw_guide_observed: descriptor_backed_edges_and_human_association" : "raw_no_edge_observed: full_valid_window_and_confirmed_attempts_only";
    }

    internal IEnumerable<Entry> Finish(double elapsed) => pairs.Finish(elapsed).Select(e => e with { Source = "gameinput_raw_guide", Button = "XboxGuide" });
    internal void Release()
    {
        foreach (var sample in samples.Values)
            GameInput.ReleaseObject(sample.Handle);
        samples.Clear();
    }
}
