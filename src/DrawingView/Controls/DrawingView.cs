using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using DrawingView.Core;
using SkiaSharp;

namespace DrawingView.Controls;

/// <summary>
/// 2D CAD 视图控件。
/// 渲染：Render → context.Custom → ICustomDrawOperation → 借用 Avalonia 自带的
/// SkiaSharp GPU 上下文（ISkiaSharpApiLeaseFeature）直接绘制，零互操作层。
/// 交互：滚轮缩放（光标锚点）、拖拽平移、双击适应窗口。
///
/// 大图性能策略（借鉴 ReadCAD 的 Gpu*Buffer 批次 + MinEntityPixelSize LOD）：
///   1. 批次路径：大图由 GeometryBaker 预先把几何按 (尺寸档, 图层, 颜色) 合并成少量
///      SKPath，可见比例高时只绘制批次（DrawPath 次数从 13.6 万降到百级）。
///   2. 逐实体路径：可见比例低时仍走 R 树剔除 + 逐实体绘制（此时可见实体少，更快）。
///   3. 两条路都施加同一套 LOD 与配色规则，切换时画面一致。
/// </summary>
public class DrawingView : Control
{
    /// <summary>无隐藏图层时的共享空集合，避免每帧分配。</summary>
    private static readonly HashSet<string> NoHiddenLayers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 文本像素高度下限（ReadCAD 的 MinTextPixelSize 取 5）：低于此值不绘制。
    /// 文字在 5px 以下不可读，绘制它只会白白消耗时间。
    /// </summary>
    public const float DefaultMinTextPixelSize = 5f;

    /// <summary>
    /// 几何实体像素尺寸下限（对应 ReadCAD 的 MinEntityPixelSize）。
    ///
    /// 实测（5.36MB / 135876 实体图纸，12 帧中位绘制耗时）：
    ///   阈值 0.0（关闭） → 1x 27.2ms / 2x 29.6ms
    ///   阈值 1.0        → 1x  5.6ms / 2x 14.6ms
    ///   阈值 1.5        → 1x  4.8ms / 2x  5.4ms   ← 取此值
    ///   阈值 3.0        → 1x  2.4ms / 2x  5.0ms
    /// 之所以不是 1.0：世界尺寸档按 4 倍递增，"256–1024 单位"这一档在 2x 缩放下的
    /// 最大像素尺寸是 1.19px，阈值 1.0 时整档必须保留（19 万个点），1.5 才能整档跳过。
    ///
    /// 被剔除的都是屏幕尺寸不足 1.5px 的几何，肉眼不可见。
    /// 若图纸极密（填充图案）且希望保留更多细节，可调到 1.0；
    /// 想进一步提速可随 ReadCAD 调到 3.0，代价是屏幕上 1~3px 的细碎构件不再绘制。
    /// </summary>
    public float MinEntityPixelSize { get; set; } = 1.5f;

    private bool _showLineWeights;

    /// <summary>
    /// 是否按图纸线宽绘制（对应 AutoCAD 的 LWDISPLAY / 状态栏「显示/隐藏线宽」）。
    ///
    /// <para>默认 <c>false</c>：全部按 1px 细线画。这是 AutoCAD 的出厂默认，
    /// 也是看图更清晰的选择——平面图里 90% 的实体都是 0.25mm 默认线宽，
    /// 一开线宽整张图会变粗、密集处糊成一片。</para>
    ///
    /// <para>显示口径与 AutoCAD 完全一致（两种空间不同，别弄混）：</para>
    /// <list type="bullet">
    /// <item><b>模型空间</b>：线宽是**固定像素宽**，不随缩放变化（0 → 1px，其余按 mm 成正比，
    /// 0.25mm ≈ 1px、0.50mm ≈ 2px、2.11mm ≈ 8px）。所以缩小看时线宽相对图纸会显得更粗。</item>
    /// <item><b>图纸空间（布局）</b>：线宽按**真实单位**显示，随缩放变化
    /// （1:1 出图的布局里 0.5mm 线在纸上就是 0.5mm）。</item>
    /// </list>
    /// <para>与 <see cref="MinEntityPixelSize"/> 不同，这里不做 LOD，细线永远保持至少 1px。</para>
    /// </summary>
    public bool ShowLineWeights
    {
        get => _showLineWeights;
        set
        {
            if (_showLineWeights == value) return;
            _showLineWeights = value;
            InvalidateVisual();
        }
    }

    public static readonly StyledProperty<SKColor> BackgroundColorProperty =
        AvaloniaProperty.Register<DrawingView, SKColor>(nameof(BackgroundColor), SKColors.Black);

    public SKColor BackgroundColor
    {
        get => GetValue(BackgroundColorProperty);
        set => SetValue(BackgroundColorProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BackgroundColorProperty)
            InvalidateVisual();
    }

    /// <summary>
    /// 批次回放成本 ≈ 多少个逐实体绘制（实测标定）。
    /// 两条路都要遍历同样的几何，差异在调用次数与 SKPaint 状态抖动，
    /// 因此当"可见实体数 &gt; 批次数 × 该比值"时才切到批次路径。
    /// </summary>
    private const int BucketVsEntityCostRatio = 200;

    private DisplayList? _display;

    /// <summary>线型间隔 → Skia 路径效果（间隔为世界单位，画布矩阵负责缩放到屏幕）。</summary>
    private readonly Dictionary<float[], SKPathEffect> _dashEffects = new();
    private bool _pendingFit;
    private Point? _panStart;
    private SKPoint _panOffsetStart;
    private Point? _lastMiddlePress;
    private long _lastMiddlePressTicks;

    // ---------- 选择（步骤 B：点选 / 高亮 / zoom-to） ----------
    // 渲染线程读 _selectedSnapshot，UI 线程写入：替换引用而非原地改列表（不可变快照原则）。
    private IReadOnlyList<CadEntity> _selectedSnapshot = Array.Empty<CadEntity>();

    /// <summary>
    /// 叠加高亮层（世界坐标临时几何）。与选中集合同一套交付方式：整个引用替换，
    /// 渲染线程只读快照，不做部分更新。
    /// </summary>
    private OverlayLayer? _overlaySnapshot;
    private Point? _bandStart;
    private Point _bandCurrent;
    private bool _bandActive;

    private static readonly SolidColorBrush BandWindowFill = new(Color.FromArgb(36, 68, 138, 255));
    private static readonly SolidColorBrush BandWindowBorder = new(Color.FromRgb(68, 138, 255));
    private static readonly SolidColorBrush BandCrossingFill = new(Color.FromArgb(36, 92, 184, 92));
    private static readonly Pen BandCrossingPen = new(new SolidColorBrush(Color.FromRgb(92, 184, 92)), 1, DashStyle.Dash);
    private static readonly Pen BandWindowPen = new(BandWindowBorder, 1);

    /// <summary>选中集合变化（含清空）。UI 线程触发。</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>当前选中实体（不可变快照）。</summary>
    public IReadOnlyList<CadEntity> SelectedEntities => _selectedSnapshot;

    private List<SubEntityPick> _subPicks;
    private bool _subPickOwnsOverlay;

    /// <summary>当前二级拾取集合（空 = 无）。任何选中变化都会清除。</summary>
    public IReadOnlyList<SubEntityPick> CurrentSubPicks => _subPicks ?? (IReadOnlyList<SubEntityPick>)Array.Empty<SubEntityPick>();

    /// <summary>二级拾取集合变化时触发（含框选清空时的空列表。UI 线程）。</summary>
    public event EventHandler<IReadOnlyList<SubEntityPick>>? SubEntitiesPicked;

    /// <summary>
    /// 二级拾取解析器：命中实体被「再次点击」或 Ctrl+点击时调用。
    /// 入参 = 命中图元、世界坐标指针位置、世界单位容差；
    /// 返回 null = 该实体没有可二级拾取的子目标，走普通选择。
    /// </summary>
    public Func<CadEntity, SKPoint, float, SubEntityPick>? SubEntityResolver { get; set; }

    /// <summary>
    /// Ctrl+框选解析器：已选中目标的情况下框选时调用。
    /// 入参 = 世界坐标矩形 + window（AutoCAD 框选规则：true = 左→右实线窗口，
    /// 只收完全包含者；false = 右→左虚线窗交，相交即收）；
    /// 返回其中命中的子图元集合（空 = 无）。
    /// </summary>
    public Func<SKRect, bool, IReadOnlyList<SubEntityPick>>? SubEntitiesInRectResolver { get; set; }

    /// <summary>单点拾取（屏幕坐标）：容差内最近的实体；无命中返回 null。</summary>
    public CadEntity? HitTest(SKPoint screenPoint, float tolerancePx = 5f)
    {
        if (_display is null) return null;
        var world = Viewport.ScreenToWorld(screenPoint);
        float tolWorld = tolerancePx / MathF.Max(Viewport.Zoom, 1e-6f);
        var rect = SKRect.Create(world.X - tolWorld, world.Y - tolWorld, tolWorld * 2, tolWorld * 2);

        var candidates = _display.Index.Query(rect);
        CadEntity? best = null;
        double bestDist = double.MaxValue;
        foreach (var e in candidates)
        {
            if (_hidden.Count > 0 && _hidden.Contains(e.Layer)) continue;
            if (!e.Bounds.IntersectsWith(rect)) continue;
            double d = DistanceToEntity(e, world, tolWorld);
            if (d < bestDist)
            {
                bestDist = d;
                best = e;
            }
        }
        return bestDist <= tolWorld ? best : null;
    }

    /// <summary>
    /// 实体距离（世界单位）：AutoCAD 语义 —— 填充实体点在内部 = 0；描边实体把容差
    /// 膨胀进描边几何后包含点 = 0（拾取框压到线才算，点在矩形/圆内部空白不选中）。
    /// 无几何（或膨胀失败）才退回包围盒距离。
    /// </summary>
    private static double DistanceToEntity(CadEntity e, SKPoint world, float tolWorld)
    {
        if (e.FillPath is { } fp && fp.Contains(world.X, world.Y)) return 0;
        if (e.Path is { } pth)
        {
            using var paint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = tolWorld * 2,
            };
            using var inflated = new SKPath();
            if (paint.GetFillPath(pth, inflated))
                return inflated.Contains(world.X, world.Y) ? 0 : double.MaxValue;
        }
        var b = e.Bounds;
        float dx = MathF.Max(MathF.Max(b.Left - world.X, world.X - b.Right), 0);
        float dy = MathF.Max(MathF.Max(b.Top - world.Y, world.Y - b.Bottom), 0);
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>替换选中集合并重绘（供外部面板/程序化驱动）。</summary>
    public void SetSelection(IEnumerable<CadEntity> entities)
    {
        ClearSubPick();
        _selectedSnapshot = entities.ToArray();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>清空选中集合并重绘。</summary>
    public void ClearSelection()
    {
        ClearSubPick();
        if (_selectedSnapshot.Count == 0) return;
        _selectedSnapshot = Array.Empty<CadEntity>();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>单选切换（点击命中调用）。</summary>
    private void SelectAt(Point p, bool subRequested)
    {
        var hit = HitTest(new SKPoint((float)p.X, (float)p.Y));
        if (hit is null)
        {
            ClearSelection();
            return;
        }
        var current = _selectedSnapshot;
        if (SubEntityResolver != null
            && (subRequested || (current.Count == 1 && ReferenceEquals(current[0], hit))))
        {
            var world = Viewport.ScreenToWorld(new SKPoint((float)p.X, (float)p.Y));
            var tol = 5f / MathF.Max(Viewport.Zoom, 1e-6f);
            var pick = SubEntityResolver(hit, world, tol);
            if (pick != null)
            {
                // Ctrl+点 = 切换：已选中的子图元再次 Ctrl+点取消，未选的加入（可与框选混用）
                if (subRequested) ToggleSubPick(pick);
                else SetSubPicks(new[] { pick });
                return;
            }
        }
        // 最小实现：单击命中 = 单选（重复点击已选实体保持选中）
        if (current.Count == 1 && ReferenceEquals(current[0], hit)) return;
        ClearSubPick();
        _selectedSnapshot = new[] { hit };
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    private void SetSubPicks(IReadOnlyList<SubEntityPick> picks)
    {
        ClearSubPickCore();
        var list = picks?.Where(p => p != null).ToList();
        if (list is { Count: > 0 })
        {
            var shapes = new List<OverlayShape>();
            foreach (var p in list)
                shapes.AddRange(p.Highlight);
            SetOverlay(new OverlayLayer { Shapes = shapes });
            _subPicks = list;
            _subPickOwnsOverlay = true;
        }
        SubEntitiesPicked?.Invoke(this, CurrentSubPicks);
    }

    /// <summary>Ctrl+点切换：按 (Handle, OwnHandle, SubPath) 识别，已在集合中则移除，否则追加。</summary>
    private void ToggleSubPick(SubEntityPick pick)
    {
        var next = new List<SubEntityPick>();
        bool removed = false;
        if (_subPicks is { Count: > 0 })
        {
            foreach (var q in _subPicks)
            {
                if (q.Handle == pick.Handle && q.OwnHandle == pick.OwnHandle
                    && string.Equals(q.SubPath, pick.SubPath, System.StringComparison.Ordinal))
                {
                    removed = true;   // 直接丢弃：其几何随旧叠加层延迟释放一并回收
                    continue;
                }
                next.Add(ClonePick(q));
            }
        }
        if (!removed)
            next.Add(pick);
        SetSubPicks(next);
    }

    /// <summary>
    /// 复用既有拾取必须深拷贝高亮几何：旧叠加层 Release 后（含延迟释放窗口）
    /// 会 Dispose 底下的 SKPath，直接把同一批 <see cref="OverlayShape"/> 挂进新层
    /// 就是释放后使用。克隆路径后旧层自生自灭，与新层互不相干。
    /// </summary>
    private static SubEntityPick ClonePick(SubEntityPick p)
    {
        var shapes = new List<OverlayShape>(p.Highlight.Count);
        foreach (var s in p.Highlight)
        {
            if (s?.Path is null) continue;
            shapes.Add(new OverlayShape
            {
                Path = new SKPath(s.Path),
                Fill = s.Fill,
                ColorArgb = s.ColorArgb,
                FillAlpha = s.FillAlpha,
                StrokeWidthWorld = s.StrokeWidthWorld,
                Dashed = s.Dashed,
            });
        }
        return new SubEntityPick
        {
            Handle = p.Handle,
            OwnHandle = p.OwnHandle,
            SubPath = p.SubPath,
            Highlight = shapes,
            Bounds = p.Bounds,
        };
    }

    /// <summary>清除二级拾取；仅当叠加层当前属于二级拾取时才一并清除。</summary>
    public void ClearSubPick()
    {
        if (_subPicks == null) return;
        ClearSubPickCore();
    }

    /// <summary>程序化安装二级拾取（与 Ctrl+点/框选同一条高亮与所有权路径；空集合 = 清除）。</summary>
    public void SetSubEntityPicks(IReadOnlyList<SubEntityPick> picks) => SetSubPicks(picks);

    private void ClearSubPickCore()
    {
        _subPicks = null;
        if (_subPickOwnsOverlay)
        {
            _subPickOwnsOverlay = false;
            ClearOverlay();
        }
    }

    private void SelectInBand()
    {
        var bs = _bandStart!.Value;
        double l = Math.Min(bs.X, _bandCurrent.X);
        double t = Math.Min(bs.Y, _bandCurrent.Y);
        double r = Math.Max(bs.X, _bandCurrent.X);
        double b = Math.Max(bs.Y, _bandCurrent.Y);
        var wa = Viewport.ScreenToWorld(new SKPoint((float)l, (float)t));
        var wb = Viewport.ScreenToWorld(new SKPoint((float)r, (float)b));
        var world = SKRect.Create(
            MathF.Min(wa.X, wb.X), MathF.Min(wa.Y, wb.Y),
            MathF.Abs(wb.X - wa.X), MathF.Abs(wb.Y - wa.Y));
        SelectByRectangle(world, window: _bandCurrent.X >= bs.X);
    }

    public int SelectByRectangle(SKRect worldRect, bool window)
    {
        if (_display is null) return 0;
        var hits = new List<CadEntity>();
        foreach (var e in _display.Index.Query(worldRect))
        {
            if (_hidden.Count > 0 && _hidden.Contains(e.Layer)) continue;
            bool hit = window ? worldRect.Contains(e.Bounds) : worldRect.IntersectsWith(e.Bounds);
            if (hit) hits.Add(e);
        }
        SetSelection(hits);
        return hits.Count;
    }

    /// <summary>
    /// 缩放到世界矩形（padding &gt; 1 时留白）。对应 WPF/Teigha 的 Zoom(Extents3d, scale)。
    /// </summary>
    public void ZoomTo(SKRect worldRect, float padding = 1.2f)
    {
        float padX = worldRect.Width * (padding - 1f) / 2f;
        float padY = worldRect.Height * (padding - 1f) / 2f;
        var inflated = new SKRect(
            worldRect.Left - padX, worldRect.Top - padY,
            worldRect.Right + padX, worldRect.Bottom + padY);
        Viewport.Fit(inflated, (float)Bounds.Width, (float)Bounds.Height);
        InvalidateVisual();
    }

    /// <summary>缩放到实体集合包围盒（对应 WPF 版 Zoom(ObjectId[])）。</summary>
    public void ZoomTo(IEnumerable<CadEntity> entities, float padding = 1.2f)
    {
        bool any = false;
        float l = float.MaxValue, t = float.MaxValue, r = float.MinValue, btm = float.MinValue;
        foreach (var e in entities)
        {
            any = true;
            l = MathF.Min(l, e.Bounds.Left); t = MathF.Min(t, e.Bounds.Top);
            r = MathF.Max(r, e.Bounds.Right); btm = MathF.Max(btm, e.Bounds.Bottom);
        }
        if (any) ZoomTo(SKRect.Create(l, t, r - l, btm - t), padding);
    }

    /// <summary>按 ACadSharp 句柄定位（标注面板点击跳转用）；命中返回 true。</summary>
    public bool ZoomTo(ulong handle, float padding = 2.5f)
    {
        if (_display is null || handle == 0) return false;
        if (!_display.HandleBounds.TryGetValue(handle, out var rect)) return false;
        ZoomTo(rect, padding);
        return true;
    }

    /// <summary>按句柄集合设置选中高亮（标注面板联动用）；返回实际命中的实体数。</summary>
    public int SetSelectionByHandles(IEnumerable<ulong> handles)
    {
        if (_display is null) return 0;
        var set = new HashSet<ulong>(handles);
        var hits = new List<CadEntity>();
        foreach (var e in _display.Entities)
        {
            if (e.Handle != 0 && set.Contains(e.Handle)) hits.Add(e);
        }
        SetSelection(hits);
        return hits.Count;
    }

    /// <summary>
    /// 按「(最外层块引用句柄, 块内实体自身句柄)」精确设置选中 —— 打标签编辑树用。
    ///
    /// <para>扁平模型空间里块内子实体的 <see cref="CadEntity.Handle"/> 会被覆盖成最外层
    /// INSERT 的句柄，因此单靠 Handle 选不中「块内某个特定子实体」，会连带选中整个块引用。
    /// 加上 <see cref="CadEntity.OwnHandle"/>（块定义层原始句柄，DWG 内全局唯一）后，
    /// 二元组即构成唯一地址。</para>
    ///
    /// <para>Inner 传 0 时退化为「按外层句柄整选」，与
    /// <see cref="SetSelectionByHandles"/> 等价。</para>
    /// </summary>
    /// <returns>实际命中的实体数。</returns>
    public int SetSelectionBySubTargets(IEnumerable<(ulong Outer, ulong Inner)> targets)
    {
        if (_display is null) return 0;
        // 单次遍历 + 哈希查找：实体数可达 5 万，逐个目标全表扫描会退化成 O(n·m)。
        var set = new HashSet<(ulong Outer, ulong Inner)>();
        var outerOnly = new HashSet<ulong>();
        foreach (var t in targets)
        {
            if (t.Outer == 0) continue;
            if (t.Inner == 0) outerOnly.Add(t.Outer);
            else set.Add(t);
        }
        if (set.Count == 0 && outerOnly.Count == 0) return 0;

        var hits = new List<CadEntity>();
        foreach (var e in _display.Entities)
        {
            if (e.Handle == 0) continue;
            if (set.Contains((e.Handle, e.OwnHandle)) || outerOnly.Contains(e.Handle))
                hits.Add(e);
        }
        SetSelection(hits);
        return hits.Count;
    }

    /// <summary>
    /// 按二元组集合定位：选中并缩放到命中实体的包围盒。未命中任何实体返回 false。
    /// </summary>
    public bool RevealSubTargets(IEnumerable<(ulong Outer, ulong Inner)> targets, float padding = 2.5f)
    {
        var list = targets as IReadOnlyCollection<(ulong Outer, ulong Inner)> ?? targets.ToArray();
        if (list.Count == 0) return false;
        if (SetSelectionBySubTargets(list) == 0) return false;
        ZoomTo(_selectedSnapshot, padding);
        return true;
    }

    // ---------- 叠加高亮（临时几何） ----------
    // 用途：高亮"比一个 CadEntity 更细"的目标 —— 多段线的某一段、填充的某个环、
    // 块引用内的某个子实体。这类几何在 DisplayList 里没有独立身份，SetSelection
    // 表达不了；对应 WPF/Teigha 侧的 TransientManager。

    /// <summary>当前叠加层的图元数（诊断用）。</summary>
    public int OverlayShapeCount => _overlaySnapshot?.Shapes.Count ?? 0;

    /// <summary>当前叠加层是否归二级拾取所有（自检用：标注叠层接管后应为 false）。</summary>
    public bool SubPickOwnsOverlay => _subPickOwnsOverlay;

    /// <summary>
    /// 替换叠加高亮层；传 null 等同于清除。
    ///
    /// <b>所有权语义</b>（与 <see cref="SetDisplay"/> 一致）：传入的
    /// <see cref="OverlayLayer"/> 由本控件接管（调用方不要再自行 Release），
    /// 下次替换、换图或控件销毁时归还。旧层的原生几何要等绘制操作放手后
    /// 才会真正释放，所以绘制中换层是安全的。
    /// </summary>
    public void SetOverlay(OverlayLayer? layer)
    {
        var old = _overlaySnapshot;
        // 外部写入（标注叠层/目标高亮）接管通道：二级拾取的视觉与所有权一并失效 ——
        // 否则过期的 _subPickOwnsOverlay 会让下一次 ClearSubPick 把别人的叠层清空
        ReleaseSubPickOwnership();
        if (ReferenceEquals(old, layer)) return;
        _overlaySnapshot = layer;
        if (old is not null) old.Release();
        InvalidateVisual();
    }

    /// <summary>清除叠加高亮层。</summary>
    public void ClearOverlay()
    {
        ReleaseSubPickOwnership();
        if (_overlaySnapshot is null) return;
        _overlaySnapshot.Release();
        _overlaySnapshot = null;
        InvalidateVisual();
    }

    /// <summary>拾取高亮被替换/清除时同步丢弃所有权与清单，并通知 VM 拾取已空。</summary>
    private void ReleaseSubPickOwnership()
    {
        if (_subPicks == null) return;
        _subPicks = null;
        _subPickOwnsOverlay = false;
        SubEntitiesPicked?.Invoke(this, CurrentSubPicks);
    }

    /// <summary>
    /// 缩放到叠加层几何的包围盒（选中目标后"跳到它"用）。
    /// 层为空或几何为空返回 false（视口不动）。
    /// </summary>
    public bool ZoomToOverlay(float padding = 1.6f)
    {
        var shapes = _overlaySnapshot?.Shapes;
        if (shapes is null || shapes.Count == 0) return false;

        float l = float.MaxValue, t = float.MaxValue, r = float.MinValue, b = float.MinValue;
        bool any = false;
        foreach (var s in shapes)
        {
            if (s?.Path is null) continue;
            // 用 TightBounds 而非 Bounds：曲线/贝塞尔的控制点会让 Bounds 明显偏大，
            // 高亮一个圆弧却缩放到很远的空处就是这个原因。
            var box = s.Path.TightBounds;
            if (box.Width <= 0 && box.Height <= 0 && box.Left == 0 && box.Top == 0) continue;
            any = true;
            l = MathF.Min(l, box.Left); t = MathF.Min(t, box.Top);
            r = MathF.Max(r, box.Right); b = MathF.Max(b, box.Bottom);
        }
        if (!any) return false;
        ZoomTo(SKRect.Create(l, t, r - l, b - t), padding);
        return true;
    }

    private long _lastDrawTicks;
    private long _drawTicksTotal;
    private long _drawCount;

    /// <summary>
    /// 最近一次绘制操作（Custom draw op）自身的耗时，毫秒 —— 不含 Avalonia 合成与
    /// 位图拷贝等固定开销，用于性能诊断与回归对比（对应 ReadCAD 的 PerformanceMonitorService）。
    /// 由渲染线程写入、UI 线程读取，用 Interlocked 保证可见性。
    /// </summary>
    public double LastDrawMs => Volatile.Read(ref _lastDrawTicks) * 1000.0 / Stopwatch.Frequency;

    /// <summary>绘制操作累计平均耗时（毫秒）；采样数为 0 时返回 0。</summary>
    public double AverageDrawMs
    {
        get
        {
            long n = Volatile.Read(ref _drawCount);
            return n == 0 ? 0 : Volatile.Read(ref _drawTicksTotal) * 1000.0 / Stopwatch.Frequency / n;
        }
    }

    /// <summary>已执行的绘制操作次数。</summary>
    public long DrawCount => Volatile.Read(ref _drawCount);

    /// <summary>清零绘制统计（供基准测试分段测量）。</summary>
    public void ResetDrawStats()
    {
        Interlocked.Exchange(ref _lastDrawTicks, 0);
        Interlocked.Exchange(ref _drawTicksTotal, 0);
        Interlocked.Exchange(ref _drawCount, 0);
    }

    private readonly HashSet<string> _hidden = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>_hidden 的不可变快照，仅在可见性变化时重建；渲染线程直接读它。</summary>
    private HashSet<string> _hiddenSnapshot = NoHiddenLayers;

    public ViewportState Viewport { get; } = new();

    /// <summary>文本绘制的最小像素高度，低于此值跳过。</summary>
    public float MinTextPixelSize { get; set; } = DefaultMinTextPixelSize;

    public DrawingView()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    /// <summary>
    /// 替换显示的图纸数据。
    ///
    /// <b>所有权语义</b>：传入的 <see cref="DisplayList"/> 由本控件接管（不必自行释放），
    /// 下次替换或控件销毁时归还。传入前若还持有旧实例，请先自行 Release。
    /// 这里对旧实例做 Release 而不是直接抛弃 —— 绘制操作可能仍持有一份引用，
    /// 计数归零时才会真正释放原生几何（见 <see cref="DisplayList"/> 的说明）。
    /// </summary>
    public void SetDisplay(DisplayList? display)
    {
        var old = _display;
        _display = display;

        // 初始可见性：图层被关闭/冻结的，打开时即隐藏
        _hidden.Clear();
        if (display is not null)
            foreach (var name in display.InitiallyHiddenLayers)
                _hidden.Add(name);
        _hiddenSnapshot = _hidden.Count == 0
            ? NoHiddenLayers
            : new HashSet<string>(_hidden, StringComparer.OrdinalIgnoreCase);

        // 换图后旧实体的选择无意义：清空（不再触发 SelectionChanged，调用方自己感知换图）
        _selectedSnapshot = Array.Empty<CadEntity>();

        // 叠加高亮同样按图存在：其几何是上一张图的世界坐标，留着只会画到错误的位置
        ClearOverlay();

        _pendingFit = display is not null;

        // 线型路径效果按图缓存（间隔是世界单位，随画布矩阵缩放），换图即失效
        DisposeDashEffects();

        // 同一实例重复 SetDisplay 时不能释放（否则会把正在使用的数据释放掉）
        if (!ReferenceEquals(old, display)) old?.Release();

        InvalidateVisual();
    }

    /// <summary>线型图案整段在屏幕上低于此像素数即按实线绘制。</summary>
    private const float MinDashPixels = 4f;

    /// <summary>
    /// 取（并缓存）线型间隔对应的 Skia 路径效果。
    /// null = 按实线画：无线型，或整段图案在屏幕上不足 <see cref="MinDashPixels"/> 像素
    /// （AutoCAD 语义：线型过密时退化为实线，否则缩到像素级只剩一片灰雾）。
    /// </summary>
    private SKPathEffect? DashEffectOf(float[]? pattern, float zoom)
    {
        if (pattern is null || pattern.Length < 2) return null;
        double total = 0;
        for (int i = 0; i < pattern.Length; i++) total += pattern[i];
        if (total * zoom < MinDashPixels) return null;

        if (_dashEffects.TryGetValue(pattern, out var hit)) return hit;
        var eff = SKPathEffect.CreateDash(pattern, 0);
        _dashEffects[pattern] = eff;
        return eff;
    }

    private void DisposeDashEffects()
    {
        foreach (var eff in _dashEffects.Values) eff.Dispose();
        _dashEffects.Clear();
    }

    // ---------- 图层可见性 ----------
    // 用方法而非公开可变集合：绘制操作在渲染线程执行，必须拿到不可变快照。

    public bool IsLayerHidden(string layer) => _hidden.Contains(layer);

    public void SetLayerHidden(string layer, bool hidden)
    {
        bool changed = hidden ? _hidden.Add(layer) : _hidden.Remove(layer);
        if (!changed) return;
        _hiddenSnapshot = _hidden.Count == 0
            ? NoHiddenLayers
            : new HashSet<string>(_hidden, StringComparer.OrdinalIgnoreCase);
        InvalidateVisual();
    }

    // ---------- 视口 ----------

    /// <summary>
    /// 「适应窗口」用哪个范围：true=核心区（默认，过滤远处孤立实体）；
    /// false=全量 <see cref="DisplayList.Extents"/>（看清孤立实体时用，画面会变小）。
    /// </summary>
    public bool FitUseCoreExtents { get; set; } = true;

    /// <summary>
    /// 只更新视口，**不**请求重绘。
    /// 切勿在此调用 InvalidateVisual：本方法会被 Render 调用，而在渲染过程中使 Visual
    /// 失效会抛 InvalidOperationException（"Visual was invalidated during the render pass"）。
    /// </summary>
    private void UpdateFit()
    {
        if (_display is null) return;
        var target = FitUseCoreExtents ? _display.CoreExtents : _display.Extents;
        Viewport.Fit(target, (float)Bounds.Width, (float)Bounds.Height);
    }

    /// <summary>适应窗口（菜单 / 按钮 / 双击调用，会请求重绘）。coreOnly=false 按全量范围。</summary>
    public void FitToContent(bool coreOnly = true)
    {
        FitUseCoreExtents = coreOnly;
        UpdateFit();
        InvalidateVisual();
    }

    public void ZoomBy(float factor)
    {
        Viewport.ZoomAt(factor, new SKPoint((float)Bounds.Width / 2, (float)Bounds.Height / 2));
        InvalidateVisual();
    }

    // ---------- 输入 ----------

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        var factor = MathF.Pow(1.2f, (float)e.Delta.Y);
        var p = e.GetCurrentPoint(this).Position;
        Viewport.ZoomAt(factor, new SKPoint((float)p.X, (float)p.Y));
        InvalidateVisual();
        e.Handled = true;
        base.OnPointerWheelChanged(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_bandStart is not null)
            {
                _bandStart = null;
                _bandActive = false;
                InvalidateVisual();
            }
            else
            {
                ClearSelection();
            }
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var pt = e.GetCurrentPoint(this);
        if (pt.Properties.IsMiddleButtonPressed || pt.Properties.IsRightButtonPressed)
        {
            if (pt.Properties.IsMiddleButtonPressed && IsMiddleDoubleClick(pt.Position))
            {
                _lastMiddlePress = null;
                FitToContent();
                e.Handled = true;
                base.OnPointerPressed(e);
                return;
            }
            _panStart = pt.Position;
            _panOffsetStart = Viewport.Offset;
            e.Pointer.Capture(this);
            e.Handled = true;
        }
        else if (pt.Properties.IsLeftButtonPressed)
        {
            _bandStart = pt.Position;
            _bandCurrent = pt.Position;
            e.Pointer.Capture(this);
            e.Handled = true;
        }
        base.OnPointerPressed(e);
    }

    private bool IsMiddleDoubleClick(Point p)
    {
        var elapsed = TimeSpan.FromSeconds(
            (Stopwatch.GetTimestamp() - _lastMiddlePressTicks) / (double)Stopwatch.Frequency);
        if (_lastMiddlePress is { } lp && elapsed <= TimeSpan.FromMilliseconds(500))
        {
            var dx = p.X - lp.X; var dy = p.Y - lp.Y;
            if (dx * dx + dy * dy <= 16) return true;
        }
        _lastMiddlePress = p;
        _lastMiddlePressTicks = Stopwatch.GetTimestamp();
        return false;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var p = e.GetCurrentPoint(this).Position;
        var pt = e.GetCurrentPoint(this);
        // 平移/框选只在对应键仍被物理按下时进行：popup（右键菜单）打开会截断
        // release 路由，_panStart/_bandStart 残留时鼠标一动画布就跑 —— 键已抬起
        // 就地清状态，下一次按下重新开始
        if (_panStart is { } start)
        {
            if (!pt.Properties.IsMiddleButtonPressed && !pt.Properties.IsRightButtonPressed)
            {
                _panStart = null;
                e.Pointer.Capture(null);
            }
            else
            {
                Viewport.Offset = new SKPoint(
                    _panOffsetStart.X + (float)(p.X - start.X),
                    _panOffsetStart.Y + (float)(p.Y - start.Y));
                InvalidateVisual();
                e.Handled = true;
            }
        }
        else if (_bandStart is { } bs)
        {
            if (!pt.Properties.IsLeftButtonPressed)
            {
                _bandStart = null;
                _bandActive = false;
                e.Pointer.Capture(null);
                InvalidateVisual();
            }
            else
            {
                _bandCurrent = p;
                var dx = p.X - bs.X; var dy = p.Y - bs.Y;
                if (dx * dx + dy * dy > 16) _bandActive = true;
                if (_bandActive) InvalidateVisual();
                e.Handled = true;
            }
        }
        base.OnPointerMoved(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_bandStart is { } bs)
        {
            var p = e.GetCurrentPoint(this).Position;
            var dx = p.X - bs.X; var dy = p.Y - bs.Y;
            if (dx * dx + dy * dy <= 16)
            {
                SelectAt(p, e.KeyModifiers.HasFlag(KeyModifiers.Control));
            }
            else if (e.KeyModifiers.HasFlag(KeyModifiers.Control)
                     && SubEntitiesInRectResolver != null
                     && _selectedSnapshot.Count > 0)
            {
                // Ctrl+框选：只框「已选中目标」的子图元（多段线段 / 块内子实体）。
                // 空结果 = 清除二级拾取（事件带空列表，宿主同步清状态）。
                var wa = Viewport.ScreenToWorld(new SKPoint(
                    (float)Math.Min(bs.X, p.X), (float)Math.Min(bs.Y, p.Y)));
                var wb = Viewport.ScreenToWorld(new SKPoint(
                    (float)Math.Max(bs.X, p.X), (float)Math.Max(bs.Y, p.Y)));
                var rect = SKRect.Create(
                    Math.Min(wa.X, wb.X), Math.Min(wa.Y, wb.Y),
                    Math.Abs(wb.X - wa.X), Math.Abs(wb.Y - wa.Y));
                SetSubPicks(SubEntitiesInRectResolver(rect, p.X >= bs.X));
            }
            else
            {
                SelectInBand();
            }
            _bandStart = null;
            _bandActive = false;
        }
        _panStart = null;
        e.Pointer.Capture(null);
        base.OnPointerReleased(e);
    }

    // ---------- 渲染 ----------

    public override void Render(DrawingContext context)
    {
        // 首次显示：在绘制前完成"适应窗口"。
        // 这里只能改视口，不能 InvalidateVisual（见 UpdateFit 注释）；同一趟渲染
        // 紧接着读取 Viewport，所以首帧就是已适应的画面，不会闪一下未适应的状态。
        if (_pendingFit && Bounds.Width > 1 && Bounds.Height > 1)
        {
            _pendingFit = false;
            UpdateFit();
        }

        if (_display is null)
        {
            var hint = new FormattedText(
                "打开 DWG / DXF 文件开始查看\n滚轮缩放 · 中键/右键拖拽平移 · 左键点选/框选 · 双击中键适应窗口",
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, Typeface.Default, 14, Brushes.Gray);
            context.DrawText(hint, new Point(16, 16));
            return;
        }

        context.Custom(new CadDrawOperation(
            this, new Rect(Bounds.Size), _display, Viewport.Zoom, Viewport.Offset,
            _hiddenSnapshot, MinTextPixelSize, MinEntityPixelSize, _overlaySnapshot));

        if (_bandActive && _bandStart is { } bs)
        {
            var rect = new Rect(
                Math.Min(bs.X, _bandCurrent.X), Math.Min(bs.Y, _bandCurrent.Y),
                Math.Abs(_bandCurrent.X - bs.X), Math.Abs(_bandCurrent.Y - bs.Y));
            bool window = _bandCurrent.X >= bs.X;
            context.FillRectangle(window ? BandWindowFill : BandCrossingFill, rect);
            context.DrawRectangle(window ? BandWindowPen : BandCrossingPen, rect);
        }
    }

    private sealed class CadDrawOperation : ICustomDrawOperation
    {
        private readonly DrawingView _owner;
        private readonly Rect _bounds;
        private readonly DisplayList _list;
        private readonly float _zoom;
        private readonly SKPoint _offset;
        private readonly HashSet<string> _hidden;
        private readonly float _minTextPx;
        private readonly float _minEntityPx;
        private readonly OverlayLayer? _overlay;
        private readonly SKColor _bg;

        public CadDrawOperation(DrawingView owner, Rect bounds, DisplayList list, float zoom, SKPoint offset,
                                HashSet<string> hidden, float minTextPx, float minEntityPx,
                                OverlayLayer? overlay)
        {
            _owner = owner;
            _bg = owner.BackgroundColor;
            _bounds = bounds;
            _list = list;
            _zoom = zoom;
            _offset = offset;
            _hidden = hidden;
            _minTextPx = minTextPx;
            _minEntityPx = minEntityPx;
            _overlay = overlay;
            // 绘制操作的生命周期由 Avalonia 管理，而 UI 线程随时可能 SetDisplay 换图。
            // 持有一份引用（在 Dispose 中归还），保证绘制期间几何不会被提前释放。
            list.AddRef();
            overlay?.AddRef();
        }

        public Rect Bounds => _bounds;
        public bool HitTest(Point p) => _bounds.Contains(p);
        public bool Equals(ICustomDrawOperation? other) => false;

        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _list.Release();
                _overlay?.Release();
            }
        }

        public void Render(ImmediateDrawingContext context)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                RenderCore(context);
            }
            finally
            {
                long dt = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                Interlocked.Exchange(ref _owner._lastDrawTicks, dt);
                Interlocked.Add(ref _owner._drawTicksTotal, dt);
                Interlocked.Increment(ref _owner._drawCount);
            }
        }

        /// <summary>
        /// 线宽的描边宽度（世界单位 —— 画布已 concat 世界→屏幕矩阵，Skia 按世界单位取宽度）。
        /// 口径（模型空间固定像素 / 图纸空间真实单位）见 <see cref="LineWeightMetrics"/>。
        /// </summary>
        private float LineWidthWorld(int mm100)
            => LineWeightMetrics.ToWorldStroke(mm100, _list.IsPaperSpace, _zoom, _list.WorldUnitsPerMm);

        private void RenderCore(ImmediateDrawingContext context)
        {
            var leaseFeature = context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) as ISkiaSharpApiLeaseFeature;
            if (leaseFeature is null) return; // 软件渲染环境：P0 暂不绘制

            using var lease = leaseFeature.Lease();
            var canvas = lease.SkCanvas;
            float w = (float)_bounds.Width, h = (float)_bounds.Height;

            canvas.Save();
            // 先裁剪再清屏：Clear 会无视裁剪区，若在 ClipRect 之前调用会刷到控件外部。
            canvas.ClipRect(SKRect.Create(0, 0, w, h));
            canvas.Clear(_bg);

            var worldToScreen = new SKMatrix(_zoom, 0, _offset.X, 0, -_zoom, _offset.Y, 0, 0, 1);
            var worldRect = ScreenRectToWorld(w, h);
            float hairline = 1f / MathF.Max(_zoom, 1e-6f);

            // 每帧只分配 3 个 paint（而非每实体一个）。
            // 注意：这里刻意保持 IsAntialias = true —— 实测关闭批次抗锯齿会让这一档
            // 从 43.9ms 恶化到 179.2ms（Skia 走不到细线快速路径），故不采用
            // ReadCAD 的 EnableBulkLineAntialias=false 设置。
            using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke };
            using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
            using var textPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };

            var baked = _list.Baked;
            if (baked is not null && ShouldUseBaked(baked, worldRect))
            {
                // ---- 批次路径：DrawPath 次数 = 批次数，且按 LOD 整档跳过亚像素几何 ----
                canvas.Save();
                canvas.Concat(worldToScreen);
                DrawBuckets(canvas, stroke, fill, baked, worldRect, hairline);
                canvas.Restore();
                DrawTexts(canvas, textPaint, baked.UnbatchedEntities, worldRect);
                DrawPointMarks(canvas, baked.UnbatchedEntities, worldRect, h);
            }
            else
            {
                // ---- 逐实体路径：R树剔除后逐条绘制 ----
                var visible = _list.Index.Query(worldRect);
                canvas.Save();
                canvas.Concat(worldToScreen);
                DrawStrokeEntities(canvas, stroke, fill, visible, hairline);
                canvas.Restore();
                DrawTexts(canvas, textPaint, visible, worldRect);
                DrawPointMarks(canvas, visible, worldRect, h);
            }

            // ---- 选中高亮 pass：叠加在最上层，不走 LOD（选中了就要看得见）----
            // AutoCAD 默认样式：蓝色 glow（宽半透明外圈 + 实线内芯），
            // 实心填充用半透明蓝罩（与 AutoCAD 的选中半透明着色一致）。
            var selected = _owner._selectedSnapshot;
            if (selected.Count > 0)
            {
                using var hlGlow = new SKPaint
                {
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke,
                    Color = new SKColor(0, 120, 215, 80),
                    StrokeWidth = hairline * 6,
                };
                using var hlStroke = new SKPaint
                {
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke,
                    Color = new SKColor(0, 120, 215),
                    StrokeWidth = hairline * 2,
                };
                using var hlFill = new SKPaint
                {
                    IsAntialias = true,
                    Style = SKPaintStyle.Fill,
                    Color = new SKColor(0, 120, 215, 70),
                };
                canvas.Save();
                canvas.Concat(worldToScreen);
                foreach (var e in selected)
                {
                    // 渲染线程只读不可变快照 _hiddenSnapshot（与主绘制 pass 同一规则）
                    if (_owner._hiddenSnapshot.Count > 0 && _owner._hiddenSnapshot.Contains(e.Layer)) continue;
                    if (e.FillPath is { } fp) canvas.DrawPath(fp, hlFill);
                    if (e.Path is { } pth)
                    {
                        canvas.DrawPath(pth, hlGlow);
                        canvas.DrawPath(pth, hlStroke);
                    }
                }
                canvas.Restore();
            }

            // ---- 叠加高亮 pass：临时几何（多段线单段 / 填充环 / 块内子实体）----
            // 画在选中高亮之上：叠加层表达的是"当前操作目标"，通常比选中集合更精确，
            // 被选中集盖住就看不见了。与选中高亮一样不走 LOD —— 高亮就得看得见。
            var overlay = _overlay;
            if (overlay is { Shapes.Count: > 0 })
            {
                using var ovStroke = new SKPaint
                {
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke,
                };
                using var ovFill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };

                // 虚线段长按世界单位给（hairline 的倍数）→ 屏幕观感不随缩放变化。
                SKPathEffect? dash = null;
                try
                {
                    canvas.Save();
                    canvas.Concat(worldToScreen);
                    foreach (var shape in overlay.Shapes)
                    {
                        if (shape?.Path is null) continue;
                        var color = new SKColor(shape.ColorArgb);
                        float sw = shape.StrokeWidthWorld > 0 ? shape.StrokeWidthWorld : hairline * 2;

                        if (shape.Dashed && dash is null)
                            dash = SKPathEffect.CreateDash(new[] { hairline * 12, hairline * 8 }, 0);
                        ovStroke.PathEffect = shape.Dashed ? dash : null;
                        ovStroke.Color = color;
                        ovStroke.StrokeWidth = sw;

                        if (shape.Fill)
                        {
                            ovFill.Color = color.WithAlpha(shape.FillAlpha);
                            canvas.DrawPath(shape.Path, ovFill);
                        }
                        canvas.DrawPath(shape.Path, ovStroke);
                    }
                    canvas.Restore();
                }
                finally
                {
                    // ovStroke 仍持有该 effect 的原生引用，此处只是归还本方法的引用，不会悬空。
                    ovStroke.PathEffect = null;
                    dash?.Dispose();
                }
            }

            canvas.Restore();
        }

        /// <summary>
        /// 是否走批次路径。
        /// 用"视口面积 / 图纸范围面积"估算可见实体数，避免为了做决策而先跑一次
        /// 会返回十几万条目的 R树查询（那本身就是笔可观开销）。
        /// 密度不均时该估算会有偏差，但两条路输出一致，选错只影响速度不影响正确性。
        /// </summary>
        private bool ShouldUseBaked(BakedGeometry baked, SKRect worldRect)
        {
            var ext = _list.Extents;
            double totalArea = (double)ext.Width * ext.Height;
            double viewArea = (double)worldRect.Width * worldRect.Height;
            double ratio = totalArea > 0 ? Math.Min(1.0, viewArea / totalArea) : 1.0;
            double estimatedVisible = _list.Entities.Count * ratio;
            return estimatedVisible > baked.BucketCount * (double)BucketVsEntityCostRatio;
        }

        private void DrawBuckets(SKCanvas canvas, SKPaint stroke, SKPaint fill,
                                 BakedGeometry baked, SKRect worldRect, float hairline)
        {
            bool lw = _owner.ShowLineWeights;
            stroke.StrokeWidth = hairline;
            uint current = 0;
            bool hasColor = false;
            float[]? currentDash = null;
            float currentWidth = -1f;

            foreach (var cls in baked.Classes)
            {
                // LOD：整档的最大屏幕尺寸都低于阈值 → 该档全是亚像素几何，整档跳过。
                // 因为比较的是"档的上界"，所以永远不会剔掉屏幕上看得见的实体。
                if (cls.MaxPixelSize(_zoom) < _minEntityPx) continue;

                foreach (var b in cls.Buckets)
                {
                    if (_hidden.Contains(b.Layer)) continue;
                    // 整批剔除：批次包围盒不与视口相交则整批跳过
                    if (!b.Bounds.IntersectsWith(worldRect)) continue;

                    if (!hasColor || b.ColorArgb != current)
                    {
                        current = b.ColorArgb;
                        hasColor = true;
                        var c = new SKColor(ResolveColor(current));
                        stroke.Color = c;
                        fill.Color = c;
                    }

                    if (!ReferenceEquals(b.DashPattern, currentDash))
                    {
                        currentDash = b.DashPattern;
                        stroke.PathEffect = _owner.DashEffectOf(currentDash, _zoom);
                    }

                    if (b.StrokePath is not null)
                    {
                        // 线宽开关关闭 → 一律细线（AutoCAD 的 LWDISPLAY 关）。细线永远不窄于 1px。
                        float w = lw ? LineWidthWorld(b.LineWidthMm100) : hairline;
                        if (w != currentWidth) { currentWidth = w; stroke.StrokeWidth = w; }
                        canvas.DrawPath(b.StrokePath, stroke);
                    }
                    if (b.FillPath is not null) canvas.DrawPath(b.FillPath, fill);
                }
            }
        }

        private void DrawStrokeEntities(SKCanvas canvas, SKPaint stroke, SKPaint fill,
                                        IList<CadEntity> visible, float hairline)
        {
            bool lw = _owner.ShowLineWeights;
            stroke.StrokeWidth = hairline;
            foreach (var ent in visible)
            {
                if (_hidden.Contains(ent.Layer)) continue;
                if (ent.Path is not null)
                {
                    if (!PassesEntityLod(ent.Bounds)) continue;
                    stroke.Color = new SKColor(ResolveColor(ent.ColorArgb));
                    float w = lw ? LineWidthWorld(ent.LineWeightMm100) : hairline;
                    stroke.StrokeWidth = w;
                    stroke.PathEffect = _owner.DashEffectOf(ent.DashPattern, _zoom);
                    canvas.DrawPath(ent.Path, stroke);
                }
                if (ent.FillPath is not null)
                {
                    if (!PassesEntityLod(ent.Bounds)) continue;
                    fill.Color = new SKColor(ResolveColor(ent.ColorArgb));
                    canvas.DrawPath(ent.FillPath, fill);
                }
            }
            stroke.StrokeWidth = hairline;
        }

        /// <summary>
        /// 点标记（POINT）—— 屏幕空间绘制（P2-3）。
        ///
        /// <para>尺寸由 PDMODE/PDSIZE 决定：PDSIZE&gt;0 按世界单位（乘当前缩放），
        /// &lt;0 按视高百分比，=0 取默认 5% 视高。后两种**屏幕尺寸恒定**，
        /// 所以必须在屏幕坐标里画，不能跟着世界矩阵走 —— 否则缩小后缩成亚像素、
        /// 放大后糊满屏（原固定 ±1 世界单位十字的两种表现）。详见 <see cref="PointMarkStyle"/>。</para>
        ///
        /// <para>不走实体的 LOD 阈值：点符号的可见性本来就由视口高度定义，
        /// 加一层"世界尺寸不足就丢"会把所有点整批剔掉。</para>
        /// </summary>
        private void DrawPointMarks(SKCanvas canvas, IEnumerable<CadEntity> entities, SKRect worldRect, float viewportH)
        {
            using var paint = new SKPaint { IsAntialias = true, StrokeWidth = 1f };
            using var path = new SKPath();   // 复用同一个原生对象：逐点 new SKPath 会拖慢大图

            foreach (var ent in entities)
            {
                if (ent.Mark is not { } mk) continue;
                if (_hidden.Contains(ent.Layer)) continue;

                int baseShape = PointMarkStyle.BaseShape(mk.Mode);
                if (baseShape == PointMarkStyle.ModeInvisible) continue;   // 防御：转换期已滤过

                float size = PointMarkStyle.SizePx(mk.SizeSpec, _zoom, viewportH);
                float h = size * 0.5f;

                // 剔除要按"符号半径"外扩：点的包围盒是零面积，视图边界上的点
                // (适应窗口后角点正好落在边界)会被严格包含判断丢掉，看起来像实体丢失。
                float pad = (h + 1f) / MathF.Max(_zoom, 1e-6f);
                if (mk.Position.X < worldRect.Left - pad || mk.Position.X > worldRect.Right + pad ||
                    mk.Position.Y < worldRect.Top - pad || mk.Position.Y > worldRect.Bottom + pad) continue;

                var c = new SKPoint(mk.Position.X * _zoom + _offset.X, -mk.Position.Y * _zoom + _offset.Y);
                paint.Color = new SKColor(ResolveColor(ent.ColorArgb));

                // 基础形状 0 = 实心点（AutoCAD 的 PDMODE 0 就是"一个点"）
                if (baseShape == 0)
                {
                    paint.Style = SKPaintStyle.Fill;
                    canvas.DrawCircle(c, MathF.Max(0.75f, h * 0.5f), paint);
                }

                path.Reset();
                switch (baseShape)
                {
                    case 2:   // 十字
                        path.MoveTo(c.X - h, c.Y); path.LineTo(c.X + h, c.Y);
                        path.MoveTo(c.X, c.Y - h); path.LineTo(c.X, c.Y + h);
                        break;
                    case 3:   // 斜叉
                        path.MoveTo(c.X - h, c.Y - h); path.LineTo(c.X + h, c.Y + h);
                        path.MoveTo(c.X + h, c.Y - h); path.LineTo(c.X - h, c.Y + h);
                        break;
                    case 4:   // 竖线
                        path.MoveTo(c.X, c.Y - h); path.LineTo(c.X, c.Y + h);
                        break;
                }
                if (PointMarkStyle.HasCircle(mk.Mode)) path.AddCircle(c.X, c.Y, h);
                if (PointMarkStyle.HasSquare(mk.Mode)) path.AddRect(SKRect.Create(c.X - h, c.Y - h, size, size));

                if (!path.IsEmpty)
                {
                    paint.Style = SKPaintStyle.Stroke;
                    canvas.DrawPath(path, paint);
                }
            }
        }

        private void DrawTexts(SKCanvas canvas, SKPaint paint, IEnumerable<CadEntity> entities, SKRect worldRect)
        {
            foreach (var ent in entities)
            {
                if (ent.Text is null || _hidden.Contains(ent.Layer)) continue;
                if (!ent.Bounds.IntersectsWith(worldRect)) continue;
                DrawText(canvas, paint, ent.Text, ResolveColor(ent.ColorArgb));
            }
        }

        /// <summary>
        /// 实体级 LOD：包围盒最长边的屏幕尺寸不足阈值即视为不可见。
        /// 与批次路径的整档跳过是同一套判据，保证两条路画面一致。
        /// </summary>
        private bool PassesEntityLod(SKRect bounds)
        {
            if (_minEntityPx <= 0f) return true;
            return MathF.Max(bounds.Width, bounds.Height) * _zoom >= _minEntityPx;
        }

        /// <summary>与背景同色的实体反过来画（纯白实体在白底上、纯黑实体在黑底上都不可见）。批次路径与逐实体路径必须用同一套规则，否则两条路观感不一致。</summary>
        private uint ResolveColor(uint color)
        {
            var bgLuma = (_bg.Red * 299 + _bg.Green * 587 + _bg.Blue * 114) / 1000;
            if (bgLuma < 128)
                return (color & 0x00FFFFFF) == 0x00000000u ? 0xFFFFFFFFu : color;
            return (color & 0x00FFFFFF) == 0x00FFFFFFu ? 0xFF000000u : color;
        }

        private SKRect ScreenRectToWorld(float w, float h)
        {
            var p0 = new SKPoint((0 - _offset.X) / _zoom, (_offset.Y - 0) / _zoom);
            var p1 = new SKPoint((w - _offset.X) / _zoom, (_offset.Y - h) / _zoom);
            return SKRect.Create(
                MathF.Min(p0.X, p1.X), MathF.Min(p0.Y, p1.Y),
                MathF.Abs(p1.X - p0.X), MathF.Abs(p1.Y - p0.Y));
        }

        /// <summary>
        /// 文本：屏幕空间绘制，按像素高度做 LOD（ReadCAD 的 MinTextPixelSize 思路）。
        ///
        /// 字高换算必须走 <see cref="TextMetrics.FontForCapHeight"/>：CAD 字高是 cap height，
        /// 直接当 em 尺寸会小 24%（2026-10-20 受控基准图实测 0.744×）。
        /// 变换顺序固定为 旋转 → 倾斜 → 宽度因子：
        /// Skia 的 Canvas 是「后调用先生效」的矩阵左乘，写成
        /// <c>Skew</c> 再 <c>Scale</c> 才是「先横向缩放、再倾斜」——
        /// 反过来倾斜角会被宽度因子一并缩放，斜体方向随宽度因子乱飘。
        ///
        /// 多行文本（MTEXT 的 \P 已转成 \n）逐行绘制 —— Skia 的 DrawText 不处理换行符，
        /// 换行符会被当缺字画成方框。行距按实体 <see cref="TextDraw.LineSpacingFactor"/>
        /// （单倍 = 1.667 × 字高），水平对齐按 <see cref="TextDraw.LineAlign"/> **逐行**处理：
        /// AutoCAD 的 MTEXT 居中/右对齐作用于每一行而不是整块，块级偏移在转换阶段已按整块算过。
        /// </summary>
        private void DrawText(SKCanvas canvas, SKPaint paint, TextDraw td, uint color)
        {
            float capPx = td.Height * _zoom;
            if (capPx < _minTextPx) return;

            var font = TextMetrics.FontForCapHeight(capPx, out float fontScale);

            paint.Color = new SKColor(color);
            float sx = td.Position.X * _zoom + _offset.X;
            float sy = -td.Position.Y * _zoom + _offset.Y;
            canvas.Save();
            canvas.Translate(sx, sy);
            canvas.RotateDegrees(td.RotationRadians * 180f / MathF.PI);

            if (MathF.Abs(td.ObliqueRadians) > 1e-6f)
                canvas.Skew(-MathF.Tan(td.ObliqueRadians), 0f);
            if (td.WidthFactor > 0 && MathF.Abs(td.WidthFactor - 1f) > 1e-6f)
                canvas.Scale(td.WidthFactor, 1f);
            if (MathF.Abs(fontScale - 1f) > 1e-6f)
                canvas.Scale(fontScale, fontScale);   // 超大字号走"上限字体 + 画布放大"

            if (td.Text.IndexOf('\n') < 0)
            {
                canvas.DrawText(td.Text, 0, 0, font, paint);
            }
            else
            {
                // 行距按实体值（MTEXT 组码 44 已折算进 LineSpacingFactor）。
                // 注意：这些偏移处在画布**局部**坐标系里，纵向被 fontScale 放大过，
                // 必须除回去 —— 否则超大字号（> MaxCachedCapHeight）时行距会被连带放大好几倍。
                float spacing = capPx * td.LineSpacingFactor / fontScale;
                var lines = td.Text.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    float dx = 0f;
                    if (td.LineAlign != TextLineAlign.Left && lines[i].Length > 0)
                    {
                        // 行宽按**世界单位**量再换到局部：局部 x 同时被宽度因子与 fontScale 缩放。
                        // 目标屏幕宽度 = W·zoom·wf，局部宽度 = W·zoom/fontScale（wf 两端约掉）。
                        float w = TextMetrics.MeasureWidth(lines[i], td.Height) * _zoom / fontScale;
                        dx = td.LineAlign == TextLineAlign.Center ? -w * 0.5f : -w;
                    }
                    canvas.DrawText(lines[i], dx, i * spacing, font, paint);
                }
            }
            canvas.Restore();
        }
    }
}
