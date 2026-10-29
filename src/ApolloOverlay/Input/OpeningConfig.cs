namespace ApolloOverlay;

public sealed record OpeningConfig(uint Modifiers, uint Key, PadButtons Buttons, int HoldMs)
{
    public static OpeningConfig Parse(string hotkey, string chord, int hold)
    {
        if (string.IsNullOrWhiteSpace(hotkey) || string.IsNullOrWhiteSpace(chord))
            throw new ArgumentException("Atalho e combinação são obrigatórios.");
        if (hold < 300 || hold > 2000)
            throw new ArgumentException("Pressão deve ficar entre 300 e 2000 ms.");
        uint modifiers = 0, key = 0;
        foreach (var token in hotkey.Split('+'))
        {
            var modifier = token.ToUpperInvariant() switch
            {
                "CTRL" => 2u,
                "ALT" => 1u,
                "SHIFT" => 4u,
                _ => 0u
            };
            if (modifier != 0)
            {
                if ((modifiers & modifier) != 0)
                    throw new ArgumentException("Modificador repetido.");
                modifiers |= modifier;
            }
            else
            {
                if (key != 0)
                    throw new ArgumentException("Use uma tecla.");
                key = token.Equals("Space", StringComparison.OrdinalIgnoreCase) ? 32u : token.Length == 1 && char.IsAsciiLetter(token[0]) ? (uint)char.ToUpperInvariant(token[0]) : 0;
                if (key == 0)
                    throw new ArgumentException("Use letra ou Space.");
            }
        }

        if (modifiers == 0 || key == 0)
            throw new ArgumentException("Use modificador e tecla.");
        PadButtons buttons = 0;
        foreach (var token in chord.Split('+'))
        {
            var button = token.ToUpperInvariant() switch
            {
                "LB" => PadButtons.LB,
                "RB" => PadButtons.RB,
                "START" => PadButtons.Start,
                "BACK" => PadButtons.Back,
                "A" => PadButtons.A,
                "B" => PadButtons.B,
                "X" => PadButtons.X,
                _ => PadButtons.None
            };
            if (button == 0 || (buttons & button) != 0)
                throw new ArgumentException("Combinação inválida.");
            buttons |= button;
        }

        var bits = (ushort)buttons;
        if ((bits & (bits - 1)) == 0)
            throw new ArgumentException("Use dois ou mais botões.");
        return new(modifiers | 0x4000, key, buttons, hold);
    }
}
