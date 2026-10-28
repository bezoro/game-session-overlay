using System.IO;
using System.Text.Json;

namespace ApolloOverlay;

public sealed record SessionProfile(string Id, string Name, string? DisplayId, int ScalePercent, string Side,
    string? OutputId = null, string? OutputName = null, int? VolumePercent = null);
public sealed record SessionProfileDocument(int Version, string ActiveId, SessionProfile[] Profiles);

public sealed class SessionProfiles
{
    private readonly string path;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true };
    public string ActiveId { get; private set; } = "";
    public string? RecoveryNotice { get; private set; }
    private SessionProfile[] profiles = [new("tv", "TV", null, 115, "Left"), new("desktop", "Computador", null, 100, "Left")];
    public IReadOnlyList<SessionProfile> Items => Array.AsReadOnly(profiles);
    public SessionProfiles(string root)
    {
        path = Path.Combine(root, "session-profiles.json");
        if (!File.Exists(path)) return;
        try
        {
            LocalUpdateHistory.RejectLink(path);
            if (new FileInfo(path).Length > 65536) throw new InvalidDataException();
            var stored = JsonSerializer.Deserialize<SessionProfileDocument>(File.ReadAllBytes(path), Json);
            if (stored is not { Version: 1, Profiles.Length: >= 2 and <= 24 } || stored.ActiveId is null ||
                stored.Profiles.Any(profile => !Valid(profile)) || stored.Profiles.Select(profile => profile.Id).Distinct().Count() != stored.Profiles.Length ||
                !stored.Profiles.Any(profile => profile.Id == "tv") || !stored.Profiles.Any(profile => profile.Id == "desktop")) throw new InvalidDataException();
            profiles = stored.Profiles;
            ActiveId = profiles.Any(profile => profile.Id == stored.ActiveId) ? stored.ActiveId : "";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        { RecoveryNotice = "O arquivo de perfis não pôde ser lido. Seus ajustes atuais foram preservados; salve novamente os perfis para recuperá-los."; }
    }
    private static bool Valid(SessionProfile? profile) => profile is not null &&
        profile.Id is { Length: > 0 and <= 48 } && profile.Id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') &&
        profile.Name is { Length: > 0 and <= 48 } && !profile.Name.Any(char.IsControl) &&
        profile.DisplayId is null or { Length: <= 256 } && profile.ScalePercent is 100 or 115 or 130 or 150 &&
        profile.Side is "Left" or "Right" && profile.OutputId is null or { Length: <= 1024 } &&
        profile.OutputName is null or { Length: <= 180 } && profile.VolumePercent is null or >= 0 and <= 100;
    public void Save(SessionProfile profile)
    {
        if (!Valid(profile)) throw new ArgumentException("O perfil tem valores inválidos.");
        var next = profiles.Where(item => item.Id != profile.Id).Append(profile).OrderBy(item => item.Id == "tv" ? 0 : item.Id == "desktop" ? 1 : 2).ToArray();
        if (next.Length > 24) throw new InvalidOperationException("O limite é de 24 perfis.");
        Write(next, ActiveId); profiles = next;
    }
    public void SetActive(string id)
    {
        if (!profiles.Any(profile => profile.Id == id)) throw new ArgumentException("Perfil não encontrado.");
        Write(profiles, id); ActiveId = id;
    }
    private void Write(SessionProfile[] next, string active)
    {
        var root = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(root); LocalUpdateHistory.RejectLink(root);
        if (File.Exists(path)) LocalUpdateHistory.RejectLink(path);
        LocalUpdateHistory.AtomicBytes(path, JsonSerializer.SerializeToUtf8Bytes(new SessionProfileDocument(1, active, next), Json));
    }
    public static UserSettings ApplyLayout(UserSettings current, SessionProfile profile) => SessionHubSettings.Copy(current with
    { DisplayId = profile.DisplayId, UiScalePercent = profile.ScalePercent, PanelSide = profile.Side });
    public static UserSettings RestoreUnchangedLayout(UserSettings before, UserSettings applied, UserSettings current) => SessionHubSettings.Copy(current with
    {
        DisplayId = current.DisplayId == applied.DisplayId ? before.DisplayId : current.DisplayId,
        UiScalePercent = current.UiScalePercent == applied.UiScalePercent ? before.UiScalePercent : current.UiScalePercent,
        PanelSide = current.PanelSide == applied.PanelSide ? before.PanelSide : current.PanelSide
    });
}
