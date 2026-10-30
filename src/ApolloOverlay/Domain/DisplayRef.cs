namespace ApolloOverlay;

public sealed record DisplayRef(string Id, int X, int Y, int Width, int Height, bool Primary)
{
    public static DisplayRef Choose(IReadOnlyList<DisplayRef> displays, string? selected) => displays.FirstOrDefault(d => d.Id == selected) ?? displays.FirstOrDefault(d => d.Primary) ?? displays[0];
    public (double X, double Y, double Width, double Height) Dips(double dpi) => (X * 96 / dpi, Y * 96 / dpi, Width * 96 / dpi, Height * 96 / dpi);
}
