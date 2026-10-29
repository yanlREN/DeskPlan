using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DeskPlan.App.ViewModels;
using DeskPlan.App.Views;
using DeskPlan.Core;
using DeskPlan.Infrastructure;
using DeskPlan.Windows;

namespace DeskPlan.App;

public sealed class AppController : IDisposable
{
    public PlannerStore Store { get; }
    public PlannerService Service { get; }
    public PlannerSettings Settings { get; private set; }
    public MainViewModel ViewModel { get; }
    public MainWindow Widget { get; private set; } = null!;
    public string? DesktopFailure { get; private set; }
    private readonly TrayService tray;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer boundsTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private ReviewWindow? review, overdue;
    private SettingsWindow? settingsWindow;
    private bool changingWindow, exiting;
    private int recoveryAttempts, ticks;
    private DateTime lastToday = DateTime.Today;
    private readonly bool testing;

    public AppController(string dataFolder, bool testing = false)
    {
        this.testing = testing;
        Store = new(Path.Combine(dataFolder, "deskplan.db")); Service = new(Store); Settings = Store.Settings();
        Service.CatchUp(DateOnly.FromDateTime(DateTime.Today));
        ViewModel = new(Service, () => Settings);
        tray = new();
        tray.ShowRequested += Show;
        tray.ToggleVisibilityRequested += () => { if (Widget.IsVisible) { SaveWindow(); Widget.Hide(); } else Show(); };
        tray.SwitchRequested += SwitchMode; tray.ReviewRequested += OpenReview; tray.SettingsRequested += OpenSettings; tray.ExitRequested += Exit;
        tray.ReminderClicked += id =>
        {
            if (Store.Find(id) is { Deleted: false } task) { ViewModel.SelectedDate = task.ScheduledDate.ToDateTime(TimeOnly.MinValue); Show(); EditTask(id); }
        };
        tray.SnoozeRequested += id => { if (Store.Find(id) is { Deleted: false, IsCompleted: false }) Service.Snooze(id); };
        boundsTimer.Tick += (_, _) => { boundsTimer.Stop(); Try(SaveWindow); };
        timer.Tick += (_, _) => Try(Tick);
        ApplyTheme(); CreateWidget(); timer.Start();
    }
    public void CreateWidget(bool forceFallback = false, bool preserveOldBounds = true, bool reveal = false)
    {
        if (changingWindow) return;
        changingWindow = true;
        try
        {
            Flush();
            var scroll = Widget?.ScrollPosition ?? 0;
            if (Widget is not null)
            {
                if (preserveOldBounds && NativeMethods.IsWindow(new WindowInteropHelper(Widget).Handle)) SaveWindow();
                Widget.InternalClose = true;
                try
                {
                    if (Widget.IsVisible) Widget.Hide();
                    Widget.Close();
                }
                catch (InvalidOperationException)
                {
                    // Explorer can destroy its child HWND before the health timer runs.
                    // An already-closed WPF Window must never prevent creating the replacement.
                }
            }
            MainWindow Make(IWindowHost host)
            {
                var window = new MainWindow(this)
                {
                    Host = host, Width = Settings.Width, Height = Settings.Height, FontSize = Settings.FontSize,
                    ShowInTaskbar = host is RevealedWindowHost, ShowActivated = host is RevealedWindowHost
                };
                new WindowInteropHelper(window).EnsureHandle();
                try
                {
                    host.Attach(window);
                    // Place after Show: WPF initial layout can otherwise restore its initial screen position.
                    window.Show(); WindowGeometry.Place(window, host, Settings);
                    var source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
                    source?.AddHook((IntPtr hwnd, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
                    {
                        if (msg is 0x007E or 0x02E0)
                            window.Dispatcher.BeginInvoke(() => { if (window.IsLoaded) WindowGeometry.Place(window, host, Settings); });
                        return IntPtr.Zero;
                    });
                    return window;
                }
                catch { window.InternalClose = true; window.Close(); throw; }
            }
            DesktopFailure = null;
            try
            {
                Widget = Make(reveal ? new RevealedWindowHost() : forceFallback ? new FloatingWindowHost(false)
                    : Settings.Mode == DisplayMode.Desktop ? new DesktopWindowHost() : new FloatingWindowHost());
            }
            catch (Exception ex) when (Settings.Mode == DisplayMode.Desktop)
            {
                DesktopFailure = ex.Message; Log("desktop-fallback", ex);
                Widget = Make(new FloatingWindowHost(false));
            }
            Application.Current.MainWindow = Widget;
            UpdateModeState();
            Widget.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Widget.ScrollPosition = scroll);
            if (DesktopFailure is not null) ViewModel.SaveStatus = "桌面嵌入暂不可用，已显示普通窗口；可从托盘切换。";
        }
        finally { changingWindow = false; }
    }
    private void UpdateModeState()
    {
        ViewModel.ModeName = Widget.Host.Name;
        ViewModel.IsRevealed = Widget.Host is RevealedWindowHost;
        ViewModel.ModeHint = Settings.Mode == DisplayMode.Floating
            ? "切换到桌面模式并保持显示；点击右下角“放回桌面”可贴回壁纸层"
            : "当前为" + Widget.Host.Name + "；点击切换到悬浮模式";
        tray.SetMode(Widget.Host.Name, Settings.Mode == DisplayMode.Floating);
    }
    public void Show()
    {
        // A desktop child HWND cannot be raised above normal applications by Activate().
        // Recreate it as a temporary top-level window without changing the saved desktop preference.
        var revealDesktop = Settings.Mode == DisplayMode.Desktop;
        if (revealDesktop && Widget.Host is not RevealedWindowHost ||
            !Widget.Host.IsHealthy(new WindowInteropHelper(Widget).Handle)) CreateWidget(reveal: revealDesktop);
        Widget.WindowState = WindowState.Normal;
        Widget.Show(); WindowGeometry.Place(Widget, Widget.Host, Settings);
        // This runs only for an explicit open request, never on background recovery/autostart.
        Widget.Activate();
        NativeMethods.SetForegroundWindow(new WindowInteropHelper(Widget).Handle);
    }
    public void ReturnToDesktop()
    {
        if (Settings.Mode != DisplayMode.Desktop) return;
        CreateWidget();
    }
    public void SwitchMode()
    {
        if (changingWindow || exiting) return;
        Flush(); SaveWindow();
        Settings = Settings with { Mode = Settings.Mode == DisplayMode.Desktop ? DisplayMode.Floating : DisplayMode.Desktop };
        Store.Save(Settings); recoveryAttempts = 0;
        if (Widget.Host is not DesktopWindowHost && Widget.Host.IsHealthy(new WindowInteropHelper(Widget).Handle))
        {
            // Both interactive modes are top-level windows. Keep the live Window/HWND and input state
            // instead of closing the window underneath the title-bar Click handler on every click.
            changingWindow = true;
            try
            {
                Widget.Host = Settings.Mode == DisplayMode.Desktop ? new RevealedWindowHost() : new FloatingWindowHost();
                Widget.ShowInTaskbar = Widget.Host is RevealedWindowHost;
                Widget.Host.Attach(Widget);
                DesktopFailure = null;
                UpdateModeState();
            }
            finally { changingWindow = false; }
        }
        else CreateWidget(reveal: Settings.Mode == DisplayMode.Desktop);
        // An explicit mode switch must leave a usable window in front of the user.
        // Only ReturnToDesktop (or autostart) sends it back to the wallpaper layer.
        Show();
    }
    public void SaveWindow()
    {
        if (Widget is null || exiting) return;
        var hwnd = new WindowInteropHelper(Widget).Handle;
        if (!NativeMethods.IsWindow(hwnd)) return;
        var captured = WindowGeometry.Capture(Widget, Settings);
        if (captured.Width >= 320 && captured.Height >= 420) { Settings = captured; Store.Save(Settings); }
    }
    public void ScheduleBoundsSave() { if (changingWindow) return; boundsTimer.Stop(); boundsTimer.Start(); }
    public void SetManualSort() { Settings = Settings with { ManualSort = true }; Store.Save(Settings); }
    public void ApplySettings(PlannerSettings value)
    {
        Flush(); SaveWindow();
        value = value with { Left = Settings.Left, Top = Settings.Top, Width = Settings.Width, Height = Settings.Height };
        if (!testing && value.Autostart != StartupRegistration.IsEnabled())
            StartupRegistration.SetEnabled(value.Autostart, Environment.ProcessPath ?? throw new InvalidOperationException("无法确定程序路径。"));
        var keepRevealed = ViewModel.IsRevealed && value.Mode == DisplayMode.Desktop;
        Settings = value; Store.Save(Settings); ApplyTheme(); recoveryAttempts = 0; CreateWidget(reveal: keepRevealed); ViewModel.Refresh();
    }
    public void ReloadAfterRestore()
    {
        Settings = Store.Settings() with { Autostart = StartupRegistration.IsEnabled() };
        Store.Save(Settings); Service.CatchUp(DateOnly.FromDateTime(DateTime.Today));
        ApplyTheme(); ViewModel.Refresh(); CreateWidget(preserveOldBounds: false); review?.RefreshData(); overdue?.RefreshData();
    }
    private void ApplyTheme()
    {
        var dark = Settings.Theme == AppTheme.Dark;
        var palette = new Dictionary<string, string>
        {
            ["Surface"] = dark ? "#192431" : "#F7F9FC", ["Card"] = dark ? "#233141" : "#FFFFFF",
            ["Ink"] = dark ? "#EDF3F8" : "#20314A", ["Muted"] = dark ? "#A2B1C2" : "#748399",
            ["Line"] = dark ? "#38495C" : "#E3E9F0", ["Accent"] = dark ? "#66CBB6" : "#238881",
            ["AccentSoft"] = dark ? "#263F42" : "#E4F4F0", ["Danger"] = dark ? "#FFABAE" : "#B84C51"
        };
        foreach (var (name, hex) in palette)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            if (name == "Surface") color.A = (byte)Math.Round(Settings.BackgroundOpacity * 255);
            if (Settings.Theme == AppTheme.Glass && name is "Card" or "AccentSoft") color.A = 180;
            Application.Current.Resources[name] = new SolidColorBrush(color);
        }
    }
    public void EditTask(Guid? id, string title = "")
    {
        Flush(); var task = id.HasValue ? Store.Find(id.Value) : null;
        if (id.HasValue && task is not { Deleted: false }) return;
        var editor = new TaskEditorWindow(this, task, title);
        editor.ShowDialog(); ViewModel.Refresh();
    }
    public void OpenReview()
    {
        Flush();
        if (review is { IsLoaded: true }) { review.Activate(); return; }
        review = new(this); review.Closed += (_, _) => review = null; review.Show();
    }
    public void OpenOverdue()
    {
        Flush();
        if (overdue is { IsLoaded: true }) { overdue.Activate(); return; }
        overdue = new(this, true); overdue.Closed += (_, _) => overdue = null; overdue.Show();
    }
    public void OpenSettings()
    {
        if (settingsWindow is { IsLoaded: true }) { settingsWindow.Activate(); return; }
        settingsWindow = new(this); settingsWindow.Closed += (_, _) => settingsWindow = null; settingsWindow.Show();
    }
    private void Tick()
    {
        if (changingWindow || exiting) return;
        if (DateTime.Today != lastToday)
        {
            Service.CatchUp(DateOnly.FromDateTime(DateTime.Today));
            if (ViewModel.SelectedDate == lastToday) ViewModel.SelectedDate = DateTime.Today; else ViewModel.Refresh();
            lastToday = DateTime.Today;
        }
        if (Settings.Mode == DisplayMode.Desktop && !Widget.Host.IsHealthy(new WindowInteropHelper(Widget).Handle))
        {
            if (recoveryAttempts < 3) { recoveryAttempts++; CreateWidget(); }
            else if (Widget.Host is DesktopWindowHost) CreateWidget(true);
        }
        // Bounded retries for an unavailable desktop layer, then leave the usable fallback alone.
        else if (Settings.Mode == DisplayMode.Desktop && DesktopFailure is not null && recoveryAttempts < 3)
        { recoveryAttempts++; CreateWidget(); }
        if (++ticks % 5 != 0 || testing) return;
        var now = DateTimeOffset.UtcNow;
        var today = DateOnly.FromDateTime(DateTime.Today);
        Service.Materialize(today, today.AddDays(1));
        var reminderTasks = Store.Occurrences(today.AddDays(-1), today.AddDays(1))
            .Concat(Store.AllOccurrences().Where(t => !t.Deleted && t.SnoozedUntil.HasValue)).DistinctBy(t => t.Id);
        foreach (var task in reminderTasks.OrderBy(t => t.SnoozedUntil.HasValue ? 0 : 1).ThenBy(t => t.ScheduledDate).ThenBy(t => t.StartTime))
        {
            if (ReminderRules.Due(task, now, TimeZoneInfo.Local) is not { } due) continue;
            // Persist before dispatch: a crash must not repeat an already queued notification.
            Service.MarkReminderDelivered(task.Id, due.Key);
            tray.Notify(task.Id, "DeskPlan · 计划提醒", task.Title); break;
        }
    }
    public void Flush() => ViewModel.Flush();
    public void FlushAll() { Flush(); review?.SaveNote(); SaveWindow(); }
    public void Exit()
    {
        if (exiting) return;
        try { FlushAll(); } catch (Exception ex) { Ui.Error(null, ex); return; }
        exiting = true; Application.Current.Shutdown();
    }
    public void Log(string context, Exception ex)
    {
        try { File.AppendAllText(Path.Combine(Path.GetDirectoryName(Store.DatabasePath)!, "deskplan.log"), $"{DateTimeOffset.Now:O} [{context}] {ex}\n"); }
        catch { /* Reporting a disk error must not overwrite or reset the database. */ }
    }
    private void Try(Action action) { try { action(); } catch (Exception ex) { Log("background", ex); ViewModel.SaveStatus = "操作未完成：" + ex.Message; } }
    public void Dispose() { timer.Stop(); boundsTimer.Stop(); tray.Dispose(); Store.Dispose(); }
}
