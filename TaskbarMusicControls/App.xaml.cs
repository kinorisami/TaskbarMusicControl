using System.Windows;
using System.Windows.Threading;
using TaskbarMusicControls.Services;

namespace TaskbarMusicControls;

public partial class App : Application
{
    private TaskbarMusicWidgetService? _widgetService;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _widgetService = new TaskbarMusicWidgetService();
        Dispatcher.BeginInvoke(
            new Action(async () => await _widgetService.StartAsync()),
            DispatcherPriority.ApplicationIdle);
    }
}
