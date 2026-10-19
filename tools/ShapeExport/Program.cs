// ShapeExport —— 把模型空间里「相互相交的图元」聚成一组，每组各导出一个独立 DWG。
//
// 用法：
//   dotnet run --project tools/ShapeExport -- <源图.dwg|dxf> [输出目录] [选项]
//
// 默认输出目录 = <源图目录>\blocks；文件按组编号命名 0001.dwg、0002.dwg …（默认按包围盒面积从大到小排序），
// 并落一份清单 CSV。
//
// 判定口径（默认）：
//   * 只在**几何图元**之间判相交：LINE / LWPOLYLINE / POLYLINE / CIRCLE / ARC / ELLIPSE /
//     SPLINE / HATCH / SOLID / FACE3D / INSERT …；文字、标注、引线、点不参与判定
//     （否则几乎每个轮廓都会碰到标注线）
//   * 「相交」= 压平后的线段真正相交 / 端点相接 / 搭接，容差 --tol（默认 1e-6 世界单位）
//   * 相交关系是传递的：A–B 相交、B–C 相交 ⇒ A、B、C 属同一组（连通分量）
//   * 关闭 / 冻结图层上的图元不参与判定，也不导出
//
// 选项：
//   --list                只统计分组结果，不导出
//   --min-size <N>        只导出含 ≥N 个图元的组（默认 1，即含单图元组）
//   --min-extent <值>     只导出包围盒对角线 ≥ 该值的组（过滤零碎）
//   --tol <值>            相交容差（世界单位，默认 1e-6）
//   --format dwg|dxf      输出格式（默认 dwg）
//   --annotations         文字 / 标注 / 引线也参与相交判定
//   --exclude-layers <名> 跳过这些图层上的图元（逗号分隔）
//   --order size|seq      组排序（默认 size = 包围盒面积从大到小；seq = 按图元出现顺序）
//   --origin              把每组几何平移到自身左下角为原点（默认保留源坐标）
//   --limit <N>           只导出排在最前面的 N 组（试跑用）
//   --max-groups <N>      组数安全上限（默认 3000，超过则中止；--force 放行）
//   --force               忽略安全上限
//   --version AC1032      输出 DWG 版本
//   --no-verify           写完后不回读校验
//
// 日志：SHAPEEXPORT_LOG=<路径> 写 UTF-8 日志文件（Windows 控制台重定向会乱码）。

using System.Text;
using ACadSharp;
using ACadSharp.Entities;
using DrawingView.Tools.Shared;

namespace DrawingView.Tools.ShapeExport;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var logPath = Environment.GetEnvironmentVariable("SHAPEEXPORT_LOG");
        StreamWriter? logFile = null;
        if (!string.IsNullOrEmpty(logPath))
        {
            logFile = new StreamWriter(logPath, false, new UTF8Encoding(false)) { AutoFlush = true };
            Console.SetOut(logFile);
            Console.SetError(logFile);
        }

        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            Console.WriteLine("致命错误: " + ex);
            return 2;
        }
        finally
        {
            logFile?.Flush();
            logFile?.Dispose();
        }
    }

    private static int Run(string[] args)
    {
        string? src = null;
        string? outDir = null;
        var listOnly = false;
        var format = "dwg";
        var withAnnotations = false;
        var verify = true;
        var tol = 1e-6;
        var minSize = 1;
        var minExtent = 0.0;
        var orderBySize = true;
        var originAtCorner = false;
        var maxGroups = 3000;
        var limit = 0;
        var force = false;
        ACadVersion? forceVersion = null;
        var excludeLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--list": listOnly = true; break;
                case "--annotations": withAnnotations = true; break;
                case "--no-verify": verify = false; break;
                case "--origin": originAtCorner = true; break;
                case "--force": force = true; break;
                case "--tol": tol = Num(Next(args, ref i), tol); break;
                case "--min-size": minSize = Math.Max(1, (int)Num(Next(args, ref i), 1)); break;
                case "--min-extent": minExtent = Num(Next(args, ref i), 0); break;
                case "--max-groups": maxGroups = Math.Max(1, (int)Num(Next(args, ref i), 3000)); break;
                case "--limit": limit = Math.Max(0, (int)Num(Next(args, ref i), 0)); break;
                case "--format": format = Next(args, ref i); break;
                case "--order": orderBySize = !Next(args, ref i).Equals("seq", StringComparison.OrdinalIgnoreCase); break;
                case "--exclude-layers":
                    foreach (var l in Next(args, ref i).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        excludeLayers.Add(l);
                    break;
                case "--version":
                    var v = Next(args, ref i);
                    if (Enum.TryParse(v, true, out ACadVersion pv)) forceVersion = pv;
                    else Console.WriteLine($"无法识别的版本 '{v}'，忽略");
                    break;
                default:
                    if (src is null) src = a;
                    else if (outDir is null) outDir = a;
                    break;
            }
        }

        if (string.IsNullOrEmpty(src))
        {
            Console.WriteLine("用法: ShapeExport <源图.dwg|dxf> [输出目录] [选项]");
            return 1;
        }
        if (!File.Exists(src))
        {
            Console.WriteLine($"文件不存在: {src}");
            return 1;
        }
        outDir ??= Path.Combine(Path.GetDirectoryName(Path.GetFullPath(src))!, "blocks");
        Directory.CreateDirectory(outDir);

        Console.WriteLine($"源文件: {src}");
        Console.WriteLine($"输出目录: {outDir}");
        Console.WriteLine($"相交容差: {tol}   最小图元数/组: {minSize}   最小尺寸: {(minExtent > 0 ? minExtent.ToString("G") : "不限")}   标注参与判定: {(withAnnotations ? "是" : "否")}");
        Console.WriteLine();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var doc = CadIo.Read(src);
        Console.WriteLine($"版本: {doc.Header.Version}   模型空间实体: {doc.Entities.Count()}   读取耗时 {sw.Elapsed.TotalSeconds:F1}s");

        // ---- 1. 取几何图元，压平成线段 ----
        sw.Restart();
        var items = new List<Item>();
        var ignored = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in doc.Entities)
        {
            var type = e.ObjectName ?? e.GetType().Name;
            if (excludeLayers.Contains(e.Layer?.Name ?? "")) { ignored[type] = ignored.GetValueOrDefault(type) + 1; continue; }
            if (!IsVisible(e)) { ignored[type] = ignored.GetValueOrDefault(type) + 1; continue; }
            if (!withAnnotations && IsAnnotation(e)) { ignored[type] = ignored.GetValueOrDefault(type) + 1; continue; }

            var segs = Geometry.Flatten(e);
            if (segs.Count == 0) { ignored[type] = ignored.GetValueOrDefault(type) + 1; continue; }
            items.Add(new Item(items.Count, e, type, segs));
        }
        var n = items.Count;
        Console.WriteLine($"参与判定的几何图元: {n}   忽略: {ignored.Values.Sum()}"
                          + (ignored.Count > 0 ? $"（{string.Join("、", ignored.OrderByDescending(kv => kv.Value).Take(8).Select(kv => $"{kv.Key}×{kv.Value}"))}）" : ""));
        Console.WriteLine($"压平耗时 {sw.Elapsed.TotalSeconds:F1}s，线段总数 {items.Sum(it => it.Segs.Count)}");

        if (n == 0)
        {
            Console.WriteLine("没有可用于判定的几何图元，结束。");
            return 0;
        }

        // ---- 2. 沿 X 扫描 + 精确相交判定，并查集合并成组 ----
        sw.Restart();
        var parent = new int[n];
        for (var i = 0; i < n; i++) parent[i] = i;
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        void Union(int a, int b) { var ra = Find(a); var rb = Find(b); if (ra != rb) parent[ra] = rb; }

        var order = Enumerable.Range(0, n).OrderBy(i => items[i].MinX).ToArray();
        var active = new List<int>();
        long tested = 0, hits = 0;
        for (var oi = 0; oi < n; oi++)
        {
            var a = items[order[oi]];
            // 左边界已经离开扫描线的对象不再参与后续比较
            active.RemoveAll(j => items[j].MaxX < a.MinX - tol);
            foreach (var j in active)
            {
                var b = items[j];
                if (b.MinY > a.MaxY + tol || b.MaxY < a.MinY - tol) continue;
                tested++;
                if (Geometry.Intersects(a.Segs, b.Segs, tol))
                {
                    hits++;
                    Union(a.Index, b.Index);
                }
            }
            active.Add(a.Index);
        }
        Console.WriteLine($"候选对精确判定 {tested} 次，相交 {hits} 处，耗时 {sw.Elapsed.TotalSeconds:F1}s");

        // ---- 3. 按根归并成组 ----
        var groups = new Dictionary<int, List<Item>>();
        foreach (var it in items)
        {
            var r = Find(it.Index);
            if (!groups.TryGetValue(r, out var list)) groups[r] = list = new List<Item>();
            list.Add(it);
        }

        var stats = groups.Values.Select(g =>
        {
            var minX = g.Min(x => x.MinX); var minY = g.Min(x => x.MinY);
            var maxX = g.Max(x => x.MaxX); var maxY = g.Max(x => x.MaxY);
            return new Group(g, minX, minY, maxX, maxY);
        }).ToList();

        var sizes = stats.Select(s => s.Items.Count).ToList();
        Console.WriteLine($"分组结果: {stats.Count} 组（最大 {sizes.Max()} 个图元，单图元组 {sizes.Count(s => s == 1)} 个，多图元组 {sizes.Count(s => s > 1)} 个）");
        Console.WriteLine("组大小分布: " + string.Join("、",
            stats.GroupBy(s => s.Items.Count).OrderBy(g => g.Key).Take(12)
                 .Select(g => $"{g.Key}图元×{g.Count()}组")));

        // ---- 4. 过滤 + 排序 ----
        var picked = stats.Where(s => s.Items.Count >= minSize).ToList();
        if (minExtent > 0) picked = picked.Where(s => s.Diagonal >= minExtent).ToList();
        picked = orderBySize
            ? picked.OrderByDescending(s => s.Area).ThenByDescending(s => s.Items.Count).ToList()
            : picked.OrderBy(s => s.Items[0].Index).ToList();
        if (limit > 0) picked = picked.Take(limit).ToList();

        Console.WriteLine();
        Console.WriteLine($"待导出: {picked.Count} 组"
                          + (picked.Count != stats.Count ? $"（已滤掉 {stats.Count - picked.Count} 组）" : ""));

        if (listOnly)
        {
            foreach (var s in picked.Take(40))
                Console.WriteLine($"  {s.Items.Count,4} 图元  {string.Join("/", CadIo.Kinds(s.Items.Select(x => x.Entity)))}  "
                                  + $"范围 ({s.MinX:F1},{s.MinY:F1})~({s.MaxX:F1},{s.MaxY:F1})  面积 {s.Area:F0}");
            return 0;
        }

        if (picked.Count > maxGroups && !force)
        {
            Console.WriteLine($"组数 {picked.Count} 超过安全上限 {maxGroups}，已中止（确认无误请加 --force，或用 --min-size/--min-extent 收窄范围）。");
            return 3;
        }

        // ---- 5. 逐组导出 ----
        sw.Restart();
        var ok = 0; var failed = 0;
        var manifest = new List<Rec>();
        var version = forceVersion ?? doc.Header.Version;

        for (var i = 0; i < picked.Count; i++)
        {
            var s = picked[i];
            var name = $"{i + 1:D4}";
            var outPath = Path.Combine(outDir, name + (format == "dxf" ? ".dxf" : ".dwg"));
            if (i % 100 == 0) Console.WriteLine($"[{i + 1}/{picked.Count}] 导出中…");

            try
            {
                var outDoc = CadDocCopier.Build(s.Items.Select(x => x.Entity), version);
                if (originAtCorner && s.Items.Count > 0)
                    ShiftToOrigin(outDoc, s.MinX, s.MinY);

                var actual = CadIo.Write(outDoc, outPath, format, forceVersion);
                var status = "OK";

                if (verify)
                {
                    var back = CadIo.Read(actual);
                    var got = back.Entities.Count();
                    if (got != s.Items.Count)
                    {
                        status = $"差异(实体 {got}/{s.Items.Count})";
                        Console.WriteLine($"       [差异] {name}: {string.Join("/", CadIo.Kinds(back.Entities))}");
                    }
                }

                ok++;
                manifest.Add(new Rec(Path.GetFileName(actual), s, status));
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"       [失败] {name}: {ex.GetType().Name}: {ex.Message}");
                manifest.Add(new Rec(name + "." + format, s, "失败: " + ex.Message));
            }
        }

        var mp = Path.Combine(outDir, "_图形分组清单.csv");
        using (var mw = new StreamWriter(mp, false, new UTF8Encoding(true)))
        {
            mw.WriteLine("序号,文件名,图元数,类型构成,最小X,最小Y,最大X,最大Y,宽,高,面积,成员句柄,状态");
            var idx = 0;
            foreach (var r in manifest)
            {
                idx++;
                var g = r.Group;
                var handles = string.Join(" ", g.Items.Select(x => x.Entity.Handle.ToString("X")).Take(12));
                if (g.Items.Count > 12) handles += " …";
                mw.WriteLine($"{idx},{CadIo.Csv(r.File)},{g.Items.Count},{CadIo.Csv(string.Join("/", CadIo.Kinds(g.Items.Select(x => x.Entity))))},"
                             + $"{g.MinX:F3},{g.MinY:F3},{g.MaxX:F3},{g.MaxY:F3},{g.Width:F3},{g.Height:F3},{g.Area:F1},"
                             + $"{CadIo.Csv(handles)},{CadIo.Csv(r.Status)}");
            }
        }

        sw.Stop();
        Console.WriteLine();
        Console.WriteLine($"清单: {mp}");
        Console.WriteLine($"完成：成功 {ok}，失败 {failed}，耗时 {sw.Elapsed.TotalSeconds:F1}s");
        return failed > 0 ? 1 : 0;
    }

    private static void ShiftToOrigin(CadDocument doc, double dx, double dy)
    {
        if (Math.Abs(dx) < 1e-12 && Math.Abs(dy) < 1e-12) return;
        var t = new CSMath.Transform { Translation = new CSMath.XYZ(-dx, -dy, 0) };
        foreach (var e in doc.Entities)
        {
            if (e is IGeometricEntity g) g.ApplyTransform(t);
        }
    }

    private static string Next(string[] args, ref int i) => i + 1 < args.Length ? args[++i] : "";

    private static double Num(string s, double fallback)
        => double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

    /// <summary>注释类图元（默认不参与相交判定）。</summary>
    private static bool IsAnnotation(Entity e) => e is
        TextEntity or MText or Dimension or Leader or MultiLeader or Tolerance or
        AttributeDefinition or AttributeEntity or Point or Viewport or Wipeout;

    /// <summary>关闭 / 冻结图层上的图元不参与判定。</summary>
    private static bool IsVisible(Entity e)
    {
        var l = e.Layer;
        if (l is null) return true;
        if (!l.IsOn) return false;
        if (l.Flags.ToString().Contains("Frozen", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }
}

/// <summary>参与相交判定的图元（压平后的线段 + 包围盒）。</summary>
internal sealed class Item
{
    public int Index { get; }
    public Entity Entity { get; }
    public string Type { get; }
    public List<Geometry.Seg> Segs { get; }
    public double MinX { get; }
    public double MinY { get; }
    public double MaxX { get; }
    public double MaxY { get; }

    public Item(int index, Entity entity, string type, List<Geometry.Seg> segs)
    {
        Index = index;
        Entity = entity;
        Type = type;
        Segs = segs;
        MinX = segs.Min(s => Math.Min(s.X1, s.X2));
        MinY = segs.Min(s => Math.Min(s.Y1, s.Y2));
        MaxX = segs.Max(s => Math.Max(s.X1, s.X2));
        MaxY = segs.Max(s => Math.Max(s.Y1, s.Y2));
    }
}

/// <summary>一组相互（传递）相交的图元 + 组包围盒。</summary>
internal sealed class Group
{
    public List<Item> Items { get; }
    public double MinX { get; }
    public double MinY { get; }
    public double MaxX { get; }
    public double MaxY { get; }

    public Group(List<Item> items, double minX, double minY, double maxX, double maxY)
    {
        Items = items;
        MinX = minX; MinY = minY; MaxX = maxX; MaxY = maxY;
    }

    public double Width => MaxX - MinX;
    public double Height => MaxY - MinY;
    public double Area => Width * Height;
    public double Diagonal => Math.Sqrt(Width * Width + Height * Height);
}

/// <summary>清单行。</summary>
internal sealed record Rec(string File, Group Group, string Status);
