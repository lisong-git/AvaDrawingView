using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using DrawingView.Controls;
using DrawingView.Core;
using SkiaSharp;
using System.Runtime.InteropServices;

// UI 冒烟测试：让 DrawingView 在无头 Avalonia 平台上真实跑一遍
// SetDisplay → 布局 → 渲染管线，专门拦截"渲染期间非法操作"这类
// 只有真正渲染才会暴露的缺陷（tools/SmokeTest 不经过渲染管线，覆盖不到）。
//
// 已知回归用例：Render 内调用 InvalidateVisual 会抛
// InvalidOperationException: Visual was invalidated during the render pass。
//
// 结果同时写 Console 与日志文件（本机控制台重定向偶发丢输出，日志文件更可靠）。
// 日志路径：环境变量 DrawingView_UISMOKE_LOG，默认 %TEMP%\DrawingView_uismoke.log

var logPath = Environment.GetEnvironmentVariable("DrawingView_UISMOKE_LOG")
              ?? Path.Combine(Path.GetTempPath(), "DrawingView_uismoke.log");
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

    AppBuilder.Configure<DrawingView.App>()
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

    var view = new DrawingView.Controls.DrawingView();
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

    // ---------- 5b. 关抗锯齿开关真的生效（--no-aa / DrawingView_NO_AA=1）----------
    // 判据：AA 打开时每条细线两侧有一圈暗边（通道值 8~40），关掉后这些像素应基本消失。
    // 这个开关只服务"与 ODA 逐像素对拍"（ODA 出图几乎不做 AA），产品里默认开。
    Check(DrawingView.Controls.DrawingView.Antialias, "抗锯齿默认开（产品口径）");
    view.InvalidateVisual();
    window.CaptureRenderedFrame();
    int dimAa = CountDimInk(window.CaptureRenderedFrame());
    DrawingView.Controls.DrawingView.Antialias = false;
    view.InvalidateVisual();
    window.CaptureRenderedFrame();
    int dimNoAa = CountDimInk(window.CaptureRenderedFrame());
    DrawingView.Controls.DrawingView.Antialias = true;
    view.InvalidateVisual();
    window.CaptureRenderedFrame();
    Log($"       暗边像素：AA 开 {dimAa}，AA 关 {dimNoAa}");
    Check(dimAa > 0 && dimNoAa >= 0, $"两种模式都取到像素（{dimAa} / {dimNoAa}）");
    Check(dimAa > dimNoAa * 3, $"关掉抗锯齿后细线暗边显著减少（{dimAa} → {dimNoAa}）");

    // ---------- 6. 双击中键适应窗口（AutoCAD 语义；基准 = 适应窗口应有的缩放） ----------
    view.FitToContent();
    double fitted = view.Viewport.Zoom;
    view.ZoomBy(8f);
    double zoomed = view.Viewport.Zoom;
    window.MouseDown(new Point(400, 300), MouseButton.Middle);
    window.MouseUp(new Point(400, 300), MouseButton.Middle);
    window.MouseDown(new Point(400, 300), MouseButton.Middle);
    window.MouseUp(new Point(400, 300), MouseButton.Middle);
    window.CaptureRenderedFrame();
    Check(Math.Abs(view.Viewport.Zoom - fitted) < 1e-3f,
          $"双击中键适应窗口（{zoomed:F3} → {view.Viewport.Zoom:F3}，期望 {fitted:F3}）");

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
    bool released = WaitUntil(() => firstList.IsDisposed, 3000);
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
    bool overlayReleased = WaitUntil(() => layer2.IsDisposed && OverlayLayer.DisposedCount > disposedBeforeOverlay, 3000);
    Check(overlayReleased,
          $"换图后叠加层原生几何已归还（DisposedCount {disposedBeforeOverlay} → {OverlayLayer.DisposedCount}）");

    view.MinEntityPixelSize = 1.5f;
    view.MinTextPixelSize = DrawingView.Controls.DrawingView.DefaultMinTextPixelSize;

    // ---------- 8b. 框选带：窗选 / 窗交 的配色与文字提示 ----------
    // 判据落在光标右下方那个小块里：提示牌摆在框角**之外**（cursor+14/+16），
    // 摆带之前该区域必是纯背景，所以那里出现的强调色像素只能来自提示文字本身。
    // 先把普通几何整档剔掉（LOD 阈值抬到不可满足），画面里就只剩带、提示牌与字。
    view.SetBandForTest(null, default);
    view.MinEntityPixelSize = 1e9f;
    view.MinTextPixelSize = 1e9f;
    view.InvalidateVisual();
    window.CaptureRenderedFrame();
    var blank = window.CaptureRenderedFrame();
    int cueBlue0 = CountHue(blank, 414, 316, 120, 26, bluish: true);
    int cueGreen0 = CountHue(blank, 414, 316, 120, 26, bluish: false);
    Check(cueBlue0 == 0 && cueGreen0 == 0, $"提示区摆带前是纯背景（蓝 {cueBlue0} / 绿 {cueGreen0}）");

    view.SetBandForTest(new Point(200, 150), new Point(400, 300));      // 从左往右 = 窗选
    window.CaptureRenderedFrame();
    var winBand = window.CaptureRenderedFrame();
    int winBlue = CountHue(winBand, 414, 316, 120, 26, bluish: true);
    int winGreen = CountHue(winBand, 414, 316, 120, 26, bluish: false);
    Check(winBlue > 15, $"窗选提示文字已画出（蓝像素 {cueBlue0} → {winBlue}）");
    Check(winGreen == 0, $"窗选时提示区没有绿色（{winGreen}）");

    view.SetBandForTest(new Point(620, 120), new Point(400, 300));      // 从右往左 = 窗交
    window.CaptureRenderedFrame();
    var crossBand = window.CaptureRenderedFrame();
    int crossGreen = CountHue(crossBand, 414, 316, 120, 26, bluish: false);
    int crossBlue = CountHue(crossBand, 414, 316, 120, 26, bluish: true);
    Check(crossGreen > 15, $"窗交提示文字已画出（绿像素 {cueGreen0} → {crossGreen}）");
    Check(crossBlue == 0, $"窗交时提示区没有蓝色（{crossBlue}）");

    view.SetBandForTest(null, default);
    window.CaptureRenderedFrame();
    var cleared = window.CaptureRenderedFrame();
    Check(CountHue(cleared, 414, 316, 120, 26, bluish: true)
          + CountHue(cleared, 414, 316, 120, 26, bluish: false) == 0,
          "清除框选带后提示不残留");

    // ---------- 8c. 大量选中：高亮降档后依然可见 ----------
    // 完整样式是每实体三次 DrawPath（外圈 glow + 核心描边 + 填充罩），一次框上万条会吃掉整帧；
    // 超过 SelectionGlowLimit 只留核心描边 —— 断言两件事：降档后还看得见、且蓝色覆盖确实变少。
    view.MinEntityPixelSize = 1.5f;
    view.MinTextPixelSize = DrawingView.Controls.DrawingView.DefaultMinTextPixelSize;
    view.FitToContent();
    var pickedMany = secondList.Entities.Take(300).ToArray();
    view.ClearSelection();
    window.CaptureRenderedFrame();
    int hlBase = CountInk(window.CaptureRenderedFrame());
    view.SetSelection(pickedMany);
    view.SelectionGlowLimit = 0;
    window.CaptureRenderedFrame();
    int hlFull = CountInk(window.CaptureRenderedFrame());
    view.SelectionGlowLimit = Math.Max(1, pickedMany.Length / 2);
    window.CaptureRenderedFrame();
    int hlLean = CountInk(window.CaptureRenderedFrame());
    Log($"       选中 {pickedMany.Length} 条：无选中 {hlBase} / 完整样式 {hlFull} / 降档后 {hlLean}");
    Check(hlLean > hlBase, $"降档后选中仍然看得见（墨量 {hlBase} → {hlLean}）");
    Check(hlFull > hlLean, $"完整样式的覆盖更多（{hlFull} vs {hlLean}）");
    view.SelectionGlowLimit = 2000;
    view.ClearSelection();

    // ---------- 8d. 双击由宿主接管后，画布必须吞掉这次点击 ----------
    // 双击的第二次按下同时也是「单击」。宿主把双击当成「进入」动作（进块 / 进面域）时，
    // 若松手仍走点选，用户刚进作用域就会在光标处顺手选中一个子图元。
    view.FitToContent();
    view.ClearSelection();
    var dblWorld = new SKPoint(secondList.Entities[0].Bounds.MidX, secondList.Entities[0].Bounds.MidY);
    var dblScreen = view.Viewport.WorldToScreen(dblWorld);
    var dblPt = new Point(dblScreen.X, dblScreen.Y);
    int dblHits = 0;
    void OnDbl(object? s, CanvasDoubleClickEventArgs e) { dblHits++; e.Handled = true; }
    view.CanvasDoubleClicked += OnDbl;
    window.MouseDown(dblPt, MouseButton.Left);
    window.MouseUp(dblPt, MouseButton.Left);
    int selAfterFirst = view.SelectedEntities.Count;
    window.MouseDown(dblPt, MouseButton.Left);
    window.MouseUp(dblPt, MouseButton.Left);
    window.CaptureRenderedFrame();
    Check(dblHits == 1, $"连按两次触发一次双击事件（{dblHits}）");
    Check(selAfterFirst >= 1, $"第一次点击已选中目标（{selAfterFirst}）");
    Check(view.SelectedEntities.Count == selAfterFirst,
          $"接管后第二次点击不再改动选中（{selAfterFirst} → {view.SelectedEntities.Count}）");
    view.CanvasDoubleClicked -= OnDbl;
    view.ClearSelection();

    // ---------- 12. 线宽显示（B3）：像素级口径 ----------
    // 判据全部落在"屏幕上多少像素"，因为线宽的两种显示口径的差别**只**体现在像素上：
    //   模型空间 = 固定像素宽（不随缩放变化）；图纸空间 = 真实单位（随缩放变化）。
    // 样例 sample_lineweight.dxf 是 5 条等长水平线，从下往上 0.13 / 0.50 / 2.11 / 1.00 / 0.50(BYBLOCK) mm。
    var lwPath = FindSampleFile("sample_lineweight.dxf");
    if (lwPath is null)
    {
        Check(false, "找得到线宽样例 sample_lineweight.dxf（先跑 tools/SampleGen）");
    }
    else
    {
        var lwList = CadDocumentLoader.LoadAsync(lwPath).GetAwaiter().GetResult();
        view.ShowLineWeights = false;
        view.SetDisplay(lwList);
        view.FitToContent();
        window.CaptureRenderedFrame();
        var offA = CrossSection(window.CaptureRenderedFrame());
        Log($"       线宽关：各线像素宽 {Str(offA)}");
        Check(offA.Count == 5 && offA.All(t => t is > 0.6f and < 1.6f),
              $"线宽关 → 5 条线全部约 1px（实际 [{Str(offA)}]）");

        view.ShowLineWeights = true;
        window.CaptureRenderedFrame();
        var onA = CrossSection(window.CaptureRenderedFrame());
        Log($"       线宽开（适应窗口, zoom={view.Viewport.Zoom:F3}）：各线像素宽 {Str(onA)}");
        // 自上而下：y=160(BYBLOCK 0.50) / 120(1.00) / 80(2.11) / 40(0.50) / 0(0.13)
        // 期望 ≈ 0.50→1.9 / 1.00→3.8 / 2.11→8.0 / 0.50→1.9 / 0.13→1.0（0.13mm 不足 1px，退回 1px）
        Check(onA.Count == 5, $"线宽开 → 仍是 5 条（实际 {onA.Count}）");
        if (onA.Count == 5)
        {
            Check(Near(onA[0], 1.89f, 0.6f), $"BYBLOCK 0.50mm → 1.9px（实际 {onA[0]:F2}）");
            Check(Near(onA[1], 3.78f, 0.8f), $"实体 1.00mm → 3.8px（实际 {onA[1]:F2}）");
            Check(Near(onA[2], 7.97f, 1.0f), $"图层 2.11mm → 8.0px（实际 {onA[2]:F2}）");
            Check(Near(onA[3], 1.89f, 0.6f), $"图层 0.50mm → 1.9px（实际 {onA[3]:F2}）");
            Check(Near(onA[4], 1.0f, 0.5f), $"0.13mm 不足 1px → 退回 1px（实际 {onA[4]:F2}）");
            Check(onA[2] > onA[1] && onA[1] > onA[0],
                  $"线宽序关系 0.13 ≈ 0.50 &lt; 1.00 &lt; 2.11 成立（[{Str(onA)}]）");
        }

        // 关键口径：模型空间线宽是**固定像素**，缩小视角后像素宽不变
        // （这正是 AutoCAD 里"缩小看线宽相对更粗"的成因）。这里缩到 0.6 倍，
        // 保持 5 条线都还在视口内（放大到 2 倍会把上下两条推出画面）。
        view.ZoomBy(0.6f);
        window.CaptureRenderedFrame();
        var onB = CrossSection(window.CaptureRenderedFrame());
        Log($"       线宽开（缩小 0.6x, zoom={view.Viewport.Zoom:F3}）：各线像素宽 {Str(onB)}");
        bool stable = onB.Count == 5 && onA.Count == 5
                      && Enumerable.Range(0, 5).All(i => Near(onB[i], onA[i], 0.35f));
        Check(stable, $"模型空间线宽不随缩放变化（0.6x 前后 [{Str(onA)}] vs [{Str(onB)}]）");

        // lwList 是本次新建的实例，SetDisplay 会接管它；上面那张 r12 的 list 早已被释放，
        // 不能再塞回去（已释放的 SKPath 再画会 AccessViolation，这是刻意留的注释）。
        view.ShowLineWeights = false;
        window.CaptureRenderedFrame();
    }

    // ---------- 13. MTEXT 排版（P2-4）：逐行对齐与行距只能在像素上验证 ----------
    // 转换端只负责把「行距系数 / 逐行对齐方式」记进 TextDraw，真正决定观感的是渲染端
    // 每一行的 x 偏移与 y 间距。数值断言（tools/SmokeTest）看不到这两件事，
    // 所以这里把 sample_mtext.dxf 画出来，直接量每行墨迹的水平中心与行距。
    var mtPath = FindSampleFile("sample_mtext.dxf");
    if (mtPath is null)
    {
        Check(false, "找得到 MTEXT 样例 sample_mtext.dxf（先跑 tools/SampleGen）");
    }
    else
    {
        var mtList = CadDocumentLoader.LoadAsync(mtPath).GetAwaiter().GetResult();
        view.SetDisplay(mtList);
        view.FitToContent();
        window.CaptureRenderedFrame();
        var frame = window.CaptureRenderedFrame();
        Log($"       MTEXT 样例 zoom={view.Viewport.Zoom:F3}");

        var textEnts = mtList.Entities.Where(e => e.Text is not null).ToList();
        var centeredEnt = textEnts.FirstOrDefault(e => Near(e.Text!.Position.X, 200f, 1f));
        var doubledEnt = textEnts.FirstOrDefault(e => e.Text!.LineSpacingFactor > 3f);
        var wrappedEnt = textEnts.FirstOrDefault(e => e.Text!.Text.Count(c => c == '\n') >= 3);

        // 居中块：3 行、"AAAA…/A/AAAAAA"，逐行居中后各行的墨迹中心必须重合在锚点 x=200 上。
        // 若渲染端只做整块居中（不逐行），短行的中心会偏到与块中心相当的位置 —— 差得足够大。
        if (centeredEnt is null) Check(false, "找到居中 MTEXT 实体");
        else
        {
            float anchorX = view.Viewport.WorldToScreen(new SKPoint(200f, 0f)).X;
            var bands = InkBands(frame, centeredEnt.Bounds, 4, view);
            Log($"       居中块各行墨迹（锚点屏幕 x={anchorX:F1}）：{BandsStr(bands)}");
            Check(bands.Count == 3, $"居中块画出 3 行（实际 {bands.Count}）");
            if (bands.Count == 3)
            {
                bool allCentered = bands.All(b => Near(b.MidX, anchorX, 3f));
                Check(allCentered,
                      $"三行各自居中于锚点（各中心 {string.Join(",", bands.Select(b => b.MidX.ToString("F1")))} vs {anchorX:F1}）");
                // 行宽差异要真的存在，否则"逐行居中"与"整块居中"无从区分
                float wMax = bands.Max(b => b.Width), wMin = bands.Min(b => b.Width);
                Check(wMax / MathF.Max(wMin, 1e-3f) > 3f,
                      $"各行宽度差异明显（{wMax:F1}px vs {wMin:F1}px）");
            }
        }

        // 双倍行距块：两行 "AAAA/AAAA"，行距 = 2 × 1.667 × 字高 = 33.3 世界单位。
        // 单倍会是 16.67 → 屏幕上差一倍，用像素间距即可判定组码 44 是否生效。
        if (doubledEnt is null) Check(false, "找到双倍行距 MTEXT 实体");
        else
        {
            var bands = InkBands(frame, doubledEnt.Bounds, 4, view);
            Log($"       双倍行距块：{BandsStr(bands)}");
            Check(bands.Count == 2, $"双倍行距块画出 2 行（实际 {bands.Count}）");
            if (bands.Count == 2)
            {
                float pitch = MathF.Abs(bands[1].MidY - bands[0].MidY) / view.Viewport.Zoom;
                Check(Near(pitch, 10f * 2f * TextMetrics.LineSpacingFactor, 1.2f),
                      $"基线间距 ≈ 33.34 世界单位（实际 {pitch:F2}，单倍会是 16.67）");
            }
        }

        // 栏宽断行块：每行墨迹宽度不得超过栏宽 60（世界单位）
        if (wrappedEnt is null) Check(false, "找到栏宽断行 MTEXT 实体");
        else
        {
            var bands = InkBands(frame, wrappedEnt.Bounds, 4, view);
            Log($"       断行块 {bands.Count} 行：{BandsStr(bands)}");
            Check(bands.Count >= 3, $"栏宽 60 的长文本被折成多行（实际 {bands.Count} 行）");
            float limitPx = 60f * view.Viewport.Zoom;
            float widest = bands.Count == 0 ? 0f : bands.Max(b => b.Width);
            Check(widest <= limitPx + 2f,
                  $"没有一行溢出栏宽（最宽 {widest:F1}px，栏宽 {limitPx:F1}px）");
        }

        // mtList 同样由 SetDisplay 接管，不再回填旧列表（见线宽段注释）
    }

    // ---------- 14. 点标记（P2-3）：尺寸是屏幕口径，不随缩放变化 ----------
    // PDSIZE=0 的语义是"视口高度的 5%"，所以判据只能是像素：两个缩放级别下
    // 同一个点的符号直径必须相等（世界坐标几何做不到这件事，这正是原实现的问题）。
    var ptPath = FindSampleFile("sample_point.dxf");
    if (ptPath is null)
    {
        Check(false, "找得到点样式样例 sample_point.dxf（先跑 tools/SampleGen）");
    }
    else
    {
        var ptList = CadDocumentLoader.LoadAsync(ptPath).GetAwaiter().GetResult();
        view.SetDisplay(ptList);
        view.FitToContent();
        // FitToContent 是"下一次渲染时生效"的挂起状态：必须先吃一帧，否则紧随其后的
        // ZoomBy 会被这一帧的 fit 结果覆盖（logs 里表现为 zoom 停在适应窗口值）。
        window.CaptureRenderedFrame();
        // 适应窗口后三个点正好落在视口四角，量尺寸会被边界裁剪干扰（点符号有半径）。
        // 缩到 0.75 留出边距，之后再放大/缩小各量一次。
        view.ZoomBy(0.75f);
        window.CaptureRenderedFrame();
        Log($"       点样例 zoom={view.Viewport.Zoom:F3}，PDMODE={ptList.Entities.First(e => e.Mark is not null).Mark!.Mode}");

        float expect = h * PointMarkStyle.DefaultViewportPercent * 0.01f;
        float first = MeasureMarkSize(window, view, 0f, 0f);
        Check(Near(first, expect, 3f),
              $"PDSIZE=0 → 符号直径 ≈ 视高 5%（期望 {expect:F1}px，实际 {first:F1}px）");

        // 缩小视角：屏幕口径的尺寸必须不变（世界口径会跟着缩）
        view.ZoomBy(0.6f);
        window.CaptureRenderedFrame();
        float second = MeasureMarkSize(window, view, 0f, 0f);
        Check(Near(second, first, 1.5f),
              $"缩小 0.6x 后点符号直径不变（{first:F1}px → {second:F1}px）");

        // 放大视角再验一次（世界口径会跟着放大，屏幕口径不变）。
        // 只放大到 1.2x：三个点都在内容四角，再放大就把被量的那个点推出视口了。
        view.ZoomBy(1.2f / 0.6f);
        window.CaptureRenderedFrame();
        float third = MeasureMarkSize(window, view, 0f, 0f);
        Check(Near(third, first, 1.5f),
              $"放大到 1.2x 后点符号直径仍不变（{first:F1}px → {third:F1}px）");
    }

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
static bool WaitUntil(Func<bool> condition, int timeoutMs)
{
    var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
    while (DateTime.UtcNow < deadline)
    {
        if (condition()) return true;
        Thread.Sleep(50);
    }
    return condition();
}

/// <summary>
/// 数"暗边"像素：最大通道落在 8~40 之间。抗锯齿在细线两侧留下的正是这一档，
/// ODA 出图几乎全无（99.8% 的非零像素 > 40）。用来判断 --no-aa 是否真的生效。
/// </summary>
static int CountDimInk(WriteableBitmap? frame)
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
            int c0 = row[o], c1 = row[o + 1], c2 = row[o + 2];
            int r = bgra ? c2 : c0, g = c1, b = bgra ? c0 : c2;
            int m = Math.Max(r, Math.Max(g, b));
            if (m >= 8 && m <= 40) count++;
        }
    }
    return count;
}

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

/// <summary>
/// 统计指定矩形内的强调色像素（框选带提示 / 选中高亮的判据）。
/// bluish = 蓝通道主导（窗选、AutoCAD 选中高亮）；否则绿色主导（窗交）。
/// 用通道差而不是精确色值：12px 文字经抗锯齿后只有核心像素是原色，边缘全是与黑底的混合色。
/// </summary>
static int CountHue(WriteableBitmap? frame, int x0, int y0, int wRegion, int hRegion, bool bluish)
{
    if (frame is null) return -1;
    using var fb = frame.Lock();
    bool bgra = fb.Format == PixelFormat.Bgra8888;
    int w = fb.Size.Width, h = fb.Size.Height, stride = fb.RowBytes;
    var row = new byte[stride];
    int count = 0;
    int xa = Math.Max(0, x0), ya = Math.Max(0, y0);
    int xb = Math.Min(w, x0 + wRegion), yb = Math.Min(h, y0 + hRegion);
    for (int y = ya; y < yb; y++)
    {
        Marshal.Copy(IntPtr.Add(fb.Address, y * stride), row, 0, stride);
        for (int x = xa; x < xb; x++)
        {
            int o = x * 4;
            byte c0 = row[o], c1 = row[o + 1], c2 = row[o + 2];
            byte r = bgra ? c2 : c0;
            byte g = c1;
            byte b = bgra ? c0 : c2;
            if (bluish) { if (b >= 150 && b > r + 60 && b > g + 40) count++; }
            else { if (g >= 110 && g > r + 30 && g > b + 30) count++; }
        }
    }
    return count;
}

/// <summary>
/// 统计非背景像素（黑底上任何通道超过阈值）。用于比较高亮样式：选中高亮的 glow 与
/// 半透明填充叠在黑底上只是很暗的蓝（(0,33,59) 这类），按"蓝色主导"判会漏掉，
/// 只能按"有没有墨"来数 —— 叠加样式的影响就是让目标周围多出一圈暗像素。
/// </summary>
static int CountInk(WriteableBitmap? frame, int threshold = 16)
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
            if (Math.Max(c0, Math.Max(c1, c2)) > threshold) count++;
        }
    }
    return count;
}

/// <summary>可选：把当前帧导出为 PNG（环境变量 DrawingView_UISMOKE_PNG），供人工核对观感。</summary>
static void DumpFrame(Window window, Action<string> log)
{
    var png = Environment.GetEnvironmentVariable("DrawingView_UISMOKE_PNG");
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

/// <summary>
/// 沿画面中间一列量各条水平线的**有效像素宽度**。
///
/// <para>为什么不直接数连续亮像素的行数：1px 的线正好落在像素边界时会被抗锯齿摊到相邻两行、
/// 每行只有约 50% 覆盖度，按"亮度 &gt; 128"数会得到 0 行或 2 行，断言随子像素位置抖动。
/// 这里改用覆盖率积分：每行取 luma/255 当覆盖率，把相邻有覆盖的行合并成一段，段内求和
/// 就是这条线的有效像素宽度（白线黑底，1px 线积分 ≈ 1.0，2px ≈ 2.0）。</para>
///
/// 返回自上而下的各段宽度（float，未取整）。
/// </summary>
static List<float> CrossSection(WriteableBitmap? frame)
{
    var bands = new List<float>();
    if (frame is null) return bands;
    using var fb = frame.Lock();
    bool bgra = fb.Format == PixelFormat.Bgra8888;
    int w = fb.Size.Width, h = fb.Size.Height, stride = fb.RowBytes;
    var row = new byte[stride];
    int x = w / 2;
    var cover = new float[h];
    for (int y = 0; y < h; y++)
    {
        Marshal.Copy(IntPtr.Add(fb.Address, y * stride), row, 0, stride);
        int o = x * 4;
        byte c0 = row[o], c1 = row[o + 1], c2 = row[o + 2];
        byte r = bgra ? c2 : c0, g = c1, b = bgra ? c0 : c2;
        cover[y] = (r * 0.299f + g * 0.587f + b * 0.114f) / 255f;
    }

    int i = 0;
    while (i < h)
    {
        if (cover[i] < 0.05f) { i++; continue; }
        int startIdx = i;
        float sum = 0f;
        while (i < h && cover[i] >= 0.05f) { sum += cover[i]; i++; }
        // 丢掉贴着画面上下边缘的段（跨屏实体，高度不可信）
        if (startIdx > 0 && i < h) bands.Add(sum);
    }
    return bands;
}

/// <summary>像素宽度列表 → "1.00,3.78,..." 便于日志比对。</summary>
static string Str(List<float> values) => string.Join(" ", values.Select(v => v.ToString("F2")));

/// <summary>
/// 把给定**世界矩形**范围内的墨迹按行带切出来（验证多行文本的行数、行中心、行距）。
///
/// <para>行带判据是"该行有任一像素够亮"，再合并相邻行 —— 字母上下半之间的空隙会被并进
/// 同一行带，正是我们要的"一行文字"粒度。每行的左右边界取该行带内墨迹像素的最小/最大 x，
/// 于是"这行是否居中""行宽多少"都能直接量出来，不必关心字体内部度量。</para>
///
/// <paramref name="marginPx"/> 沿四周外扩，避免抗锯齿边缘压界被切掉。
/// </summary>
static List<InkBand> InkBands(WriteableBitmap? frame, SKRect world, int marginPx,
                               DrawingView.Controls.DrawingView view)
{
    var bands = new List<InkBand>();
    if (frame is null) return bands;

    // 世界 Y 向上、屏幕 Y 向下：SKRect.Top 是较小 y（世界里的**下方**），故各自取 min/max
    var a = view.Viewport.WorldToScreen(new SKPoint(world.Left, world.Top));
    var b = view.Viewport.WorldToScreen(new SKPoint(world.Right, world.Bottom));
    int x0 = (int)MathF.Floor(MathF.Min(a.X, b.X)) - marginPx;
    int x1 = (int)MathF.Ceiling(MathF.Max(a.X, b.X)) + marginPx;
    int y0 = (int)MathF.Floor(MathF.Min(a.Y, b.Y)) - marginPx;
    int y1 = (int)MathF.Ceiling(MathF.Max(a.Y, b.Y)) + marginPx;

    using var fb = frame.Lock();
    bool bgra = fb.Format == PixelFormat.Bgra8888;
    int w = fb.Size.Width, h = fb.Size.Height, stride = fb.RowBytes;
    var row = new byte[stride];

    x0 = Math.Clamp(x0, 0, w - 1); x1 = Math.Clamp(x1, 0, w - 1);
    y0 = Math.Clamp(y0, 0, h - 1); y1 = Math.Clamp(y1, 0, h - 1);
    if (x1 <= x0 || y1 <= y0) return bands;

    int bandTop = -1, bandLeft = int.MaxValue, bandRight = int.MinValue;

    void Flush(int bottom)
    {
        if (bandTop < 0) return;
        bands.Add(new InkBand(bandTop, bottom, bandLeft, bandRight));
        bandTop = -1; bandLeft = int.MaxValue; bandRight = int.MinValue;
    }

    for (int y = y0; y <= y1; y++)
    {
        Marshal.Copy(IntPtr.Add(fb.Address, y * stride), row, 0, stride);
        bool any = false;
        for (int x = x0; x <= x1; x++)
        {
            int o = x * 4;
            byte c0 = row[o], c1 = row[o + 1], c2 = row[o + 2];
            byte r = bgra ? c2 : c0, g = c1, bl = bgra ? c0 : c2;
            if (r * 0.299f + g * 0.587f + bl * 0.114f < 64f) continue;   // 白字黑底：暗像素不是墨迹
            any = true;
            if (x < bandLeft) bandLeft = x;
            if (x > bandRight) bandRight = x;
        }
        if (any) { if (bandTop < 0) bandTop = y; }
        else Flush(y - 1);
    }
    Flush(y1);

    // 贴着窗口上下边缘的行带宽度不可信（被裁掉了），剔除
    bands.RemoveAll(bd => bd.Top <= y0 || bd.Bottom >= y1);
    return bands;
}

/// <summary>
/// 量某个世界坐标处点标记的**符号直径**（像素）。做法是用方圆 ±60px 的窗口取行带，
/// 取唯一那条行带的高度：PDMODE=34 是"十字 + 套圆"，圆的上下沿就是符号的上下沿。
/// 窗口内没有墨迹（或压到别的实体）时返回 -1，让断言直接失败而不是巧合通过。
/// </summary>
static float MeasureMarkSize(Window window, DrawingView.Controls.DrawingView view, float wx, float wy)
{
    var frame = window.CaptureRenderedFrame();
    float half = 60f / MathF.Max(view.Viewport.Zoom, 1e-6f);
    var region = SKRect.Create(wx - half, wy - half, half * 2, half * 2);
    var bands = InkBands(frame, region, 0, view);
    if (bands.Count != 1) return -1f;
    return bands[0].Bottom - bands[0].Top + 1;
}

/// <summary>行带列表 → "top-bottom[cx,w]" 便于日志比对。</summary>
static string BandsStr(List<InkBand> bands)
    => string.Join(" ", bands.Select(b => $"{b.Top}-{b.Bottom}[{b.MidX:F1},w{b.Width:F0}]"));

/// <summary>浮点近似比较（像素量测用，容差以像素为单位）。</summary>
static bool Near(float actual, float expected, float tolerance) => MathF.Abs(actual - expected) <= tolerance;

static string? FindSampleFile(string fileName)
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        var candidate = Path.Combine(dir.FullName, "samples", fileName);
        if (File.Exists(candidate)) return candidate;
        dir = dir.Parent;
    }
    return null;
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

/// <summary>一行墨迹：垂直范围、水平范围与中心。</summary>
record InkBand(int Top, int Bottom, int Left, int Right)
{
    public float MidX => (Left + Right) / 2f;
    public float MidY => (Top + Bottom) / 2f;
    public float Width => Right - Left;
}
