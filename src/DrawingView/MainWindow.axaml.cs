using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using DrawingView.Core;

namespace DrawingView;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        // 注意：不要在此手写 InitializeComponent()，否则会覆盖 Avalonia 生成的版本，
        // 导致 XAML 中 x:Name 声明的控件的后台字段（StatusText/View/LayerList）全部为 null。
        InitializeComponent();
        WriteDiag($"ctor: StatusText={(StatusText is null ? "NULL" : "ok")}, "
                  + $"View={(View is null ? "NULL" : "ok")}, LayerList={(LayerList is null ? "NULL" : "ok")}");
    }

    /// <summary>
    /// 打开图纸。
    /// Avalonia 12 已移除 Windows 专用的 OpenFileDialog / FileDialogFilter，
    /// 统一走 TopLevel.StorageProvider（跨平台，Windows/macOS/Linux 同一套代码）。
    /// </summary>
    private async void OnOpenClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "打开 CAD 图纸",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("CAD 图纸 (DWG/DXF)")
                {
                    Patterns = new[] { "*.dwg", "*.dxf" },
                },
                FilePickerFileTypes.All,
            },
        });
        if (files is not { Count: > 0 }) return;

        // 桌面端返回的是本地文件；沙箱/远程平台可能为 null，此时明确提示而不是静默失败。
        var path = files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
        {
            StatusText.Text = "无法获取所选文件的本地路径";
            return;
        }

        await LoadDrawingAsync(path);
    }

    /// <summary>幂等保护：命令行自动加载最多尝试一次（OnOpened 与 App 两处调用点共用）。</summary>
    private bool _autoLoadAttempted;

    /// <summary>
    /// 窗口打开后尝试自动加载命令行指定的图纸。
    /// 用法：DrawingView.exe D:\path\to\file.dxf；无参数或文件不存在时保持空窗口。
    /// </summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        WriteDiag("enter OnOpened");
        TryAutoLoadFromCommandLine();
    }

    /// <summary>
    /// 尝试从命令行自动加载图纸（双来源兜底 + 幂等）。
    /// 候选来源：Program.Args 快照，加上 Environment.GetCommandLineArgs()（进程真实 argv）。
    /// 可被 OnOpened 与 App.OnFrameworkInitializationCompleted 两处安全重复调用，仅首次生效。
    /// </summary>
    public void TryAutoLoadFromCommandLine()
    {
        if (_autoLoadAttempted) return;
        _autoLoadAttempted = true;

        // Environment.GetCommandLineArgs() 第 0 项是 exe 自身路径，故 Skip(1) 只取用户参数。
        var path = Program.Args
            .Concat(Environment.GetCommandLineArgs().Skip(1))
            .FirstOrDefault(a => !string.IsNullOrWhiteSpace(a) && File.Exists(a));

        WriteDiag($"Program.Args.Length={Program.Args.Length}: [{string.Join(" | ", Program.Args)}]; "
                  + $"GetCommandLineArgs: [{string.Join(" | ", Environment.GetCommandLineArgs())}]; "
                  + $"selected={path ?? "none"}");

        if (path is not null)
            _ = LoadDrawingAsync(path);
    }

    /// <summary>
    /// 诊断日志：仅当环境变量 DrawingView_DIAG=1 时，向 %TEMP%\DrawingView_diag.log 追加一行。
    /// 用于排查命令行自动加载问题，正常使用不写任何文件。
    /// </summary>
    private static void WriteDiag(string message)
    {
        if (Environment.GetEnvironmentVariable("DrawingView_DIAG") != "1") return;
        try
        {
            var line = $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "DrawingView_diag.log"), line);
        }
        catch
        {
            // 诊断写入失败不应影响主流程
        }
    }

    /// <summary>当前打开的图纸路径（切换模型/布局时重载用）。</summary>
    private string? _currentPath;

    /// <summary>程序化改 SpaceBox 选中项时抑制事件，避免"切换 → 重载 → 再切换"的回环。</summary>
    private bool _suppressSpaceChange;

    /// <summary>
    /// 加载指定路径的图纸并刷新视图状态（文件对话框与命令行启动共用）。
    /// 加载结果为不可变快照，通过 View.SetDisplay(list) 原子替换。
    /// </summary>
    /// <param name="layout">要渲染的布局名；null = 模型空间。</param>
    private async Task LoadDrawingAsync(string path, string? layout = null)
    {
        try
        {
            _currentPath = path;
            StatusText.Text = $"加载中… {Path.GetFileName(path)}";
            var progress = new Progress<string>(s => StatusText.Text = $"{Path.GetFileName(path)} — {s}");
            var options = string.IsNullOrWhiteSpace(layout) ? null : new GeometryConvertOptions { LayoutName = layout };
            var list = await CadDocumentLoader.LoadAsync(path, progress, default, options);
            View.SetDisplay(list);
            BuildLayerPanel(list);
            SyncSpaceBox(list);
            StatusText.Text = $"{Path.GetFileName(path)} — {list.Entities.Count} 个实体，{list.LayerNames.Count} 个图层"
                              + (list.ActiveSpace == "Model" ? "" : $"（图纸空间：{list.ActiveSpace}）");
            if (list.ActiveSpace != "Model" && list.Entities.Count == 0)
                StatusText.Text = $"{Path.GetFileName(path)} — 布局「{list.ActiveSpace}」没有可绘制的常规实体"
                                  + "（几何由 ACIS/代理实体承载时 ACadSharp 读不出来，需 ODA/RealDWG 才能渲染）";
            WriteDiag($"load ok: {path} → {list.Entities.Count} entities, {list.LayerNames.Count} layers, space={list.ActiveSpace}");
        }
        catch (Exception ex)
        {
            WriteDiag($"load failed: {path} → {ex.GetType().Name}: {ex.Message}");
            StatusText.Text = $"加载失败: {ex.Message}";
        }
    }

    private void BuildLayerPanel(DisplayList list)
    {
        LayerList.Items.Clear();
        foreach (var name in list.LayerNames)
        {
            var cb = new CheckBox
            {
                Content = name,
                IsChecked = true,
                Margin = new Avalonia.Thickness(0, 2, 0, 2),
            };
            var layer = name;
            cb.IsCheckedChanged += (_, _) => View.SetLayerHidden(layer, cb.IsChecked != true);
            LayerList.Items.Add(cb);
        }
    }

    /// <summary>模型/布局切换：重新加载同一张图的另一个空间。</summary>
    private async void OnSpaceChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSpaceChange) return;
        if (_currentPath is not { } path) return;
        var selected = SpaceBox.SelectedItem as string;
        if (string.IsNullOrEmpty(selected)) return;
        var layout = selected == ModelSpaceLabel ? null : selected;
        await LoadDrawingAsync(path, layout);
    }

    private const string ModelSpaceLabel = "模型";

    /// <summary>把可切换的空间填进下拉框并选中当前项（不触发重载）。</summary>
    private void SyncSpaceBox(DisplayList list)
    {
        _suppressSpaceChange = true;
        try
        {
            SpaceBox.Items.Clear();
            SpaceBox.Items.Add(ModelSpaceLabel);
            foreach (var name in list.LayoutNames) SpaceBox.Items.Add(name);
            SpaceBox.SelectedItem = list.ActiveSpace == "Model" ? ModelSpaceLabel : list.ActiveSpace;
        }
        finally
        {
            _suppressSpaceChange = false;
        }
    }

    private void OnFitClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => View.FitToContent();

    private void OnZoomInClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => View.ZoomBy(1.25f);

    private void OnZoomOutClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => View.ZoomBy(0.8f);

    /// <summary>显示线宽开关（AutoCAD 的 LWDISPLAY）。关闭时全部按 1px 细线画。</summary>
    private void OnLineWeightClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => View.ShowLineWeights = ChkLineWeight.IsChecked == true;
}
