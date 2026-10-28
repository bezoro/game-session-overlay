using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ApolloOverlay;

public interface IUpdateCredentialStore
{
    string? Read();
    void Write(string token);
    void Delete();
}

public sealed record UpdateCandidate(long ReleaseId, string Version, string BuildId, string Commit,
    string Channel, DateTimeOffset PublishedAt, string Notes, long PackageAssetId,
    string PackageName, long Size, string Sha256);
public sealed record UpdateProgress(string Phase, long Bytes = 0, long Total = 0);
public sealed record PreparedUpdate(string Directory, AppBuildInfo Build);

public interface IUpdateSource : IDisposable
{
    bool HasCredential { get; }
    Task ConnectAsync(string token, CancellationToken cancellation);
    void Disconnect();
    Task<IReadOnlyList<UpdateCandidate>> CheckAsync(string channel, CancellationToken cancellation);
    Task<PreparedUpdate> StageAsync(UpdateCandidate candidate, IProgress<UpdateProgress>? progress, CancellationToken cancellation);
    void Discard(PreparedUpdate prepared) { }
}

/// <summary>Downloads only release assets from the fixed project, with authentication confined to GitHub's API.</summary>
public sealed class GitHubUpdateSource : IUpdateSource
{
    internal const string Repository = "bezoro/apollo-tv-overlay";
    private const string Api = "https://api.github.com/repos/" + Repository;
    private const long MaxPackage = 256L * 1024 * 1024, MaxExpanded = 1024L * 1024 * 1024;
    private readonly HttpClient client;
    private readonly IUpdateCredentialStore credentials;
    private readonly string cacheRoot;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    public bool HasCredential { get { try { return !string.IsNullOrWhiteSpace(credentials.Read()); } catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { return false; } } }

    public GitHubUpdateSource(HttpMessageHandler? handler = null, IUpdateCredentialStore? credentials = null, string? cacheRoot = null)
    {
        client = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GameSessionOverlay/0.2");
        this.credentials = credentials ?? new UpdateCredentialStore();
        this.cacheRoot = cacheRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bezoro", "GameSessionOverlay", "update-cache");
    }

    public async Task ConnectAsync(string token, CancellationToken cancellation)
    {
        token = token.Trim();
        if (!Regex.IsMatch(token, "^[A-Za-z0-9_]{20,255}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("Informe um token de acesso do GitHub com leitura de Contents neste repositório.");
        using var response = await SendApiAsync(Api, false, token, cancellation).ConfigureAwait(false);
        using var json = JsonDocument.Parse(await ReadBoundedAsync(response, 1024 * 1024, cancellation).ConfigureAwait(false));
        if (!json.RootElement.TryGetProperty("full_name", out var name) || !string.Equals(name.GetString(), Repository, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("O GitHub não confirmou acesso ao repositório da overlay.");
        credentials.Write(token);
    }

    public void Disconnect() => credentials.Delete();

    public async Task<IReadOnlyList<UpdateCandidate>> CheckAsync(string channel, CancellationToken cancellation)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(75));
        try { return await CheckCoreAsync(channel, deadline.Token).ConfigureAwait(false); }
        catch(OperationCanceledException e) when(!cancellation.IsCancellationRequested) { throw new TimeoutException("A consulta de versões demorou demais. Tente novamente.",e); }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new InvalidDataException("A publicação de atualização está incompleta ou tem um formato incompatível. A versão instalada foi preservada.", e); }
    }

    private async Task<IReadOnlyList<UpdateCandidate>> CheckCoreAsync(string channel, CancellationToken cancellation)
    {
        if (channel is not ("stable" or "testing")) throw new ArgumentException("Canal inválido.", nameof(channel));
        var token = credentials.Read();
        using var response = await SendApiAsync(Api + "/releases?per_page=100", false, token, cancellation).ConfigureAwait(false);
        using var json = JsonDocument.Parse(await ReadBoundedAsync(response, 4 * 1024 * 1024, cancellation).ConfigureAwait(false));
        if (json.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Lista de versões inválida.");
        var releases = json.RootElement.EnumerateArray().Where(r => !r.GetProperty("draft").GetBoolean() &&
            r.GetProperty("prerelease").GetBoolean() == (channel == "testing") &&
            r.GetProperty("assets").EnumerateArray().Any(a => a.GetProperty("name").GetString() == "overlay-release.json"))
            .OrderByDescending(r => r.GetProperty("published_at").GetDateTimeOffset()).Take(12).Select(r => r.Clone()).ToArray();
        using var limit = new SemaphoreSlim(3);
        var tasks = releases.Select(async release =>
        {
            await limit.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                var assets = release.GetProperty("assets").EnumerateArray().Select(a => a.Clone()).ToArray();
                var manifestAsset = assets.Single(a => a.GetProperty("name").GetString() == "overlay-release.json");
                using var manifestResponse = await DownloadAssetAsync(manifestAsset.GetProperty("id").GetInt64(), token, cancellation).ConfigureAwait(false);
                var manifestBytes = await ReadBoundedAsync(manifestResponse, 1024 * 1024, cancellation).ConfigureAwait(false);
                VerifyGitHubDigest(manifestAsset, manifestBytes);
                var manifest = JsonSerializer.Deserialize<ReleaseManifest>(manifestBytes, Json) ?? throw new InvalidDataException("Manifesto vazio.");
                Validate(manifest, channel);
                var package = assets.SingleOrDefault(a => a.GetProperty("name").GetString() == manifest.Package.Name);
                if (package.ValueKind == JsonValueKind.Undefined || package.GetProperty("size").GetInt64() != manifest.Package.Size)
                    throw new InvalidDataException("O pacote publicado não corresponde ao manifesto da versão.");
                if (!release.TryGetProperty("tag_name", out var tag) || tag.GetString() != "v" + manifest.Version)
                    throw new InvalidDataException("A identificação da publicação não corresponde à versão do pacote.");
                var notes = release.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
                return new UpdateCandidate(release.GetProperty("id").GetInt64(), manifest.Version, manifest.BuildId, manifest.Commit,
                    manifest.Channel, manifest.PublishedAt, notes.Length > 12000 ? notes[..12000] : notes,
                    package.GetProperty("id").GetInt64(), manifest.Package.Name, manifest.Package.Size, manifest.Package.Sha256.ToLowerInvariant());
            }
            finally { limit.Release(); }
        });
        return (await Task.WhenAll(tasks).ConfigureAwait(false)).OrderByDescending(r => r.PublishedAt).ToArray();
    }

    public async Task<PreparedUpdate> StageAsync(UpdateCandidate candidate, IProgress<UpdateProgress>? progress, CancellationToken cancellation)
    {
        Validate(new(1, candidate.Version, candidate.BuildId, candidate.Commit, candidate.Channel, candidate.PublishedAt,
            new(candidate.PackageName, candidate.Size, candidate.Sha256)), candidate.Channel);
        var requestedCancellation=cancellation;
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));cancellation=deadline.Token;
        var workspace = Path.Combine(cacheRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            var zip = Path.Combine(workspace, "package.zip");
            using (var response = await DownloadAssetAsync(candidate.PackageAssetId, credentials.Read(), cancellation).ConfigureAwait(false))
            {
                if (response.Content.Headers.ContentLength is {} length && length != candidate.Size)
                    throw new InvalidDataException("O tamanho do download não corresponde à versão publicada.");
                await using var input = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
                await using var output = new FileStream(zip, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[65536]; long total = 0; long nextProgress=0; int read;
                while ((read = await input.ReadAsync(buffer, cancellation).ConfigureAwait(false)) != 0)
                {
                    total += read;
                    if (total > candidate.Size || total > MaxPackage) throw new InvalidDataException("Download excedeu o tamanho permitido.");
                    hash.AppendData(buffer, 0, read); await output.WriteAsync(buffer.AsMemory(0, read), cancellation).ConfigureAwait(false);
                    var now=Environment.TickCount64; if(now>=nextProgress){progress?.Report(new("Baixando atualização", total, candidate.Size));nextProgress=now+100;}
                }
                if (total != candidate.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(candidate.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("A verificação SHA-256 falhou. A versão atual foi preservada.");
            }
            progress?.Report(new("Verificando arquivos"));
            var payload = Path.Combine(workspace, "payload");
            await ExtractAsync(zip, payload, cancellation).ConfigureAwait(false);
            var buildPath = Path.Combine(payload, "build-info.json");
            if (!File.Exists(buildPath)) throw new InvalidDataException("O pacote não contém a identificação da versão.");
            if (new FileInfo(buildPath).Length > 16384) throw new InvalidDataException("Identificação de versão inválida.");
            var buildBytes = await File.ReadAllBytesAsync(buildPath, cancellation).ConfigureAwait(false);
            if (buildBytes.Length > 16384) throw new InvalidDataException("Identificação de versão inválida.");
            var build = JsonSerializer.Deserialize<AppBuildInfo>(buildBytes, Json);
            if (build is null || build.BuildId != candidate.BuildId || build.Version != candidate.Version || build.Commit != candidate.Commit || build.Channel != candidate.Channel || !File.Exists(Path.Combine(payload, "ApolloOverlay.exe")))
                throw new InvalidDataException("A identificação interna do pacote difere da publicação. Nada foi instalado.");
            File.Delete(zip);
            cancellation.ThrowIfCancellationRequested();
            return new(payload, build);
        }
        catch(OperationCanceledException e) when(!requestedCancellation.IsCancellationRequested) { TryDelete(workspace); throw new TimeoutException("O download demorou demais. A versão instalada foi preservada.",e); }
        catch { TryDelete(workspace); throw; }
    }

    private async Task<HttpResponseMessage> SendApiAsync(string uri, bool asset, string? token, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(asset ? "application/octet-stream" : "application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (!string.IsNullOrEmpty(token)) request.Headers.Authorization = new("Bearer", token);
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            response.Dispose(); throw new UnauthorizedAccessException("Conecte uma conta do GitHub com acesso de leitura ao repositório privado da overlay.");
        }
        if (asset && (int)response.StatusCode is 301 or 302 or 303 or 307 or 308) return response;
        if (!response.IsSuccessStatusCode) { var code = (int)response.StatusCode; response.Dispose(); throw new HttpRequestException("O GitHub não respondeu à consulta de versões. Código " + code + "."); }
        return response;
    }

    private async Task<HttpResponseMessage> DownloadAssetAsync(long id, string? token, CancellationToken cancellation)
    {
        if (id <= 0) throw new InvalidDataException("Identificador de download inválido.");
        var response = await SendApiAsync(Api + "/releases/assets/" + id, true, token, cancellation).ConfigureAwait(false);
        for (var redirect = 0; (int)response.StatusCode is 301 or 302 or 303 or 307 or 308; redirect++)
        {
            var location = response.Headers.Location;
            response.Dispose();
            if (redirect >= 3 || location is null || !location.IsAbsoluteUri || location.Scheme != Uri.UriSchemeHttps || !location.IsDefaultPort ||
                location.UserInfo.Length != 0 || location.Host is not ("release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                throw new InvalidDataException("O GitHub indicou um endereço de download não permitido.");
            // A signed asset URL needs no account credential. Never forward the API token.
            using var request = new HttpRequestMessage(HttpMethod.Get, location);
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        }
        if (!response.IsSuccessStatusCode) { response.Dispose(); throw new HttpRequestException("O download da versão não está disponível. Tente novamente."); }
        return response;
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int maximum, CancellationToken cancellation)
    {
        if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("Resposta de atualização muito grande.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        using var output = new MemoryStream(); var buffer = new byte[16384]; int read;
        while ((read = await input.ReadAsync(buffer, cancellation).ConfigureAwait(false)) != 0)
        { if (output.Length + read > maximum) throw new InvalidDataException("Resposta de atualização muito grande."); output.Write(buffer, 0, read); }
        return output.ToArray();
    }

    private static void Validate(ReleaseManifest manifest, string channel)
    {
        if (manifest.SchemaVersion != 1 || manifest.Channel != channel || channel is not ("testing" or "stable") ||
            !Regex.IsMatch(manifest.BuildId ?? "", "^[A-Za-z0-9][A-Za-z0-9._-]{0,100}$") || (manifest.BuildId ?? "").Contains("..", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(manifest.Version) || manifest.Version.Length > 128 || manifest.Version.Any(char.IsControl) ||
            !Regex.IsMatch(manifest.Commit ?? "", "^[a-fA-F0-9]{40}$") || manifest.PublishedAt == default ||
            manifest.Package is null || manifest.Package.Size <= 0 || manifest.Package.Size > MaxPackage ||
            !Regex.IsMatch(manifest.Package.Sha256 ?? "", "^[a-fA-F0-9]{64}$") ||
            string.IsNullOrWhiteSpace(manifest.Package.Name) || manifest.Package.Name.Length > 180 ||
            manifest.Package.Name.IndexOfAny(['/', '\\', ':']) >= 0 || !manifest.Package.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Manifesto de atualização inválido ou incompatível. A versão atual foi preservada.");
    }

    private static void VerifyGitHubDigest(JsonElement asset, byte[] bytes)
    {
        if (!asset.TryGetProperty("digest", out var digest) || digest.ValueKind == JsonValueKind.Null) return;
        var expected = digest.GetString();
        if (expected is null || !expected.StartsWith("sha256:", StringComparison.Ordinal) || !Convert.ToHexString(SHA256.HashData(bytes)).Equals(expected[7..], StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("O manifesto baixado difere do arquivo publicado no GitHub.");
    }

    private static async Task ExtractAsync(string zip, string destination, CancellationToken cancellation)
    {
        Directory.CreateDirectory(destination);
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(zip);
        if (archive.Entries.Count > 12000) throw new InvalidDataException("Pacote com arquivos demais.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            cancellation.ThrowIfCancellationRequested();
            var relative = entry.FullName.Replace('\\', '/');
            var parts = relative.TrimEnd('/').Split('/');
            if (string.IsNullOrEmpty(relative) || relative.StartsWith('/') || parts.Any(p => p is "" or "." or ".." || p.EndsWith('.') || p.EndsWith(' ') || p.Any(c => c < 32 || c is ':' or '*' or '?' or '"' or '<' or '>' or '|') ||
                Regex.IsMatch(p, "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) ||
                !seen.Add(relative.TrimEnd('/')) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || (entry.ExternalAttributes & 0x400) != 0)
                throw new InvalidDataException("O pacote contém um caminho não permitido.");
            expanded = checked(expanded + entry.Length);
            if (expanded > MaxExpanded || entry.Length > 512L * 1024 * 1024) throw new InvalidDataException("Pacote expandido muito grande.");
            var target = Path.GetFullPath(Path.Combine(destination, relative));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("O pacote tenta escrever fora da pasta de atualização.");
            if (relative.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = entry.Open();
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
            var buffer = new byte[65536]; long written = 0; int read;
            while ((read = await input.ReadAsync(buffer, cancellation).ConfigureAwait(false)) != 0)
            {
                written += read;
                if (written > entry.Length || written > MaxExpanded) throw new InvalidDataException("Arquivo expandido excedeu o tamanho declarado.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellation).ConfigureAwait(false);
            }
            if (output.Length != entry.Length) throw new InvalidDataException("Arquivo extraído incompleto.");
        }
    }

    internal static void TryDelete(string path)
    { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
    public void Discard(PreparedUpdate prepared)
    {
        var payload = Path.GetFullPath(prepared.Directory);
        var workspace = Path.GetDirectoryName(payload);
        if (workspace is null || Path.GetFileName(payload) != "payload" || !Guid.TryParseExact(Path.GetFileName(workspace), "N", out _) ||
            !string.Equals(Path.GetDirectoryName(workspace), Path.GetFullPath(cacheRoot).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return;
        TryDelete(workspace);
    }
    public void Dispose() => client.Dispose();
    private sealed record PackageManifest(string Name, long Size, string Sha256);
    private sealed record ReleaseManifest(int SchemaVersion, string Version, string BuildId, string Commit, string Channel, DateTimeOffset PublishedAt, PackageManifest Package);
}
