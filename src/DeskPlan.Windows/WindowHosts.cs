using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DeskPlan.Core;
using Forms = System.Windows.Forms;

namespace DeskPlan.Windows;

public interface IWindowHost
{
    string Name { get; }
    IntPtr ParentHandle { get; }
    bool IsHealthy(IntPtr hwnd);
    void Attach(Window window);
}

public sealed class DesktopWindowHost : IWindowHost
{
    public string Name => "桌面模式";
    public IntPtr ParentHandle { get; private set; }
    public bool IsHealthy(IntPtr hwnd) => NativeMethods.IsWindow(hwnd) && NativeMethods.IsWindow(ParentHandle)
        && NativeMethods.GetParent(hwnd) == ParentHandle;
    public void Attach(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var progman = NativeMethods.FindWindow("Progman", null);
        if (progman == IntPtr.Zero) throw new InvalidOperationException("未找到 Windows 桌面进程。");
        NativeMethods.SendMessageTimeout(progman, 0x052C, UIntPtr.Zero, IntPtr.Zero, 2, 800, out _);
        IntPtr worker = IntPtr.Zero;
        NativeMethods.EnumWindows((top, _) =>
        {
            if (NativeMethods.FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
            {
                worker = NativeMethods.FindWindowEx(IntPtr.Zero, top, "WorkerW", null);
                if (worker != IntPtr.Zero) return false;
            }
            return true;
        }, IntPtr.Zero);
        // Recent Explorer versions can place the wallpaper WorkerW inside Progman.
        if (worker == IntPtr.Zero) worker = NativeMethods.FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
        if (worker == IntPtr.Zero || !NativeMethods.IsWindowVisible(worker))
            throw new InvalidOperationException("当前 Explorer 没有可用的壁纸窗口。");
        var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlStyle).ToInt64();
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GwlStyle, new IntPtr((style & ~NativeMethods.WsPopup) | NativeMethods.WsChild));
        Marshal.SetLastPInvokeError(0);
        var previous = NativeMethods.SetParent(hwnd, worker);
        var error = Marshal.GetLastPInvokeError();
        if (previous == IntPtr.Zero && error != 0) throw new Win32Exception(error, "桌面嵌入失败。");
        if (NativeMethods.GetParent(hwnd) != worker) throw new InvalidOperationException("桌面嵌入未生效。");
        ParentHandle = worker;
        WindowGeometry.HideFromSwitcher(hwnd);
    }
}

public sealed class FloatingWindowHost(bool topmost = true) : IWindowHost
{
    public string Name => topmost ? "悬浮模式" : "普通窗口 · 安全回退";
    public IntPtr ParentHandle => IntPtr.Zero;
    public bool IsHealthy(IntPtr hwnd) => NativeMethods.IsWindow(hwnd);
    public void Attach(Window window)
    {
        window.Topmost = topmost;
        var hwnd = new WindowInteropHelper(window).Handle;
        WindowGeometry.HideFromSwitcher(hwnd);
        NativeMethods.SetWindowPos(hwnd, new IntPtr(topmost ? -1 : -2), 0, 0, 0, 0, 0x0001 | 0x0002 | NativeMethods.SwpNoActivate);
    }
}

/// <summary>An explicit open request needs an independently activatable window, not an Explorer child.</summary>
public sealed class RevealedWindowHost : IWindowHost
{
    public string Name => "桌面模式 · 已唤出";
    public IntPtr ParentHandle => IntPtr.Zero;
    public bool IsHealthy(IntPtr hwnd) => NativeMethods.IsWindow(hwnd) && NativeMethods.GetParent(hwnd) == IntPtr.Zero;
    public void Attach(Window window)
    {
        window.Topmost = false;
        var hwnd = new WindowInteropHelper(window).Handle;
        var ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle).ToInt64();
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GwlExStyle,
            new IntPtr((ex & ~NativeMethods.WsExToolWindow) | NativeMethods.WsExAppWindow));
    }
}

public static class WindowGeometry
{
    public static void HideFromSwitcher(IntPtr hwnd)
    {
        var ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle).ToInt64();
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GwlExStyle, new IntPtr((ex | NativeMethods.WsExToolWindow) & ~NativeMethods.WsExAppWindow));
    }
    public static double Scale(IntPtr hwnd) => Math.Max(96, NativeMethods.GetDpiForWindow(hwnd)) / 96.0;
    // Positions are physical virtual-screen pixels; dimensions are WPF device-independent pixels.
    public static PlannerSettings Clamp(PlannerSettings settings, double scale)
    {
        var x = double.IsFinite(settings.Left) ? settings.Left : 60;
        var y = double.IsFinite(settings.Top) ? settings.Top : 80;
        var desired = new System.Drawing.Rectangle((int)x, (int)y, 1, 1);
        var screen = Forms.Screen.AllScreens.OrderBy(s => Distance(s.WorkingArea, desired)).First();
        var area = screen.WorkingArea;
        var width = Math.Clamp(settings.Width, 320, Math.Max(320, area.Width / scale));
        var height = Math.Clamp(settings.Height, 420, Math.Max(420, area.Height / scale));
        return settings with
        {
            Width = width, Height = height,
            Left = Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - width * scale)),
            Top = Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - height * scale))
        };
    }
    private static double Distance(System.Drawing.Rectangle area, System.Drawing.Rectangle point)
    {
        var dx = Math.Max(Math.Max(area.Left - point.X, 0), point.X - area.Right);
        var dy = Math.Max(Math.Max(area.Top - point.Y, 0), point.Y - area.Bottom);
        return (double)dx * dx + (double)dy * dy;
    }
    public static void Place(Window window, IWindowHost host, PlannerSettings bounds)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var scale = Scale(hwnd);
        var safe = Clamp(bounds, scale);
        window.Width = safe.Width; window.Height = safe.Height;
        var point = new NativeMethods.Point { X = (int)safe.Left, Y = (int)safe.Top };
        if (host.ParentHandle != IntPtr.Zero) NativeMethods.ScreenToClient(host.ParentHandle, ref point);
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, point.X, point.Y, (int)Math.Round(safe.Width * scale),
            (int)Math.Round(safe.Height * scale), NativeMethods.SwpNoActivate | NativeMethods.SwpNoZOrder | NativeMethods.SwpFrameChanged);
    }
    public static PlannerSettings Capture(Window window, PlannerSettings settings)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return settings;
        return settings with { Left = rect.Left, Top = rect.Top, Width = window.ActualWidth, Height = window.ActualHeight };
    }
}
