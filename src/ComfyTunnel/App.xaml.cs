using System;
using System.Threading;
using System.Windows;

namespace ComfyTunnel.Desktop;

public partial class App : Application
{
    private Mutex? instance;
    private EventWaitHandle? showEvent;
    private RegisteredWaitHandle? showRegistration;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\ComfyTunnel.Show");
        instance = new Mutex(true, @"Local\ComfyTunnel.Desktop", out bool created);
        if (!created) { showEvent.Set(); Shutdown(); return; }
        var window = new MainWindow(); MainWindow = window; window.Show();
        showRegistration = ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) => Dispatcher.BeginInvoke(() =>
        { window.Show(); window.WindowState = WindowState.Normal; window.Activate(); }), null, Timeout.Infinite, false);
    }
    protected override void OnExit(ExitEventArgs e) { showRegistration?.Unregister(null); showEvent?.Dispose(); instance?.Dispose(); base.OnExit(e); }
}
