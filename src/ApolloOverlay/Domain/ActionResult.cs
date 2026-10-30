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
    public string? Summary
    {
        get; init;
    }
    public override string ToString() => $"{Kind switch { ResultKind.Simulated => "Simulated", ResultKind.Requested => "Requested", ResultKind.Completed => "Completed", ResultKind.Failed => "Failed", ResultKind.Unavailable => "Unavailable", ResultKind.Partial => "Partial", ResultKind.Unknown => "Unconfirmed", _ => "Cancelled" }} · {Message}";
}

public static class Outcome
{
    public static ActionResult Keys(uint sent, uint expected) => sent == expected ? new(ResultKind.Requested, "Shortcut sent; opening and visibility are unconfirmed. Reopen using your shortcut or controller combination.") : new(sent == 0 ? ResultKind.Failed : ResultKind.Partial, "Windows rejected some or all shortcut events. Key release was attempted; there will be no automatic retry.");
    public static ActionResult Closure(bool gone, bool deadline) => gone ? new(ResultKind.Completed, "The verified window was closed.") : new(deadline ? ResultKind.Unknown : ResultKind.Requested, deadline ? "The window is still open: there may be a save dialog or a refusal. No escalation was attempted." : "WM_CLOSE was sent only to the verified window.");
}
