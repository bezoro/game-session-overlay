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
    bool SupportsMinimize=>false;
    bool IsMinimized(WindowRef window)=>false;
    bool RequestMinimize(WindowRef window)=>false;
}

public sealed class GuardedActions(IWindowEffects effects)
{
    public async Task<ActionResult> Minimize(WindowRef window,CancellationToken cancellation)
    {
        if(cancellation.IsCancellationRequested)return new(ResultKind.Cancelled,"Cancelado antes do envio; nada minimizado.");
        if(!effects.SupportsMinimize)return new(ResultKind.Unavailable,"Minimização indisponível nesta plataforma.");
        if(window.Protected||window.Handle==0||window.Pid<=0||window.Started<=0||!effects.Valid(window))return new(ResultKind.Unavailable,"Identidade inválida ou janela protegida; minimização bloqueada.");
        if(effects.IsMinimized(window))return new(ResultKind.Completed,"A janela confirmada já está minimizada.");
        if(cancellation.IsCancellationRequested||!effects.Valid(window))return new(ResultKind.Cancelled,"Alvo mudou antes do envio; nada minimizado.");
        if(!effects.RequestMinimize(window))return new(ResultKind.Failed,"O Windows recusou a solicitação de minimizar.");
        for(var i=0;i<=20;i++)
        {
            if(cancellation.IsCancellationRequested)return new(ResultKind.Unknown,"Observação cancelada após envio; a solicitação não pode ser desfeita.");
            if(!effects.Valid(window))return new(ResultKind.Unknown,"A identidade da janela mudou após a solicitação; minimização não confirmada.");
            if(effects.IsMinimized(window))return new(ResultKind.Completed,"A mesma janela foi confirmada como minimizada.");
            if(i<20)await effects.Wait(50);
        }
        return new(ResultKind.Unknown,"Minimização solicitada; estado não confirmado em um segundo. O painel continua disponível.");
    }
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
