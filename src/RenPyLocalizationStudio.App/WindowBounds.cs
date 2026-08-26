namespace RenPyLocalizationStudio.App;

internal readonly record struct PixelRectangle(int Left, int Top, int Right, int Bottom)
{
    public int Width => Math.Max(0, Right - Left);

    public int Height => Math.Max(0, Bottom - Top);
}

internal readonly record struct PixelPoint(int X, int Y);

internal readonly record struct PixelSize(int Width, int Height);

internal readonly record struct MaximizedBounds(PixelPoint Position, PixelSize Size);

internal static class MaximizedWindowBounds
{
    internal static MaximizedBounds Calculate(PixelRectangle monitor, PixelRectangle workArea)
    {
        var position = new PixelPoint(
            workArea.Left - monitor.Left,
            workArea.Top - monitor.Top);
        var size = new PixelSize(workArea.Width, workArea.Height);
        return new MaximizedBounds(position, size);
    }
}
