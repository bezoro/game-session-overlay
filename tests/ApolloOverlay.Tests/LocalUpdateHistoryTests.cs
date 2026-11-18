using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ApolloOverlay;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

internal static class LocalUpdateHistoryTests
{
    private static void Require(bool value, string reason)
    {
        if (!value)
            throw new Exception(reason);
    }

    private static void Complete(Task task) => task.GetAwaiter().GetResult();
    private static void Refuses(Func<Task> operation)
    {
        try
        {
            Complete(operation());
        }
        catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or ArgumentException or OperationCanceledException)
        {
            return;
        }

        throw new Exception("Invalid cached package was accepted");
    }

    internal static void Run(Action<string, Action> test)
    {
        test("update history observations share background work and invalidate cached snapshots", () =>
        {
            var caller = Environment.CurrentManagedThreadId;
            var reader = caller;
            var reads = 0;
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var history = new UpdateHistorySnapshot(() =>
            {
                reader = Environment.CurrentManagedThreadId;
                var read = Interlocked.Increment(ref reads);
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(3)))
                    throw new TimeoutException("Fixture history read was not released");
                return new[] { new InstalledBuild("build-" + read, "1.0.0", new string('a', 40), DateTimeOffset.UnixEpoch) };
            });
            var first = history.ReadAsync();
            Require(entered.Wait(TimeSpan.FromSeconds(3)) && !first.IsCompleted && history.Current.Count == 0,
                "The history read blocked its caller or published incomplete data");
            Require(ReferenceEquals(first, history.ReadAsync()), "Concurrent observations started duplicate disk reads");
            history.Invalidate();
            release.Set();
            Complete(first);
            Require(reader != caller && reads == 2 && history.Current.Single().BuildId == "build-2" && history.Fresh,
                "An invalidated observation was published or file work ran on the caller");
            Complete(history.ReadAsync());
            Require(reads == 2, "Fresh history was reread during polling");
        });
        test("update receipt writes preserve durable ordering and coalesce superseded checkpoints", () =>
        {
            var caller = Environment.CurrentManagedThreadId;
            var writes = new List<int>();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var writer = new UpdateReceiptWriter(bytes =>
            {
                Require(Environment.CurrentManagedThreadId != caller, "Receipt persistence ran on the UI caller");
                if (bytes[0] == 1)
                {
                    entered.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(3)))
                        throw new TimeoutException("Fixture receipt write was not released");
                }
                writes.Add(bytes[0]);
            });
            var initial = writer.WriteAsync([1]);
            Require(entered.Wait(TimeSpan.FromSeconds(3)) && !initial.IsCompleted, "Receipt did not expose its pending durable boundary");
            var progress = writer.WriteAsync([2], checkpoint: true);
            var superseding = writer.WriteAsync([3], checkpoint: true);
            var apply = writer.WriteAsync([4]);
            Require(ReferenceEquals(progress, superseding) && !apply.IsCompleted, "Progress backlog grew or apply bypassed a prior write");
            release.Set();
            Complete(writer.Drained);
            Require(writes.SequenceEqual(new[] { 1, 3, 4 }), "A stale checkpoint overwrote a newer durable receipt");
        });
        test("update receipt failure is observable without poisoning the next durable write", () =>
        {
            var writes = 0;
            var writer = new UpdateReceiptWriter(_ =>
            {
                if (Interlocked.Increment(ref writes) == 1)
                    throw new IOException("Fixture disk failure");
            });
            var failed = writer.WriteAsync([1]);
            var recovered = writer.WriteAsync([2]);
            Refuses(() => failed);
            Complete(recovered);
            Require(writes == 2 && writer.Drained.IsCompletedSuccessfully, "A failed receipt prevented subsequent recovery");
        });
        test("rollback history retains verified packages and lists only actually installed versions", () =>
        {
            using var f = new Fixture();
            var a = f.Package("a", "1.0.0");
            Complete(f.History.ArchiveVerifiedAsync(a.Path, a.Asset, a.Build));
            Require(f.History.List().Count == 0, "Untried download was offered as a prior installation");
            Refuses(() => f.History.GetVerifiedAsync("a"));
            Complete(f.History.ArchiveVerifiedAsync(a.Path, a.Asset, a.Build, true));
            Require(f.History.List("a").Single().Current && File.Exists(f.History.GetVerifiedAsync("a").GetAwaiter().GetResult().PackagePath), "Installed version was not preserved");
        });
        test("rollback archive compares the original feed hash instead of trusting a new baseline", () =>
        {
            using var f = new Fixture();
            var a = f.Package("a", "1.0.0");
            Refuses(() => f.History.ArchiveVerifiedAsync(a.Path, a.Asset with { SHA256 = new string('0', 64) }, a.Build, true));
            Require(f.History.List().Count == 0, "Mismatched download entered history");
        });
        test("rollback archive verifies internal build identity as well as package manifest", () =>
        {
            using var f = new Fixture();
            var a = f.Package("a", "1.0.0");
            Refuses(() => f.History.ArchiveVerifiedAsync(a.Path, a.Asset, a.Build with { Commit = new string('b', 40) }, true));
            Refuses(() => f.History.ArchiveVerifiedAsync(a.Path, a.Asset with { Version = SemanticVersion.Parse("2.0.0") }, a.Build, true));
        });
        test("rollback archive refuses corrupted cached bytes before SDK apply", () =>
        {
            using var f = new Fixture();
            var a = f.Package("a", "1.0.0");
            Complete(f.History.ArchiveVerifiedAsync(a.Path, a.Asset, a.Build, true));
            var retained = f.History.GetVerifiedAsync("a").GetAwaiter().GetResult();
            File.AppendAllText(retained.PackagePath, "corrupt");
            Refuses(() => f.History.GetVerifiedAsync("a"));
            Require(File.ReadAllText(a.Path).Length > 0, "Original package was changed");
        });
        test("rollback archive rejects unsafe IDs and package filenames", () =>
        {
            using var f = new Fixture();
            var a = f.Package("a", "1.0.0");
            foreach (var name in new[]
            {
                "../escape.nupkg",
                "C:\\escape.nupkg",
                "nested/file.nupkg",
                "nested\\file.nupkg"
            }

            )
                Refuses(() => f.History.ArchiveVerifiedAsync(a.Path, a.Asset with { FileName = name }, a.Build, true));
            foreach (var id in new[]
            {
                "../escape",
                "CON",
                "..",
                "bad/dir"
            }

            )
                Refuses(() => f.History.ArchiveVerifiedAsync(a.Path, a.Asset, a.Build with { BuildId = id }, true));
        });
        test("rollback archive refuses a changed package with the same immutable build ID", () =>
        {
            using var f = new Fixture();
            var a = f.Package("a", "1.0.0");
            Complete(f.History.ArchiveVerifiedAsync(a.Path, a.Asset, a.Build, true));
            var changed = f.Package("a", "1.0.0", "different");
            Refuses(() => f.History.ArchiveVerifiedAsync(changed.Path, changed.Asset, changed.Build, true));
            Require(f.History.GetVerifiedAsync("a").GetAwaiter().GetResult().Asset.SHA256 == a.Asset.SHA256, "Previous immutable package was overwritten");
        });
        test("rollback archive cancellation leaves no installation record", () =>
        {
            using var f = new Fixture();
            var a = f.Package("a", "1.0.0");
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            Refuses(() => f.History.ArchiveVerifiedAsync(a.Path, a.Asset, a.Build, true, cancel.Token));
            Require(f.History.List().Count == 0, "Cancelled copy entered history");
        });
        test("rollback restore matches the selected running build and retains newest settings", () =>
        {
            using var f = new Fixture();
            var a = f.Package("a", "1.0.0");
            Complete(f.History.ArchiveVerifiedAsync(a.Path, a.Asset, a.Build, true));
            File.WriteAllText(f.Settings, "{\"compatible\":true}");
            f.History.SnapshotSettings("a");
            File.WriteAllText(f.Settings, "{\"newSchema\":99}");
            f.History.QueueSettingsRestore(a.Build);
            Require(f.History.CompleteSettingsRestore(a.Build) is { Outcome: SettingsRestoreOutcome.Restored }, "Rollback restore missing");
            Require(File.ReadAllText(f.Settings) == "{\"compatible\":true}" && File.ReadAllText(f.Settings + ".before-rollback") == "{\"newSchema\":99}", "Compatible or newest settings were lost");
            Require(f.History.CompleteSettingsRestore(a.Build) is null, "Restore repeated on another startup");
        });
        test("rollback pending settings cannot be applied to an unrelated running version", () =>
        {
            using var f = new Fixture();
            var a = f.Package("a", "1.0.0");
            Complete(f.History.ArchiveVerifiedAsync(a.Path, a.Asset, a.Build, true));
            File.WriteAllText(f.Settings, "{\"prior\":true}");
            f.History.SnapshotSettings("a");
            File.WriteAllText(f.Settings, "{\"current\":true}");
            f.History.QueueSettingsRestore(a.Build);
            Require(f.History.CompleteSettingsRestore(a.Build with
            {
                BuildId = "unrelated"
            }) is null
                && File.ReadAllText(f.Settings).Contains("current"), "Unrelated startup restored old settings");
            Require(f.History.CompleteSettingsRestore(a.Build) is { Outcome: SettingsRestoreOutcome.Restored } && File.ReadAllText(f.Settings).Contains("prior"), "An unrelated launch consumed the active rollback");
        });
        test("rollback corrupt settings snapshot preserves current settings", () =>
        {
            using var f = new Fixture();
            var a = f.Package("a", "1.0.0");
            Complete(f.History.ArchiveVerifiedAsync(a.Path, a.Asset, a.Build, true));
            File.WriteAllText(f.Settings, "{\"old\":true}");
            f.History.SnapshotSettings("a");
            File.WriteAllText(Path.Combine(f.History.Root, "a", "settings.snapshot"), "damaged");
            File.WriteAllText(f.Settings, "{\"current\":true}");
            f.History.QueueSettingsRestore(a.Build);
            Refuses(() => Task.Run(() => f.History.CompleteSettingsRestore(a.Build)));
            Require(File.ReadAllText(f.Settings).Contains("current"), "Corrupt snapshot overwrote active settings");
        });
        test("rollback history survives SDK cleanup of all other local packages", () =>
        {
            using var f = new Fixture();
            var a = f.Package("a", "1.0.0");
            var b = f.Package("b", "2.0.0");
            Complete(f.History.ArchiveVerifiedAsync(a.Path, a.Asset, a.Build, true));
            var cache = Path.Combine(f.Directory, "sdk-cache");
            System.IO.Directory.CreateDirectory(cache);
            File.Copy(a.Path, Path.Combine(cache, a.Asset.FileName));
            var locator = new TestVelopackLocator("GameSessionOverlay", "1.0.0", cache);
            var manager = new UpdateManager(new SimpleFileSource(new DirectoryInfo(Path.GetDirectoryName(b.Path)!)), new UpdateOptions { AllowVersionDowngrade = true }, locator);
            Complete(manager.DownloadUpdatesAsync(new UpdateInfo(b.Asset, false)));
            Require(!File.Exists(Path.Combine(cache, a.Asset.FileName)), "SDK cleanup contract changed; adjust independent-cache integration");
            Require(File.Exists(f.History.GetVerifiedAsync("a").GetAwaiter().GetResult().PackagePath), "SDK cleanup destroyed offline rollback history");
        });
        test("rollback history ignores corrupt metadata without hiding healthy entries", () =>
        {
            using var f = new Fixture();
            var a = f.Package("a", "1.0.0");
            Complete(f.History.ArchiveVerifiedAsync(a.Path, a.Asset, a.Build, true));
            System.IO.Directory.CreateDirectory(Path.Combine(f.History.Root, "corrupt"));
            File.WriteAllText(Path.Combine(f.History.Root, "corrupt", "release.json"), "{}");
            Require(f.History.List().Single().BuildId == "a", "Corrupt metadata broke the entire history screen");
        });
        test("rollback archives actual SDK current package and blocks updates if recovery bytes are lost", () =>
        {
            using var f = new Fixture();
            var a = f.Package("a", "1.0.0");
            var cache = Path.Combine(f.Directory, "sdk-cache");
            System.IO.Directory.CreateDirectory(cache);
            var locator = new TestVelopackLocator("GameSessionOverlay", "1.0.0", cache);
            Refuses(() => f.History.RecordCurrentAsync(a.Build, locator));
            File.Copy(a.Path, Path.Combine(cache, a.Asset.FileName));
            Complete(f.History.RecordCurrentAsync(a.Build, locator));
            var retained = f.History.GetVerifiedAsync("a").GetAwaiter().GetResult();
            var timestamp = new DateTime(2020, 2, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(retained.PackagePath, timestamp);
            Complete(f.History.RecordCurrentAsync(a.Build, locator));
            Require(File.GetLastWriteTimeUtc(retained.PackagePath) == timestamp, "Recording a healthy installation replaced retained package bytes");
            File.Delete(Path.Combine(cache, a.Asset.FileName));
            Complete(f.History.RecordCurrentAsync(a.Build, locator));
            Require(f.History.List("a").Single().Current, "SDK cache cleanup hid an archived installed version");
            File.Delete(retained.PackagePath);
            Refuses(() => f.History.RecordCurrentAsync(a.Build, locator));
        });
    }

    private sealed class Fixture : IDisposable
    {
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "VelopackHistoryTests", Guid.NewGuid().ToString("N"));
        internal LocalUpdateHistory History
        {
            get;
        }
        internal string Settings => Path.Combine(Directory, "data", "settings.json");

        internal Fixture()
        {
            System.IO.Directory.CreateDirectory(Path.Combine(Directory, "data"));
            History = new(Path.Combine(Directory, "data"));
        }

        internal (string Path, VelopackAsset Asset, AppBuildInfo Build) Package(string id, string version, string payload = "fixture")
        {
            var folder = Path.Combine(Directory, "packages", Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "GameSessionOverlay-" + version + "-full.nupkg");
            var build = new AppBuildInfo(version + "+" + id, id, new string('a', 40), "testing");
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                Write(archive, "GameSessionOverlay.nuspec", "<package><metadata><id>GameSessionOverlay</id><version>" + version + "</version><authors>Fixture</authors><description>Test package only</description><channel>testing</channel></metadata></package>");
                Write(archive, "lib/app/build-info.json", JsonSerializer.Serialize(build, LocalUpdateHistory.Json));
                Write(archive, "lib/app/fixture.txt", payload);
            }

            return (path, VelopackAsset.FromNupkgGenerateChecksumAsync(path).GetAwaiter().GetResult(), build);
        }

        private static void Write(ZipArchive archive, string path, string content)
        {
            using var writer = new StreamWriter(archive.CreateEntry(path).Open(), new UTF8Encoding(false));
            writer.Write(content);
        }

        public void Dispose()
        {
            if (System.IO.Directory.Exists(Directory))
                System.IO.Directory.Delete(Directory, true);
        }
    }
}
