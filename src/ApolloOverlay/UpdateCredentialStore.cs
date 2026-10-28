using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ApolloOverlay;

/// <summary>Current-user DPAPI storage. No token is written to settings, logs or the installation directory.</summary>
public sealed class UpdateCredentialStore : IUpdateCredentialStore
{
    private readonly string path;
    public UpdateCredentialStore(string? path = null) => this.path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bezoro", "GameSessionOverlay", "github-update.bin");
    public string? Read()
    {
        if (!File.Exists(path)) return null;
        var info = new FileInfo(path);
        if (info.Length > 16384) throw new InvalidDataException("Credencial de atualização inválida. Conecte novamente.");
        var plain = Transform(File.ReadAllBytes(path), protect: false);
        try { return Encoding.UTF8.GetString(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public void Write(string token)
    {
        var plain = Encoding.UTF8.GetBytes(token);
        byte[] encrypted;
        try { encrypted = Transform(plain, protect: true); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, encrypted); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Delete() { if (File.Exists(path)) File.Delete(path); }

    private static byte[] Transform(byte[] value, bool protect)
    {
        var input = new Blob { Length = value.Length, Data = Marshal.AllocHGlobal(value.Length) };
        var output = default(Blob);
        try
        {
            Marshal.Copy(value, 0, input.Data, value.Length);
            var success = protect
                ? CryptProtectData(ref input, null, nint.Zero, nint.Zero, nint.Zero, 1, out output)
                : CryptUnprotectData(ref input, nint.Zero, nint.Zero, nint.Zero, nint.Zero, 1, out output);
            if (!success) throw new Win32Exception(Marshal.GetLastWin32Error(), "A credencial não pôde ser protegida ou lida pelo usuário atual do Windows.");
            if (output.Length is <= 0 or > 16384) throw new InvalidDataException("Credencial inválida.");
            var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally
        {
            for (var i = 0; i < input.Length; i++) Marshal.WriteByte(input.Data, i, 0);
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != nint.Zero) { for (var i = 0; i < output.Length; i++) Marshal.WriteByte(output.Data, i, 0); LocalFree(output.Data); }
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public nint Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptProtectData(ref Blob input, string? description, nint entropy, nint reserved, nint prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptUnprotectData(ref Blob input, nint description, nint entropy, nint reserved, nint prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
}
