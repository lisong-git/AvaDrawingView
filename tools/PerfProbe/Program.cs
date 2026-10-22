using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using DrawingView.Controls;
using DrawingView.Core;
using SkiaSharp;

// 大图性能基准探针：把"卡顿"拆成可测量的分段，先定位瓶颈再优化。
//   阶段 A：解析 → 几何转换 → 空间索引（各自耗时）
//   阶段 B：内存占用
//   阶段 C：逐缩放级别的每帧渲染耗时（静态重绘 / 平移）
// 结果写 Console + 日志文件（本机控制台重定向偶发丢输出）。
// 日志路径：环境变量 DrawingView_PERF_LOG，默认 %TEMP%\DrawingView_perf.log

var logPath = Environment.GetEnvironmentVariable("DrawingView_PERF_LOG")
              ?? Path.Combine(Path.GetTempPath(), "DrawingView_perf.log");
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
    var loadOnly = argv.Contains("--load-only");

    // --fonts <dir>：统计图集里"文本实体实际用到哪些字体文件"（按实体数加权）。
    // 上一次普查只统计了"SHX 样式占 54.9%"，但没回答"具体是哪些 .shx"——
    // 而 SHX 实现的边界完全由被引用的字体文件决定（simplex 与 gbcbig 是两套编码）。
    int fontsIdx = Array.IndexOf(argv, "--fonts");
    if (fontsIdx >= 0 && fontsIdx + 1 < argv.Length)
    {
        int limit = 0;
        int limitIdx = Array.IndexOf(argv, "--limit");
        if (limitIdx >= 0 && limitIdx + 1 < argv.Length) int.TryParse(argv[limitIdx + 1], out limit);
        int minKb = 0;
        int minKbIdx = Array.IndexOf(argv, "--min-kb");
        if (minKbIdx >= 0 && minKbIdx + 1 < argv.Length) int.TryParse(argv[minKbIdx + 1], out minKb);
        return FontStat(argv[fontsIdx + 1], limit, minKb);
    }

    // --survey <dir>：批量普查（不看渲染，只回答"图集里到底有什么实体/字体"）
    // 用途：决定 3DSOLID/BODY/SURFACE 值不值得实现、SHX 字体占比多少 —— 先用数据说话，
    // 不要凭"感觉国内图纸都用 SHX"去排优先级。
    int surveyIdx = Array.IndexOf(argv, "--survey");
    if (surveyIdx >= 0 && surveyIdx + 1 < argv.Length)
    {
        int limit = 0;
        int limitIdx = Array.IndexOf(argv, "--limit");
        if (limitIdx >= 0 && limitIdx + 1 < argv.Length) int.TryParse(argv[limitIdx + 1], out limit);
        int minKb = 0;
        int minKbIdx = Array.IndexOf(argv, "--min-kb");
        if (minKbIdx >= 0 && minKbIdx + 1 < argv.Length) int.TryParse(argv[minKbIdx + 1], out minKb);
        string csv = Path.Combine(Path.GetTempPath(), "DrawingView_survey.csv");
        int csvIdx = Array.IndexOf(argv, "--csv");
        if (csvIdx >= 0 && csvIdx + 1 < argv.Length) csv = argv[csvIdx + 1];
        return SurveyCorpus(argv[surveyIdx + 1], limit, minKb, csv);
    }

    // --repeat N：连续打开同一图纸 N 次，观察内存是否随次数线性增长（验证 SKPath 释放）
    int repeatCount = 0;
    int repeatIdx = Array.IndexOf(argv, "--repeat");
    if (repeatIdx >= 0 && repeatIdx + 1 < argv.Length) int.TryParse(argv[repeatIdx + 1], out repeatCount);

    int dumpSabIdx = Array.IndexOf(argv, "--dump-sab");
    var path = argv
        .Select((a, i) => (arg: a, idx: i))
        .Where(t => !t.arg.StartsWith("--") && t.idx != repeatIdx + 1 && t.idx != dumpSabIdx + 1)
        .Select(t => t.arg)
        .FirstOrDefault() ?? @"D:\Downloads\测试图.dwg";
    if (!File.Exists(path)) { Log($"FAIL: 找不到 {path}"); return 1; }

    var fileSize = new FileInfo(path).Length;
    DisplayList.DisposalDelayMs = 0;
    Log($"=== PerfProbe ===");
    Log($"文件: {path}");
    Log($"大小: {fileSize / 1024.0 / 1024.0:F2} MB");
    Log($"模式: {(loadOnly ? "仅加载" : "加载 + 渲染")}");
    Log($"机器: {Environment.ProcessorCount} 核, .NET {Environment.Version}");

    // ---------- 阶段 A：加载各分段耗时 ----------
    var rec = new StageRecorder();
    var total = Stopwatch.StartNew();
    var list = CadDocumentLoader.Load(path, rec);
    total.Stop();

    Log("");
    Log("--- 阶段 A：加载分段 ---");
    double prev = 0;
    foreach (var (stage, ms) in rec.Stages)
    {
        Log($"{(ms - prev) / 1000.0,8:F2} s   {stage}");
        prev = ms;
    }
    Log($"{(total.ElapsedMilliseconds) / 1000.0,8:F2} s   合计（含未上报开销）");

    Log("");
    Log("--- 阶段 A 结果 ---");
    Log($"实体数:        {list.Entities.Count}");
    Log($"图层数:        {list.LayerNames.Count}");
    Log($"范围:          L={list.Extents.Left:F1} T={list.Extents.Top:F1} R={list.Extents.Right:F1} B={list.Extents.Bottom:F1}");
    Log($"范围尺寸:      {list.Extents.Width:F1} x {list.Extents.Height:F1} 世界单位");
    Log($"是否为空:      {list.Entities.Count == 0}");

    if (argv.Contains("--points"))
    {
        var marks = list.Entities.Where(e => e.Mark is not null).ToList();
        Log($"点标记明细（共 {marks.Count} 个，前 12 条）：");
        foreach (var m in marks.Take(12))
            Log($"   layer={m.Layer} handle=0x{m.Handle:X} own=0x{m.OwnHandle:X} "
                + $"at=({m.Mark!.Position.X:F2},{m.Mark.Position.Y:F2}) mode={m.Mark.Mode} size={m.Mark.SizeSpec}");
        Log($"   分层分布：{string.Join(", ", marks.GroupBy(m => m.Layer).OrderByDescending(g => g.Count()).Take(8).Select(g => $"{g.Key}={g.Count()}"))}");
        var texts = list.Entities.Count(e => e.Text is not null);
        Log($"   文本实体 {texts} 个；点/文本 = {(texts > 0 ? (double)marks.Count / texts : 0):F2}");
    }

    if (argv.Contains("--types"))
        DumpEntityTypes(path);

    // 视口裁剪有效性：按文件大小估算全图查询规模
    var allHit = list.Index.Query(list.Extents);
    Log($"全图查询命中:  {allHit.Count}");

    // ---------- 烘焙批次（大图才启用） ----------
    Log("");
    Log("--- 阶段 A2：几何烘焙 ---");
    var baked = list.Baked;
    if (baked is null)
    {
        Log("未启用（实体数低于阈值，逐实体绘制已达标）");
    }
    else
    {
        Log($"总批次数:      {baked.BucketCount}（{baked.SourceEntityCount} 实体 → 压缩 {baked.SourceEntityCount / Math.Max(1, baked.BucketCount)} 倍）");
        Log($"烘焙耗时:      {baked.BakeMs:F0} ms");
        Log($"线宽种类:      {baked.Classes.SelectMany(c => c.Buckets).Select(b => b.LineWidthMm100).Distinct().Count()}（每批只用一个描边宽度，见 BakedBucket.LineWidthMm100）");
        Log($"不合并实体:    {baked.UnbatchedEntities.Count}（文本 + 点标记，屏幕空间绘制）");
        Log($"尺寸档数:      {baked.Classes.Count}");
        Log("档位世界尺寸区间          实体数    批次数    路径点数");
        foreach (var c in baked.Classes)
        {
            string hi = float.IsPositiveInfinity(c.MaxWorldSize) || c.MaxWorldSize > 1e30f
                ? "∞" : $"{c.MaxWorldSize:N0}";
            Log($"{c.MinWorldSize,-8:N0} - {hi,-10}  {c.EntityCount,8}  {c.Buckets.Count,8}  {c.PointCount,10:N0}");
        }
    }

    // ---------- 阶段 B：内存 ----------
    // 必须先强制完整回收：SKPath 是原生资源，靠终结器释放，
    // 不强制 GC 时测到的是"垃圾 + 未回收原生内存"，两次运行能差 160MB，毫无可比性。
    Log("");
    Log("--- 阶段 B：内存（已强制 GC.Collect + WaitForPendingFinalizers） ---");
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    long managed = GC.GetTotalMemory(true);
    Log($"托管堆:        {managed / 1024.0 / 1024.0:F1} MB");
    using var proc = Process.GetCurrentProcess();
    Log($"进程工作集:    {proc.WorkingSet64 / 1024.0 / 1024.0:F1} MB");
    Log($"进程私有:      {proc.PrivateMemorySize64 / 1024.0 / 1024.0:F1} MB");
    Log($"GC 次数:       gen0={GC.CollectionCount(0)} gen1={GC.CollectionCount(1)} gen2={GC.CollectionCount(2)}");
    Log($"每实体托管均值: {managed / Math.Max(1.0, list.Entities.Count):F0} B");

    // ---------- 实体尺寸分布（决定 LOD 能剔掉多少几何） ----------
    Log("");
    Log("--- 阶段 A3：实体世界尺寸分布 ---");
    PrintSizeDistribution(list);

    if (loadOnly) { Log(""); Log("(仅加载模式，跳过渲染测量)"); return 0; }

    // ---------- 泄漏验证：连续开图 N 次 ----------
    // 每张图的几何都是一批 SKPath（原生内存）。修复前换图不释放旧图，
    // 工作集会随打开次数线性增长；这里就是它的量化证据。
    if (repeatCount > 0)
    {
        Log("");
        Log($"--- 泄漏验证：连续打开同一图纸 {repeatCount} 次 ---");
        AppBuilder.Configure<DrawingView.App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia()
            .SetupWithoutStarting();

        var memView = new DrawingView.Controls.DrawingView();
        var memWindow = new Window { Width = 1200, Height = 800, Content = memView };
        memWindow.Show();

        Log("次数   工作集MB    私有MB    托管MB   已释放DisplayList");
        for (int i = 0; i < repeatCount; i++)
        {
            var loaded = CadDocumentLoader.Load(path);
            memView.SetDisplay(loaded);
            memWindow.CaptureRenderedFrame();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            using var p = Process.GetCurrentProcess();
            Log($"{i + 1,4}   {p.WorkingSet64 / 1024.0 / 1024.0,9:F1}  {p.PrivateMemorySize64 / 1024.0 / 1024.0,8:F1}  "
                + $"{GC.GetTotalMemory(true) / 1024.0 / 1024.0,7:F1}   {DisplayList.DisposedCount,10}");
        }
        Log("--- 泄漏验证结束（工作集应趋于平稳而非线性增长） ---");
        return 0;
    }

    // ---------- 阶段 C：渲染 ----------
    Log("");
    Log("--- 阶段 C：渲染 ---");
    Log("初始化无头 Avalonia（Skia 真实绘制）…");

    AppBuilder.Configure<DrawingView.App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .UseSkia()
        .SetupWithoutStarting();

    const int W = 1600, H = 900;
    var view = new DrawingView.Controls.DrawingView();
    var window = new Window { Width = W, Height = H, Content = view };
    window.Show();
    Log($"控件尺寸: {view.Bounds.Width:F0} x {view.Bounds.Height:F0}");

    view.SetDisplay(list);
    var firstFrame = Stopwatch.StartNew();
    window.CaptureRenderedFrame();
    firstFrame.Stop();
    Log($"首帧耗时:      {firstFrame.Elapsed.TotalMilliseconds:F0} ms（含首次路径光栅化）");

    view.FitToContent();
    double fitZoom = view.Viewport.Zoom;
    Log($"适应窗口缩放:  {fitZoom:F6}");
    Log("");

    // 逐缩放级别测量静态重绘
    Log("缩放级别    可见实体   帧数    墙钟中位  绘制中位   p95ms   最快ms   最慢ms   FPS(墙钟)");
    foreach (var mult in new[] { 1.0, 2.0, 4.0, 8.0, 16.0 })
    {
        view.FitToContent();
        if (mult != 1.0)
            view.Viewport.ZoomAt((float)mult, new SKPoint(W / 2f, H / 2f));

        int visible = CountVisible(list, view, W, H);
        DrawingView.Controls.DrawingView.ResetPassStats();
        var ms = MeasureFrames(window, view, 12);

        Log($"{mult,6:F0}x   {visible,8}   {ms.Count,4}   {ms.Median,8:F1}  {ms.DrawMedian,8:F1} {ms.P95,7:F1} {ms.Min,7:F1} {ms.Max,7:F1}   {1000.0 / Math.Max(ms.Median, 0.001),7:F1}");

        // 分段拆解：批次 / 文本 / 点标记 / 高亮 各占多少（DrawingView_RENDER_SPLIT=1 时才有数）
        if (DrawingView.Controls.DrawingView.RenderSplitEnabled)
        {
            var (bk, tx, pt, se) = DrawingView.Controls.DrawingView.PassMsPerFrame(ms.Count);
            var (bs, bf) = DrawingView.Controls.DrawingView.BucketDetailMsPerFrame(ms.Count);
            Log($"        └ 批次 {bk:F2}ms（描边 {bs:F2} + 填充 {bf:F2}）/ 文本 {tx:F2}ms / 点标记 {pt:F2}ms / 高亮 {se:F2}ms"
                + $" / 其余 {Math.Max(0, ms.DrawMedian - bk - tx - pt - se):F2}ms"
                + $" / 点标记 实画 {DrawingView.Controls.DrawingView.MarksDrawn}"
                + $" 隐藏层跳过 {DrawingView.Controls.DrawingView.MarksHiddenLayer}");
        }
    }

    // 平移场景（卡顿的直接来源：每次 pointer move 都重绘）
    Log("");
    Log("--- 平移帧（模拟拖拽，每帧位移 8px） ---");
    view.FitToContent();
    view.Viewport.ZoomAt(4f, new SKPoint(W / 2f, H / 2f));
    var panMs = MeasurePanFrames(window, view, 12, 8f);
    Log($"平移 {panMs.Count} 帧: 中位 {panMs.Median:F1} ms / p95 {panMs.P95:F1} ms / 最慢 {panMs.Max:F1} ms → 中位 FPS {1000.0 / Math.Max(panMs.Median, 0.001):F1}");

    // ---------- LOD 阈值扫描（对应 ReadCAD 的 MinEntityPixelSize） ----------
    var pngDir = Path.Combine(Path.GetTempPath(), "DrawingView_perf_png");
    Log("");
    Log("--- LOD 阈值扫描（MinEntityPixelSize，耗时均为 12 帧中位） ---");
    Log($"对比图输出: {pngDir}");
    Log("阈值px   1x绘制   2x绘制   4x绘制   8x绘制   1x覆盖率  2x覆盖率   1x FPS");
    foreach (var lod in new[] { 0f, 1.0f, 1.5f, 3.0f })
    {
        view.MinEntityPixelSize = lod;
        var cells = new List<string>();
        double first = 0, ink1x = 0, ink2x = 0;
        foreach (var mult in new[] { 1.0, 2.0, 4.0, 8.0 })
        {
            view.FitToContent();
            if (mult != 1.0)
                view.Viewport.ZoomAt((float)mult, new SKPoint(W / 2f, H / 2f));
            var ms = MeasureFrames(window, view, 12);
            if (mult == 1.0) first = ms.Median;
            cells.Add($"{ms.DrawMedian,7:F1}");

            if (mult <= 2.0)
            {
                var png = Path.Combine(pngDir, $"lod{lod:F1}_{mult:F0}x.png");
                var (ink, nonWhite, _) = CaptureAndAnalyze(window, png);
                Log($"         └ {mult:F0}x 墨迹覆盖 {ink:F2}%（{nonWhite:N0} px），已存 {Path.GetFileName(png)}");
                if (mult == 1.0) ink1x = ink; else ink2x = ink;
            }
        }
        Log($"{lod,5:F1}   {string.Join("  ", cells)}   {ink1x,8:F2}%  {ink2x,7:F2}%   {1000.0 / Math.Max(first, 0.001),7:F1}");
    }
    view.MinEntityPixelSize = 1.5f;

    Log("");
    Log("--- 阶段 C 结束 ---");
    return 0;

    // ---------------------------------------------------------------- 诊断：实体类型与 ACIS
    /// <summary>
    /// 实体类型直方图 + REGION/3DSOLID 一类「模型器几何」的 ACIS 数据概览。
    ///
    /// <para>为什么要这个：这类实体在当前渲染器里完全不画（P2-7），排查第一步是搞清楚
    /// 图纸里到底有多少、ACadSharp 有没有把 ACIS 载荷读出来、载荷是 SAT 文本还是 SAB 二进制
    /// —— 三者决定后续是「解析 SAT 画真实轮廓」「扫 SAB 拿顶点」还是「只能降级虚线框」。</para>
    /// </summary>
    void DumpEntityTypes(string file)
    {
        ACadSharp.CadDocument doc;
        var ext = Path.GetExtension(file).ToLowerInvariant();
        if (ext == ".dwg") { using var r = new ACadSharp.IO.DwgReader(file); doc = r.Read(); }
        else { using var r = new ACadSharp.IO.DxfReader(file); doc = r.Read(); }

        var counts = new Dictionary<string, int>();
        void Tally(IEnumerable<ACadSharp.Entities.Entity> ents)
        {
            foreach (var e in ents)
            {
                var n = e.GetType().Name;
                counts[n] = counts.GetValueOrDefault(n) + 1;
            }
        }

        var blockEnts = doc.BlockRecords.SelectMany(b => b.Entities).ToList();
        Tally(doc.Entities);
        Tally(blockEnts);

        Log("");
        Log($"--- 诊断：实体类型（模型空间 {doc.Entities.Count()} + 块定义 {blockEnts.Count}）---");
        foreach (var kv in counts.OrderByDescending(k => k.Value).Take(30))
            Log($"   {kv.Key,-26} {kv.Value,8:N0}");

        var modelers = doc.Entities.Concat(blockEnts)
                          .OfType<ACadSharp.Entities.ModelerGeometry>().ToList();
        int withData = modelers.Count(g => g.AcisData is { Length: > 0 });
        int binary = modelers.Count(g => g.AcisData is { Length: > 0 } && g.IsBinaryAcisData);
        int withText = modelers.Count(g => g.GetAcisText() is { Length: > 0 });
        Log($"--- 模型器几何（REGION / 3DSOLID / BODY / SURFACE）：{modelers.Count} 条 ---");
        Log($"   ACIS 非空 {withData}，其中 SAB 二进制 {binary}，可取 SAT 文本 {withText}");

        var first = modelers.FirstOrDefault(g => g.GetAcisText() is { Length: > 0 });
        if (first is not null)
        {
            var text = first.GetAcisText()!;
            Log($"   首条 SAT 长度 {text.Length} 字符（前 24 行，注意记录类型与字段顺序）：");
            foreach (var line in text.Replace("\r\n", "\n").Split('\n').Take(24))
                Log($"      | {line.TrimEnd()}");
        }
        else
        {
            var sab = modelers.FirstOrDefault(g => g.AcisData is { Length: > 0 })?.AcisData;
            if (sab is not null)
            {
                Log($"   首条 SAB {sab.Length} 字节，ASCII 片段（判断有哪些记录标签）：");
                var sb = new System.Text.StringBuilder();
                foreach (var b in sab)
                    sb.Append(b >= 32 && b < 127 ? (char)b : '.');
                var ascii = sb.ToString();
                var tags = System.Text.RegularExpressions.Regex.Matches(ascii, "[a-z_]{3,24}")
                                    .Select(m => m.Value).Where(s => s.Length >= 3).ToList();
                Log($"      {string.Join(" ", tags.Take(60))}");
                var recs = AcisSab.Parse(sab);
                if (recs is null) { Log("   SAB 解析失败（未知标记或截断）"); return; }
                var byName = recs.GroupBy(r => r.TypeName).OrderByDescending(g => g.Count()).Take(14)
                                 .Select(g => $"{g.Key}={g.Count()}");
                Log($"   解析成功：{recs.Count} 条记录 —— {string.Join(", ", byName)}");
                // --dump-sab <path>：把首条模型器实体的 ACIS 载荷存成样例（回归用例用真数据最靠谱）
                int sabOutIdx = argv.ToList().IndexOf("--dump-sab");
                if (sabOutIdx >= 0 && sabOutIdx + 1 < argv.Length)
                {
                    var dumper = modelers.FirstOrDefault(g => g.AcisData is { Length: > 0 });
                    if (dumper is not null)
                    {
                        File.WriteAllBytes(argv[sabOutIdx + 1], dumper.AcisData!);
                        Log($"   已导出 ACIS 载荷 → {argv[sabOutIdx + 1]}（{dumper.AcisData!.Length} 字节）");
                    }
                }

                Log($"   ---- RegionOutline 重建 ----");
                int okReg = 0, failReg = 0, paths = 0, pts = 0;
                var reasons = new Dictionary<string, int>();
                foreach (var g in modelers)
                {
                    var loops = RegionOutline.TryBuild(g.AcisData, out var why);
                    if (loops is null)
                    {
                        failReg++;
                        reasons[why ?? "?"] = reasons.GetValueOrDefault(why ?? "?") + 1;
                        continue;
                    }
                    okReg++; paths += loops.Count;
                    foreach (var lp in loops) pts += lp.PointCount;
                    if (okReg <= 3)
                    {
                        var b = loops[0].Bounds;
                        Log($"   REGION#{okReg}: {loops.Count} 环, 首环 {loops[0].PointCount} 点, "
                            + $"bbox {b.Left:F2},{b.Top:F2} → {b.Right:F2},{b.Bottom:F2}");
                    }
                }
                // 失败个例解剖：第一条失败的 REGION 的 loop/coedge/edge 记录长什么样
                var bad = modelers.FirstOrDefault(g => RegionOutline.TryBuild(g.AcisData, out _) is null);
                var badRecs = bad is null ? null : AcisSab.Parse(bad.AcisData);
                if (badRecs is not null)
                {
                    Log($"   --- 失败个例解剖（{badRecs.Count} 条记录）---");
                    foreach (var (r, idx) in badRecs.Select((r, i) => (r, i))
                             .Where(t => t.r.TypeName is "loop" or "coedge" or "edge" or "vertex" or "point")
                             .Take(16))
                    {
                        var parts = r.Values.Select(v => v.Kind == AcisKind.Pointer
                            ? $"@{v.I}{{( {(v.I >= 0 && v.I < badRecs.Count ? badRecs[(int)v.I].TypeName : "?")} )}}"
                            : v.ToString());
                        Log($"      [{idx,3}] {r.TypeName,-8}: {string.Join(" ", parts)}");
                    }
                }

                Log($"   成功 {okReg} / 失败 {failReg}，共 {paths} 环 {pts} 点"
                    + (reasons.Count > 0 ? $"，失败原因：{string.Join(", ", reasons.Select(kv => $"{kv.Key}={kv.Value}"))}" : ""));

                Log($"   记录表（索引: 名字: 属性；指针后注明目标类型）：");
                for (int ri = 0; ri < Math.Min(14, recs.Count); ri++)
                {
                    var r = recs[ri];
                    var parts = new List<string>();
                    foreach (var v in r.Values)
                    {
                        if (v.Kind == AcisKind.Pointer)
                        {
                            var tgt = v.I >= 0 && v.I < recs.Count ? recs[(int)v.I].TypeName : "?";
                            parts.Add($"@{v.I}{{{tgt}}}");
                        }
                        else parts.Add(v.ToString());
                    }
                    Log($"      [{ri,3}] {r.TypeName,-9}: {string.Join(" ", parts)}");
                }
            }
        }
    }

}

/// <summary>
/// 按世界尺寸分档统计实体数与路径点数。
/// 用途：估算 LOD（按屏幕像素尺寸剔除亚像素几何）在不同缩放下的可剔除比例。
/// 屏幕尺寸 = 世界尺寸 × zoom，故已知各档比例即可推算任一缩放下的剩余几何量。
/// </summary>
void PrintSizeDistribution(DisplayList list)
{
    // 档位边界（世界单位），按 4 倍递增
    double[] edges = { 1, 4, 16, 64, 256, 1024, 4096, 16384, 65536, 262144, 1048576, double.MaxValue };
    var entCount = new int[edges.Length];
    var ptCount = new long[edges.Length];
    long totalPts = 0;

    foreach (var e in list.Entities)
    {
        double s = Math.Max(e.Bounds.Width, e.Bounds.Height);
        if (double.IsNaN(s) || double.IsInfinity(s)) s = 0;
        int bin = Array.FindIndex(edges, x => s < x);
        if (bin < 0) bin = edges.Length - 1;
        entCount[bin]++;
        int pts = (e.Path?.PointCount ?? 0) + (e.FillPath?.PointCount ?? 0);
        ptCount[bin] += pts;
        totalPts += pts;
    }

    Log($"路径点总数:    {totalPts:N0}（直接决定光栅化成本）");
    Log("世界尺寸区间            实体数    占比    路径点数    点数占比");
    for (int i = 0; i < edges.Length; i++)
    {
        if (entCount[i] == 0 && ptCount[i] == 0) continue;
        string lo = i == 0 ? "0" : $"{edges[i - 1]:N0}";
        string hi = edges[i] == double.MaxValue ? "∞" : $"{edges[i]:N0}";
        Log($"{lo,-10} - {hi,-10}  {entCount[i],8}  {100.0 * entCount[i] / Math.Max(1, list.Entities.Count),5:F1}%  {ptCount[i],10:N0}  {100.0 * ptCount[i] / Math.Max(1, totalPts),7:F1}%");
    }
}

// ---------- 辅助 ----------

static int CountVisible(DisplayList list, DrawingView.Controls.DrawingView view, int w, int h)
{
    var vp = view.Viewport;
    var p0 = vp.ScreenToWorld(new SKPoint(0, 0));
    var p1 = vp.ScreenToWorld(new SKPoint(w, h));
    var rect = SKRect.Create(MathF.Min(p0.X, p1.X), MathF.Min(p0.Y, p1.Y),
                             MathF.Abs(p1.X - p0.X), MathF.Abs(p1.Y - p0.Y));
    return list.Index.Query(rect).Count;
}

/// <summary>
/// 抓一帧并统计"墨迹覆盖率"（非白像素占比）。
/// 存在的意义：性能数字好看也可能是"什么都没画"造成的 —— 必须同时验证画面非空，
/// 并对比不同 LOD 阈值下的覆盖率变化，才能判断 LOD 到底是剔掉了不可见几何，还是剔掉了内容。
/// </summary>
static (double Ink, int NonWhite, string? Png) CaptureAndAnalyze(Window window, string? savePath)
{
    var bmp = window.CaptureRenderedFrame();
    if (bmp is null) return (0, 0, null);
    if (savePath is not null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(savePath)!);
        // Avalonia 12：Save(string, int? quality) 已过时，改用编码选项重载
        bmp.Save(savePath, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    using var fb = bmp.Lock();
    int w = fb.Size.Width, h = fb.Size.Height;
    var row = new byte[fb.RowBytes];
    int nonWhite = 0;
    // 背景色取左上角像素：无头捕获的背景是控件的 BackgroundColor（AutoCAD 风格默认黑底），
    // 旧实现写死"非白 = 墨迹"，黑底下恒报 99.9%，覆盖率这个指标等于没有。
    System.Runtime.InteropServices.Marshal.Copy(fb.Address, row, 0, fb.RowBytes);
    int bgB = row[0], bgG = row[1], bgR = row[2];
    for (int y = 0; y < h; y++)
    {
        System.Runtime.InteropServices.Marshal.Copy(fb.Address + y * fb.RowBytes, row, 0, fb.RowBytes);
        for (int x = 0; x < w; x++)
        {
            int o = x * 4;
            if (row[o + 3] < 8) continue;                       // 全透明 = 未绘制
            int d = Math.Abs(row[o] - bgB) + Math.Abs(row[o + 1] - bgG) + Math.Abs(row[o + 2] - bgR);
            if (d > 24) nonWhite++;                             // 与背景明显不同 = 墨迹
        }
    }
    long total = (long)w * h;
    return (100.0 * nonWhite / total, nonWhite, savePath);
}

/// <summary>
/// 帧测量：预热 3 帧 → 强制一次 GC → 采 15 帧。
/// 同时记录两项：
///   墙钟  —— 含 Avalonia 合成与无头位图拷贝的完整成本（≈ 用户体感）
///   绘制  —— Custom draw op 自身耗时（不含固定开销，用于配置间对比，噪声小）
/// 首版测量漏了预热与 GC 隔离，同一配置两次相差 25%，据此下的结论都不可信。
/// </summary>
static Stats MeasureFrames(Window window, DrawingView.Controls.DrawingView view, int frames)
{
    for (int i = 0; i < 3; i++)
    {
        view.InvalidateVisual();
        window.CaptureRenderedFrame();
    }

    GC.Collect();
    GC.WaitForPendingFinalizers();

    var samples = new List<double>(frames);
    var drawSamples = new List<double>(frames);
    for (int i = 0; i < frames; i++)
    {
        var sw = Stopwatch.StartNew();
        view.InvalidateVisual();
        window.CaptureRenderedFrame();
        sw.Stop();
        samples.Add(sw.Elapsed.TotalMilliseconds);
        drawSamples.Add(view.LastDrawMs);
    }
    return new Stats(samples) { DrawSamples = drawSamples };
}

static Stats MeasurePanFrames(Window window, DrawingView.Controls.DrawingView view, int frames, float step)
{
    var samples = new List<double>(frames);
    for (int i = 0; i < frames; i++)
    {
        var off = view.Viewport.Offset;
        view.Viewport.Offset = new SKPoint(off.X - step, off.Y + step * 0.4f);
        var sw = Stopwatch.StartNew();
        view.InvalidateVisual();
        window.CaptureRenderedFrame();
        sw.Stop();
        samples.Add(sw.Elapsed.TotalMilliseconds);
    }
    return new Stats(samples);
}

/// <summary>
/// 图集普查（--survey &lt;dir&gt; [--limit N] [--csv path]）：只读文件、不走渲染。
///
/// <para>回答两个排期问题，避免凭感觉定优先级：
/// ① 3DSOLID / BODY / SURFACE 这类"暂不渲染"的实体到底占多少；
/// ② SHX 单线字体在真实图集里的渗透率（样式数 + 落在 SHX 样式上的文本实体数）。</para>
///
/// <para>抽样是等距抽样（先按路径排序再均匀取 N 个），避免 limit 只扫到某个子目录。</para>
/// </summary>
int FontStat(string dir, int limit, int minKb)
{
    long minBytes = (long)minKb * 1024;
    var files = Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
        .Where(f => (f.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase)
                  || f.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase))
                 && (minBytes <= 0 || new FileInfo(f).Length >= minBytes))
        .OrderBy(f => f, StringComparer.Ordinal).ToList();
    if (files.Count == 0) { Log($"FAIL: {dir} 下没有 dwg/dxf"); return 1; }

    int total = files.Count;
    if (limit > 0 && files.Count > limit)
    {
        double step = (double)files.Count / limit;
        var sampled = new List<string>(limit);
        for (int i = 0; i < limit; i++) sampled.Add(files[(int)(i * step)]);
        files = sampled;
    }
    Log($"=== 字体使用普查 === {dir}");
    Log($"文件总数 {total}，本次抽样 {files.Count}（等距抽样，--min-kb {minKb}）");
    Log("");

    static string Prop(object? o, string name)
        => o?.GetType().GetProperty(name)?.GetValue(o)?.ToString() ?? "";

    // 字体文件名 → 引用它的文本实体数（只统计真正有字的实体）
    var latin = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    var big = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    long noStyle = 0, totalText = 0;
    int failed = 0;

    foreach (var f in files)
    {
        try
        {
            ACadSharp.CadDocument doc;
            if (f.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase))
            { using var r = new ACadSharp.IO.DwgReader(f); doc = r.Read(); }
            else { using var r = new ACadSharp.IO.DxfReader(f); doc = r.Read(); }

            var styleMap = new Dictionary<string, (string Font, string Big)>(StringComparer.OrdinalIgnoreCase);
            foreach (var st in doc.TextStyles)
                styleMap[Prop(st, "Name")] = (Prop(st, "Filename"), Prop(st, "BigFontFilename"));

            var all = doc.Entities.Concat(doc.BlockRecords.SelectMany(b => b.Entities));
            foreach (var e in all)
            {
                string tn = e.GetType().Name;
                if (tn != "TextEntity" && tn != "MText" && tn != "AttributeEntity" && tn != "AttributeDefinition")
                    continue;
                totalText++;
                string sn = Prop(e.GetType().GetProperty("Style")?.GetValue(e), "Name");
                if (sn.Length == 0 || !styleMap.TryGetValue(sn, out var st2))
                { noStyle++; continue; }
                string fn = st2.Font.Length > 0 ? st2.Font : "(空=默认)";
                latin[fn] = latin.GetValueOrDefault(fn) + 1;
                if (st2.Big.Length > 0) big[st2.Big] = big.GetValueOrDefault(st2.Big) + 1;
            }
        }
        catch (Exception ex)
        {
            failed++;
            Log($"{Path.GetFileName(f),-40} 解析失败：{ex.GetType().Name}");
        }
    }

    // 本机能不能找到这些字体文件（SHX 解析第一步：有文件才能验字形）
    var fontDirs = new[] { @"C:\Windows\Fonts" };
    var local = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var d in fontDirs.Where(Directory.Exists))
        foreach (var p in Directory.EnumerateFiles(d, "*.shx"))
            local.Add(Path.GetFileName(p));

    Log($"文本实体合计 {totalText:N0}（样式名缺失 {noStyle:N0}），解析失败 {failed}");
    Log("");
    Log($"--- 主字体（按文本实体数）---  本机有 {local.Count} 个 .shx");
    foreach (var kv in latin.OrderByDescending(k => k.Value).Take(20))
    {
        string tag = local.Contains(kv.Key) ? "本地有" : (kv.Key.Contains('.') ? "本地缺" : "");
        Log($"{kv.Key,-24}{kv.Value,10:N0}{(double)kv.Value / Math.Max(1, totalText),8:P1}  {tag}");
    }
    if (big.Count > 0)
    {
        Log("");
        Log("--- 大字体（中文）---");
        foreach (var kv in big.OrderByDescending(k => k.Value).Take(10))
        {
            string tag = local.Contains(kv.Key) ? "本地有" : "本地缺";
            Log($"{kv.Key,-24}{kv.Value,10:N0}{(double)kv.Value / Math.Max(1, totalText),8:P1}  {tag}");
        }
    }
    return 0;
}

int SurveyCorpus(string dir, int limit, int minKb, string csvPath)
{
    long minBytes = (long)minKb * 1024;
    var files = Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
        .Where(f => (f.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase)
                  || f.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase))
                 && (minBytes <= 0 || new FileInfo(f).Length >= minBytes))
        .OrderBy(f => f, StringComparer.Ordinal).ToList();
    if (files.Count == 0) { Log($"FAIL: {dir} 下没有 dwg/dxf"); return 1; }

    int total = files.Count;
    if (limit > 0 && files.Count > limit)
    {
        double step = (double)files.Count / limit;
        var sampled = new List<string>(limit);
        for (int i = 0; i < limit; i++) sampled.Add(files[(int)(i * step)]);
        files = sampled;
    }
    Log($"=== 图集普查 === {dir}");
    Log($"文件总数 {total}，本次抽样 {files.Count}（等距抽样）");
    Log("");

    var csv = new System.Text.StringBuilder("file,entities,region,solid3d,body,surface,proxy,hatch,insert,text,mtext,styles,shxStyles,textsOnShx,status\n");
    int failed = 0;
    long sumEnt = 0, sumRegion = 0, sumSolid = 0, sumBody = 0, sumSurface = 0, sumProxy = 0, sumHatch = 0, sumInsert = 0, sumText = 0, sumMtext = 0;
    long sumStyles = 0, sumShxStyles = 0, sumTextsOnShx = 0;
    int filesWithSolid = 0, filesWithShx = 0;

    static string Prop(object? o, string name)
        => o?.GetType().GetProperty(name)?.GetValue(o)?.ToString() ?? "";

    foreach (var f in files)
    {
        string name = Path.GetFileName(f);
        try
        {
            ACadSharp.CadDocument doc;
            if (f.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase))
            { using var r = new ACadSharp.IO.DwgReader(f); doc = r.Read(); }
            else { using var r = new ACadSharp.IO.DxfReader(f); doc = r.Read(); }

            var blocks = doc.BlockRecords.SelectMany(b => b.Entities).ToList();
            var all = doc.Entities.Concat(blocks).ToList();

            int Count(string t) => all.Count(e => e.GetType().Name == t);
            int region = Count("Region"), solid = Count("Solid3D"), body = Count("Body"), surface = Count("Surface");
            int proxy = all.Count(e => e.GetType().Name.Contains("Proxy", StringComparison.Ordinal));
            int hatch = Count("Hatch"), insert = Count("Insert");
            int text = Count("TextEntity") + Count("MText") + Count("AttributeEntity") + Count("AttributeDefinition");
            int mtext = Count("MText");

            // SHX 渗透率：样式级（Filename 以 .shx 结尾或配了大字体）+ 实体级（文本用到的样式）
            var styles = doc.TextStyles.ToList();
            var shxNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var st in styles)
            {
                string fn = Prop(st, "Filename");
                string big = Prop(st, "BigFontFilename");
                if (fn.EndsWith(".shx", StringComparison.OrdinalIgnoreCase) || big.Length > 0)
                    shxNames.Add(Prop(st, "Name"));
            }
            int textsOnShx = all.Count(e =>
            {
                var st = e.GetType().GetProperty("Style")?.GetValue(e);
                string styleName = Prop(st, "Name");
                return styleName.Length > 0 && shxNames.Contains(styleName);
            });

            sumEnt += all.Count; sumRegion += region; sumSolid += solid; sumBody += body; sumSurface += surface;
            sumProxy += proxy; sumHatch += hatch; sumInsert += insert; sumText += text; sumMtext += mtext;
            sumStyles += styles.Count; sumShxStyles += shxNames.Count; sumTextsOnShx += textsOnShx;
            if (solid + body + surface > 0) filesWithSolid++;
            if (shxNames.Count > 0) filesWithShx++;

            csv.Append($"\"{name}\",{all.Count},{region},{solid},{body},{surface},{proxy},{hatch},{insert},{text},{mtext},{styles.Count},{shxNames.Count},{textsOnShx},ok\n");
            Log($"{name,-42} 实体 {all.Count,7:N0}  3D/体/面 {solid}/{body}/{surface}  SHX 样式 {shxNames.Count}/{styles.Count}  用 SHX 的文本 {textsOnShx:N0}");
        }
        catch (Exception ex)
        {
            failed++;
            csv.Append($"\"{name}\",0,0,0,0,0,0,0,0,0,0,0,0,0,\"{ex.GetType().Name}\"\n");
            Log($"{name,-42} 解析失败：{ex.GetType().Name}");
        }
    }

    File.WriteAllText(csvPath, csv.ToString());
    Log("");
    Log("--- 汇总 ---");
    Log($"扫描 {files.Count} 个，解析失败 {failed}");
    Log($"实体合计 {sumEnt:N0}：REGION {sumRegion:N0} / 3DSOLID {sumSolid:N0} / BODY {sumBody:N0} / SURFACE {sumSurface:N0} / 代理 {sumProxy:N0}");
    Log($"含 3DSOLID/BODY/SURFACE 的文件：{filesWithSolid}（{(double)filesWithSolid / Math.Max(1, files.Count - failed):P1}）");
    Log($"SHX：样式 {sumShxStyles:N0}/{sumStyles:N0}（{(double)sumShxStyles / Math.Max(1, sumStyles):P1}），"
        + $"含 SHX 样式的文件 {filesWithShx}（{(double)filesWithShx / Math.Max(1, files.Count - failed):P1}），"
        + $"落在 SHX 样式上的文本实体 {sumTextsOnShx:N0}/{sumText:N0}");
    Log($"明细 CSV：{csvPath}");
    return 0;
}

/// <summary>同步记录各阶段时间戳（Progress&lt;T&gt; 在无 UI 上下文时会派发到线程池，不适合计时）。</summary>
sealed class StageRecorder : IProgress<string>
{
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    public List<(string Stage, double Ms)> Stages { get; } = new();
    public void Report(string value) => Stages.Add((value, _sw.Elapsed.TotalMilliseconds));
}

/// <summary>帧耗时统计。</summary>
readonly record struct Stats(List<double> Samples)
{
    /// <summary>绘制操作自身耗时样本（与 Samples 一一对应）。</summary>
    public List<double>? DrawSamples { get; init; }

    private List<double> Sorted => Samples.OrderBy(x => x).ToList();
    public int Count => Samples.Count;
    public double Min => Samples.Count == 0 ? 0 : Samples.Min();
    public double Max => Samples.Count == 0 ? 0 : Samples.Max();
    public double Median => MedianOf(Samples);
    public double P95
    {
        get
        {
            if (Samples.Count == 0) return 0;
            var s = Sorted;
            return s[Math.Min(s.Count - 1, (int)(s.Count * 0.95))];
        }
    }
    public double DrawMedian => DrawSamples is null ? 0 : MedianOf(DrawSamples);

    private static double MedianOf(List<double> v)
    {
        if (v.Count == 0) return 0;
        var s = v.OrderBy(x => x).ToList();
        return s[s.Count / 2];
    }
}
