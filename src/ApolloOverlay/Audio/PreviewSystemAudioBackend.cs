namespace ApolloOverlay;

public sealed class PreviewSystemAudioBackend : ISystemAudioBackend
{
    public SystemAudioSnapshot State
    {
        get;
        set;
    } = new([new("preview-output", "Sample audio output")], "preview-output", "preview-output", "preview-mic", "Sample microphone", false, "Audio preview.");

    public SystemAudioSnapshot Read() => State;
    public ActionResult SetMicrophoneMuted(string expectedId, bool muted, Func<bool> continuing)
    {
        if (!continuing() || expectedId != State.MicrophoneId)
            return new(ResultKind.Cancelled, "The preview context changed.");
        State = State with
        {
            MicrophoneMuted = muted
        };
        return new(ResultKind.Simulated, "Preview: microphone state changed.");
    }

    public ActionResult SetOutputs(string expectedConsole, string expectedMedia, string console, string media, Func<bool> continuing)
    {
        if (!continuing() || expectedConsole != State.ConsoleOutputId || expectedMedia != State.MediaOutputId)
            return new(ResultKind.Cancelled, "The preview context changed.");
        State = State with
        {
            ConsoleOutputId = console,
            MediaOutputId = media
        };
        return new(ResultKind.Simulated, "Preview: output selected.");
    }
}
