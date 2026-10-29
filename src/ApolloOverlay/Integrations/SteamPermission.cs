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
        ModuleEvidence.Loaded => "Módulo carregado; teste e confirme nesta sessão antes de alternar.",
        ModuleEvidence.Absent => "O módulo do overlay não foi observado nesta janela.",
        ModuleEvidence.Denied => "Sem acesso aos módulos desta janela; suporte desconhecido.",
        _ => "Steam aberto ou origem do jogo não comprovam suporte."
    };
}
