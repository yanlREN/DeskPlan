using System.IO;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using DeskPlan.App.Views;
using DeskPlan.Windows;

namespace DeskPlan.App;

public partial class App : Application
{
    private Mutex? mutex;
    private EventWaitHandle? showEvent;
    private RegisteredWaitHandle? wait;
    private MemoryMappedFile? ownerMap;
    public AppController? Controller { get; private set; }
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var smokeIndex = Array.IndexOf(e.Args, "--smoke-test");
        var dataIndex = Array.IndexOf(e.Args, "--data-dir");
        var test = smokeIndex >= 0;
        var root = test && smokeIndex + 1 < e.Args.Length ? Path.GetFullPath(e.Args[smokeIndex + 1])
            : dataIndex >= 0 && dataIndex + 1 < e.Args.Length ? Path.GetFullPath(e.Args[dataIndex + 1])
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskPlan");
        if (test && smokeIndex + 1 >= e.Args.Length) { Shutdown(2); return; }
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())))[..16];
        mutex = new Mutex(true, @"Local\DeskPlan-" + suffix, out var first);
        if (!first)
        {
            var signaled = false;
            // A second click can arrive while the first process is still creating its event.
            for (var attempt = 0; attempt < 20 && !signaled; attempt++)
            {
                try
                {
                    using var existing = EventWaitHandle.OpenExisting(@"Local\DeskPlanShow-" + suffix);
                    try
                    {
                        using var map = MemoryMappedFile.OpenExisting(@"Local\DeskPlanOwner-" + suffix, MemoryMappedFileRights.Read);
                        using var reader = map.CreateViewAccessor(0, sizeof(int), MemoryMappedFileAccess.Read);
                        NativeMethods.AllowSetForegroundWindow((uint)reader.ReadInt32(0));
                    }
                    catch (FileNotFoundException) { /* Compatible with an already-running older version. */ }
                    existing.Set(); signaled = true;
                }
                catch (WaitHandleCannotBeOpenedException) { Thread.Sleep(50); }
            }
            if (!signaled) MessageBox.Show("DeskPlan 正在启动，请稍后再打开一次。", "DeskPlan");
            mutex.Dispose(); mutex = null; Shutdown(); return;
        }
        try
        {
            showEvent = new(false, EventResetMode.AutoReset, @"Local\DeskPlanShow-" + suffix);
            ownerMap = MemoryMappedFile.CreateOrOpen(@"Local\DeskPlanOwner-" + suffix, sizeof(int));
            using (var writer = ownerMap.CreateViewAccessor()) writer.Write(0, Environment.ProcessId);
            Controller = new(root, test);
            wait = ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) => Dispatcher.BeginInvoke(() => Controller?.Show()), null, Timeout.Infinite, false);
            DispatcherUnhandledException += (_, args) =>
            {
                Controller.Log("ui", args.Exception); args.Handled = true;
                if (!test) Ui.Error(null, args.Exception);
                else { File.WriteAllText(Path.Combine(root, "fatal.txt"), args.Exception.ToString()); Shutdown(1); }
            };
            SessionEnding += (_, args) => { try { Controller.FlushAll(); } catch { args.Cancel = true; } };
            if (test) Dispatcher.BeginInvoke(async () => await SmokeTest.Run(Controller, root));
            else if (!e.Args.Contains("--autostart")) Dispatcher.BeginInvoke(() => Controller.Show());
        }
        catch (Exception ex)
        {
            Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "startup-error.log"), ex.ToString());
            if (!test) MessageBox.Show("DeskPlan 无法启动，原数据未被清空。\n" + ex.Message + "\n\n诊断文件：" + Path.Combine(root, "startup-error.log"), "DeskPlan", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        wait?.Unregister(null); showEvent?.Dispose(); ownerMap?.Dispose(); Controller?.Dispose();
        if (mutex is not null) { mutex.ReleaseMutex(); mutex.Dispose(); }
        base.OnExit(e);
    }
}
