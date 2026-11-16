namespace ApolloOverlay;

internal sealed record GuideDiagnostic(string Command, ResultKind Outcome, string Reason);
internal readonly record struct GuideSample(uint NativeResult, bool Down);

/// <summary>The sole Guide gesture state machine. Its diagnostics describe the same decisions that authorize toggles.</summary>
internal sealed class GuideGestureTracker
{
    private struct SlotState
    {
        internal bool Armed, Opening;
        internal bool? Down;
        internal long PressedAt;
        internal int HoldLimit;
    }

    private readonly SlotState[] slots = new SlotState[4];
    private bool? visible, valid;
    private int? connections;
    private long generation;
    internal bool Held
    {
        get; private set;
    }
    internal bool SnapshotValid
    {
        get; private set;
    }
    internal int Connections
    {
        get; private set;
    }
    internal IReadOnlyList<GuideDiagnostic> Diagnostics { get; private set; } = [];

    internal void Reset()
    {
        Array.Clear(slots);
        visible = valid = null;
        connections = null;
        Held = SnapshotValid = false;
        Connections = 0;
        Diagnostics = [];
    }

    internal bool Update(ReadOnlySpan<GuideSample> samples, bool overlayVisible, long cycle, long now, int maximumHoldMs)
    {
        if (samples.Length != slots.Length)
            throw new ArgumentException("A Guide snapshot must contain all four XInput slots.", nameof(samples));

        List<GuideDiagnostic>? events = null;
        void Record(string command, ResultKind outcome, string reason, int? slot = null) =>
            (events ??= []).Add(new(command, outcome, slot is { } index ? reason + "/slot-" + (index + 1) : reason));

        var contextChanged = visible != overlayVisible || generation != cycle;
        if (contextChanged)
        {
            for (var slot = 0; slot < slots.Length; slot++)
                if (slots[slot].Opening)
                    Record("guide-opening", ResultKind.Cancelled, generation != cycle ? "activation-cycle-changed" : "visibility-changed", slot);
            Array.Clear(slots);
            visible = overlayVisible;
            generation = cycle;
        }

        Held = false;
        SnapshotValid = true;
        Connections = 0;
        for (var slot = 0; slot < samples.Length; slot++)
        {
            var sample = samples[slot];
            if (sample.NativeResult == 0)
            {
                Connections |= 1 << slot;
                Held |= sample.Down;
            }
            else if (sample.NativeResult != 1167)
                SnapshotValid = false;
        }

        if (valid != SnapshotValid)
            Record("guide-reading", SnapshotValid ? ResultKind.Completed : ResultKind.Unknown, SnapshotValid ? "valid" : "read-invalid");
        if (connections != Connections)
            Record("guide-connections", Connections != 0 ? ResultKind.Completed : ResultKind.Unavailable, "slot-mask-" + Connections);
        valid = SnapshotValid;
        connections = Connections;

        var toggle = false;
        for (var slot = 0; slot < samples.Length; slot++)
        {
            ref var state = ref slots[slot];
            var sample = samples[slot];
            if (sample.NativeResult != 0)
            {
                if (state.Opening)
                    Record("guide-opening", ResultKind.Cancelled, sample.NativeResult == 1167 ? "disconnected" : "reading-invalid", slot);
                state = default;
                continue;
            }

            if (state.Down != sample.Down)
                Record("guide-button", ResultKind.Completed,
                    sample.Down ? state.Armed ? "down" : "held-unarmed" : state.Down == true ? "up" : "released-baseline", slot);
            state.Down = sample.Down;

            if (!sample.Down)
            {
                if (state.Opening)
                {
                    if (!SnapshotValid)
                        Record("guide-opening", ResultKind.Cancelled, "reading-invalid", slot);
                    else if (now < state.PressedAt || now - state.PressedAt > state.HoldLimit)
                        Record("guide-opening", ResultKind.Cancelled, "hold-limit-exceeded", slot);
                    else
                    {
                        toggle = true;
                        Record("guide-opening", ResultKind.Requested, "release-qualified", slot);
                    }
                }
                state.Opening = false;
                state.Armed = true;
            }
            else if (state.Armed)
            {
                state.Armed = false;
                if (overlayVisible)
                {
                    if (SnapshotValid)
                    {
                        toggle = true;
                        Record("guide-dismiss", ResultKind.Requested, "press-qualified", slot);
                    }
                }
                else if (SnapshotValid)
                {
                    state.Opening = true;
                    state.PressedAt = now;
                    state.HoldLimit = maximumHoldMs;
                    Record("guide-opening", ResultKind.Requested, "press-started", slot);
                }
            }
            else if (state.Opening && (!SnapshotValid || now < state.PressedAt || now - state.PressedAt > state.HoldLimit))
            {
                Record("guide-opening", ResultKind.Cancelled, SnapshotValid ? "hold-limit-exceeded" : "reading-invalid", slot);
                state.Opening = false;
            }
        }

        Diagnostics = events is null ? Array.Empty<GuideDiagnostic>() : events.ToArray();
        return toggle;
    }
}
