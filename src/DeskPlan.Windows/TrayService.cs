using System;
using System.Drawing;
using Forms = System.Windows.Forms;
using Microsoft.Win32;

namespace DeskPlan.Windows;

public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon icon;
    private readonly Icon? ownedIcon;
    private readonly Forms.ToolStripMenuItem mode;
    private readonly Forms.ToolStripMenuItem snooze;
    private Guid? lastReminder;
    public event Action? ShowRequested;
    public event Action? ToggleVisibilityRequested;
    public event Action? SwitchRequested;
    public event Action? ReviewRequested;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;
    public event Action<Guid>? ReminderClicked;
    public event Action<Guid>? SnoozeRequested;
    public TrayService()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示 / 隐藏组件", null, (_, _) => ToggleVisibilityRequested?.Invoke());
        mode = new("切换模式", null, (_, _) => SwitchRequested?.Invoke()); menu.Items.Add(mode);
        menu.Items.Add("历史回顾", null, (_, _) => ReviewRequested?.Invoke());
        menu.Items.Add("设置与备份", null, (_, _) => SettingsRequested?.Invoke());
        snooze = new("最近提醒：稍后 10 分钟", null, (_, _) => { if (lastReminder is { } id) SnoozeRequested?.Invoke(id); }) { Enabled = false };
        menu.Items.Add(snooze); menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出 DeskPlan", null, (_, _) => ExitRequested?.Invoke());
        try { if (Environment.ProcessPath is { } path) ownedIcon = Icon.ExtractAssociatedIcon(path); } catch { }
        icon = new Forms.NotifyIcon { Text = "DeskPlan · 桌面计划", Icon = ownedIcon ?? SystemIcons.Application, ContextMenuStrip = menu, Visible = true };
        icon.DoubleClick += (_, _) => ShowRequested?.Invoke();
        icon.BalloonTipClicked += (_, _) => { if (lastReminder is { } id) ReminderClicked?.Invoke(id); };
    }
    public void SetMode(string current, bool floating)
    { mode.Text = current + " → " + (floating ? "切换到桌面" : "切换到悬浮"); }
    public void Notify(Guid id, string title, string message)
    {
        lastReminder = id; snooze.Enabled = true;
        icon.ShowBalloonTip(10000, title, message + "\n点击查看；托盘菜单可稍后 10 分钟提醒。", Forms.ToolTipIcon.Info);
    }
    public void Dispose() { icon.Visible = false; icon.ContextMenuStrip?.Dispose(); icon.Dispose(); ownedIcon?.Dispose(); }
}

public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static bool IsEnabled()
    { using var key = Registry.CurrentUser.OpenSubKey(RunKey); return key?.GetValue("DeskPlan") is string; }
    public static void SetEnabled(bool enabled, string executable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue("DeskPlan", "\"" + executable + "\" --autostart");
        else key.DeleteValue("DeskPlan", false);
    }
}
