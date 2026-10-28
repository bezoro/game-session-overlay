using System.Net.Http;
using System.IO;
using ApolloOverlay;
using Velopack;
using Velopack.Logging;
using SdkUpdateSource = Velopack.Sources.IUpdateSource;

internal static class UpdateSourceTests
{
    private const string Commit = "1111111111111111111111111111111111111111";
    private const string Version = "0.2.0-preview.42.1+1111111";
    private const string BuildId = "ci-42-a1-1111111";
    internal static void Run(Action<string, Action> test)
    {
        test("updates official SDK adapter preserves identity and its wrapped download asset", () =>
        {
            var asset = Asset();
            var sdk = new SourceFake(asset);
            using var source = new GitHubUpdateSource(_ => sdk);
            var values = Done(() => source.CheckAsync("testing", CancellationToken.None));
            Require(values.Count == 1 && values[0].Version == Version && values[0].BuildId == BuildId && values[0].Commit == Commit, "Published build identity was lost");
            Require(ReferenceEquals(values[0].Asset, asset) && values[0].PackageName == asset.FileName && values[0].Sha256 == asset.SHA256, "SDK download wrapping was replaced or detached");
            Require(values[0].Notes == "Plain release notes." && asset.NotesMarkdown.Contains("Commit: " + Commit), "Technical identity leaked into displayed notes or changed the SDK asset");
            Require(sdk.Feeds == 1 && sdk.AppId == VelopackInstallation.PackageId && sdk.Channel == "testing" && sdk.Downloads == 0, "Catalog query did not stay read-only");
            Require(GitHubUpdateSource.RepositoryUrl == "https://github.com/bezoro/game-session-overlay", "Public repository identity is wrong");
        });
        test("updates SDK adapter ignores delta installer and foreign-application feed entries", () =>
        {
            var full = Asset();
            var sdk = new SourceFake(full, full with
            {
                Type = VelopackAssetType.Delta
            }, full with
            {
                Type = VelopackAssetType.Installer
            }, full with
            {
                PackageId = "OtherApplication"
            });
            using var source = new GitHubUpdateSource(_ => sdk);
            var values = Done(() => source.CheckAsync("testing", CancellationToken.None));
            Require(values.Count == 1 && ReferenceEquals(values[0].Asset, full), "Non-full or foreign-app release offered as an update");
        });
        test("updates SDK adapter collapses repeated identical release indexes", () =>
        {
            var asset = Asset();
            var sdk = new SourceFake(asset, asset with
            {
            });
            using var source = new GitHubUpdateSource(_ => sdk);
            Require(Done(() => source.CheckAsync("testing", CancellationToken.None)).Count == 1, "Repeated index produced duplicate controller actions");
        });
        test("updates SDK adapter rejects conflicting payloads with the same build identity", () =>
        {
            var asset = Asset();
            var sdk = new SourceFake(asset, asset with
            {
                SHA256 = new string('b', 64)
            });
            using var source = new GitHubUpdateSource(_ => sdk);
            Throws<InvalidDataException>(() => Done(() => source.CheckAsync("testing", CancellationToken.None)));
        });
        test("updates SDK adapter orders semantic versions rather than strings", () =>
        {
            var lower = Asset("0.2.0-preview.2.1+1111111", "ci-2-a1-1111111");
            var higher = Asset("0.2.0-preview.10.1+1111111", "ci-10-a1-1111111");
            var sdk = new SourceFake(lower, higher);
            using var source = new GitHubUpdateSource(_ => sdk);
            var values = Done(() => source.CheckAsync("testing", CancellationToken.None));
            Require(values[0].BuildId == "ci-10-a1-1111111" && values[1].BuildId == "ci-2-a1-1111111", "Version ordering used publication text");
        });
        foreach (var field in new[]
        {
            "Version",
            "Build",
            "Commit",
            "Channel"
        }

        )
        {
            var key = field;
            test("updates SDK adapter requires a unique published metadata field " + key, () =>
            {
                var asset = Asset();
                var line = asset.NotesMarkdown.Split('\n').Single(l => l.StartsWith(key + ":", StringComparison.Ordinal));
                foreach (var notes in new[]
                {
                    asset.NotesMarkdown.Replace(line, "", StringComparison.Ordinal),
                    asset.NotesMarkdown + "\n" + line
                }

                )
                {
                    var sdk = new SourceFake(asset with
                    {
                        NotesMarkdown = notes
                    });
                    using var source = new GitHubUpdateSource(_ => sdk);
                    Throws<InvalidDataException>(() => Done(() => source.CheckAsync("testing", CancellationToken.None)));
                    Require(sdk.Downloads == 0, "Invalid metadata initiated download");
                }
            });
        }

        test("updates SDK adapter rejects mismatch between notes version and nupkg version", () =>
        {
            var asset = Asset() with
            {
                Version = SemanticVersion.Parse("0.3.0")
            };
            var sdk = new SourceFake(asset);
            using var source = new GitHubUpdateSource(_ => sdk);
            Throws<InvalidDataException>(() => Done(() => source.CheckAsync("testing", CancellationToken.None)));
        });
        foreach (var buildId in new[]
        {
            "../outside",
            "contains space",
            "CON",
            "ending.",
            new string ('x', 97)
        }

        )
        {
            var id = buildId;
            test("updates SDK adapter refuses unsafe build identity " + id, () =>
            {
                var sdk = new SourceFake(Asset(buildId: id));
                using var source = new GitHubUpdateSource(_ => sdk);
                Throws<InvalidDataException>(() => Done(() => source.CheckAsync("testing", CancellationToken.None)));
            });
        }

        test("updates SDK adapter requires full source commit and the selected channel", () =>
        {
            foreach (var notes in new[]
            {
                Notes().Replace(Commit, "unknown", StringComparison.Ordinal),
                Notes().Replace("Channel: testing", "Channel: stable", StringComparison.Ordinal)
            }

            )
            {
                var sdk = new SourceFake(Asset() with
                {
                    NotesMarkdown = notes
                });
                using var source = new GitHubUpdateSource(_ => sdk);
                Throws<InvalidDataException>(() => Done(() => source.CheckAsync("testing", CancellationToken.None)));
            }
        });
        test("updates SDK adapter supports an explicitly selected stable channel", () =>
        {
            var sdk = new SourceFake(Asset("0.2.42+1111111", channel: "stable"));
            using var source = new GitHubUpdateSource(_ => sdk);
            var values = Done(() => source.CheckAsync("stable", CancellationToken.None));
            Require(values.Single().Channel == "stable" && sdk.Channel == "stable", "Stable channel did not reach the SDK source");
        });
        foreach (var badAsset in new[]
        {
            Asset()with
            {
                SHA256 = ""
            },
            Asset()with
            {
                SHA1 = "bad"
            },
            Asset()with
            {
                Size = 0
            },
            Asset()with
            {
                Size = 1024L * 1024 * 1024 + 1
            }
        }

        )
        {
            var asset = badAsset;
            test("updates SDK adapter rejects incomplete hash or size declaration " + asset.SHA256.Length + "/" + asset.SHA1.Length + "/" + asset.Size, () =>
            {
                var sdk = new SourceFake(asset);
                using var source = new GitHubUpdateSource(_ => sdk);
                Throws<InvalidDataException>(() => Done(() => source.CheckAsync("testing", CancellationToken.None)));
            });
        }

        foreach (var path in new[]
        {
            "../package.nupkg",
            "folder\\package.nupkg",
            "C:package.nupkg",
            "package.zip"
        }

        )
        {
            var file = path;
            test("updates SDK adapter refuses unsafe package filename " + file, () =>
            {
                var sdk = new SourceFake(Asset() with
                {
                    FileName = file
                });
                using var source = new GitHubUpdateSource(_ => sdk);
                Throws<InvalidDataException>(() => Done(() => source.CheckAsync("testing", CancellationToken.None)));
            });
        }

        test("updates SDK adapter bounds displayed release notes", () =>
        {
            var sdk = new SourceFake(Asset() with
            {
                NotesMarkdown = Notes() + new string('x', 65537)
            });
            using var source = new GitHubUpdateSource(_ => sdk);
            Throws<InvalidDataException>(() => Done(() => source.CheckAsync("testing", CancellationToken.None)));
        });
        test("updates SDK adapter empty feed stays distinct from a freshness guarantee", () =>
        {
            var sdk = new SourceFake();
            using var source = new GitHubUpdateSource(_ => sdk);
            Require(Done(() => source.CheckAsync("testing", CancellationToken.None)).Count == 0 && sdk.Downloads == 0, "Empty catalog initiated work");
        });
        test("updates SDK adapter propagates feed failures instead of claiming no updates", () =>
        {
            var sdk = new SourceFake
            {
                Failure = new HttpRequestException("Fixture server refused")
            };
            using var source = new GitHubUpdateSource(_ => sdk);
            Throws<HttpRequestException>(() => Done(() => source.CheckAsync("testing", CancellationToken.None)));
        });
        test("updates SDK adapter metadata cancellation fences a late official source completion", () =>
        {
            var sdk = new SourceFake
            {
                Pending = new(TaskCreationOptions.RunContinuationsAsynchronously)
            };
            using var source = new GitHubUpdateSource(_ => sdk);
            using var cancellation = new CancellationTokenSource();
            var check = source.CheckAsync("testing", cancellation.Token);
            cancellation.Cancel();
            Throws<OperationCanceledException>(() => Complete(check));
            sdk.Pending.SetResult(new VelopackAssetFeed { Assets = [Asset()] });
            Require(check.IsCanceled && sdk.Downloads == 0, "Late SDK completion changed the cancelled query");
        });
        test("updates SDK adapter disposal fences a pending feed and rejects new work", () =>
        {
            var sdk = new SourceFake
            {
                Pending = new(TaskCreationOptions.RunContinuationsAsynchronously)
            };
            var source = new GitHubUpdateSource(_ => sdk);
            var pending = source.CheckAsync("testing", CancellationToken.None);
            source.Dispose();
            sdk.Pending.SetResult(new VelopackAssetFeed { Assets = [Asset()] });
            Throws<ObjectDisposedException>(() => Complete(pending));
            Throws<ObjectDisposedException>(() => Done(() => source.CheckAsync("testing", CancellationToken.None)));
            Require(sdk.Feeds == 1 && sdk.Downloads == 0, "Disposed adapter started work");
        });
        test("updates SDK adapter rejects invalid channels and cancellation before contacting the source", () =>
        {
            var factories = 0;
            using var source = new GitHubUpdateSource(_ =>
            {
                factories++;
                return new SourceFake();
            });
            Throws<ArgumentException>(() => Done(() => source.CheckAsync("other", CancellationToken.None)));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Throws<OperationCanceledException>(() => Done(() => source.CheckAsync("testing", cancellation.Token)));
            Require(factories == 0, "Invalid or cancelled query contacted the SDK source");
        });
        test("updates SDK adapter refuses detached or modified staged candidates before touching native update", () =>
        {
            var sdk = new SourceFake(Asset());
            using var source = new GitHubUpdateSource(_ => sdk);
            var candidate = Done(() => source.CheckAsync("testing", CancellationToken.None)).Single();
            foreach (var invalid in new[]
            {
                candidate with
                {
                    Asset = null
                },
                candidate with
                {
                    Sha256 = new string ('b', 64)
                },
                candidate with
                {
                    Commit = new string ('2', 40)
                },
                candidate with
                {
                    Size = 1
                },
                candidate with
                {
                    Version = "0.9.0"
                }
            }

            )
                Throws<InvalidDataException>(() => Done(() => source.StageAsync(invalid, null, CancellationToken.None)));
            Require(sdk.Downloads == 0, "Invalid candidate reached SDK download");
        });
    }

    private static VelopackAsset Asset(string version = Version, string buildId = BuildId, string channel = "testing") => new()
    {
        PackageId = VelopackInstallation.PackageId,
        Version = SemanticVersion.Parse(version),
        Type = VelopackAssetType.Full,
        FileName = "GameSessionOverlay-" + SemanticVersion.Parse(version).ToNormalizedString() + "-full.nupkg",
        Size = 12345,
        SHA256 = new string('a', 64),
        SHA1 = new string('b', 40),
        NotesMarkdown = Notes(version, buildId, channel)
    };
    private static string Notes(string version = Version, string buildId = BuildId, string channel = "testing") => "Version: " + version + "\nBuild: " + buildId + "\nCommit: " + Commit + "\nChannel: " + channel + "\n\nPlain release notes.";
    private static void Require(bool condition, string reason)
    {
        if (!condition)
            throw new Exception(reason);
    }

    private static T Done<T>(Func<Task<T>> action) => Task.Run(action).WaitAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult();
    private static void Complete(Task task) => task.WaitAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult();
    private static T Throws<T>(Action action)
        where T : Exception
    {
        try
        {
            action();
        }
        catch (T error)
        {
            return error;
        }

        throw new Exception("Expected " + typeof(T).Name);
    }

    private sealed class SourceFake(params VelopackAsset[] assets) : SdkUpdateSource
    {
        internal int Feeds, Downloads;
        internal string? AppId, Channel;
        internal TaskCompletionSource<VelopackAssetFeed>? Pending;
        internal Exception? Failure;
        public Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
        {
            Feeds++;
            AppId = appId;
            Channel = channel;
            if (Failure is { } error)
                return Task.FromException<VelopackAssetFeed>(error);
            return Pending?.Task ?? Task.FromResult(new VelopackAssetFeed { Assets = assets });
        }

        public Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress, CancellationToken cancelToken)
        {
            Downloads++;
            return Task.FromException(new Exception("Native SDK download is not permitted in catalog policy fixtures"));
        }
    }
}
