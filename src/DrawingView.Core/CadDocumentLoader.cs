using ACadSharp.IO;
using SkiaSharp;

namespace DrawingView.Core;

/// <summary>
/// 图纸加载：后台线程 解析 → 几何转换 → 索引构建，产出不可变 DisplayList。
/// </summary>
public static class CadDocumentLoader
{
    public static Task<DisplayList> LoadAsync(string path, IProgress<string>? progress = null, CancellationToken ct = default, GeometryConvertOptions? options = null)
        => Task.Run(() => Load(path, progress, ct, options), ct);

    public static DisplayList Load(string path, IProgress<string>? progress = null, CancellationToken ct = default, GeometryConvertOptions? options = null)
    {
        progress?.Report("解析文件…");
        ct.ThrowIfCancellationRequested();
        var doc = ReadDocument(path, ct);

        // SHX 字库查找：先看图纸所在目录（实测图集里有 11 个 .shx 随图附带）
        try { ShxFontLibrary.SetDrawingDirectory(Path.GetDirectoryName(Path.GetFullPath(path))); }
        catch { }

        progress?.Report("转换几何…");
        ct.ThrowIfCancellationRequested();
        var converter = new GeometryConverter();
        var entities = converter.Convert(doc, ct, options);

        progress?.Report("构建空间索引…");
        ct.ThrowIfCancellationRequested();
        var index = SpatialIndex.Build(entities, ct);

        progress?.Report("烘焙绘制批次…");
        ct.ThrowIfCancellationRequested();
        var baked = GeometryBaker.Bake(entities, ct);

        var extents = ComputeExtents(entities);
        var coreExtents = ComputeCoreExtents(entities, HeaderModelSpaceRect(doc));
        if (converter.ActiveSpace != "Model")
            progress?.Report($"图纸空间：{converter.ActiveSpace}（视口裁剪 {converter.ClippedAway} 条）");
        progress?.Report(baked is null
            ? $"完成：{entities.Count} 个实体，跳过 {converter.Skipped}"
            : $"完成：{entities.Count} 个实体，跳过 {converter.Skipped}，"
              + $"合并为 {baked.BucketCount} 个绘制批次（{baked.BakeMs:F0} ms）");
        if (converter.DroppedNoBlock + converter.DroppedByDepth + converter.DroppedByMatrix + converter.DroppedByMInsert > 0)
            progress?.Report($"块展开丢弃：无块 {converter.DroppedNoBlock}，超深 {converter.DroppedByDepth}，"
                             + $"矩阵失败 {converter.DroppedByMatrix}，阵列超限 {converter.DroppedByMInsert}");
        if (converter.AttributeDrawn + converter.AttributeInvisible + converter.AttributeDefsSkipped > 0)
            progress?.Report($"块属性：绘制 {converter.AttributeDrawn}，不可见 {converter.AttributeInvisible}，"
                             + $"跳过占位符 {converter.AttributeDefsSkipped}");
        if (converter.LineWeightEntities > 0)
            progress?.Report($"线宽：{converter.LineWeightEntities} 个实体带线宽（状态栏「显示线宽」打开后生效）");
        if (converter.RegionEntities + converter.RegionUnresolved > 0)
            progress?.Report($"REGION：{converter.RegionEntities} 个重建轮廓（{converter.RegionLoops} 环）"
                             + (converter.RegionUnresolved > 0
                                ? $"，{converter.RegionUnresolved} 个无法解析（ACIS 载荷异常）" : ""));
        if (converter.ShxTextEntities + converter.ShxTextMissingFont > 0)
            progress?.Report($"SHX 文字：{converter.ShxTextEntities} 个实体按字库描线"
                             + (converter.ShxTextMissingFont > 0
                                ? $"，{converter.ShxTextMissingFont} 条文字缺字库（回退 TTF）" : ""));
        if (converter.NonPlottingLayers > 0)
            progress?.Report($"不打印图层：{converter.NonPlottingLayers} 个已默认隐藏"
                             + "（对应 AutoCAD 的 DEFPOINTS，可在图层面板打开）");
        if (converter.RationalSplineEntities > 0)
            progress?.Report($"有理样条：{converter.RationalSplineEntities} 条（按权重求值）");
        if (converter.WidePolylineEntities > 0)
            progress?.Report($"宽多段线：{converter.WidePolylineEntities} 条（按宽度轮廓填充）");
        if (converter.PointMarkEntities + converter.PointMarksHidden > 0)
            progress?.Report($"点标记：{converter.PointMarkEntities} 个"
                             + $"（PDMODE={converter.PointDisplayMode}, PDSIZE={converter.PointDisplaySize:G4}）"
                             + (converter.PointMarksHidden > 0 ? $"，PDMODE=1 不画 {converter.PointMarksHidden} 个" : ""));

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
            CoreExtents = coreExtents,
            Baked = baked,
            InitiallyHiddenLayers = converter.InitiallyHiddenLayers,
            LayoutNames = converter.LayoutNames,
            ActiveSpace = converter.ActiveSpace,
            SpaceNotes = converter.SpaceNotes,
            AttributesDrawn = converter.AttributeDrawn,
            AttributesInvisible = converter.AttributeInvisible,
            AttributeDefsSkipped = converter.AttributeDefsSkipped,
            LineWeightEntities = converter.LineWeightEntities,
            WidePolylineEntities = converter.WidePolylineEntities,
            PointMarkEntities = converter.PointMarkEntities,
            RegionEntities = converter.RegionEntities,
            RegionLoops = converter.RegionLoops,
            RegionUnresolved = converter.RegionUnresolved,
            WorldUnitsPerMm = converter.WorldUnitsPerMm,
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

    /// <summary>
    /// 鲁棒范围（核心区）：供「适应窗口」首屏定位，绝大多数图纸与 <see cref="ComputeExtents"/>
    /// 的结果一致。部分图纸含有位于 1e5~1e10 外的实体（INSERT 插入点垃圾值，DWG 原数据
    /// 如此，AutoCAD 的 ZOOM EXTENTS 同样被撑爆），按全量范围适应会把画面压到只剩一个点。
    /// 这里迭代切掉孤立簇（判据见 <see cref="TryCutSparseSide"/>），保留主密集区；
    /// 判据都不满足时原样保留（含两端详图的狭长排布等正常图纸不受影响）。
    /// 索引 / 句柄查询 / 打印范围仍用全量 <c>Extents</c>。
    /// </summary>
    private static SKRect ComputeCoreExtents(IReadOnlyList<CadEntity> entities, SKRect? fileRect)
    {
        var boxes = new List<SKRect>(entities.Count);
        foreach (var e in entities)
        {
            var b = e.Bounds;
            if (float.IsNaN(b.Left) || float.IsNaN(b.Top) || float.IsNaN(b.Right) || float.IsNaN(b.Bottom)) continue;
            if (float.IsInfinity(b.Left) || float.IsInfinity(b.Top) || float.IsInfinity(b.Right) || float.IsInfinity(b.Bottom)) continue;
            // 无渲染内容的实体不参与：空实体常位于 (0,0) 等默认坐标，会把范围与切割都带偏
            if (e.Path == null && e.FillPath == null && e.Text == null) continue;
            boxes.Add(b);
        }

        // 优先按文件存储范围取簇：存盘时的范围是"当时图纸本体"的权威边界，天然不含后来
        // 被插件/复制操作甩到远处的几何。只在切掉的确实是"远处垃圾"时才采用
        // （保留区面积不足全量的 1/5），否则轻微裁剪反而让首屏构图偏离存储范围。
        if (fileRect is { } fr && TryKeepInside(boxes, fr, out var inside))
        {
            var full = UnionOf(boxes);
            double areaInside = (double)inside.Width * inside.Height;
            double areaFull = (double)full.Width * full.Height;
            if (areaFull > 0 && areaInside <= areaFull * 0.2) return inside;
        }

        return CoreOf(boxes);
    }

    /// <summary>文件头记录的模型空间范围（EXTMIN/EXTMAX）；不可用（未填/退化）时返回 null。</summary>
    private static SKRect? HeaderModelSpaceRect(ACadSharp.CadDocument doc)
    {
        try
        {
            var lo = doc.Header.ModelSpaceExtMin;
            var hi = doc.Header.ModelSpaceExtMax;
            float l = (float)lo.X, t = (float)lo.Y, r = (float)hi.X, b = (float)hi.Y;
            if (float.IsNaN(l) || float.IsNaN(t) || float.IsNaN(r) || float.IsNaN(b)) return null;
            if (float.IsInfinity(l) || float.IsInfinity(t) || float.IsInfinity(r) || float.IsInfinity(b)) return null;
            // 退化（空图未存范围）与反向包围盒都不作为锚点
            if (r - l <= 1e-3f || b - t <= 1e-3f) return null;
            return SKRect.Create(l, t, r - l, b - t);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 只保留与给定范围相交的实体并求并集。保留量不足一半时判定该范围不可信（存盘后新增了
    /// 大量内容导致范围过旧）而放弃，交回 <see cref="CoreOf"/> 的通用切分。
    /// </summary>
    private static bool TryKeepInside(List<SKRect> boxes, SKRect rect, out SKRect result)
    {
        result = default;
        if (boxes.Count == 0) return false;
        var keep = new List<SKRect>(boxes.Count);
        foreach (var b in boxes)
        {
            if (b.Right < rect.Left || b.Left > rect.Right || b.Bottom < rect.Top || b.Top > rect.Bottom) continue;
            keep.Add(b);
        }
        if (keep.Count < 8 || keep.Count * 2 < boxes.Count) return false;
        result = UnionOf(keep);
        return result.Width > 1e-3f && result.Height > 1e-3f;
    }

    private static SKRect CoreOf(List<SKRect> boxes)
    {
        if (boxes.Count < 16) return UnionOf(boxes);
        for (int round = 0; round < 2; round++)
        {
            if (!TryCutSparseSide(boxes, out var keep)) break;
            boxes = keep;
        }
        return UnionOf(boxes);
    }

    /// <summary>
    /// 在最大空隙处把实体切成两簇，切掉一簇、保留另一簇。
    /// 触发判据（满足其一）：空隙占该轴中心跨度的比例 ≥ 0.85 且少数侧实体占比 &lt; 25%
    /// （极端远处垃圾）；空隙占比 ≥ 0.5 且少数侧实体占比 &lt; 5%（中距离稀疏小簇）。
    /// 两端详图等正常排布两侧体量都超过 5%，不会误切。
    /// 保留侧取**实体包围盒面积总和**更大的一侧：数量多的一侧往往是被展开的垃圾块引用
    /// （实测有 3518/3526 个实体全在 5 亿外的图），数量判据不可靠。
    /// </summary>
    private static bool TryCutSparseSide(List<SKRect> boxes, out List<SKRect> keep)
    {
        keep = boxes;
        var (ratioX, cutX, orderX) = MaxCenterGap(boxes, axisX: true);
        var (ratioY, cutY, orderY) = MaxCenterGap(boxes, axisX: false);
        double ratio = Math.Max(ratioX, ratioY);
        if (ratio < 0.5) return false;
        bool useX = ratioX >= ratioY;
        var order = useX ? orderX : orderY;
        int cut = useX ? cutX : cutY;
        int n = boxes.Count;
        double minorityShare = Math.Min(cut, n - cut) / (double)n;
        bool isolated = (ratio >= 0.85 && minorityShare < 0.25) || (ratio >= 0.5 && minorityShare < 0.05);
        if (!isolated) return false;

        var left = order.Take(cut).Select(i => boxes[i]).ToList();
        var right = order.Skip(cut).Select(i => boxes[i]).ToList();
        double areaL = left.Sum(b => (double)b.Width * b.Height);
        double areaR = right.Sum(b => (double)b.Width * b.Height);
        keep = areaL >= areaR ? left : right;
        return keep.Count >= 8;   // 保留侧只剩孤点 → 放弃切割
    }

    /// <summary>按实体中心坐标排序后找最大相邻间隙；返回 (间隙/跨度, 切分索引, 排序索引表)。</summary>
    private static (double ratio, int cut, List<int> order) MaxCenterGap(List<SKRect> boxes, bool axisX)
    {
        double Key(int i) => axisX ? (boxes[i].Left + boxes[i].Right) * 0.5f : (boxes[i].Top + boxes[i].Bottom) * 0.5f;
        var order = Enumerable.Range(0, boxes.Count).OrderBy(Key).ToList();
        if (order.Count < 2) return (0, 0, order);
        double span = Key(order[^1]) - Key(order[0]);
        if (span <= 0) return (0, 0, order);
        double bestGap = 0;
        int best = 0;
        for (int i = 1; i < order.Count; i++)
        {
            double g = Key(order[i]) - Key(order[i - 1]);
            if (g > bestGap) { bestGap = g; best = i; }
        }
        return (bestGap / span, best, order);
    }

    private static SKRect UnionOf(List<SKRect> boxes)
    {
        if (boxes.Count == 0) return SKRect.Create(0, 0, 100, 100);
        float l = float.MaxValue, t = float.MaxValue, r = float.MinValue, b = float.MinValue;
        foreach (var box in boxes)
        {
            l = MathF.Min(l, box.Left); t = MathF.Min(t, box.Top);
            r = MathF.Max(r, box.Right); b = MathF.Max(b, box.Bottom);
        }
        return SKRect.Create(l, t, MathF.Max(r - l, 1e-3f), MathF.Max(b - t, 1e-3f));
    }
}
