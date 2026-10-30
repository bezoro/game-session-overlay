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
            return new(ResultKind.Unavailable, "Use 1 to 512 characters without Enter or control commands.");
        if (target.Protected || target.Handle == 0 || target.Pid <= 0 || target.Started <= 0 || !effects.Valid(target))
            return new(ResultKind.Unavailable, "The target window is unavailable.");
        if (!await owner.WaitAsync(0, cancellation))
            return new(ResultKind.Unavailable, "Another text insertion is in progress.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(8000);
        var sent = 0;
        char? pendingRelease = null;
        var uncertain = false;
        async Task<ActionResult> Run()
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (!continuing())
                return new(ResultKind.Cancelled, "Text insertion cancelled before dispatch.");
            var focused = await effects.FocusAsync(target, deadline.Token);
            if (focused.Kind is not (ResultKind.Completed or ResultKind.Requested or ResultKind.Simulated))
                return focused;
            var stable = System.Diagnostics.Stopwatch.StartNew();
            long? confirmedAt = null;
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (!continuing() || !effects.Valid(target))
                    return new(ResultKind.Cancelled, "Focus or input changed. No text was sent.");
                if (effects.Foreground(target))
                {
                    confirmedAt ??= stable.ElapsedMilliseconds;
                    if (stable.ElapsedMilliseconds - confirmedAt >= 500)
                        break;
                }
                else if (confirmedAt is not null)
                    return new(ResultKind.Cancelled, "The window lost focus before dispatch. No text was sent.");
                if (stable.ElapsedMilliseconds >= 2000)
                    return new(ResultKind.Unavailable, "Windows did not confirm window focus. No text was sent.");
                await Task.Delay(10, deadline.Token);
            }

            foreach (var character in text)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (!continuing() || !effects.Valid(target) || !effects.Foreground(target))
                    return new(sent == 0 ? ResultKind.Cancelled : ResultKind.Partial, sent == 0 ? "Text insertion cancelled before dispatch." : $"The context changed after {sent} characters. There will be no automatic retry.");
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
                    return new(sent == 0 && accepted == 0 ? ResultKind.Failed : ResultKind.Partial, $"Text insertion was incomplete after {sent} characters. Check the field before trying again.");
                }
            }

            return new(focused.Kind == ResultKind.Simulated ? ResultKind.Simulated : ResultKind.Requested, "Text sent to the selected window without pressing Enter. Check the field before continuing.");
        }

        ActionResult result;
        try
        {
            result = await Run();
        }
        catch (OperationCanceledException)
        {
            result = new(sent == 0 && pendingRelease is null ? ResultKind.Cancelled : ResultKind.Unknown, "Text insertion interrupted. Check the field before trying again.");
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            result = new(sent == 0 && pendingRelease is null ? ResultKind.Unavailable : ResultKind.Unknown, "The window did not confirm text insertion. Check the field before trying again.");
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

        return uncertain ? new(ResultKind.Unknown, "Text dispatch or key release was not confirmed. Do not retry automatically; check the window.") : result;
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
