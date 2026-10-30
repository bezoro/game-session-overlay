namespace ApolloOverlay;

public sealed record CapturePixels(int Width, int Height, byte[] Bgra);
public sealed record ScreenCaptureItem(int Version, string Id, string Title, string GameKey, DateTimeOffset Created, int Width, int Height, long Bytes);
public sealed record ScreenCaptureResult(ActionResult Result, ScreenCaptureItem? Item);
public interface IScreenCaptureBackend
{
    CapturePixels Capture(DisplayRef display);
}
