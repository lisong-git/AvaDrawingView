using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;
using SkiaSharp;

namespace CadViewer.Core;

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
            if (b.Width <= 0 && b.Height <= 0 && b.Left == 0 && b.Top == 0) continue;
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
