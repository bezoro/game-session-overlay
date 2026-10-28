using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;
using SdkUpdateSource = Velopack.Sources.IUpdateSource;

namespace ApolloOverlay;

public sealed record UpdateCandidate(long ReleaseId, string Version, string BuildId, string Commit,
    string Channel, DateTimeOffset PublishedAt, string Notes, long PackageAssetId,
    string PackageName, long Size, string Sha256, VelopackAsset? Asset = null);
public sealed record UpdateProgress(string Phase, long Bytes = 0, long Total = 0);
public sealed record PreparedUpdate(string PackagePath, AppBuildInfo Build, PreparedVeloUpdate? Native = null);

public interface IUpdateSource : IDisposable
{
    Task<IReadOnlyList<UpdateCandidate>> CheckAsync(string channel, CancellationToken cancellation);
    Task<PreparedUpdate> StageAsync(UpdateCandidate candidate, IProgress<UpdateProgress>? progress, CancellationToken cancellation);
    void Discard(PreparedUpdate prepared) { }
}

/// <summary>Application release policy over the official Velopack source and update manager.</summary>
public sealed class GitHubUpdateSource : IUpdateSource
{
    public const string Repository = "bezoro/game-session-overlay";
    public const string RepositoryUrl = "https://github.com/" + Repository;
    private readonly Func<string, SdkUpdateSource> createSource;
    private int disposed;

    public GitHubUpdateSource(Func<string, SdkUpdateSource>? sourceFactory = null)
        => createSource = sourceFactory ?? (channel => new ProjectGithubSource(channel));

    public async Task<IReadOnlyList<UpdateCandidate>> CheckAsync(string channel, CancellationToken cancellation)
    {
        ThrowIfDisposed(); ValidateChannel(channel); cancellation.ThrowIfCancellationRequested();
        var source = createSource(channel);
        var request = source.GetReleaseFeed(NullVelopackLogger.Instance, VelopackInstallation.PackageId, channel);
        VelopackAssetFeed feed;
        try { feed = await request.WaitAsync(TimeSpan.FromSeconds(75), cancellation).ConfigureAwait(false); }
        catch { ObserveLateFault(request); throw; }
        ThrowIfDisposed(); cancellation.ThrowIfCancellationRequested();
        if (feed?.Assets is null) throw new InvalidDataException("O catálogo de versões do Velopack é inválido.");
        var result = new Dictionary<string, UpdateCandidate>(StringComparer.Ordinal);
        foreach (var asset in feed.Assets.Where(a => a is not null && a.PackageId == VelopackInstallation.PackageId && a.Type == VelopackAssetType.Full))
        {
            var candidate = Describe(asset, channel, ProjectGithubSource.PublishedAt(asset));
            if (result.TryGetValue(candidate.BuildId, out var previous))
            {
                if (previous.Version != candidate.Version || previous.Commit != candidate.Commit || previous.PackageName != candidate.PackageName ||
                    previous.Size != candidate.Size || !previous.Sha256.Equals(candidate.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("O mesmo identificador de versão foi publicado com pacotes diferentes.");
                continue;
            }
            result.Add(candidate.BuildId, candidate);
        }
        return result.Values.OrderByDescending(c => c.Asset!.Version).ThenByDescending(c => c.PublishedAt).ToArray();
    }

    public async Task<PreparedUpdate> StageAsync(UpdateCandidate candidate, IProgress<UpdateProgress>? progress, CancellationToken cancellation)
    {
        ThrowIfDisposed(); cancellation.ThrowIfCancellationRequested();
        if (candidate.Asset is not {} asset) throw new InvalidDataException("Consulte novamente as versões antes de baixar a atualização.");
        var verified = Describe(asset, candidate.Channel, candidate.PublishedAt);
        if (candidate.Version != verified.Version || candidate.BuildId != verified.BuildId || candidate.Commit != verified.Commit ||
            candidate.PackageName != verified.PackageName || candidate.Size != verified.Size || !candidate.Sha256.Equals(verified.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A versão selecionada mudou. Consulte novamente o catálogo.");
        if (!VelopackInstallation.IsInstalled) throw new InvalidOperationException("Instale a overlay pelo Setup para aplicar atualizações.");

        // Velopack cleans its own cache even after failed/cancelled downloads.
        // Retain the running version first, outside that disposable cache.
        await VelopackInstallation.RecordCurrentAsync(cancellation).ConfigureAwait(false);
        var build = new AppBuildInfo(candidate.Version, candidate.BuildId, candidate.Commit, candidate.Channel);
        var manager = new UpdateManager(createSource(candidate.Channel), new UpdateOptions
        {
            ExplicitChannel = candidate.Channel,
            AllowVersionDowngrade = true,
            MaximumDeltasBeforeFallback = -1
        });
        var path = VelopackInstallation.LocalPackagePath(asset);
        if (File.Exists(path))
        {
            try { await LocalUpdateHistory.VerifyPackageAsync(path, asset, build, cancellation).ConfigureAwait(false); }
            catch (InvalidDataException) { File.Delete(path); }
        }
        ThrowIfDisposed(); cancellation.ThrowIfCancellationRequested();
        var downgrade = manager.CurrentVersion is {} current && asset.Version <= current;
        await manager.DownloadUpdatesAsync(new UpdateInfo(asset, downgrade), percent =>
            progress?.Report(new("Baixando atualização", Math.Clamp(percent, 0, 100), 100)), cancellation).ConfigureAwait(false);
        ThrowIfDisposed(); cancellation.ThrowIfCancellationRequested();
        progress?.Report(new("Verificando pacote"));
        await LocalUpdateHistory.VerifyPackageAsync(path, asset, build, cancellation).ConfigureAwait(false);
        await VelopackInstallation.ArchiveVerifiedAsync(path, asset, build, cancellation).ConfigureAwait(false);
        ThrowIfDisposed(); cancellation.ThrowIfCancellationRequested();
        return new(path, build, new(asset, manager, path, downgrade));
    }

    private static UpdateCandidate Describe(VelopackAsset asset, string channel, DateTimeOffset published)
    {
        ValidateChannel(channel);
        if (asset.PackageId != VelopackInstallation.PackageId || asset.Type != VelopackAssetType.Full || asset.Version is null ||
            asset.Size <= 0 || asset.Size > 1024L * 1024 * 1024 ||
            !Regex.IsMatch(asset.SHA256 ?? "", "^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant) ||
            !Regex.IsMatch(asset.SHA1 ?? "", "^[a-fA-F0-9]{40}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("O pacote publicado não contém a identidade e os hashes completos do Velopack.");
        LocalUpdateHistory.SafeFileName(asset.FileName);
        var notes = asset.NotesMarkdown ?? "";
        if (notes.Length > 65536) throw new InvalidDataException("As notas de versão excedem o limite permitido.");
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in notes.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            foreach (var field in new[] { "Version", "Build", "Commit", "Channel" })
            {
                if (!line.StartsWith(field + ":", StringComparison.Ordinal)) continue;
                if (!fields.TryAdd(field, line[(field.Length + 1)..].Trim()))
                    throw new InvalidDataException("As notas de versão contêm identidades duplicadas.");
            }
        }
        if (!fields.TryGetValue("Version", out var version) || !fields.TryGetValue("Build", out var buildId) ||
            !fields.TryGetValue("Commit", out var commit) || !fields.TryGetValue("Channel", out var declaredChannel) ||
            !SemanticVersion.TryParse(version, out var parsed) || parsed != asset.Version ||
            !Regex.IsMatch(buildId, "^[A-Za-z0-9][A-Za-z0-9._-]{0,95}$", RegexOptions.CultureInvariant) || buildId.EndsWith('.') || buildId.Contains("..", StringComparison.Ordinal) ||
            Regex.IsMatch(buildId.Split('.')[0], "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
            !Regex.IsMatch(commit, "^[a-fA-F0-9]{40}$", RegexOptions.CultureInvariant) || declaredChannel != channel)
            throw new InvalidDataException("A identificação da versão não corresponde ao pacote ou ao canal selecionado.");
        return new(0, version, buildId, commit, channel, published, notes, 0, asset.FileName, asset.Size, (asset.SHA256 ?? "").ToLowerInvariant(), asset);
    }

    private static void ValidateChannel(string channel)
    { if (channel is not ("testing" or "stable")) throw new ArgumentException("Canal de atualização inválido.", nameof(channel)); }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
    private static void ObserveLateFault(Task task)
        => _ = task.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    public void Dispose() => Interlocked.Exchange(ref disposed, 1);

    /// <summary>Keep SDK asset/download behavior; only expand/filter the repository release listing.</summary>
    private sealed class ProjectGithubSource(string channel) : GithubSource(RepositoryUrl, null, channel == "testing", new BoundedMetadataDownloader())
    {
        protected override async Task<GithubRelease[]> GetReleases(bool includePrereleases)
        {
            const string endpoint = "https://api.github.com/repositories/1430327913/releases?per_page=100";
            var text = await Downloader.DownloadString(endpoint, GetRequestHeaders("application/vnd.github+json"), .75).ConfigureAwait(false);
            if (text.Length > 8 * 1024 * 1024) throw new InvalidDataException("A lista de publicações excede o limite permitido.");
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("A lista de publicações é inválida.");
            var matching = new List<(DateTimeOffset Date, GithubRelease Release)>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.GetProperty("draft").GetBoolean() || item.GetProperty("prerelease").GetBoolean() != (channel == "testing")) continue;
                if (!item.GetProperty("assets").EnumerateArray().Any(a => a.GetProperty("name").GetString() == "releases." + channel + ".json")) continue;
                var release = JsonSerializer.Deserialize<GithubRelease>(item.GetRawText()) ?? throw new InvalidDataException("Publicação inválida.");
                matching.Add((item.GetProperty("published_at").GetDateTimeOffset(), release));
            }
            return matching.OrderByDescending(r => r.Date).Take(12).Select(r => r.Release).ToArray();
        }
        internal static DateTimeOffset PublishedAt(VelopackAsset asset)
            => asset is GitBaseAsset wrapped && wrapped.Release.PublishedAt is {} date ? new DateTimeOffset(date.ToUniversalTime()) : DateTimeOffset.UnixEpoch;
    }

    private sealed class BoundedMetadataDownloader : HttpClientFileDownloader
    {
        protected override HttpClient CreateHttpClient(IDictionary<string, string>? headers, double timeout)
            => base.CreateHttpClient(headers, Math.Min(timeout, .75));
    }
}
