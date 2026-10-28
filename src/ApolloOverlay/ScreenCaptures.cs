using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ApolloOverlay;

public sealed record CapturePixels(int Width, int Height, byte[] Bgra);
public sealed record ScreenCaptureItem(int Version, string Id, string Title, string GameKey, DateTimeOffset Created,
    int Width, int Height, long Bytes);
public sealed record ScreenCaptureResult(ActionResult Result, ScreenCaptureItem? Item);
public interface IScreenCaptureBackend { CapturePixels Capture(DisplayRef display); }

public sealed class ScreenCaptures
{
    private readonly IScreenCaptureBackend backend;
    private readonly SemaphoreSlim operation = new(1, 1);
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    public string DirectoryPath { get; }
    public ScreenCaptures(string dataRoot, IScreenCaptureBackend? backend = null)
    { DirectoryPath = Path.Combine(dataRoot, "Capturas"); this.backend = backend ?? new DesktopCaptureBackend(); }
    private static bool ValidId(string? id) => id is { Length: < 100 } && Regex.IsMatch(id, "^gso-[0-9]{17}-[a-f0-9]{32}$");
    private string ItemPath(string id, string extension)
    {
        if (!ValidId(id)) throw new InvalidDataException("Identificação da captura inválida.");
        return Path.Combine(DirectoryPath, id + extension);
    }
    private void CheckRoot(bool create)
    {
        if (create) Directory.CreateDirectory(DirectoryPath);
        if (Directory.Exists(DirectoryPath)) LocalUpdateHistory.RejectLink(DirectoryPath);
    }
    private static string Text(string? value, int limit) => new((value ?? "").Where(c => !char.IsControl(c)).Take(limit).ToArray());
    public async Task<ScreenCaptureResult> CaptureAsync(DisplayRef display, string title, string gameKey, CancellationToken cancellation = default)
    {
        if (!await operation.WaitAsync(0, cancellation)) return new(new(ResultKind.Unavailable, "Uma captura já está em andamento."), null);
        try
        {
            return await Task.Run(() =>
            {
                cancellation.ThrowIfCancellationRequested(); CheckRoot(true);
                var pixels = backend.Capture(display);
                if (pixels.Width is < 1 or > 16384 || pixels.Height is < 1 or > 16384 || (long)pixels.Width * pixels.Height > 40000000 ||
                    pixels.Bgra.LongLength != (long)pixels.Width * pixels.Height * 4) throw new InvalidDataException("A imagem ultrapassa o tamanho suportado.");
                cancellation.ThrowIfCancellationRequested();
                var created = DateTimeOffset.UtcNow; var id = "gso-" + created.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
                var file = ItemPath(id, ".png"); var temporary = file + ".new";
                try
                {
                    var bitmap = BitmapSource.Create(pixels.Width, pixels.Height, 96, 96, PixelFormats.Bgra32, null, pixels.Bgra, checked(pixels.Width * 4)); bitmap.Freeze();
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { encoder.Save(output); output.Flush(true); }
                    cancellation.ThrowIfCancellationRequested(); File.Move(temporary, file);
                    var item = new ScreenCaptureItem(1, id, Text(title, 180), Text(gameKey, 120), created, pixels.Width, pixels.Height, new FileInfo(file).Length);
                    try { LocalUpdateHistory.AtomicBytes(ItemPath(id, ".json"), JsonSerializer.SerializeToUtf8Bytes(item, Json)); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return new ScreenCaptureResult(new(ResultKind.Partial, "A imagem foi salva, mas não foi possível adicioná-la à galeria. Abra a pasta de capturas."), item); }
                    return new ScreenCaptureResult(new(ResultKind.Completed, "Imagem salva na pasta Capturas."), item);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }, cancellation);
        }
        catch (OperationCanceledException) { return new(new(ResultKind.Cancelled, "Captura cancelada antes de salvar."), null); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or InvalidDataException or ArgumentException or NotSupportedException)
        { return new(new(ResultKind.Unavailable, "Não foi possível salvar a imagem. Confira o monitor, as permissões e o espaço em disco."), null); }
        finally { operation.Release(); }
    }
    public Task<IReadOnlyList<ScreenCaptureItem>> ListAsync(CancellationToken cancellation = default) => Task.Run<IReadOnlyList<ScreenCaptureItem>>(() =>
    {
        CheckRoot(false); if (!Directory.Exists(DirectoryPath)) return [];
        var result = new List<ScreenCaptureItem>();
        foreach (var file in Directory.EnumerateFiles(DirectoryPath, "gso-*.json").Take(10000).OrderDescending(StringComparer.Ordinal).Take(96))
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                LocalUpdateHistory.RejectLink(file); if (new FileInfo(file).Length > 16384) continue;
                var item = JsonSerializer.Deserialize<ScreenCaptureItem>(File.ReadAllBytes(file), Json);
                if (item is null || item.Version != 1 || !ValidId(item.Id) || Path.GetFileNameWithoutExtension(file) != item.Id || item.Title is null || item.Title.Length > 180 ||
                    item.GameKey is null || item.GameKey.Length > 120 || item.Width is < 1 or > 16384 || item.Height is < 1 or > 16384 || (long)item.Width * item.Height > 40000000) continue;
                var image = ItemPath(item.Id, ".png"); if (!File.Exists(image)) continue; LocalUpdateHistory.RejectLink(image);
                if (new FileInfo(image).Length != item.Bytes || item.Bytes is <= 0 or > 268435456) continue;
                result.Add(item);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException) { }
        }
        return Array.AsReadOnly(result.OrderByDescending(item => item.Created).ToArray());
    }, cancellation);
    public Task<BitmapSource> ReadImageAsync(ScreenCaptureItem item, int maximumWidth = 900, CancellationToken cancellation = default) => Task.Run<BitmapSource>(() =>
    {
        cancellation.ThrowIfCancellationRequested(); CheckRoot(false); var path = ItemPath(item.Id, ".png"); LocalUpdateHistory.RejectLink(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> header = stackalloc byte[24]; input.ReadExactly(header);
        if (!header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
            System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header[16..20]) != item.Width || System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header[20..24]) != item.Height)
            throw new InvalidDataException("A imagem não corresponde à captura selecionada.");
        input.Position = 0; var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = Math.Min(item.Width,Math.Clamp(maximumWidth, 64, 1920)); image.StreamSource = input; image.EndInit(); image.Freeze(); return image;
    }, cancellation);
}

internal sealed class DesktopCaptureBackend : IScreenCaptureBackend
{
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    {
        public uint Size; public int Width, Height; public ushort Planes, Bits; public uint Compression, ImageSize;
        public int XPels, YPels; public uint Used, Important;
    }
    public CapturePixels Capture(DisplayRef display)
    {
        if (display.Width is < 1 or > 16384 || display.Height is < 1 or > 16384 || (long)display.Width * display.Height > 40000000) throw new ArgumentException("Monitor inválido.");
        var screen = GetDC(0); if (screen == 0) throw new Win32Exception();
        nint memory = 0, bitmap = 0, previous = 0;
        try
        {
            memory = CreateCompatibleDC(screen); if (memory == 0) throw new Win32Exception();
            var info = new BitmapInfo { Size = 40, Width = display.Width, Height = -display.Height, Planes = 1, Bits = 32 };
            bitmap = CreateDIBSection(screen, ref info, 0, out var bits, 0, 0); if (bitmap == 0 || bits == 0) throw new Win32Exception();
            previous = SelectObject(memory, bitmap); if (previous is 0 or -1) throw new Win32Exception();
            if (!BitBlt(memory, 0, 0, display.Width, display.Height, screen, display.X, display.Y, 0x40CC0020)) throw new Win32Exception();
            var bytes = new byte[checked(display.Width * display.Height * 4)]; Marshal.Copy(bits, bytes, 0, bytes.Length);
            for (var index = 3; index < bytes.Length; index += 4) bytes[index] = 255;
            return new(display.Width, display.Height, bytes);
        }
        finally { if (previous is not (0 or -1)) SelectObject(memory, previous); if (bitmap != 0) DeleteObject(bitmap); if (memory != 0) DeleteDC(memory); ReleaseDC(0, screen); }
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BitBlt(nint destination, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);
}
