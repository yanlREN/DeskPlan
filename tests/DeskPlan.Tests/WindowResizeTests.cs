using DeskPlan.Core;
using Xunit;

namespace DeskPlan.Tests;

public sealed class WindowResizeTests
{
    private static readonly ResizeEdges[] Directions =
    [ResizeEdges.Left, ResizeEdges.Right, ResizeEdges.Top, ResizeEdges.Bottom,
        ResizeEdges.Left | ResizeEdges.Top, ResizeEdges.Right | ResizeEdges.Top,
        ResizeEdges.Left | ResizeEdges.Bottom, ResizeEdges.Right | ResizeEdges.Bottom];

    [Theory]
    [InlineData(1.0)] [InlineData(1.25)] [InlineData(1.5)] [InlineData(2.0)]
    public void AllEightDirectionsFollowThePointerAtDifferentDpi(double scale)
    {
        var start = new WindowBounds(400 * scale, 300 * scale, 500 * scale, 500 * scale);
        var area = new WindowBounds(0, 0, 1920 * scale, 1080 * scale);
        foreach (var edges in Directions)
        {
            var dx = (edges.HasFlag(ResizeEdges.Left) ? -40 : 40) * scale;
            var dy = (edges.HasFlag(ResizeEdges.Top) ? -30 : 30) * scale;
            var next = WindowResize.Calculate(start, edges, dx, dy, 320 * scale, 420 * scale, area);
            Assert.Equal(edges.HasFlag(ResizeEdges.Left) ? 360 : 400, next.Left / scale, 6);
            Assert.Equal(edges.HasFlag(ResizeEdges.Top) ? 270 : 300, next.Top / scale, 6);
            Assert.Equal(edges.HasFlag(ResizeEdges.Left) || edges.HasFlag(ResizeEdges.Right) ? 540 : 500, next.Width / scale, 6);
            Assert.Equal(edges.HasFlag(ResizeEdges.Top) || edges.HasFlag(ResizeEdges.Bottom) ? 530 : 500, next.Height / scale, 6);
        }
    }

    [Theory]
    [InlineData(ResizeEdges.Left)] [InlineData(ResizeEdges.Right)]
    [InlineData(ResizeEdges.Top)] [InlineData(ResizeEdges.Bottom)]
    [InlineData(ResizeEdges.Left | ResizeEdges.Top)] [InlineData(ResizeEdges.Right | ResizeEdges.Top)]
    [InlineData(ResizeEdges.Left | ResizeEdges.Bottom)] [InlineData(ResizeEdges.Right | ResizeEdges.Bottom)]
    public void MinimumSizeKeepsOppositeEdgesAnchored(ResizeEdges edges)
    {
        var start = new WindowBounds(400, 300, 500, 500);
        var next = WindowResize.Calculate(start, edges,
            edges.HasFlag(ResizeEdges.Left) ? 10000 : -10000,
            edges.HasFlag(ResizeEdges.Top) ? 10000 : -10000, 320, 420, new(0, 0, 1920, 1080));
        Assert.Equal(edges.HasFlag(ResizeEdges.Left) || edges.HasFlag(ResizeEdges.Right) ? 320 : 500, next.Width);
        Assert.Equal(edges.HasFlag(ResizeEdges.Top) || edges.HasFlag(ResizeEdges.Bottom) ? 420 : 500, next.Height);
        AssertAnchors(start, next, edges);
    }

    [Theory]
    [InlineData(ResizeEdges.Left)] [InlineData(ResizeEdges.Right)]
    [InlineData(ResizeEdges.Top)] [InlineData(ResizeEdges.Bottom)]
    [InlineData(ResizeEdges.Left | ResizeEdges.Top)] [InlineData(ResizeEdges.Right | ResizeEdges.Top)]
    [InlineData(ResizeEdges.Left | ResizeEdges.Bottom)] [InlineData(ResizeEdges.Right | ResizeEdges.Bottom)]
    public void WorkAreaLimitsSupportNegativeMonitorCoordinates(ResizeEdges edges)
    {
        var start = new WindowBounds(-1600, 100, 500, 600);
        var area = new WindowBounds(-1920, 0, 1920, 1040);
        var next = WindowResize.Calculate(start, edges,
            edges.HasFlag(ResizeEdges.Left) ? -10000 : 10000,
            edges.HasFlag(ResizeEdges.Top) ? -10000 : 10000, 320, 420, area);
        Assert.InRange(next.Left, area.Left, area.Right);
        Assert.InRange(next.Right, area.Left, area.Right);
        Assert.InRange(next.Top, area.Top, area.Bottom);
        Assert.InRange(next.Bottom, area.Top, area.Bottom);
        AssertAnchors(start, next, edges);
    }

    [Fact]
    public void WorkAreaSmallerThanMinimumDoesNotInvertClampRange()
    {
        var start = new WindowBounds(0, 0, 320, 420);
        foreach (var edges in Directions)
            Assert.Equal(start, WindowResize.Calculate(start, edges, 100, 100, 320, 420, new(0, 0, 200, 300)));
    }

    private static void AssertAnchors(WindowBounds start, WindowBounds next, ResizeEdges edges)
    {
        if (!edges.HasFlag(ResizeEdges.Left)) Assert.Equal(start.Left, next.Left);
        if (!edges.HasFlag(ResizeEdges.Right)) Assert.Equal(start.Right, next.Right);
        if (!edges.HasFlag(ResizeEdges.Top)) Assert.Equal(start.Top, next.Top);
        if (!edges.HasFlag(ResizeEdges.Bottom)) Assert.Equal(start.Bottom, next.Bottom);
    }
}
