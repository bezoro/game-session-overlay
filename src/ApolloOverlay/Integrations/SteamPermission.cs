namespace ApolloOverlay;

public enum ModuleEvidence
{
    Unknown,
    Absent,
    Denied,
    Loaded
}

public sealed class SteamPermission
{
    private WindowRef? verified;
    private string shortcut = "Shift+Tab";
    public void Verify(WindowRef target, string keys)
    {
        verified = target;
        shortcut = keys;
    }

    public bool ToggleAllowed(WindowRef? target, ModuleEvidence evidence, string keys) => evidence == ModuleEvidence.Loaded && target is not null && target.Same(verified) && shortcut == keys;
    public void Invalidate() => verified = null;
    public static string Reason(ModuleEvidence evidence) => evidence switch
    {
        ModuleEvidence.Loaded => "Module loaded; test and verify it in this session before toggling.",
        ModuleEvidence.Absent => "The overlay module was not observed in this window.",
        ModuleEvidence.Denied => "This window's modules are inaccessible; support is unknown.",
        _ => "Steam running or the game's origin does not prove support."
    };
}
