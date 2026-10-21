using System.Diagnostics;
using SkiaSharp;

namespace DrawingView.Core;

/// <summary>
/// 合并绘制批次：同一 (图层, 颜色) 的所有细线几何合并进单个 SKPath。
/// 渲染时每个批次只需一次 DrawPath，替代"每实体一次 DrawPath"。
/// </summary>
public sealed class BakedBucket
{
    public required string Layer { get; init; }
    public required uint ColorArgb { get; init; }
    /// <summary>合并后的描边几何（细线，线宽由渲染期按缩放给出）。</summary>
    public SKPath? StrokePath { get; init; }
    /// <summary>合并后的填充几何（SOLID / HATCH 边界）。</summary>
    public SKPath? FillPath { get; init; }
    /// <summary>批次包围盒（世界坐标），用于整批剔除。</summary>
    public SKRect Bounds { get; init; }
    /// <summary>本批次合并了多少个实体。</summary>
    public int EntityCount { get; init; }
    /// <summary>线型虚线间隔（世界单位）；null = 实线。同一批次内线型相同。</summary>
    public float[]? DashPattern { get; init; }
    /// <summary>
    /// 本批次的描边宽度（世界单位，0 = 细线）。
    ///
    /// <para>线宽必须进批次键：一批只用一个 SKPaint，混宽会串线。
    /// 早期实现把"有线宽实体"整批剔到 <c>WideStrokeEntities</c> 里逐条画，
    /// 但 AutoCAD 图纸里 0.25mm 是**默认线宽**——按 ByLayer 解析后几乎每个实体都有线宽，
    /// 那样等于把全部几何踢出批次路径（13.5 万实体 → 13.5 万次 DrawPath），
    /// 实测帧时间从 5ms 退回 137ms。改为按线宽分桶后批次数量只增加"线宽种类数"倍（通常 2~5）。</para>
    /// </summary>
    public int LineWidthMm100 { get; init; }
}

/// <summary>
/// 一个世界尺寸档的批次集合。
/// LOD 依据：实体在屏幕上的尺寸 = 世界尺寸 × 缩放。
/// 若整档的最大屏幕尺寸都低于剔除阈值，说明该档全是亚像素几何，可整档跳过。
/// </summary>
public sealed class BakedBucketSet
{
    public required float MinWorldSize { get; init; }
    public required float MaxWorldSize { get; init; }
    public required IReadOnlyList<BakedBucket> Buckets { get; init; }
    public int EntityCount { get; init; }
    public int PointCount { get; init; }

    /// <summary>该档在给定缩放下能画出的最大像素尺寸。</summary>
    public float MaxPixelSize(float zoom) => MaxWorldSize * zoom;
}

/// <summary>
/// 烘焙几何层 —— Skia 栈下等价于 ReadCAD 的 Gpu*Buffer 批次缓冲与 LOD 分级。
///
/// 动机（本项目实测，5.36MB / 135876 实体图纸）：
///   1) 逐实体绘制每帧成本 ≈ 固定开销 + 实体数 × 1.0µs。全图视角 135876 个实体可见
///      → 137.5ms/帧（7.3 FPS）。成本几乎全在"每实体一次托管→原生 DrawPath 调用 +
///      每次改 SKPaint.Color 导致 Skia 状态失效"，与几何大小无关。→ 合并批次解决。
///   2) 合并后仍有 53.9 万个点，成本转为光栅化本身。而全图视角下 87% 的点属于
///      世界尺寸 &lt; 1024 单位（屏幕上不到 0.6px）的几何 —— 画了也看不见。
///      → 按世界尺寸分档 + 渲染期整档跳过（LOD）解决。
///
/// 与 ReadCAD 的对应关系：
///   GpuLineBuffer / GpuArcBuffer / GpuPolylineBuffer ...  → Buckets（按颜色合并）
///   ViewportFilteringService / ViewportBoundsCache        → BakedBucket.Bounds 整批剔除
///   MinEntityPixelSize（LOD）                             → Classes 尺寸分档
///   MaxViewportEntities（上限保护）                        → 见 DrawingView 的双路切换
/// </summary>
public sealed class BakedGeometry : IDisposable
{
    /// <summary>世界尺寸档，由小到大。</summary>
    public required IReadOnlyList<BakedBucketSet> Classes { get; init; }
    /// <summary>
    /// 不合并的实体：文本与点标记。
    ///
    /// <para>共同点是**屏幕尺寸不受世界坐标约束**：文字按像素高度做 LOD 与字体取整，
    /// 点标记（POINT）按 PDSIZE 的视高百分比定尺寸。烘焙成世界坐标几何就只能二选一错，
    /// 所以它们留在原列表里，渲染期逐条在屏幕空间绘制。</para>
    /// </summary>
    public required IReadOnlyList<CadEntity> UnbatchedEntities { get; init; }
    public required int SourceEntityCount { get; init; }
    public required double BakeMs { get; init; }
    /// <summary>合并后的总批次数（各档合计）。</summary>
    public required int BucketCount { get; init; }

    public bool IsEmpty => BucketCount == 0;

    /// <summary>
    /// 释放批次内的合并路径（原生内存）。
    ///
    /// 注意：<see cref="UnbatchedEntities"/> 里的实体与
    /// <c>DisplayList.Entities</c> 是**同一批对象**（烘焙只是过滤，没有复制），
    /// 它们的路径由 <c>DisplayList</c> 统一释放，此处绝不能重复释放。
    /// 本类只负责自己新建的合并路径。
    /// </summary>
    public void Dispose()
    {
        foreach (var cls in Classes)
            foreach (var b in cls.Buckets)
            {
                b.StrokePath?.Dispose();
                b.FillPath?.Dispose();
            }
    }
}

/// <summary>
/// 几何烘焙器：把逐实体几何按 (世界尺寸档, 图层, 颜色) 合并成批次。
/// 在后台加载线程执行，产出后不再修改（与 DisplayList 同为不可变快照）。
/// </summary>
public static class GeometryBaker
{
    /// <summary>
    /// 低于此实体数不烘焙：逐实体绘制的上限成本约 1µs/实体，
    /// 2 万实体 ≈ 20ms/帧（50 FPS），已达标，烘焙反而白白多占一份内存。
    /// </summary>
    public const int DefaultMinEntityCount = 20_000;

    /// <summary>
    /// 世界尺寸档的分界（单位：世界长度），按 4 倍递增。
    ///
    /// 取 4 倍步长是"保守正确"的权衡：渲染时跳过的是**最大世界尺寸都低于阈值**的整档，
    /// 因此永远不会剔掉屏幕上可见的实体，最多是多留一档（多画一点）。步长越小越精确、
    /// 但档数越多、批次数越碎，故取 4。
    /// </summary>
    private static readonly float[] SizeEdges =
    {
        1f, 4f, 16f, 64f, 256f, 1024f, 4096f, 16384f, 65536f, 262144f, 1048576f, float.MaxValue,
    };

    public static BakedGeometry? Bake(IReadOnlyList<CadEntity> entities,
                                      CancellationToken ct = default,
                                      int minEntityCount = DefaultMinEntityCount)
    {
        if (entities.Count < minEntityCount) return null;

        var sw = Stopwatch.StartNew();
        var map = new Dictionary<Key, Accum>(entities.Count / 8 + 16);
        var unbatched = new List<CadEntity>();

        int n = 0;
        foreach (var e in entities)
        {
            if ((++n % 4096) == 0) ct.ThrowIfCancellationRequested();
            // 文本与点标记都不进批次（屏幕空间绘制），见 UnbatchedEntities 的说明
            if (e.Text is not null || e.Mark is not null) { unbatched.Add(e); continue; }
            if (e.Path is not null)
            {
                var acc = Get(map, SizeClassOf(e.Bounds), e.Layer, e.ColorArgb, e.DashPattern, evenOdd: false, e.LineWeightMm100);
                acc.Stroke ??= new SKPath();
                acc.Stroke.AddPath(e.Path, 0f, 0f);
                acc.AddBounds(e.Bounds);
            }
            if (e.FillPath is not null)
            {
                // 填充规则必须进分桶键：HATCH 孤岛靠 EvenOdd 才是"洞"，
                // 与普通 Winding 填充合并到同一批次会让洞被填实。
                bool evenOdd = e.FillPath.FillType == SKPathFillType.EvenOdd;
                var acc = Get(map, SizeClassOf(e.Bounds), e.Layer, e.ColorArgb, e.DashPattern, evenOdd, lineWidthMm100: 0);
                acc.Fill ??= new SKPath();
                if (evenOdd) acc.Fill.FillType = SKPathFillType.EvenOdd;
                acc.Fill.AddPath(e.FillPath, 0f, 0f);
                acc.AddBounds(e.Bounds);
            }
        }

        // 按尺寸档分组；档内按 (图层, 颜色) 排序，让同色批次连续绘制，避免 SKPaint.Color 反复失效。
        var classes = new List<BakedBucketSet>();
        int totalBuckets = 0;
        foreach (var group in map.GroupBy(kv => kv.Key.Class).OrderBy(g => g.Key))
        {
            var buckets = group
                .OrderBy(kv => kv.Key.Layer, StringComparer.Ordinal)
                .ThenBy(kv => kv.Key.Color)
                .Select(kv => new BakedBucket
                {
                    Layer = kv.Key.Layer,
                    ColorArgb = kv.Key.Color,
                    StrokePath = kv.Value.Stroke,
                    FillPath = kv.Value.Fill,
                    Bounds = kv.Value.HasBounds ? kv.Value.Bounds : SKRect.Empty,
                    EntityCount = kv.Value.Count,
                    DashPattern = kv.Key.Dash,
                    LineWidthMm100 = kv.Key.LineWidthMm100,
                })
                .ToArray();

            totalBuckets += buckets.Length;
            classes.Add(new BakedBucketSet
            {
                MinWorldSize = SizeEdges[group.Key],
                MaxWorldSize = SizeEdges[group.Key + 1],
                Buckets = buckets,
                EntityCount = buckets.Sum(b => b.EntityCount),
                PointCount = buckets.Sum(b => (b.StrokePath?.PointCount ?? 0) + (b.FillPath?.PointCount ?? 0)),
            });
        }

        sw.Stop();
        return new BakedGeometry
        {
            Classes = classes,
            UnbatchedEntities = unbatched,
            SourceEntityCount = entities.Count,
            BakeMs = sw.Elapsed.TotalMilliseconds,
            BucketCount = totalBuckets,
        };
    }

    /// <summary>实体所属的世界尺寸档（用包围盒最长边作为实体尺寸）。</summary>
    private static int SizeClassOf(SKRect bounds)
    {
        float size = MathF.Max(bounds.Width, bounds.Height);
        if (float.IsNaN(size)) return 0;
        for (int i = 0; i < SizeEdges.Length - 1; i++)
            if (size < SizeEdges[i + 1]) return i;
        return SizeEdges.Length - 2;
    }

    private static Accum Get(Dictionary<Key, Accum> map, int cls, string layer, uint color,
                             float[]? dash, bool evenOdd, int lineWidthMm100)
    {
        var key = new Key(cls, layer, color, dash, evenOdd, lineWidthMm100);
        if (!map.TryGetValue(key, out var acc))
        {
            acc = new Accum();
            map[key] = acc;
        }
        acc.Count++;
        return acc;
    }

    private readonly record struct Key(int Class, string Layer, uint Color, float[]? Dash, bool EvenOdd, int LineWidthMm100);

    private sealed class Accum
    {
        public SKPath? Stroke;
        public SKPath? Fill;
        public int Count;
        public SKRect Bounds;
        public bool HasBounds;

        public void AddBounds(SKRect b)
        {
            if (float.IsNaN(b.Left) || float.IsInfinity(b.Left)) return;
            if (!HasBounds)
            {
                Bounds = b;
                HasBounds = true;
                return;
            }
            // SKRect.Union 是就地修改（返回 void），故先取副本再写回
            var u = Bounds;
            u.Union(b);
            Bounds = u;
        }
    }
}
