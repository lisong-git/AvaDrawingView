using ACadSharp.IO;
using SkiaSharp;

namespace CadViewer.Core;

/// <summary>
/// 图纸加载：后台线程 解析 → 几何转换 → 索引构建，产出不可变 DisplayList。
/// </summary>
public static class CadDocumentLoader
{
    public static Task<DisplayList> LoadAsync(string path, IProgress<string>? progress = null, CancellationToken ct = default)
        => Task.Run(() => Load(path, progress, ct), ct);

    public static DisplayList Load(string path, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        progress?.Report("解析文件…");
        ct.ThrowIfCancellationRequested();
        var doc = ReadDocument(path, ct);

        progress?.Report("转换几何…");
        ct.ThrowIfCancellationRequested();
        var converter = new GeometryConverter();
        var entities = converter.Convert(doc, ct);

        progress?.Report("构建空间索引…");
        ct.ThrowIfCancellationRequested();
        var index = SpatialIndex.Build(entities, ct);

        progress?.Report("烘焙绘制批次…");
        ct.ThrowIfCancellationRequested();
        var baked = GeometryBaker.Bake(entities, ct);

        var extents = ComputeExtents(entities);
        progress?.Report(baked is null
            ? $"完成：{entities.Count} 个实体，跳过 {converter.Skipped}"
            : $"完成：{entities.Count} 个实体，跳过 {converter.Skipped}，"
              + $"合并为 {baked.BucketCount} 个绘制批次（{baked.BakeMs:F0} ms）");

        var handleBounds = new Dictionary<ulong, SKRect>();
        foreach (var ent in entities)
        {
            if (ent.Handle != 0 && !handleBounds.ContainsKey(ent.Handle))
                handleBounds.Add(ent.Handle, ent.Bounds);
        }

        return new DisplayList
        {
            Entities = entities,
            Index = index,
            LayerNames = converter.LayerNames,
            HandleBounds = handleBounds,
            Extents = extents,
            Baked = baked,
            InitiallyHiddenLayers = converter.InitiallyHiddenLayers,
        };
    }

    /// <summary>
    /// 读取 DWG/DXF。
    /// 注意：ACadSharp 的 <c>Read()</c> 不接受 CancellationToken，解析阶段**无法中断**
    /// （大图约 4 秒），取消只能在解析结束后的各阶段生效。
    /// </summary>
    private static ACadSharp.CadDocument ReadDocument(string path, CancellationToken ct)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        switch (ext)
        {
            case ".dwg":
                using (var reader = new DwgReader(path))
                    return reader.Read();
            case ".dxf":
                using (var reader = new DxfReader(path))
                    return reader.Read();
            default:
                throw new NotSupportedException($"不支持的文件格式: {ext}");
        }
    }

    private static SKRect ComputeExtents(IReadOnlyList<CadEntity> entities)
    {
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        bool any = false;
        foreach (var e in entities)
        {
            var b = e.Bounds;
            if (float.IsNaN(b.Left) || float.IsInfinity(b.Left) || float.IsNaN(b.Top) || float.IsInfinity(b.Top)) continue;
            any = true;
            minX = MathF.Min(minX, b.Left);
            minY = MathF.Min(minY, b.Top);
            maxX = MathF.Max(maxX, b.Right);
            maxY = MathF.Max(maxY, b.Bottom);
        }
        if (!any) return SKRect.Create(0, 0, 100, 100);
        return SKRect.Create(minX, minY, MathF.Max(maxX - minX, 1e-3f), MathF.Max(maxY - minY, 1e-3f));
    }
}
