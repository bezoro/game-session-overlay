using ApolloOverlay;
using Velopack;

internal static class UpdateDiscoveryTests
{
    internal static void Run(Action<string, Action> test)
    {
        test("automatic update discovery waits for startup and defers busy attempts by thirty seconds", () =>
        {
            var schedule = new UpdateDiscoverySchedule(123_000);
            Require(!schedule.IsDue(123_000) && !schedule.IsDue(137_999) && schedule.IsDue(138_000), "Startup discovery did not respect its delay");
            schedule.Defer(138_000);
            Require(!schedule.IsDue(167_999) && schedule.IsDue(168_000), "Busy discovery did not wait thirty seconds");
        });
        test("automatic update discovery uses the completed check time for its fifteen minute cadence", () =>
        {
            var schedule = new UpdateDiscoverySchedule(0);
            schedule.Complete(90_000, succeeded: true);
            Require(!schedule.IsDue(989_999) && schedule.IsDue(990_000), "The next query was scheduled from startup instead of completion");
        });
        test("automatic update discovery backs off failures at five ten and fifteen minutes with a fixed cap", () =>
        {
            var schedule = new UpdateDiscoverySchedule(0);
            long now = 15_000;
            foreach (var minutes in new[] { 5, 10, 15, 15, 15 })
            {
                schedule.Complete(now, succeeded: false);
                var due = now + minutes * 60_000;
                Require(!schedule.IsDue(due - 1) && schedule.IsDue(due), "Failure backoff was incorrect or unbounded");
                now = due;
            }
            schedule.Complete(now, succeeded: true);
            schedule.Complete(now + 1, succeeded: false);
            Require(schedule.NextCheckAt == now + 1 + 5 * 60_000, "A successful query did not reset failure backoff");
        });
        test("automatic update discovery channel changes are immediate and clear the previous channel backoff", () =>
        {
            var schedule = new UpdateDiscoverySchedule(0);
            schedule.Complete(15_000, succeeded: false);
            schedule.Complete(315_000, succeeded: false);
            schedule.Reset(400_000);
            Require(schedule.IsDue(400_000), "A channel change retained the old channel delay");
            schedule.Complete(400_000, succeeded: false);
            Require(schedule.NextCheckAt == 700_000, "A new channel inherited the previous channel failure count");
        });
        test("update availability selects the highest semantic version before publication time", () =>
        {
            var current = Build("0.50.0-preview.2.1");
            var laterPublished = Release("0.50.0-preview.9.1", "later", DateTimeOffset.UnixEpoch.AddDays(2));
            var newer = Release("0.50.0-preview.10.1", "newer", DateTimeOffset.UnixEpoch.AddDays(1));
            Require(ReferenceEquals(UpdateAvailability.Newer([laterPublished, newer], current, "testing"), newer), "Available preview ordering compared text or publication time first");
        });
        test("update availability breaks equal version ties by publication time", () =>
        {
            var earlier = Release("0.51.0-preview.1", "earlier", DateTimeOffset.UnixEpoch);
            var later = Release("0.51.0-preview.1", "later", DateTimeOffset.UnixEpoch.AddMinutes(1));
            Require(ReferenceEquals(UpdateAvailability.Newer([later, earlier], Build("0.50.0"), "testing"), later), "Equal version releases did not select the later publication");
        });
        test("update availability does not advertise a stable channel downgrade from a newer testing build", () =>
        {
            var stable = Release("0.49.1", "stable", DateTimeOffset.UnixEpoch.AddDays(1), "stable");
            Require(UpdateAvailability.Newer([stable], Build("0.50.0-preview.96.1"), "stable") is null, "Changing channels advertised an older stable version as an upgrade");
            var promoted = Release("0.50.0", "promoted", DateTimeOffset.UnixEpoch, "stable");
            Require(ReferenceEquals(UpdateAvailability.Newer([promoted], Build("0.50.0-preview.96.1"), "stable"), promoted), "A stable promotion was not newer than its prerelease");
        });
        test("update availability ignores malformed versions wrong channels and mismatched SDK versions", () =>
        {
            var invalid = Release("invalid", "invalid", DateTimeOffset.UnixEpoch);
            var otherChannel = Release("9.0.0", "stable", DateTimeOffset.UnixEpoch, "stable");
            var mismatch = Release("9.0.0", "mismatch", DateTimeOffset.UnixEpoch) with
            {
                Asset = new VelopackAsset { Version = SemanticVersion.Parse("0.1.0") }
            };
            var installed = Release("9.0.0", "installed", DateTimeOffset.UnixEpoch);
            Require(UpdateAvailability.Newer([invalid, otherChannel, mismatch, installed], Build("0.50.0"), "testing") is null,
                "Invalid, mismatched, or installed identities were advertised");
            Require(UpdateAvailability.Newer([otherChannel], Build("invalid"), "stable") is null
                && UpdateAvailability.Newer([otherChannel], Build("0.50.0"), "other") is null,
                "An unknown current version or channel made a freshness claim");
        });
        test("update availability does not treat new build metadata as a semantic upgrade", () =>
        {
            var sameVersion = Release("0.50.0+2222222", "different-commit", DateTimeOffset.UnixEpoch.AddDays(1));
            Require(UpdateAvailability.Newer([sameVersion], Build("0.50.0+1111111"), "testing") is null,
                "A different build hash created a false upgrade indicator");
            Require(UpdateAvailability.Newer([], Build("0.50.0"), "testing") is null, "An empty feed claimed an update");
        });
    }

    private static AppBuildInfo Build(string version) => new(version, "installed", new string('1', 40), "testing");
    private static UpdateCandidate Release(string version, string build, DateTimeOffset published, string channel = "testing") =>
        new(0, version, build, new string('2', 40), channel, published, "", 0, "fixture.nupkg", 12345, new string('a', 64));
    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
