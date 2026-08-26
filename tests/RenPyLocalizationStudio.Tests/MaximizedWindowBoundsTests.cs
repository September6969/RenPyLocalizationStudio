using RenPyLocalizationStudio.App;

namespace RenPyLocalizationStudio.Tests;

public sealed class MaximizedWindowBoundsTests
{
    [Fact]
    public void Calculate_UsesPrimaryMonitorWorkAreaAboveTaskbar()
    {
        var monitor = new PixelRectangle(0, 0, 1920, 1080);
        var workArea = new PixelRectangle(0, 0, 1920, 1032);

        var result = MaximizedWindowBounds.Calculate(monitor, workArea);

        Assert.Equal(new PixelPoint(0, 0), result.Position);
        Assert.Equal(new PixelSize(1920, 1032), result.Size);
    }

    [Fact]
    public void Calculate_UsesCoordinatesRelativeToSecondaryMonitor()
    {
        var monitor = new PixelRectangle(-2560, 0, 0, 1440);
        var workArea = new PixelRectangle(-2560, 40, 0, 1440);

        var result = MaximizedWindowBounds.Calculate(monitor, workArea);

        Assert.Equal(new PixelPoint(0, 40), result.Position);
        Assert.Equal(new PixelSize(2560, 1400), result.Size);
    }

    [Fact]
    public void Calculate_PreservesTaskbarOffsetsOnTwoEdges()
    {
        var monitor = new PixelRectangle(1920, -200, 3840, 880);
        var workArea = new PixelRectangle(1960, -200, 3840, 840);

        var result = MaximizedWindowBounds.Calculate(monitor, workArea);

        Assert.Equal(new PixelPoint(40, 0), result.Position);
        Assert.Equal(new PixelSize(1880, 1040), result.Size);
    }
}
