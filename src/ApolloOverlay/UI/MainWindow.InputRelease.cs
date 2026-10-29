namespace ApolloOverlay;

public partial class MainWindow
{
    private readonly ReleaseGate releaseGate = new();
    private readonly HeldInputTracker handoffInputs = new();
    private Action? handoff;
    private Func<bool>? handoffValid;
    private bool handoffDismiss, handoffDegraded, degradedExplicit, committing;
    private long? degradedSince;
    private ReleaseSample? lastInputSample;
    internal ReleaseSample? HandoffSampleFixture
    {
        set => lastInputSample = value;
    }
    internal bool TransitionPending => handoff is not null;

    private void QueueHandoff(Action action, Func<bool>? valid = null, bool dismiss = false)
    {
        if (exiting || busy)
            return;
        if (handoff is not null)
        {
            if (!dismiss || handoffDismiss)
                return;
            CancelHandoff();
        }

        handoff = action;
        handoffValid = valid;
        handoffDismiss = dismiss;
        releaseGate.Reset();
        if (lastInputSample is { } sample && sample.Timestamp <= Environment.TickCount64)
        {
            releaseGate.Prime(sample);
            CaptureHandoffBaseline(sample);
            ObserveHandoffInputs(sample, Environment.TickCount64);
        }

        CancelHold();
        input.Cancel();
        if (!dismiss)
            RetainOperation("Pendente", "Solte os controles para continuar. B / Esc cancela antes do envio.");
        FeedbackText(dismiss ? "Solte os controles para ocultar o painel." : "Solte os controles para continuar · B / Esc cancela.");
    }

    private void CancelHandoff(bool terminalize = true)
    {
        var cancelled = terminalize && handoff is not null && !handoffDismiss && Controller.Operation is { State: "Pendente", Dispatched: false };
        handoff = null;
        handoffValid = null;
        handoffDismiss = false;
        handoffDegraded = false;
        degradedExplicit = false;
        degradedSince = null;
        handoffInputs.Reset();
        releaseGate.Reset();
        if (cancelled)
            RetainOperation("Cancelado", "Solicitação cancelada antes do envio; nenhum comando externo enviado.");
    }

    private void RejectHandoff(string message)
    {
        if (!handoffDismiss)
        {
            CancelHandoff(false);
            input.Cancel();
            RetainOperation("Cancelado", message);
            FeedbackText(message);
            return;
        }

        if (handoffDegraded)
            return;
        handoffDegraded = true;
        handoffValid = null;
        handoff = CompleteDegradedDismiss;
        degradedSince = null;
        releaseGate.Reset();
        input.Cancel();
        FeedbackText("Leitura ou contexto incerto. Solte os controles para ocultar sem retornar ao aplicativo.");
    }

    private void CompleteDegradedDismiss()
    {
        HideNow(false);
        FeedbackText("Painel ocultado sem solicitar retorno ao aplicativo.");
        if (!Controller.Platform.IsPreview)
            Controller.Diagnostics.Log("dismiss-without-focus-return", ResultKind.Cancelled);
    }

    private bool PadHeld(PadFrame pad) => HeldInputTracker.IsHeld(pad, Controller.Settings.Deadzone);
    private void CaptureHandoffBaseline(ReleaseSample sample) => handoffInputs.CaptureBaseline(sample, Controller.Settings.Deadzone);
    private void ObserveHandoffInputs(ReleaseSample sample, long now) => handoffInputs.Observe(sample, now, Controller.Settings.Deadzone);
    private void AdvanceDegradedDismiss(ReleaseSample sample, long now)
    {
        if (!sample.KeysNeutral || heldKeys.Count != 0 || handoffInputs.KnownActive)
        {
            degradedSince = null;
            return;
        }

        if (handoffInputs.UnknownHeld && !degradedExplicit)
        {
            degradedSince = null;
            FeedbackText("Leitura indisponível. Pressione e solte Esc para ocultar sem retornar ao aplicativo.");
            return;
        }

        if (degradedSince is null || now < degradedSince)
            degradedSince = now;
        if (now - degradedSince.Value >= 100)
            CommitHandoff();
    }

    private void ReaffirmDismiss(bool trusted)
    {
        if (!trusted || !handoffDegraded)
            return;
        degradedExplicit = true;
        degradedSince = null;
        FeedbackText("Solte as entradas disponíveis para ocultar sem retornar ao aplicativo.");
    }

    private void CommitHandoff()
    {
        var action = handoff;
        CancelHandoff(false);
        committing = true;
        try
        {
            action?.Invoke();
        }
        finally
        {
            committing = false;
        }
    }

    internal void AdvanceHandoff(ReleaseSample sample, long now)
    {
        if (handoff is null)
            return;
        sample = sample with
        {
            KeysNeutral = sample.KeysNeutral && heldKeys.Count == 0 && PhysicalKeysNeutral()
        };
        ObserveHandoffInputs(sample, now);
        if (handoffDegraded)
        {
            AdvanceDegradedDismiss(sample, now);
            return;
        }

        var context = InputContext;
        var state = releaseGate.Update(sample, now, context.Visible && context.Active && context.OwnForeground && !context.Exiting, Controller.Settings.Deadzone);
        if (state == ReleaseState.Cancelled || handoffValid?.Invoke() == false)
        {
            RejectHandoff("Transição cancelada; foco, controle ou alvo mudou.");
            if (handoffDegraded)
                AdvanceDegradedDismiss(sample, now);
            return;
        }

        if (state == ReleaseState.Waiting)
            return;
        CommitHandoff();
    }
}
