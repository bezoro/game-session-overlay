using ApolloOverlay;

internal static class GuideDiagnosticTrackerTests
{
    internal static void Run(Action<string, Action> test)
    {
        test("Guide diagnostics record edges without idle polling spam", () =>
        {
            var tracker = new GuideDiagnosticTracker();
            tracker.Observe(true, 1, false, false, 1, 0, 2000);
            Require(tracker.Observe(true, 1, false, false, 1, 20, 2000).Count == 0);
            Require(tracker.Observe(true, 1, true, false, 1, 40, 2000).Single().Reason == "down");
            Require(tracker.Observe(true, 1, true, false, 1, 60, 2000).Count == 0);
            Require(tracker.Observe(true, 1, false, false, 1, 80, 2000).Single().Reason == "up");
        });
        test("Guide diagnostics explain a held opening rejected by the configured limit", () =>
        {
            var tracker = new GuideDiagnosticTracker();
            tracker.Observe(true, 1, false, false, 1, 0, 500);
            tracker.Observe(true, 1, true, false, 1, 10, 500);
            Require(tracker.Observe(true, 1, true, false, 1, 511, 500).Single().Reason == "hold-limit-exceeded");
            Require(tracker.Observe(true, 1, true, false, 1, 700, 500).Count == 0);
        });
        test("Guide diagnostics preserve invalid readings and disconnects without a false opening rejection", () =>
        {
            var tracker = new GuideDiagnosticTracker();
            tracker.Observe(true, 1, false, false, 1, 0, 500);
            tracker.Observe(true, 1, true, false, 1, 10, 500);
            var invalid = tracker.Observe(false, 0, false, false, 1, 20, 500);
            Require(invalid.Any(entry => entry.Outcome == ResultKind.Unknown && entry.Reason == "read-invalid")
                && invalid.Any(entry => entry.Outcome == ResultKind.Unavailable));
            Require(!tracker.Observe(true, 1, true, false, 1, 800, 500).Any(entry => entry.Reason == "hold-limit-exceeded"));
        });
    }
    private static void Require(bool condition)
    {
        if (!condition)
            throw new Exception("Guide diagnostic state was incorrect");
    }
}
