namespace ApolloOverlay;

public sealed record AudioDeviceChoice(string Id, string Name);
public sealed record SystemAudioSnapshot(
    IReadOnlyList<AudioDeviceChoice> Outputs,
    string ConsoleOutputId,
    string MediaOutputId,
    string MicrophoneId,
    string MicrophoneName,
    bool? MicrophoneMuted,
    string Status
)
{
    public static SystemAudioSnapshot Empty { get; } = new([], "", "", "", "Microfone indisponível", null, "Áudio não consultado.");
    public string OutputName => Outputs.FirstOrDefault(output => output.Id == ConsoleOutputId)?.Name ?? "Saída indisponível";
}

public interface ISystemAudioBackend
{
    SystemAudioSnapshot Read();
    ActionResult SetMicrophoneMuted(string expectedId, bool muted, Func<bool> continuing);
    ActionResult SetOutputs(string expectedConsole, string expectedMedia, string console, string media, Func<bool> continuing);
}
