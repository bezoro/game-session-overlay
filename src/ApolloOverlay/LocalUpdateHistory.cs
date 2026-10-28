using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Velopack;
using Velopack.Locators;

namespace ApolloOverlay;

public sealed record InstalledBuild(string BuildId, string Version, string Commit, DateTimeOffset InstalledAt, bool Current = false, bool LastStartFailed = false);
internal sealed record CachedAsset(string PackageId, string Version, string FileName, string Sha1, string Sha256, long Size)
{
    internal VelopackAsset ToAsset() => new() { PackageId = PackageId, Version = SemanticVersion.Parse(Version), FileName = FileName, SHA1 = Sha1, SHA256 = Sha256, Size = Size, Type = VelopackAssetType.Full };
    internal static CachedAsset From(VelopackAsset value) => new(value.PackageId, value.Version.ToFullString(), value.FileName, value.SHA1, value.SHA256, value.Size);
}
internal sealed record CachedBuild(AppBuildInfo Build, CachedAsset Asset, DateTimeOffset SavedAt, DateTimeOffset? InstalledAt);
internal sealed record SettingsCopy(bool Missing, string? Sha256);
internal sealed record PendingSettingsRestore(string BuildId, string Version, string Commit);

/// <summary>Retains rollback packages outside Velopack's disposable package cache.</summary>
public sealed class LocalUpdateHistory
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true };
    internal string DataRoot { get; }
    public string Root { get; }
    private string SettingsPath => Path.Combine(DataRoot, "settings.json");
    private string PendingPath => Path.Combine(Root, "restore-settings.json");
    public LocalUpdateHistory(string? dataRoot = null)
    {
        DataRoot = Path.GetFullPath(dataRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bezoro", "GameSessionOverlay"));
        Root = Path.Combine(DataRoot, "rollback-packages");
    }
    private static string Id(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9._-]{0,95}$") || value.EndsWith('.') ||
            Regex.IsMatch(value.Split('.')[0], "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase)) throw new InvalidDataException("Identificador de versão inválido.");
        return value;
    }
    internal static string SafeFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.GetFileName(value) != value || value.Contains('\\') || value.Contains('/') || value.Contains(':') || !value.EndsWith(".nupkg", StringComparison.Ordinal) || value.Length > 200)
            throw new InvalidDataException("Nome de pacote inválido.");
        return value;
    }
    private string Folder(string buildId) => Path.Combine(Root, Id(buildId));
    private void EnsureFolder(string buildId)
    {
        Directory.CreateDirectory(DataRoot); RejectLink(DataRoot);
        Directory.CreateDirectory(Root); RejectLink(Root);
        Directory.CreateDirectory(Folder(buildId)); RejectLink(Folder(buildId));
    }
    internal static void RejectLink(string path) { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Links não são aceitos no histórico de versões."); }
    internal static void AtomicBytes(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".new-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void Write<T>(string path, T value) => AtomicBytes(path, JsonSerializer.SerializeToUtf8Bytes(value, Json));
    private static T Read<T>(string path)
    {
        RejectLink(path); if (new FileInfo(path).Length > 65536) throw new InvalidDataException("Metadados do histórico inválidos.");
        return JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), Json) ?? throw new InvalidDataException("Metadados do histórico vazios.");
    }
    private CachedBuild Record(string buildId)
    {
        RejectLink(Root); RejectLink(Folder(buildId));
        var record = Read<CachedBuild>(Path.Combine(Folder(buildId), "release.json"));
        if (record.Build is null || record.Asset is null || record.Build.BuildId != buildId || string.IsNullOrEmpty(record.Asset.Sha256)) throw new InvalidDataException("O histórico não corresponde à versão escolhida.");
        SafeFileName(record.Asset.FileName); return record;
    }
    public IReadOnlyList<InstalledBuild> List(string? currentBuildId = null)
    {
        if (!Directory.Exists(Root)) return [];
        RejectLink(Root); var result = new List<InstalledBuild>();
        foreach (var directory in Directory.EnumerateDirectories(Root).Take(10000))
        {
            try
            {
                var value = Record(Path.GetFileName(directory));
                if (value.InstalledAt is {} date && File.Exists(Path.Combine(directory, value.Asset.FileName)))
                    result.Add(new(value.Build.BuildId, value.Build.Version, value.Build.Commit, date, value.Build.BuildId == currentBuildId));
            }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or ArgumentException or UnauthorizedAccessException) { }
        }
        return result.OrderByDescending(b => b.InstalledAt).ToArray();
    }
    public async Task RecordCurrentAsync(AppBuildInfo build, IVelopackLocator? locator = null, CancellationToken cancellation = default)
    {
        locator ??= VelopackLocator.Current;
        if (locator.CurrentlyInstalledVersion is null || locator.PackagesDir is null) return;
        if (locator.CurrentlyInstalledVersion != SemanticVersion.Parse(build.Version)) throw new InvalidDataException("A versão instalada difere da identificação compilada.");
        var asset = locator.GetLocalPackages().FirstOrDefault(p => p.Type == VelopackAssetType.Full && p.Version == locator.CurrentlyInstalledVersion);
        if (asset is null)
        {
            if (File.Exists(Path.Combine(Folder(build.BuildId), "release.json")))
            {
                var retained = Record(build.BuildId);
                if (retained.Build != build) throw new InvalidDataException("A identificação do histórico foi alterada.");
                await VerifyPackageAsync(Path.Combine(Folder(build.BuildId), retained.Asset.FileName), retained.Asset.ToAsset(), retained.Build, cancellation).ConfigureAwait(false);
                Write(Path.Combine(Folder(build.BuildId), "release.json"), retained with { InstalledAt = retained.InstalledAt ?? DateTimeOffset.UtcNow });
                return;
            }
            throw new InvalidOperationException("O pacote da versão atual não pôde ser preservado para reversão. Reinstale o Setup desta versão antes de atualizar; seus ajustes permanecem no perfil do Windows.");
        }
        var path = Path.Combine(locator.PackagesDir, SafeFileName(asset.FileName));
        var verified = await VelopackAsset.FromNupkgGenerateChecksumAsync(path).ConfigureAwait(false);
        await ArchiveVerifiedAsync(path, verified, build, true, cancellation).ConfigureAwait(false);
    }
    public async Task ArchiveVerifiedAsync(string packagePath, VelopackAsset expected, AppBuildInfo build, bool installed = false, CancellationToken cancellation = default)
    {
        Id(build.BuildId); SafeFileName(expected.FileName);
        await VerifyPackageAsync(packagePath, expected, build, cancellation).ConfigureAwait(false);
        EnsureFolder(build.BuildId);
        using var lease = new FileStream(Path.Combine(Root, "history.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var metadata = Path.Combine(Folder(build.BuildId), "release.json");
        CachedBuild? previous = File.Exists(metadata) ? Record(build.BuildId) : null;
        if (previous is not null && (previous.Build != build || !previous.Asset.Sha256.Equals(expected.SHA256, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Este identificador já está associado a outro pacote. A versão preservada não foi sobrescrita.");
        var target = Path.Combine(Folder(build.BuildId), expected.FileName);
        var temporary = target + ".copy-" + Guid.NewGuid().ToString("N") + ".nupkg";
        try
        {
            await using (var input = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            { await input.CopyToAsync(output, cancellation).ConfigureAwait(false); output.Flush(true); }
            await VerifyPackageAsync(temporary, expected, build, cancellation).ConfigureAwait(false);
            File.Move(temporary, target, true);
            Write(metadata, new CachedBuild(build, CachedAsset.From(expected), previous?.SavedAt ?? DateTimeOffset.UtcNow,
                previous?.InstalledAt ?? (installed ? DateTimeOffset.UtcNow : null)));
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public async Task<(AppBuildInfo Build, VelopackAsset Asset, string PackagePath)> GetVerifiedAsync(string buildId, CancellationToken cancellation = default)
    {
        var record = Record(buildId);
        if (record.InstalledAt is null) throw new InvalidOperationException("Esta versão ainda não foi executada como instalação neste computador.");
        var path = Path.Combine(Folder(buildId), record.Asset.FileName);
        await VerifyPackageAsync(path, record.Asset.ToAsset(), record.Build, cancellation).ConfigureAwait(false);
        return (record.Build, record.Asset.ToAsset(), path);
    }
    public static async Task VerifyPackageAsync(string path, VelopackAsset expected, AppBuildInfo? build = null, CancellationToken cancellation = default)
    {
        SafeFileName(expected.FileName); RejectLink(path);
        if (expected.PackageId != VelopackInstallation.PackageId || expected.Type != VelopackAssetType.Full || expected.Size <= 0 || expected.Size > 1024L * 1024 * 1024 ||
            !Regex.IsMatch(expected.SHA256 ?? "", "^[0-9a-fA-F]{64}$") || !Regex.IsMatch(expected.SHA1 ?? "", "^[0-9a-fA-F]{40}$")) throw new InvalidDataException("O pacote não tem os hashes completos publicados pelo Velopack.");
        cancellation.ThrowIfCancellationRequested();
        VelopackAsset actual;
        try { actual = await VelopackAsset.FromNupkgGenerateChecksumAsync(path).ConfigureAwait(false); }
        catch (Exception error) when (error is System.Xml.XmlException or FormatException)
        { throw new InvalidDataException("O pacote preservado está danificado. Baixe a versão novamente.", error); }
        cancellation.ThrowIfCancellationRequested();
        if (actual.PackageId != expected.PackageId || actual.Version != expected.Version || actual.Type != expected.Type || actual.Size != expected.Size ||
            !actual.SHA256.Equals(expected.SHA256, StringComparison.OrdinalIgnoreCase) || !actual.SHA1.Equals(expected.SHA1, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("O pacote preservado não corresponde à publicação. Baixe a versão novamente.");
        if (build is not null && await ReadBuildInfoAsync(path, cancellation).ConfigureAwait(false) != build)
            throw new InvalidDataException("A identificação interna do pacote difere da versão selecionada.");
    }
    public static async Task<AppBuildInfo> ReadBuildInfoAsync(string packagePath, CancellationToken cancellation = default)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var entries = archive.Entries.Where(e => e.FullName == "build-info.json" || e.FullName.StartsWith("lib/", StringComparison.Ordinal) && e.FullName.EndsWith("/build-info.json", StringComparison.Ordinal)).ToArray();
        if (entries.Length != 1 || entries[0].Length > 16384) throw new InvalidDataException("Pacote sem identificação única de versão.");
        await using var stream = entries[0].Open(); using var output = new MemoryStream();
        var buffer = new byte[4096]; int read;
        while ((read = await stream.ReadAsync(buffer, cancellation).ConfigureAwait(false)) != 0)
        { if (output.Length + read > 16384) throw new InvalidDataException("Identificação de versão muito grande."); output.Write(buffer, 0, read); }
        var build = JsonSerializer.Deserialize<AppBuildInfo>(output.ToArray(), Json) ?? throw new InvalidDataException("Identificação de versão vazia.");
        Id(build.BuildId); if (!SemanticVersion.TryParse(build.Version, out _) || !Regex.IsMatch(build.Commit ?? "", "^[0-9a-fA-F]{40}$") || build.Channel is not ("testing" or "stable")) throw new InvalidDataException("Identificação de versão inválida.");
        return build;
    }
    public void SnapshotSettings(string buildId)
    {
        EnsureFolder(buildId); var directory = Folder(buildId);
        byte[]? bytes = null;
        if (File.Exists(SettingsPath)) { RejectLink(SettingsPath); bytes = File.ReadAllBytes(SettingsPath); if (bytes.Length > 1024 * 1024) throw new InvalidDataException("Os ajustes excedem o limite de backup."); }
        if (bytes is not null) AtomicBytes(Path.Combine(directory, "settings.snapshot"), bytes);
        Write(Path.Combine(directory, "settings.json"), new SettingsCopy(bytes is null, bytes is null ? null : Convert.ToHexString(SHA256.HashData(bytes))));
    }
    public void QueueSettingsRestore(AppBuildInfo target)
    {
        Record(target.BuildId); Write(PendingPath, new PendingSettingsRestore(target.BuildId, target.Version, target.Commit));
    }
    public void CancelSettingsRestore() { if (File.Exists(PendingPath)) File.Delete(PendingPath); }
    public string? CompleteSettingsRestore(AppBuildInfo running)
    {
        if (!File.Exists(PendingPath)) return null;
        var pending = Read<PendingSettingsRestore>(PendingPath);
        try
        {
            if (pending.BuildId != running.BuildId || pending.Version != running.Version || pending.Commit != running.Commit) return "A reversão não foi aplicada. Seus ajustes atuais foram preservados.";
            var metadata = Path.Combine(Folder(pending.BuildId), "settings.json");
            if (!File.Exists(metadata)) return "Versão anterior iniciada. Não havia um backup de ajustes dessa versão; os ajustes atuais foram mantidos.";
            var snapshot = Read<SettingsCopy>(metadata);
            if (File.Exists(SettingsPath)) { RejectLink(SettingsPath); File.Copy(SettingsPath, SettingsPath + ".before-rollback", true); }
            if (snapshot.Missing) { if (File.Exists(SettingsPath)) File.Delete(SettingsPath); File.Delete(SettingsPath + ".previous"); }
            else
            {
                var source = Path.Combine(Folder(pending.BuildId), "settings.snapshot"); RejectLink(source);
                var bytes = File.ReadAllBytes(source);
                if (bytes.Length > 1024 * 1024 || !Convert.ToHexString(SHA256.HashData(bytes)).Equals(snapshot.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Backup de ajustes danificado; os ajustes atuais foram preservados.");
                AtomicBytes(SettingsPath, bytes); File.Delete(SettingsPath + ".previous");
            }
            return "Versão anterior restaurada com seus ajustes preservados. Os ajustes mais recentes ficaram em uma cópia separada.";
        }
        finally { File.Delete(PendingPath); }
    }
}
