namespace ApolloOverlay;
/// <summary>Retains held input across stale/failed reads; an unknown sample never proves release.</summary>
internal sealed class HeldInputTracker
{
    private readonly bool[] held = new bool[4], known = new bool[4];
    private bool guideHeld, guideKnown;
    internal bool KnownActive => guideKnown && guideHeld || HasHeld(knownState: true);
    internal bool UnknownHeld => !guideKnown && guideHeld || HasHeld(knownState: false);

    private bool HasHeld(bool knownState)
    {
        for (var slot = 0; slot < held.Length; slot++)
            if (known[slot] == knownState && held[slot])
                return true;
        return false;
    }

    internal void Reset()
    {
        Array.Clear(held);
        Array.Clear(known);
        guideHeld = false;
        guideKnown = false;
    }

    internal void CaptureBaseline(ReleaseSample sample, int deadzone)
    {
        for (var slot = 0; slot < Math.Min(4, sample.Pads.Count); slot++)
            if (!sample.Pads[slot].ReadFailed && IsHeld(sample.Pads[slot], deadzone))
                held[slot] = true;
        guideHeld = sample.GuideHeld;
    }

    internal void Observe(ReleaseSample sample, long now, int deadzone)
    {
        Array.Clear(known);
        guideKnown = false;
        if (sample.Timestamp > now || now - sample.Timestamp > 80)
            return;
        for (var slot = 0; slot < Math.Min(4, sample.Pads.Count); slot++)
        {
            var pad = sample.Pads[slot];
            if (pad.ReadFailed)
                continue;
            known[slot] = true;
            held[slot] = IsHeld(pad, deadzone);
        }

        if (sample.GuideValid || sample.GuideHeld)
        {
            guideKnown = true;
            guideHeld = sample.GuideHeld;
        }
    }

    internal static bool IsHeld(PadFrame pad, int deadzone) => pad.Connected
        && (pad.Buttons != 0
        || Math.Abs((int)pad.X) >= deadzone * 2 / 3
        || Math.Abs((int)pad.Y) >= deadzone * 2 / 3
        || Math.Abs((int)pad.RX) >= deadzone * 2 / 3
        || Math.Abs((int)pad.RY) >= deadzone * 2 / 3
        || pad.LT >= 30
        || pad.RT >= 30);
}
