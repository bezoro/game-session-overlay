namespace ApolloOverlay;

public enum ResultKind
{
    Simulated,
    Requested,
    Completed,
    Failed,
    Unavailable,
    Partial,
    Unknown,
    Cancelled
}

public sealed record ActionResult(ResultKind Kind, string Message)
{
    public override string ToString() => $"{Kind switch { ResultKind.Simulated => "Simulado", ResultKind.Requested => "Solicitado", ResultKind.Completed => "Concluído", ResultKind.Failed => "Falhou", ResultKind.Unavailable => "Indisponível", ResultKind.Partial => "Parcial", ResultKind.Unknown => "Não confirmado", _ => "Cancelado" }} · {Message}";
}

public static class Outcome
{
    public static ActionResult Keys(uint sent, uint expected) => sent == expected ? new(ResultKind.Requested, "Atalho enviado; abertura e visibilidade não confirmadas. Reabra pelo seu atalho ou combinação do controle.") : new(sent == 0 ? ResultKind.Failed : ResultKind.Partial, "O Windows recusou parte ou todo o atalho. Soltura de teclas tentada; não haverá repetição automática.");
    public static ActionResult Closure(bool gone, bool deadline) => gone ? new(ResultKind.Completed, "A janela confirmada foi fechada.") : new(deadline ? ResultKind.Unknown : ResultKind.Requested, deadline ? "A janela continua aberta: pode haver diálogo de salvar ou recusa. Nenhuma escalada." : "WM_CLOSE enviado somente à janela confirmada.");
}
