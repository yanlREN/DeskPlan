namespace DeskPlan.Core;

[Flags]
public enum ResizeEdges { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }

/// <summary>Screen coordinates and sizes in physical pixels, including negative monitor origins.</summary>
public readonly record struct WindowBounds(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
}

public static class WindowResize
{
    /// <summary>Move only the selected edges, keeping the opposite edges anchored at size/screen limits.</summary>
    public static WindowBounds Calculate(WindowBounds start, ResizeEdges edges, double dx, double dy,
        double minWidth, double minHeight, WindowBounds workArea)
    {
        var left = start.Left; var right = start.Right;
        var top = start.Top; var bottom = start.Bottom;
        if (edges.HasFlag(ResizeEdges.Left))
            left = Math.Clamp(start.Left + dx, Math.Min(workArea.Left, right - minWidth), right - minWidth);
        if (edges.HasFlag(ResizeEdges.Right))
            right = Math.Clamp(start.Right + dx, left + minWidth, Math.Max(workArea.Right, left + minWidth));
        if (edges.HasFlag(ResizeEdges.Top))
            top = Math.Clamp(start.Top + dy, Math.Min(workArea.Top, bottom - minHeight), bottom - minHeight);
        if (edges.HasFlag(ResizeEdges.Bottom))
            bottom = Math.Clamp(start.Bottom + dy, top + minHeight, Math.Max(workArea.Bottom, top + minHeight));
        return new(left, top, right - left, bottom - top);
    }
}
