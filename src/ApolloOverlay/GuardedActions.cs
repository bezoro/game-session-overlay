using ApolloOverlay.Automation;

namespace ApolloOverlay;

public readonly record struct SteamKeyReceipt(bool Started, uint? Accepted);
public interface IWindowEffects
{
    bool Valid(WindowRef window);
    WindowObservation ObserveWindow(WindowRef window);
    bool IsForeground(WindowRef window);
    bool RequestFocus(WindowRef window);
    uint SendKeys(string keys);
    bool PostClose(WindowRef window);
    Task Wait(int milliseconds);
    long ShortcutTime => Environment.TickCount64;

    SteamKeyReceipt SendSteamEvent(ushort key, bool up) => new(false, 0);
    bool SteamKeysReleased(int mask) => false;
    void SteamOwnership(int mask)
    {
    }

    void RecordSteamSequence(SteamSequenceEvidence evidence)
    {
    }

    bool SupportsMinimize => false;

    bool IsMinimized(WindowRef window) => false;
    bool RequestMinimize(WindowRef window) => false;
}

public sealed class GuardedActions(IWindowEffects effects, Action<SteamSequenceEvidence>? progress = null)
{
    public async Task<ActionResult> Minimize(WindowRef window, CancellationToken cancellation)
    {
        if (cancellation.IsCancellationRequested)
            return new(ResultKind.Cancelled, "Cancelado antes do envio; nada minimizado.");
        if (!effects.SupportsMinimize)
            return new(ResultKind.Unavailable, "Minimização indisponível nesta plataforma.");
        if (window.Protected || window.Handle == 0 || window.Pid <= 0 || window.Started <= 0 || !effects.Valid(window))
            return new(ResultKind.Unavailable, "Identidade inválida ou janela protegida; minimização bloqueada.");
        if (effects.IsMinimized(window))
            return new(ResultKind.Completed, "A janela confirmada já está minimizada.");
        if (cancellation.IsCancellationRequested || !effects.Valid(window))
            return new(ResultKind.Cancelled, "Alvo mudou antes do envio; nada minimizado.");
        if (!effects.RequestMinimize(window))
            return new(ResultKind.Failed, "O Windows recusou a solicitação de minimizar.");
        for (var i = 0; i <= 20; i++)
        {
            if (cancellation.IsCancellationRequested)
                return new(ResultKind.Unknown, "Observação cancelada após envio; a solicitação não pode ser desfeita.");
            if (!effects.Valid(window))
                return new(ResultKind.Unknown, "A identidade da janela mudou após a solicitação; minimização não confirmada.");
            if (effects.IsMinimized(window))
                return new(ResultKind.Completed, "A mesma janela foi confirmada como minimizada.");
            if (i < 20)
                await effects.Wait(50);
        }

        return new(ResultKind.Unknown, "Minimização solicitada; estado não confirmado em um segundo. O painel continua disponível.");
    }

    public async Task<ActionResult> Switch(WindowRef window)
    {
        if (!effects.Valid(window))
            return new(ResultKind.Failed, "A identidade da janela mudou ou não pôde ser validada. Selecione novamente.");
        if (!effects.RequestFocus(window))
            return new(ResultKind.Failed, "O Windows recusou a ativação da janela.");
        for (var i = 0; i < 6; i++)
        {
            if (!effects.Valid(window))
                return new(ResultKind.Failed, "A janela foi substituída durante a ativação.");
            if (effects.IsForeground(window))
                return new(ResultKind.Completed, "A janela selecionada está em primeiro plano.");
            await effects.Wait(50);
        }

        return new(ResultKind.Unknown, "Ativação não confirmada; nenhum atalho foi enviado.");
    }

    public Task<ActionResult> Shortcut(WindowRef window, string keys) => Shortcut(window, keys, CancellationToken.None, () => true);
    public async Task<ActionResult> SteamMenu(WindowRef window, CancellationToken cancellation, Func<bool> continuing, Func<bool> canUse)
    {
        bool Allowed() => !cancellation.IsCancellationRequested && continuing() && canUse();
        if (!Allowed())
            return new(ResultKind.Cancelled, "Contexto ou solicitação indisponível; nenhum F1 enviado.");
        if (!effects.RequestFocus(window))
            return new(ResultKind.Failed, "O Windows recusou o foco do Big Picture.");
        for (var i = 0; i < 6; i++)
        {
            if (!Allowed())
                return new(ResultKind.Cancelled, "Foco já solicitado; F1 cancelado antes do envio.");
            if (effects.IsForeground(window))
            {
                if (!Allowed() || !effects.IsForeground(window))
                    return new(ResultKind.Cancelled, "Alvo mudou antes do F1.");
                return Outcome.Keys(effects.SendKeys("F1"), 2);
            }

            await effects.Wait(50);
        }

        return new(ResultKind.Unknown, "Foco Big Picture não confirmado; nenhum F1 enviado.");
    }

    private async Task<ActionResult> SteamSequence(WindowRef window, CancellationToken cancellation, Func<bool> continuing)
    {
        var events = new List<SteamKeyEvent>(6);
        var start = effects.ShortcutTime;
        var held = 0;
        var interrupted = false;
        var uncertain = false;
        var logFailed = false;
        var refused = false;
        bool Allowed() => !cancellation.IsCancellationRequested && !logFailed && effects.ShortcutTime - start < 10000 && continuing() && effects.Valid(window) && effects.IsForeground(window);
        bool Released(int mask)
        {
            try
            {
                return effects.SteamKeysReleased(mask);
            }
            catch (Exception)
            {
                uncertain = true;
                return false;
            }
        }

        void Publish(bool? released = null)
        {
            var evidence = new SteamSequenceEvidence("Shift+Tab", [20, 60, 20], 500, events.ToArray(), held, released);
            try
            {
                effects.RecordSteamSequence(evidence);
                if (!logFailed)
                    progress?.Invoke(evidence);
            }
            catch (Exception)
            {
                logFailed = true;
                uncertain = true;
            }
        }

        bool Send(ushort key, bool up, bool cleanup = false)
        {
            if (!up && !Allowed())
            {
                interrupted = true;
                return false;
            }

            SteamKeyReceipt receipt;
            try
            {
                receipt = effects.SendSteamEvent(key, up);
            }
            catch (Exception)
            {
                receipt = new(true, null);
            }

            var bit = key == 16 ? 1 : 2;
            // Started is reported by the adapter only at the actual native-call boundary.
            if (!up && receipt.Started && receipt.Accepted != 0)
                held |= bit;
            if (receipt.Accepted is null || receipt.Accepted > 1)
                uncertain = true;
            if (cleanup && (!receipt.Started || receipt.Accepted != 1))
                uncertain = true;
            if (up && receipt.Accepted == 1 && Released(bit))
                held &= ~bit;
            effects.SteamOwnership(held);
            events.Add(new(key, up, cleanup, effects.ShortcutTime - start, receipt.Started, receipt.Accepted));
            Publish();
            if (!receipt.Started || receipt.Accepted != 1)
            {
                refused = true;
                return false;
            }

            return !logFailed;
        }

        async Task<bool> Pause(int milliseconds)
        {
            var until = effects.ShortcutTime + milliseconds;
            while (effects.ShortcutTime < until)
            {
                if (!Allowed())
                {
                    interrupted = true;
                    return false;
                }

                await effects.Wait((int)Math.Min(10, until - effects.ShortcutTime));
                if (!Allowed())
                {
                    interrupted = true;
                    return false;
                }
            }

            return true;
        }

        Publish();
        try
        {
            if (Send(16, false) && await Pause(20) && Send(9, false) && await Pause(60) && Send(9, true) && await Pause(20))
                Send(16, true);
        }
        catch (Exception)
        {
            uncertain = true;
        }
        finally
        {
            foreach (var key in new ushort[]
            {
                9,
                16
            }

            )
            {
                var bit = key == 16 ? 1 : 2;
                if ((held & bit) != 0)
                    try
                    {
                        Send(key, true, true);
                    }
                    catch (Exception)
                    {
                        uncertain = true;
                    }
            }

            var until = effects.ShortcutTime + 100;
            while (held != 0 && effects.ShortcutTime < until)
            {
                if (Released(held))
                {
                    held = 0;
                    break;
                }

                try
                {
                    await effects.Wait(10);
                }
                catch (Exception)
                {
                    uncertain = true;
                    break;
                }
            }

            effects.SteamOwnership(held);
            Publish(held == 0);
        }

        var normal = events.Where(e => !e.Cleanup).ToArray();
        var accepted = normal.Where(e => e.Accepted.HasValue).Sum(e => (long)e.Accepted!.Value);
        if (uncertain || held != 0)
            return new(ResultKind.Unknown, "Sequência ou liberação não confirmada; não repita o toggle sem verificar o estado.");
        if (accepted == 4 && !refused && !interrupted)
            return new(ResultKind.Requested, "Atalho Shift+Tab solicitado; teclas liberadas. Visibilidade Steam não confirmada.");
        if (accepted > 0)
            return new(ResultKind.Partial, "Sequência interrompida após envio parcial; teclas liberadas. Verifique o estado antes de nova tentativa.");
        return new(interrupted ? ResultKind.Cancelled : ResultKind.Failed, "Sequência interrompida com zero eventos aceitos.");
    }

    public async Task<ActionResult> FocusStable(WindowRef window, CancellationToken cancellation, Func<bool> continuing)
    {
        bool Allowed() => !cancellation.IsCancellationRequested && continuing() && effects.Valid(window);
        if (!Allowed())
            return new(ResultKind.Cancelled, "Cancelado antes de solicitar foco.");
        if (!effects.RequestFocus(window))
            return new(ResultKind.Failed, "O Windows recusou o foco; nenhum atalho enviado.");
        if (!effects.Valid(window))
            return new(ResultKind.Failed, "A janela foi substituída durante a ativação; nenhum atalho enviado.");
        var confirmed = false;
        for (var i = 0; i <= 6; i++)
        {
            if (!Allowed())
                return new(ResultKind.Cancelled, "Alvo ou solicitação mudou durante o foco; nenhum atalho enviado.");
            if (effects.IsForeground(window))
            {
                confirmed = true;
                break;
            }

            if (i < 6)
            {
                await effects.Wait(50);
                if (!Allowed())
                    return new(ResultKind.Cancelled, "Foco cancelado; nenhum atalho enviado.");
            }
        }

        if (!confirmed)
            return new(ResultKind.Unknown, "Foco não confirmado em 300 ms; nenhum atalho enviado.");
        for (var i = 0; i < 10; i++)
        {
            if (!Allowed() || !effects.IsForeground(window))
                return new(ResultKind.Cancelled, "Foco ou entrada alterada; nenhum atalho enviado.");
            await effects.Wait(50);
            if (!Allowed() || !effects.IsForeground(window))
                return new(ResultKind.Cancelled, "Foco ou entrada alterada; nenhum atalho enviado.");
        }

        return new(ResultKind.Completed, "A mesma janela permaneceu em primeiro plano por 500 ms.");
    }

    public async Task<ActionResult> Shortcut(WindowRef window, string keys, CancellationToken cancellation, Func<bool> continuing)
    {
        if (keys is not ("Shift+Tab" or "Ctrl+Tab"))
        {
            if (cancellation.IsCancellationRequested || !continuing())
                return new(ResultKind.Cancelled, "Cancelado antes do foco; nenhum atalho enviado.");
            var focus = await Switch(window);
            if (cancellation.IsCancellationRequested || !continuing())
                return new(ResultKind.Cancelled, "Foco já solicitado; atalho cancelado antes do envio.");
            if (focus.Kind != ResultKind.Completed)
                return focus;
            if (!effects.Valid(window) || !effects.IsForeground(window))
                return new(ResultKind.Failed, "O alvo mudou antes do envio; atalho bloqueado.");
            return Outcome.Keys(effects.SendKeys(keys), 4);
        }

        var focused = await FocusStable(window, cancellation, continuing);
        if (focused.Kind != ResultKind.Completed)
            return focused;
        if (cancellation.IsCancellationRequested || !continuing() || !effects.Valid(window) || !effects.IsForeground(window))
            return new(ResultKind.Cancelled, "Cancelado antes do envio; nenhum atalho enviado.");
        if (keys == "Shift+Tab")
            return await SteamSequence(window, cancellation, continuing);
        return Outcome.Keys(effects.SendKeys(keys), 4);
    }

    public async Task<ActionResult> Close(WindowRef window, CancellationToken cancellation)
    {
        if (cancellation.IsCancellationRequested)
            return new(ResultKind.Cancelled, "Cancelado antes do envio.");
        if (!window.CanClose || !effects.Valid(window))
            return new(ResultKind.Failed, "Identidade inválida ou janela protegida; fechamento bloqueado.");
        if (!effects.PostClose(window))
            return new(ResultKind.Failed, "WM_CLOSE recusado pelo Windows; nenhuma escalada.");
        for (var i = 0; i < 20; i++)
        {
            if (effects.ObserveWindow(window) == WindowObservation.GoneOrReplaced)
                return Outcome.Closure(true, false);
            if (cancellation.IsCancellationRequested)
                return new(ResultKind.Unknown, "Observação cancelada após envio; a solicitação não pode ser desfeita.");
            await effects.Wait(100);
        }

        return Outcome.Closure(false, true);
    }
}
