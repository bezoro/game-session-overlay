using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ApolloOverlay;

internal static class UpdateSourceTests
{
    private const string Repository = "bezoro/apollo-tv-overlay";
    private const string Api = "https://api.github.com/repos/" + Repository;
    private const string Token = "fixture_existing_token_123456789";
    private const string Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string BuildId = "ci-42-a1-aaaaaaa";
    private const string Version = "0.2.0-preview.42.1+aaaaaaa";
    private static readonly DateTimeOffset Published = DateTimeOffset.Parse("2026-10-28T12:00:00Z");

    internal static void Run(Action<string, Action> test)
    {
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.NotFound })
        {
            var code = status;
            test("updates private repository refusal is explicit " + (int)code, () =>
            {
                using var fixture = new Fixture((_, _) => new(code));
                using var source = fixture.Source();
                var error = Throws<UnauthorizedAccessException>(() => Done(() => source.CheckAsync("testing", CancellationToken.None)));
                Require(!error.ToString().Contains(Token, StringComparison.Ordinal), "Account secret leaked in error");
                Require(fixture.Requests.Single().Authorization == "Bearer " + Token, "Private request lacked account credential");
                fixture.Preserved();
            });
        }
        test("updates connection validates repository before replacing a working credential", () =>
        {
            const string replacement = "fixture_replacement_token_123456789";
            using var fixture = new Fixture((_, _) => new(HttpStatusCode.Forbidden));
            using var source = fixture.Source();
            Throws<UnauthorizedAccessException>(() => Done(() => source.ConnectAsync(replacement, CancellationToken.None)));
            Require(fixture.Credentials.Value == Token && fixture.Credentials.Writes == 0, "Failed connection replaced working credential");
            Require(fixture.Requests.Single().Authorization == "Bearer " + replacement, "Candidate credential was not validated");
        });
        test("updates connection rejects a response for a different repository", () =>
        {
            using var fixture = new Fixture((_, _) => Json(new { full_name = "other/repository" }));
            using var source = fixture.Source();
            Throws<InvalidDataException>(() => Done(() => source.ConnectAsync("fixture_replacement_token_123456789", CancellationToken.None)));
            Require(fixture.Credentials.Value == Token && fixture.Credentials.Writes == 0, "Foreign repository response accepted");
        });
        test("updates successful connection stores the validated credential and disconnect removes it", () =>
        {
            const string replacement = "fixture_replacement_token_123456789";
            using var fixture = new Fixture((_, _) => Json(new { full_name = Repository }));
            using var source = fixture.Source();
            Done(() => source.ConnectAsync(replacement, CancellationToken.None));
            Require(source.HasCredential && fixture.Credentials.Value == replacement && fixture.Credentials.Writes == 1, "Validated credential was not saved");
            source.Disconnect();
            Require(!source.HasCredential && fixture.Credentials.Deletes == 1, "Disconnect retained credential");
        });
        test("updates invalid credential input never enters an HTTP request", () =>
        {
            using var fixture = new Fixture(); using var source = fixture.Source();
            Throws<InvalidDataException>(() => Done(() => source.ConnectAsync("secret\r\nInjected: header", CancellationToken.None)));
            Require(fixture.Requests.IsEmpty && fixture.Credentials.Writes == 0, "Invalid credential reached network or storage");
        });
        test("updates catalog filters stable draft and legacy releases before downloading manifests", () =>
        {
            using var fixture = new Fixture(); fixture.ReleaseVersion = "Game Session Overlay " + Version;
            fixture.ReleaseList = [fixture.Release(), fixture.Release(id: 2, prerelease: false), fixture.Release(id: 3, draft: true), fixture.Release(id: 4, legacy: true)];
            using var source = fixture.Source();
            var values = Done(() => source.CheckAsync("testing", CancellationToken.None));
            Require(values.Count == 1 && values[0].BuildId == BuildId && values[0].Version == Version, "Wrong release channel or identity");
            Require(fixture.Requests.Count(r => r.Uri.AbsolutePath.EndsWith("/assets/101", StringComparison.Ordinal)) == 1, "Filtered release manifest still downloaded");
            Require(fixture.Requests.All(r => r.Uri.Host == "api.github.com" && r.Authorization == "Bearer " + Token), "Catalog request escaped API or lost auth");
        });
        test("updates catalog rejects a manifest with a different GitHub asset digest", () =>
        {
            using var fixture = new Fixture(); fixture.ManifestDigest = "sha256:" + new string('0', 64);
            using var source = fixture.Source();
            Throws<InvalidDataException>(() => Done(() => source.CheckAsync("testing", CancellationToken.None)));
            Require(fixture.Requests.All(r => !r.Uri.AbsolutePath.EndsWith("/assets/201", StringComparison.Ordinal)), "Package was downloaded after bad manifest");
            fixture.Preserved();
        });
        test("updates catalog rejects a release tag that does not identify its package", () =>
        {
            using var fixture = new Fixture(); fixture.ReleaseTag = "v0.9.9+bbbbbbb";
            using var source = fixture.Source();
            Throws<InvalidDataException>(() => Done(() => source.CheckAsync("testing", CancellationToken.None)));
            fixture.Preserved();
        });
        test("updates catalog rejects declared package size inconsistent with its asset", () =>
        {
            using var fixture = new Fixture(); fixture.PackageAssetSize = fixture.Package.Length + 1;
            using var source = fixture.Source();
            Throws<InvalidDataException>(() => Done(() => source.CheckAsync("testing", CancellationToken.None)));
            fixture.Preserved();
        });
        test("updates release channel must also match its downloaded manifest", () =>
        {
            using var fixture = new Fixture(); fixture.ReleaseList = [fixture.Release(prerelease: false)];
            using var source = fixture.Source();
            Throws<InvalidDataException>(() => Done(() => source.CheckAsync("stable", CancellationToken.None)));
            fixture.Preserved();
        });
        test("updates server failure is not reported as an empty successful catalog", () =>
        {
            using var fixture = new Fixture((_, _) => new(HttpStatusCode.ServiceUnavailable));
            using var source = fixture.Source();
            Throws<HttpRequestException>(() => Done(() => source.CheckAsync("testing", CancellationToken.None)));
        });
        test("updates signed asset redirect never receives the repository credential", () =>
        {
            using var fixture = new Fixture();
            fixture.Route = (request, _) => request.RequestUri!.Host == "api.github.com"
                ? Redirect("https://release-assets.githubusercontent.com/fixture/package.zip?signature=fixture")
                : Bytes(fixture.Package);
            using var source = fixture.Source();
            var staged = Done(() => source.StageAsync(fixture.Candidate(), null, CancellationToken.None));
            Require(File.Exists(Path.Combine(staged.Directory, "ApolloOverlay.exe")), "Valid payload was not prepared");
            var calls = fixture.Requests.ToArray();
            Require(calls.Length == 2 && calls[0].Authorization == "Bearer " + Token && calls[1].Authorization is null, "Credential was forwarded to CDN");
            Require(calls.All(r => !r.Uri.AbsoluteUri.Contains(Token, StringComparison.Ordinal)), "Credential entered a URL");
            fixture.Preserved();
        });
        foreach (var location in new[] { "http://release-assets.githubusercontent.com/file.zip", "https://example.invalid/file.zip", "https://release-assets.githubusercontent.com:444/file.zip", "https://user:pass@release-assets.githubusercontent.com/file.zip", "/relative.zip" })
        {
            var destination = location;
            test("updates refuses unsafe redirect " + destination, () =>
            {
                using var fixture = new Fixture((_, _) => Redirect(destination));
                using var source = fixture.Source();
                Throws<InvalidDataException>(() => Done(() => source.StageAsync(fixture.Candidate(), null, CancellationToken.None)));
                Require(fixture.Requests.Count == 1, "Unsafe destination was contacted");
                fixture.CleanCache(); fixture.Preserved();
            });
        }
        test("updates redirect loops are bounded and all later requests remain credential-free", () =>
        {
            using var fixture = new Fixture((_, _) => Redirect("https://objects.githubusercontent.com/fixture/package.zip"));
            using var source = fixture.Source();
            Throws<InvalidDataException>(() => Done(() => source.StageAsync(fixture.Candidate(), null, CancellationToken.None)));
            var requests = fixture.Requests.ToArray();
            Require(requests.Length == 4 && requests.Skip(1).All(r => r.Authorization is null), "Redirect limit or credential boundary failed");
            fixture.CleanCache(); fixture.Preserved();
        });
        test("updates package checksum mismatch removes only its staging workspace", () =>
        {
            using var fixture = new Fixture(); using var source = fixture.Source();
            Throws<InvalidDataException>(() => Done(() => source.StageAsync(fixture.Candidate() with { Sha256 = new string('0', 64) }, null, CancellationToken.None)));
            fixture.CleanCache(); fixture.Preserved();
        });
        test("updates rejects HTTP content length inconsistent with the manifest", () =>
        {
            using var fixture = new Fixture();
            fixture.Route = (_, _) => { var response = Bytes(fixture.Package); response.Content.Headers.ContentLength = fixture.Package.Length + 1; return response; };
            using var source = fixture.Source();
            Throws<InvalidDataException>(() => Done(() => source.StageAsync(fixture.Candidate(), null, CancellationToken.None)));
            fixture.CleanCache(); fixture.Preserved();
        });
        test("updates oversized package declaration is refused before download", () =>
        {
            using var fixture = new Fixture(); using var source = fixture.Source();
            Throws<InvalidDataException>(() => Done(() => source.StageAsync(fixture.Candidate() with { Size = 256L * 1024 * 1024 + 1 }, null, CancellationToken.None)));
            Require(fixture.Requests.IsEmpty, "Oversized package reached the network");
            fixture.CleanCache(); fixture.Preserved();
        });
        foreach (var relative in new[] { "../outside.txt", "nested/../../outside.txt", "..\\outside.txt", "/absolute.txt", "C:/outside.txt", "nested/file:stream", "nested/CON.txt", "nested/COM1", "nested/trailing.", "nested/trailing " })
        {
            var name = relative;
            test("updates refuses noncanonical package path " + name, () =>
            {
                using var fixture = new Fixture(); fixture.Package = Package(extra: [(name, "fixture", 0)]);
                using var source = fixture.Source();
                Throws<InvalidDataException>(() => Done(() => source.StageAsync(fixture.Candidate(), null, CancellationToken.None)));
                fixture.CleanCache(); fixture.Preserved();
                Require(!Directory.GetFiles(fixture.Root, "outside.txt", SearchOption.AllDirectories).Any(), "Archive wrote outside staging");
            });
        }
        test("updates rejects case-insensitive duplicate paths before trusting a package", () =>
        {
            using var fixture = new Fixture(); fixture.Package = Package(extra: [("Assets/Icon.txt", "first", 0), ("assets/icon.txt", "second", 0)]);
            using var source = fixture.Source();
            Throws<InvalidDataException>(() => Done(() => source.StageAsync(fixture.Candidate(), null, CancellationToken.None)));
            fixture.CleanCache(); fixture.Preserved();
        });
        foreach (var attributes in new[] { unchecked((int)0xA1FF0000), 0x400 })
        {
            var flags = attributes;
            test("updates refuses link or reparse archive attributes " + flags, () =>
            {
                using var fixture = new Fixture(); fixture.Package = Package(extra: [("linked-file", "outside", flags)]);
                using var source = fixture.Source();
                Throws<InvalidDataException>(() => Done(() => source.StageAsync(fixture.Candidate(), null, CancellationToken.None)));
                fixture.CleanCache(); fixture.Preserved();
            });
        }
        test("updates rejects package whose internal identity differs from the release", () =>
        {
            using var fixture = new Fixture(); fixture.Package = Package(buildVersion: "0.3.0+bbbbbbb");
            using var source = fixture.Source();
            Throws<InvalidDataException>(() => Done(() => source.StageAsync(fixture.Candidate(), null, CancellationToken.None)));
            fixture.CleanCache(); fixture.Preserved();
        });
        test("updates rejects a payload without the application executable", () =>
        {
            using var fixture = new Fixture(); fixture.Package = Package(executable: false);
            using var source = fixture.Source();
            Throws<InvalidDataException>(() => Done(() => source.StageAsync(fixture.Candidate(), null, CancellationToken.None)));
            fixture.CleanCache(); fixture.Preserved();
        });
        test("updates bounds the build identity file before trusting its contents", () =>
        {
            using var fixture = new Fixture(); fixture.Package = Package(identityPadding: 20000);
            using var source = fixture.Source();
            Throws<InvalidDataException>(() => Done(() => source.StageAsync(fixture.Candidate(), null, CancellationToken.None)));
            fixture.CleanCache(); fixture.Preserved();
        });
        test("updates forged ZIP sizes cannot expand output beyond their declared size", () =>
        {
            using var fixture = new Fixture();
            fixture.Package = ForgeLength(Package(extra: [("forged.txt", new string('x', 100000), 0)]), "forged.txt", 1);
            using var source = fixture.Source();
            PreparedUpdate? staged = null;
            try { staged = Done(() => source.StageAsync(fixture.Candidate(), null, CancellationToken.None)); }
            catch (InvalidDataException) { fixture.CleanCache(); }
            if (staged is not null)
                Require(new FileInfo(Path.Combine(staged.Directory, "forged.txt")).Length == 1, "Forged declaration expanded beyond its size budget");
            fixture.Preserved();
        });
        test("updates cancellation during download cleans its workspace and preserves current history", () =>
        {
            using var fixture = new Fixture(); using var source = fixture.Source(); using var cancel = new CancellationTokenSource();
            var progress = new ImmediateProgress(value => { if (value.Bytes > 0) cancel.Cancel(); });
            Throws<OperationCanceledException>(() => Done(() => source.StageAsync(fixture.Candidate(), progress, cancel.Token)));
            fixture.CleanCache(); fixture.Preserved();
        });
        test("updates positive staging reports verified identity without activating or removing old versions", () =>
        {
            using var fixture = new Fixture(); using var source = fixture.Source();
            var reports = new ConcurrentQueue<UpdateProgress>();
            var staged = Done(() => source.StageAsync(fixture.Candidate(), new ImmediateProgress(reports.Enqueue), CancellationToken.None));
            Require(staged.Build == new AppBuildInfo(Version, BuildId, Commit, "testing"), "Wrong prepared build");
            Require(staged.Directory.StartsWith(fixture.Cache + Path.DirectorySeparatorChar, StringComparison.Ordinal), "Payload not inside staging cache");
            Require(!File.Exists(Path.Combine(Path.GetDirectoryName(staged.Directory)!, "package.zip")), "Verified ZIP was retained unnecessarily");
            Require(reports.Any(p => p.Total == fixture.Package.Length && p.Bytes == fixture.Package.Length) && reports.Any(p => p.Phase.Contains("Verificando")), "Download verification progress missing");
            fixture.Preserved();
        });
    }

    private static void Require(bool condition, string reason) { if (!condition) throw new Exception(reason); }
    private static T Done<T>(Func<Task<T>> action) => Task.Run(action).WaitAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult();
    private static void Done(Func<Task> action) => Task.Run(action).WaitAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult();
    private static T Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T error) { return error; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static HttpResponseMessage Json(object value) => Bytes(JsonSerializer.SerializeToUtf8Bytes(value));
    private static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private static HttpResponseMessage Redirect(string uri)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(uri, UriKind.RelativeOrAbsolute); return response;
    }
    private static byte[] Package(string buildVersion = Version, bool executable = true, int identityPadding = 0, (string Name, string Text, int Attributes)[]? extra = null)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            void Add(string name, string text, int attributes = 0)
            {
                var entry = zip.CreateEntry(name); entry.ExternalAttributes = attributes;
                using var stream = entry.Open(); var bytes = Encoding.UTF8.GetBytes(text); stream.Write(bytes);
            }
            Add("build-info.json", JsonSerializer.Serialize(new { version = buildVersion, buildId = BuildId, commit = Commit, channel = "testing" }) + new string(' ', identityPadding));
            if (executable) Add("ApolloOverlay.exe", "MZ fixture only, never executed");
            foreach (var entry in extra ?? []) Add(entry.Name, entry.Text, entry.Attributes);
        }
        return output.ToArray();
    }
    private static byte[] ForgeLength(byte[] archive, string name, uint declared)
    {
        var result = archive.ToArray();
        for (var offset = 0; offset <= result.Length - 46; offset++)
        {
            if (BitConverter.ToUInt32(result, offset) != 0x02014b50) continue;
            var length = BitConverter.ToUInt16(result, offset + 28);
            if (offset + 46 + length > result.Length || Encoding.UTF8.GetString(result, offset + 46, length) != name) continue;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(offset + 24, 4), declared); return result;
        }
        throw new Exception("Fixture ZIP entry not found");
    }

    private sealed class ImmediateProgress(Action<UpdateProgress> report) : IProgress<UpdateProgress>
    { public void Report(UpdateProgress value) => report(value); }
    private sealed class MemoryCredentials : IUpdateCredentialStore
    {
        internal string? Value = Token; internal int Writes, Deletes;
        public string? Read() => Value;
        public void Write(string token) { Value = token; Writes++; }
        public void Delete() { Value = null; Deletes++; }
    }
    private sealed record Request(Uri Uri, string? Authorization);
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> route, ConcurrentQueue<Request> requests) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            requests.Enqueue(new(request.RequestUri!, request.Headers.Authorization?.ToString()));
            cancellation.ThrowIfCancellationRequested();
            return Task.FromResult(route(request, cancellation));
        }
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "OverlayUpdateTests", Guid.NewGuid().ToString("N"));
        internal string Cache => Path.Combine(Root, "cache");
        internal readonly MemoryCredentials Credentials = new();
        internal readonly ConcurrentQueue<Request> Requests = new();
        internal byte[] Package = UpdateSourceTests.Package();
        internal object[]? ReleaseList;
        internal string? ManifestDigest;
        internal string ReleaseVersion = Version;
        internal string? ReleaseTag;
        internal long? PackageAssetSize;
        internal Func<HttpRequestMessage, CancellationToken, HttpResponseMessage>? Route;
        internal Fixture(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage>? route = null)
        {
            Route = route; Directory.CreateDirectory(Path.Combine(Root, "installed", "versions", "previous"));
            File.WriteAllText(Path.Combine(Root, "installed", "install-state.json"), "old pointer");
            File.WriteAllText(Path.Combine(Root, "installed", "versions", "previous", "ApolloOverlay.exe"), "previous version");
            File.WriteAllText(Path.Combine(Root, "settings.json"), "user settings");
        }
        internal GitHubUpdateSource Source() => new(new Handler(Respond, Requests), Credentials, Cache);
        internal UpdateCandidate Candidate() => new(1, Version, BuildId, Commit, "testing", Published, "Fixture notes", 201,
            "GameSessionOverlay-fixture-win-x64.zip", Package.Length, Convert.ToHexString(SHA256.HashData(Package)).ToLowerInvariant());
        private byte[] Manifest() => JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1, version = Version, buildId = BuildId, commit = Commit, channel = "testing", publishedAt = Published,
            package = new { name = Candidate().PackageName, size = Package.Length, sha256 = Candidate().Sha256 }
        });
        internal object Release(long id = 1, bool prerelease = true, bool draft = false, bool legacy = false) => new
        {
            id, name = ReleaseVersion, tag_name = ReleaseTag ?? "v" + Version, draft, prerelease, published_at = Published, body = "Fixture notes",
            assets = legacy ? [] : new object[]
            {
                new { id = 101L, name = "overlay-release.json", size = Manifest().Length, digest = ManifestDigest ?? "sha256:" + Convert.ToHexString(SHA256.HashData(Manifest())).ToLowerInvariant() },
                new { id = 201L, name = Candidate().PackageName, size = PackageAssetSize ?? Package.Length, digest = "sha256:" + Candidate().Sha256 }
            }
        };
        private HttpResponseMessage Respond(HttpRequestMessage request, CancellationToken cancellation)
        {
            if (Route is not null) return Route(request, cancellation);
            return request.RequestUri!.AbsoluteUri switch
            {
                Api => Json(new { full_name = Repository }),
                Api + "/releases?per_page=100" => Json(ReleaseList ?? [Release()]),
                Api + "/releases/assets/101" => Bytes(Manifest()),
                Api + "/releases/assets/201" => Bytes(Package),
                _ => throw new Exception("Unexpected fixture URL: " + request.RequestUri)
            };
        }
        internal void CleanCache() => Require(!Directory.Exists(Cache) || !Directory.EnumerateFileSystemEntries(Cache).Any(), "Incomplete download left a staging workspace");
        internal void Preserved()
        {
            Require(File.ReadAllText(Path.Combine(Root, "installed", "install-state.json")) == "old pointer", "Active installation pointer modified during staging");
            Require(File.ReadAllText(Path.Combine(Root, "installed", "versions", "previous", "ApolloOverlay.exe")) == "previous version", "Historical payload modified during staging");
            Require(File.ReadAllText(Path.Combine(Root, "settings.json")) == "user settings", "User settings modified during staging");
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
