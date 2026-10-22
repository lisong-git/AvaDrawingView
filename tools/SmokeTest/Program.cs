using ACadSharp.Entities;
using DrawingView.Core;
using SkiaSharp;

// 核心链路冒烟测试：解析 → 转换 → 索引 → 查询。
// 不经过 Avalonia 渲染管线（渲染相关回归见 tools/UiSmokeTest）。
//
// 两个样例各司其职：
//   sample_r12.dxf      基线样例（2 图层 / 6 实体）——断言**精确值**，
//                       原来用"实体数 >= 5""范围宽度 > 0"这类松阈值，
//                       文本包围盒撑大 34%（范围左边界 -53 应为 0）就是这样漏过去的。
//   sample_features.dxf 功能样例（tools/SampleGen 生成）——覆盖全部受支持实体类型，
//                       以及 BYBLOCK 继承、冻结/关闭图层、隐藏实体、MINSERT 阵列等边界情况。
//
// 用法：dotnet run --project tools/SmokeTest [可选路径，指定则只跑该文件]
//
// 结果同时写 Console 与日志文件。**必须写日志文件**：本机 PowerShell 重定向
// 会把原生命令的 UTF-8 输出按 GBK 解码，中文断言名全变乱码（[Console]::OutputEncoding
// 在 PS 5.1 非交互重定向下不生效），日志文件才是可靠通道。
// 日志路径：环境变量 DrawingView_SMOKE_LOG，默认 %TEMP%\DrawingView_smoke.log

var logPath = Environment.GetEnvironmentVariable("DrawingView_SMOKE_LOG")
              ?? Path.Combine(Path.GetTempPath(), "DrawingView_smoke.log");
try { File.WriteAllText(logPath, string.Empty); } catch { /* 日志不可写不影响主流程 */ }

void Log(string message = "")
{
    System.Console.WriteLine(message);
    try { File.AppendAllText(logPath, message + Environment.NewLine); } catch { /* 同上 */ }
}

int fails = 0;
void Check(bool ok, string name)
{
    Log($"  {(ok ? "PASS" : "FAIL")} - {name}");
    if (!ok) fails++;
}

var explicitPath = args.FirstOrDefault();

if (explicitPath is not null)
{
    if (!File.Exists(explicitPath)) { Log($"FAIL: 找不到 {explicitPath}"); return 1; }
    // 按文件名路由到对应的断言套件（功能样例断言与基线断言完全不同）
    var name = Path.GetFileName(explicitPath);
    if (name.Contains("feature", StringComparison.OrdinalIgnoreCase))
        RunFeatureSample(explicitPath);
    else if (name.Contains("nonplot", StringComparison.OrdinalIgnoreCase))
        RunNonPlotSample(explicitPath);
    else if (name.Contains("spline", StringComparison.OrdinalIgnoreCase))
        RunSplineFileSample(explicitPath);
    else if (name.Contains("ocs", StringComparison.OrdinalIgnoreCase))
        RunOcsFileSample(explicitPath);
    else if (name.Contains("point", StringComparison.OrdinalIgnoreCase))
        RunPointFileSample(explicitPath);
    else if (name.Contains("mtext", StringComparison.OrdinalIgnoreCase))
        RunMTextSample(explicitPath);
    else if (name.Contains("text", StringComparison.OrdinalIgnoreCase))
        RunTextSample(explicitPath);
    else if (name.Contains("hatch", StringComparison.OrdinalIgnoreCase))
        RunHatchFileSample(explicitPath);
    else if (name.Contains("lineweight", StringComparison.OrdinalIgnoreCase))
        RunLineWeightFileSample(explicitPath);
    else if (name.Contains("region", StringComparison.OrdinalIgnoreCase))
        RunRegionChecks();
    else if (name.Contains("widepoly", StringComparison.OrdinalIgnoreCase))
        RunWidePolylineFileSample(explicitPath);
    else
        RunBaseSample(explicitPath, heading: null);
    Log(fails == 0 ? "SMOKE TEST PASSED" : $"SMOKE TEST FAILED ({fails} 项)");
    return fails == 0 ? 0 : 1;
}

var baseSample = FindSample("sample_r12.dxf");
var featureSample = FindSample("sample_features.dxf");
if (baseSample is null || featureSample is null)
{
    Log($"FAIL: 找不到样例（base={baseSample ?? "null"}, features={featureSample ?? "null"}）");
    return 1;
}

RunBaseSample(baseSample, "基线样例 sample_r12.dxf");
RunFeatureSample(featureSample);
var textSample = FindSample("sample_text.dxf");
if (textSample is not null) RunTextSample(textSample);
var mtextSample = FindSample("sample_mtext.dxf");
if (mtextSample is not null) RunMTextSample(mtextSample);
var pointSample = FindSample("sample_point.dxf");
if (pointSample is not null) RunPointFileSample(pointSample);
RunOcsMathChecks();
RunSplineChecks();
var ocsSample = FindSample("sample_ocs.dxf");
if (ocsSample is not null) RunOcsFileSample(ocsSample);
var splineSample = FindSample("sample_spline.dxf");
if (splineSample is not null) RunSplineFileSample(splineSample);
var nonPlotSample = FindSample("sample_nonplot.dxf");
if (nonPlotSample is not null) RunNonPlotSample(nonPlotSample);
RunLayoutProjectionChecks();
RunHatchChecks();
RunAttribChecks();
RunLineWeightChecks();
RunWidePolylineChecks();
RunCurvePrecisionChecks();
var lwSample = FindSample("sample_lineweight.dxf");
if (lwSample is not null) RunLineWeightFileSample(lwSample);
var wideSample = FindSample("sample_widepolyline.dxf");
if (wideSample is not null) RunWidePolylineFileSample(wideSample);
var hatchSample = FindSample("sample_hatch.dxf");
if (hatchSample is not null) RunHatchFileSample(hatchSample);
RunRegionChecks();
RunFarCoordinateChecks();
RunShxChecks();

Log();
Log(fails == 0 ? "SMOKE TEST PASSED" : $"SMOKE TEST FAILED ({fails} 项)");
return fails == 0 ? 0 : 1;

// ---------------------------------------------------------------- 基线样例

void RunBaseSample(string path, string? heading)
{
    if (heading is not null) Log($"=== {heading} ===");
    Log($"加载: {path}");

    var list = CadDocumentLoader.Load(path);

    Check(list.Entities.Count == 6, $"实体总数 == 6（实际 {list.Entities.Count}）");
    Check(list.LayerNames.Count == 2, $"图层数 == 2（实际 {list.LayerNames.Count}）");
    Check(!list.LayerNames.Any(n => n.StartsWith("unnamed", StringComparison.OrdinalIgnoreCase)),
          $"无 unnamed* 占位图层（实际 [{string.Join(", ", list.LayerNames)}]）");

    // 范围：左边界必须是 0（LINE 起点 / SOLID 起点），
    // 历史缺陷：文本包围盒用 字符数×字高×0.7 粗估，把左边界虚增到 -53。
    Check(MathF.Abs(list.Extents.Left) < 0.01f, $"范围左边界 == 0（实际 {list.Extents.Left:F2}，粗估缺陷会得到 -53）");
    Check(MathF.Abs(list.Extents.Right - 100f) < 0.01f, $"范围右边界 == 100（实际 {list.Extents.Right:F2}）");
    Check(MathF.Abs(list.Extents.Bottom - 120f) < 0.01f, $"范围下边界 == 120（实际 {list.Extents.Bottom:F2}）");
    Check(list.Extents.Top > -20f && list.Extents.Top < -5f,
          $"范围上边界在文本上方（实际 {list.Extents.Top:F2}，字体度量相关，取宽松区间）");

    // 文本包围盒必须与真实墨迹一致：从插入点向右延伸，而不是左右对称撑开
    var text = list.Entities.FirstOrDefault(e => e.Text is not null);
    Check(text is not null, "存在文本实体");
    if (text is not null)
    {
        Check(text.Bounds.Left >= 9.9f && text.Bounds.Left <= 10.2f,
              $"文本包围盒左边界 == 插入点 x=10（实际 {text.Bounds.Left:F2}）");
        Check(text.Bounds.Width > 20f && text.Bounds.Width < 70f,
              $"文本宽度为真实度量值（实际 {text.Bounds.Width:F1}，粗估会得到 126）");
    }

    // 索引：全图命中应等于实体数，角落查询应显著少于全图
    var all = list.Index.Query(list.Extents);
    Check(all.Count >= list.Entities.Count - 1, $"全图查询命中 {all.Count}/{list.Entities.Count}");
    var corner = SKRect.Create(list.Extents.Right - 1, list.Extents.Bottom - 1, 2, 2);
    Check(list.Index.Query(corner).Count < all.Count, "角落查询少于全图（索引有效）");
}

// ---------------------------------------------------------------- 功能样例

void RunFeatureSample(string path)
{
    Log();
    Log("=== 功能样例 sample_features.dxf ===");
    Log($"加载: {path}");

    var list = CadDocumentLoader.Load(path);

    // ---- 规模 ----
    // 13 个基本实体 + INSERT 展开 2 + MINSERT 2×3 展开 12 + 冻结层 1 + 关闭层 1 = 29；
    // 隐藏实体应被跳过（不产出）。总数是精确值：漏一笔就会露馅。
    Check(list.Entities.Count == 29, $"实体总数 == 29（实际 {list.Entities.Count}）");
    Check(list.LayerNames.Count == 4, $"图层数 == 4（实际 {list.LayerNames.Count}）");
    Check(!list.LayerNames.Any(n => n.StartsWith("unnamed", StringComparison.OrdinalIgnoreCase)),
          "无 unnamed* 占位图层");

    // ---- 图层状态 ----
    Check(list.InitiallyHiddenLayers.Contains("冻结层"), "冻结图层默认隐藏");
    Check(list.InitiallyHiddenLayers.Contains("关闭层"), "关闭图层默认隐藏");

    // ---- 颜色 ----
    int red = list.Entities.Count(e => e.ColorArgb == 0xFFFF0000u);
    Check(red == 2, $"轮廓层 BYLAYER 解析为红（ACI 1，实际 {red} 个：LINE + CIRCLE）");

    // 块内 BYBLOCK 实体必须继承 INSERT 的颜色（本样例 INSERT 为 ACI 3 绿）。
    // 原实现把 BYBLOCK 当白色 → 白底上被翻成黑色，这条断言就是它的回归防线。
    int green = list.Entities.Count(e => e.ColorArgb == 0xFF00FF00u);
    Check(green == 1, $"块内 BYBLOCK 继承插入色为绿（实际 {green} 个）");

    // ---- 实体类型覆盖（用"包围盒中心 + 尺寸"定位，避免依赖具体路径点数） ----
    Check(HasEntity(list, 50, 50, 39, 41), "CIRCLE 圆心(50,50) 半径 20");
    Check(HasEntity(list, 7.5f, 67.5f, 14, 16), "ARC 四分之一圆 (0,60)–(15,75)");
    Check(HasEntity(list, 0, 100, 39, 41), "ELLIPSE 长轴 20 × 短轴比 0.5");
    Check(HasEntity(list, 10, 155, 29, 31), "LWPOLYLINE（带 bulge 半圆，含半圆下探段）");
    Check(HasEntity(list, 50, 160, 19, 21), "POLYLINE2D 老式折线");

    // ---- 凸度归属回归防线（2026-09-23 定标修正） ----
    // DXF 组码 42 挂在顶点上、管「本顶点 → 下一顶点」那一段。原实现取的是**终点顶点**的
    // Bulge（整体错位一段）：首顶点的凸度被丢弃、闭合收尾段的凸度被挪到上一段。
    // 这条样例折线只有 v0 带 bulge=1，于是半圆**整个没画**，被画成纯三角形——
    // 而旧的 HasEntity 断言（中心 + 尺寸窗口）恰好容得下三角形，所以一直没暴露。
    // 定标证据：LabelProbe --bulge（画布 / 本探针按规范重建 / ACadSharp GetBoundingBox 三方比对）。
    var bulgePoly = list.Entities.FirstOrDefault(e => e.Handle == 0x4E);
    Check(bulgePoly is not null, "LWPOLYLINE 可按句柄取到（0x4E）");
    if (bulgePoly is not null)
    {
        var bb = bulgePoly.Path is not null ? bulgePoly.Path.TightBounds : bulgePoly.Bounds;
        Check(MathF.Abs(bb.Left) < 0.5f && MathF.Abs(bb.Top - 140f) < 0.5f
              && MathF.Abs(bb.Right - 20f) < 0.5f && MathF.Abs(bb.Bottom - 170f) < 0.5f,
              $"LWPOLYLINE 半圆凸度生效（应为 (0,140)-(20,170)，实际 "
              + $"({bb.Left:F2},{bb.Top:F2})-({bb.Right:F2},{bb.Bottom:F2})）");
    }
    Check(HasEntity(list, 100, 160, 25, 45), "SPLINE 三次样条压平");
    Check(HasEntity(list, 130, 200, 19, 21), "SOLID 填充四边形");
    Check(HasEntity(list, 150, 200, 15, 17), "HATCH 实心填充圆");
    Check(HasEntity(list, 165, 157.5f, 29, 31), "LEADER 引线");
    Check(HasPoint(list, 100, 200), "POINT 点标记在 (100,200)（几何由 PDMODE/PDSIZE 在渲染期生成）");

    // ---- MINSERT 阵列 ----
    // 注意行距沿 +Y 方向（AutoCAD 语义：正行距向 +Y 复制），插入点在 (0,-40)，故第 2 行在 y=-20。
    Check(HasEntity(list, 5, -40, 9.9f, 10.1f), "MINSERT 第 1 行第 1 列");
    Check(HasEntity(list, 45, -40, 9.9f, 10.1f), "MINSERT 第 1 行第 3 列（列距 20 沿 +X 生效）");
    Check(HasEntity(list, 5, -20, 9.9f, 10.1f), "MINSERT 第 2 行第 1 列（行距 20 沿 +Y 生效）");
    Check(HasEntity(list, 45, -20, 9.9f, 10.1f), "MINSERT 第 2 行第 3 列");

    // ---- 块内实体身份（CadEntity.OwnHandle） ----
    // 扁平模型空间里块内子实体的句柄被覆盖成外层 INSERT，导致「块内某个子实体」不可寻址。
    // OwnHandle 保留块定义层那个原始句柄，(Handle, OwnHandle) 组合即「哪个插入的哪个子实体」。
    var blockInner = list.Entities.Where(e => e.OwnHandle != e.Handle).ToList();
    var topLevel = list.Entities.Count(e => e.OwnHandle == e.Handle);
    var distinctOuter = blockInner.Select(e => e.Handle).Distinct().Count();
    var distinctOwn = blockInner.Select(e => e.OwnHandle).Distinct().Count();

    Check(list.Entities.All(e => e.OwnHandle != 0), "所有显示实体都带自身句柄 OwnHandle");
    Check(blockInner.Count == 14, $"块内实体 == 14（INSERT 展开 2 + MINSERT 2×3 展开 12，实际 {blockInner.Count}）");
    Check(topLevel == 15, $"非块内实体 == 15（13 基本 + 冻结层 + 关闭层，实际 {topLevel}）");
    Check(distinctOuter == 2, $"块内实体归属 2 个外层句柄（INSERT + MINSERT，实际 {distinctOuter}）");
    // MINSERT 的 12 个阵列实例共享同一外层句柄、同一自身句柄 —— 这正是「同一子实体被插多次」的形态
    Check(distinctOwn == 2, $"块定义内共 2 个不同自身句柄（实际 {distinctOwn}）");
    Check(blockInner.GroupBy(e => e.Handle).All(g => g.Select(e => e.OwnHandle).Distinct().Count() == 2),
          "每个外层句柄下都能看到块定义的全部 2 个子实体");

    // ---- 隐藏/关闭状态 ----
    Check(!HasEntity(list, 25, -100, 49, 51), "隐藏实体（IsInvisible）未产出");
    Check(HasEntity(list, 25, -150, 49, 51), "冻结层几何仍然转换（由可见性控制，而非丢弃）");
    Check(HasEntity(list, 25, -170, 49, 51), "关闭层几何仍然转换（由可见性控制，而非丢弃）");

    // ---- 文本 ----
    var texts = list.Entities.Where(e => e.Text is not null).ToList();
    Check(texts.Count == 2, $"文本实体 == 2（TEXT + MTEXT，实际 {texts.Count}）");
    Check(texts.Any(t => t.Text!.Text.Contains('\n')), "MTEXT 的 \\P 已转成换行符");

    var filtered = CadDocumentLoader.Load(path, null, default,
        new GeometryConvertOptions { SkipRootAnnotations = true });
    Check(filtered.Entities.Count == 26, $"根层标注过滤后实体总数 == 26（实际 {filtered.Entities.Count}）");
    Check(!filtered.Entities.Any(e => e.Text is not null), "过滤后无文本实体");
    Check(filtered.Entities.Count(e => e.OwnHandle == e.Handle) == 12,
          $"过滤后根层实体 == 12（实际 {filtered.Entities.Count(e => e.OwnHandle == e.Handle)}）");
    Check(filtered.Entities.Count(e => e.OwnHandle != e.Handle) == 14,
          $"过滤只作用于根层：块内实体仍 == 14（实际 {filtered.Entities.Count(e => e.OwnHandle != e.Handle)}）");
    Check(HasEntity(filtered, 50, 50, 39, 41), "过滤不影响普通几何（CIRCLE 仍在）");

    // ---- 范围 ----
    Check(MathF.Abs(list.Extents.Left + 20f) < 0.5f, $"范围左边界 ≈ -20（ELLIPSE，实际 {list.Extents.Left:F2}）");
    Check(MathF.Abs(list.Extents.Right - 180f) < 0.5f, $"范围右边界 ≈ 180（LEADER，实际 {list.Extents.Right:F2}）");
    Check(MathF.Abs(list.Extents.Top + 170f) < 0.5f, $"范围上边界 ≈ -170（关闭层，实际 {list.Extents.Top:F2}）");
    Check(list.Extents.Bottom > 218f && list.Extents.Bottom < 232f,
          $"范围下边界在 MTEXT 顶部（实际 {list.Extents.Bottom:F2}，字体度量相关）");

    // ---- 诊断导出（设 DrawingView_SMOKE_DUMP=1 可打印全部实体，便于核对断言） ----
    if (Environment.GetEnvironmentVariable("DrawingView_SMOKE_DUMP") == "1") DumpEntities(list);

    // ---- 取消令牌 ----
    bool cancelled = false;
    try
    {
        CadDocumentLoader.Load(path, null, new CancellationToken(canceled: true));
    }
    catch (OperationCanceledException)
    {
        cancelled = true;
    }
    Check(cancelled, "取消令牌在加载入口生效");

    // ---- 资源释放 ----
    // DisplayList 持有一批 SKPath（原生内存），引用计数归零才释放。
    // 加载器返回的实例初始计数为 1，显式 Release 后应被回收。
    DisplayList.DisposalDelayMs = 0;
    int before = DisplayList.DisposedCount;
    var temp = CadDocumentLoader.Load(path);
    temp.Release();
    Check(DisplayList.DisposedCount == before + 1, "DisplayList 引用计数归零后释放原生几何");
}

// ---------------------------------------------------------------- 文字口径样例

/// <summary>
/// 文字口径样例 sample_text.dxf —— 锁定 P0-2（字高 = 大写字高）与 P0-3
/// （宽度因子 / 倾斜 / 72-73 对齐 / MTEXT 锚点）的数值行为。
///
/// 这些断言的作用是"挡住静默回退"：字高口径错了全图不会崩，只会整体小 24%；
/// 对齐没实现也不会崩，只是所有文字往右偏半个字宽。两者都只有数值断言能发现。
/// </summary>
void RunTextSample(string path)
{
    Log();
    Log("=== 文字口径样例 sample_text.dxf ===");
    Log($"加载: {path}");

    // ---- 度量层：字高即大写字高 ----
    var probe = TextMetrics.MeasureLocalBounds("HHHH", 80f);
    Check(MathF.Abs(probe.Height - 80f) < 0.6f,
          $"MeasureLocalBounds 高度 == 字高 80（实际 {probe.Height:F2}，em 口径会得到 105.6）");
    Check(TextMetrics.CapHeightRatio > 0.6f && TextMetrics.CapHeightRatio < 0.95f,
          $"cap/em 比例实测为合理值（实际 {TextMetrics.CapHeightRatio:F3}）");
    var desc = TextMetrics.MeasureLocalBounds("Ag", 80f);
    Check(desc.Height > 80.6f, $"含降部字符的包围盒向下延伸（实际 {desc.Height:F2}）");

    // 测宽必须与字高**成比例**（世界坐标里字高可达数千，实测 幕墙横方总汇.dwg 有 2160）：
    // 早期 FontForCapHeight 把缓存键 Clamp 到 1024，超过就按 1024 量 → 宽度严重低估，
    // 包围盒比墨迹窄一半，整图 Extents 被算小、右侧内容被裁出画面。
    var w100 = TextMetrics.MeasureWidth("HHHH", 100f);
    var w1080 = TextMetrics.MeasureWidth("HHHH", 1080f);
    var w2160 = TextMetrics.MeasureWidth("HHHH", 2160f);
    Check(MathF.Abs(w1080 / w100 - 10.8f) < 0.02f,
          $"字高 1080 的宽度 = 字高 100 的 10.8 倍（实际 {w1080 / w100:F3}，按 1024 截断会是 10.24）");
    Check(MathF.Abs(w2160 / w1080 - 2f) < 0.005f,
          $"字高翻倍宽度也翻倍（2160/1080，实际 {w2160 / w1080:F3}，按 1024 截断会是 1.0）");

    var list = CadDocumentLoader.Load(path);
    Check(list.Entities.Count == 6, $"实体总数 == 6（实际 {list.Entities.Count}）");

    var texts = list.Entities.Where(e => e.Text is not null).ToList();
    Check(texts.Count == 6, $"文本实体 == 6（实际 {texts.Count}）");

    // 注意 SKRect 的 Top/Bottom 是"屏幕向下的上下"，即 Top = 最小 y、Bottom = 最大 y；
    // 世界坐标 Y 向上，故**基线**落在 Top 上，字顶落在 Bottom 上。
    var a = texts.FirstOrDefault(t => Near(t.Bounds.Left, 0) && Near(t.Bounds.Top, 0));
    Check(a is not null, "左侧基线文本在 (0,0)");
    if (a is null) return;

    Check(MathF.Abs(a.Bounds.Height - 10f) < 0.4f,
          $"单行无降部文本的包围盒高度 == 字高 10（实际 {a.Bounds.Height:F2}）");
    Check(a.Text!.Height > 9.5f && a.Text.Height < 10.5f,
          $"TextDraw.Height 存的是字高（实际 {a.Text.Height:F2}）");

    // ---- 宽度因子 ----
    var b = texts.FirstOrDefault(t => Near(t.Bounds.Left, 0) && Near(t.Bounds.Top, 50));
    Check(b is not null, "宽度因子 0.5 的文本在 (0,50)");
    if (b is not null)
    {
        Check(MathF.Abs(b.Text!.WidthFactor - 0.5f) < 1e-3f,
              $"宽度因子被读出（实际 {b.Text.WidthFactor:F3}）");
        float ratio = b.Bounds.Width / a.Bounds.Width;
        Check(ratio > 0.48f && ratio < 0.52f,
              $"宽度因子 0.5 → 包围盒宽度减半（实际 {ratio:F3}，未实现时为 1.0）");
    }

    // ---- 倾斜 ----
    var c = texts.FirstOrDefault(t => Near(t.Bounds.Left, 0) && Near(t.Bounds.Top, 100));
    Check(c is not null, "倾斜 30° 的文本在 (0,100)");
    if (c is not null)
    {
        float widen = c.Bounds.Width / a.Bounds.Width;
        Check(widen > 1.10f, $"倾斜 30° → 包围盒明显变宽（实际 {widen:F3}，未实现时为 1.0）");
        Check(MathF.Abs(c.Bounds.Height - 10f) < 1.5f,
              $"倾斜不改变字高（实际 {c.Bounds.Height:F2}）");
    }

    // ---- 72/73 对齐 ----
    var center = texts.FirstOrDefault(t => Near(t.Bounds.MidX, 200) && Near(t.Bounds.MidY, 0));
    Check(center is not null, "中-中对齐文本墨迹中心落在 (200,0)");
    if (center is not null)
    {
        Check(MathF.Abs(center.Bounds.MidX - 200f) < 0.5f && MathF.Abs(center.Bounds.MidY) < 0.5f,
              $"中-中锚点生效（实际中心 {center.Bounds.MidX:F2},{center.Bounds.MidY:F2}）");
    }

    var right = texts.FirstOrDefault(t => Near(t.Bounds.Right, 200) && Near(t.Bounds.Top, 50));
    Check(right is not null, "右对齐文本右边界落在 x=200");
    if (right is not null)
    {
        Check(MathF.Abs(right.Bounds.Right - 200f) < 0.5f,
              $"右对齐锚点生效（实际右边界 {right.Bounds.Right:F2}）");
    }

    // ---- MTEXT 多行与左上锚点 ----
    var mt = texts.FirstOrDefault(t => t.Text!.Text.Contains('\n'));
    Check(mt is not null, "MTEXT 保留换行");
    if (mt is not null)
    {
        Check(Near(mt.Bounds.Bottom, 150f, 0.6f),
              $"MTEXT 左上锚点 → 首行顶部在 y=150（实际 {mt.Bounds.Bottom:F2}）");
        float expect = 10f + TextMetrics.LineSpacingFor(10f);
        Check(MathF.Abs(mt.Bounds.Height - expect) < 1.2f,
              $"两行文本高度 ≈ 字高 + 单倍行距（应 {expect:F2}，实际 {mt.Bounds.Height:F2}）");
    }
}

static bool Near(float v, float target, float tol = 0.3f) => MathF.Abs(v - target) <= tol;

// ---------------------------------------------------------------- MTEXT 排版（P2-4）

/// <summary>
/// MTEXT 排版样例 sample_mtext.dxf —— 锁三件事：
/// <list type="number">
/// <item><b>栏宽断行</b>：组码 41（RectangleWidth）是 AutoCAD 的排版栏宽，超过要折行。
///   不折的后果不是崩溃，而是一条说明文字横穿整张图纸，只有量每行宽度才看得出来。</item>
/// <item><b>逐行对齐</b>：MTEXT 的居中/右对齐作用于**每一行**，不是整块（与 TEXT 不同）。
///   转换端因此把水平量交给渲染端逐行分配，锚点不再承担水平偏移。</item>
/// <item><b>行距倍数</b>：单倍 = 1.667 × 字高，组码 44 是倍数。</item>
/// </list>
/// 断行/行距/对齐错了全都不会崩，只会错位，所以全部用数值钉住。
/// </summary>
void RunMTextSample(string path)
{
    Log();
    Log("=== MTEXT 排版样例 sample_mtext.dxf ===");
    Log($"加载: {path}");

    // ---- 断行器本身 ----
    // CJK 任意字间可断、西文只在空格处断、单词超宽时硬断、超宽行必须真的被切开。
    var cjk = TextWrap.Wrap("一二三四五六七八九十", 10f, 35f);
    Check(cjk.Contains('\n'), $"CJK 超宽被断行（实际 [{cjk.Replace("\n", "|")}]）");
    Check(cjk.Replace("\n", "").Length == 10, "断行不丢字");
    var words = TextWrap.Wrap("AAAA BBBB CCCC", 10f, TextMetrics.MeasureWidth("AAAA BBBB", 10f) * 1.02f);
    Check(words.Contains('\n') && !words.Contains("AAAABBBB"),
          $"西文只在空格处断（实际 [{words.Replace("\n", "|")}]）");
    var hard = TextWrap.Wrap("WWWWWWWWWW", 10f, TextMetrics.CharAdvance('W', 10f) * 3.5f);
    Check(hard.Split('\n').Length >= 3, $"单词超宽硬断（实际 {hard.Split('\n').Length} 行）");
    Check(TextWrap.Wrap("AAAA", 10f, 0f) == "AAAA", "栏宽 ≤ 0 时原样返回（不做断行）");

    var list = CadDocumentLoader.Load(path);
    var ents = list.Entities.Where(e => e.Text is not null).ToList();
    Check(ents.Count == 3, $"文本实体 == 3（实际 {ents.Count}）");

    // 三个实体靠锚点区分：断行块在 (0,390)、居中块在 (200,390)、双倍行距块在 (0,190)
    // （TopLeft/TopCenter 的垂直偏移都是「首行基线 = 锚点 − 字高」，故 y 为 390 / 190）
    var wrappedEnt = ents.FirstOrDefault(e => Near(e.Text!.Position.X, 0, 1) && Near(e.Text.Position.Y, 390, 1));
    var centeredEnt = ents.FirstOrDefault(e => Near(e.Text!.Position.X, 200, 1));
    var doubledEnt = ents.FirstOrDefault(e => Near(e.Text!.Position.X, 0, 1) && Near(e.Text.Position.Y, 190, 1));
    var wrapped = wrappedEnt?.Text;
    var centered = centeredEnt?.Text;
    var doubled = doubledEnt?.Text;

    Check(wrapped is not null, "断行块锚点在 (0,390)");
    if (wrapped is not null)
    {
        var lines = wrapped.Text.Split('\n');
        Check(lines.Length >= 2, $"栏宽 60 的超长文本被折成多行（实际 {lines.Length} 行）");
        Check(string.Concat(lines) == new string('W', 40), "折行不丢字（拼接后与原串一致）");
        float widest = lines.Max(l => TextMetrics.MeasureWidth(l, wrapped.Height));
        Check(widest <= 60.5f, $"每行宽度不超过栏宽 60（最宽行 {widest:F2}）");
        Check(widest > 45f, $"行宽接近栏宽而不是保守地只放一个字（最宽行 {widest:F2}）");
    }

    Check(centered is not null, "居中块锚点在 x=200");
    if (centered is not null)
    {
        Check(centered.LineAlign == TextLineAlign.Center,
              $"逐行对齐方式为居中（实际 {centered.LineAlign}）");
        var lines = centered.Text.Split('\n');
        Check(lines.Length == 3, $"居中块 3 行（实际 {lines.Length}）");
        if (lines.Length == 3)
        {
            float w0 = TextMetrics.MeasureWidth(lines[0], centered.Height);
            float w1 = TextMetrics.MeasureWidth(lines[1], centered.Height);
            // 行宽差异足够大，逐行居中与整块居中才在画面上可区分
            Check(w0 / MathF.Max(w1, 1e-3f) > 3f, $"各行宽度差异明显（{w0:F2} vs {w1:F2}）");
        }
        // 块级包围盒仍以锚点为中心：逐行居中不能把整块带偏
        Check(Near(centeredEnt!.Bounds.MidX, 200f, 1f),
              $"居中块包围盒中心仍在 x=200（实际 {centeredEnt.Bounds.MidX:F2}）");
    }

    Check(doubled is not null, "双倍行距块锚点在 (0,190)");
    if (doubled is not null)
    {
        Check(Near(doubled.LineSpacingFactor, TextMetrics.LineSpacingFactor * 2f, 1e-2f),
              $"行距倍数 2.0 → 系数 {TextMetrics.LineSpacingFactor * 2f:F3}（实际 {doubled.LineSpacingFactor:F3}）");
        // 两行：块高 = 字高 + 1 个行距（降部为 0，全是大写 A）
        float expect = 10f + 10f * doubled.LineSpacingFactor;
        Check(Near(doubledEnt!.Bounds.Height, expect, 1.2f),
              $"双倍行距块高 ≈ {expect:F2}（实际 {doubledEnt.Bounds.Height:F2}，单倍会是 {10f + 16.67f:F2}）");
    }

    // 对照：同一位置的单倍行距（断行块有多行）行距系数应为默认值
    if (wrapped is not null)
        Check(Near(wrapped.LineSpacingFactor, TextMetrics.LineSpacingFactor, 1e-3f),
              $"未指定行距倍数时用默认 1.667（实际 {wrapped.LineSpacingFactor:F3}）");
}

// ---------------------------------------------------------------- 大坐标 float32 精度（P2-8）

/// <summary>
/// 大坐标（±1e6）下的 float32 量化误差测量。
///
/// <para>几何在显示列表里是 <c>float</c>：1e6 处 的 ulp = 0.0625 单位，也就是 1e6 附近
/// 任何顶点最多偏 0.0625 个世界单位。这个偏差在整图视角下无感，但放大到 0.01 单位/像素时
/// 就是 6 个像素。断言把**当前量级**钉住：既不假装没误差，也不让误差悄悄变大。</para>
/// </summary>
void RunFarCoordinateChecks()
{
    Log();
    Log("=== 大坐标 float32 精度（P2-8）===");
    var p = FindSample("sample_far.dxf");
    if (p is null) { Log("  SKIP: 找不到 samples/sample_far.dxf"); return; }

    var list = CadDocumentLoader.Load(p);
    Check(list.Entities.Count == 12, $"两组各 6 个实体（实际 {list.Entities.Count}）");

    // 方框左上角顶点：理想 (0,0) 与 (1e6,1e6)
    (float ex, float ey)? FindCorner(float nearX, float nearY)
    {
        float best = float.MaxValue; (float, float)? hit = null;
        foreach (var e in list.Entities)
            foreach (var pt in e.Path.Points)
            {
                float d = MathF.Abs(pt.X - nearX) + MathF.Abs(pt.Y - nearY);
                if (d < best) { best = d; hit = (pt.X, pt.Y); }
            }
        return hit;
    }

    const double farIdeal = 1_000_000.3;
    const float far = (float)farIdeal;
    var cNear = FindCorner(0, 0);
    var cFar = FindCorner(far, far);
    Check(cNear is not null && cFar is not null, "两组方框角点都找到");
    if (cNear is null || cFar is null) return;

    float eNear = MathF.Abs(cNear.Value.Item1) + MathF.Abs(cNear.Value.Item2);
    float eFar = (float)(Math.Abs(cFar.Value.Item1 - farIdeal) + Math.Abs(cFar.Value.Item2 - farIdeal));
    Log($"  原点组角点误差 {eNear:F6}，1e6 组角点误差 {eFar:F6}（float32 在 1e6 的 ulp = 0.0625）");

    Check(eNear < 1e-3f, $"原点处几何无量化误差（{eNear:F6}）");
    Check(eFar <= 0.125f + 1e-4f, $"1e6 处误差不超过 2 个 ulp（{eFar:F6} ≤ 0.125，1 ulp = 0.0625）");

    // 尺寸不受量化影响：同一个 10×10 方框，两组的包围盒宽高应一致
    var farBox = list.Entities.Where(e => e.Bounds.Left > 900_000f).Select(e => e.Bounds).ToList();
    float fw = farBox.Max(b => b.Right) - farBox.Min(b => b.Left);
    Check(Near(fw, 10f, 0.13f), $"1e6 处方框宽度仍是 10（实际 {fw:F4}，量化误差 ≤ 0.125）");

    // 视口矩阵：world×zoom 会被放大成 1e8 量级，float 矩阵的平移/乘积都会量化。
    // 这条量的是"渲染管线本身的误差"，与几何存储无关。
    foreach (float z in new[] { 1f, 10f, 58f, 1000f })
    {
        var mtx = new SKMatrix(z, 0, 0, 0, -z, 0, 0, 0, 1);
        var s1 = mtx.MapPoint(new SKPoint(far + 0.3f, far + 0.7f));
        double expX = (double)z * (far + 0.3);
        double errPx = Math.Abs(s1.X - expX) + Math.Abs(s1.Y + (double)z * (far + 0.7));
        Log($"  zoom={z,6} → 屏幕坐标误差 {errPx,8:F2} px");
    }

    Log($"  结论：±1e6 时几何已按 float32 存储，误差上限 0.0625 单位/顶点；"
        + "形状不受影响（整体平移），深度放大时有台阶。");
}

// ---------------------------------------------------------------- REGION / ACIS（P2-7）

/// <summary>
/// REGION 的 ACIS 载荷 → B-rep 轮廓重建。
///
/// <para>样例 <c>samples/sample_region.sab</c> 是从 <c>3、幕墙横方总汇.dwg</c> 里导出的一条真实
/// REGION 载荷（20961 字节，SAB 二进制）—— 用真数据而不是手搓的 mock，才能覆盖
/// 「子类型+基类名两段记录头」「椭圆弧参数方向」「coedge 链回环」这些真实坑。</para>
/// </summary>
void RunRegionChecks()
{
    Log();
    Log("=== REGION / ACIS 轮廓重建（P2-7）===");
    var p = FindSample("sample_region.sab");
    if (p is null) { Log("  SKIP: 找不到 samples/sample_region.sab"); return; }

    var data = File.ReadAllBytes(p);
    Check(AcisSab.LooksLikeSab(data), $"识别为 SAB 载荷（{data.Length} 字节）");

    // 空 / 垃圾数据必须干净失败，不能抛也不能给出半截几何
    Check(AcisSab.Parse(null) is null, "Parse(null) → null");
    Check(AcisSab.Parse(new byte[] { 1, 2, 3 }) is null, "Parse(垃圾字节) → null");
    Check(RegionOutline.TryBuild(null, out _) is null, "TryBuild(null) → null");
    Check(RegionOutline.TryBuild(new byte[] { 1, 2, 3 }, out var why) is null && why is not null,
          $"TryBuild(垃圾字节) → null 且带原因（{why}）");

    var recs = AcisSab.Parse(data);
    Check(recs is not null, "记录流解析成功");
    if (recs is null) return;

    int Count(string name) => recs.Count(r => r.TypeName == name);
    Check(Count("coedge") == 65, $"coedge 记录 65 条（实际 {Count("coedge")}）");
    Check(Count("edge") == 65, $"edge 记录 65 条（实际 {Count("edge")}）");
    Check(Count("vertex") == 65, $"vertex 记录 65 条（实际 {Count("vertex")}）");
    Check(Count("point") == 65, $"point 记录 65 条（实际 {Count("point")}）");
    Check(Count("straight") + Count("ellipse") == 65,
          $"曲线记录 65 条（实际 straight={Count("straight")} + ellipse={Count("ellipse")}）");
    Check(Count("plane") == 1, $"面所在曲面是 1 条 plane（实际 {Count("plane")}）");
    Check(recs.All(r => r.TypeName.All(c => char.IsAsciiLetter(c) || c is '_' or '-' or ' ')),
          "没有残缺记录名（错位时会出现 eye / ref_vt 这类垃圾名）");

    // 指针与记录位置一致：body→lump→shell→face→loop 的链必须走得通
    var body = recs.FirstOrDefault(r => r.TypeName == "body");
    Check(body is not null, "存在 body 记录");

    var loops = RegionOutline.TryBuild(data, out var reason);
    Check(loops is not null, $"轮廓重建成功（{(loops is null ? reason : $"{loops.Count} 环")}）");
    if (loops is null) return;

    Check(loops.Count == 1, $"单环区域（实际 {loops.Count} 环）");
    var path = loops[0];
    Check(path.PointCount == 692, $"环上有 692 个点（实际 {path.PointCount}）");
    Check(path.PointCount > 65,
          $"点数多于顶点数 → 椭圆弧已被采样（{path.PointCount} 点 vs 65 顶点）");

    var b = path.Bounds;
    Check(Near(b.Left, 31115.37f, 0.5f) && Near(b.Top, -15130.88f, 0.5f)
          && Near(b.Right, 31141.12f, 0.5f) && Near(b.Bottom, -15112.88f, 0.5f),
          $"包围盒 ({b.Left:F2},{b.Top:F2}) - ({b.Right:F2},{b.Bottom:F2}) 与实测一致");
    Check(b.Width > 25f && b.Width < 26.5f, $"宽度 ≈ 25.75（实际 {b.Width:F3}）");

    var pts = path.Points;
    Check(pts.Length > 2 && Near(pts[0].X, pts[^1].X, 1e-3f) && Near(pts[0].Y, pts[^1].Y, 1e-3f),
          $"闭合：首点 ({pts[0].X:F2},{pts[0].Y:F2}) = 末点 ({pts[^1].X:F2},{pts[^1].Y:F2})");

    // 顶点坐标必须落在包围盒内（错位解析常会得到 0 或天文数字）
    Check(pts.All(q => q.X >= b.Left - 0.01f && q.X <= b.Right + 0.01f
                    && q.Y >= b.Top - 0.01f && q.Y <= b.Bottom + 0.01f),
          "所有点都在包围盒内（无 NaN / 无越界）");
}

// ---------------------------------------------------------------- 不打印图层

/// <summary>
/// 不打印图层（DEFPOINTS 一类）默认隐藏。
///
/// <para>为什么这条规则要单独守着：3、幕墙横方总汇.dwg 的 15,992 个定义点全在 DEFPOINTS 上，
/// 一旦这条规则失效，整张图会被点阵淹没（ODA 出图里一个都看不到 —— 参照实现按打印语义过滤）。
/// 规则做成"默认隐藏"而不是"丢弃"，用户在图层面板里仍能打开核对。</para>
/// </summary>
void RunNonPlotSample(string path)
{
    Log();
    Log("=== 不打印图层样例 sample_nonplot.dxf ===");
    Log($"加载: {path}");

    var list = CadDocumentLoader.Load(path);
    Check(list.Entities.Count == 2, $"两条线都转换了（实际 {list.Entities.Count}，几何不丢）");
    Check(list.InitiallyHiddenLayers.Contains("DEFPOINTS"),
          $"不打印图层进入默认隐藏集合（实际 [{string.Join(",", list.InitiallyHiddenLayers)}]）");
    Check(!list.InitiallyHiddenLayers.Contains("0"), "普通图层不受影响");
    Check(list.LayerNames.Contains("DEFPOINTS"), "图层仍在图层列表里（可手动打开）");
}

// ---------------------------------------------------------------- 样条权重 / 闭合（P2-6）

/// <summary>
/// 有理样条与闭合样的内存断言。
///
/// <para>判别式选"到圆心的半径偏差"而不是"包围盒"：权重的影响恰好是**中间鼓/瘪**，
/// 端点与包围盒几乎不变，只有沿曲线的半径能抓住它。</para>
/// </summary>
void RunSplineChecks()
{
    Log();
    Log("=== 样条权重与闭合（纯计算） ===");

    // 四分之一圆：控制点 (10,0),(10,10),(0,10) + 权重 (1,√2/2,1) = 半径 10 的精确圆弧
    double[] cx = { 10, 10, 0 };
    double[] cy = { 0, 10, 10 };
    double[] knots = { 0, 0, 0, 1, 1, 1 };

    using var rational = SplineFlattener.Flatten(cx, cy, knots, 2, weights: new[] { 1.0, Math.Sqrt(0.5), 1.0 });
    using var plain = SplineFlattener.Flatten(cx, cy, knots, 2);
    Check(rational is not null && plain is not null, "两种样条都能压平");
    if (rational is not null && plain is not null)
    {
        float errRational = MaxRadiusError(rational, new SKPoint(0, 0), 10f);
        float errPlain = MaxRadiusError(plain, new SKPoint(0, 0), 10f);
        Log($"       半径偏差：带权重 {errRational:F4} / 忽略权重 {errPlain:F4}");
        Check(errRational < 0.1f, $"带权重 → 半径 10 的精确圆弧（最大偏差 {errRational:F4}）");
        Check(errPlain > 0.3f, $"忽略权重 → 明显不是圆（最大偏差 {errPlain:F4}，断言才有 discriminating 力）");
    }

    // 闭合：控制点不首尾重合时，Close() 会补上一条弦 → 周长必须变长
    double[] bx = { 100, 140, 140, 100 };
    double[] by = { 0, 0, 40, 40 };
    double[] bk = { 0, 1, 2, 3, 4, 5, 6 };
    using var openPath = SplineFlattener.Flatten(bx, by, bk, 2);
    using var closedPath = SplineFlattener.Flatten(bx, by, bk, 2, closed: true);
    Check(openPath is not null && closedPath is not null, "闭合参数下也能压平");
    if (openPath is not null && closedPath is not null)
    {
        using var mo = new SKPathMeasure(openPath, false, 200f);
        using var mc = new SKPathMeasure(closedPath, false, 200f);
        Check(mc.Length > mo.Length + 1f,
              $"闭合后周长变长（{mo.Length:F2} → {mc.Length:F2}，补上了首尾弦）");
    }
}

/// <summary>沿路径采样，返回采样点到圆心距离与半径之差的**最大值**。</summary>
static float MaxRadiusError(SKPath path, SKPoint center, float radius)
{
    using var measure = new SKPathMeasure(path, false, 200f);
    float len = measure.Length;
    if (len <= 0) return float.MaxValue;
    float worst = 0f;
    const int steps = 200;
    for (int i = 0; i <= steps; i++)
    {
        if (!measure.GetPositionAndTangent(len * i / steps, out var pos, out _)) continue;
        float dx = pos.X - center.X, dy = pos.Y - center.Y;
        float d = MathF.Sqrt(dx * dx + dy * dy);
        worst = MathF.Max(worst, MathF.Abs(d - radius));
    }
    return worst;
}

/// <summary>
/// 样条样例 sample_spline.dxf —— 文件层验证"权重与闭合标志真的从 DXF 读进来了"。
/// </summary>
void RunSplineFileSample(string path)
{
    Log();
    Log("=== 样条样例 sample_spline.dxf ===");
    Log($"加载: {path}");

    var list = CadDocumentLoader.Load(path);
    Check(list.Entities.Count == 2, $"实体数 == 2（实际 {list.Entities.Count}）");

    // 1) 有理四分之一圆：路径上所有点到原点距离都应是 10
    var quarter = list.Entities.FirstOrDefault(e => e.Bounds.MidX < 50f);
    Check(quarter is not null, "找到有理样条（四分之一圆）");
    if (quarter?.Path is not null)
    {
        float err = MaxRadiusError(quarter.Path, new SKPoint(0, 0), 10f);
        Check(err < 0.15f, $"权重已从 DXF 读入 → 半径 10 的圆弧（最大偏差 {err:F4}，忽略权重会 > 0.3）");
        Check(Near(quarter.Bounds.Width, 10f, 0.2f) && Near(quarter.Bounds.Height, 10f, 0.2f),
              $"四分之一圆包围盒 10×10（实际 {quarter.Bounds.Width:F2}×{quarter.Bounds.Height:F2}）");
    }

    // 2) 闭合样条：周长必须比"同一控制点的开放版本"长一截
    var closedEnt = list.Entities.FirstOrDefault(e => e.Bounds.MidX > 50f);
    Check(closedEnt is not null, "找到闭合样条");
    if (closedEnt?.Path is not null)
    {
        double[] bx = { 100, 140, 140, 100 };
        double[] by = { 0, 0, 40, 40 };
        double[] bk = { 0, 1, 2, 3, 4, 5, 6 };
        using var reference = SplineFlattener.Flatten(bx, by, bk, 2);
        using var mRef = new SKPathMeasure(reference!, false, 200f);
        using var mFile = new SKPathMeasure(closedEnt.Path, false, 200f);
        Check(mFile.Length > mRef.Length + 1f,
              $"闭合标志已生效（开放 {mRef.Length:F2} → 文件 {mFile.Length:F2}）");
    }
}

// ---------------------------------------------------------------- OCS（P2-5）

/// <summary>
/// 任意轴算法的纯数学断言。期望值都是**手算**的（在注释里写出推导），
/// 不从实现里反推 —— 否则实现错了断言跟着错。
/// </summary>
void RunOcsMathChecks()
{
    Log();
    Log("=== OCS 任意轴算法（纯数学） ===");

    static string P(SKPoint p) => $"({p.X:F2},{p.Y:F2})";

    // N=(0,0,-1)：|Nx|,|Ny| &lt; 1/64 → Ax = Wy×N = (-1,0,0)；Ay = N×Ax = (0,1,0)
    //   ⇒ (x,y) → (-x, y)，即老实现的那个 x→−x 镜像
    var mNegZ = CadGeometry.OcsMatrix(new CSMath.XYZ(0, 0, -1));
    var pNegZ = mNegZ.MapPoint(new SKPoint(3, 4));
    Check(Near(pNegZ.X, -3f, 1e-4f) && Near(pNegZ.Y, 4f, 1e-4f),
          $"N=(0,0,-1) → x 取反 {P(pNegZ)}");

    // N=(0,0,1)：轴对齐正法向必须退化为单位矩阵（99.9% 的实体走这条捷径）
    var mPosZ = CadGeometry.OcsMatrix(new CSMath.XYZ(0, 0, 1));
    Check(mPosZ.IsIdentity, "N=(0,0,1) → 单位矩阵（常见图纸不做多余变换）");

    // N=(0.6,0,0.8)：|Nx| 不 < 1/64 → Ax = Wz×N = (0,0.6,0) → (0,1,0)；Ay = N×Ax = (-0.8,0,0.6)
    //   ⇒ (x,y) → (-0.8y, x)
    var mTilt = CadGeometry.OcsMatrix(new CSMath.XYZ(0.6, 0, 0.8));
    var pTilt = mTilt.MapPoint(new SKPoint(3, 4));
    Check(Near(pTilt.X, -3.2f, 1e-4f) && Near(pTilt.Y, 3f, 1e-4f),
          $"N=(0.6,0,0.8) → (-0.8y, x) = {P(pTilt)}");

    // N=(1,0,0)：Ax = Wz×N = (0,1,0)；Ay = N×Ax = (0,0,1)
    //   ⇒ (x,y) → (0, x)：整个平面在 XY 上投影成一条竖线（这正是"竖着放的图元"应有的样子）
    var mVert = CadGeometry.OcsMatrix(new CSMath.XYZ(1, 0, 0));
    var pVert = mVert.MapPoint(new SKPoint(3, 4));
    Check(Near(pVert.X, 0f, 1e-4f) && Near(pVert.Y, 3f, 1e-4f),
          $"N=(1,0,0) → 投影成 (0, x) = {P(pVert)}");

    // 高程沿法向：N=(0,0,-1) 时高程全在 Z 上，二维画面里看不见
    var mElevZ = CadGeometry.OcsMatrix(new CSMath.XYZ(0, 0, -1), 50);
    var pElevZ = mElevZ.MapPoint(new SKPoint(0, 0));
    Check(Near(pElevZ.X, 0f, 1e-4f) && Near(pElevZ.Y, 0f, 1e-4f),
          $"法向 ±Z 的 50 单位高程在二维上不产生位移 {P(pElevZ)}");
    // 倾斜法向时高程投影成 e·(Nx,Ny)：50 × (0.6, 0) = (30, 0)
    var mElevTilt = CadGeometry.OcsMatrix(new CSMath.XYZ(0.6, 0, 0.8), 50);
    var pElevTilt = mElevTilt.MapPoint(new SKPoint(0, 0));
    Check(Near(pElevTilt.X, 30f, 1e-4f) && Near(pElevTilt.Y, 0f, 1e-4f),
          $"倾斜法向的 50 单位高程 → 平移 (30,0) = {P(pElevTilt)}");

    // 非单位法向先归一化：N=(0,0,5) 与 N=(0,0,1) 等价
    Check(CadGeometry.OcsMatrix(new CSMath.XYZ(0, 0, 5)).IsIdentity, "法向未归一化（0,0,5）也能正确判定为轴对齐");
    // 退化法向（0,0,0）不能抛异常，退回单位矩阵
    Check(CadGeometry.OcsMatrix(new CSMath.XYZ(0, 0, 0)).IsIdentity, "零法向退回单位矩阵（不抛异常）");
}

/// <summary>
/// OCS 样例 sample_ocs.dxf —— 三个圆，法向分别 (0,0,−1) / (0.6,0,0.8) / (1,0,0)。
/// 期望包围盒（手算见 <c>WriteOcsSample</c> 的注释）：
///   (0,0) 20×20；圆心移到 (0,100) 的 16×20 椭圆；圆心移到 (0,300) 的 20×0 线段。
/// 旧实现（只做 Z&lt;0 镜像）会让后两个圆停在 (100,0)/(300,0)，尺寸都还是 20×20。
/// </summary>
void RunOcsFileSample(string path)
{
    Log();
    Log("=== OCS 样例 sample_ocs.dxf ===");
    Log($"加载: {path}");

    var list = CadDocumentLoader.Load(path);
    Check(list.Entities.Count == 3, $"实体数 == 3（实际 {list.Entities.Count}）");

    var circles = list.Entities.OrderBy(e => e.Bounds.MidY).ToList();

    // 1) N=(0,0,-1)：镜像后仍是正圆，圆心留在 (0,0)
    var a = list.Entities.FirstOrDefault(e => Near(e.Bounds.MidX, 0f, 0.5f) && Near(e.Bounds.MidY, 0f, 0.5f));
    Check(a is not null, "法向 -Z 的圆仍在 (0,0)");
    if (a is not null)
    {
        Check(Near(a.Bounds.Width, 20f, 0.2f) && Near(a.Bounds.Height, 20f, 0.2f),
              $"-Z 法向 → 仍是 20×20（实际 {a.Bounds.Width:F2}×{a.Bounds.Height:F2}）");
    }

    // 2) N=(0.6,0,0.8)：圆心 (100,0) → (0,100)，压成 16 宽 × 20 高
    var b = list.Entities.FirstOrDefault(e => Near(e.Bounds.MidY, 100f, 0.5f));
    Check(b is not null, "倾斜法向的圆被转到 x=0、y=100（OCS 的 x 轴 → 世界 +Y）");
    if (b is not null)
    {
        Check(Near(b.Bounds.MidX, 0f, 0.5f), $"倾斜法向圆心 x 归 0（实际 {b.Bounds.MidX:F2}）");
        Check(Near(b.Bounds.Width, 16f, 0.3f) && Near(b.Bounds.Height, 20f, 0.3f),
              $"倾斜法向 → 16×20 的椭圆投影（实际 {b.Bounds.Width:F2}×{b.Bounds.Height:F2}，"
              + "未做 OCS 时是 20×20 且留在 (100,0)）");
    }

    // 3) N=(1,0,0)：整个圆落在 YZ 平面 → XY 投影是竖直线段（高度 0 由 1e-3 兜底）
    var c = list.Entities.FirstOrDefault(e => Near(e.Bounds.MidY, 300f, 0.5f));
    Check(c is not null, "法向 +X 的圆被转到 x=0、y=300");
    if (c is not null)
    {
        // (x,y) → (0, x)：OCS 的 x 轴映射到世界 +Y，所以整圈压成一条**竖直**线段
        Check(c.Bounds.Width < 0.01f, $"法向 +X → 投影退化成竖直线段（宽 {c.Bounds.Width:F3}）");
        Check(Near(c.Bounds.Height, 20f, 0.3f), $"...长度为直径 20（实际 {c.Bounds.Height:F2}）");
    }
}

// ---------------------------------------------------------------- POINT 显示口径（P2-3）

/// <summary>
/// 点符号的显示口径 sample_point.dxf（PDMODE=34 / PDSIZE=0）。
///
/// <para>判据分两层：<b>形状解析</b>（PDMODE 的位组合）与<b>尺寸换算</b>（PDSIZE 的三种口径）
/// 都是纯函数，可以直接数值断言；文件层只验证"头变量真的读进来了、并且没变成几何"。
/// 真正的观感（屏幕尺寸恒定）由 UiSmokeTest 在像素上量。</para>
/// </summary>
void RunPointFileSample(string path)
{
    Log();
    Log("=== 点样式样例 sample_point.dxf ===");
    Log($"加载: {path}");

    // ---- 形状：低 5 位是基础形状，位 32 = 圆，位 64 = 方 ----
    Check(PointMarkStyle.BaseShape(0) == 0, "PDMODE 0 → 基础形状 0（实心点）");
    Check(PointMarkStyle.BaseShape(34) == 2, "PDMODE 34 → 基础形状 2（十字）");
    Check(PointMarkStyle.HasCircle(34) && !PointMarkStyle.HasSquare(34), "PDMODE 34 → 套圆不套方");
    Check(PointMarkStyle.BaseShape(66) == 2 && PointMarkStyle.HasSquare(66), "PDMODE 66 → 十字 + 方框");
    Check(PointMarkStyle.BaseShape(98) == 2 && PointMarkStyle.HasCircle(98) && PointMarkStyle.HasSquare(98),
          "PDMODE 98 → 十字 + 圆 + 方（96 = 32|64 叠加后仍能取到基础形状）");
    Check(PointMarkStyle.BaseShape(4) == 4 && !PointMarkStyle.HasCircle(4), "PDMODE 4 → 竖线且不套圆");

    // ---- 尺寸：>0 世界单位 / <0 视高百分比 / =0 默认 5% ----
    Check(Near(PointMarkStyle.SizePx(20f, 2f, 600f), 40f, 1e-3f),
          $"PDSIZE=20 世界单位、缩放 2 → 40px（实际 {PointMarkStyle.SizePx(20f, 2f, 600f):F2}）");
    Check(Near(PointMarkStyle.SizePx(20f, 4f, 600f), 80f, 1e-3f),
          "PDSIZE>0 时尺寸随缩放变化（世界尺寸口径）");
    Check(Near(PointMarkStyle.SizePx(-10f, 2f, 600f), 60f, 1e-3f),
          $"PDSIZE=-10 → 视高 10%（600px → 60px，实际 {PointMarkStyle.SizePx(-10f, 2f, 600f):F2}）");
    Check(Near(PointMarkStyle.SizePx(-10f, 8f, 600f), 60f, 1e-3f),
          "PDSIZE<0 时尺寸与缩放无关（屏幕尺寸恒定）");
    Check(Near(PointMarkStyle.SizePx(0f, 1f, 600f), 30f, 1e-3f),
          $"PDSIZE=0 → 默认 5% 视高（600px → 30px，实际 {PointMarkStyle.SizePx(0f, 1f, 600f):F2}）");
    Check(Near(PointMarkStyle.SizePx(0f, 1e-4f, 600f), 30f, 1e-3f),
          "PDSIZE=0 时缩放退化也不影响（视高口径）");
    Check(PointMarkStyle.SizePx(0.001f, 1e-4f, 600f) >= 1f,
          $"极端小尺寸兜底为 1px（实际 {PointMarkStyle.SizePx(0.001f, 1e-4f, 600f):F3}）");

    // PDMODE 0（点）与符号模式的口径不同（2026-10-22）：
    // 「5% 视高」只给符号用；点就是一个点，不随视口放大。
    // 反例证据：幕墙图 4,477 个 PDMODE=0/PDSIZE=0 的点按 5% 算成 45px，
    // 逐个抗锯齿实心圆要 29.5ms/帧（全图 21FPS），而 ODA 参考出图里它们只是像素级小点。
    Check(Near(PointMarkStyle.DotDiameterPx(0f, 0.000581f), PointMarkStyle.DefaultDotPx, 1e-4f),
          $"PDMODE=0 且 PDSIZE=0 → 固定 1.5px 点（实际 {PointMarkStyle.DotDiameterPx(0f, 0.000581f):F2}）");
    Check(Near(PointMarkStyle.DotDiameterPx(-10f, 4f), PointMarkStyle.DefaultDotPx, 1e-4f),
          "PDMODE=0 且 PDSIZE<0 → 仍是 1.5px 点（百分比口径只对符号生效）");
    Check(Near(PointMarkStyle.DotDiameterPx(20f, 2f), 40f, 1e-3f),
          $"PDMODE=0 但 PDSIZE>0 → 尊重显式世界尺寸（20×2 = 40px，实际 {PointMarkStyle.DotDiameterPx(20f, 2f):F2}）");
    Check(PointMarkStyle.DotDiameterPx(0f, 1f) < PointMarkStyle.SizePx(0f, 1f, 600f) / 10f,
          "点模式尺寸远小于同参数下的符号尺寸（不是同一套口径）");

    var list = CadDocumentLoader.Load(path);
    Check(list.Entities.Count == 3, $"实体数 == 3（实际 {list.Entities.Count}）");
    Check(list.PointMarkEntities == 3, $"点标记 == 3（实际 {list.PointMarkEntities}）");

    var marks = list.Entities.Where(e => e.Mark is not null).Select(e => e.Mark!).ToList();
    Check(marks.Count == 3, $"Mark 已填 3 个（实际 {marks.Count}）");
    Check(marks.All(m => m.Mode == 34), $"PDMODE=34 已从文件头读入（实际 {string.Join(",", marks.Select(m => m.Mode))}）");
    Check(marks.All(m => Near(m.SizeSpec, 0f, 1e-6f)),
          $"PDSIZE=0 已从文件头读入（实际 {string.Join(",", marks.Select(m => m.SizeSpec))}）");
    Check(marks.All(m => m.Position.X is 0f or 100f), "点位置取自 Location");
    // 点不再产出世界坐标几何：撑大图面范围且缩放后必然错（见 PointMarkStyle 说明）
    Check(list.Entities.Where(e => e.Mark is not null).All(e => e.Path is null && e.FillPath is null),
          "点不烘焙世界坐标几何（改为渲染期屏幕空间绘制）");
}

// ---------------------------------------------------------------- 布局投影

/// <summary>
/// 布局视口的投影数学（P0-1）：不依赖任何图纸文件，纯数值断言。
///
/// 这一组断言存在的理由：视口有 4 个独立字段（视口中心/尺寸、视点、视高），
/// 串错一个画面就整体缩放错，而"看图"分辨不出是"图变大了"还是"取错了视口"。
/// 这里把每个字段的角色都用数值钉住。
/// </summary>
void RunLayoutProjectionChecks()
{
    Log();
    Log("=== 布局视口投影（纯数学） ===");

    var vp = new ACadSharp.Entities.Viewport
    {
        Center = new CSMath.XYZ(210, 148, 0),      // 图纸上的视口中心
        Width = 180,
        Height = 120,
        ViewCenter = new CSMath.XY(200, 150),   // 模型里对准视口中心的点
        ViewHeight = 300,                          // 视口里能看到 300 高的模型
    };

    Check(LayoutProjection.IsDrawable(vp), "正常视口可投影");
    Check(MathF.Abs(LayoutProjection.Scale(vp) - 2.5f) < 1e-3f,
          $"出图比例 = 视高/视口高 = 2.5（实际 {LayoutProjection.Scale(vp):F3}）");
    Check(MathF.Abs(LayoutProjection.Zoom(vp) - 0.4f) < 1e-4f,
          $"缩放系数 = 视口高/视高 = 0.4（方向反了会得到 2.5，实际 {LayoutProjection.Zoom(vp):F4}）");

    var m = LayoutProjection.ModelToPaper(vp);
    var pCenter = LayoutProjection.Transform(m, new SKPoint(200, 150));
    Check(MathF.Abs(pCenter.X - 210f) < 1e-3f && MathF.Abs(pCenter.Y - 148f) < 1e-3f,
          $"视点映射到视口中心 (210,148)（实际 {pCenter.X:F2},{pCenter.Y:F2}）");

    // 模型右移 90 → 图纸右移 90/2.5 = 36
    var pRight = LayoutProjection.Transform(m, new SKPoint(290, 150));
    Check(MathF.Abs(pRight.X - 246f) < 1e-3f,
          $"模型 +90 → 图纸 +36（实际 {pRight.X:F2}）");

    // 模型上移 90 → 图纸上移 36（Y 不翻转：两套坐标都是 Y 向上）
    var pUp = LayoutProjection.Transform(m, new SKPoint(200, 240));
    Check(MathF.Abs(pUp.Y - 184f) < 1e-3f,
          $"模型 +90 → 图纸 +36（Y 方向不翻转，实际 {pUp.Y:F2}）");

    var rect = LayoutProjection.PaperRect(vp);
    Check(MathF.Abs(rect.Left - 120f) < 1e-3f && MathF.Abs(rect.Top - 88f) < 1e-3f
          && MathF.Abs(rect.Width - 180f) < 1e-3f && MathF.Abs(rect.Height - 120f) < 1e-3f,
          $"裁剪矩形 = 视口矩形 (120,88,180x120)（实际 {rect}）");

    var zero = new ACadSharp.Entities.Viewport { Width = 0, Height = 0, ViewHeight = 0 };
    Check(!LayoutProjection.IsDrawable(zero), "零尺寸视口不参与投影");
}

// ---------------------------------------------------------------- 工具

/// <summary>
/// HATCH 孤岛（B1）——在内存里直接构造 HATCH，不走 DXF 读写。
///
/// 这一组断言存在的理由：孤岛是"填充/图案的洞里不能有东西"，而**看起来正常**的
/// 错误渲染很多（外框在、图案在，只是洞被填了）。所以断言必须落在
/// "洞内某点被不被覆盖"和"图案线是否绕开洞"这种可判定的量上。
/// </summary>
void RunHatchChecks()
{
    Log();
    Log("=== HATCH 孤岛（外边界 + 洞） ===");

    // 外边界 0..100 正方形（4 条 LINE 边），孤岛 30..70 正方形
    static Hatch.BoundaryPath Square(double x0, double y0, double x1, double y1)
    {
        var bp = new Hatch.BoundaryPath();
        void Edge(double ax, double ay, double bx, double by)
            => bp.Edges.Add(new Hatch.BoundaryPath.Line
            {
                Start = new CSMath.XY(ax, ay),
                End = new CSMath.XY(bx, by),
            });
        Edge(x0, y0, x1, y0);
        Edge(x1, y0, x1, y1);
        Edge(x1, y1, x0, y1);
        Edge(x0, y1, x0, y0);
        return bp;
    }

    static List<CadEntity> ConvertHatch(Hatch h)
    {
        var doc = new ACadSharp.CadDocument();
        doc.Entities.Add(h);
        return new GeometryConverter().Convert(doc);
    }

    // ---- 1. 实心 + 孤岛 ----
    var solid = new Hatch { IsSolid = true };
    solid.Paths.Add(Square(0, 0, 100, 100));
    solid.Paths.Add(Square(30, 30, 70, 70));
    var ents = ConvertHatch(solid);
    var fills = ents.Where(e => e.FillPath is not null).ToList();
    Check(fills.Count == 1, $"两环合并为 1 个填充（逐环独立填充时为 2，实际 {fills.Count}）");
    if (fills.Count == 1)
    {
        var p = fills[0].FillPath!;
        Check(p.FillType == SKPathFillType.EvenOdd, $"填充规则为 EvenOdd（实际 {p.FillType}）");
        // 四边直线环必须是真正的多边形：包围盒 = 100x100（逐边子路径退化时是 0 高/宽）
        Check(MathF.Abs(p.Bounds.Width - 100f) < 1e-2f && MathF.Abs(p.Bounds.Height - 100f) < 1e-2f,
              $"直线环填充面积为整块（包围盒 {p.Bounds.Width:F1}x{p.Bounds.Height:F1}）");
        Check(p.Contains(10f, 10f), "外环内部被填充（(10,10)）");
        Check(!p.Contains(50f, 50f), "孤岛内部是洞，不被填充（(50,50)）");
        Check(p.Contains(35f, 20f), "孤岛下方仍被填充（(35,20)）");
    }

    // ---- 2. 洞中洞：三层 → 最内层又变实心 ----
    var nested = new Hatch { IsSolid = true };
    nested.Paths.Add(Square(0, 0, 100, 100));
    nested.Paths.Add(Square(20, 20, 80, 80));
    nested.Paths.Add(Square(40, 40, 60, 60));
    var nEnts = ConvertHatch(nested).Where(e => e.FillPath is not null).ToList();
    Check(nEnts.Count == 1, $"三层环仍为 1 个填充（实际 {nEnts.Count}）");
    if (nEnts.Count == 1)
    {
        var p = nEnts[0].FillPath!;
        Check(p.Contains(10f, 10f) && !p.Contains(30f, 30f) && p.Contains(50f, 50f),
              $"奇偶规则：第1层实/第2层洞/第3层实（10,10)={p.Contains(10f, 10f)} (30,30)={p.Contains(30f, 30f)} (50,50)={p.Contains(50f, 50f)}）");
    }

    // ---- 3. 图案填充 + 孤岛：水平线族必须绕开孤岛 ----
    var pat = new Hatch
    {
        IsSolid = false,
        Pattern = new HatchPattern("U")
        {
            Lines =
            {
                new HatchPattern.Line
                {
                    Angle = 0,
                    BasePoint = new CSMath.XY(0, 0),
                    Offset = new CSMath.XY(0, 10),
                },
            },
        },
        PatternAngle = 0,
    };
    pat.Paths.Add(Square(0, 0, 100, 100));
    pat.Paths.Add(Square(30, 30, 70, 70));
    var pEnts = ConvertHatch(pat).Where(e => e.Path is not null).ToList();
    Check(pEnts.Count == 1, $"图案线合并为 1 条路径（实际 {pEnts.Count}）");
    if (pEnts.Count == 1)
    {
        var segs = Segments(pEnts[0].Path!);
        Check(segs.Count > 0, $"生成了图案线（段数 {segs.Count}）");

        // 行 y=35/45/55/65 落在孤岛内部 → 该行必须分成左右两段，中间不能有线
        int crossing = segs.Count(s =>
            MathF.Abs(s.a.Y - s.b.Y) < 1e-3f &&
            s.a.Y > 31f && s.a.Y < 69f &&
            MathF.Min(s.a.X, s.b.X) < 31f && MathF.Max(s.a.X, s.b.X) > 69f);
        Check(crossing == 0, $"图案线不穿过孤岛（横穿孤岛的行段数 {crossing}，未实现时会把整行连成一条）");

        // 孤岛外的行（y<30）应为整段 0→100
        bool fullRow = segs.Any(s =>
            MathF.Abs(s.a.Y - s.b.Y) < 1e-3f && s.a.Y < 29f &&
            MathF.Abs(MathF.Min(s.a.X, s.b.X)) < 0.6f && MathF.Abs(MathF.Max(s.a.X, s.b.X) - 100f) < 0.6f);
        Check(fullRow, "孤岛外的行仍是整段 0→100");

        // 孤岛内的行应被切成左右两段（左段止于 30、右段起于 70）
        bool splitRow = segs.Any(s => MathF.Abs(s.a.Y - s.b.Y) < 1e-3f && s.a.Y > 31f && s.a.Y < 69f
                                      && MathF.Abs(MathF.Max(s.a.X, s.b.X) - 30f) < 0.6f)
                        && segs.Any(s => MathF.Abs(s.a.Y - s.b.Y) < 1e-3f && s.a.Y > 31f && s.a.Y < 69f
                                      && MathF.Abs(MathF.Min(s.a.X, s.b.X) - 70f) < 0.6f);
        Check(splitRow, "孤岛内的行被切成左右两段（止于 x=30 / 起于 x=70）");
    }

    // ---- 4. 渐变填充按实心铺满（不是轮廓线） ----
    var grad = new Hatch { IsSolid = false };
    grad.Paths.Add(Square(0, 0, 100, 100));
    grad.GradientColor.Enabled = true;
    var gEnts = ConvertHatch(grad);
    Check(gEnts.Any(e => e.FillPath is not null), "渐变 HATCH 走实心填充（未实现时退化为边界轮廓）");
}

/// <summary>
/// 块属性 ATTRIB（B2）——内存构造，不走 DXF。
///
/// 为什么不能用 DXF 夹具：ACadSharp 的 DxfWriter 会把块定义里的 AttributeDefinition
/// 额外复制成一批**默认值 ATTRIB**（实测 2 个插入写了 10 条 ATTRIB，应为 5 条），
/// 读回来占位符文本也被画出来，夹具本身就错。真实 DWG 里每个 ATTDEF 只有一个 ATTRIB，
/// 所以这组断言在内存里直接构造，文件路径的验证用真实图纸做（见 docs）。
/// </summary>
void RunAttribChecks()
{
    Log();
    Log("=== 块属性 ATTRIB ===");

    var doc = new ACadSharp.CadDocument();
    var defNo = new AttributeDefinition
    {
        Tag = "NO",
        Value = "TAG",                       // 占位符文本：任何情况下都不该出现在图面上
        InsertPoint = new CSMath.XYZ(10, 0, 0),
        Height = 5,
    };
    var defInv = new AttributeDefinition
    {
        Tag = "INV",
        Value = "TAG3",
        InsertPoint = new CSMath.XYZ(10, 30, 0),
        Height = 5,
    };

    var block = new ACadSharp.Tables.BlockRecord("ATT");
    block.Entities.Add(new Circle { Center = new CSMath.XYZ(0, 0, 0), Radius = 5 });
    block.Entities.Add(defNo);
    block.Entities.Add(defInv);
    doc.BlockRecords.Add(block);

    var ins = new Insert(block)
    {
        InsertPoint = new CSMath.XYZ(100, 0, 0),
        XScale = 2,
        YScale = 2,
        ZScale = 2,
    };
    // ACadSharp 在把 INSERT 挂到文档时，会按块里的 AttributeDefinition **自动生成**属性
    // （值取占位符默认值，与 AutoCAD 新建块参照的行为一致）。真实 DWG 里每个 ATTDEF 只有
    // 一条 ATTRIB，所以必须**先挂文档**、再改自动生成的那几条（追加会造成同 tag 两条）。
    //
    // 坐标口径：DWG/DXF 里 ATTRIB 的插入点与字高**已经是变换后的最终值**（AutoCAD 写盘时
    // 就把本插入的平移/比例乘进去了，实测见 GeometryConverter.ConvertInsert 注释），
    // 所以这里也要写成"已变换"的值：(10,0) 高 5、插入点 (100,0) 比例 2 → (120,0) 高 10。
    doc.Entities.Add(ins);
    var attNo = ins.Attributes.First(a => a.Tag == "NO");
    attNo.Value = "A-01";
    attNo.InsertPoint = new CSMath.XYZ(120, 0, 0);
    attNo.Height = 10;
    var attInv = ins.Attributes.First(a => a.Tag == "INV");
    attInv.Value = "HIDDEN";
    attInv.IsInvisible = true;

    // ② 嵌套一层：属性坐标在**外层块**的空间里 → 必须再叠加外层矩阵（这里的 +1000 平移）
    var wrap = new ACadSharp.Tables.BlockRecord("WRAP");
    doc.BlockRecords.Add(wrap);
    var inner = new Insert(block) { InsertPoint = new CSMath.XYZ(0, 0, 0) };
    wrap.Entities.Add(inner);
    var outerIns = new Insert(wrap) { InsertPoint = new CSMath.XYZ(1000, 0, 0) };
    doc.Entities.Add(outerIns);
    var innerAtt = inner.Attributes.First(a => a.Tag == "NO");
    innerAtt.Value = "N-09";
    innerAtt.InsertPoint = new CSMath.XYZ(120, 0, 0);
    innerAtt.Height = 10;
    inner.Attributes.First(a => a.Tag == "INV").IsInvisible = true;   // 默认值那条也不要画

    var converter = new GeometryConverter();
    var ents = converter.Convert(doc);
    var texts = ents.Where(e => e.Text is not null).ToList();
    Log($"  实体 {ents.Count} 个（其中文字 {texts.Count}）：{string.Join(" | ", texts.Select(t => $"{t.Text!.Text}@({t.Text.Position.X:F0},{t.Text.Position.Y:F0})h{t.Text.Height:F0}"))}");
    Log($"  属性统计：绘制 {converter.AttributeDrawn}，不可见 {converter.AttributeInvisible}，占位符 {converter.AttributeDefsSkipped}");

    Check(texts.Count == 2, $"画出 2 条属性文字（不可见属性被跳过，实际 {texts.Count}）");
    Check(texts.All(t => t.Text!.Text is not ("TAG" or "TAG3")),
          "块定义里的占位符文本没有被画出来");
    Check(converter.AttributeDefsSkipped == 4, $"两个块实例各 2 个占位符被跳过（实际 {converter.AttributeDefsSkipped}）");
    Check(converter.AttributeInvisible == 2, $"不可见属性跳过 2 条（实际 {converter.AttributeInvisible}）");

    var direct = texts.FirstOrDefault(t => t.Text!.Text == "A-01");
    Check(direct is not null, "属性值 A-01 已画出");
    if (direct is not null)
    {
        Check(MathF.Abs(direct.Text!.Position.X - 120f) < 1e-2f,
              $"属性坐标按存值原样使用、不再乘插入矩阵（期望 120，实际 {direct.Text.Position.X:F2}；错乘会得 220）");
        Check(MathF.Abs(direct.Text.Height - 10f) < 1e-2f,
              $"属性字高按存值原样使用（期望 10，实际 {direct.Text.Height:F2}；错乘会得 20）");
    }

    var nested = texts.FirstOrDefault(t => t.Text!.Text == "N-09");
    Check(nested is not null, "嵌套块里的属性已画出");
    if (nested is not null)
        Check(MathF.Abs(nested.Text!.Position.X - 1120f) < 1e-2f,
              $"嵌套块属性叠加外层矩阵到 x=1120（实际 {nested.Text.Position.X:F2}）");
}

/// <summary>
/// 线宽（B3）——内存构造。
///
/// <para>锁三件事：① BYLAYER 取图层线宽、② BYBLOCK 逐层继承块引用线宽、
/// ③「1/100 mm → 世界单位」的换算（WorldUnitsPerMm）。</para>
/// </summary>
/// <remarks>
/// AutoCAD 的线宽语义有两处与直觉不同，这里用断言钉住：
///   · 块内实体写 BYLAYER 时按**实体自己所在图层**取线宽，与块引用无关；
///     只有写 BYBLOCK 才继承 INSERT 的有效线宽（本例 0.50mm）。
///   · 顶层（不在块里）的 BYBLOCK 没有可继承对象，走默认 0.25mm。
/// </remarks>
void RunLineWeightChecks()
{
    Log();
    Log("=== 线宽 ===");

    var doc = new ACadSharp.CadDocument();
    doc.Layers.Add(new ACadSharp.Tables.Layer("W50") { LineWeight = ACadSharp.LineWeightType.W50 });
    doc.Layers.Add(new ACadSharp.Tables.Layer("W13") { LineWeight = ACadSharp.LineWeightType.W13 });
    doc.Layers.Add(new ACadSharp.Tables.Layer("WDEF") { LineWeight = ACadSharp.LineWeightType.Default });
    doc.Layers.Add(new ACadSharp.Tables.Layer("WDIP") { LineWeight = ACadSharp.LineWeightType.ByDIPs });

    static Line Line(double y, string layer) => new()
    {
        StartPoint = new CSMath.XYZ(0, y, 0),
        EndPoint = new CSMath.XYZ(10, y, 0),
        Layer = new ACadSharp.Tables.Layer(layer),
    };

    // ① BYLAYER → 图层线宽
    var byLayer50 = Line(0, "W50"); byLayer50.LineWeight = ACadSharp.LineWeightType.ByLayer;
    var byLayer13 = Line(10, "W13"); byLayer13.LineWeight = ACadSharp.LineWeightType.ByLayer;
    // ② 实体显式线宽优先于图层
    var explicit211 = Line(20, "W13"); explicit211.LineWeight = ACadSharp.LineWeightType.W211;
    // ③ ByDIPs：按设备像素，不参与毫米口径 → 0（细线）
    var byDips = Line(30, "WDIP"); byDips.LineWeight = ACadSharp.LineWeightType.ByDIPs;
    // ④ 默认线宽图层 → 0.25mm
    var byDefault = Line(40, "WDEF"); byDefault.LineWeight = ACadSharp.LineWeightType.ByLayer;
    doc.Entities.Add(byLayer50);
    doc.Entities.Add(byLayer13);
    doc.Entities.Add(explicit211);
    doc.Entities.Add(byDips);
    doc.Entities.Add(byDefault);

    // ⑤ 块内 BYBLOCK：继承 INSERT 的有效线宽（INSERT 在 W50 层、BYLAYER → 0.50mm）
    var blk = new ACadSharp.Tables.BlockRecord("BLW");
    var childByBlock = Line(0, "0"); childByBlock.LineWeight = ACadSharp.LineWeightType.ByBlock;
    var childByLayer = Line(5, "W13"); childByLayer.LineWeight = ACadSharp.LineWeightType.ByLayer;
    blk.Entities.Add(childByBlock);
    blk.Entities.Add(childByLayer);
    doc.BlockRecords.Add(blk);

    var insChain = new Insert(blk) { InsertPoint = new CSMath.XYZ(100, 0, 0), Layer = new ACadSharp.Tables.Layer("W50") };
    insChain.LineWeight = ACadSharp.LineWeightType.ByLayer;
    doc.Entities.Add(insChain);

    // ⑥ 嵌套两层：外层 INSERT 显式 W100，内层 INSERT 写 BYBLOCK → 块内实体拿 1.00mm
    var innerBlk = new ACadSharp.Tables.BlockRecord("BLIN");
    var deepChild = Line(0, "0"); deepChild.LineWeight = ACadSharp.LineWeightType.ByBlock;
    innerBlk.Entities.Add(deepChild);
    doc.BlockRecords.Add(innerBlk);
    var innerIns = new Insert(innerBlk) { InsertPoint = new CSMath.XYZ(0, 0, 0) };
    innerIns.LineWeight = ACadSharp.LineWeightType.ByBlock;
    var wrap = new ACadSharp.Tables.BlockRecord("BLWRAP");
    wrap.Entities.Add(innerIns);
    doc.BlockRecords.Add(wrap);
    var outerIns = new Insert(wrap) { InsertPoint = new CSMath.XYZ(1000, 0, 0) };
    outerIns.LineWeight = ACadSharp.LineWeightType.W100;
    doc.Entities.Add(outerIns);

    var converter = new GeometryConverter();
    var ents = converter.Convert(doc);
    Log($"  实体 {ents.Count} 个，带线宽 {converter.LineWeightEntities} 个");
    Log("  " + string.Join(" | ", ents.Where(e => e.Path is not null)
        .Select(e => $"({e.Bounds.Left:F0},{e.Bounds.Top:F0})lw{e.LineWeightMm100}")));

    int LwAt(float x, float y)
    {
        var hit = ents.FirstOrDefault(e => e.Path is not null
            && MathF.Abs(e.Bounds.Left - x) < 1f && MathF.Abs(e.Bounds.Top - y) < 1f);
        return hit?.LineWeightMm100 ?? -999;
    }

    Check(LwAt(0, 0) == 50, $"BYLAYER 取图层线宽 0.50mm（实际 {LwAt(0, 0)}）");
    Check(LwAt(0, 10) == 13, $"BYLAYER 取图层线宽 0.13mm（实际 {LwAt(0, 10)}）");
    Check(LwAt(0, 20) == 211, $"实体显式线宽覆盖图层（期望 211，实际 {LwAt(0, 20)}）");
    Check(LwAt(0, 30) == 0, $"ByDIPs 不参与毫米口径（期望 0，实际 {LwAt(0, 30)}）");
    Check(LwAt(0, 40) == 25, $"图层 Default → 0.25mm 默认值（实际 {LwAt(0, 40)}）");

    Check(LwAt(100, 0) == 50, $"块内 BYBLOCK 继承块引用线宽 0.50mm（实际 {LwAt(100, 0)}）");
    Check(LwAt(100, 5) == 13, $"块内 BYLAYER 按自己图层（期望 13，实际 {LwAt(100, 5)}）");
    Check(LwAt(1000, 0) == 100, $"嵌套块 BYBLOCK 逐层继承到 1.00mm（实际 {LwAt(1000, 0)}）");

    // 线宽**不在转换期**换算成像素：模型空间是固定像素宽、图纸空间按真实单位，
    // 同一个毫米值要两种换算，只能在渲染期（知道缩放与空间）做。这里只锁"毫米原值不被改写"。
    Check(converter.LineWeightEntities == 7, $"统计带线宽实体 7 个（8 条里 ByDIPs 那条不算，实际 {converter.LineWeightEntities}）");

    // ---- 两种空间的像素口径（AutoCAD 帮助：模型空间不随缩放变、图纸空间按真实单位随缩放变）----
    float ModelPx(int mm100, float zoom) => LineWeightMetrics.ToPixels(mm100, paperSpace: false, zoom, 1f);
    float PaperPx(int mm100, float zoom) => LineWeightMetrics.ToPixels(mm100, paperSpace: true, zoom, 1f);

    Log($"  模型空间 px: 0.13mm→{ModelPx(13, 1f):F2} 0.25→{ModelPx(25, 1f):F2} 0.50→{ModelPx(50, 1f):F2} 2.11→{ModelPx(211, 1f):F2}");
    Check(MathF.Abs(ModelPx(25, 1f) - 0.984f) < 0.02f, $"模型空间 0.25mm ≈ 1px（实际 {ModelPx(25, 1f):F3}）");
    Check(MathF.Abs(ModelPx(50, 1f) - 1.89f) < 0.03f, $"模型空间 0.50mm ≈ 2px（实际 {ModelPx(50, 1f):F3}）");
    Check(MathF.Abs(ModelPx(211, 1f) - 7.97f) < 0.05f, $"模型空间 2.11mm ≈ 8px（实际 {ModelPx(211, 1f):F3}）");
    Check(MathF.Abs(ModelPx(50, 1f) - ModelPx(50, 1f / 64f)) < 1e-6f && MathF.Abs(ModelPx(50, 1f) - ModelPx(50, 64f)) < 1e-6f,
          "模型空间线宽与缩放无关（1x / 1/64x / 64x 结果相同）");
    Check(ModelPx(13, 1f) == 1f, $"不足 1px 的线宽按 1px 兜底（0.13mm @1x → {ModelPx(13, 1f):F2}）");
    Check(ModelPx(0, 1f) == 1f, "线宽 0（ByDIPs）按 1px");

    Log($"  图纸空间 px: 0.50mm z=1→{PaperPx(50, 1f):F2} z=4→{PaperPx(50, 4f):F2} z=0.25→{PaperPx(50, 0.25f):F2}");
    Check(MathF.Abs(PaperPx(50, 3f) - 1.5f) < 1e-4f, $"图纸空间 0.50mm @3x = 1.5px（真实单位，实际 {PaperPx(50, 3f):F3}）");
    Check(PaperPx(50, 1f) == 1f, $"图纸空间 0.50mm @1x 不足 1px → 兜底 1px（实际 {PaperPx(50, 1f):F3}）");
    Check(MathF.Abs(PaperPx(50, 4f) - 2f) < 1e-4f, $"图纸空间 0.50mm @4x = 2px（随缩放变粗，实际 {PaperPx(50, 4f):F3}）");
    Check(MathF.Abs(PaperPx(50, 0.25f) - 1f) < 1e-4f, $"图纸空间缩小后有 1px 下限（实际 {PaperPx(50, 0.25f):F3}）");
    Check(MathF.Abs(PaperPx(50, 2f) - ModelPx(50, 2f)) > 0.1f,
          $"两种空间口径确实不同（0.50mm @2x：图纸 {PaperPx(50, 2f):F2}px vs 模型 {ModelPx(50, 2f):F2}px）");
    Check(MathF.Abs(LineWeightMetrics.ToPixels(50, true, 2f, worldUnitsPerMm: 2f) - 2f) < 1e-4f,
          "图纸单位不是 mm 时按 worldUnitsPerMm 折算（2 单位/mm @2x → 2px）");

    // 世界单位描边宽度 = 像素 / 缩放（画布已 concat 世界→屏幕矩阵）
    Check(MathF.Abs(LineWeightMetrics.ToWorldStroke(211, false, 10f, 1f) - 0.797f) < 0.01f,
          $"2.11mm @10x → 描边 0.797 世界单位（= 7.97px / 10，实际 {LineWeightMetrics.ToWorldStroke(211, false, 10f, 1f):F3}）");
}

/// <summary>
/// 线宽样例 sample_lineweight.dxf —— 走**文件**这条路（DXF 370 组码写出/读入 + 图层线宽表）。
/// 与 <see cref="RunLineWeightChecks"/> 的内存构造互补：那边锁解析语义，这边锁"真实文件里也对"。
/// </summary>
void RunLineWeightFileSample(string path)
{
    Log();
    Log("=== 线宽样例 sample_lineweight.dxf ===");
    var list = CadDocumentLoader.Load(path);
    Log($"加载: {path}（{list.Entities.Count} 个实体，带线宽 {list.LineWeightEntities} 个）");

    // 五条线从下往上 y = 0/40/80/120/160，按 y 排序后逐条核对线宽
    var rows = list.Entities
        .Where(e => e.Path is not null && MathF.Abs(e.Bounds.Left) < 0.5f && MathF.Abs(e.Bounds.Right - 200f) < 0.5f)
        .OrderBy(e => e.Bounds.Top)
        .ToList();
    Log("  " + string.Join(" | ", rows.Select(r => $"y={r.Bounds.Top:F0}→{r.LineWeightMm100}")));

    Check(rows.Count == 5, $"5 条水平线（实际 {rows.Count}）");
    if (rows.Count == 5)
    {
        Check(rows[0].LineWeightMm100 == 13, $"① 图层 LW13 → 0.13mm（实际 {rows[0].LineWeightMm100}）");
        Check(rows[1].LineWeightMm100 == 50, $"② 图层 LW50 → 0.50mm（实际 {rows[1].LineWeightMm100}）");
        Check(rows[2].LineWeightMm100 == 211, $"③ 图层 LW211 → 2.11mm（实际 {rows[2].LineWeightMm100}）");
        Check(rows[3].LineWeightMm100 == 100, $"④ 实体显式 1.00mm 覆盖图层（实际 {rows[3].LineWeightMm100}）");
        Check(rows[4].LineWeightMm100 == 50, $"⑤ 块内 BYBLOCK 继承块引用 0.50mm（实际 {rows[4].LineWeightMm100}）");
    }
    Check(list.LineWeightEntities == 5, $"统计带线宽实体 5 个（实际 {list.LineWeightEntities}）");
    Check(MathF.Abs(list.WorldUnitsPerMm - 1f) < 1e-6f,
          $"图纸空间 1mm = 1 图纸单位（实际 {list.WorldUnitsPerMm:F3}）");
}

/// <summary>变宽多段线样例（文件路径：宽度从 DXF 40/41 组码读回后仍要正确）。</summary>
void RunWidePolylineFileSample(string path)
{
    Log();
    Log("=== 变宽多段线样例 sample_widepolyline.dxf ===");
    var list = CadDocumentLoader.Load(path);
    var fills = list.Entities.Where(e => e.FillPath is not null).ToList();
    Log($"加载: {path}（{list.Entities.Count} 个实体，其中填充 {fills.Count}）");
    Check(fills.Count == 2, $"2 条宽多段线画成填充（实际 {fills.Count}）");
    if (fills.Count == 2)
    {
        var even = fills.OrderBy(f => f.Bounds.Height).First();
        var taper = fills.OrderBy(f => f.Bounds.Height).Last();
        Check(MathF.Abs(even.Bounds.Height - 4f) < 0.05f, $"等宽段轮廓宽 4（实际 {even.Bounds.Height:F3}）");
        Check(MathF.Abs(taper.Bounds.Height - 8f) < 0.05f, $"锥形段最宽 8（实际 {taper.Bounds.Height:F3}）");
    }
    Check(list.WidePolylineEntities == 2, $"统计带宽度多段线 2 条（实际 {list.WidePolylineEntities}）");
}

/// <summary>
/// 变宽多段线（B4）—— 内存构造，锁"宽度是几何、按轮廓填充"这条口径。
///
/// <para>关键判据全部落在**填充轮廓的包围盒/面积**上：
/// 等宽段是矩形（面积 = 长 × 宽）、锥形段是梯形（面积 = 长 × 平均宽）、
/// 接头要斜接（外角要突出到 w/2 之外）、闭合段没有端头。</para>
/// </summary>
void RunWidePolylineChecks()
{
    Log();
    Log("=== 变宽多段线 ===");

    static LwPolyline Poly(params (double x, double y, double sw, double ew, double bulge)[] pts)
    {
        var p = new LwPolyline();
        foreach (var (x, y, sw, ew, bulge) in pts)
            p.Vertices.Add(new LwPolyline.Vertex(new CSMath.XY(x, y))
            {
                StartWidth = sw,
                EndWidth = ew,
                Bulge = bulge,
            });
        return p;
    }

    List<CadEntity> Convert(LwPolyline lw, out GeometryConverter conv)
    {
        var doc = new ACadSharp.CadDocument();
        doc.Entities.Add(lw);
        conv = new GeometryConverter();
        return conv.Convert(doc);
    }

    // ---- 1. 等宽：长 100、宽 4 → 矩形 100 × 4，面积 400 ----
    var even = Poly((0, 0, 4, 4, 0), (100, 0, 4, 4, 0));
    var ents = Convert(even, out var conv1);
    var fill = ents.FirstOrDefault(e => e.FillPath is not null);
    Check(fill is not null, "有宽度的多段线画成填充轮廓（不是加粗描边）");
    Check(fill is not null && ents.All(e => e.Path is null || e.FillPath is not null),
          "有宽度时不再输出描边几何（AutoCAD：全局宽度忽略线宽）");
    if (fill is not null)
    {
        var b = fill.Bounds;
        Check(MathF.Abs(b.Width - 100f) < 0.01f, $"轮廓长度 = 100（实际 {b.Width:F3}）");
        Check(MathF.Abs(b.Height - 4f) < 0.01f, $"轮廓宽度 = 4（实际 {b.Height:F3}）");
        // 注意 SKRect 的 Top/Bottom 是"屏幕口径"：Top = y 较小者、Bottom = y 较大者，
        // 与 AutoCAD 的"上下边"相反（本项目世界坐标 y 向上，这一区别踩过好几次）。
        var rb = fill.FillPath!.Bounds;
        Check(MathF.Abs(rb.Top + 2f) < 0.01f && MathF.Abs(rb.Bottom - 2f) < 0.01f,
              $"轴线居中：轮廓 y ∈ [-2, 2]（实际 Top={rb.Top:F2}, Bottom={rb.Bottom:F2}）");
    }
    Check(conv1.WidePolylineEntities == 1, $"统计带宽度多段线 1 条（实际 {conv1.WidePolylineEntities}）");

    // ---- 2. 锥形：起点宽 0、终点宽 6 → 三角形，尖端在起点 ----
    var taper = Poly((0, 0, 0, 6, 0), (100, 0, 6, 6, 0));
    var tEnts = Convert(taper, out _);
    var tFill = tEnts.FirstOrDefault(e => e.FillPath is not null)?.FillPath;
    Check(tFill is not null, "锥形段也走填充轮廓");
    if (tFill is not null)
    {
        Check(MathF.Abs(tFill.Bounds.Height - 6f) < 0.01f, $"锥形最宽处 = 6（实际 {tFill.Bounds.Height:F3}）");
        // 尖端落在 (0,0)：轮廓在 x=0 处应收缩成一点
        Check(MathF.Abs(tFill.Bounds.Left) < 0.01f, $"尖端落在起点 x=0（实际 {tFill.Bounds.Left:F3}）");
    }

    // ---- 3. 拐角斜接：L 形宽 4 → 外角必须突出到 (102, -2) ----
    var corner = Poly((0, 0, 4, 4, 0), (100, 0, 4, 4, 0), (100, 100, 4, 4, 0));
    var cFill = Convert(corner, out _).FirstOrDefault(e => e.FillPath is not null)?.FillPath;
    Check(cFill is not null, "折线拐角生成轮廓");
    if (cFill is not null)
    {
        Check(MathF.Abs(cFill.Bounds.Right - 102f) < 0.01f,
              $"外角沿法线外扩 w/2 = 2（右边到 102，实际 {cFill.Bounds.Right:F3}；不做斜接会得 100）");
        Check(MathF.Abs(cFill.Bounds.Top + 2f) < 0.01f,
              $"内侧偏移仍是 w/2 = 2（最小 y = -2，实际 {cFill.Bounds.Top:F3}）");
        Check(MathF.Abs(cFill.Bounds.Bottom - 100f) < 0.01f,
              $"末端垂直端面止于 y=100（实际 {cFill.Bounds.Bottom:F3}）");
    }

    // ---- 4. 闭合：正方形宽 6 → 轮廓 (±3)，没有端头 ----
    var closedPoly = Poly((0, 0, 6, 6, 0), (100, 0, 6, 6, 0), (100, 100, 6, 6, 0), (0, 100, 6, 6, 0));
    closedPoly.IsClosed = true;
    var kFill = Convert(closedPoly, out _).FirstOrDefault(e => e.FillPath is not null)?.FillPath;
    Check(kFill is not null, "闭合多段线生成轮廓");
    if (kFill is not null)
    {
        Check(MathF.Abs(kFill.Bounds.Left + 3f) < 0.01f && MathF.Abs(kFill.Bounds.Right - 103f) < 0.01f,
              $"闭合轮廓四边各外扩 3（x ∈ [-3,103]，实际 [{kFill.Bounds.Left:F2}, {kFill.Bounds.Right:F2}]）");
        Check(MathF.Abs(kFill.Bounds.Top + 3f) < 0.01f && MathF.Abs(kFill.Bounds.Bottom - 103f) < 0.01f,
              $"闭合轮廓 y ∈ [-3,103]（实际 Top={kFill.Bounds.Top:F2}, Bottom={kFill.Bounds.Bottom:F2}）");
    }

    // ---- 5. 带凸度的宽段：半圆（半径 10）+ 宽 4 → 外沿半径 12 ----
    var bulge = Poly((0, 0, 4, 4, 1), (20, 0, 4, 4, 0));
    bulge.IsClosed = true;   // 单段 + 闭合：避免收尾零长度段
    var bFill = Convert(bulge, out _).FirstOrDefault(e => e.FillPath is not null)?.FillPath;
    if (bFill is not null)
    {
        // 半圆半径 10、带宽 4：弧外侧到 12、弦外侧到 2 → 法向总跨度 14（按中线画只有 12）
        float h = bFill.Bounds.Height;
        Log($"  半圆宽段轮廓包围盒 {bFill.Bounds.Width:F2} × {h:F2}，y ∈ [{bFill.Bounds.Top:F2}, {bFill.Bounds.Bottom:F2}]");
        Check(MathF.Abs(h - 14f) < 0.6f,
              $"弧外侧到 12、弦外侧到 2，法向跨度 14（实际 {h:F2}；按中线画只有 12）");
        Check(MathF.Abs(bFill.Bounds.Left + 2f) < 0.3f && MathF.Abs(bFill.Bounds.Right - 22f) < 0.3f,
              $"轴向两端各外扩 w/2 = 2（x ∈ [-2,22]，实际 [{bFill.Bounds.Left:F2}, {bFill.Bounds.Right:F2}]）");
    }
    else Check(false, "带凸度的宽段能生成轮廓");

    // ---- 6. 宽度全 0 → 退回普通描边 ----
    var thin = Poly((0, 0, 0, 0, 0), (100, 0, 0, 0, 0));
    var thinEnts = Convert(thin, out var conv6);
    Check(thinEnts.Any(e => e.Path is not null) && thinEnts.All(e => e.FillPath is null),
          "宽度全 0 的多段线仍走描边");
    Check(conv6.WidePolylineEntities == 0, $"宽度全 0 不计数（实际 {conv6.WidePolylineEntities}）");

    // ---- 7. ConstantWidth（组码 43）等价于每段等宽 ----
    var constant = Poly((0, 0, 0, 0, 0), (100, 0, 0, 0, 0));
    constant.ConstantWidth = 4;
    var cwFill = Convert(constant, out _).FirstOrDefault(e => e.FillPath is not null)?.FillPath;
    Check(cwFill is not null && MathF.Abs(cwFill.Bounds.Height - 4f) < 0.01f,
          $"ConstantWidth=4 生效（轮廓宽 {cwFill?.Bounds.Height:F2}）");

    // ---- 8. 180° 折返不能生成尖刺（斜接限制必须生效） ----
    var reversal = Poly((0, 0, 4, 4, 0), (100, 0, 4, 4, 0), (0, 0, 4, 4, 0));
    var rFill = Convert(reversal, out _).FirstOrDefault(e => e.FillPath is not null)?.FillPath;
    Check(rFill is not null, "180° 折返仍能生成轮廓");
    if (rFill is not null)
    {
        var rb = rFill.Bounds;
        Log($"  折返轮廓 {rb.Width:F2} × {rb.Height:F2}");
        Check(rb.Width < 110f && rb.Height < 10f,
              $"折返没有生成尖刺（包围盒 {rb.Width:F1}×{rb.Height:F1}；无限制时斜接会飞出几千单位）");
        Check(!float.IsNaN(rb.Left) && !float.IsInfinity(rb.Left), "折返轮廓坐标有限（无 NaN）");
    }

    // ---- 9. 重复顶点（零长段）不能产生 NaN ----
    var dup = Poly((0, 0, 4, 4, 0), (0, 0, 4, 4, 0), (100, 0, 4, 4, 0));
    var dFill = Convert(dup, out _).FirstOrDefault(e => e.FillPath is not null)?.FillPath;
    Check(dFill is not null && !float.IsNaN(dFill.Bounds.Left),
          $"重复顶点（零长段）不产生 NaN（包围盒宽 {dFill?.Bounds.Width:F2}）");
    Check(dFill is not null && MathF.Abs(dFill.Bounds.Height - 4f) < 0.01f,
          $"重复顶点不影响轮廓宽度（实际 {dFill?.Bounds.Height:F2}）");

    // ---- 10. 只有起点宽度（EndWidth=0）按等宽处理 ----
    var halfSpec = Poly((0, 0, 4, 0, 0), (100, 0, 0, 0, 0));
    var hFill = Convert(halfSpec, out _).FirstOrDefault(e => e.FillPath is not null)?.FillPath;
    Check(hFill is not null && MathF.Abs(hFill.Bounds.Height - 4f) < 0.01f,
          $"只给起点宽度时按等宽（轮廓宽 {hFill?.Bounds.Height:F2}）");
}

/// <summary>
/// 曲线精度（P2-1 顺时针凸度弧 / P2-2 椭圆）—— 判据是**路径长度与理论值之差**。
///
/// <para>折线化必然偏短：n 段等分的弦长总和 = 弧长 × sin(θ/2)/(θ/2)。
/// 5° 步长偏短 0.032%、128 段椭圆偏短 0.001%。用 <see cref="SKPathMeasure"/> 量出来直接比。</para>
/// </summary>
void RunCurvePrecisionChecks()
{
    Log();
    Log("=== 曲线精度 ===");

    // resScale 必须给大：SKPathMeasure 默认 resScale=1，对几十单位大的小路径按 ~0.5 单位的
    // 容差折线化，量半径 10 的半圆会偏短 0.6%（实测），比"折线 vs 精确"的差异还大。
    static float PathLength(SKPath p)
    {
        using var measure = new SKPathMeasure(p, false, 200f);
        float total = 0;
        do { total += measure.Length; } while (measure.NextContour());
        return total;
    }

    // ---- P2-1：顺时针凸度弧 = 半径 10 的半圆（弧长 πr = 31.4159）----
    var cw = new SKPath();
    cw.MoveTo(0, 0);
    CadGeometry.AppendBulgeArc(cw, new SKPoint(0, 0), new SKPoint(20, 0), -1.0);
    float cwLen = PathLength(cw);
    Log($"  顺时针半圆弧长 {cwLen:F4}（理论 31.4159）");
    Check(MathF.Abs(cwLen - (float)Math.PI * 10f) / ((float)Math.PI * 10f) < 5e-4f,
          $"顺时针凸度弧长度误差 < 0.05%（5° 折线会偏短 0.3%，实际偏差 {(cwLen - MathF.PI * 10f) / (MathF.PI * 10f):P3}）");

    var ccw = new SKPath();
    ccw.MoveTo(0, 0);
    CadGeometry.AppendBulgeArc(ccw, new SKPoint(0, 0), new SKPoint(20, 0), 1.0);
    Check(MathF.Abs(PathLength(ccw) - cwLen) < 0.05f,
          $"顺时针与逆时针同凸度弧长一致（{PathLength(ccw):F3} vs {cwLen:F3}）");
    // 方向相反：一条在弦上方、一条在下方
    Check(ccw.Bounds.Top < 0f && cw.Bounds.Top >= -0.001f,
          $"正凸度向上、负凸度向下（ccw.Top={ccw.Bounds.Top:F2}, cw.Top={cw.Bounds.Top:F2}）");

    // ---- P2-2：椭圆精确构造 ----
    // 长轴 20、半径比 0.5（短轴 10）的整椭圆：周长近似（Ramanujan）≈ 96.6
    var ell = CadGeometry.BuildEllipticalArc(0, 0, 20, 0, 0, 10, 0, 2 * Math.PI);
    Check(ell is not null, "整椭圆构造成功");
    if (ell is not null)
    {
        var sampled = new SKPath();
        CadGeometry.AppendEllipticalArcSampled(sampled, 0, 0, 20, 0, 0, 10, 0, 2 * Math.PI, 128);
        float exactLen = PathLength(ell), sampledLen = PathLength(sampled);
        double a = 20, b = 10;
        double h = Math.Pow(a - b, 2) / Math.Pow(a + b, 2);
        double theory = Math.PI * (a + b) * (1 + 3 * h / (10 + Math.Sqrt(4 - 3 * h)));  // Ramanujan，误差 ~1e-6
        Log($"  椭圆周长：理论 {theory:F3} / 精确 {exactLen:F3} / 128 段折线 {sampledLen:F3}");
        Check(Math.Abs(exactLen - theory) / theory < 1e-3,
              $"精确椭圆周长与 Ramanujan 公式相差 < 0.1%（实际 {exactLen:F3} vs {theory:F3}）");
        Check(exactLen > sampledLen,
              $"精确椭圆比 128 段折线更长（{exactLen:F1} > {sampledLen:F1}，折线化必然偏短）");
        Check(MathF.Abs(ell.Bounds.Width - 40f) < 0.01f && MathF.Abs(ell.Bounds.Height - 20f) < 0.01f,
              $"椭圆包围盒 40×20（实际 {ell.Bounds.Width:F3}×{ell.Bounds.Height:F3}）");
        // 整椭圆必须闭合（首尾接上），否则描边缺一小段
        using var measure = new SKPathMeasure(ell, false, 200f);
        Check(measure.IsClosed, "整椭圆路径闭合");
    }

    // 椭圆**弧**（四分之一）：从 0° 到 90°，端点应落在 (20,0) 与 (0,10)
    var quarter = CadGeometry.BuildEllipticalArc(0, 0, 20, 0, 0, 10, 0, Math.PI / 2);
    Check(quarter is not null && MathF.Abs(quarter.Bounds.Right - 20f) < 0.01f
                              && MathF.Abs(quarter.Bounds.Bottom - 10f) < 0.01f,
          $"四分之一椭圆弧端点正确（包围盒 {quarter?.Bounds.Width:F2}×{quarter?.Bounds.Height:F2}，期望 20×10）");

    // 椭圆接线到真实实体：sample_features.dxf 的 ELLIPSE 仍是 20 × 0.5（跑 Feature 套件已覆盖）
}

/// <summary>路径 → 线段表（只取直线段，测试用图案线全是直线）。</summary>
static List<(SKPoint a, SKPoint b)> Segments(SKPath path){
    var segs = new List<(SKPoint, SKPoint)>();
    using var it = path.CreateRawIterator();
    var pts = new SKPoint[4];
    SKPoint cur = default;
    SKPathVerb v;
    while ((v = it.Next(pts)) != SKPathVerb.Done)
    {
        if (v == SKPathVerb.Move) cur = pts[0];
        else if (v == SKPathVerb.Line) { segs.Add((cur, pts[1])); cur = pts[1]; }
    }
    return segs;
}

/// <summary>
/// 填充样例 sample_hatch.dxf —— 走**文件**这条路（DXF 写出/读入 + 图层/颜色解析），
/// 与 <see cref="RunHatchChecks"/> 的内存构造互补：那边锁几何语义，这边锁"真实文件里也对"。
/// </summary>
void RunHatchFileSample(string path)
{
    Log();
    Log("=== 填充样例 sample_hatch.dxf ===");
    var list = CadDocumentLoader.Load(path);
    Log($"加载: {path}（{list.Entities.Count} 个实体）");

    var holes = new List<CadEntity>();
    var fills = new List<CadEntity>();
    var strokes = new List<CadEntity>();
    foreach (var e in list.Entities)
    {
        if (e.FillPath is not null) { fills.Add(e); holes.Add(e); }
        else if (e.Path is not null) strokes.Add(e);
    }

    Check(fills.Count == 2, $"实心填充 2 个（含孤岛的那个必须合并成 1 个，实际 {fills.Count}）");
    Check(strokes.Count == 1, $"图案填充 1 条路径（实际 {strokes.Count}）");

    // 带孤岛的实心填充：包围盒覆盖整块 100x100（0..100），洞里不能被覆盖
    var withHole = fills.FirstOrDefault(e => e.FillPath!.Bounds.Width > 90f && e.FillPath.Bounds.Left < 10f);
    Check(withHole is not null, "找到带孤岛的实心填充（包围盒 0..100）");
    if (withHole is not null)
    {
        var p = withHole.FillPath!;
        Check(p.FillType == SKPathFillType.EvenOdd, $"填充规则 EvenOdd（实际 {p.FillType}）");
        Check(p.Contains(50f, 50f) == false, "孤岛内部是洞（(50,50) 未填充）");
        Check(p.Contains(10f, 10f), "外环内部已填充（(10,10)）");
    }

    // 图案填充：洞内无线段，且洞外每行都命中
    if (strokes.Count == 1)
    {
        var segs = Segments(strokes[0].Path!);
        Check(segs.Count > 20, $"图案线数量合理（实际 {segs.Count} 段）");

        int inHole = 0;
        foreach (var (a, b) in segs)
        {
            // 取线段中点与两端：全在孤岛 (180..220, 30..70) 内即算穿洞
            float mx = (a.X + b.X) * 0.5f, my = (a.Y + b.Y) * 0.5f;
            if (mx > 181f && mx < 219f && my > 31f && my < 69f) inHole++;
        }
        Check(inHole == 0, $"图案线不落在孤岛内（洞内段数 {inHole}）");

        float xs = segs.Min(s => MathF.Min(s.a.X, s.b.X));
        Check(MathF.Abs(xs - 150f) < 1f, $"图案块左边界 150（实际 {xs:F2}）");
    }
}

/// <summary>
/// SHX 单线字体：解析 / 字形 / 子形 / 缺字库回退。
///
/// <para>**不依赖系统字库**。本机只装了 9 个 .shx，而且最常用的 simplex / hztxt / tssdeng
/// 都不在 <c>Windows\Fonts</c> 里；只有现场合成字库，才能把"字头定位、索引自校验、子形引用、
/// 缺字回退"这几条钉在断言里（真实字库只作人工对照，见 .compare/）。</para>
///
/// <para>合成字库刻意复刻了实测到的两个坑：
/// ① unifont 字形数据前面带 ASCII 名字（<c>"H\0"</c>）；
/// ② bigfont 每条记录是 <c>内码(2) + 00 07 8e 05 … 07 8f 00</c>，尾部的 0x8F00 是个**不存在的**
/// 子形引用 —— 按单字节读会命中 0x8F（一个图案块），每个汉字底下就多出一坨乱线。</para>
/// </summary>
void RunShxChecks()
{
    Log();
    Log("=== SHX 单线字体（合成字库） ===");

    string dir = Path.Combine(Path.GetTempPath(), "DrawingView_shx_" + Guid.NewGuid().ToString("N").Substring(0, 8));
    Directory.CreateDirectory(dir);

    try
    {
        string ansiPath = Path.Combine(dir, "TESTANSI.SHX");
        string bigPath = Path.Combine(dir, "TESTBIG.SHX");
        string dxfPath = Path.Combine(dir, "shx_probe.dxf");
        File.WriteAllBytes(ansiPath, BuildTestAnsiFont());
        File.WriteAllBytes(bigPath, BuildTestBigFont());
        File.WriteAllText(dxfPath, ShxProbeDxf(), new System.Text.UTF8Encoding(false));

        // ---- 1. unifont：字头 / 名字前缀 / 字形 / 前进量 / 字高换算 ----
        var ansi = ShxFont.TryLoad(ansiPath, out string? ansiErr);
        Check(ansi is not null, $"unifont 解析成功（{ansiErr ?? "ok"}）");
        if (ansi is not null)
        {
            Check(ansi.Kind == ShxFont.FontKind.Unifont, $"字体类型 unifont（实际 {ansi.Kind}）");
            Check(ansi.Name == "TESTANSI", $"字体名取自头部（实际 '{ansi.Name}'）");
            var l = ansi.GetGlyph('L');
            Check(l is not null, "字形 L 存在");
            if (l is not null)
            {
                Check(l.Polylines.Count == 1, $"L 是 1 段折线（实际 {l.Polylines.Count}）");
                Check(l.Polylines[0].Length == 3, $"L 有 3 个点（实际 {l.Polylines[0].Length}）");
                Check(MathF.Abs(l.Polylines[0][2].Y - 20f) < 0.01f, $"L 末点 y=20（实际 {l.Polylines[0][2].Y:F2}）");
                Check(MathF.Abs(l.Advance - 10f) < 0.01f, $"L 前进量 10（实际 {l.Advance:F2}）");
            }
            // 字高换算按实测上沿（H=20），不是头部 above（我们故意写成 30）
            Check(MathF.Abs(ansi.CapUnits - 20f) < 0.01f, $"字高换算 20 而不是 above=30（实际 {ansi.CapUnits:F2}）");
            Check(ansi.GetGlyph('Z') is null, "字库里没有 Z 时返回 null（不抛异常）");
        }

        // ---- 2. bigfont：索引自校验 / 双字节子形 / 空子形不能画 ----
        var big = ShxFont.TryLoad(bigPath, out string? bigErr);
        Check(big is not null, $"bigfont 解析成功（{bigErr ?? "ok"}）");
        if (big is not null)
        {
            Check(big.Kind == ShxFont.FontKind.BigFont, $"字体类型 bigfont（实际 {big.Kind}）");
            var blob = big.GetGlyphByCode(0x8F);
            Check(blob is not null && blob.Polylines.Count == 3, $"图案块 0x8F 自身可解析（{blob?.Polylines.Count ?? 0} 段）");
            var tian = big.GetGlyph('田');
            Check(tian is not null, "汉字 田 存在（按 GBK 内码索引）");
            if (tian is not null)
            {
                Check(tian.Polylines.Count == 2,
                      $"田 只画出自己的 2 段，尾部 07 8F 00 不当子形（实际 {tian.Polylines.Count} 段）");
                Check(MathF.Abs(tian.Advance - 40f) < 0.01f, $"田 结束点 x=40（两笔累计，实际 {tian.Advance:F2}）");
            }
        }

        // ---- 3. TTF 名字不该当成"缺 SHX 字库" ----
        Check(!ShxFontLibrary.IsShxName("ARIAL.TTF"), "ARIAL.TTF 不算 SHX 字体名");
        Check(!ShxFontLibrary.IsShxName("simhei.ttf"), "simhei.ttf 不算 SHX 字体名");
        Check(ShxFontLibrary.IsShxName("simplex.shx"), "simplex.shx 是 SHX 字体名");
        Check(ShxFontLibrary.IsShxName("txt"), "无扩展名的 txt 按 SHX 处理（AutoCAD 老习惯）");
        Check(ShxFontLibrary.Resolve("ARIAL.TTF") is null, "Resolve 不会去找 ARIAL.TTF.shx");

        // ---- 4. 接进管线：能描线、能定宽、缺字库回退 TTF ----
        var msgs = new List<string>();
        var list = CadDocumentLoader.Load(dxfPath, new Progress<string>(m => msgs.Add(m)));
        int strokes = list.Entities.Count(e => e.TextStrokes);
        int ttf = list.Entities.Count(e => e.Text is not null);
        Check(strokes == 1, $"只有 SHX 那条被描成笔画（实际 {strokes}）");
        Check(ttf == 1, $"缺字库那条回退 TTF（实际 {ttf} 条 TTF 文字）");
        string shxMsg = msgs.FirstOrDefault(m => m.Contains("SHX 文字")) ?? string.Empty;
        Check(shxMsg.Contains("1 个实体"), $"状态行报告 1 个实体按字库描线（实际：{shxMsg}）");
        Check(shxMsg.Contains("1 条文字缺字库"), $"状态行报告 1 条缺字库（实际：{shxMsg}）");

        var stroke = list.Entities.FirstOrDefault(e => e.TextStrokes);
        if (stroke is not null)
        {
            // 字高 100、字库里 L 高 20 单位、宽 10 单位 → 缩放 5 倍 → 50 × 100
            var b = stroke.Bounds;
            Log($"  描线包围盒 x[{b.Left:F2},{b.Right:F2}] y[{b.Bottom:F2},{b.Top:F2}]");
            Check(MathF.Abs(b.Width - 50f) < 1f, $"L 宽 50（实际 {b.Width:F2}）");
            Check(MathF.Abs(b.Height - 100f) < 1f, $"L 高 100（实际 {b.Height:F2}）");
            Check(MathF.Abs(b.Left) < 1f, $"L 起点 x=0（实际 {b.Left:F2}）");
        }
    }
    finally
    {
        try { Directory.Delete(dir, true); } catch { /* 临时目录清不掉不影响断言结果 */ }
    }
}

/// <summary>合成一个最小 unifont：'H'（竖，高 20）与 'L'（横+竖，宽 10 高 20）。</summary>
static byte[] BuildTestAnsiFont()
{
    var ms = new MemoryStream();
    void Raw(params byte[] b) => ms.Write(b, 0, b.Length);
    void U16(int v) { ms.WriteByte((byte)(v & 0xFF)); ms.WriteByte((byte)((v >> 8) & 0xFF)); }
    void U32(int v) { for (int k = 0; k < 4; k++) ms.WriteByte((byte)((v >> (8 * k)) & 0xFF)); }

    Raw(System.Text.Encoding.ASCII.GetBytes("AutoCAD-86 unifont 1"));   // 头部以 0x0D 结尾，不能用 index(0)
    Raw(0x0D, 0x0A, 0x1A);
    U32(3);                       // count = 记录数 + 1
    U16(0);                       // 索引区字节数（解析不用）
    Raw(System.Text.Encoding.ASCII.GetBytes("TESTANSI"));
    Raw(0x00);
    Raw(30, 5, 0, 0, 0, 0);       // above=30（故意与真实字高 20 不同）/ below / modes / enc / emb / pad

    // 每条记录：内码 u16（低字节在前）| 数据字节数 u16 | 数据（前面带 ASCII 名字 + 0x00）
    byte[] h = { (byte)'H', 0x00, 0x02, 0x08, 0x00, 0x00, 0x01, 0x08, 0x00, 0x14, 0x00 };
    U16('H'); U16(h.Length); Raw(h);
    byte[] l = { (byte)'L', 0x00, 0x02, 0x08, 0x00, 0x00, 0x01, 0x08, 0x0A, 0x00, 0x08, 0x00, 0x14, 0x00 };
    U16('L'); U16(l.Length); Raw(l);
    return ms.ToArray();
}

/// <summary>
/// 合成一个最小 bigfont：0xCCEF（田，两笔 + 尾部空子形 07 8F 00）与 0x008F（图案块，三笔）。
/// 尾部那个 0x8F00 在字库里不存在 —— 这正是"退回单字节就读成图案块"的回归点。
/// </summary>
static byte[] BuildTestBigFont()
{
    var tian = new List<byte> { 0xCC, 0xEF, 0x00, 0x07, 0x8E, 0x05 };        // 6 字节固定前缀
    tian.AddRange(new byte[] { 0x02, 0x08, 0x00, 0x00, 0x01, 0x08, 0x14, 0x00 });   // 笔 1：(0,0)→(20,0)
    tian.AddRange(new byte[] { 0x02, 0x08, 0x00, 0x28, 0x01, 0x08, 0x14, 0x00 });   // 笔 2：(0,40)→(20,40)
    tian.AddRange(new byte[] { 0x07, 0x8F, 0x00 });                          // 空子形引用

    var blob = new List<byte>();
    for (int k = 0; k < 3; k++)
        blob.AddRange(new byte[] { 0x02, 0x08, 0x00, 0x00, 0x01, 0x08, 0x05, 0x00 });
    blob.Add(0x00);

    int header = "AutoCAD-86 bigfont 1".Length + 3;   // 头 + 0D 0A 1A
    int indexEnd = header + 2 + 2 + 2 + 4 + 2 * 8;    // 前导 + count + nChange + 1 条变更区间 + 2 条索引
    int offTian = indexEnd;
    int offBlob = offTian + tian.Count;

    var ms = new MemoryStream();
    void Raw(params byte[] b) => ms.Write(b, 0, b.Length);
    void U16(int v) { ms.WriteByte((byte)(v & 0xFF)); ms.WriteByte((byte)((v >> 8) & 0xFF)); }
    void U32(int v) { for (int k = 0; k < 4; k++) ms.WriteByte((byte)((v >> (8 * k)) & 0xFF)); }

    Raw(System.Text.Encoding.ASCII.GetBytes("AutoCAD-86 bigfont 1"));
    Raw(0x0D, 0x0A, 0x1A);
    U16(8);            // 前导（实测恒 8，用途未知）
    U16(2);            // count：两条索引记录
    U16(1);            // nChange
    U16(0x0000); U16(0xFFFF);
    // 索引：内码 u16（低字节在前）| 字节数 u16 | 文件内偏移 u32
    U16(0xCCEF); U16(tian.Count); U32(offTian);
    U16(0x008F); U16(blob.Count); U32(offBlob);
    foreach (byte b in tian) ms.WriteByte(b);
    foreach (byte b in blob) ms.WriteByte(b);
    return ms.ToArray();
}

/// <summary>两条 TEXT：一条用合成 SHX 样式，一条指向不存在的字库（验证回退）。</summary>
static string ShxProbeDxf()
{
    string Style(string name, string font) =>
        $"0\nSTYLE\n2\n{name}\n70\n0\n40\n0.0\n41\n1.0\n50\n0.0\n71\n0\n42\n2.5\n3\n{font}\n4\n\n";
    string Text(string style, double y) =>
        $"0\nTEXT\n8\n0\n10\n0.0\n20\n{y}\n30\n0.0\n40\n100.0\n1\nL\n50\n0.0\n41\n1.0\n51\n0.0\n7\n{style}\n";

    return "0\nSECTION\n2\nHEADER\n9\n$ACADVER\n1\nAC1009\n"
         + "9\n$EXTMIN\n10\n0.0\n20\n0.0\n30\n0.0\n"
         + "9\n$EXTMAX\n10\n1000.0\n20\n1000.0\n30\n0.0\n0\nENDSEC\n"
         + "0\nSECTION\n2\nTABLES\n"
         + "0\nTABLE\n2\nLAYER\n70\n1\n0\nLAYER\n2\n0\n70\n0\n62\n7\n6\nCONTINUOUS\n0\nENDTAB\n"
         + "0\nTABLE\n2\nSTYLE\n70\n2\n"
         + Style("SHXOK", "TESTANSI.SHX")
         + Style("SHXMISS", "NOSUCHFONT.SHX")
         + "0\nENDTAB\n0\nENDSEC\n"
         + "0\nSECTION\n2\nENTITIES\n"
         + Text("SHXOK", 0.0)
         + Text("SHXMISS", 300.0)
         + "0\nENDSEC\n0\nEOF\n";
}

/// <summary>打印全部实体的包围盒中心/尺寸/颜色/图层（核对断言用）。</summary>
void DumpEntities(DisplayList list)
{
    Log("--- 实体清单 ---");
    Log("   中心(x,y)          尺寸(w,h)        颜色      图层      类型");
    foreach (var e in list.Entities)
    {
        var b = e.Bounds;
        string kind = e.Text is not null ? "TEXT" : e.FillPath is not null ? "FILL" : "STROKE";
        Log($"  ({b.MidX,9:F2},{b.MidY,9:F2})  ({b.Width,8:F2},{b.Height,8:F2})  {e.ColorArgb:X8}  {e.Layer,-8}  {kind}");
    }
    Log("--- 清单结束 ---");
}

/// <summary>按"包围盒中心 + 尺寸区间"判断存在某实体（几何均为整数坐标，容差很小即可）。</summary>
/// <summary>该位置是否有 POINT 点标记（点是渲染期生成的，没有几何尺寸可比，只能认位置）。</summary>
static bool HasPoint(DisplayList list, float cx, float cy)
    => list.Entities.Any(e => e.Mark is { } m
                              && MathF.Abs(m.Position.X - cx) <= 1f
                              && MathF.Abs(m.Position.Y - cy) <= 1f);

static bool HasEntity(DisplayList list, float cx, float cy, float minSize, float maxSize)
{
    foreach (var e in list.Entities)
    {
        var b = e.Bounds;
        if (MathF.Abs(b.MidX - cx) > 1f) continue;
        if (MathF.Abs(b.MidY - cy) > 1f) continue;
        float size = MathF.Max(b.Width, b.Height);
        if (size >= minSize && size <= maxSize) return true;
    }
    return false;
}

/// <summary>从输出目录向上找 samples/，避免写死盘符。</summary>
static string? FindSample(string fileName)
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
