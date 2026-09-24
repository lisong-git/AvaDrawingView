using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace CadViewer;

public class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;
            // 保底调用点：投递到 UI 线程、主循环启动后再尝试自动加载。
            // 直接在初始化阶段 await Task.Run 会导致续体无法回到消息泵（死锁），故用 Dispatcher.Post 排到消息队列；方法内部幂等。
            Dispatcher.UIThread.Post(() => window.TryAutoLoadFromCommandLine());
        }
        base.OnFrameworkInitializationCompleted();
    }
}
