namespace ApolloOverlay;

public sealed class PreviewSystemAudioBackend : ISystemAudioBackend
{
    public SystemAudioSnapshot State
    {
        get;
        set;
    } = new([new("preview-output", "Saída de áudio de exemplo")], "preview-output", "preview-output", "preview-mic", "Microfone de exemplo", false, "Prévia de áudio.");

    public SystemAudioSnapshot Read() => State;
    public ActionResult SetMicrophoneMuted(string expectedId, bool muted, Func<bool> continuing)
    {
        if (!continuing() || expectedId != State.MicrophoneId)
            return new(ResultKind.Cancelled, "Contexto da prévia mudou.");
        State = State with
        {
            MicrophoneMuted = muted
        };
        return new(ResultKind.Simulated, "Prévia: estado do microfone alterado.");
    }

    public ActionResult SetOutputs(string expectedConsole, string expectedMedia, string console, string media, Func<bool> continuing)
    {
        if (!continuing() || expectedConsole != State.ConsoleOutputId || expectedMedia != State.MediaOutputId)
            return new(ResultKind.Cancelled, "Contexto da prévia mudou.");
        State = State with
        {
            ConsoleOutputId = console,
            MediaOutputId = media
        };
        return new(ResultKind.Simulated, "Prévia: saída selecionada.");
    }
}
