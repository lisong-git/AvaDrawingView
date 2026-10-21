using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Objects;
using ACadSharp.Tables;
using CSMath;
using SkiaSharp;

namespace DrawingView.Core;

public sealed class GeometryConvertOptions
{
    public bool SkipRootAnnotations { get; init; }

    /// <summary>
    /// 要渲染的布局名（大小写不敏感）。null = 模型空间。
    /// 指定布局时按图纸空间渲染：先画布局自身几何（图框/标题栏），
    /// 再按每个视口的「视口中心/视高/扭转角」把模型空间几何变换到图纸坐标并裁剪到视口矩形。
    /// </summary>
    public string? LayoutName { get; init; }

    /// <summary>
    /// 模型空间为空时自动切到第一个有内容的布局。
    /// Inventor / SolidWorks 导出的机械图模型空间本来就是空的（几何全在布局的视图块里），
    /// 不开这个开关的表现是"整张白屏"，比"渲染了布局"更容易被误判成打不开。
    /// </summary>
    public bool AutoPaperSpaceWhenModelEmpty { get; init; } = true;
}

/// <summary>
/// 几何转换器：ACadSharp 实体 → 显示基元（SKPath / 文本）。
/// 单个实体转换失败时跳过并计数，不影响整图。
/// 圆/弧用圆锥曲线构造（分辨率无关），椭圆与样条为压平折线。
/// </summary>
public sealed class GeometryConverter
{
    /// <summary>块引用递归深度上限（防止损坏文件造成无限递归）。取值与标注树共用（CadTransform）。</summary>
    private const int MaxInsertDepth = CadTransform.MaxInsertDepth;

    /// <summary>MINSERT 阵列实例总数上限（防止异常文件生成天量几何）。与标注树共用。</summary>
    private const int MaxMInsertInstances = CadTransform.MaxMInsertInstances;

    /// <summary>每处理这么多实体检查一次取消请求（大图取消要能及时响应）。</summary>
    private const int CancellationCheckInterval = 1024;

    private readonly List<CadEntity> _entities = new();
    private readonly Dictionary<string, uint> _layerColors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _layerWeights = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _layerOrder = new();
    private readonly HashSet<string> _initiallyHidden = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>当前正在转换的实体句柄（块引用展开的子实体继承块引用句柄）。</summary>
    private ulong _currentHandle;

    /// <summary>
    /// 当前正在转换的实体**自身**句柄（不被块引用覆盖）。
    /// 与 <see cref="_currentHandle"/> 配对写进 CadEntity，用于还原块内子实体身份。
    /// </summary>
    private ulong _currentOwnHandle;

    /// <summary>全局线型比例（LTSCALE，文件头）；<=0 时按 1。</summary>
    private double _globalLtScale = 1.0;

    /// <summary>
    /// POINT 的显示模式与尺寸（文件头变量 PDMODE / PDSIZE）。
    /// 二者都是**整张图**级别的设置，实体本身不带（DXF 里 POINT 没有自己的模式字段）。
    /// </summary>
    private int _pointDisplayMode;
    private float _pointDisplaySize;

    /// <summary>因 PDMODE=1 而画不出来的点数。</summary>
    public int PointMarksHidden { get; private set; }

    /// <summary>标记为"不打印"的图层数（DEFPOINTS 一类；默认隐藏，见 <see cref="CacheLayerColors"/>）。</summary>
    public int NonPlottingLayers { get; private set; }
    /// <summary>产出点标记的实体数。</summary>
    public int PointMarkEntities { get; private set; }

    /// <summary>成功重建出轮廓的 REGION 实体数。</summary>
    public int RegionEntities { get; private set; }

    /// <summary>REGION 重建出的环总数（外环 + 内环）。</summary>
    public int RegionLoops { get; private set; }

    /// <summary>ACIS 载荷无法重建轮廓、只能跳过的 REGION 数。</summary>
    public int RegionUnresolved { get; private set; }
    /// <summary>文件头的 PDMODE（诊断用：0 点 / 1 不画 / 2 十字 / 3 叉 / 4 竖线，+32 圆 +64 方）。</summary>
    public int PointDisplayMode => _pointDisplayMode;
    /// <summary>文件头的 PDSIZE（诊断用：&gt;0 世界单位 / &lt;0 视高百分比 / =0 默认 5%）。</summary>
    public float PointDisplaySize => _pointDisplaySize;

    /// <summary>虚线间隔数组池：等值的线型共用同一实例，便于烘焙按引用分桶。</summary>
    private readonly Dictionary<string, float[]> _dashPool = new(StringComparer.Ordinal);

    /// <summary>转换失败 / 有意跳过的实体数（含隐藏实体）。</summary>
    public int Skipped { get; private set; }

    /// <summary>视口裁剪掉的实体数（布局渲染诊断用）。</summary>
    public int ClippedAway { get; private set; }

    /// <summary>实际渲染的空间名（"Model" 或布局名）。</summary>
    public string ActiveSpace { get; private set; } = "Model";

    /// <summary>图纸里可切换的布局名（按 TabOrder 排序，不含模型空间）。</summary>
    public IReadOnlyList<string> LayoutNames { get; private set; } = Array.Empty<string>();

    /// <summary>视口内容的裁剪矩形（图纸坐标）；null = 不裁剪（模型空间或布局自身几何）。</summary>
    private SKRect? _clipRect;

    /// <summary>当前视口冻结的图层（AutoCAD：视口冻结只影响该视口）。</summary>
    private HashSet<string>? _clipFrozenLayers;

    /// <summary>
    /// 布局里的视口（先收集，最后统一按视口画模型内容）。
    /// 带上「视口所在坐标系 → 图纸」的矩阵：图框成块时视口藏在 INSERT 里，
    /// 只认顶层 Viewport 会漏掉绝大多数视口（实测一张 47 视口的图只认出 1 个）。
    /// </summary>
    private readonly List<(Viewport Vp, SKMatrix ToPaper)> _viewports = new();

    private readonly List<string> _spaceNotes = new();

    /// <summary>布局/视口诊断（仅在 DrawingView_CONV_TRACE=1 时填充）。</summary>
    public IReadOnlyList<string> SpaceNotes => _spaceNotes;

    // 块引用展开的丢弃原因计数（诊断用：定位「块内容整片消失」）
    public int DroppedNoBlock { get; private set; }
    public int DroppedByDepth { get; private set; }
    public int DroppedByMatrix { get; private set; }
    public int DroppedByMInsert { get; private set; }

    // 块属性（ATTRIB）计数（诊断用：区分「块里没文字」与「属性没画出来」）
    /// <summary>已绘制的属性值条数。</summary>
    public int AttributeDrawn { get; private set; }
    /// <summary>因不可见（实体 60 / 属性标志位 1）而跳过的属性条数。</summary>
    public int AttributeInvisible { get; private set; }
    /// <summary>块定义里的占位符（AttributeDefinition）条数——这些**不应该**出现在图面上。</summary>
    public int AttributeDefsSkipped { get; private set; }

    /// <summary>填充里被判定为非法（未闭合）而忽略的边界环数。</summary>
    public int HatchBadLoops { get; private set; }
    /// <summary>填充里完全重合、被去重掉的边界环数。</summary>
    public int HatchDuplicateLoops { get; private set; }

    /// <summary>
    /// 当前块引用上下文里的「随块（ByBlock）」线宽（1/100 mm，&gt;0 才算有值）。
    /// 块内实体写 BYBLOCK 时继承它，与 <c>_inheritedColor</c> 的语义完全对称。
    /// </summary>
    private int _inheritedLineWeight;

    /// <summary>解析出非零线宽的实体数（诊断用：判断线宽开关到底有没有东西可画）。</summary>
    public int LineWeightEntities { get; private set; }

    /// <summary>带宽度（几何宽度，不是线宽）的多段线实体数——这些画成填充轮廓。</summary>
    public int WidePolylineEntities { get; private set; }

    /// <summary>图纸里被关闭或冻结的图层：打开时应默认隐藏。</summary>
    public IReadOnlyCollection<string> InitiallyHiddenLayers => _initiallyHidden;

    private static readonly bool ConvTrace = Environment.GetEnvironmentVariable("DrawingView_CONV_TRACE") == "1";
    private static readonly bool ShowInvisible = Environment.GetEnvironmentVariable("DrawingView_SHOW_INVISIBLE") == "1";

    /// <summary>
    /// REGION 轮廓重建开关（默认开）。置 <c>DrawingView_NO_REGION=1</c> 可关掉，
    /// 用于「有/无 REGION」的像素对照 —— 判断渲染差异到底来自 REGION 还是别的东西。
    /// </summary>
    private static readonly bool ShowRegions = Environment.GetEnvironmentVariable("DrawingView_NO_REGION") != "1";
    private static readonly string TracePath = Path.Combine(Path.GetTempPath(), "conv_trace.log");
    private static int _traceSeq;

    private static void Trace(string m)
    {
        if (!ConvTrace) return;
        try
        {
            File.AppendAllText(TracePath, $"[{_traceSeq++}] {DateTime.Now:HH:mm:ss.fff} {m}{Environment.NewLine}");
        }
        catch { }
    }

    public List<CadEntity> Convert(CadDocument doc, CancellationToken ct = default, GeometryConvertOptions? options = null)
    {
        _entities.Clear();
        _layerColors.Clear();
        _layerOrder.Clear();
        _initiallyHidden.Clear();
        _viewports.Clear();
        _clipRect = null;
        _clipFrozenLayers = null;
        ClippedAway = 0;
        Skipped = 0;
        AttributeDrawn = 0;
        AttributeInvisible = 0;
        AttributeDefsSkipped = 0;
        HatchBadLoops = 0;
        HatchDuplicateLoops = 0;
        LineWeightEntities = 0;
        WidePolylineEntities = 0;
        _inheritedLineWeight = 0;
        PointMarksHidden = 0;
        PointMarkEntities = 0;
        RationalSplineEntities = 0;
        NonPlottingLayers = 0;
        RegionEntities = 0;
        RegionLoops = 0;
        RegionUnresolved = 0;
        _pointDisplayMode = 0;
        _pointDisplaySize = 0f;

        CacheLayerColors(doc);
        try
        {
            double lts = doc.Header.LineTypeScale;
            if (lts > 0 && !double.IsNaN(lts) && !double.IsInfinity(lts)) _globalLtScale = lts;
        }
        catch { }
        try
        {
            // PDMODE / PDSIZE：不读的后果是"点全按同一个固定十字画"，
            // 缩放后要么看不见要么糊满屏（见 PointMarkStyle 的说明）。
            _pointDisplayMode = (int)doc.Header.PointDisplayMode;
            _pointDisplaySize = (float)doc.Header.PointDisplaySize;
            if (!float.IsFinite(_pointDisplaySize)) _pointDisplaySize = 0f;
        }
        catch { }

        if (ConvTrace)
        {
            try { File.Delete(TracePath); } catch { }
            _traceSeq = 0;
        }

        var layouts = EnumerateLayouts(doc);
        LayoutNames = layouts.Select(l => l.Name ?? string.Empty).Where(n => n.Length > 0).ToArray();

        var target = ResolveLayout(doc, layouts, options);
        if (target is not null)
        {
            ActiveSpace = target.Name ?? "Layout";
            ConvertLayout(doc, target, ct, options);
        }
        else
        {
            ActiveSpace = "Model";
            int n = 0;
            foreach (var e in EnumerateModelSpace(doc))
            {
                // 大图解析后仍有十几万实体要转换，必须让取消能真正中断（原来只在阶段之间检查）
                if ((++n % CancellationCheckInterval) == 0) ct.ThrowIfCancellationRequested();
                if (options?.SkipRootAnnotations == true && IsRootAnnotation(e)) { Skipped++; continue; }
                ConvertEntity(e, SKMatrix.Identity, 0, inheritedColor: null);
            }
        }

        DropUnreferencedPlaceholderLayers();

        return _entities;
    }

    /// <summary>本图出现的图层名（保持图纸中的出现顺序）。</summary>
    public IReadOnlyList<string> LayerNames => _layerOrder.ToArray();

    // ---------- 布局（图纸空间） ----------

    /// <summary>图纸里的布局（按 TabOrder 排序，排除模型空间布局）。</summary>
    private static List<Layout> EnumerateLayouts(CadDocument doc)
    {
        var result = new List<Layout>();
        try
        {
            foreach (var l in doc.Layouts)
            {
                if (l is null) continue;
                // 模型空间也是一个 Layout 对象（名字通常叫 "Model"），不要当成可切换的图纸
                if (!l.IsPaperSpace) continue;
                result.Add(l);
            }
        }
        catch { /* 个别文件布局表损坏：当作没有布局，退回模型空间 */ }
        return result.OrderBy(l => l.TabOrder).ToList();
    }

    /// <summary>
    /// 选目标空间：显式指定的布局 &gt; 模型空间（非空）&gt; 第一个有视口内容的布局。
    /// 最后这一条是"整张白屏"的解药：Inventor 导出的机械图模型空间 0 实体，
    /// 几何全在布局的视图块里，不自动切过去就什么都看不见。
    /// </summary>
    private static Layout? ResolveLayout(CadDocument doc, List<Layout> layouts, GeometryConvertOptions? options)
    {
        if (!string.IsNullOrWhiteSpace(options?.LayoutName))
        {
            var wanted = options!.LayoutName!;
            return layouts.FirstOrDefault(l => string.Equals(l.Name, wanted, StringComparison.OrdinalIgnoreCase));
        }

        if (options?.AutoPaperSpaceWhenModelEmpty == false) return null;

        bool modelEmpty;
        try { modelEmpty = doc.ModelSpace?.Entities is null || !doc.ModelSpace.Entities.Any(); }
        catch { modelEmpty = true; }
        if (!modelEmpty) return null;

        // 有视口（Id != 1 且尺寸有效）= 该布局确实"看得见东西"
        return layouts.FirstOrDefault(l =>
        {
            try
            {
                return l.Viewports.Any(v => v is not null && v.Id != 1 && v.Width > 1e-6 && v.Height > 1e-6);
            }
            catch { return false; }
        });
    }

    /// <summary>
    /// 图纸空间转换：先画布局自身几何（图框、标题栏、视图块引用），
    /// 再逐个视口把模型空间几何变换到图纸坐标并裁剪。
    /// </summary>
    private void ConvertLayout(CadDocument doc, Layout layout, CancellationToken ct, GeometryConvertOptions? options)
    {
        _viewports.Clear();
        _spaceNotes.Clear();

        var block = layout.AssociatedBlock;
        if (block?.Entities is not null)
        {
            int n = 0;
            foreach (var e in block.Entities)
            {
                if ((++n % CancellationCheckInterval) == 0) ct.ThrowIfCancellationRequested();
                // 视口自身不画边框：AutoCAD 里视口边框属于非打印的 Defpoints 语义，
                // 打印/出图看不到；画出来反而在图纸四周多出一圈不该有的矩形。
                if (e is Viewport vp) { if (vp.Id != 1) _viewports.Add((vp, SKMatrix.Identity)); continue; }
                if (options?.SkipRootAnnotations == true && IsRootAnnotation(e)) { Skipped++; continue; }
                ConvertEntity(e, SKMatrix.Identity, 0, inheritedColor: null);
            }
        }

        var model = doc.ModelSpace;
        if (model?.Entities is null) return;

        foreach (var (vp, toPaper) in _viewports)
            ConvertViewport(model, vp, toPaper, ct);
    }

    /// <summary>
    /// 单个视口：模型空间几何 → 图纸坐标。
    ///
    /// 变换链（世界→图纸）：先平移到视口视图中心，按 <c>视高/视口高</c> 缩放，
    /// 按扭转角旋转，再平移到视口中心；最后裁剪到视口矩形。
    /// <para>视口中心是图纸坐标（mm），视高是模型单位，二者之比就是出图比例。</para>
    /// </summary>
    private void ConvertViewport(BlockRecord model, Viewport vp, in SKMatrix toPaper, CancellationToken ct)
    {
        if (!LayoutProjection.IsDrawable(vp)) return;

        // 模型 → 视口所在坐标系 → 图纸（视口可能挂在图框块里）
        var m = Mul(LayoutProjection.ModelToPaper(vp), toPaper);
        var clip = LayoutProjection.TransformRect(LayoutProjection.PaperRect(vp), toPaper);

        HashSet<string>? frozen = null;
        try
        {
            if (vp.FrozenLayers is { Count: > 0 })
                frozen = new HashSet<string>(vp.FrozenLayers.Select(l => l.Name ?? string.Empty),
                                             StringComparer.OrdinalIgnoreCase);
        }
        catch { }

        _clipRect = clip;
        _clipFrozenLayers = frozen;
        int before = _entities.Count;
        try
        {
            int n = 0;
            foreach (var e in model.Entities)
            {
                if ((++n % CancellationCheckInterval) == 0) ct.ThrowIfCancellationRequested();
                ConvertEntity(e, m, 0, inheritedColor: null);
            }
        }
        finally
        {
            _clipRect = null;
            _clipFrozenLayers = null;
        }
        _spaceNotes.Add($"vp id={vp.Id} paper={LayoutProjection.PaperRect(vp)} " +
                            $"viewH={vp.ViewHeight:F1} scale={LayoutProjection.Scale(vp):F4} " +
                            $"clip={clip} kept={_entities.Count - before}");
    }

    /// <summary>把路径裁到当前视口矩形；返回 null 表示被裁没了。</summary>
    private SKPath? ApplyViewportClip(SKPath path)
    {
        if (_clipRect is not { } rect) return path;
        using var rectPath = new SKPath();
        rectPath.AddRect(rect);
        var clipped = path.Op(rectPath, SKPathOp.Intersect);
        path.Dispose();
        if (clipped is null || clipped.IsEmpty)
        {
            clipped?.Dispose();
            ClippedAway++;
            return null;
        }
        return clipped;
    }

    private bool InsideClip(SKRect bounds)
        => _clipRect is not { } rect || bounds.IntersectsWith(rect);

    private bool IsFrozenInViewport(Entity e)
        => _clipFrozenLayers is { Count: > 0 } set && set.Contains(LayerName(e));

    // ---------- 入口 ----------

    private static IEnumerable<Entity> EnumerateModelSpace(CadDocument doc)
    {
        var ms = doc.ModelSpace;
        if (ms?.Entities != null)
            foreach (var e in ms.Entities)
                yield return e;
    }

    private static bool IsRootAnnotation(Entity e)
        => e is Dimension || e is Leader || e is TextEntity || e is MText;

    /// <param name="inheritedColor">
    /// 块内实体的 BYBLOCK 继承色：由外层 INSERT 解析后的颜色传入。
    /// 为 null 表示当前不在块内（此时 BYBLOCK 无意义，退化为白色，与 AutoCAD 一致）。
    /// </param>
    /// <param name="handleOverride">
    /// 句柄覆盖：块引用展开时传入块引用句柄，使点选块内任一图元都能定位/标注到块引用整体
    /// （与 WPF 版按 BlockReference 建标注树的语义一致）。嵌套块保持最外层句柄。
    /// </param>
    private void ConvertEntity(Entity e, in SKMatrix parent, int depth, uint? inheritedColor, ulong? handleOverride = null)
    {
        // 实体被显式隐藏（DXF 组码 60 = 1，AutoCAD 亦不显示）。
        // 实测测试图.dwg：7836 个源实体带此标记（其中 1734 个 INSERT），展开约 5 万个显示实体；
        // 过滤后全图墨迹覆盖率 3.06% → 2.03%，经与 AutoCAD 语义核对属于正确行为而非内容丢失。
        // DrawingView_SHOW_INVISIBLE=1 可关闭过滤（仅供与第三方渲染器对照基准用）。
        if (e.IsInvisible && !ShowInvisible)
        {
            // 属性有独立的不可见标志（组码 70 bit 1 或实体 60），单独计数便于诊断
            // "块里属性没显示"到底是不可见、还是没画。
            if (e is AttributeEntity) AttributeInvisible++;
            Skipped++;
            return;
        }

        // 视口冻结（VPFREEZE）：只在该视口内不显示，与全局图层冻结不同
        if (IsFrozenInViewport(e)) { Skipped++; return; }

        var prevHandle = _currentHandle;
        var prevOwnHandle = _currentOwnHandle;
        _currentHandle = handleOverride ?? e.Handle;
        _currentOwnHandle = e.Handle;

        if (ConvTrace)
            Trace($"depth={depth} {e.GetType().Name} H={e.Handle:X} L={e.Layer?.Name}");

        try
        {
            uint color = ResolveColor(e, inheritedColor);
            switch (e)
            {
                case Line l: AddLine(l, parent, color); break;
                case Arc a: AddArc(a, parent, color); break;
                case Circle c: AddCircle(c, parent, color); break;
                case Ellipse el: AddEllipse(el, parent, color); break;
                case LwPolyline lw: AddLwPolyline(lw, parent, color); break;
                case Polyline2D p2: AddPolyline2D(p2, parent, color); break;
                case Spline sp: AddSpline(sp, parent, color); break;
                case MText mt:
                {
                    // MText 与 TextEntity 是兄弟类型（is TextEntity 匹配不到 MText，2026-09-25 SmokeTest 实证）
                    var raw = !string.IsNullOrEmpty(mt.PlainText) ? mt.PlainText : CleanMText(mt.Value);
                    AddMText(mt, raw, parent, color);
                    break;
                }
                // 块定义里的属性**占位符**（AttributeDefinition）不画：块参照显示的是 ATTRIB 的值，
                // 占位符（TAG）只在块编辑器里可见。它继承自 TextEntity，不拦住会被当成正文画出来。
                case AttributeDefinition:
                    AttributeDefsSkipped++;
                    break;
                // 属性值（ATTRIB）：ConvertInsert 会把 Insert.Attributes 按实例送进来，
                // 位置/字高随 INSERT 变换，与块内正文走同一条文本管线。
                case AttributeEntity att:
                    if (att.Flags.HasFlag(AttributeFlags.Hidden)) { AttributeInvisible++; break; }
                    AddText(att, parent, color);
                    AttributeDrawn++;
                    break;
                case TextEntity t: AddText(t, parent, color); break;
                case Insert ins: ConvertInsert(ins, parent, depth, inheritedColor); break;
                case Viewport vp:
                    // 图框常被做成块，视口就藏在块里：记下「视口坐标系→图纸」的矩阵，
                    // 布局收尾时统一展开。视口本身不画边框（同顶层视口）。
                    if (vp.Id != 1) _viewports.Add((vp, parent));
                    break;
                case Hatch h: AddHatch(h, parent, color); break;
                case Solid sol: AddSolid(sol, parent, color); break;
                case Leader ld: AddLeader(ld, parent, color); break;
                case Dimension dim: AddDimensionBlock(dim, parent, depth, inheritedColor); break;
                case ACadSharp.Entities.Point pt: AddPointCross(pt, parent, color); break;
                // REGION：ACadSharp 只给 ACIS 载荷（R2013+ 是二进制 SAB），几何要从 B-rep 拓扑重建，
                // 见 RegionOutline（P2-7）。3DSOLID/BODY/SURFACE 同为 ModelerGeometry，暂不处理：
                // 三维实体的线框投影到 XY 会得到一堆互相穿插的边，反而干扰识图。
                case Region rg: AddRegion(rg, parent, color); break;
                default: Skipped++; break;
            }
        }
        catch (Exception ex)
        {
            Skipped++;
            if (ConvTrace) Trace($"THROW depth={depth} H={_currentHandle:X} {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _currentHandle = prevHandle;
            _currentOwnHandle = prevOwnHandle;
        }
    }

    // ---------- 基础实体 ----------

    private void AddLine(Line l, in SKMatrix m, uint color)
    {
        var path = new SKPath();
        path.MoveTo((float)l.StartPoint.X, (float)l.StartPoint.Y);
        path.LineTo((float)l.EndPoint.X, (float)l.EndPoint.Y);
        AddStroke(path, l, color, m);
    }

    private void AddCircle(Circle c, in SKMatrix m, uint color)
    {
        var path = new SKPath();
        path.AddCircle((float)c.Center.X, (float)c.Center.Y, (float)c.Radius, SKPathDirection.Clockwise);
        AddStroke(path, c, color, m);
    }

    private void AddArc(Arc a, in SKMatrix m, uint color)
    {
        var path = new SKPath();
        var oval = CadGeometry.OvalRect(a.Center.X, a.Center.Y, a.Radius);
        CadGeometry.AppendArcTo(path, oval, a.StartAngle, CadGeometry.ArcSweep(a.StartAngle, a.EndAngle));
        AddStroke(path, a, color, m);
    }

    private void AddEllipse(Ellipse el, in SKMatrix m, uint color)
    {
        // p(t) = center + A·cos(t) + B·sin(t)，A = 长轴端点向量，B = 左法线 × 半径比
        var path = CadGeometry.BuildEllipticalArc(el.Center.X, el.Center.Y,
            el.MajorAxisEndPoint.X, el.MajorAxisEndPoint.Y,
            -el.MajorAxisEndPoint.Y * el.RadiusRatio,
            el.MajorAxisEndPoint.X * el.RadiusRatio,
            el.StartParameter, el.EndParameter);
        if (path is null) { Skipped++; return; }
        AddStroke(path, el, color, m);
    }

    /// <summary>
    /// LWPOLYLINE，带**宽度轮廓**（B4）。
    ///
    /// <para>宽度是几何不是线宽：有宽度时生成闭合轮廓交给填充（并与 AutoCAD 一致地忽略线宽），
    /// 全段宽度为 0 时才退回描边。宽度取值遵循 DXF 口径——组码 40/41 挂在**起点顶点**上，
    /// 描述"从该顶点出发那一段"的起/终宽度；两者都为 0 时回落到组码 43（ConstantWidth）。</para>
    /// </summary>
    private void AddLwPolyline(LwPolyline lw, in SKMatrix m, uint color)
    {
        if (lw.Vertices == null || lw.Vertices.Count == 0) { Skipped++; return; }
        float constant = (float)lw.ConstantWidth;

        if (TryAddWidePolyline(lw.Vertices.Count, lw.IsClosed, i =>
            {
                var v = lw.Vertices[i];
                var next = lw.Vertices[(i + 1) % lw.Vertices.Count];
                var (w0, w1) = ResolveSegmentWidths((float)v.StartWidth, (float)v.EndWidth, constant);
                return new WidePolyline.Piece(
                    new SKPoint((float)v.Location.X, (float)v.Location.Y),
                    new SKPoint((float)next.Location.X, (float)next.Location.Y),
                    w0, w1, v.Bulge);
            }, lw, color, m))
            return;

        var path = new SKPath();
        CadGeometry.AppendLwPolyline(path, lw);
        AddStroke(path, lw, color, m);
    }

    /// <summary>POLYLINE2D（老式折线），宽度口径同 <see cref="AddLwPolyline"/>。</summary>
    private void AddPolyline2D(Polyline2D p2, in SKMatrix m, uint color)
    {
        if (p2.Vertices == null || p2.Vertices.Count == 0) { Skipped++; return; }

        if (TryAddWidePolyline(p2.Vertices.Count, p2.IsClosed, i =>
            {
                var v = p2.Vertices[i];
                var next = p2.Vertices[(i + 1) % p2.Vertices.Count];
                var (w0, w1) = ResolveSegmentWidths((float)v.StartWidth, (float)v.EndWidth, 0f);
                return new WidePolyline.Piece(
                    new SKPoint((float)v.Location.X, (float)v.Location.Y),
                    new SKPoint((float)next.Location.X, (float)next.Location.Y),
                    w0, w1, v.Bulge);
            }, p2, color, m))
            return;

        var path = new SKPath();
        CadGeometry.AppendPolyline2D(path, p2);
        AddStroke(path, p2, color, m);
    }

    /// <summary>
    /// 每段的起/终宽度（世界单位）。DXF 组码 40/41 挂在起点顶点上；
    /// 只给了起点宽度（41=0）时按等宽处理，两者都 0 时用 ConstantWidth。
    /// </summary>
    private static (float Start, float End) ResolveSegmentWidths(float startWidth, float endWidth, float constantWidth)
    {
        if (startWidth <= 0f && endWidth <= 0f) return (constantWidth, constantWidth);
        if (endWidth <= 0f) return (startWidth, startWidth);
        return (startWidth, endWidth);
    }

    /// <summary>
    /// 有宽度就生成轮廓填充并计数；返回 false = 所有段宽度都是 0（调用方按普通折线描边）。
    /// </summary>
    private bool TryAddWidePolyline(int vertexCount, bool closed,
                                    Func<int, WidePolyline.Piece> pieceOf, Entity e, uint color, in SKMatrix m)
    {
        int segs = CadGeometry.VertexSegmentCount(vertexCount, closed);
        if (segs <= 0) return false;

        var pieces = new List<WidePolyline.Piece>(segs);
        bool anyWidth = false;
        for (int i = 0; i < segs; i++)
        {
            var pc = pieceOf(i);
            if (pc.StartWidth > 0f || pc.EndWidth > 0f) anyWidth = true;
            pieces.Add(pc);
        }
        if (!anyWidth) return false;

        var fill = WidePolyline.Build(pieces, closed);
        if (fill is null)
        {
            // 顶点太少/退化：仍然算"有宽度"，但画不出来，记一次跳过便于定位
            Skipped++;
            WidePolylineEntities++;
            return true;
        }
        AddFill(fill, e, color, m);
        WidePolylineEntities++;
        return true;
    }

    private void AddSpline(Spline sp, in SKMatrix m, uint color)
    {
        if (sp.ControlPoints == null || sp.ControlPoints.Count < 2) { Skipped++; return; }
        var cx = sp.ControlPoints.Select(p => p.X).ToArray();
        var cy = sp.ControlPoints.Select(p => p.Y).ToArray();
        var knots = sp.Knots?.ToArray() ?? Array.Empty<double>();

        // 权重（组码 41）与闭合标志（组码 70 的 bit1）都必须吃进来（P2-6）：
        //   有理权重是"精确画圆/椭圆"的唯一途径，忽略它形状会鼓/瘪；
        //   闭合样条不 Close 会少最后一段，看起来像"描边缺个口"。
        double[]? weights = sp.Weights is { Count: > 0 } w ? w.ToArray() : null;
        bool closed = sp.IsClosed;

        var path = SplineFlattener.Flatten(cx, cy, knots, sp.Degree, weights: weights, closed: closed);
        if (path is null) { Skipped++; return; }
        if (weights is not null) RationalSplineEntities++;
        AddStroke(path, sp, color, m);
    }

    /// <summary>按有理（带权重）求值的样条条数。</summary>
    public int RationalSplineEntities { get; private set; }

    /// <summary>
    /// REGION → 轮廓路径（P2-7）。
    ///
    /// <para>REGION 的几何不在实体字段里，而在 ACIS 的 B-rep 载荷中；ACadSharp 只把载荷原样
    /// 交出（R2013+ 是二进制 SAB，<c>GetAcisText()</c> 返回 null）。所以这里走
    /// <see cref="RegionOutline"/>，把 loop→coedge→edge→vertex→point 的链走一遍重建闭合轮廓。</para>
    ///
    /// <para>只描边、不填充：AutoCAD 在二维线框下就是这样显示 REGION 的，内环（洞）也天然正确。</para>
    /// </summary>
    private void AddRegion(Region rg, in SKMatrix m, uint color)
    {
        if (!ShowRegions) { Skipped++; return; }
        var loops = RegionOutline.TryBuild(rg.AcisData, out _);
        if (loops is null) { RegionUnresolved++; Skipped++; return; }

        int drawn = 0;
        foreach (var loop in loops)
        {
            if (loop.PointCount < 2) { loop.Dispose(); continue; }
            AddStroke(loop, rg, color, m);
            drawn++;
        }
        if (drawn == 0) { RegionUnresolved++; Skipped++; return; }
        RegionEntities++;
        RegionLoops += drawn;
    }

    private void AddLeader(Leader ld, in SKMatrix m, uint color)
    {
        if (ld.Vertices == null || ld.Vertices.Count == 0) { Skipped++; return; }
        var path = new SKPath();
        bool first = true;
        foreach (var v in ld.Vertices)
        {
            if (first) { path.MoveTo((float)v.X, (float)v.Y); first = false; }
            else path.LineTo((float)v.X, (float)v.Y);
        }
        AddStroke(path, ld, color, m);
    }

    /// <summary>
    /// POINT → 点标记（P2-3）。
    ///
    /// <para>原实现画固定 ±1 世界单位的十字：在 1:100 的图纸里那是 0.02mm，屏幕上永远看不见；
    /// 深放大时又变成糊满屏的巨大十字。根因是点符号的尺寸本质上是**屏幕尺寸**
    /// （PDSIZE 的两种主要口径都是视高百分比），世界坐标里没有唯一答案。</para>
    ///
    /// <para>所以这里只存 PDMODE/PDSIZE 原值（<see cref="PointMark"/>），
    /// 由渲染端按当前视口换算成像素再画。包围盒退化为一个点（AutoCAD 的 POINT
    /// 也不撑大图面范围），因此它不参与批次合并。</para>
    /// </summary>
    private void AddPointCross(ACadSharp.Entities.Point pt, in SKMatrix m, uint color)
    {
        int mode = _pointDisplayMode;
        if (mode == PointMarkStyle.ModeInvisible) { PointMarksHidden++; return; }

        // 与其它实体走同一条 OCS → 块/布局变换链（顺序与 AddStroke 一致：先 OCS、后块矩阵）
        var ocs = CadGeometry.OcsToWcs(pt);
        var local = new SKPoint((float)pt.Location.X, (float)pt.Location.Y);
        if (!ocs.IsIdentity) local = ocs.MapPoint(local);
        var world = TransformPoint(m, local);

        if (!InsideClip(SKRect.Create(world.X, world.Y, 0, 0))) { ClippedAway++; return; }

        PointMarkEntities++;
        _entities.Add(new CadEntity
        {
            Layer = LayerName(pt),
            ColorArgb = color,
            Handle = _currentHandle,
            OwnHandle = _currentOwnHandle,
            Mark = new PointMark { Position = world, Mode = mode, SizeSpec = _pointDisplaySize },
            Bounds = SKRect.Create(world.X, world.Y, 0f, 0f),
        });
    }

    private void AddSolid(Solid sol, in SKMatrix m, uint color)
    {
        var path = new SKPath();
        path.MoveTo((float)sol.FirstCorner.X, (float)sol.FirstCorner.Y);
        path.LineTo((float)sol.SecondCorner.X, (float)sol.SecondCorner.Y);
        path.LineTo((float)sol.FourthCorner.X, (float)sol.FourthCorner.Y);
        path.LineTo((float)sol.ThirdCorner.X, (float)sol.ThirdCorner.Y);
        path.Close();
        AddFill(path, sol, color, m);
    }

    // ---------- 组合实体 ----------

    /// <summary>
    /// 块引用。支持 MINSERT 阵列（RowCount/ColumnCount &gt; 1）。
    ///
    /// <para>BYBLOCK 颜色继承：块内实体的颜色若为 BYBLOCK，应取"插入所在的实体颜色"，
    /// 即 INSERT 自身的解析颜色。原实现直接把 BYBLOCK 当白色，导致块内线条在白底上被
    /// 翻成黑色（本应跟随插入颜色）。</para>
    /// </summary>
    private void ConvertInsert(Insert ins, in SKMatrix parent, int depth, uint? inheritedColor)
    {
        if (ins.Block is null) { DroppedNoBlock++; Skipped++; return; }
        if (depth >= MaxInsertDepth) { DroppedByDepth++; Skipped++; return; }
        // 变换链与标注目标树共用（CadTransform），保证「高亮几何」与「显示几何」不会漂移
        if (!CadTransform.TryInsertMatrix(ins, out var m)) { DroppedByMatrix++; Skipped++; return; }

        // 子实体继承色：INSERT 自身的颜色（BYLAYER 时已解析到图层色）
        uint blockColor = ResolveColor(ins, inheritedColor);

        if (ConvTrace)
            Trace($"EXPAND depth={depth} H={ins.Handle:X} block={ins.Block?.Name} blockH={ins.Block?.Handle:X} ents={ins.Block?.Entities.Count} scale=({ins.XScale:F2},{ins.YScale:F2}) rot={ins.Rotation:F3}");

        CadTransform.ArraySize(ins, out int rows, out int cols, out long total);
        if (total > MaxMInsertInstances)
        {
            // 异常阵列直接放弃整个插入，避免生成天量几何拖垮渲染
            DroppedByMInsert++;
            Skipped++;
            return;
        }

        // 随块线宽：块内写 BYBLOCK 的实体继承本 INSERT 的有效线宽（与上方随块颜色同构）。
        // INSERT 自己是 BYLAYER → 解析出图层线宽；自己是 BYBLOCK → 继承外层块引用，层层上溯。
        int outerWeight = _inheritedLineWeight;
        int blockWeight = ResolveLineWeight(ins, outerWeight);
        _inheritedLineWeight = blockWeight;
        try
        {
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    // 阵列偏移在父坐标系内生效（与 insert 点同层），故夹在块变换与父变换之间
                    var mm = CadTransform.Mul(m, CadTransform.ArrayOffset(ins, r, c));
                    var totalM = CadTransform.Mul(mm, parent);
                    foreach (var child in ins.Block.Entities)
                        ConvertEntity(child, totalM, depth + 1, blockColor, handleOverride: _currentHandle);
                }
            }

            // 属性值（ATTRIB）：值存在 ATTRIB 上（块定义里同名的是 AttributeDefinition 占位符）。
            //
            // **关键**：DWG/DXF 里 ATTRIB 的插入点与字高**已经过本插入的变换**，不能再乘一次。
            // 实测（01 1#1层平面.dwg 的 DYC-A1 图框）：块内 ATTDEF 项目名称 (781.77, 76.09) 高 5，
            // 插入点 (-23173.3,-30200.3) 比例 64 → ATTRIB 存的是 (26054.58,-25330.69)、高 320，
            // 与 (-23173.3 + 781.77×64, -30200.3 + 76.09×64) 分毫不差。再乘一次会飞到 1.6e6 外。
            // 因此这里只叠加**外层**矩阵（本插入若在别的块里，属性坐标仍在那一层的空间里）。
            if (ins.Attributes is { Count: > 0 })
            {
                foreach (var att in ins.Attributes)
                {
                    if (att is null) continue;
                    ConvertEntity(att, parent, depth + 1, blockColor, handleOverride: _currentHandle);
                }
            }
        }
        finally
        {
            _inheritedLineWeight = outerWeight;
        }
    }

    private void AddDimensionBlock(Dimension dim, in SKMatrix parent, int depth, uint? inheritedColor)
    {
        if (depth >= MaxInsertDepth) { Skipped++; return; }
        var block = dim.Block;
        if (block is null) { Skipped++; return; }
        uint color = ResolveColor(dim, inheritedColor);
        // 标注块里的几何几乎全是 BYBLOCK：继承标注自身的线宽，否则会掉回 0.25mm 默认值
        int outerWeight = _inheritedLineWeight;
        _inheritedLineWeight = ResolveLineWeight(dim, outerWeight);
        try
        {
            foreach (var child in block.Entities)
                ConvertEntity(child, parent, depth + 1, color, handleOverride: _currentHandle);
        }
        finally
        {
            _inheritedLineWeight = outerWeight;
        }
    }

    /// <summary>
    /// HATCH → 填充/图案线。
    ///
    /// <para><b>孤岛语义（CAD 口径）</b>：<see cref="Hatch.Paths"/> 是"外边界 + 若干孤岛"的
    /// **环集合**，不是一个环一段独立填充。全部环必须合并成一个路径按**奇偶规则**填充：
    /// 第 1/3/5… 层实心、第 2/4/6… 层是洞（洞中洞又实心），这才是 AutoCAD/ODA 的显示。
    /// 图案线同样要对**全部环**求奇偶，否则图案会穿过孤岛。</para>
    ///
    /// <para>旧实现逐环独立填充 + 逐环独立生成图案，导致孤岛被填实、图案线穿洞；
    /// 且边界路径是"每条边一个子路径"（<see cref="CadGeometry.AppendHatchLoop"/>），
    /// 用 Fill 画多段直线环会退化成零面积 → 正方形实心填充整块不显示。
    /// 这里改用压平后的**闭合点列**重建轮廓，实心与图案两条路径都正确。</para>
    /// </summary>
    private void AddHatch(Hatch h, in SKMatrix m, uint color)
    {
        if (h.Paths == null) { Skipped++; return; }

        var boundary = new SKPath { FillType = SKPathFillType.EvenOdd };
        var segs = new List<(SKPoint a, SKPoint b)>();
        var seenContours = new HashSet<string>(StringComparer.Ordinal);
        int contours = 0;

        foreach (var bp in h.Paths)
        {
            if (bp?.Edges == null || bp.Edges.Count == 0) continue;

            // 非法/重复边界不是"可填充的环"，AutoCAD 也不拿它们参与填充：
            //   NotClosed —— 边界没闭合（实测剪力墙填充里有个覆盖全图的 4 边环带此标志，
            //                当外边界用会把整层楼填成一片灰，墙反而变成洞）；
            //   Duplicate —— 同一轮廓出现两次，进奇偶计数会把这块直接变成洞。
            if (bp.Flags.HasFlag(BoundaryPathFlags.NotClosed)) { HatchBadLoops++; continue; }

            foreach (var poly in FlattenHatchContours(bp))
            {
                if (poly.Count < 3) continue;
                // 完全重合的环只保留一份（SameContour 用点序+"规格化"后的坐标串比对）
                if (!seenContours.Add(ContourKey(poly))) { HatchDuplicateLoops++; continue; }
                contours++;
                boundary.MoveTo(poly[0]);
                for (int i = 1; i < poly.Count; i++) boundary.LineTo(poly[i]);
                boundary.Close();
                for (int i = 0; i < poly.Count; i++)
                {
                    var a = poly[i];
                    var b = poly[(i + 1) % poly.Count];
                    if (a != b) segs.Add((a, b));
                }
            }
        }

        if (contours == 0 || boundary.IsEmpty) { boundary.Dispose(); Skipped++; return; }

        // 渐变填充没有图案定义，AutoCAD/ODA 按渐变色铺满整个边界；
        // 这里至少用渐变首色做实心填充（完整双向渐变留待 P2）。
        if (h.IsSolid || IsGradientHatch(h))
        {
            AddFill(boundary, h, IsGradientHatch(h) ? GradientFillColor(h, color) : color, m);
            return;
        }

        if (AddHatchPatternLines(segs, boundary.Bounds, h, color, m))
        {
            // 图案已生成；边界不描边（AutoCAD/ODA 语义：非实心填充只显示图案线）
            boundary.Dispose();
            return;
        }
        AddStroke(boundary, h, color, m);
    }

    /// <summary>是否渐变 HATCH（ACadSharp 没有 IsGradient 属性，看 GradientColor.Enabled）。</summary>
    private static bool IsGradientHatch(Hatch h)
    {
        try { return h.GradientColor is { Enabled: true }; }
        catch { return false; }
    }

    /// <summary>渐变 HATCH 的填充色：取渐变首色，取不到时退回实体/图层色。</summary>
    private static uint GradientFillColor(Hatch h, uint fallback)
    {
        try
        {
            var colors = h.GradientColor?.Colors;
            if (colors is { Count: > 0 })
            {
                var c = colors[0].Color;
                if (!c.IsByLayer && !c.IsByBlock) return ToArgb(c, 7);
            }
        }
        catch { }
        return fallback;
    }

    /// <summary>轮廓的"规格化"键：从最小点开始按原方向排列，用于剔除完全重合的重复环。</summary>
    private static string ContourKey(List<SKPoint> poly)
    {
        int n = poly.Count, best = 0;
        for (int i = 1; i < n; i++)
        {
            var a = poly[i];
            var b = poly[best];
            if (a.X < b.X - 1e-3f || (MathF.Abs(a.X - b.X) <= 1e-3f && a.Y < b.Y - 1e-3f)) best = i;
        }
        var sb = new System.Text.StringBuilder(n * 14);
        for (int k = 0; k < n; k++)
        {
            var p = poly[(best + k) % n];
            sb.Append(MathF.Round(p.X, 2).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
              .Append(MathF.Round(p.Y, 2).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)).Append(';');
        }
        return sb.ToString();
    }

    /// <summary>
    /// 把一个边界环压平成若干**闭合多边形点列**（通常 1 条）。
    /// 优先用边源数据（<see cref="CadGeometry.FlattenHatchLoopPoints"/>，顶点精确共享，
    /// 图案行扫描的奇偶计数才可靠）；含 Spline 边时回退 SKPath 压平。
    /// </summary>
    private static List<List<SKPoint>> FlattenHatchContours(Hatch.BoundaryPath bp)
    {
        var direct = CadGeometry.FlattenHatchLoopPoints(bp);
        if (direct is { Count: >= 3 }) return new List<List<SKPoint>> { direct };

        using var loopPath = new SKPath();
        CadGeometry.AppendHatchLoop(loopPath, bp);
        return loopPath.IsEmpty ? new List<List<SKPoint>>() : FlattenContours(loopPath);
    }

    private const int MaxHatchPatternRows = 4096;
    private const int MaxHatchPatternSegments = 200_000;

    // 诊断计数（RenderShot 排查用）
    public static int DiagHatchPatternOk;
    public static int DiagHatchPatternFallback;
    public static int DiagHatchOddRows;
    public static string DiagHatchLastReason = "";
    // 临时诊断：最后一次图案生成的边界线段与图案路径（RenderShot/HatchProbe 排查用）
    public static System.Collections.Generic.List<(SKPoint a, SKPoint b)> DiagHatchBoundarySegs;
    public static SKPath DiagHatchPatternPath;

    /// <summary>
    /// 非实心 HATCH → 图案线（经典 hatch 线族算法）。
    /// 对图案定义的每条线（角度/基点/行步距/虚线表）生成平行线族：
    /// 行号由边界包围盒在行法线上的投影区间反推（图案基点可能是远处绝对坐标，
    /// 不能从 0 起步遍历）；行线与边界压平线段求交，按奇偶规则取内部区间，
    /// 再按虚线表切段（相位锚定图案基点）。全部坐标在实体空间生成，经 AddStroke
    /// 统一施加 OCS 镜像与父矩阵，与边界路径同一变换链。
    ///
    /// <para><paramref name="segs"/> 是**全部环**（外边界 + 孤岛）的闭合线段表：
    /// 一次穿越计数就把孤岛算成"外部"，图案线不会穿洞。</para>
    /// 返回 false = 无法生成（无图案定义 / 行数或线段数超限），调用方回退边界轮廓。
    /// </summary>
    private bool AddHatchPatternLines(List<(SKPoint a, SKPoint b)> segs, SKRect bbox, Hatch h, uint color, in SKMatrix m)
    {
        var defs = h.Pattern?.Lines;
        if (defs == null || defs.Count == 0) { DiagHatchPatternFallback++; DiagHatchLastReason = "no-defs"; return false; }

        if (segs.Count == 0) { DiagHatchPatternFallback++; DiagHatchLastReason = "no-segs"; return false; }

        float pad = MathF.Max(bbox.Width, bbox.Height) * 0.05f + 1f;
        float minX = bbox.Left - pad, maxX = bbox.Right + pad;
        float minY = bbox.Bottom - pad, maxY = bbox.Top + pad;

        var pattern = new SKPath();
        var hits = new List<float>();
        int emitted = 0;
        int diagRows = 0, diagHits = 0, diagOdd = 0, diagEpsKill = 0;
        bool overflow = false;
        try
        {
            foreach (var def in defs)
            {
                double ang = def.Angle + h.PatternAngle;
                double dx = Math.Cos(ang), dy = Math.Sin(ang);
                double nx = -dy, ny = dx;
                double ox = def.Offset.X, oy = def.Offset.Y;
                double bx = def.BasePoint.X, by = def.BasePoint.Y;
                double offN = ox * nx + oy * ny;
                if (!double.IsFinite(offN) || Math.Abs(offN) < 1e-9) continue;

                double cN0 = minX * nx + minY * ny, cN1 = maxX * nx + minY * ny;
                double cN2 = minX * nx + maxY * ny, cN3 = maxX * nx + maxY * ny;
                double nMin = Math.Min(Math.Min(cN0, cN1), Math.Min(cN2, cN3));
                double nMax = Math.Max(Math.Max(cN0, cN1), Math.Max(cN2, cN3));
                if (!double.IsFinite(nMin) || !double.IsFinite(nMax) || nMax <= nMin) continue;
                double bN = bx * nx + by * ny;

                // 行号原点折进边界带（整数倍 offset 不改变行集合）；基点太远折不动时
                // （标准图案的历史垃圾坐标可达 1e81，double 加法都吃不掉 offset）
                // 以带中心重锚：只丢法向相位（≤半行距），行集合保持有界。
                double kFold = Math.Floor((nMin - bN) / offN);
                if (!double.IsFinite(kFold) || Math.Abs(kFold) > MaxHatchPatternRows)
                {
                    double cx = (minX + maxX) * 0.5, cy = (minY + maxY) * 0.5;
                    double cn = cx * nx + cy * ny;
                    bN = (nMin + nMax) * 0.5;
                    bx = cx + (bN - cn) * nx;
                    by = cy + (bN - cn) * ny;
                    kFold = 0;
                }
                else if (kFold != 0)
                {
                    bx += ox * kFold; by += oy * kFold; bN += offN * kFold;
                }

                long i0 = (long)Math.Floor((nMin - bN) / offN) - 1;
                long i1 = (long)Math.Ceiling((nMax - bN) / offN) + 1;
                if (i0 > i1) (i0, i1) = (i1, i0);
                if (i1 - i0 > MaxHatchPatternRows) { overflow = true; break; }

                var dashes = def.DashLengths;
                bool dashed = dashes != null && dashes.Count > 0;
                float cycle = 0f;
                if (dashed)
                    foreach (var d in dashes) cycle += MathF.Abs((float)d);

                for (long i = i0; i <= i1; i++)
                {
                    float rX = (float)(bx + ox * i), rY = (float)(by + oy * i);

                    hits.Clear();
                    foreach (var (a, b) in segs)
                    {
                        // 符号距离穿越法：交点由两端点在行法线上的符号距离线性插值，
                        // 不解方程组 → 行线与边共线/近共线（det≈0）时无假交点。
                        // d=0 归正侧（半开区间），顶点穿越/擦过的奇偶计数自洽。
                        double da = (a.X - rX) * nx + (a.Y - rY) * ny;
                        double db = (b.X - rX) * nx + (b.Y - rY) * ny;
                        bool aneg = da < 0, bneg = db < 0;
                        if (aneg == bneg) continue;
                        double tt = da / (da - db);
                        double px = a.X + (b.X - a.X) * tt - rX;
                        double py = a.Y + (b.Y - a.Y) * tt - rY;
                        hits.Add((float)(px * dx + py * dy));
                    }
                    if (hits.Count < 2) continue;
                    diagRows++;
                    diagHits += hits.Count;
                    hits.Sort();
                    if (hits.Count % 2 != 0)
                    {
                        DiagHatchOddRows++;
                        diagOdd++;
                        continue;
                    }

                    float eps = MathF.Max(bbox.Width, bbox.Height) * 1e-4f + 1e-5f;
                    float dotLen = MathF.Max(MathF.Sqrt(bbox.Width * bbox.Width + bbox.Height * bbox.Height) * 5e-4f, 1e-3f);
                    for (int k = 0; k + 1 < hits.Count; k += 2)
                    {
                        // 相邻交点几乎重合 = 行线擦过顶点，奇偶不可靠，丢弃该对
                        if (hits[k + 1] - hits[k] < eps) { diagEpsKill++; continue; }
                        emitted += EmitDashes(pattern, rX, rY, (float)dx, (float)dy, hits[k], hits[k + 1],
                            dashed, cycle, dashes, 1f, dotLen);
                        if (emitted > MaxHatchPatternSegments) { overflow = true; break; }
                    }
                    if (overflow) break;
                }
                if (overflow) break;
            }
        }
        catch
        {
            pattern.Dispose();
            throw;
        }
        if (overflow || pattern.IsEmpty)
        {
            pattern.Dispose();
            DiagHatchPatternFallback++;
            DiagHatchLastReason = overflow
                ? $"overflow(H={h.Handle:X})"
                : $"empty(H={h.Handle:X}):segs={segs.Count} rows={diagRows} hits={diagHits} odd={diagOdd} epsKill={diagEpsKill} emitted={emitted}";
            return false;
        }
        DiagHatchPatternOk++;
        DiagHatchBoundarySegs = segs;
        DiagHatchPatternPath = new SKPath(pattern);
        AddStroke(pattern, h, color, m);
        return true;
    }

    /// <summary>区间 [s0,s1] 内按虚线表切段并写入 path，返回写入的段数（dashed=false 时整段直通）。</summary>
    /// <para>DWG 虚线表语义：正值=实线段，负值=空段，0=点。点按 <paramref name="dotLen"/> 长的
    /// 最短线段绘制（DisplayList 预转换、无缩放信息，取边界尺度的固定比例，放大后清晰可见）。</para>
    private static int EmitDashes(SKPath path, float rX, float rY, float dx, float dy,
        float s0, float s1, bool dashed, float cycle, System.Collections.Generic.List<double> dashes, float scale, float dotLen)
    {
        int emitted = 0;
        void Emit(float a, float b)
        {
            path.MoveTo(rX + a * dx, rY + a * dy);
            path.LineTo(rX + b * dx, rY + b * dy);
            emitted++;
        }
        if (!dashed || cycle <= 0f)
        {
            Emit(s0, s1);
            return emitted;
        }
        long k0 = (long)MathF.Floor(s0 / cycle) - 1;
        long k1 = (long)MathF.Ceiling(s1 / cycle) + 1;
        if (k0 > k1) (k0, k1) = (k1, k0);
        if (k1 - k0 > 262144) return emitted;
        for (long k = k0; k <= k1; k++)
        {
            float t = k * cycle;
            foreach (var d in dashes)
            {
                float len = MathF.Abs((float)(d * scale));
                if (d > 0 && len > 0)
                {
                    float a = MathF.Max(s0, t), b = MathF.Min(s1, t + len);
                    if (b > a) Emit(a, b);
                }
                else if (d == 0)
                {
                    float a = MathF.Max(s0, t), b = MathF.Min(s1, t + dotLen);
                    if (b > a) Emit(a, b);
                }
                t += len;
            }
        }
        return emitted;
    }

    /// <summary>
    /// 路径 → 每条子路径的压平点列（曲线按固定密度取样）。
    /// 与"线段表"版本的区别：保留子路径边界，调用方可以重建闭合轮廓
    /// （填充需要真正的多边形，一堆独立线段填不出面积）。
    /// </summary>
    private static List<List<SKPoint>> FlattenContours(SKPath path)
    {
        var result = new List<List<SKPoint>>();
        using var it = path.CreateRawIterator();
        var pts = new SKPoint[4];
        SKPoint current = default, start = default;
        var cur = new List<SKPoint>();
        SKPathVerb verb;

        void Flush()
        {
            if (cur.Count >= 3)
            {
                // 与起点重合的收尾点去掉（后续按环形取边）
                if (cur.Count > 3 && MathF.Abs(cur[^1].X - cur[0].X) < 1e-4f && MathF.Abs(cur[^1].Y - cur[0].Y) < 1e-4f)
                    cur.RemoveAt(cur.Count - 1);
                result.Add(cur);
            }
            cur = new List<SKPoint>();
        }

        void Poly(params SKPoint[] p)
        {
            foreach (var q in p)
            {
                if (cur.Count > 0)
                {
                    var l = cur[^1];
                    if (MathF.Abs(l.X - q.X) < 1e-6f && MathF.Abs(l.Y - q.Y) < 1e-6f) continue;
                }
                cur.Add(q);
            }
        }

        while ((verb = it.Next(pts)) != SKPathVerb.Done)
        {
            switch (verb)
            {
                case SKPathVerb.Move:
                    Flush();
                    current = start = pts[0];
                    cur.Add(current);
                    break;
                case SKPathVerb.Line:
                    Poly(pts[1]);
                    current = pts[1];
                    break;
                case SKPathVerb.Quad:
                {
                    var t = new List<SKPoint>();
                    SampleQuad(current, pts[1], pts[2], 12, t);
                    Poly(t.ToArray());
                    current = pts[2];
                    break;
                }
                case SKPathVerb.Conic:
                {
                    var t = new List<SKPoint>();
                    SampleConic(current, pts[1], pts[2], it.ConicWeight(), 16, t);
                    Poly(t.ToArray());
                    current = pts[2];
                    break;
                }
                case SKPathVerb.Cubic:
                {
                    var t = new List<SKPoint>();
                    SampleCubic(current, pts[1], pts[2], pts[3], 16, t);
                    Poly(t.ToArray());
                    current = pts[3];
                    break;
                }
                case SKPathVerb.Close:
                    if (cur.Count > 0) Poly(start);
                    Flush();
                    current = start;
                    break;
            }
        }
        Flush();
        return result;
    }

    private static void SampleQuad(SKPoint p0, SKPoint p1, SKPoint p2, int n, List<SKPoint> outp)
    {
        for (int j = 1; j <= n; j++)
        {
            float t = (float)j / n, mt = 1f - t;
            outp.Add(new SKPoint(
                mt * mt * p0.X + 2 * mt * t * p1.X + t * t * p2.X,
                mt * mt * p0.Y + 2 * mt * t * p1.Y + t * t * p2.Y));
        }
    }

    private static void SampleConic(SKPoint p0, SKPoint p1, SKPoint p2, float w, int n, List<SKPoint> outp)
    {
        for (int j = 1; j <= n; j++)
        {
            float t = (float)j / n, mt = 1f - t;
            float u0 = mt * mt, u1 = 2 * mt * t * w, u2 = t * t;
            float sum = u0 + u1 + u2;
            outp.Add(new SKPoint((u0 * p0.X + u1 * p1.X + u2 * p2.X) / sum,
                                 (u0 * p0.Y + u1 * p1.Y + u2 * p2.Y) / sum));
        }
    }

    private static void SampleCubic(SKPoint p0, SKPoint p1, SKPoint p2, SKPoint p3, int n, List<SKPoint> outp)
    {
        for (int j = 1; j <= n; j++)
        {
            float t = (float)j / n, mt = 1f - t;
            outp.Add(new SKPoint(
                mt * mt * mt * p0.X + 3 * mt * mt * t * p1.X + 3 * mt * t * t * p2.X + t * t * t * p3.X,
                mt * mt * mt * p0.Y + 3 * mt * mt * t * p1.Y + 3 * mt * t * t * p2.Y + t * t * t * p3.Y));
        }
    }

    // ---------- 文本 ----------

    /// <summary>
    /// TEXT（单行）→ <see cref="TextDraw"/>。
    ///
    /// 与 ODA/AutoCAD 对齐的口径（2026-10-20 定标）：
    /// <list type="bullet">
    ///   <item>字高 = **大写字高**（cap height），不是字体 em 尺寸 —— 见 <see cref="TextMetrics"/>；</item>
    ///   <item>宽度因子 = 组码 41（实体有效值，样式仅兜底）；</item>
    ///   <item>倾斜 = 组码 51（弧度，样式仅兜底）；</item>
    ///   <item>72/73 对齐：非「左 + 基线」时以组码 11 的定位点为锚，按 9 宫格算局部偏移。</item>
    /// </list>
    /// </summary>
    private void AddText(TextEntity t, in SKMatrix m, uint color)
    {
        double height = t.Height;

        // **字高/宽度因子/倾斜一律以实体值为准，样式参数不参与渲染** ——
        // 受控基准图 .compare/fixture_style.dxf 五组对照实证（同一张图两路渲染）：
        //   样式宽 0.5 + 实体 1.0 → ODA 按 1.0 画（293.2）；实体 0.5 → 147.6 ✔ 只有实体生效
        //   样式倾斜 30° + 实体 0 → ODA 不倾斜；实体倾斜 30° → bbox 变宽 339.9 ✔ 同上
        //   样式固定字高 40 + 实体 80 → ODA 按 80 画 ✔ 同上
        // 原因：AutoCAD 在创建文字时就把样式值抄进实体，DXF 的 40/41/51 是**有效值**；
        // 再乘/叠加样式值会双重压缩（0.7×0.7=0.49），字距与表格排版会整体错位。
        double widthFactor = t.WidthFactor > 0 ? t.WidthFactor : 1.0;
        double oblique = t.ObliqueAngle;   // ACadSharp 读出即为弧度

        AddTextEntity(t.Value, t.InsertPoint, t.AlignmentPoint, height, t.Rotation,
            widthFactor, oblique, t.HorizontalAlignment, t.VerticalAlignment, t, m, color);
    }

    /// <summary>
    /// MTEXT → <see cref="TextDraw"/>。
    /// 锚点按 <c>AttachmentPoint</c> 的 9 宫格换算成「基线左端」，与 TEXT 走同一条对齐管线。
    /// （换行/断行见 P2-4，本步不做宽度断行。）
    /// </summary>
    private void AddMText(MText mt, string? raw, in SKMatrix m, uint color)
    {
        // MText 没有宽度因子字段（宽度由 RectangleWidth 决定，见下方断行）；
        // 样式参数同样不参与渲染，理由见 AddText 的实测说明。
        double widthFactor = 1.0;
        double oblique = 0;
        double height = mt.Height;

        // ---- 断行：组码 41（RectangleWidth）是 AutoCAD 的排版栏宽，超过就折行 ----
        var text = raw ?? string.Empty;
        float rectWidth = (float)mt.RectangleWidth;
        if (rectWidth > 0 && height > 0 && text.Length > 0)
            text = TextWrap.Wrap(text, (float)height, rectWidth);

        // 行距：AutoCAD 单倍 = 1.667 × 字高；组码 44（LineSpacing）是倍数，默认 1.0
        float spacingFactor = TextMetrics.LineSpacingFactor
                              * (mt.LineSpacing > 0 ? (float)mt.LineSpacing : 1f);
        int lineCount = 1;
        foreach (char ch in text) if (ch == '\n') lineCount++;

        // 1..9 = 左上→右下；列决定水平对齐，行决定垂直对齐
        int ap = (int)mt.AttachmentPoint;
        if (ap < 1 || ap > 9) ap = 1;
        int col = (ap - 1) % 3, row = (ap - 1) / 3;
        var h = col switch
        {
            1 => TextHorizontalAlignment.Center,
            2 => TextHorizontalAlignment.Right,
            _ => TextHorizontalAlignment.Left,
        };
        var v = row switch
        {
            1 => TextVerticalAlignmentType.Middle,
            2 => TextVerticalAlignmentType.Bottom,
            _ => TextVerticalAlignmentType.Top,   // MTEXT 默认左上 = 首行顶部，不是基线
        };

        // MTEXT 的水平对齐作用于**每一行**（AutoCAD 行为），因此交给渲染端逐行对齐，
        // 锚点偏移不再承担水平量；垂直方向仍按整块高度算首行基线。
        var lineAlign = h switch
        {
            ACadSharp.Entities.TextHorizontalAlignment.Center => TextLineAlign.Center,
            ACadSharp.Entities.TextHorizontalAlignment.Right => TextLineAlign.Right,
            _ => TextLineAlign.Left,
        };

        // 注意：MTEXT 的组码 11 是「X 轴方向向量」而不是对齐点（ACadSharp 也照此映射到
        // AlignmentPoint），因此锚点恒为 InsertPoint，垂直/水平对齐由 9 宫格偏移表达。
        AddTextEntity(text, mt.InsertPoint, mt.InsertPoint, height, mt.Rotation,
            widthFactor, oblique, h, v, mt, m, color,
            lineCount: lineCount, lineSpacingFactor: spacingFactor,
            lineAlign: lineAlign, alignPerLine: true);
    }

    /// <summary>
    /// 文本实体 → <see cref="TextDraw"/>。转换阶段完成全部布局计算（对齐偏移、两点定宽），
    /// 渲染端只负责「从 Position 按 Rotation 画、套 WidthFactor 与 Oblique」。
    /// </summary>
    private void AddTextEntity(string? raw, XYZ insert, XYZ align, double height, double rotation,
        double widthFactor, double obliqueRadians,
        ACadSharp.Entities.TextHorizontalAlignment h, TextVerticalAlignmentType v,
        Entity e, in SKMatrix m, uint color,
        int lineCount = 1, float lineSpacingFactor = TextMetrics.LineSpacingFactor,
        TextLineAlign lineAlign = TextLineAlign.Left, bool alignPerLine = false)
    {
        if (string.IsNullOrWhiteSpace(raw) || height <= 0) { Skipped++; return; }

        // 统一换行符，供测量与渲染共用（MTEXT 的 \P 已在 CleanMText 中转为 \n）
        var text = raw.Replace("\r\n", "\n").Replace('\r', '\n');
        if (text.TrimEnd('\n').Length == 0) { Skipped++; return; }

        float wf = (float)(widthFactor > 0 ? widthFactor : 1.0);
        float obl = (float)obliqueRadians;

        var basePt = new SKPoint((float)insert.X, (float)insert.Y);
        var alignPt = new SKPoint((float)align.X, (float)align.Y);

        // 需要定位点（组码 11）的对齐方式：中心/右/中/两点式
        bool needsAlignPoint = h != ACadSharp.Entities.TextHorizontalAlignment.Left
                               || v != TextVerticalAlignmentType.Baseline;

        // 老图纸常常写了 72/73 却漏写组码 11（读到 0,0），此时若直接用会把文字甩到原点。
        // 判据：定位点在原点而插入点不在 → 视为缺失，退回插入点并把对齐降级为左/基线。
        if (needsAlignPoint && IsOrigin(alignPt) && !IsOrigin(basePt))
        {
            needsAlignPoint = false;
            h = ACadSharp.Entities.TextHorizontalAlignment.Left;
            v = TextVerticalAlignmentType.Baseline;
        }

        // ---- 两点定宽（Aligned / Fit）：方向与宽度都由「插入点 → 定位点」决定 ----
        float extraRot = 0f;
        float alignWidth = -1f;
        if (h is ACadSharp.Entities.TextHorizontalAlignment.Aligned
              or ACadSharp.Entities.TextHorizontalAlignment.Fit)
        {
            float dx0 = alignPt.X - basePt.X, dy0 = alignPt.Y - basePt.Y;
            float dist = MathF.Sqrt(dx0 * dx0 + dy0 * dy0);
            if (dist > 1e-6f)
            {
                extraRot = MathF.Atan2(dy0, dx0) - (float)rotation;
                alignWidth = dist;
            }
        }

        float capE = (float)height;
        float measured = TextMetrics.MeasureWidth(text, capE) * wf;
        if (alignWidth > 0 && measured > 1e-6f)
        {
            float ratio = alignWidth / measured;
            if (h == ACadSharp.Entities.TextHorizontalAlignment.Aligned)
            {
                // 两点对齐：字高等比缩放，宽度因子保持
                capE *= ratio;
                measured = alignWidth;
            }
            else
            {
                // 布满两点：只改宽度因子
                wf *= ratio;
                measured = alignWidth;
            }
        }

        float desc = TextMetrics.DescenderDepth(text, capE);

        // ---- 9 宫格局部偏移（世界坐标，y 向上；文本从基线左端向右上延伸）----
        // 逐行对齐时水平量由渲染端按行分配（MTEXT），这里不再偏移，
        // 否则"块居中"与"行居中"会叠加成二次偏移。
        float offX = alignPerLine ? 0f : h switch
        {
            ACadSharp.Entities.TextHorizontalAlignment.Center => -measured * 0.5f,
            ACadSharp.Entities.TextHorizontalAlignment.Middle => -measured * 0.5f,
            ACadSharp.Entities.TextHorizontalAlignment.Right => -measured,
            _ => 0f,
        };

        // 多行文本的垂直对齐按**整块**算（AutoCAD MTEXT 的 Middle/Bottom 指整个文字块）：
        //   块高 = 字高 + (n-1)×行距 + 降部
        //   Top    → 首行基线 = 锚点 − 字高（首行 cap 顶端贴锚点）
        //   Middle → 首行基线 = 锚点 − 字高 + 块高/2
        //   Bottom → 首行基线 = 锚点 + (n-1)×行距 + 降部
        float lineH = capE * lineSpacingFactor;
        float blockH = capE + (lineCount - 1) * lineH + desc;
        float offY = v switch
        {
            TextVerticalAlignmentType.Top => -capE,                 // 顶部对齐 → 基线在锚点下方一个字高
            TextVerticalAlignmentType.Middle => -capE + blockH * 0.5f,
            TextVerticalAlignmentType.Bottom => (lineCount - 1) * lineH + desc,
            _ => 0f,                                                // 基线对齐
        };

        var anchor = needsAlignPoint ? alignPt : basePt;
        float rotEntity = (float)rotation + extraRot;
        float cos = MathF.Cos(rotEntity), sin = MathF.Sin(rotEntity);
        var entityPos = new SKPoint(
            anchor.X + offX * cos - offY * sin,
            anchor.Y + offX * sin + offY * cos);

        // ---- 块变换 ----
        float det = m.ScaleX * m.ScaleY - m.SkewX * m.SkewY;
        float scale = MathF.Sqrt(MathF.Abs(det));
        if (float.IsNaN(scale) || scale <= 0) scale = 1f;
        float blockRot = MathF.Atan2(m.SkewY, m.ScaleX);

        var worldPos = TransformPoint(m, entityPos);
        float worldCap = capE * scale;
        float rotTotal = blockRot + rotEntity;

        // 逐行对齐时包围盒按"整块矩形"算（各行首尾落在 ±块宽/2 或 −块宽 上）；
        // 否则沿用字形级精确包围盒（TEXT 的口径不变，历史断言依赖它）。
        SKRect local;
        if (alignPerLine)
        {
            float left = lineAlign switch
            {
                TextLineAlign.Center => -measured * 0.5f,
                TextLineAlign.Right => -measured,
                _ => 0f,
            };
            float bottomLocal = -(lineCount - 1) * (worldCap * lineSpacingFactor)
                                - TextMetrics.DescenderDepth(text, worldCap);
            local = new SKRect(left, bottomLocal, left + measured, worldCap);
        }
        else
        {
            local = TextMetrics.MeasureLocalBounds(text, worldCap, wf, obl);
        }
        var bounds = TextMetrics.ToWorldBounds(local, worldPos, rotTotal);

        // 视口裁剪：文字不在裁剪区内就整条跳过（压边的少量溢出可接受，
        // 逐字裁剪要拆字形，代价远高于收益）
        if (!InsideClip(bounds)) { ClippedAway++; return; }

        _entities.Add(new CadEntity
        {
            Layer = LayerName(e),
            ColorArgb = color,
            Handle = _currentHandle,
            OwnHandle = _currentOwnHandle,
            Text = new TextDraw
            {
                Text = text,
                Position = worldPos,
                Height = worldCap,
                RotationRadians = rotTotal,
                WidthFactor = wf,
                ObliqueRadians = obl,
                LineSpacingFactor = lineSpacingFactor,
                LineAlign = lineAlign,
            },
            Bounds = bounds,
        });
    }

    private static bool IsOrigin(SKPoint p) => MathF.Abs(p.X) < 1e-9f && MathF.Abs(p.Y) < 1e-9f;

    public static string CleanMText(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        s = s.Replace("\\P", "\n").Replace("\\~", " ");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\{([^{}]*)\}", "$1");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\\[A-Za-z][^;\\]*;?", "");
        return s.TrimEnd('\0');
    }

    // ---------- 颜色 / 通用 ----------

    private void CacheLayerColors(CadDocument doc)
    {
        foreach (var layer in doc.Layers)
        {
            var name = layer.Name ?? string.Empty;
            _layerColors[name] = ToArgb(layer.Color, 7);
            _layerOrder.Add(name);

            // 图层线宽（1/100 mm）：图层的线宽也可能是 ByLayer/Default，此时用默认 0.25mm
            int lw = Mm100(layer.LineWeight);
            _layerWeights[name] = lw > 0 ? lw : DefaultLineWeightMm100;

            // 图层被关闭（IsOn=false）或冻结 → 打开图纸时默认隐藏
            if (!layer.IsOn || layer.Flags.HasFlag(LayerFlags.Frozen))
                _initiallyHidden.Add(name);
            else if (!layer.PlotFlag)
            {
                // 不打印图层（AutoCAD 的 DEFPOINTS 就是这一类）→ 也默认隐藏。
                //
                // 判据来自 ODA 对照（2026-10-21，3、幕墙横方总汇.dwg）：该图 15,992 个 POINT
                // 全部落在 DEFPOINTS 上，Teigha 出图里一个都看不到，而我们按 PDMODE/PDSIZE
                // 画出来是满屏的点阵 —— 说明参照实现走的是**打印语义**。
                // 处理成"默认隐藏、可在图层面板打开"，比直接丢弃更保守：
                // 编辑器里核对时仍能看到，也能随时对比有无。
                _initiallyHidden.Add(name);
                NonPlottingLayers++;
            }
        }
    }

    /// <summary>autoCAD 默认线宽 0.25mm（头变量 LWDEFAULT；ACadSharp 未暴露，用行业默认值）。</summary>
    private const int DefaultLineWeightMm100 = 25;

    /// <summary>
    /// <see cref="LineWeightType"/> → 1/100 mm。
    /// 枚举名直接编码了线宽（W25 = 0.25mm、W211 = 2.11mm），负值那几种是"随层/随块/默认/设备像素"。
    /// </summary>
    private static int Mm100(LineWeightType lw) => lw switch
    {
        LineWeightType.ByLayer => -1,
        LineWeightType.ByBlock => -2,
        LineWeightType.Default => -3,
        LineWeightType.ByDIPs => -4,
        _ => int.TryParse(lw.ToString().AsSpan(1), out var v) ? v : -3,
    };

    /// <summary>
    /// 解析实体线宽（1/100 mm）。ByBlock 继承块引用线宽，ByLayer 取图层线宽，
    /// Default 用 0.25mm。返回 0 = 不显示线宽（按细线画）。
    /// </summary>
    private int ResolveLineWeight(Entity e, int inheritedMm100)
    {
        int lw = Mm100(e.LineWeight);
        switch (lw)
        {
            case -2: return inheritedMm100 > 0 ? inheritedMm100 : DefaultLineWeightMm100;   // ByBlock
            case -4: return 0;                                                              // ByDIPs：按像素，不参与
            case -1:
            {
                var name = LayerName(e);
                if (_layerWeights.TryGetValue(name, out var v)) return v;
                return DefaultLineWeightMm100;
            }
            case -3: return DefaultLineWeightMm100;
            default: return lw;
        }
    }

    /// <summary>
    /// 剔除"占位图层"。
    ///
    /// 实测：样例 DXF 的 LAYER 表只声明了 "0" 与 "红-测试" 两个图层，
    /// 但 ACadSharp 读出的 <c>doc.Layers</c> 有 3 项，多出一个名为 "unnamed0"、
    /// 无任何实体引用的条目（handle=5，flags=None，颜色=7）。
    /// 直接显示会在图层面板里凭空多出一行，故对"名字为 unnamed* 且无人引用"的条目不予采纳。
    /// 双重条件保证不会误删真实存在、只是恰好叫这名字的图层。
    /// </summary>
    private void DropUnreferencedPlaceholderLayers()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in _entities) used.Add(e.Layer);

        for (int i = _layerOrder.Count - 1; i >= 0; i--)
        {
            var name = _layerOrder[i];
            if (!IsPlaceholderName(name) || used.Contains(name)) continue;
            _layerOrder.RemoveAt(i);
            _layerColors.Remove(name);
            _initiallyHidden.Remove(name);
        }
    }

    private static bool IsPlaceholderName(string name)
        => string.IsNullOrWhiteSpace(name)
           || name.StartsWith("unnamed", StringComparison.OrdinalIgnoreCase);

    private static uint ToArgb(Color c, int fallbackIndex)
    {
        if (c.IsTrueColor)
            return 0xFF000000u | ((uint)c.TrueColor & 0xFFFFFF);
        return AciColorTable.FromAci(c.Index != 0 ? c.Index : fallbackIndex);
    }

    private static string LayerName(Entity e) => e.Layer?.Name ?? "0";

    /// <summary>
    /// 解析实体的显示颜色。
    /// BYBLOCK → 用块继承色（由外层 INSERT 传入），块外无继承色时退化为白色；
    /// BYLAYER → 查图层色表。
    /// </summary>
    private uint ResolveColor(Entity e, uint? inheritedColor)
    {
        var c = e.Color;
        if (c.IsByBlock)
            return inheritedColor ?? AciColorTable.FromAci(7);
        if (c.IsByLayer)
        {
            var name = LayerName(e);
            return _layerColors.TryGetValue(name, out var v) ? v : AciColorTable.FromAci(7);
        }
        return ToArgb(c, 7);
    }

    // 法线 (0,0,-1) 的实体：OCS→WCS 需绕 Y 轴镜像（x→-x）。
    // 不做这步，弧/圆会画到 X 镜像位置（实测 0053datr：41 条圆角弧全跑到 +900，
    // 轮廓每处圆角断开、Extents 被撑大近 20 倍）。2D 图纸法向只有 ±Z 两种。
    // Normal 不在 Entity 基类上（ACadSharp 3.4.9 各具体类自行声明），只能逐类型取。

    private void AddStroke(SKPath path, Entity e, uint color, in SKMatrix m)
    {
        var ocs = CadGeometry.OcsToWcs(e);
        if (!ocs.IsIdentity) path.Transform(ocs);
        path.Transform(m);
        var clipped = ApplyViewportClip(path);
        if (clipped is null) return;
        path = clipped;
        var bounds = path.Bounds;
        if (float.IsNaN(bounds.Left)) { path.Dispose(); Skipped++; return; }

        // 线宽：实体/图层/块的解析结果（1/100 mm），换算成屏幕像素留给渲染期
        // （模型空间是固定像素、图纸空间按真实单位，见 CadEntity.LineWeightMm100）。
        int lw100 = ResolveLineWeight(e, _inheritedLineWeight);
        if (lw100 > 0) LineWeightEntities++;

        _entities.Add(new CadEntity
        {
            Layer = LayerName(e),
            ColorArgb = color,
            Handle = _currentHandle,
            OwnHandle = _currentOwnHandle,
            Path = path,
            LineWeightMm100 = lw100,
            DashPattern = ResolveDashPattern(e, m),
            Bounds = bounds,
        });
    }

    /// <summary>
    /// 图纸空间里「1 mm = 多少图纸单位」。按 1:1 mm 出图时取 1；英制或缩放图纸可调。
    /// 只影响**布局**里的线宽显示（那里线宽按真实单位画）；模型空间是固定像素宽，用不到它。
    /// </summary>
    public float WorldUnitsPerMm { get; set; } = 1f;

    /// <summary>
    /// 解析实体的显示线型为 Skia 虚线间隔（on/off 交替，世界单位）。
    /// 实线（Continuous / ByBlock / 无段 / 全为实段）返回 null。
    /// </summary>
    private float[]? ResolveDashPattern(Entity e, in SKMatrix m)
    {
        var lt = e.LineType;
        if (lt == null || lt.Segments == null) return null;

        double unit = _globalLtScale * (e.LineTypeScale > 0 ? e.LineTypeScale : 1.0);
        double det = Math.Abs((double)m.ScaleX * m.ScaleY - (double)m.SkewX * m.SkewY);
        if (det > 0 && !double.IsNaN(det) && !double.IsInfinity(det)) unit *= Math.Sqrt(det);
        if (unit <= 0) return null;

        var segs = lt.Segments.ToList();
        if (segs.Count == 0) return null;

        double total = 0;
        foreach (var s in segs) total += Math.Abs(s.Length);
        if (total <= 0) return null;

        // 段长带符号（负 = 空档），同时用 IsSpace 兜底；点段按极短实段画，保证交替序列合法。
        var list = new List<double>();
        foreach (var s in segs)
        {
            double len = Math.Abs(s.Length) * unit;
            bool on = !s.IsSpace && !s.IsText && !s.IsShape;
            if (on && len <= 0) len = Math.Max(total * unit * 0.02, 1e-6);
            if (len <= 0) continue;
            bool expectOn = list.Count % 2 == 0;
            if (expectOn == on) list.Add(len);
            else if (list.Count > 0) list[^1] += len;   // 同类型相邻 → 合并，保持 on/off 交替
            else list.Add(len);
        }
        if (list.Count < 2) return null;

        var arr = new float[list.Count];
        for (int i = 0; i < list.Count; i++) arr[i] = (float)Math.Max(list[i], 1e-6);
        return PoolDash(arr);
    }

    /// <summary>等值线型复用同一数组实例（烘焙按引用分桶、渲染按引用缓存路径效果）。</summary>
    private float[] PoolDash(float[] pattern)
    {
        var key = string.Join("|", Array.ConvertAll(pattern, v => v.ToString("R")));
        if (_dashPool.TryGetValue(key, out var hit)) return hit;
        _dashPool[key] = pattern;
        return pattern;
    }

    private void AddFill(SKPath path, Entity e, uint color, in SKMatrix m)
    {
        var ocs = CadGeometry.OcsToWcs(e);
        if (!ocs.IsIdentity) path.Transform(ocs);
        path.Transform(m);
        var clipped = ApplyViewportClip(path);
        if (clipped is null) return;
        path = clipped;
        var bounds = path.Bounds;
        if (float.IsNaN(bounds.Left)) { path.Dispose(); Skipped++; return; }
        _entities.Add(new CadEntity
        {
            Layer = LayerName(e),
            ColorArgb = color,
            Handle = _currentHandle,
            OwnHandle = _currentOwnHandle,
            FillPath = path,
            Bounds = bounds,
        });
    }

    internal static SKPoint TransformPoint(in SKMatrix m, SKPoint p)
        => new(p.X * m.ScaleX + p.Y * m.SkewX + m.TransX,
               p.X * m.SkewY + p.Y * m.ScaleY + m.TransY);

    // 矩阵组合（行向量约定，a 先 b 后）
    internal static SKMatrix Mul(in SKMatrix a, in SKMatrix b) => new(
        a.ScaleX * b.ScaleX + a.SkewY * b.SkewX,
        a.SkewX * b.ScaleX + a.ScaleY * b.SkewX,
        a.TransX * b.ScaleX + a.TransY * b.SkewX + b.TransX,
        a.ScaleX * b.SkewY + a.SkewY * b.ScaleY,
        a.SkewX * b.SkewY + a.ScaleY * b.ScaleY,
        a.TransX * b.SkewY + a.TransY * b.ScaleY + b.TransY,
        0, 0, 1);

    internal static SKMatrix ScaleM(float sx, float sy) => new(sx, 0, 0, 0, sy, 0, 0, 0, 1);
    internal static SKMatrix RotM(float rad)
    {
        float c = MathF.Cos(rad), s = MathF.Sin(rad);
        return new SKMatrix(c, -s, 0, s, c, 0, 0, 0, 1);
    }
    internal static SKMatrix TransM(float tx, float ty) => new(1, 0, tx, 0, 1, ty, 0, 0, 1);
}
