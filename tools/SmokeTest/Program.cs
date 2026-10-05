using CadViewer.Core;
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
// 日志路径：环境变量 CADVIEWER_SMOKE_LOG，默认 %TEMP%\cadviewer_smoke.log

var logPath = Environment.GetEnvironmentVariable("CADVIEWER_SMOKE_LOG")
              ?? Path.Combine(Path.GetTempPath(), "cadviewer_smoke.log");
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
    if (Path.GetFileName(explicitPath).Contains("feature", StringComparison.OrdinalIgnoreCase))
        RunFeatureSample(explicitPath);
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
    Check(HasEntity(list, 100, 200, 1.9f, 2.1f), "POINT 十字标记");

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

    // ---- 诊断导出（设 CADVIEWER_SMOKE_DUMP=1 可打印全部实体，便于核对断言） ----
    if (Environment.GetEnvironmentVariable("CADVIEWER_SMOKE_DUMP") == "1") DumpEntities(list);

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

// ---------------------------------------------------------------- 工具

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
