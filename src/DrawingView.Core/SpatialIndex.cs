using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;
using SkiaSharp;

namespace DrawingView.Core;

/// <summary>
/// 基于 NTS STRtree (R树) 的空间索引：视口裁剪 + 拾取查询。
/// </summary>
public sealed class SpatialIndex
{
    private readonly STRtree<CadEntity> _tree = new();

    private SpatialIndex() { }

    /// <summary>构建索引。大图（十万级）构建约 0.8s，期间周期性响应取消。</summary>
    public static SpatialIndex Build(IReadOnlyList<CadEntity> entities, CancellationToken ct = default)
    {
        var index = new SpatialIndex();
        int n = 0;
        foreach (var e in entities)
        {
            if ((++n % 4096) == 0) ct.ThrowIfCancellationRequested();
            var b = e.Bounds;
            if (float.IsNaN(b.Left) || float.IsInfinity(b.Left)) continue;
            // 零面积且落在原点的实体通常是"没几何"的空壳，不入索引（避免原点堆积）。
            // 但 POINT 点标记本来就是零面积包围盒（尺寸在渲染期按视口算），必须收进来，
            // 否则位于原点附近（含负数区）的点会被索引漏掉，查询结果里凭空少实体。
            if (b.Width <= 0 && b.Height <= 0 && b.Left == 0 && b.Top == 0 && e.Mark is null) continue;
            index._tree.Insert(new Envelope(b.Left, b.Right, b.Bottom, b.Top), e);
        }
        index._tree.Build();
        return index;
    }

    /// <summary>查询世界矩形内的实体（含相交）。</summary>
    public IList<CadEntity> Query(SKRect worldRect)
    {
        var env = new Envelope(worldRect.Left, worldRect.Right, worldRect.Bottom, worldRect.Top);
        return _tree.Query(env);
    }
}
