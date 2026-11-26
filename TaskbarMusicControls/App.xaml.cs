using System.Windows;
using System.Windows.Threading;
using TaskbarMusicControls.Services;

namespace TaskbarMusicControls;

public partial class App : Application
{
    private const string InstanceMutexName = @"Local\TaskbarMusicControls.SingleInstance";

    private TaskbarMusicWidgetService? _widgetService;
    private Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!TryAcquireSingleInstance(out _instanceMutex))
        {
            // A second copy would attach a second overlay to the same taskbar
            // position, so the two widgets would be drawn on top of each other.
            try
            {
                MessageBox.Show(
                    "组件已经在运行了。",
                    "Taskbar Music Controls",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception)
            {
                // Ignore: the second instance has to go away either way.
            }

            Shutdown();
            return;
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _widgetService = new TaskbarMusicWidgetService();
        Dispatcher.BeginInvoke(
            new Action(async () => await _widgetService.StartAsync()),
            DispatcherPriority.ApplicationIdle);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ReleaseSingleInstance();
        base.OnExit(e);
    }

    private bool TryAcquireSingleInstance(out Mutex? mutex)
    {
        mutex = null;
        try
        {
            var created = new Mutex(false, InstanceMutexName);
            bool owned;
            try
            {
                owned = created.WaitOne(TimeSpan.Zero, false);
            }
            catch (AbandonedMutexException)
            {
                // The previous owner died without releasing the mutex; it is
                // owned by this process now.
                owned = true;
            }

            if (!owned)
            {
                created.Dispose();
                return false;
            }

            mutex = created;
            return true;
        }
        catch (Exception)
        {
            // If the single instance check cannot be performed at all, run anyway
            // instead of refusing to start.
            mutex = null;
            return true;
        }
    }

    private void ReleaseSingleInstance()
    {
        var mutex = _instanceMutex;
        _instanceMutex = null;
        if (mutex is null)
            return;

        try
        {
            mutex.ReleaseMutex();
        }
        catch (Exception)
        {
            // The mutex is not owned by this thread any more.
        }

        mutex.Dispose();
    }
}
