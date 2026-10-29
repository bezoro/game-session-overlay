using System.ComponentModel;

namespace ApolloOverlay;

public interface ITextEntryEffects
{
    bool Valid(WindowRef target);
    bool Foreground(WindowRef target);
    Task<ActionResult> FocusAsync(WindowRef target, CancellationToken cancellation);
    uint Send(char value, bool releaseOnly);
}

public sealed class SessionTextEntry(ITextEntryEffects effects)
{
    private readonly SemaphoreSlim owner = new(1, 1);
    public async Task<ActionResult> InsertAsync(WindowRef target, string text, Func<bool> continuing, CancellationToken cancellation = default)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 512 || text.Any(char.IsControl) || !ValidUnicode(text))
            return new(ResultKind.Unavailable, "Use de 1 a 512 caracteres, sem Enter ou comandos de controle.");
        if (target.Protected || target.Handle == 0 || target.Pid <= 0 || target.Started <= 0 || !effects.Valid(target))
            return new(ResultKind.Unavailable, "A janela de destino não está disponível.");
        if (!await owner.WaitAsync(0, cancellation))
            return new(ResultKind.Unavailable, "Outra inserção de texto está em andamento.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(8000);
        var sent = 0;
        char? pendingRelease = null;
        var uncertain = false;
        async Task<ActionResult> Run()
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (!continuing())
                return new(ResultKind.Cancelled, "Inserção cancelada antes do envio.");
            var focused = await effects.FocusAsync(target, deadline.Token);
            if (focused.Kind is not (ResultKind.Completed or ResultKind.Requested or ResultKind.Simulated))
                return focused;
            var stable = System.Diagnostics.Stopwatch.StartNew();
            long? confirmedAt = null;
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (!continuing() || !effects.Valid(target))
                    return new(ResultKind.Cancelled, "O foco ou a entrada mudou. Nenhum texto foi enviado.");
                if (effects.Foreground(target))
                {
                    confirmedAt ??= stable.ElapsedMilliseconds;
                    if (stable.ElapsedMilliseconds - confirmedAt >= 500)
                        break;
                }
                else if (confirmedAt is not null)
                    return new(ResultKind.Cancelled, "A janela perdeu o foco antes do envio. Nenhum texto foi enviado.");
                if (stable.ElapsedMilliseconds >= 2000)
                    return new(ResultKind.Unavailable, "O Windows não confirmou o foco da janela. Nenhum texto foi enviado.");
                await Task.Delay(10, deadline.Token);
            }

            foreach (var character in text)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (!continuing() || !effects.Valid(target) || !effects.Foreground(target))
                    return new(sent == 0 ? ResultKind.Cancelled : ResultKind.Partial, sent == 0 ? "Inserção cancelada antes do envio." : $"O contexto mudou após {sent} caracteres. Não haverá repetição automática.");
                pendingRelease = character;
                var accepted = effects.Send(character, false);
                if (accepted == 2)
                {
                    pendingRelease = null;
                    sent++;
                    if (sent % 8 == 0 && !char.IsHighSurrogate(character))
                        await Task.Delay(1, deadline.Token);
                }
                else
                {
                    if (accepted == 0)
                        pendingRelease = null;
                    uncertain = accepted > 2;
                    return new(sent == 0 && accepted == 0 ? ResultKind.Failed : ResultKind.Partial, $"Inserção incompleta após {sent} caracteres. Confira o campo antes de repetir.");
                }
            }

            return new(focused.Kind == ResultKind.Simulated ? ResultKind.Simulated : ResultKind.Requested, "Texto enviado à janela escolhida, sem pressionar Enter. Confira o campo antes de continuar.");
        }

        ActionResult result;
        try
        {
            result = await Run();
        }
        catch (OperationCanceledException)
        {
            result = new(sent == 0 && pendingRelease is null ? ResultKind.Cancelled : ResultKind.Unknown, "Inserção interrompida. Confira o campo antes de tentar novamente.");
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            result = new(sent == 0 && pendingRelease is null ? ResultKind.Unavailable : ResultKind.Unknown, "A janela não confirmou a inserção. Confira o campo antes de repetir.");
        }
        finally
        {
            if (pendingRelease is { } character)
            {
                try
                {
                    if (effects.Send(character, true) != 1)
                        uncertain = true;
                }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException)
                {
                    uncertain = true;
                }
            }

            owner.Release();
        }

        return uncertain ? new(ResultKind.Unknown, "O envio ou a liberação de texto não foi confirmado. Não repita automaticamente; confira a janela.") : result;
    }

    private static bool ValidUnicode(string text)
    {
        for (var index = 0; index < text.Length; index++)
            if (char.IsSurrogate(text[index]) && (!char.IsHighSurrogate(text[index]) || ++index >= text.Length || !char.IsLowSurrogate(text[index])))
                return false;
        return true;
    }

    public static string Backspace(string text)
    {
        if (text.Length == 0)
            return text;
        var last = System.Globalization.StringInfo.ParseCombiningCharacters(text);
        return text[..last[^1]];
    }
}

internal sealed class WindowsTextEntryEffects(IPlatform platform) : ITextEntryEffects
{
    public bool Valid(WindowRef target) => platform.Valid(target);
    public bool Foreground(WindowRef target) => Native.GetForegroundWindow() == target.Handle;
    public Task<ActionResult> FocusAsync(WindowRef target, CancellationToken cancellation) => platform.Switch(target);
    public uint Send(char value, bool releaseOnly)
    {
        var up = new Native.Input
        {
            Type = 1,
            Scan = value,
            Flags = 6
        };
        Native.Input[] input = releaseOnly ? [up] : [new()
        {
            Type = 1,
            Scan = value,
            Flags = 4
        }, up];
        return Native.SendInput((uint)input.Length, input, System.Runtime.InteropServices.Marshal.SizeOf<Native.Input>());
    }
}
