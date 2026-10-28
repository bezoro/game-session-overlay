using System.IO;
using System.Text.Json;
using ApolloOverlay;

internal static class ManagedInstallTests
{
    private static void Require(bool value, string reason) { if (!value) throw new Exception(reason); }
    private static void Refuses(Action operation)
    {
        try { operation(); }
        catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException) { return; }
        throw new Exception("Unsafe operation was accepted");
    }
    internal static void Run(Action<string, Action> test)
    {
        test("managed install refuses path traversal rooted paths and reserved IDs", () =>
        {
            foreach (var id in new[] { "../outside", "..", "x/../y", "CON", "COM1", "C:\\outside", "trailing.", "" }) Refuses(() => ManagedInstallStore.SafeId(id));
            foreach (var file in new[] { "../outside", "/outside", "a/../../outside", "a\\b", "a:b", "a//b" }) Refuses(() => ManagedInstallStore.Child(Path.GetTempPath(), file));
        });
        test("managed immutable import verifies every file and refuses changed source", () =>
        {
            using var f = new Fixture();
            var source = f.Source("a"); var inventory = ManagedInstallStore.Inventory(source);
            File.AppendAllText(Path.Combine(source, "ApolloOverlay.dll"), "changed");
            Refuses(() => f.Store.Import(source, "a", "0.2.0-a", "commit-a", inventory));
            Require(!Directory.Exists(f.Store.VersionPath("a")) && f.Store.Load().Current is null, "Changed payload was committed");
        });
        test("managed incomplete package and identity mismatch do not replace active version", () =>
        {
            using var f = new Fixture(); var a = f.Import("a"); f.Store.Activate(a, false, _ => true);
            var b = f.Source("b"); File.Delete(Path.Combine(b, "ApolloOverlay.runtimeconfig.json"));
            Refuses(() => f.Store.Import(b, "b", "0.2.0-b", "commit-b"));
            Refuses(() => f.Store.Import(f.Source("c"), "d", "0.2.0-d", "commit-d"));
            Require(f.Store.Load().Current == "a", "Refused package changed active pointer");
        });
        test("managed activation success retains previous immutable payload and history", () =>
        {
            using var f = new Fixture(); var a = f.Import("a"); var b = f.Import("b");
            Require(f.Store.Activate(a, false, _ => true), "First activation failed");
            Require(f.Store.Activate(b, false, _ => true), "Second activation failed");
            var state = f.Store.Load();
            Require(state.Current == "b" && state.Previous == "a" && !state.Pending, "Pointer did not commit only after health");
            Require(File.ReadAllText(Path.Combine(f.Store.VersionPath("a"), "ApolloOverlay.dll")) == "payload-a", "Previous payload overwritten");
            Require(f.Store.History().Count == 2 && f.Store.History().Single(x => x.Current).BuildId == "b", "Local rollback history lost");
        });
        test("managed failed startup restores previous pointer and exact settings bytes", () =>
        {
            using var f = new Fixture(); var a = f.Import("a"); var b = f.Import("b"); f.Store.Activate(a, false, _ => true);
            var before = "{\"Version\":3,\"SoundFeedbackEnabled\":true}"; File.WriteAllText(f.Settings, before);
            Require(!f.Store.Activate(b, false, _ => { File.WriteAllText(f.Settings, "{\"newSchema\":true}"); return false; }), "Failed startup reported success");
            Require(f.Store.Load().Current == "a" && !f.Store.Load().Pending, "Failed child left new pointer active");
            Require(File.ReadAllText(f.Settings) == before, "Failed candidate changed prior settings");
            Require(f.Store.History().Any(x => x.BuildId == "b"), "Failure evidence/history discarded");
            Require(f.Store.History().Single(x => x.BuildId == "b").LastStartFailed, "Failed build was presented as a healthy rollback choice");
        });
        test("managed explicit rollback restores matching settings and keeps newer settings backup", () =>
        {
            using var f = new Fixture(); var a = f.Import("a"); var b = f.Import("b"); f.Store.Activate(a, false, _ => true);
            File.WriteAllText(f.Settings, "{\"theme\":\"old\"}"); f.Store.Activate(b, false, _ => true);
            File.WriteAllText(f.Settings, "{\"schema\":99,\"theme\":\"new\"}");
            Require(f.Store.Activate(a, true, _ => true), "Rollback failed");
            Require(File.ReadAllText(f.Settings) == "{\"theme\":\"old\"}", "Old version received incompatible new settings");
            Require(File.ReadAllText(f.Settings + ".before-rollback").Contains("99"), "Newest settings were destroyed");
            Require(f.Store.Load().Current == "a" && f.Store.Load().Previous == "b", "Rollback has no forward recovery");
        });
        test("managed power loss journal is recovered before another launch", () =>
        {
            using var f = new Fixture(); var a = f.Import("a"); var b = f.Import("b"); f.Store.Activate(a, false, _ => true);
            File.WriteAllText(f.Settings, "{\"compatible\":true}"); f.Store.SnapshotSettings("a");
            ManagedInstallStore.AtomicWrite(f.Store.StatePath, new InstallState(1, "b", "a", true, [a, b]));
            File.WriteAllText(f.Settings, "{\"candidate\":true}");
            var restored = f.Store.RecoverPending();
            Require(restored.Current == "a" && !restored.Pending && File.ReadAllText(f.Settings).Contains("compatible"), "Interrupted activation was not rolled back");
        });
        test("managed corrupt pointer uses prior atomic journal without trusting arbitrary paths", () =>
        {
            using var f = new Fixture(); var a = f.Import("a"); var b = f.Import("b"); f.Store.Activate(a, false, _ => true); f.Store.Activate(b, false, _ => true);
            File.WriteAllText(f.Store.StatePath, "{truncated");
            Require(f.Store.RecoverPending().Current == "a", "Corrupt pointer did not recover from prior journal");
            ManagedInstallStore.AtomicWrite(f.Store.StatePath, new InstallState(1, "../outside", null, false, [a]));
            File.Delete(f.Store.StatePath + ".previous");
            Refuses(() => f.Store.Load());
        });
        test("managed same ID cannot overwrite another immutable version", () =>
        {
            using var f = new Fixture(); var a = f.Import("a"); f.Store.Activate(a, false, _ => true);
            var changed = f.Source("a"); File.WriteAllText(Path.Combine(changed, "ApolloOverlay.dll"), "different contents");
            Refuses(() => f.Store.Import(changed, "a", "0.2.0-a", "commit-a"));
            Require(File.ReadAllText(Path.Combine(f.Store.VersionPath("a"), "ApolloOverlay.dll")) == "payload-a", "Immutable files were replaced");
        });
        test("managed failed health exception recovers journal before propagating error", () =>
        {
            using var f = new Fixture(); var a = f.Import("a"); var b = f.Import("b"); f.Store.Activate(a, false, _ => true);
            try { f.Store.Activate(b, false, _ => throw new IOException("owned startup refusal")); throw new Exception("Expected startup error"); }
            catch (IOException) { }
            Require(f.Store.Load().Current == "a" && !f.Store.Load().Pending, "Exception left failed candidate active");
        });
        test("managed concurrent transaction cannot acquire the same installation lock", () =>
        {
            using var f = new Fixture(); using var lease = f.Store.Acquire(); Refuses(() => { using var next = f.Store.Acquire(); });
        });
        test("managed tampered installed file blocks activation and preserves current state", () =>
        {
            using var f = new Fixture(); var a = f.Import("a"); var b = f.Import("b"); f.Store.Activate(a, false, _ => true);
            File.WriteAllText(Path.Combine(f.Store.VersionPath("b"), "ApolloOverlay.dll"), "damaged");
            Refuses(() => f.Store.Activate(b, false, _ => true));
            Require(f.Store.Load().Current == "a", "Tampered version activated");
        });
        test("managed first installation health failure preserves pre-existing user settings", () =>
        {
            using var f = new Fixture(); var a = f.Import("a"); File.WriteAllText(f.Settings, "{\"existing\":true}");
            Require(!f.Store.Activate(a, false, _ => { File.WriteAllText(f.Settings, "{\"candidate\":true}"); return false; }), "First failed start was committed");
            Require(f.Store.Load().Current is null && File.ReadAllText(f.Settings) == "{\"existing\":true}", "First install lost prior settings");
        });
        test("managed failed rollback restores newer version and its settings", () =>
        {
            using var f = new Fixture(); var a = f.Import("a"); var b = f.Import("b"); f.Store.Activate(a, false, _ => true);
            File.WriteAllText(f.Settings, "{\"old\":true}"); f.Store.Activate(b, false, _ => true); File.WriteAllText(f.Settings, "{\"new\":true}");
            Require(!f.Store.Activate(a, true, _ => false), "Failed rollback reported success");
            Require(f.Store.Load().Current == "b" && File.ReadAllText(f.Settings) == "{\"new\":true}", "Failed rollback left incompatible settings");
            Require(f.Store.Activate(a, true, _ => true) && !f.Store.History().Single(x => x.BuildId == "a").LastStartFailed, "Successful retry did not clear health failure");
        });
        test("managed interrupted snapshot write restores the last committed matching bytes", () =>
        {
            using var f = new Fixture(); File.WriteAllText(f.Settings, "{\"committed\":true}"); f.Store.SnapshotSettings("a");
            ManagedInstallStore.AtomicBytes(Path.Combine(f.Store.Root, "settings-backups", "a", "settings.json"), System.Text.Encoding.UTF8.GetBytes("{\"uncommitted\":true}"));
            File.WriteAllText(f.Settings, "{\"candidate\":true}"); f.Store.RestoreSettings("a");
            Require(File.ReadAllText(f.Settings) == "{\"committed\":true}", "Snapshot ignored its committed checksum");
        });
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "OverlayManagedTests", Guid.NewGuid().ToString("N"));
        internal readonly ManagedInstallStore Store;
        internal string Settings => Path.Combine(directory, "user", "settings.json");
        internal Fixture() { Store = new(Path.Combine(directory, "install"), Settings); Directory.CreateDirectory(Path.GetDirectoryName(Settings)!); }
        internal string Source(string id)
        {
            var path = Path.Combine(directory, "source-" + id); Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "ApolloOverlay.exe"), "fake PE, never executed");
            File.WriteAllText(Path.Combine(path, "ApolloOverlay.dll"), "payload-" + id);
            File.WriteAllText(Path.Combine(path, "ApolloOverlay.runtimeconfig.json"), "{}");
            File.WriteAllText(Path.Combine(path, "build-info.json"), JsonSerializer.Serialize(new InstallIdentity("0.2.0-" + id, id, "commit-" + id), ManagedInstallStore.Json));
            return path;
        }
        internal InstalledBuild Import(string id) => Store.Import(Source(id), id, "0.2.0-" + id, "commit-" + id);
        public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
