using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ApolloOverlay;

public sealed record InstalledBuild(string BuildId, string Version, string Commit, DateTimeOffset InstalledAt, bool Current = false, bool LastStartFailed = false);
internal sealed record InstallIdentity(string Version, string BuildId, string Commit, string Channel = "testing");
internal sealed record InstallPayload(InstalledBuild Build, Dictionary<string, string> Files);
internal sealed record InstallState(int SchemaVersion, string? Current, string? Previous, bool Pending, InstalledBuild[] Builds);
internal sealed record InstallSettingsSnapshot(bool Missing, string? Sha256);

/// <summary>Version directories are immutable. Only the small activation pointer is replaced.</summary>
internal sealed class ManagedInstallStore
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true };
    internal string Root { get; }
    internal string SettingsPath { get; }
    internal string StatePath => Path.Combine(Root, "install-state.json");
    internal string Versions => Path.Combine(Root, "versions");
    internal ManagedInstallStore(string root, string settingsPath)
    {
        Root = Path.GetFullPath(root); SettingsPath = Path.GetFullPath(settingsPath);
        Directory.CreateDirectory(Root); RejectLink(Root);
    }
    internal IDisposable Acquire() => new FileStream(Path.Combine(Root, "install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    internal static string SafeId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || !Regex.IsMatch(id, "^[A-Za-z0-9][A-Za-z0-9._-]{0,95}$", RegexOptions.CultureInvariant) ||
            id.EndsWith('.') || Regex.IsMatch(id.Split('.')[0], "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            throw new InvalidDataException("Identificador de versão inválido.");
        return id;
    }
    internal string VersionPath(string id) => Path.Combine(Versions, SafeId(id));
    internal static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Links não são aceitos no pacote ou na instalação.");
    }
    internal static string Child(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains('\\') || relative.Contains(':') ||
            relative.Split('/').Any(p => p is "" or "." or "..")) throw new InvalidDataException("Caminho de pacote inválido.");
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Caminho fora do pacote.");
        return full;
    }
    internal static void AtomicWrite<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, value, Json); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(temp, path, path + ".previous", true);
            else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    internal static void AtomicBytes(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(temporary, path, path + ".previous", true); else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal static string SafeDirectory(string root, string relative)
    {
        var current = Path.GetFullPath(root); RejectLink(current);
        foreach (var part in relative.Split('/'))
        {
            current = Child(current, part); Directory.CreateDirectory(current); RejectLink(current);
        }
        return current;
    }
    internal static T Read<T>(string path)
    {
        RejectLink(path);
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Metadados de instalação muito grandes.");
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("Metadados vazios.");
    }
    internal InstallState Load()
    {
        if (!File.Exists(StatePath)) return File.Exists(StatePath + ".previous") ? Validate(Read<InstallState>(StatePath + ".previous")) : new(1, null, null, false, []);
        try { return Validate(Read<InstallState>(StatePath)); }
        catch (Exception e) when (e is IOException or JsonException or InvalidDataException)
        {
            if (!File.Exists(StatePath + ".previous")) throw new InvalidDataException("Estado de instalação inválido; nenhuma versão foi substituída.", e);
            return Validate(Read<InstallState>(StatePath + ".previous"));
        }
    }
    private static InstallState Validate(InstallState state)
    {
        if (state.SchemaVersion != 1 || state.Builds is null || state.Builds.Length > 10000 || state.Builds.Select(x => x.BuildId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != state.Builds.Length)
            throw new InvalidDataException("Histórico de instalação inválido.");
        foreach (var item in state.Builds) SafeId(item.BuildId);
        if (state.Current is {} current && !state.Builds.Any(b => b.BuildId == SafeId(current)) ||
            state.Previous is {} previous && !state.Builds.Any(b => b.BuildId == SafeId(previous))) throw new InvalidDataException("Versão ativa ausente do histórico.");
        return state;
    }
    internal IReadOnlyList<InstalledBuild> History()
    {
        var state = Load();
        return state.Builds.OrderByDescending(b => b.InstalledAt).Select(b => b with { Current = b.BuildId == state.Current }).ToArray();
    }
    internal static Dictionary<string, string> Inventory(string source)
    {
        source = Path.GetFullPath(source); RejectLink(source);
        var inventory = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long bytes = 0;
        var pending = new Stack<string>(); pending.Push(source);
        while (pending.TryPop(out var folder))
        {
            RejectLink(folder);
            foreach (var directory in Directory.EnumerateDirectories(folder)) { RejectLink(directory); pending.Push(directory); }
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                RejectLink(file);
                var name = Path.GetRelativePath(source, file).Replace(Path.DirectorySeparatorChar, '/');
                if (name is ".installed.json" or ".installed.json.previous") continue;
                Child(source, name);
                bytes += new FileInfo(file).Length;
                if (bytes > 1536L * 1024 * 1024 || inventory.Count >= 5000) throw new InvalidDataException("Pacote excede o limite de tamanho ou arquivos.");
                using var stream = File.OpenRead(file);
                inventory.Add(name, Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
            }
        }
        foreach (var required in new[] { "ApolloOverlay.exe", "ApolloOverlay.dll", "ApolloOverlay.runtimeconfig.json", "build-info.json" })
            if (!inventory.ContainsKey(required)) throw new InvalidDataException("Pacote incompleto: " + required);
        return inventory;
    }
    internal static void CheckInventory(string source, IReadOnlyDictionary<string, string> expected)
    {
        var actual = Inventory(source);
        if (actual.Count != expected.Count || actual.Any(pair => !expected.TryGetValue(pair.Key, out var hash) || !string.Equals(pair.Value, hash, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Os arquivos do pacote foram alterados; a versão ativa foi preservada.");
    }
    internal static InstallIdentity Identity(string source)
    {
        var identity = Read<InstallIdentity>(Path.Combine(source, "build-info.json"));
        SafeId(identity.BuildId);
        if (string.IsNullOrWhiteSpace(identity.Version) || identity.Version.Length > 100 || identity.Commit is null || identity.Commit.Length > 100)
            throw new InvalidDataException("Identidade de compilação inválida.");
        return identity;
    }
    internal InstalledBuild Import(string source, string buildId, string version, string commit, IReadOnlyDictionary<string, string>? inventory = null)
    {
        SafeId(buildId);
        var identity = Identity(source);
        if (identity.BuildId != buildId || identity.Version != version || identity.Commit != commit) throw new InvalidDataException("A identidade do pacote não corresponde à versão escolhida.");
        var files = inventory is null ? Inventory(source) : new Dictionary<string, string>(inventory, StringComparer.OrdinalIgnoreCase);
        CheckInventory(source, files);
        SafeDirectory(Root, "versions");
        var target = VersionPath(buildId);
        if (Directory.Exists(target))
        {
            var existing = Payload(buildId); CheckInventory(target, files);
            if (existing.Build.Version != version || existing.Build.Commit != commit) throw new InvalidDataException("Esta versão já existe com outra identidade.");
            return existing.Build;
        }
        var stage = Path.Combine(Versions, ".stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            CopyFiles(source, stage, files);
            CheckInventory(stage, files);
            var build = new InstalledBuild(buildId, version, commit, DateTimeOffset.UtcNow);
            AtomicWrite(Path.Combine(stage, ".installed.json"), new InstallPayload(build, files));
            Directory.Move(stage, target);
            return build;
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }
    internal static void CopyFiles(string source, string target, IReadOnlyDictionary<string, string> files)
    {
        foreach (var file in files.Keys)
        {
            var src = Child(source, file); RejectLink(src);
            var dst = Child(target, file); Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src, dst, false);
        }
    }
    internal InstallPayload Payload(string id)
    {
        RejectLink(Versions);
        var path = VersionPath(id); RejectLink(path);
        var payload = Read<InstallPayload>(Path.Combine(path, ".installed.json"));
        if (payload.Build.BuildId != id || payload.Files is null) throw new InvalidDataException("Identidade local inválida.");
        return payload;
    }
    internal void SnapshotSettings(string id)
    {
        var directory = SafeDirectory(Root, "settings-backups/" + SafeId(id));
        if (File.Exists(SettingsPath))
        {
            RejectLink(SettingsPath);
            var bytes = File.ReadAllBytes(SettingsPath);
            if (bytes.Length > 1024 * 1024) throw new InvalidDataException("Configuração muito grande para preservar.");
            AtomicBytes(Path.Combine(directory, "settings.json"), bytes);
            AtomicWrite(Path.Combine(directory, "snapshot.json"), new InstallSettingsSnapshot(false, Convert.ToHexString(SHA256.HashData(bytes))));
        }
        else AtomicWrite(Path.Combine(directory, "snapshot.json"), new InstallSettingsSnapshot(true, null));
    }
    internal void RestoreSettings(string id)
    {
        var directory = Path.Combine(Root, "settings-backups", SafeId(id));
        var path = Path.Combine(directory, "settings.json");
        var descriptor = Path.Combine(directory, "snapshot.json");
        if (!File.Exists(descriptor)) return;
        RejectLink(Path.Combine(Root, "settings-backups")); RejectLink(directory);
        var snapshot = Read<InstallSettingsSnapshot>(descriptor);
        if (!snapshot.Missing)
        {
            byte[]? bytes = null;
            foreach (var candidate in new[] { path, path + ".previous" })
                if (File.Exists(candidate))
                {
                    RejectLink(candidate); var value = File.ReadAllBytes(candidate);
                    if (value.Length <= 1024 * 1024 && string.Equals(Convert.ToHexString(SHA256.HashData(value)), snapshot.Sha256, StringComparison.OrdinalIgnoreCase)) { bytes = value; break; }
                }
            if (bytes is null) throw new InvalidDataException("O backup de ajustes está danificado. Os ajustes atuais foram preservados.");
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            // Keep the user's newest file before restoring this version's compatible snapshot.
            if (File.Exists(SettingsPath)) File.Copy(SettingsPath, SettingsPath + ".before-rollback", true);
            AtomicBytes(SettingsPath, bytes);
        }
        else if (File.Exists(SettingsPath))
        { File.Move(SettingsPath, SettingsPath + ".before-rollback", true); File.Delete(SettingsPath + ".previous"); }
    }
    internal bool Activate(InstalledBuild target, bool rollback, Func<InstalledBuild, bool> launchHealthy)
    {
        var prior = Load();
        var payload = Payload(target.BuildId); CheckInventory(VersionPath(target.BuildId), payload.Files);
        if (prior.Current == target.BuildId) throw new InvalidOperationException("Esta versão já está ativa.");
        if (prior.Current is {} current) SnapshotSettings(current); else SnapshotSettings("before-first-install");
        var builds = prior.Builds.Where(b => b.BuildId != target.BuildId).Append(target with { Current = false }).ToArray();
        var next = new InstallState(1, target.BuildId, prior.Current, true, builds);
        AtomicWrite(StatePath, next);
        bool healthy;
        try { if (rollback) RestoreSettings(target.BuildId); healthy = launchHealthy(target); }
        catch { RecoverPending(); throw; }
        if (!healthy) { RecoverPending(); return false; }
        AtomicWrite(StatePath, next with { Pending = false, Builds = next.Builds.Select(b => b.BuildId == target.BuildId ? b with { LastStartFailed = false } : b).ToArray() });
        return true;
    }
    internal InstallState RecoverPending()
    {
        var state = Load();
        if (!state.Pending) return state;
        RestoreSettings(state.Previous ?? "before-first-install");
        var restored = state with { Current = state.Previous, Previous = null, Pending = false, Builds = state.Builds.Select(b => b.BuildId == state.Current ? b with { LastStartFailed = true } : b).ToArray() };
        AtomicWrite(StatePath, restored);
        return restored;
    }
}
