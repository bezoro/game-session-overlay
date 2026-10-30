using System.IO;
using ApolloOverlay;
using ApolloOverlay.Automation;

internal static class AdministratorCompatibilityTests
{
    internal static void Run(Action<string, Action> test)
    {
        test("administrator restart binds the handoff to the same user, logon, session and process lifetime", () =>
        {
            var current = new AutomationPipeSecurity.Peer("S-1-5-21-100", "S-1-5-5-100-200", 1, 40, 1000);
            var child = current with
            {
                Pid = 41,
                Started = 2000,
                Elevated = true
            };
            Require(AdministratorRestart.MatchesPeer(child, child, current, true), "Expected elevated peer was rejected");
            foreach (var changed in new[]
            {
                child with { User = "S-1-5-21-101" },
                child with { Logon = "S-1-5-5-100-201" },
                child with { Session = 2 },
                child with { Pid = 42 },
                child with { Started = 3000 },
                child with { Elevated = false }
            })
                Require(!AdministratorRestart.MatchesPeer(child, changed, current, true), "A mismatched administrator peer was accepted");
            Require(!AdministratorRestart.MatchesPeer(child with
            {
                Started = 0
            }, child with
            {
                Started = 0
            }, current, true), "An unbound process lifetime was accepted");
        });
        test("administrator handoff accepts only the exact executable image", () =>
        {
            var image = Path.Combine(Path.GetTempPath(), "overlay-admin-fixture", "ApolloOverlay.exe");
            Require(AdministratorRestart.SameExecutable(image.ToUpperInvariant(), image), "Windows path casing changed ownership");
            Require(!AdministratorRestart.SameExecutable(null, image)
                && !AdministratorRestart.SameExecutable("ApolloOverlay.exe", image)
                && !AdministratorRestart.SameExecutable(Path.Combine(Path.GetDirectoryName(image)!, "other", "ApolloOverlay.exe"), image), "A foreign or unqualified image was accepted");
        });
        test("administrator bootstrap validates its nonce and private argument envelope before native operations", () =>
        {
            var peer = new AutomationPipeSecurity.Peer("S-1-5-21-100", "S-1-5-5-100-200", 1, 40, 1000);
            var request = new AdministratorRestart.Request(peer, new string('A', 64));
            Require(AdministratorRestart.TryDecode(AdministratorRestart.Encode(request), out var decoded)
                && decoded == request, "A complete handshake could not round-trip");
            foreach (var invalid in new[] { "", "not-base64", Convert.ToBase64String("null"u8), new string('A', 4097), AdministratorRestart.Encode(request with { Nonce = @"Global\arbitrary" }), AdministratorRestart.Encode(request with { Parent = peer with { Started = 0 } }) })
                Require(!AdministratorRestart.TryDecode(invalid, out _), "A malformed handoff was accepted");
            string[] ordinary = ["--startup"];
            Require(AdministratorRestart.Bootstrap(ref ordinary) is null && ordinary.SequenceEqual(["--startup"]), "Ordinary startup entered administrator bootstrap");
            string[] incomplete = [AdministratorRestart.HostArgument];
            Require(AdministratorRestart.Bootstrap(ref incomplete) == 2, "An incomplete internal request reached native operations");
            string[] injected = [AdministratorRestart.HostArgument, AdministratorRestart.Encode(request), "--show"];
            Require(AdministratorRestart.Bootstrap(ref injected) == 2, "Extra internal arguments were accepted");
        });
        test("cancelled administrator restart never requests UAC or closes the current app", () =>
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var result = AdministratorRestart.RequestAsync(cancellation.Token).GetAwaiter().GetResult();
            Require(result.Kind == ResultKind.Cancelled, "Pre-cancelled elevation was attempted");
        });
        test("startup task principal accepts only the current SID", () =>
        {
            const string sid = "S-1-5-21-100";
            Require(ScheduledStartup.PrincipalMatches(sid, sid), "Current task principal was rejected");
            Require(!ScheduledStartup.PrincipalMatches("S-1-5-21-101", sid), "Another user's task principal was accepted");
        });
    }

    private static void Require(bool value, string message)
    {
        if (!value)
            throw new Exception(message);
    }
}
