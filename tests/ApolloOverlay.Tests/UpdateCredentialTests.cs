using System.ComponentModel;
using System.IO;
using System.Text;
using ApolloOverlay;

internal static class UpdateCredentialTests
{
    internal static void Run(Action<string, Action> test)
    {
        test("update account credential uses current-user DPAPI without plaintext on disk", () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "OverlayCredentialTests", Guid.NewGuid().ToString("N"));
            var path = Path.Combine(folder, "credential.bin");
            try
            {
                var store = new UpdateCredentialStore(path);
                const string token = "owned_test_credential_without_real_access";
                Require(store.Read() is null, "Missing account became a credential");
                store.Write(token);
                Require(store.Read() == token, "Current user could not read protected credential");
                Require(!Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains(token), "Credential was stored as plaintext");
                store.Write(token + "_replacement");
                Require(store.Read() == token + "_replacement", "Atomic replacement lost credential");
                store.Delete();
                Require(store.Read() is null, "Disconnect retained the credential");
            }
            finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        });
        test("update account rejects corrupted protected data instead of treating it as a token", () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "OverlayCredentialTests", Guid.NewGuid().ToString("N"));
            var path = Path.Combine(folder, "credential.bin");
            try
            {
                var store = new UpdateCredentialStore(path); store.Write("owned_test_credential_without_real_access");
                var bytes = File.ReadAllBytes(path); bytes[^1] ^= 0x5a; File.WriteAllBytes(path, bytes);
                try { _ = store.Read(); throw new Exception("Corrupted ciphertext was accepted"); }
                catch (Win32Exception) { }
                store.Delete();
            }
            finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        });
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
