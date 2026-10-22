namespace ApolloOverlay;

public interface IWindowEffects
{
    bool Valid(WindowRef window);
    WindowObservation ObserveWindow(WindowRef window);
    bool IsForeground(WindowRef window);
    bool RequestFocus(WindowRef window);
    uint SendKeys(string keys);
    bool PostClose(WindowRef window);
    Task Wait(int milliseconds);
}

public sealed class GuardedActions(IWindowEffects effects)
{
    public async Task<ActionResult> Switch(WindowRef window)
    {
        if (!effects.Valid(window)) return new(ResultKind.Failed,"A identidade da janela mudou ou não pôde ser validada. Selecione novamente.");
        if (!effects.RequestFocus(window)) return new(ResultKind.Failed,"O Windows recusou a ativação da janela.");
        for(var i=0;i<6;i++) {
            if(!effects.Valid(window)) return new(ResultKind.Failed,"A janela foi substituída durante a ativação.");
            if(effects.IsForeground(window)) return new(ResultKind.Completed,"A janela selecionada está em primeiro plano.");
            await effects.Wait(50);
        }
        return new(ResultKind.Unknown,"Ativação não confirmada; nenhum atalho foi enviado.");
    }
    public async Task<ActionResult> Shortcut(WindowRef window,string keys)
    {
        var focused=await Switch(window);
        if(focused.Kind!=ResultKind.Completed) return focused;
        if(!effects.Valid(window) || !effects.IsForeground(window)) return new(ResultKind.Failed,"O alvo mudou antes do envio; atalho bloqueado.");
        return Outcome.Keys(effects.SendKeys(keys),4);
    }
    public async Task<ActionResult> Close(WindowRef window,CancellationToken cancellation)
    {
        if(cancellation.IsCancellationRequested) return new(ResultKind.Cancelled,"Cancelado antes do envio.");
        if(!window.CanClose || !effects.Valid(window)) return new(ResultKind.Failed,"Identidade inválida ou janela protegida; fechamento bloqueado.");
        if(!effects.PostClose(window)) return new(ResultKind.Failed,"WM_CLOSE recusado pelo Windows; nenhuma escalada.");
        for(var i=0;i<20;i++) {
            if(effects.ObserveWindow(window)==WindowObservation.GoneOrReplaced) return Outcome.Closure(true,false);
            if(cancellation.IsCancellationRequested) return new(ResultKind.Unknown,"Observação cancelada após envio; a solicitação não pode ser desfeita.");
            await effects.Wait(100);
        }
        return Outcome.Closure(false,true);
    }
}
