using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeskPlan.App.Views;
using DeskPlan.Core;
using DeskPlan.Windows;

namespace DeskPlan.App;

/// <summary>Opt-in, isolated integration harness. Never touches the normal user database.</summary>
public static class SmokeTest
{
    public static async Task Run(AppController app, string folder)
    {
        var checks = new List<object>();
        var failures = new List<string>();
        void Check(string name, bool success, string? detail = null) { checks.Add(new { name, success, detail }); if (!success) failures.Add(name); }
        try
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            if (app.Store.Definitions().Count == 0)
            {
                var a = app.Service.Add(new("阅读文献，整理关键结论", today, new TimeOnly(9, 0), new TimeOnly(10, 0), "保持专注，记录一个值得继续研究的问题。"));
                app.Service.Progress(a.Id, 100);
                var b = app.Service.Add(new("整理实验数据", today, new TimeOnly(14, 0))); app.Service.Progress(b.Id, 65);
                app.Service.Add(new("散步 30 分钟", today, new TimeOnly(18, 0), Repeat: RepeatRule.Daily));
                for (var i = 1; i <= 12; i++)
                {
                    var t = app.Service.Add(new(i % 2 == 0 ? "完成研究笔记" : "复习与练习", today.AddDays(-i), new TimeOnly(10, 0)));
                    app.Service.Progress(t.Id, i % 3 == 0 ? 40 : 100);
                }
                app.Service.SaveReview(ReviewPeriod.Week, today, "本周成果：研究笔记与实验数据逐步完善。\n下一步：每天留出一段不被打扰的深度工作时间。");
            }
            app.ViewModel.Refresh(); await Task.Delay(500);
            Check("desktop-or-visible-fallback", app.Widget.IsVisible && app.Widget.Host.IsHealthy(new WindowInteropHelper(app.Widget).Handle), app.Widget.Host.Name + " / " + app.DesktopFailure);
            checks.Add(new { name = "desktop-embedded", success = app.Widget.Host is DesktopWindowHost, detail = app.DesktopFailure });
            Capture(app.Widget, Path.Combine(folder, "widget-light.png"));
            await VerifyResize(app, Check, "desktop");
            var ids = app.ViewModel.Tasks.Select(t => t.Id).ToArray();
            app.Widget.ScrollPosition = 70; await Task.Delay(50);
            var beforeScroll = app.Widget.ScrollPosition;
            app.SaveWindow(); var before = app.Settings;
            for (var i = 0; i < 6; i++) { app.SwitchMode(); await Task.Delay(120); }
            Check("six-mode-switches-one-widget", Application.Current.Windows.OfType<MainWindow>().Count(w => w.IsVisible) == 1);
            Check("switch-retains-tasks", app.ViewModel.Tasks.Select(t => t.Id).SequenceEqual(ids));
            Check("switch-retains-size", Math.Abs(app.Widget.ActualWidth - before.Width) < 2 && Math.Abs(app.Widget.ActualHeight - before.Height) < 2);
            Check("switch-retains-scroll", Math.Abs(app.Widget.ScrollPosition - beforeScroll) < 2);
            Check("switch-retains-position", Math.Abs(app.Settings.Left - before.Left) < 2 && Math.Abs(app.Settings.Top - before.Top) < 2);
            app.Show(); await Task.Delay(100);
            var revealedHwnd = new WindowInteropHelper(app.Widget).Handle;
            Check("explicit-open-reveals-desktop-as-toplevel", app.Widget.Host is RevealedWindowHost &&
                NativeMethods.GetParent(revealedHwnd) == IntPtr.Zero && NativeMethods.IsWindowVisible(revealedHwnd) && app.Widget.ShowInTaskbar);
            Check("explicit-open-keeps-desktop-preference", app.Store.Settings().Mode == DisplayMode.Desktop);
            // Exercise the actual title-bar Click handler from the state shown after opening the EXE.
            // IsVisible alone also returns true for an HWND buried in Explorer's wallpaper layer.
            var clickStates = new List<object>();
            var allClicksVisible = true;
            var clickWindow = app.Widget;
            var allClicksReuseWindow = true;
            var clickTaskState = app.ViewModel.Tasks.Select(t => (t.Id, t.Progress)).ToArray();
            for (var i = 1; i <= 30; i++)
            {
                var button = (Button)app.Widget.FindName("ModeButton");
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(80);
                var clickHwnd = new WindowInteropHelper(app.Widget).Handle;
                var expected = i % 2 == 1 ? DisplayMode.Floating : DisplayMode.Desktop;
                var usable = app.Settings.Mode == expected && app.Store.Settings().Mode == expected &&
                    app.Widget.IsVisible && NativeMethods.IsWindowVisible(clickHwnd) &&
                    (NativeMethods.GetWindowLongPtr(clickHwnd, NativeMethods.GwlStyle).ToInt64() & NativeMethods.WsChild) == 0 &&
                    (expected == DisplayMode.Floating ? app.Widget.Topmost : app.Widget.Host is RevealedWindowHost) &&
                    Application.Current.Windows.OfType<MainWindow>().Count(w => w.IsVisible) == 1;
                allClicksVisible &= usable;
                allClicksReuseWindow &= ReferenceEquals(clickWindow, app.Widget) && clickHwnd == revealedHwnd;
                clickStates.Add(new { click = i, mode = app.Widget.Host.Name, usable });
            }
            Check("thirty-title-button-clicks-stay-accessible", allClicksVisible, JsonSerializer.Serialize(clickStates));
            Check("title-button-clicks-reuse-live-window-and-handle", allClicksReuseWindow);
            Check("title-button-clicks-retain-task-progress", app.ViewModel.Tasks.Select(t => (t.Id, t.Progress)).SequenceEqual(clickTaskState));
            await Task.Delay(3300); // Include a health-timer tick after returning to revealed desktop mode.
            Check("health-timer-keeps-manually-switched-window-revealed", app.ViewModel.IsRevealed && app.Widget.IsVisible);
            app.Show();
            revealedHwnd = new WindowInteropHelper(app.Widget).Handle;
            app.Show(); await Task.Delay(50);
            Check("repeated-open-reuses-visible-widget", new WindowInteropHelper(app.Widget).Handle == revealedHwnd &&
                Application.Current.Windows.OfType<MainWindow>().Count(w => w.IsVisible) == 1);
            app.Widget.WindowState = WindowState.Minimized; app.Show();
            Check("explicit-open-restores-minimized-widget", app.Widget.WindowState == WindowState.Normal);
            app.ReturnToDesktop(); await Task.Delay(100);
            Check("return-to-desktop-removes-temporary-reveal", !app.ViewModel.IsRevealed && !app.Widget.ShowInTaskbar && app.Settings.Mode == DisplayMode.Desktop);
            await OpenAnotherInstance(folder);
            Check("second-process-reveals-desktop", app.ViewModel.IsRevealed && app.Widget.IsVisible &&
                Application.Current.Windows.OfType<MainWindow>().Count(w => w.IsVisible) == 1);
            app.Widget.Hide(); await OpenAnotherInstance(folder);
            Check("second-process-restores-hidden-widget", app.Widget.IsVisible && app.Widget.ShowInTaskbar);
            app.ApplySettings(app.Settings with { Mode = DisplayMode.Floating, Theme = AppTheme.Dark }); await Task.Delay(350);
            var hwnd = new WindowInteropHelper(app.Widget).Handle;
            Check("floating-native-topmost", (NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle).ToInt64() & 8) != 0);
            Check("floating-not-taskbar", !app.Widget.ShowInTaskbar && (NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle).ToInt64() & NativeMethods.WsExToolWindow) != 0);
            await VerifyResize(app, Check, "floating");
            var vm = app.ViewModel.Tasks.First(t => t.Progress != 100);
            var slider = Descendants<Slider>(app.Widget).Single(s => ReferenceEquals(s.DataContext, vm));
            slider.SetCurrentValue(Slider.ValueProperty, 100.0); app.Flush(); await Task.Delay(50);
            Check("slider-binding-completes-and-persists", app.Store.Find(vm.Id) is { Progress: 100, CompletedAt: not null });
            var titleBlock = Descendants<TextBlock>(app.Widget).First(t => t.Text == vm.Title);
            Check("completion-has-real-strikethrough", titleBlock.TextDecorations.Any(d => d.Location == TextDecorationLocation.Strikethrough));
            Check("completion-has-muted-foreground", ((SolidColorBrush)titleBlock.Foreground).Color == ((SolidColorBrush)Application.Current.FindResource("Muted")).Color);
            slider.SetCurrentValue(Slider.ValueProperty, 80.0); app.Flush(); await Task.Delay(50);
            Check("slider-binding-reopens", app.Store.Find(vm.Id) is { Progress: 80, CompletedAt: null });
            Check("reopened-removes-strikethrough", titleBlock.TextDecorations is null || titleBlock.TextDecorations.Count == 0);
            app.Widget.ScrollPosition = 0;
            Capture(app.Widget, Path.Combine(folder, "widget-dark.png"));
            app.Widget.Hide(); app.Show(); Check("hide-and-show", app.Widget.IsVisible);
            app.ApplySettings(app.Settings with { Theme = AppTheme.Light });
            var review = new ReviewWindow(app); review.Show(); await Task.Delay(350); Capture(review, Path.Combine(folder, "review-week.png"));
            Descendants<ComboBox>(review).First().SelectedIndex = 2; await Task.Delay(150); Capture(review, Path.Combine(folder, "review-month.png"));
            review.Close();
            var settings = new SettingsWindow(app); settings.Show(); await Task.Delay(250); Capture(settings, Path.Combine(folder, "settings.png")); settings.Close();
            var editor = new TaskEditorWindow(app, app.Store.Find(ids[0])); editor.Show(); await Task.Delay(250); Capture(editor, Path.Combine(folder, "editor.png")); editor.Close();
            app.FlushAll();
            Check("window-bounds-persist", app.Store.Settings().Width >= 320 && app.Store.Settings().Height >= 420);
            var clamped = WindowGeometry.Clamp(app.Settings with { Left = -100000, Top = -100000 }, 1);
            Check("offscreen-bounds-clamped", clamped.Left > -100000 && clamped.Top > -100000);
            app.CreateWidget(forceFallback: true); await Task.Delay(100);
            Check("safe-fallback-visible-nontopmost", app.Widget.IsVisible && !app.Widget.Topmost && NativeMethods.GetParent(new WindowInteropHelper(app.Widget).Handle) == IntPtr.Zero);
            app.Widget.InternalClose = true; app.Widget.Close(); app.CreateWidget(); await Task.Delay(100);
            Check("recreates-closed-widget", app.Widget.IsVisible && app.Widget.Host.IsHealthy(new WindowInteropHelper(app.Widget).Handle));
        }
        catch (Exception ex) { checks.Add(new { name = "unhandled", success = false, detail = ex.ToString() }); failures.Add("unhandled"); }
        File.WriteAllText(Path.Combine(folder, "smoke-results.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
        Application.Current.Shutdown(failures.Count == 0 ? 0 : 1);
    }
    private static async Task OpenAnotherInstance(string folder)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing executable path.");
        var info = new System.Diagnostics.ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "DeskPlan.dll"));
        info.ArgumentList.Add("--data-dir"); info.ArgumentList.Add(folder);
        using var child = System.Diagnostics.Process.Start(info) ?? throw new InvalidOperationException("Failed to launch second instance.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await child.WaitForExitAsync(timeout.Token);
        if (child.ExitCode != 0) throw new InvalidOperationException("Second instance exited with error.");
        await Task.Delay(150);
    }
    private static async Task VerifyResize(AppController app, Action<string, bool, string?> check, string mode)
    {
        var window = app.Widget;
        var hwnd = new WindowInteropHelper(window).Handle;
        var scale = WindowGeometry.Scale(hwnd);
        var area = WindowGeometry.WorkArea(hwnd);
        var saved = WindowGeometry.Capture(window, app.Settings);
        var baseline = saved with { Left = area.Left + 60, Top = area.Top + 60, Width = 400, Height = 540 };
        var directions = new (string Name, ResizeEdges Edges, double X, double Y)[]
        {
            ("ResizeLeft", ResizeEdges.Left, 3, .5), ("ResizeRight", ResizeEdges.Right, -3, .5),
            ("ResizeTop", ResizeEdges.Top, .5, 3), ("ResizeBottom", ResizeEdges.Bottom, .5, -3),
            ("ResizeTopLeft", ResizeEdges.Top | ResizeEdges.Left, 8, 8),
            ("ResizeTopRight", ResizeEdges.Top | ResizeEdges.Right, -8, 8),
            ("ResizeBottomLeft", ResizeEdges.Bottom | ResizeEdges.Left, 8, -8),
            ("ResizeBottomRight", ResizeEdges.Bottom | ResizeEdges.Right, -8, -8)
        };
        var hitTargets = true; var edgesCorrect = true; var persistence = true;
        var details = new List<object>();
        foreach (var direction in directions)
        {
            WindowGeometry.Place(window, window.Host, baseline); await Task.Delay(30);
            var x = direction.X == .5 ? window.ActualWidth / 2 : direction.X < 0 ? window.ActualWidth + direction.X : direction.X;
            var y = direction.Y == .5 ? window.ActualHeight / 2 : direction.Y < 0 ? window.ActualHeight + direction.Y : direction.Y;
            var hit = window.InputHitTest(new Point(x, y)) as DependencyObject;
            while (hit is not null && hit is not Thumb) hit = VisualTreeHelper.GetParent(hit);
            hitTargets &= hit is Thumb thumb && thumb.Name == direction.Name;
            NativeMethods.GetWindowRect(hwnd, out var start);
            var origin = new NativeMethods.Point { X = start.Left + 50, Y = start.Top + 50 };
            var dx = direction.Edges.HasFlag(ResizeEdges.Left) ? -24 : 24;
            var dy = direction.Edges.HasFlag(ResizeEdges.Top) ? -24 : 24;
            window.BeginResize(direction.Edges, origin);
            window.ResizeTo(new() { X = origin.X + dx, Y = origin.Y + dy });
            await Task.Delay(30); window.EndResize();
            NativeMethods.GetWindowRect(hwnd, out var after);
            var correct = Near(after.Left, start.Left + (direction.Edges.HasFlag(ResizeEdges.Left) ? dx : 0)) &&
                Near(after.Right, start.Right + (direction.Edges.HasFlag(ResizeEdges.Right) ? dx : 0)) &&
                Near(after.Top, start.Top + (direction.Edges.HasFlag(ResizeEdges.Top) ? dy : 0)) &&
                Near(after.Bottom, start.Bottom + (direction.Edges.HasFlag(ResizeEdges.Bottom) ? dy : 0));
            edgesCorrect &= correct;
            var stored = app.Store.Settings();
            persistence &= Near(stored.Left, after.Left) && Near(stored.Top, after.Top) &&
                Near(stored.Width * scale, after.Right - after.Left) && Near(stored.Height * scale, after.Bottom - after.Top);
            details.Add(new { direction.Name, correct });
        }
        check(mode + "-eight-resize-hit-targets", hitTargets, null);
        check(mode + "-eight-directions-move-only-selected-edges", edgesCorrect, JsonSerializer.Serialize(details));
        check(mode + "-resize-bounds-persist", persistence, null);

        WindowGeometry.Place(window, window.Host, baseline); await Task.Delay(30);
        NativeMethods.GetWindowRect(hwnd, out var initial);
        var pointer = new NativeMethods.Point { X = initial.Left, Y = initial.Top };
        window.BeginResize(ResizeEdges.Top | ResizeEdges.Left, pointer);
        window.ResizeTo(new() { X = pointer.X + 10000, Y = pointer.Y + 10000 }); await Task.Delay(30);
        NativeMethods.GetWindowRect(hwnd, out var minimum);
        var limits = Near(minimum.Right, initial.Right) && Near(minimum.Bottom, initial.Bottom) &&
            Near(minimum.Right - minimum.Left, window.MinWidth * scale) && Near(minimum.Bottom - minimum.Top, window.MinHeight * scale);
        window.ResizeTo(pointer); await Task.Delay(30); window.EndResize();
        NativeMethods.GetWindowRect(hwnd, out var restored);
        check(mode + "-resize-minimum-and-return-without-drift", limits && restored.Equals(initial), null);

        // Coalesced move messages must not lose the final screen displacement on mouse-up.
        WindowGeometry.Place(window, window.Host, baseline); await Task.Delay(30);
        NativeMethods.GetWindowRect(hwnd, out var fastStart);
        var topLeft = (Thumb)window.FindName("ResizeTopLeft");
        topLeft.RaiseEvent(new DragStartedEventArgs(8, 8));
        topLeft.RaiseEvent(new DragCompletedEventArgs(12, 10, false));
        var fastSaved = app.Store.Settings(); // Mouse-up must persist the final size without waiting for layout.
        await Task.Delay(30);
        NativeMethods.GetWindowRect(hwnd, out var fastEnd);
        check(mode + "-fast-drag-completion-commits-final-position",
            Near(fastEnd.Left, fastStart.Left + 12) && Near(fastEnd.Top, fastStart.Top + 10) &&
            Near(fastEnd.Right, fastStart.Right) && Near(fastEnd.Bottom, fastStart.Bottom) &&
            Near(fastSaved.Width * scale, fastEnd.Right - fastEnd.Left) && Near(fastSaved.Height * scale, fastEnd.Bottom - fastEnd.Top), null);

        WindowGeometry.Place(window, window.Host, baseline); await Task.Delay(30);
        NativeMethods.GetWindowRect(hwnd, out var eventStart);
        topLeft.RaiseEvent(new DragStartedEventArgs(8, 8));
        topLeft.RaiseEvent(new DragDeltaEventArgs(12 / scale, 10 / scale)); await Task.Delay(30);
        // The Thumb moved with the window. This relative delta gives a total of 20 by 16 screen pixels.
        topLeft.RaiseEvent(new DragDeltaEventArgs(8 / scale, 6 / scale)); await Task.Delay(30);
        NativeMethods.GetWindowRect(hwnd, out var eventEnd);
        topLeft.RaiseEvent(new DragCompletedEventArgs(20, 16, false));
        check(mode + "-left-top-drag-event-coordinates-do-not-drift",
            Near(eventEnd.Left, eventStart.Left + 20) && Near(eventEnd.Top, eventStart.Top + 16) &&
            Near(eventEnd.Right, eventStart.Right) && Near(eventEnd.Bottom, eventStart.Bottom), null);
        WindowGeometry.Place(window, window.Host, saved); await Task.Delay(30); app.SaveWindow();
    }
    private static bool Near(double a, double b) => Math.Abs(a - b) <= 1;
    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var root = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth + root.Margin.Left + root.Margin.Right),
            (int)Math.Ceiling(root.ActualHeight + root.Margin.Top + root.Margin.Bottom), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed) yield return typed;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
