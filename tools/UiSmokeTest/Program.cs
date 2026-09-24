using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CadViewer.Controls;
using CadViewer.Core;
using SkiaSharp;
using System.Runtime.InteropServices;

// UI 冒烟测试：让 CadViewControl 在无头 Avalonia 平台上真实跑一遍
// SetDisplay → 布局 → 渲染管线，专门拦截"渲染期间非法操作"这类
// 只有真正渲染才会暴露的缺陷（tools/SmokeTest 不经过渲染管线，覆盖不到）。
//
// 已知回归用例：Render 内调用 InvalidateVisual 会抛
// InvalidOperationException: Visual was invalidated during the render pass。
//
// 结果同时写 Console 与日志文件（本机控制台重定向偶发丢输出，日志文件更可靠）。
// 日志路径：环境变量 CADVIEWER_UISMOKE_LOG，默认 %TEMP%\cadviewer_uismoke.log

var logPath = Environment.GetEnvironmentVariable("CADVIEWER_UISMOKE_LOG")
              ?? Path.Combine(Path.GetTempPath(), "cadviewer_uismoke.log");
File.WriteAllText(logPath, string.Empty);

void Log(string message)
{
    Console.WriteLine(message);
    try { File.AppendAllText(logPath, message + Environment.NewLine); } catch { /* 日志失败不影响主流程 */ }
}

AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    Log($"!!! UNHANDLED: {e.ExceptionObject}");

try
{
    return Run(args);
}
catch (Exception ex)
{
    Log($"!!! FATAL {ex.GetType().Name}: {ex.Message}");
    Log(ex.ToString());
    return 2;
}

int Run(string[] argv)
{
    var samplePath = argv.Length > 0 ? argv[0] : FindSample();
    if (samplePath is null || !File.Exists(samplePath))
    {
        Log("FAIL: 找不到样例 DXF（可用参数指定路径）");
        return 1;
    }

    Log($"无头平台启动（Skia 真实绘制），样例: {samplePath}");

    AppBuilder.Configure<CadViewer.App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .UseSkia()
        .SetupWithoutStarting();
    Log("AppBuilder 初始化完成");

    int fails = 0;
    void Check(bool ok, string name)
    {
        Log($"  {(ok ? "PASS" : "FAIL")} - {name}");
        if (!ok) fails++;
    }

    var view = new CadViewControl();
    var window = new Window { Width = 800, Height = 600, Content = view };
    window.Show();
    Log($"窗口已显示，控件布局 {view.Bounds.Width:F0}x{view.Bounds.Height:F0}");

    var list = CadDocumentLoader.LoadAsync(samplePath).GetAwaiter().GetResult();
    Log($"已加载: {list.Entities.Count} 实体 / {list.LayerNames.Count} 图层 [{string.Join(", ", list.LayerNames)}]");

    // ---------- 1. 首帧渲染（原崩溃点） ----------
    bool rendered = true;
    string? error = null;
    try
    {
        view.SetDisplay(list);
        window.CaptureRenderedFrame();
    }
    catch (Exception ex)
    {
        rendered = false;
        error = $"{ex.GetType().Name}: {ex.Message}";
    }
    Check(rendered, "SetDisplay 后首帧渲染不抛异常");
    if (error is not null) Log($"       {error}");

    // ---------- 2. 适应窗口后内容应完整可见且居中 ----------
    float w = (float)view.Bounds.Width, h = (float)view.Bounds.Height;
    Check(w > 1 && h > 1, $"控件已完成布局（{w:F0}x{h:F0}）");

    var ext = list.Extents;
    var topLeft = view.Viewport.WorldToScreen(new SKPoint(ext.Left, ext.Top));
    var bottomRight = view.Viewport.WorldToScreen(new SKPoint(ext.Right, ext.Bottom));
    bool insideScreen = topLeft.X >= -0.5f && topLeft.Y >= -0.5f &&
                        bottomRight.X <= w + 0.5f && bottomRight.Y <= h + 0.5f;
    Log($"       范围映射: L={topLeft.X:F1} T={topLeft.Y:F1} R={bottomRight.X:F1} B={bottomRight.Y:F1}, zoom={view.Viewport.Zoom:F4}");
    Check(insideScreen, "图纸范围完整落在视口内");

    float centerX = (topLeft.X + bottomRight.X) / 2f, centerY = (topLeft.Y + bottomRight.Y) / 2f;
    Check(Math.Abs(centerX - w / 2f) < 1f && Math.Abs(centerY - h / 2f) < 1f,
          $"图纸居中（中心 {centerX:F1},{centerY:F1} vs 视口中心 {w / 2f:F1},{h / 2f:F1}）");

    // ---------- 3. 渲染管线外的适应窗口 / 缩放仍正常 ----------
    rendered = true;
    error = null;
    try
    {
        view.FitToContent();
        view.ZoomBy(2f);
        window.CaptureRenderedFrame();
    }
    catch (Exception ex)
    {
        rendered = false;
        error = $"{ex.GetType().Name}: {ex.Message}";
    }
    Check(rendered, "外部调用 FitToContent / ZoomBy 后重绘不抛异常");
    if (error is not null) Log($"       {error}");

    // ---------- 4. 图层隐藏走快照，渲染不受影响 ----------
    rendered = true;
    error = null;
    try
    {
        view.SetLayerHidden(list.LayerNames[0], true);
        window.CaptureRenderedFrame();
        view.SetLayerHidden(list.LayerNames[0], false);
        window.CaptureRenderedFrame();
    }
    catch (Exception ex)
    {
        rendered = false;
        error = $"{ex.GetType().Name}: {ex.Message}";
    }
    Check(rendered, "隐藏图层后重绘不抛异常");
    if (error is not null) Log($"       {error}");

    // ---------- 5. 重复渲染稳定性（原崩溃点是每帧都会触发） ----------
    rendered = true;
    error = null;
    try
    {
        for (int i = 0; i < 5; i++) window.CaptureRenderedFrame();
    }
    catch (Exception ex)
    {
        rendered = false;
        error = $"{ex.GetType().Name}: {ex.Message}";
    }
    Check(rendered, "连续 5 帧渲染不抛异常");
    if (error is not null) Log($"       {error}");

    // ---------- 6. 双击适应窗口（基准 = 适应窗口应有的缩放，而非缩放前的值） ----------
    view.FitToContent();
    double fitted = view.Viewport.Zoom;
    view.ZoomBy(8f);
    double zoomed = view.Viewport.Zoom;
    window.MouseDown(new Point(400, 300), MouseButton.Left);
    window.MouseUp(new Point(400, 300), MouseButton.Left);
    window.MouseDown(new Point(400, 300), MouseButton.Left);
    window.MouseUp(new Point(400, 300), MouseButton.Left);
    window.CaptureRenderedFrame();
    if (Math.Abs(view.Viewport.Zoom - zoomed) < 1e-6f)
        Log("  SKIP - 双击适应窗口（无头平台未合成双击手势，需真实桌面环境验证）");
    else
        Check(Math.Abs(view.Viewport.Zoom - fitted) < 1e-3f,
              $"双击适应窗口（{zoomed:F3} → {view.Viewport.Zoom:F3}，期望 {fitted:F3}）");

    // ---------- 7. 资源释放：换图后旧 DisplayList 必须被回收 ----------
    // 每张图的几何都是一批 SKPath（原生内存，GC 管不到）。若换图时不释放，
    // 连开几张 5MB 级图纸就会吃掉数 GB。释放依赖 Avalonia 在丢弃绘制操作时
    // 回调 ICustomDrawOperation.Dispose() 归还引用 —— 这里就是实测该行为。
    int disposedBefore = DisplayList.DisposedCount;
    var firstList = list;
    var secondList = CadDocumentLoader.LoadAsync(samplePath).GetAwaiter().GetResult();
    view.SetDisplay(secondList);      // 所有权转移给控件，控件归还 firstList 的引用
    window.CaptureRenderedFrame();
    window.CaptureRenderedFrame();
    bool released = firstList.IsDisposed;
    Check(released,
          $"换图后旧 DisplayList 已释放（DisposedCount {disposedBefore} → {DisplayList.DisposedCount}）");
    if (!released)
        Log("       说明：Avalonia 未回调 Dispose，引用计数无法归零，需改用延迟释放策略");

    // 同一实例重复 SetDisplay 不得把正在使用的数据释放掉（引用计数自伤防护）
    view.SetDisplay(secondList);
    window.CaptureRenderedFrame();
    Check(!secondList.IsDisposed, "重复 SetDisplay 同一实例不会误释放");

    // ---------- 8. 叠加高亮层（临时几何：多段线单段 / 填充环 / 块内子实体） ----------
    // 断言方式：像素差分。"渲染不抛异常"证明不了几何真的画到画面上，而这类目标
    // 比一个 CadEntity 更细，SetSelection 表达不了，只能走叠加通道。
    // 判据用增量而不是绝对值 —— 图纸本身就可能含洋红实体（ACI 6）。
    const uint Magenta = 0xFFFF00FF;
    view.FitToContent();
    int mag0 = CountMagenta(window.CaptureRenderedFrame());
    Check(mag0 >= 0, $"能读取渲染帧像素（洋红基线 {mag0}）");

    var rect = ext;
    var overlayPath = new SKPath();
    overlayPath.AddRect(rect);
    var layer = new OverlayLayer
    {
        Shapes = new[]
        {
            new OverlayShape { Path = overlayPath, Fill = true, ColorArgb = Magenta, FillAlpha = 40 },
            OverlayShape.Box(rect, colorArgb: Magenta),
        },
    };
    view.SetOverlay(layer);
    int mag1 = CountMagenta(window.CaptureRenderedFrame());
    Check(mag1 > mag0 + 200, $"叠加层已画到画面上（洋红像素 {mag0} → {mag1}）");
    Check(view.OverlayShapeCount == 2, $"叠加层图元数（{view.OverlayShapeCount}，期望 2）");
    DumpFrame(window, Log);

    // 缩放跟随：叠加几何是世界坐标，缩放后应仍然贴合目标（而不是停在屏幕原位）
    view.ZoomBy(3f);
    window.CaptureRenderedFrame();
    Check(view.ZoomToOverlay(), "ZoomToOverlay 命中叠加几何并缩放");
    var ovl = view.Viewport.WorldToScreen(new SKPoint(rect.Left, rect.Top));
    var ovr = view.Viewport.WorldToScreen(new SKPoint(rect.Right, rect.Bottom));
    Check(ovl.X >= -0.5f && ovl.Y >= -0.5f && ovr.X <= w + 0.5f && ovr.Y <= h + 0.5f,
          $"叠加目标完整落在视口内（L={ovl.X:F1} T={ovl.Y:F1} R={ovr.X:F1} B={ovr.Y:F1}）");

    // 叠加层不走 LOD：把普通几何/文字的可见阈值都抬到不可能满足，画面理应只剩叠加层
    view.MinEntityPixelSize = 1e9f;
    view.MinTextPixelSize = 1e9f;
    int mag2 = CountMagenta(window.CaptureRenderedFrame());
    Check(mag2 > 200, $"叠加层不受 LOD 裁剪（普通几何全被剔除后仍有 {mag2} 个洋红像素）");

    // 清除后不留残影（此刻画面上本应只剩叠加层，故可断言"归零"而非"减少"）
    view.ClearOverlay();
    int mag3 = CountMagenta(window.CaptureRenderedFrame());
    Check(mag3 == 0, $"ClearOverlay 后无残影（洋红像素 {mag2} → {mag3}）");

    // 换图必须自动清空：叠加几何是上一张图的**世界坐标**，留着会画到完全错误的位置
    var layer2 = new OverlayLayer { Shapes = new[] { OverlayShape.Box(rect, colorArgb: Magenta) } };
    view.SetOverlay(layer2);
    window.CaptureRenderedFrame();
    Check(CountMagenta(window.CaptureRenderedFrame()) > 0, "换图前叠加层可见（换图测试的前置条件）");
    int disposedBeforeOverlay = OverlayLayer.DisposedCount;
    view.SetDisplay(secondList);
    window.CaptureRenderedFrame();
    int mag4 = CountMagenta(window.CaptureRenderedFrame());
    Check(view.OverlayShapeCount == 0, $"换图后叠加层已自动清空（图元数 {view.OverlayShapeCount}）");
    Check(mag4 == 0, $"换图后画面上无叠加层残留（洋红像素 {mag4}）");
    Check(layer2.IsDisposed && OverlayLayer.DisposedCount > disposedBeforeOverlay,
          $"换图后叠加层原生几何已归还（DisposedCount {disposedBeforeOverlay} → {OverlayLayer.DisposedCount}）");

    view.MinEntityPixelSize = 1.5f;
    view.MinTextPixelSize = CadViewControl.DefaultMinTextPixelSize;

    if (fails == 0)
    {
        Log("UI SMOKE TEST PASSED");
        return 0;
    }
    Log($"UI SMOKE TEST FAILED ({fails} 项)");
    return 1;
}

/// <summary>
/// 统计画面里"接近洋红"的像素数。抗锯齿边缘会与白底混合，所以判据留了余量；
/// 调用方一律用**增量**判断（叠加前 → 叠加后），图纸本身含洋红实体（ACI 6）也不会误判。
/// 返回 -1 表示拿不到渲染帧。
/// </summary>
static int CountMagenta(WriteableBitmap? frame)
{
    if (frame is null) return -1;
    using var fb = frame.Lock();
    bool bgra = fb.Format == PixelFormat.Bgra8888;
    int w = fb.Size.Width, h = fb.Size.Height, stride = fb.RowBytes;
    var row = new byte[stride];
    int count = 0;
    for (int y = 0; y < h; y++)
    {
        Marshal.Copy(IntPtr.Add(fb.Address, y * stride), row, 0, stride);
        for (int x = 0; x < w; x++)
        {
            int o = x * 4;
            byte c0 = row[o], c1 = row[o + 1], c2 = row[o + 2];
            byte r = bgra ? c2 : c0;
            byte g = c1;
            byte b = bgra ? c0 : c2;
            if (r >= 200 && b >= 200 && g <= 180) count++;
        }
    }
    return count;
}

/// <summary>可选：把当前帧导出为 PNG（环境变量 CADVIEWER_UISMOKE_PNG），供人工核对观感。</summary>
static void DumpFrame(Window window, Action<string> log)
{
    var png = Environment.GetEnvironmentVariable("CADVIEWER_UISMOKE_PNG");
    if (string.IsNullOrEmpty(png)) return;
    try
    {
        var frame = window.CaptureRenderedFrame();
        frame?.Save(png, new PngBitmapEncoderOptions());
        log($"       画面已导出: {png}");
    }
    catch (Exception ex)
    {
        log($"       导出 PNG 失败: {ex.Message}");
    }
}

static string? FindSample()
{
    // 从输出目录向上找 samples/，避免写死盘符（与 SmokeTest 硬编码路径的区别）
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        var candidate = Path.Combine(dir.FullName, "samples", "sample_r12.dxf");
        if (File.Exists(candidate)) return candidate;
        dir = dir.Parent;
    }
    return null;
}
