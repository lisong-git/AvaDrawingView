using System;
using Avalonia;

namespace CadViewer;

internal static class Program
{
    /// <summary>命令行参数快照：由 Main 入口写入，供主窗口启动时读取（如自动加载图纸路径）。</summary>
    public static string[] Args { get; private set; } = Array.Empty<string>();

    [STAThread]
    public static void Main(string[] args)
    {
        // 记录一份不可变参数快照，MainWindow.OnOpened 据此自动加载图纸。
        Args = args ?? Array.Empty<string>();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(Args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
