using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ApolloOverlay;

public sealed class ScreenCaptures
{
    private readonly IScreenCaptureBackend backend;
    private readonly SemaphoreSlim operation = new(1, 1);
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
    public string DirectoryPath
    {
        get;
    }

    public ScreenCaptures(string dataRoot, IScreenCaptureBackend? backend = null)
    {
        DirectoryPath = Path.Combine(dataRoot, "Capturas");
        this.backend = backend ?? new DesktopCaptureBackend();
    }

    private static bool ValidId(string? id) => id is { Length: < 100 } && Regex.IsMatch(id, "^gso-[0-9]{17}-[a-f0-9]{32}$");
    private string ItemPath(string id, string extension)
    {
        if (!ValidId(id))
            throw new InvalidDataException("Invalid capture identity.");
        return Path.Combine(DirectoryPath, id + extension);
    }

    private void CheckRoot(bool create)
    {
        if (create)
            Directory.CreateDirectory(DirectoryPath);
        if (Directory.Exists(DirectoryPath))
            LocalUpdateHistory.RejectLink(DirectoryPath);
    }

    private static string Text(string? value, int limit) => new((value ?? "").Where(c => !char.IsControl(c)).Take(limit).ToArray());
    public async Task<ScreenCaptureResult> CaptureAsync(DisplayRef display, string title, string gameKey, CancellationToken cancellation = default)
    {
        if (!await operation.WaitAsync(0, cancellation))
            return new(new(ResultKind.Unavailable, "A capture is already in progress."), null);
        try
        {
            return await Task.Run(() =>
            {
                cancellation.ThrowIfCancellationRequested();
                CheckRoot(true);
                var pixels = backend.Capture(display);
                if (pixels.Width is < 1 or > 16384
                    || pixels.Height is < 1 or > 16384
                    || (long)pixels.Width * pixels.Height > 40000000
                    || pixels.Bgra.LongLength != (long)pixels.Width * pixels.Height * 4)
                    throw new InvalidDataException("The image exceeds the supported size.");
                cancellation.ThrowIfCancellationRequested();
                var created = DateTimeOffset.UtcNow;
                var id = "gso-" + created.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
                var file = ItemPath(id, ".png");
                var temporary = file + ".new";
                try
                {
                    var bitmap = BitmapSource.Create(pixels.Width, pixels.Height, 96, 96, PixelFormats.Bgra32, null, pixels.Bgra, checked(pixels.Width * 4));
                    bitmap.Freeze();
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        encoder.Save(output);
                        output.Flush(true);
                    }

                    cancellation.ThrowIfCancellationRequested();
                    File.Move(temporary, file);
                    var item = new ScreenCaptureItem(1, id, Text(title, 180), Text(gameKey, 120), created, pixels.Width, pixels.Height, new FileInfo(file).Length);
                    try
                    {
                        LocalUpdateHistory.AtomicBytes(ItemPath(id, ".json"), JsonSerializer.SerializeToUtf8Bytes(item, Json));
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        return new ScreenCaptureResult(new(ResultKind.Partial, "The image was saved but could not be added to the gallery. Open the captures folder."), item);
                    }

                    return new ScreenCaptureResult(new(ResultKind.Completed, "Image saved to the captures folder."), item);
                }
                finally
                {
                    if (File.Exists(temporary))
                        File.Delete(temporary);
                }
            }, cancellation);
        }
        catch (OperationCanceledException)
        {
            return new(new(ResultKind.Cancelled, "Capture cancelled before saving."), null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or InvalidDataException or ArgumentException or NotSupportedException)
        {
            return new(new(ResultKind.Unavailable, "Could not save the image. Check the monitor, permissions and available disk space."), null);
        }
        finally
        {
            operation.Release();
        }
    }

    public Task<IReadOnlyList<ScreenCaptureItem>> ListAsync(CancellationToken cancellation = default) => Task.Run<IReadOnlyList<ScreenCaptureItem>>(() =>
    {
        CheckRoot(false);
        if (!Directory.Exists(DirectoryPath))
            return [];
        var result = new List<ScreenCaptureItem>();
        foreach (var file in Directory.EnumerateFiles(DirectoryPath, "gso-*.json").Take(10000).OrderDescending(StringComparer.Ordinal).Take(96))
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                LocalUpdateHistory.RejectLink(file);
                if (new FileInfo(file).Length > 16384)
                    continue;
                var item = JsonSerializer.Deserialize<ScreenCaptureItem>(File.ReadAllBytes(file), Json);
                if (item is null
                    || item.Version != 1
                    || !ValidId(item.Id)
                    || Path.GetFileNameWithoutExtension(file) != item.Id
                    || item.Title is null
                    || item.Title.Length > 180
                    || item.GameKey is null
                    || item.GameKey.Length > 120
                    || item.Width is < 1 or > 16384
                    || item.Height is < 1 or > 16384
                    || (long)item.Width * item.Height > 40000000)
                    continue;
                var image = ItemPath(item.Id, ".png");
                if (!File.Exists(image))
                    continue;
                LocalUpdateHistory.RejectLink(image);
                if (new FileInfo(image).Length != item.Bytes || item.Bytes is <= 0 or > 268435456)
                    continue;
                result.Add(item);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
            {
            }
        }

        return Array.AsReadOnly(result.OrderByDescending(item => item.Created).ToArray());
    }, cancellation);
    public Task<BitmapSource> ReadImageAsync(ScreenCaptureItem item, int maximumWidth = 900, CancellationToken cancellation = default) => Task.Run<BitmapSource>(() =>
    {
        cancellation.ThrowIfCancellationRequested();
        CheckRoot(false);
        var path = ItemPath(item.Id, ".png");
        LocalUpdateHistory.RejectLink(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> header = stackalloc byte[24];
        input.ReadExactly(header);
        if (!header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            || System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header[16..20]) != item.Width
            || System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header[20..24]) != item.Height)
            throw new InvalidDataException("The image does not match the selected capture.");
        input.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = Math.Min(item.Width, Math.Clamp(maximumWidth, 64, 1920));
        image.StreamSource = input;
        image.EndInit();
        image.Freeze();
        return image;
    }, cancellation);
}
