namespace ApolloOverlay;

[Flags]
public enum PadButtons : ushort
{
    None = 0,
    Up = 1,
    Down = 2,
    Left = 4,
    Right = 8,
    Start = 16,
    Back = 32,
    LB = 256,
    RB = 512,
    A = 4096,
    B = 8192,
    X = 16384
}

public readonly record struct PadFrame(
    bool Connected,
    PadButtons Buttons,
    short X = 0,
    short Y = 0,
    short RX = 0,
    short RY = 0,
    byte LT = 0,
    byte RT = 0,
    bool ReadFailed = false
);
public readonly record struct PadEvents(PadButtons Pressed, int Direction, bool ADown, bool Disconnected);
