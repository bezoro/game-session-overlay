namespace ApolloOverlay;

public sealed class PadInterpreter(int deadzone = 18000)
{
    private bool armed;
    private PadButtons previous;
    private int lastDirection;
    private long repeatAt;
    public void Reset()
    {
        armed = false;
        previous = 0;
        lastDirection = 0;
    }

    public PadEvents Update(PadFrame frame, long now)
    {
        if (!frame.Connected)
        {
            Reset();
            return new(0, 0, false, true);
        }

        var vertical = frame.Buttons.HasFlag(PadButtons.Up) ? -1 : frame.Buttons.HasFlag(PadButtons.Down) ? 1 : 0;
        var horizontal = frame.Buttons.HasFlag(PadButtons.Left) ? -2 : frame.Buttons.HasFlag(PadButtons.Right) ? 2 : 0;
        var direction = vertical != 0 ? vertical : horizontal;
        if (direction == 0 && Math.Max(Math.Abs((int)frame.X), Math.Abs((int)frame.Y)) > deadzone)
            direction = Math.Abs((int)frame.X) > Math.Abs((int)frame.Y) ? frame.X < 0 ? -2 : 2 : frame.Y > 0 ? -1 : 1; // Vertical wins exact diagonal ties.
        if (!armed)
        {
            if (frame.Buttons == 0 && Math.Abs((int)frame.Y) < deadzone * 2 / 3 && Math.Abs((int)frame.X) < deadzone * 2 / 3)
                armed = true;
            previous = frame.Buttons;
            return default;
        }

        var pressed = frame.Buttons & ~previous;
        previous = frame.Buttons;
        var move = 0;
        if (direction == 0)
            lastDirection = 0;
        else if (direction != lastDirection)
        {
            move = direction;
            repeatAt = now + 420;
            lastDirection = direction;
        }
        else if (now >= repeatAt)
        {
            move = direction;
            repeatAt = now + 150;
        }

        return new(pressed, move, frame.Buttons.HasFlag(PadButtons.A), false);
    }
}
