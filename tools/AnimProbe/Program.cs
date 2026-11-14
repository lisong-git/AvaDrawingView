using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DrawingView.Controls;
using DrawingView.Core;
using SkiaSharp;

// 探针：动画期间「只改变换、不重绘内容」这条路是否成立。
//
// 要回答两件事，缺一都不能下结论：
//   ① 只改 RenderTransform 且不 InvalidateVisual 时，Render() 还会不会被重新执行
//      （会 = 这条路没有意义，内容照样重画一遍）；
//   ② 每帧的实际墙钟成本，以及画面确实动了（不是只改了属性没人理会）。
//
// 三个配置各采 N 帧：不动（固定开销基线）/ 改视口+Invalidate / 只改 RenderTransform。
// 结果写 Console + 日志文件；环境变量 DV_ANIM_LOG 覆盖日志路径。

var logPath = Environment.GetEnvironmentVariable("DV_ANIM_LOG")
              ?? Path.Combine(Path.GetTempPath(), "dv_anim.log");
File.WriteAllText(logPath, string.Empty);

void Log(string message)
{
    Console.WriteLine(message);
    try { File.AppendAllText(logPath, message + Environment.NewLine); } catch { }
}

try { return Run(args); }
catch (Exception ex)
{
    Log($"!!! FATAL {ex.GetType().Name}: {ex.Message}");
    Log(ex.ToString());
    return 2;
}

int Run(string[] argv)
{
    var path = argv.Length > 0 ? argv[0] : @"D:\Work\上海八局\测试文件\图纸集\2015-1\1-DY-15.dwg";
    int frames = argv.Length > 1 && int.TryParse(argv[1], out var n) ? n : 15;
    if (!File.Exists(path)) { Log($"FAIL: 文件不存在 {path}"); return 1; }

    // locate 模式：验证「显式跳转会通知，且闪烁反馈不额外重绘 CAD」
    if (argv.Length > 1 && argv[1] == "locate") return RunLocate(path);

    AppBuilder.Configure<DrawingView.App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .UseSkia()
        .SetupWithoutStarting();

    var view = new CountingView();
    var host = new Grid();
    host.Children.Add(view);
    var window = new Window { Width = 800, Height = 560, Content = host };
    window.Show();

    var list = CadDocumentLoader.LoadAsync(path).GetAwaiter().GetResult();
    view.SetDisplay(list);
    Log($"样例 {Path.GetFileName(path)}：{list.Entities.Count} 实体，控件 {view.Bounds.Width:F0}x{view.Bounds.Height:F0}");

    // 预热：首帧还要跑 fit，别把它的成本摊进样本
    for (int i = 0; i < 4; i++) { view.InvalidateVisual(); Pump(window); }
    Log($"预热完成，静帧 drawMs={view.LastDrawMs:F1}");

    var idle = Measure(window, view, frames, _ => { });
    var redraw = Measure(window, view, frames, _ =>
    {
        view.Viewport.Zoom *= 1.02f;
        view.InvalidateVisual();
    });

    var scale = new MatrixTransform(Matrix.Identity) { };
    view.RenderTransform = scale;
    view.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
    double k = 1.0;
    var xform = Measure(window, view, frames, _ =>
    {
        k *= 1.02;
        scale.Matrix = Matrix.CreateScale(k, k);
    });

    Report("静帧（只 capture，固定开销）", idle);
    Report("改视口 + InvalidateVisual", redraw);
    Report("只改 RenderTransform（不 Invalidate）", xform);

    // 第四条路：把当前画面抓成位图盖在上面，之后每帧只变换这张位图。
    // 只有这一条真的不碰几何——下面用它自己的 Render 增量证明。
    view.RenderTransform = new MatrixTransform(Matrix.Identity);
    Pump(window);
    var rtb = new RenderTargetBitmap(new PixelSize((int)view.Bounds.Width, (int)view.Bounds.Height), new Vector(96, 96));
    rtb.Render(view);
    var img = new Image
    {
        Source = rtb,
        Stretch = Stretch.None,
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
        RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
    };
    var host2 = host;
    host2.Children.Clear();
    host2.Children.Add(img);
    view.IsVisible = false;
    Pump(window);

    double bk = 1.0;
    var snap = Measure(window, view, frames, _ =>
    {
        bk *= 1.02;
        img.RenderTransform = new MatrixTransform(Matrix.CreateScale(bk, bk));
    });
    Report("位图快照 + 只改变换", snap);

    // 画面到底动没动：变换前后的墨迹包围盒应该不同
    host.Children.Clear();
    host.Children.Add(view);
    view.IsVisible = true;
    view.RenderTransform = new MatrixTransform(Matrix.Identity);
    Pump(window);
    var before = InkBox(window);
    k = 1.12;
    scale.Matrix = Matrix.CreateScale(k, k);
    view.RenderTransform = scale;
    Pump(window);
    var after = InkBox(window);

    Log("");
    Log($"变换前墨迹 bbox {before}  变换后 {after}");
    bool moved = before is { } b && after is { } a
                 && (Math.Abs(b.Width - a.Width) > 2 || Math.Abs(b.Height - a.Height) > 2);
    Log(moved ? "PASS 只改变换确实重排了画面" : "FAIL 只改变换后画面没变（合成器没理会 / 需要 invalidate）");
    Log($"合计：静帧 {Med(idle):F1}ms/帧、重绘 {Med(redraw):F1}ms/帧、变换 {Med(xform):F1}ms/帧、快照 {Med(snap):F1}ms/帧");
    return moved ? 0 : 3;
}

void Pump(Window window)
{
    Dispatcher.UIThread.RunJobs();
    window.CaptureRenderedFrame();
}

// 验证「跳转 + 反馈」这条链路的两条关键性质：
//   ① 显式跳转会发 Located（带正确的屏幕矩形），首屏/适应窗口不发；
//   ② 闪烁环重绘期间，画布的绘制操作一次都不增加。
int RunLocate(string path)
{
    AppBuilder.Configure<DrawingView.App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .UseSkia()
        .SetupWithoutStarting();

    var view = new CountingView();
    var marker = new CountingMarker();
    var host = new Grid();
    host.Children.Add(view);
    host.Children.Add(marker);
    var window = new Window { Width = 800, Height = 560, Content = host };
    window.Show();

    var list = CadDocumentLoader.LoadAsync(path).GetAwaiter().GetResult();
    view.SetDisplay(list);
    Pump(window);
    Pump(window);
    Log($"样例 {Path.GetFileName(path)}：{list.Entities.Count} 实体，控件 {view.Bounds.Width:F0}x{view.Bounds.Height:F0}");

    CadEntity? target = null;
    foreach (var e in list.Entities)
    {
        if (e.Handle == 0) continue;
        if (e.Bounds.Width <= 0 && e.Bounds.Height <= 0) continue;
        if (target is null || e.Bounds.Width * e.Bounds.Height > target.Bounds.Width * target.Bounds.Height)
            target = e;
    }
    if (target is null) { Log("FAIL: 找不到可定位的实体"); return 1; }

    int fired = 0;
    ViewportLocatedEventArgs? got = null;
    view.Located += (_, e) => { fired++; got = e; };

    bool jumped = view.ZoomTo(target.Handle);
    Pump(window);
    bool pass1 = jumped && fired == 1 && got is not null;
    if (got is not null)
    {
        var c = got.Screen.Center;
        double dx = c.X - view.Bounds.Width / 2, dy = c.Y - view.Bounds.Height / 2;
        bool centered = Math.Abs(dx) < 3 && Math.Abs(dy) < 3;
        bool sized = got.Screen.Width > 0 || got.Screen.Height > 0;
        pass1 &= centered && sized;
        Log($"  按句柄跳转：事件 {fired} 次，屏幕矩形 {got.Screen}，中心偏移 ({dx:F1},{dy:F1})，缩放 {got.Zoom:F4}"
            + (pass1 ? "  PASS" : "  FAIL"));
    }
    else Log("  按句柄跳转：未收到 Located  FAIL");

    fired = 0;
    view.FitToContent();
    Pump(window);
    bool pass2 = fired == 0;
    Log($"  适应窗口：事件 {fired} 次（应为 0）" + (pass2 ? "  PASS" : "  FAIL"));

    fired = 0;
    view.ZoomToOverlay();
    Pump(window);
    Log($"  叠加层跳转（当前无叠层，应不发事件）：事件 {fired} 次" + (fired == 0 ? "  PASS" : "  FAIL"));
    pass2 &= fired == 0;

    if (got is null) return 3;

    long draws0 = view.DrawCount;
    // 对照：什么都不做也空转 7 帧 —— 用来区分「重绘是环引起的」还是「无头抓帧本身每帧全渲」
    for (int i = 0; i < 7; i++) Pump(window);
    long idleDraws = view.DrawCount;
    Log($"  对照 空转 7 帧：画布绘制操作 {draws0} → {idleDraws}（+{idleDraws - draws0}），控件 Render {view.RenderCount} 次");

    // 一次闪烁的全过程应该只花 2 帧重绘：出现 1 帧 + 消失 1 帧（停留期间不动画）
    int amber0 = CountAmber(window);
    marker.Flash(got.Screen, view.LastDrawMs);
    Pump(window);
    long d1 = view.DrawCount;
    int amber1 = CountAmber(window);
    for (int i = 0; i < 6; i++) Pump(window);
    long d2 = view.DrawCount;
    marker.Hide();
    Pump(window);
    long d3 = view.DrawCount;
    int amber2 = CountAmber(window);

    bool pass3 = marker.LastMode != "off" && d1 - idleDraws == 1 && d2 == d1 && d3 - d2 == 1
                 && amber1 > amber0 && amber2 <= amber0;
    Log($"  闪烁全过程（上一帧 drawMs {view.LastDrawMs:F1}，档位 {marker.LastMode}）："
        + $"出现 +{d1 - idleDraws} 帧、停留 +{d2 - d1} 帧、消失 +{d3 - d2} 帧；"
        + $"琥珀像素 {amber0} → {amber1} → {amber2}" + (pass3 ? "  PASS" : "  FAIL"));

    bool all = pass1 && pass2 && pass3;
    Log(all ? "PASS 跳转反馈链路全部通过" : "FAIL 有断言未通过");
    return all ? 0 : 3;
}

int CountAmber(Window window)
{
    var bmp = window.CaptureRenderedFrame();
    if (bmp is null) return -1;
    using var ms = new MemoryStream();
    bmp.Save(ms);
    ms.Position = 0;
    using var sk = SKBitmap.Decode(ms);
    if (sk is null) return -1;
    int n = 0;
    for (int y = 0; y < sk.Height; y++)
        for (int x = 0; x < sk.Width; x++)
        {
            var c = sk.GetPixel(x, y);
            if (c.Red > 150 && c.Green is > 90 and < 220 && c.Blue < 120) n++;
        }
    return n;
}

Stats Measure(Window window, CountingView view, int frames, Action<int> step)
{
    GC.Collect();
    GC.WaitForPendingFinalizers();

    var render0 = view.RenderCount;
    var wall = new List<double>(frames);
    for (int i = 0; i < frames; i++)
    {
        step(i);
        var sw = Stopwatch.StartNew();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame();
        sw.Stop();
        wall.Add(sw.Elapsed.TotalMilliseconds);
    }
    return new Stats(wall, view.RenderCount - render0, view.LastDrawMs);
}

void Report(string name, Stats s)
{
    var sorted = s.Wall.OrderBy(x => x).ToArray();
    double med = sorted[sorted.Length / 2];
    Log($"  {name,-34} 中位 {med,7:F1}ms  最小 {sorted[0],7:F1}  最大 {sorted[^1],7:F1}  " +
        $"Render() 增量 {s.Renders,4}（{s.Renders / (double)s.Wall.Count:F2}/帧）  drawMs {s.DrawMs:F1}");
}

SKRect? InkBox(Window window)
{
    var bmp = window.CaptureRenderedFrame();
    if (bmp is null) return null;
    using var ms = new MemoryStream();
    bmp.Save(ms);
    ms.Position = 0;
    using var sk = SKBitmap.Decode(ms);
    if (sk is null) return null;
    int l = int.MaxValue, t = int.MaxValue, r = int.MinValue, b = int.MinValue;
    for (int y = 0; y < sk.Height; y++)
        for (int x = 0; x < sk.Width; x++)
        {
            var c = sk.GetPixel(x, y);
            if (c.Red + c.Green + c.Blue > 60)
            {
                if (x < l) l = x;
                if (x > r) r = x;
                if (y < t) t = y;
                if (y > b) b = y;
            }
        }
    return r < l ? null : new SKRect(l, t, r, b);
}

double Med(Stats s) => s.Wall.OrderBy(x => x).ElementAt(s.Wall.Count / 2);

readonly record struct Stats(List<double> Wall, int Renders, double DrawMs);

sealed class CountingView : DrawingView.Controls.DrawingView
{
    public int RenderCount;
    public override void Render(DrawingContext context)
    {
        RenderCount++;
        base.Render(context);
    }
}

sealed class CountingMarker : LocateMarker
{
    public int RenderCount;
    public override void Render(DrawingContext context)
    {
        RenderCount++;
        base.Render(context);
    }
}
