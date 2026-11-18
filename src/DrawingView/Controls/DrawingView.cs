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
    /// 文本大写字高的屏幕像素下限：低于此值不再按字形绘制，改画等宽色条。
    ///
    /// 完全跳过（老做法）的后果是整块文字区"凭空消失"：
    /// 中文图纸字高大都在 200–500 单位，整图适配时 capPx 只有 1~2px，
    /// 一屏几千条字全被吃掉，对照 ODA 少了三成以上墨量。
    /// 色条是 AutoCAD/ODA 缩到极限时的观感（字已经不可读，但"那里有字"必须还在）。
    /// </summary>
    public const float DefaultMinTextPixelSize = 2f;

    /// <summary>色条也不画的下限：再小连一像素都占不满，画了只会把图糊成一片。</summary>
    public const float DefaultMinTextBarPixelSize = 0.1f;

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

    /// <summary>逐实体路径的填充绘制计数（诊断用：seen 进入 / lodSkip 被 LOD 剔 / drawn 实际下发）。</summary>
    public static int DiagFillSeen, DiagFillLodSkip, DiagFillDrawn;

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
    ///
    /// <para>2026-10-22 重标定：原值 200 是"批次里塞的是简单细线"时代的数。P1/P2 补齐保真后
    /// （宽多段线改成轮廓填充、按线宽拆批），批次路径每帧只需 3–5ms 就能画完 8.5 万实体，
    /// 而逐实体路径要 0.45–0.6µs/个 —— 幕墙图 2x 时估算可见 2.1 万 → 仍走逐实体 → 33ms/21FPS。
    /// 按实测把比值降到 20：2x 起就切批次（2.1 万 &gt; 344×20），4x 以下仍走逐实体。</para>
    /// </summary>
    private const int BucketVsEntityCostRatio = 20;

    private DisplayList? _display;

    /// <summary>线型间隔 → Skia 路径效果（间隔为世界单位，画布矩阵负责缩放到屏幕）。</summary>
    private readonly Dictionary<float[], SKPathEffect> _dashEffects = new();
    private bool _pendingFit;
    private Point? _panStart;
    private SKPoint _panOffsetStart;
    private Point? _lastMiddlePress;
    private long _lastMiddlePressTicks;
    private KeyModifiers _modsAtPress;

    // ---------- 选择（步骤 B：点选 / 高亮 / zoom-to） ----------
    // 渲染线程读 _selectedSnapshot，UI 线程写入：替换引用而非原地改列表（不可变快照原则）。
    private IReadOnlyList<CadEntity> _selectedSnapshot = Array.Empty<CadEntity>();

    /// <summary>
    /// 选中集里代表「整个块引用」的那些组（累加语义下可以同时选中多个块）。
    ///
    /// <para>显示层把块展平了：块内每个图元都是独立 <see cref="CadEntity"/>，
    /// <see cref="CadEntity.Handle"/> 指向最外层 INSERT，<see cref="CadEntity.OwnHandle"/>
    /// 指向自己，而 INSERT 本身没有几何条目。所以"选了整块"只能表现为
    /// "选中了同一 Handle 的全部条目"，这个量本身无法从集合反推（框选也可能恰好框全），
    /// 必须显式记下来给宿主上报目标用。</para>
    /// </summary>
    private readonly List<BlockLift> _blockScopes = new();

    /// <summary>选中集里的整块组（每项 = 一个块实例的身份与成员）。</summary>
    public IReadOnlyList<BlockLift> BlockScopes => _blockScopes;

    /// <summary>
    /// 只选中了一个整块时返回它的外层句柄，否则 0（多块 / 无块都要看
    /// <see cref="BlockScopes"/>，单值表达不了）。
    /// </summary>
    public ulong BlockScopeHandle => _blockScopes.Count == 1 ? _blockScopes[0].Outer : 0;

    /// <summary>
    /// 抬升到的块实例自身句柄：<c>0</c> = 最外层块引用（整块），非 0 = 块内嵌套块实例。
    /// 显示层是扁平的，嵌套 INSERT 没有几何条目，所以"选了哪个嵌套块"也只能这样带出来。
    /// </summary>
    public ulong BlockScopeInner => _blockScopes.Count == 1 ? _blockScopes[0].Inner : 0;

    // ---------- 循环穿透（Ctrl+单击，AutoCAD 无命令状态） ----------
    // 同一位置连续 Ctrl+单击时在重叠候选间轮换；光标挪开就重新从最近的那个开始。
    private SKPoint _cycleAnchor;
    private List<CadEntity>? _cycleCandidates;
    private int _cycleIndex = -1;
    private CadEntity? _cyclePicked;

    /// <summary>
    /// 块抬升决策（宿主提供）：给定命中的显示层图元，返回要一起选中的条目与块实例身份
    /// （<see cref="BlockLift"/>）。<c>null</c> = 用默认策略（同一 <c>Handle</c> 的全部条目，
    /// 粒度 = 最外层块引用）。在位编辑块时宿主靠它把粒度收窄到「当前作用域内最内层那个块」。
    /// </summary>
    public Func<CadEntity, BlockLift?>? BlockLiftResolver { get; set; }

    /// <summary>
    /// 单击块内图元时是否抬升到整个块引用（默认开，AutoCAD 语义：点块选块）。
    /// 关掉则保持"点到哪个图元就选哪个图元"。
    /// </summary>
    public bool SelectWholeBlockOnClick { get; set; } = true;

    /// <summary>
    /// 叠加高亮层（世界坐标临时几何）。与选中集合同一套交付方式：整个引用替换，
    /// 渲染线程只读快照，不做部分更新。
    /// </summary>
    private OverlayLayer? _overlaySnapshot;

    /// <summary>
    /// 作用域骨架层（弱色底图）：进入面域子对象模式时铺出该面域全部环 / 边。
    /// 与 <see cref="_overlaySnapshot"/> 分开存放 —— 二级拾取每次点选都整层替换，
    /// 骨架挂在同一层里会被一次点击冲掉。
    /// </summary>
    private OverlayLayer? _scopeSkeletonSnapshot;
    private Point? _bandStart;
    private Point _bandCurrent;
    private bool _bandActive;
    private bool _suppressReleaseClick;

    private static readonly SolidColorBrush BandWindowFill = new(Color.FromArgb(36, 68, 138, 255));
    private static readonly SolidColorBrush BandWindowBorder = new(Color.FromRgb(68, 138, 255));
    private static readonly SolidColorBrush BandCrossingFill = new(Color.FromArgb(36, 92, 184, 92));
    private static readonly SolidColorBrush BandCrossingBorder = new(Color.FromRgb(92, 184, 92));
    private static readonly Pen BandCrossingPen = new(BandCrossingBorder, 1, DashStyle.Dash);
    private static readonly Pen BandWindowPen = new(BandWindowBorder, 1);
    private static readonly SolidColorBrush BandCuePlate = new(Color.FromArgb(208, 24, 24, 24));

    /// <summary>
    /// 直接摆出框选带（自检用）：真实拖拽要注入「按下 → 移动 → 抬起」三段输入，
    /// 无头平台很难稳定停在中间的绘制帧上，窗口模式和窗交模式的提示就没法按像素验。
    /// </summary>
    public void SetBandForTest(Point? start, Point current)
    {
        _bandStart = start;
        _bandCurrent = current;
        _bandActive = start is not null;
        InvalidateVisual();
    }

    /// <summary>选中集合变化（含清空）。UI 线程触发。</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>
    /// 左键双击（连击计数由输入层给）。宿主可用于「进入」类动作（进块 / 进面域）；
    /// 置 <see cref="CanvasDoubleClickEventArgs.Handled"/> 后画布会吞掉这次点击。
    /// </summary>
    public event EventHandler<CanvasDoubleClickEventArgs>? CanvasDoubleClicked;

    /// <summary>自检用：不经过真实连击，直接按一次双击派发（无头平台很难稳定产出 ClickCount=2）。</summary>
    public void RaiseDoubleClickForTest(Point position)
    {
        var args = new CanvasDoubleClickEventArgs(position);
        CanvasDoubleClicked?.Invoke(this, args);
        if (args.Handled)
        {
            _bandStart = null;
            _bandActive = false;
            _suppressReleaseClick = true;
        }
    }

    /// <summary>当前选中实体（不可变快照）。</summary>
    public IReadOnlyList<CadEntity> SelectedEntities => _selectedSnapshot;

    /// <summary>
    /// 选中高亮走完整样式（外圈 glow + 半透明填充）的条数上限；超过只画核心描边。
    /// 整图框选动辄上万条，完整样式是每实体多两次 <c>DrawPath</c>，会让这一趟占掉整帧一半。
    /// 传 0 或负数 = 永不降档（诊断 / 截图对比用）。
    /// </summary>
    public int SelectionGlowLimit
    {
        get => _selectionGlowLimit;
        set
        {
            if (value == _selectionGlowLimit) return;
            _selectionGlowLimit = value;
            InvalidateVisual();
        }
    }

    private int _selectionGlowLimit = 2000;

    // ---------- 淡化 / 禁止拾取（在位面域打标签） ----------
    // 与选中集合同一套交付方式：整个引用替换，渲染线程只读。
    // 语义 = 「这组图元只当背景」：画布上冲淡，点选 / 框选都跳它。
    private (ulong Outer, ulong Inner)[] _dimmedSnapshot = Array.Empty<(ulong, ulong)>();

    /// <summary>
    /// 置为「淡化且不可拾取」的实体集合（<c>(最外层句柄, 自身句柄)</c>，Inner = 0 = 整个外层）。
    /// 已在选中集里的会被摘掉 —— 淡化的图元同时是选中项的话，用户会以为它还能操作。
    /// </summary>
    public void SetDimmedTargets(IEnumerable<(ulong Outer, ulong Inner)> targets)
    {
        _dimmedSnapshot = targets?.Where(t => t.Outer != 0).Distinct().ToArray()
                          ?? Array.Empty<(ulong, ulong)>();
        var selected = _selectedSnapshot;
        if (_dimmedSnapshot.Length > 0 && selected.Count > 0)
        {
            var keep = selected.Where(e => !IsDimmedEntity(e)).ToArray();
            if (keep.Length != selected.Count)
            {
                _selectedSnapshot = keep;
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        InvalidateVisual();
    }

    /// <summary>当前淡化 / 不可拾取的实体组数（自检与诊断用）。</summary>
    public int DimmedCount => _dimmedSnapshot.Length;

    // ---------- 在位编辑块作用域 ----------
    // 「只允许拾取这一个块实例内的图元」：作用域外一律淡化且不进选中集。
    // 与 _dimmedSnapshot 分开是因为粒度不同 —— 面域模式点的是「几条要淡化的」，
    // 块模式点的是「一整个块里除它全要淡化」，按名单反推会退化成 O(实体数) 的集合，
    // 命中测试逐帧都要问一次。这里用 (外层句柄, 允许的自身句柄集合) 判 O(1)。
    private ulong _pickScopeOuter;
    private HashSet<ulong>? _pickScopeOwns;

    /// <summary>
    /// 限定可拾取范围：只有 <paramref name="outer"/> 这层里、自身句柄在
    /// <paramref name="owns"/> 中的图元正常；其余淡化且不可拾取。
    /// <paramref name="owns"/> 传 null = 该外层句柄下的全部图元（整块）。<c>outer = 0</c> = 取消作用域。
    /// </summary>
    public void SetPickScope(ulong outer, IEnumerable<ulong>? owns)
    {
        _pickScopeOuter = outer;
        _pickScopeOwns = outer == 0 ? null : (owns == null ? null : new HashSet<ulong>(owns));
        var selected = _selectedSnapshot;
        if (selected.Count > 0)
        {
            var keep = selected.Where(e => !IsDimmedEntity(e)).ToArray();
            if (keep.Length != selected.Count)
            {
                _selectedSnapshot = keep;
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        InvalidateVisual();
    }

    /// <summary>是否处于「限定拾取范围」状态。</summary>
    public bool HasPickScope => _pickScopeOuter != 0;

    /// <summary>作用域外层句柄（0 = 无）。</summary>
    public ulong PickScopeOuter => _pickScopeOuter;

    /// <summary>作用域内允许的自身句柄数；整个外层块允许时为 -1。</summary>
    public int PickScopeCount => _pickScopeOuter == 0 ? 0 : (_pickScopeOwns?.Count ?? -1);

    /// <summary>
    /// 作用域是否仍然有效（宿主判定）。返回 false 时画布立刻丢弃作用域状态。
    /// <para>作用域由宿主设置，清理却只能靠宿主回调。回调链一旦断过（视图摘下再挂回、
    /// 宿主那边已经把作用域清成空而画布没收到），画布就停在「作用域外全淡」：
    /// 整张图发灰、点哪里都选不中，只剩作用域那个块还能点（还按默认策略抬升成整块）。
    /// 这种残留不报错、不留痕，事后极难归因，所以每次拾取前都问一句 ——
    /// 让残留最多活到下一次点击，而不是活到用户重开程序。</para>
    /// </summary>
    public Func<bool>? PickScopeKeeper { get; set; }

    /// <summary>宿主不再认领作用域时，把作用域相关的画布状态整体丢弃。返回是否丢弃过。</summary>
    private bool DropPickScopeIfStale()
    {
        if (_pickScopeOuter == 0) return false;
        if (PickScopeKeeper?.Invoke() != false) return false;
        SetPickScope(0, null);
        SetScopeBody(0, 0);
        SetScopeSkeleton(null);
        SubPickOnScopeClick = false;
        SubPickOnBand = false;
        return true;
    }

    /// <summary>
    /// 作用域内单击即进二级拾取（多段线打标签：点线就是选那一段，不必先选中整条再点）。
    /// 块作用域不要开这个 —— 那里单击要先走块抬升。
    /// </summary>
    public bool SubPickOnScopeClick { get; set; }

    /// <summary>
    /// 框选直接产出子图元（面域 / 多段线的子图元模式）：普通框选走
    /// <see cref="SubEntitiesInRectResolver"/>，不再选「整条实体」。
    /// 与 <see cref="SubPickOnScopeClick"/> 是同一层级的粒度切换，一个管单击一个管框选。
    /// </summary>
    public bool SubPickOnBand { get; set; }

    // ---------- 作用域本体 ----------
    // 「正在被编辑的那一个」：完整显示、不参与选中、普通命中跳过，但仍是软命中的子对象入口。
    // 与淡化名单分开是因为这两件事被绑在同一个谓词上时互相拖累 —— 面域模式为了让本体
    // 不可拾取而把它塞进淡化名单，结果可编辑范围自己只剩 22% 不透明度，什么都看不清。
    private ulong _scopeBodyOuter;
    private ulong _scopeBodyInner;

    /// <summary>作用域本体填充的不透明度：留色感，但不能盖住环 / 边。</summary>
    private const byte ScopeBodyFillAlpha = 40;

    /// <summary>
    /// 标记作用域本体（<paramref name="outer"/> = 外层句柄，<paramref name="inner"/> = 自身句柄，0 = 整层）。
    /// 传 0 清除。设置时把它从选中集摘掉：本体是编辑上下文，不是选中项。
    /// </summary>
    public void SetScopeBody(ulong outer, ulong inner)
    {
        _scopeBodyOuter = outer;
        _scopeBodyInner = inner;
        var selected = _selectedSnapshot;
        if (_scopeBodyOuter != 0 && selected.Count > 0)
        {
            var keep = selected.Where(e => !IsScopeBody(e)).ToArray();
            if (keep.Length != selected.Count)
            {
                _selectedSnapshot = keep;
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        InvalidateVisual();
    }

    /// <summary>该图元是否就是当前作用域本体（块被多处插入时每一处实例都算）。</summary>
    public bool IsScopeBody(CadEntity? e)
    {
        if (_scopeBodyOuter == 0 || e is null || e.Handle != _scopeBodyOuter) return false;
        if (_scopeBodyInner == 0) return true;
        return e.OwnHandle == _scopeBodyInner || e.OwnHandle == e.Handle;
    }

    /// <summary>作用域本体外层句柄（0 = 无）。</summary>
    public ulong ScopeBodyOuter => _scopeBodyOuter;

    /// <summary>该图元是否处于淡化 / 不可拾取状态。</summary>
    public bool IsDimmedEntity(CadEntity e) => DimAlphaOf(e) != 0;

    /// <summary>淡化名单的 alpha（面域当背景：仍要看得出形状）。</summary>
    private const int DimAlphaList = 110;

    /// <summary>
    /// 拾取作用域外的 alpha。取 90（≈35% 亮度）而不是更暗的档位：30 时黑底上细线的
    /// 抗锯齿边缘会掉到背景阈值以下，整片线条断续甚至消失（实测 <c>10---陶板节点2.dwg</c>
    /// 进作用域后 96% 的墨迹落在亮度 21–40 档，原色档几乎清零）。
    /// </summary>
    private const int DimAlphaOutOfScope = 90;

    /// <summary>
    /// 淡化强度（0 = 不淡化）。两档：名单（面域本体当背景）/ 作用域外（只剩方位参考）。
    /// </summary>
    internal int DimAlphaOf(CadEntity e)
    {
        if (e is null) return 0;
        // 作用域本体永不禁淡化：它是可编辑范围的骨架，糊掉就看不出能点哪里
        if (IsScopeBody(e)) return 0;
        var dimmed = _dimmedSnapshot;
        if (dimmed.Length > 0)
        {
            foreach (var t in dimmed)
            {
                if (e.Handle != t.Outer) continue;
                // 顶层面域 OwnHandle == Handle，Inner 传真实句柄或 0 都应命中
                if (t.Inner == 0 || e.OwnHandle == t.Inner || e.OwnHandle == t.Outer) return DimAlphaList;
            }
        }
        if (_pickScopeOuter != 0)
        {
            if (e.Handle != _pickScopeOuter) return DimAlphaOutOfScope;
            if (_pickScopeOwns == null) return 0;
            return _pickScopeOwns.Contains(e.OwnHandle != 0 ? e.OwnHandle : e.Handle) ? 0 : DimAlphaOutOfScope;
        }
        return 0;
    }

    private List<SubEntityPick> _subPicks;
    private bool _subPickOwnsOverlay;

    /// <summary>当前二级拾取集合（空 = 无）。任何选中变化都会清除。</summary>
    public IReadOnlyList<SubEntityPick> CurrentSubPicks => _subPicks ?? (IReadOnlyList<SubEntityPick>)Array.Empty<SubEntityPick>();

    /// <summary>二级拾取集合变化时触发（含框选清空时的空列表。UI 线程）。</summary>
    public event EventHandler<IReadOnlyList<SubEntityPick>>? SubEntitiesPicked;

    /// <summary>
    /// 显式定位（按句柄 / 目标集合 / 叠加层跳转）完成后触发，带目标的世界与屏幕矩形。
    ///
    /// <para>只在这些"用户动作引起的跳转"上发；首屏适应窗口、换图、滚轮缩放、拖拽平移都不发
    /// —— 那些是视图自身行为，弹提示反而干扰。宿主接这个事件去画"已定位"反馈
    /// （<see cref="LocateMarker"/>），反馈层重绘不触发本控件的绘制操作。</para>
    /// </summary>
    public event EventHandler<ViewportLocatedEventArgs>? Located;

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
    /// <param name="includeDimmed">
    /// true 时连「淡化 / 不可拾取」的实体一起参与命中。只给子图元拾取用 ——
    /// 在位面域模式下面域本体不能进选中集，但它仍是「轮廓环 / 边」的拾取入口，
    /// 跳过它等于连子图元都选不出来。
    /// </param>
    public CadEntity? HitTest(SKPoint screenPoint, float tolerancePx = 5f, bool includeDimmed = false)
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
            // 作用域本体：看得见但不可作为整体拾取（点它要落到环 / 边）
            if (!includeDimmed && (IsDimmedEntity(e) || IsScopeBody(e))) continue;
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
    /// 有几何却没压中一律 <see cref="double.MaxValue"/>；只有完全没有几何的实体
    /// （TTF 文字按 <see cref="CadEntity.Bounds"/> 估）才退回包围盒距离。
    /// </summary>
    private static double DistanceToEntity(CadEntity e, SKPoint world, float tolWorld)
    {
        if (e.FillPath is { } fp) return FillDistance(fp, world, tolWorld);
        if (e.Path is { } pth) return StrokeDistance(pth, world, tolWorld);
        return BoundsDistance(e.Bounds, world);
    }

    /// <summary>
    /// 填充体命中：落在填充区内 = 0，否则看拾取框是否压到填充边界（膨胀描边后包含点）。
    /// 洞内部与包围盒内的空白都不命中 —— 填充体常是「有洞的环」，退包围盒会把
    /// 整张图纸的空白都算成命中区。
    /// </summary>
    private static double FillDistance(SKPath fill, SKPoint world, float tolWorld)
    {
        if (fill.Contains(world.X, world.Y)) return 0;
        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = tolWorld * 2,
        };
        using var outline = new SKPath();
        if (paint.GetFillPath(fill, outline) && outline.Contains(world.X, world.Y)) return 0;
        return double.MaxValue;
    }

    /// <summary>描边命中：路径按拾取框膨胀后包含点 = 0，否则不命中（含退化路径膨胀失败）。</summary>
    private static double StrokeDistance(SKPath stroke, SKPoint world, float tolWorld)
    {
        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = tolWorld * 2,
        };
        using var inflated = new SKPath();
        if (!paint.GetFillPath(stroke, inflated)) return double.MaxValue;
        return inflated.Contains(world.X, world.Y) ? 0 : double.MaxValue;
    }

    private static double BoundsDistance(SKRect b, SKPoint world)
    {
        float dx = MathF.Max(MathF.Max(b.Left - world.X, world.X - b.Right), 0);
        float dy = MathF.Max(MathF.Max(b.Top - world.Y, world.Y - b.Bottom), 0);
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>替换选中集合并重绘（供外部面板/程序化驱动）。</summary>
    public void SetSelection(IEnumerable<CadEntity> entities)
    {
        ClearSubPick();
        ResetBlockScope();
        ResetCycle();
        _selectedSnapshot = entities.ToArray();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>清空选中集合并重绘。</summary>
    public void ClearSelection()
    {
        ClearSubPick();
        ResetBlockScope();
        ResetCycle();
        if (_selectedSnapshot.Count == 0) return;
        _selectedSnapshot = Array.Empty<CadEntity>();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>
    /// 同一位置的全部命中，按距离由近到远（Ctrl+单击循环穿透用）。顺序必须稳定：
    /// 等距时按 (Handle, OwnHandle)，再靠 LINQ 的稳定排序保留 R 树遍历序 ——
    /// 否则连续两次 Ctrl+单击会跳到同一个对象上，循环看起来"卡住"。
    /// </summary>
    public IReadOnlyList<CadEntity> HitTestAll(SKPoint screenPoint, float tolerancePx = 5f,
                                               bool includeDimmed = false)
    {
        if (_display is null) return Array.Empty<CadEntity>();
        var world = Viewport.ScreenToWorld(screenPoint);
        float tolWorld = tolerancePx / MathF.Max(Viewport.Zoom, 1e-6f);
        var rect = SKRect.Create(world.X - tolWorld, world.Y - tolWorld, tolWorld * 2, tolWorld * 2);

        var found = new List<(CadEntity E, double D)>();
        foreach (var e in _display.Index.Query(rect))
        {
            if (_hidden.Count > 0 && _hidden.Contains(e.Layer)) continue;
            if (!includeDimmed && (IsDimmedEntity(e) || IsScopeBody(e))) continue;
            if (!e.Bounds.IntersectsWith(rect)) continue;
            var d = DistanceToEntity(e, world, tolWorld);
            if (d <= tolWorld) found.Add((e, d));
        }
        return found.OrderBy(t => t.D)
                    .ThenBy(t => t.E.Handle)
                    .ThenBy(t => t.E.OwnHandle)
                    .Select(t => t.E)
                    .ToList();
    }

    /// <summary>
    /// Ctrl+单击 · 循环穿透重叠对象：同一位置连按时逐个换到下一个候选，
    /// 换掉的只是「上一次循环选中的那个」，选择集里其它目标不受影响。
    /// 光标挪开 4px 以上或候选集合变了就重新从最近的那个开始。
    /// </summary>
    private void CyclePick(SKPoint screen)
    {
        var sameSpot = _cycleCandidates is { Count: > 0 }
                       && MathF.Abs(screen.X - _cycleAnchor.X) <= 4f
                       && MathF.Abs(screen.Y - _cycleAnchor.Y) <= 4f;
        if (!sameSpot)
        {
            _cycleCandidates = HitTestAll(screen).ToList();
            _cycleIndex = -1;
            _cycleAnchor = screen;
            _cyclePicked = null;
        }
        var cands = _cycleCandidates;
        if (cands is not { Count: > 0 })
        {
            if (!sameSpot) ClearSelection();
            return;
        }
        _cycleIndex = (_cycleIndex + 1) % cands.Count;
        var pick = cands[_cycleIndex];
        if (_cyclePicked is { } prev && !ReferenceEquals(prev, pick))
            RemoveFromSelection(new[] { prev });
        _cyclePicked = pick;
        AddToSelection(new[] { pick });
    }

    private void ResetCycle()
    {
        _cycleCandidates = null;
        _cycleIndex = -1;
        _cyclePicked = null;
    }

    /// <summary>
    /// 按世界坐标执行一次「单击选中」，路径与鼠标左键点击完全相同
    /// （命中测试 → 淡化 / 拾取作用域过滤 → 二级拾取 → 块抬升 → <see cref="SelectionChanged"/>）。
    /// 供自检与诊断脚本驱动：验证点击链路不必去合成指针事件与屏幕坐标。
    /// <paramref name="ctrl"/> / <paramref name="shift"/> 与真实按键同义（见 <see cref="SelectAt"/>）。
    /// 返回点击后的选中条目数。
    /// </summary>
    public int SelectAtWorld(SKPoint world, bool ctrl = false, bool shift = false)
    {
        if (_display is null) return 0;
        var screen = Viewport.WorldToScreen(world);
        SelectAt(new Point(screen.X, screen.Y), ctrl, shift);
        return _selectedSnapshot.Count;
    }

    /// <summary>
    /// 单击选择，AutoCAD 语义：
    /// 无修饰键 = <b>累加</b>到选择集（再点已选对象 = 保持，不取消）；
    /// <c>Shift</c> = 从选择集<b>移除</b>；<c>Ctrl</c> 在普通浏览 = 循环穿透重叠对象、
    /// 在打标签作用域内 = 子图元入口；点空白 = 清空选择集（Shift 点空白不动）。
    /// </summary>
    private void SelectAt(Point p, bool ctrl, bool shift)
    {
        // 残留作用域先落地：否则这一下点击会被它拦成「作用域外不可拾取」，
        // 用户看到的就是"点了没反应"
        DropPickScopeIfStale();
        var screen = new SKPoint((float)p.X, (float)p.Y);
        var inScope = _pickScopeOuter != 0;

        // Ctrl+单击 · 普通浏览 = 循环穿透（AutoCAD 无命令状态的选择循环）。
        // 打标签作用域内 Ctrl 是「子图元」入口，所以只在作用域外走这条。
        if (ctrl && !inScope)
        {
            CyclePick(screen);
            return;
        }
        ResetCycle();

        var hit = HitTest(screen);
        if (hit is null)
        {
            // 在位面域模式：面域本体被排除在选中之外，但点它仍然是「选轮廓环 / 边」的入口。
            // 这里用含淡化项的命中再问一次二级拾取 —— 拿到子图元就只设子图元，不设选中集。
            if (SubEntityResolver != null)
            {
                var soft = HitTest(screen, 5f, includeDimmed: true);
                if (soft != null && (IsDimmedEntity(soft) || IsScopeBody(soft)))
                {
                    var sw = Viewport.ScreenToWorld(screen);
                    var stol = 5f / MathF.Max(Viewport.Zoom, 1e-6f);
                    var spick = SubEntityResolver(soft, sw, stol);
                    if (spick != null)
                    {
                        if (shift) RemoveSubPick(spick);
                        else AddSubPick(spick);
                        return;
                    }
                }
            }
            // Shift 点空白：没有可移除的对象，选择集也不该被清（AutoCAD 只认「点空白」本身）
            if (shift) return;
            ClearSelection();
            return;
        }
        // 子图元入口只认两种：作用域内 Ctrl，和作用域内「单击即选子对象」（面域 / 多段线）。
        // 以前还有一条「再点一次已选对象」，与 AutoCAD「单击已选对象 = 保持」冲突，已去掉。
        var wantSub = (ctrl && inScope)
                      || (SubPickOnScopeClick && inScope && hit.Handle == _pickScopeOuter);
        if (SubEntityResolver != null && wantSub)
        {
            var world = Viewport.ScreenToWorld(new SKPoint((float)p.X, (float)p.Y));
            var tol = 5f / MathF.Max(Viewport.Zoom, 1e-6f);
            var pick = SubEntityResolver(hit, world, tol);
            if (pick != null)
            {
                // 子图元同样累加（AutoCAD 子对象选择也是累加），Shift 摘掉这一条
                ResetBlockScope();          // 进了子图元层级，不再是"整块"
                if (shift) RemoveSubPick(pick);
                else AddSubPick(pick);
                return;
            }
        }
        // 作用域本体不可作为整体选中：点环内空白由子图元解析接管（上面那段），
        // 解析不出子图元 = 落在无效区域（比如有洞的面域内部），什么都不选。
        if (IsScopeBody(hit)) return;
        // 块内图元：显示层把块展平了，INSERT 自身没有几何条目，所以"点块"只能表现为
        // "选中同一 Handle 的全部条目"。抬升后记下块身份（集合本身表达不了这个语义：
        // 框选也可能恰好框全），宿主据此上报整块目标；要标块内具体图元走右键子图元菜单。
        if (SelectWholeBlockOnClick && hit.OwnHandle != 0 && hit.OwnHandle != hit.Handle)
        {
            var lift = BlockLiftResolver?.Invoke(hit) ?? DefaultBlockLift(hit);
            if (lift is { Members.Count: > 0 } spec)
            {
                if (shift)
                {
                    // Shift+点块 = 整个块移出选择集（块只能整进整出，没有"少选一个成员"）
                    RemoveFromSelection(spec.Members);
                    return;
                }
                AddBlockScope(spec);
                return;
            }
            // Members 空 = 宿主判定不抬升（在位编辑块里点到的是作用域块的直接子元素）→ 走单选
        }
        if (shift)
        {
            RemoveFromSelection(new[] { hit });
            return;
        }
        AddToSelection(new[] { hit });
    }

    /// <summary>默认抬升：同一 <c>Handle</c> 的全部条目，粒度 = 最外层块引用。</summary>
    private BlockLift DefaultBlockLift(CadEntity hit)
    {
        if (_display is null) return BlockLift.None;
        var blockHits = new List<CadEntity>();
        foreach (var e in _display.Entities)
            if (e.Handle == hit.Handle) blockHits.Add(e);
        return blockHits.Count > 0
            ? new BlockLift(hit.Handle, 0, blockHits)
            : BlockLift.None;
    }

    private void ResetBlockScope()
    {
        if (_blockScopes.Count == 0) return;
        _blockScopes.Clear();
    }

    /// <summary>
    /// 把「整块」并进选择集：同一个块再次点击 = 保持（AutoCAD 不会因为再点一次就取消），
    /// 不同块各自成组 —— 宿主按 <see cref="BlockScopes"/> 上报多个块目标。
    /// </summary>
    private void AddBlockScope(BlockLift spec)
    {
        foreach (var s in _blockScopes)
            if (s.Outer == spec.Outer && s.Inner == spec.Inner)
                return;     // 已经是这个块了，别重建选中集（会闪一下）
        ClearSubPick();
        _blockScopes.Add(spec);
        var before = _selectedSnapshot.Count;
        AddToSelection(spec.Members);
        // 成员本来就在选中集里（先框选整块再点它）时粒度变了，也得让宿主重新上报
        if (_selectedSnapshot.Count == before)
            SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// AutoCAD 累加语义：把命中项加入选择集；已在集合里的保持原样（不取消）。
    /// 真正有变化时才清二级拾取 —— 重复点同一对象是「保持」，不该顺手把选好的边清掉。
    /// </summary>
    private void AddToSelection(IReadOnlyList<CadEntity> entities)
    {
        if (entities.Count == 0) return;
        var next = new List<CadEntity>(_selectedSnapshot);
        var changed = false;
        foreach (var e in entities)
        {
            if (e is null) continue;
            if (next.Exists(x => ReferenceEquals(x, e))) continue;
            next.Add(e);
            changed = true;
        }
        if (!changed) return;
        ClearSubPick();
        _selectedSnapshot = next.ToArray();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>
    /// AutoCAD Shift 语义：从选择集移除。块作用域只代表「整块」，任一成员被移走就不再成立。
    /// </summary>
    private void RemoveFromSelection(IReadOnlyList<CadEntity> entities)
    {
        if (entities.Count == 0 || _selectedSnapshot.Count == 0) return;
        var next = _selectedSnapshot.Where(e => !entities.Any(x => ReferenceEquals(x, e))).ToArray();
        if (next.Length == _selectedSnapshot.Count) return;
        for (var i = _blockScopes.Count - 1; i >= 0; i--)
            if (entities.Any(x => x.Handle == _blockScopes[i].Outer))
                _blockScopes.RemoveAt(i);
        _selectedSnapshot = next;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    private void SetSubPicks(IReadOnlyList<SubEntityPick> picks)
    {
        ResetBlockScope();          // 选了子图元就不再代表"整块"（宿主据此上报目标）
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

    /// <summary>单击 / Ctrl+点追加一条子图元（AutoCAD 子对象选择同样累加）。</summary>
    private void AddSubPick(SubEntityPick pick) => AddSubEntityPicks(new[] { pick });

    /// <summary>Shift+点移除：按 (Handle, OwnHandle, SubPath) 摘掉这一条，其余保持不变。</summary>
    private void RemoveSubPick(SubEntityPick pick) => RemoveSubEntityPicks(new[] { pick });

    /// <summary>
    /// Shift+框选移除：从当前子图元拾取里摘掉给定集合。传进来但本来就没被选中的那些
    /// 是高亮几何的唯一持有者，要自己放掉；已在集合里的随旧叠加层释放一并回收。
    /// </summary>
    public void RemoveSubEntityPicks(IReadOnlyList<SubEntityPick> picks)
    {
        if (picks == null || picks.Count == 0) return;
        var live = new bool[picks.Count];
        var next = new List<SubEntityPick>();
        if (_subPicks is { Count: > 0 })
        {
            foreach (var q in _subPicks)
            {
                var hitIdx = -1;
                for (var i = 0; i < picks.Count; i++)
                {
                    if (picks[i] != null && SameSubPick(q, picks[i])) { hitIdx = i; break; }
                }
                if (hitIdx >= 0) { live[hitIdx] = true; continue; }
                next.Add(ClonePick(q));
            }
        }
        if (!Array.Exists(live, x => x)) return;    // 一条都不在集合里：什么都没变
        for (var i = 0; i < picks.Count; i++)
            if (!live[i] && picks[i] != null) DisposePick(picks[i]);
        SetSubPicks(next);
    }

    /// <summary>
    /// 程序化追加二级拾取（与单击 / Ctrl+点同一条合成规则）：按 (Handle, OwnHandle, SubPath)
    /// 去重，已存在的不再重复加入、也不移除。空集合 / 全部重复时不改动现有拾取。
    /// </summary>
    public void AddSubEntityPicks(IReadOnlyList<SubEntityPick> picks)
    {
        if (picks == null || picks.Count == 0) return;
        var next = new List<SubEntityPick>();
        if (_subPicks is { Count: > 0 })
        {
            foreach (var q in _subPicks)
                next.Add(ClonePick(q));
        }
        var changed = false;
        foreach (var p in picks)
        {
            if (p == null) continue;
            if (next.Exists(q => SameSubPick(q, p)))
            {
                // 这一份是高亮几何的唯一持有者：不进叠加层就得自己放掉
                if (!IsLivePick(p)) DisposePick(p);
                continue;
            }
            next.Add(p);
            changed = true;
        }
        if (changed)
            SetSubPicks(next);
        else
            SubEntitiesPicked?.Invoke(this, CurrentSubPicks);   // 无变化也要回一次状态（宿主提示用）
    }

    private bool IsLivePick(SubEntityPick p)
    {
        if (_subPicks == null) return false;
        foreach (var q in _subPicks)
            if (ReferenceEquals(q, p)) return true;
        return false;
    }

    private static void DisposePick(SubEntityPick p)
    {
        if (p?.Highlight == null) return;
        foreach (var s in p.Highlight)
            s?.Path?.Dispose();
    }

    private static bool SameSubPick(SubEntityPick a, SubEntityPick b)
        => a.Handle == b.Handle && a.OwnHandle == b.OwnHandle
           && string.Equals(a.SubPath, b.SubPath, System.StringComparison.Ordinal);

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
        // Shift = 移除（AutoCAD），不是追加：追加是默认行为，不需要修饰键
        SelectInRect(WorldRectOf(bs, _bandCurrent), window: _bandCurrent.X >= bs.X,
                     additive: false, remove: _modsAtPress.HasFlag(KeyModifiers.Shift));
    }

    /// <summary>屏幕两角 → 世界矩形（Y 轴翻转后取包围盒）。</summary>
    private SKRect WorldRectOf(Point a, Point b)
    {
        var wa = Viewport.ScreenToWorld(new SKPoint((float)Math.Min(a.X, b.X), (float)Math.Min(a.Y, b.Y)));
        var wb = Viewport.ScreenToWorld(new SKPoint((float)Math.Max(a.X, b.X), (float)Math.Max(a.Y, b.Y)));
        return SKRect.Create(
            MathF.Min(wa.X, wb.X), MathF.Min(wa.Y, wb.Y),
            MathF.Abs(wb.X - wa.X), MathF.Abs(wb.Y - wa.Y));
    }

    /// <summary>
    /// 框选入口（世界矩形）。子图元模式下先问 <see cref="SubEntitiesInRectResolver"/>
    /// —— 空结果 = 清空选择的子图元与实体；否则走普通整实体框选。
    /// <paramref name="additive"/> 追加 / <paramref name="remove"/>（Shift）移除，
    /// 都为 false 时替换整个选择集 —— 与单击同一套 AutoCAD 语义。
    /// 返回本次命中的目标数（子图元模式下是子图元数，否则是实体数）。
    /// </summary>
    public int SelectInRect(SKRect worldRect, bool window, bool additive = false, bool remove = false)
    {
        // 与单击同一条口径：残留作用域会把这些图元全判成"作用域外"，
        // 框选于是只剩作用域那个块里的条目可选
        DropPickScopeIfStale();
        if (SubPickOnBand && SubEntitiesInRectResolver != null)
        {
            var picks = SubEntitiesInRectResolver(worldRect, window);
            if (picks is not { Count: > 0 })
            {
                // Shift 框到空白：没有可移除的，已有拾取不该被清
                if (remove) return 0;
                SetSubPicks(Array.Empty<SubEntityPick>());
                ClearSelection();
                return 0;
            }
            if (remove) RemoveSubEntityPicks(picks);
            else if (additive) AddSubEntityPicks(picks);
            else SetSubPicks(picks);
            return picks.Count;
        }
        return SelectByRectangle(worldRect, window, additive, remove);
    }

    /// <summary>
    /// 矩形框选整实体（AutoCAD 的「窗选 / 窗交」）。<paramref name="window"/> = 左→右（完全包含），
    /// false = 右→左（接触即选）。判定按<b>图元真实几何</b>而不是包围盒（见
    /// <see cref="RectPick.EntityHit"/>）：圆角矩形的方角、圆弧包围盒的空角、有洞填充的
    /// 洞内空白都不算命中。窗口内的候选仍由空间索引按包围盒预筛。
    /// <paramref name="additive"/> / <paramref name="remove"/> 见
    /// <see cref="SelectInRect"/>。返回命中的条目数（不是改动后的选择集大小）。
    /// </summary>
    public int SelectByRectangle(SKRect worldRect, bool window, bool additive = false, bool remove = false)
    {
        if (_display is null) return 0;
        var hits = new List<CadEntity>();
        foreach (var e in _display.Index.Query(worldRect))
        {
            if (_hidden.Count > 0 && _hidden.Contains(e.Layer)) continue;
            if (IsDimmedEntity(e)) continue;      // 框选同样跳过淡化的面域本体
            if (!RectPick.EntityHit(e, worldRect, window)) continue;
            hits.Add(e);
        }
        if (remove) RemoveFromSelection(hits);
        else if (additive) AddToSelection(hits);
        else SetSelection(hits);
        return hits.Count;
    }

    /// <summary>
    /// 缩放到世界矩形（padding &gt; 1 时留白）。对应 WPF/Teigha 的 Zoom(Extents3d, scale)。
    /// <paramref name="notify"/> = true 时视口落定后触发 <see cref="Located"/>
    /// （用户显式跳转用；首屏适应窗口、换图不传）。
    /// </summary>
    public void ZoomTo(SKRect worldRect, float padding = 1.2f, bool notify = false)
    {
        float padX = worldRect.Width * (padding - 1f) / 2f;
        float padY = worldRect.Height * (padding - 1f) / 2f;
        var inflated = new SKRect(
            worldRect.Left - padX, worldRect.Top - padY,
            worldRect.Right + padX, worldRect.Bottom + padY);
        Viewport.Fit(inflated, (float)Bounds.Width, (float)Bounds.Height);
        InvalidateVisual();
        // 先落视口再算屏幕矩形：环要套在目标上，不能套在跳转前的位置
        if (notify) RaiseLocated(worldRect);
    }

    /// <summary>缩放到实体集合包围盒（对应 WPF 版 Zoom(ObjectId[])）。</summary>
    public void ZoomTo(IEnumerable<CadEntity> entities, float padding = 1.2f, bool notify = false)
    {
        bool any = false;
        float l = float.MaxValue, t = float.MaxValue, r = float.MinValue, btm = float.MinValue;
        foreach (var e in entities)
        {
            any = true;
            l = MathF.Min(l, e.Bounds.Left); t = MathF.Min(t, e.Bounds.Top);
            r = MathF.Max(r, e.Bounds.Right); btm = MathF.Max(btm, e.Bounds.Bottom);
        }
        if (any) ZoomTo(SKRect.Create(l, t, r - l, btm - t), padding, notify);
    }

    /// <summary>按 ACadSharp 句柄定位（标注面板点击跳转用）；命中返回 true。</summary>
    public bool ZoomTo(ulong handle, float padding = 2.5f)
    {
        if (_display is null || handle == 0) return false;
        if (!_display.HandleBounds.TryGetValue(handle, out var rect)) return false;
        ZoomTo(rect, padding, notify: true);
        return true;
    }

    /// <summary>把世界矩形换算成控件坐标（Y 轴翻转后取包围盒）。</summary>
    public Rect WorldToScreen(SKRect world)
    {
        var a = Viewport.WorldToScreen(new SKPoint(world.Left, world.Top));
        var b = Viewport.WorldToScreen(new SKPoint(world.Right, world.Bottom));
        return new Rect(
            Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
            Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
    }

    /// <summary>触发 <see cref="Located"/>：目标世界矩形 → 屏幕矩形。</summary>
    private void RaiseLocated(SKRect world)
    {
        var handler = Located;
        if (handler is null) return;
        if (!float.IsFinite(world.Left) || !float.IsFinite(world.Top)
            || !float.IsFinite(world.Width) || !float.IsFinite(world.Height)) return;
        handler(this, new ViewportLocatedEventArgs(world, WorldToScreen(world), Viewport.Zoom));
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
        ZoomTo(_selectedSnapshot, padding, notify: true);
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

    /// <summary>当前作用域骨架层的图元数（诊断 / 自检用）。</summary>
    public int ScopeSkeletonShapeCount => _scopeSkeletonSnapshot?.Shapes.Count ?? 0;

    /// <summary>
    /// 替换作用域骨架层；传 null 等同于清除。所有权同 <see cref="SetOverlay"/>。
    ///
    /// <para>骨架画在二级拾取高亮<b>之下</b>：它表达的是「这些子对象可选」，
    /// 而拾取层表达「这几个已选」。两层分开后，点击只替换拾取层 ——
    /// 骨架不需要跟着重算（面域进入一次，环 / 边集合在本次作用域内不变）。</para>
    /// </summary>
    public void SetScopeSkeleton(OverlayLayer? layer)
    {
        var old = _scopeSkeletonSnapshot;
        if (ReferenceEquals(old, layer)) return;
        _scopeSkeletonSnapshot = layer;
        if (old is not null) old.Release();
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
        ZoomTo(SKRect.Create(l, t, r - l, b - t), padding, notify: true);
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
        ResetPassStats();
    }

    // ---- 分段耗时诊断（DrawingView_RENDER_SPLIT=1 时累计）----
    // 只回答一个问题：24ms 里批次、文本、点标记各占多少。默认为关，关掉时零开销。

    /// <summary>分段诊断开关：环境变量 <c>DrawingView_RENDER_SPLIT=1</c>。</summary>
    public static bool RenderSplitEnabled { get; } =
        Environment.GetEnvironmentVariable("DrawingView_RENDER_SPLIT") == "1";

    /// <summary>
    /// 全部画笔的抗锯齿开关（默认开）。<b>只给像素对拍用</b>：ODA 出图几乎不做抗锯齿
    /// （实测 161,529 个非零像素里 99.8% 亮度 &gt;40），我们开 AA 后每条细线四周都带一圈
    /// 灰边（同一张图 230,480 个非零像素里 65,261 落在 7~40），阈值一卡就把"我们画了但更淡"
    /// 判成漏画。对拍时关掉，两边的墨迹口径才可比。
    ///
    /// <para><b>不要在产品里关</b>：实测关掉会让批次路径从 43.9ms 恶化到 179.2ms
    /// （Skia 走不到细线快速路径）。所以默认 true，由 <c>--no-aa</c> /
    /// <c>DrawingView_NO_AA=1</c> 显式打开。</para>
    /// </summary>
    public static bool Antialias { get; set; } =
        Environment.GetEnvironmentVariable("DrawingView_NO_AA") != "1";

    /// <summary>批次路径累计耗时（Stopwatch ticks）。</summary>
    public static long TicksBucketPass;
    /// <summary>文本 pass 累计耗时。</summary>
    public static long TicksTextPass;
    /// <summary>点标记 pass 累计耗时。</summary>
    public static long TicksPointPass;
    /// <summary>批次描边累计耗时（诊断用）。</summary>
    public static long TicksBucketStroke;
    /// <summary>批次填充累计耗时（诊断用）。</summary>
    public static long TicksBucketFill;
    /// <summary>实际画出的点标记数（上一次绘制）。</summary>
    public static long MarksDrawn;
    /// <summary>因图层隐藏而跳过的点标记数（上一次绘制）。</summary>
    public static long MarksHiddenLayer;
    /// <summary>选中高亮 + 叠加层累计耗时。</summary>
    public static long TicksSelectPass;

    /// <summary>清空分段耗时。</summary>
    public static void ResetPassStats()
    {
        Interlocked.Exchange(ref TicksBucketPass, 0);
        Interlocked.Exchange(ref TicksTextPass, 0);
        Interlocked.Exchange(ref TicksPointPass, 0);
        Interlocked.Exchange(ref TicksSelectPass, 0);
        Interlocked.Exchange(ref TicksBucketStroke, 0);
        Interlocked.Exchange(ref TicksBucketFill, 0);
    }

    /// <summary>批次内部再拆一层：描边 / 填充 的每帧毫秒。</summary>
    public static (double Stroke, double Fill) BucketDetailMsPerFrame(long frames)
    {
        double f = frames <= 0 ? 1 : frames;
        double k = 1000.0 / Stopwatch.Frequency / f;
        return (Volatile.Read(ref TicksBucketStroke) * k, Volatile.Read(ref TicksBucketFill) * k);
    }

    /// <summary>把分段耗时折成"每帧毫秒"，顺序为 批次 / 文本 / 点标记 / 高亮。</summary>
    public static (double Bucket, double Text, double Point, double Select) PassMsPerFrame(long frames)
    {
        double f = frames <= 0 ? 1 : frames;
        double k = 1000.0 / Stopwatch.Frequency / f;
        return (Volatile.Read(ref TicksBucketPass) * k, Volatile.Read(ref TicksTextPass) * k,
                Volatile.Read(ref TicksPointPass) * k, Volatile.Read(ref TicksSelectPass) * k);
    }

    private readonly HashSet<string> _hidden = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>_hidden 的不可变快照，仅在可见性变化时重建；渲染线程直接读它。</summary>
    private HashSet<string> _hiddenSnapshot = NoHiddenLayers;

    public ViewportState Viewport { get; } = new();

    /// <summary>文本按字形绘制的最小像素高度，低于此值改画等宽色条。</summary>
    public float MinTextPixelSize { get; set; } = DefaultMinTextPixelSize;

    /// <summary>低于此像素高度连色条也不画（完全亚像素的字，画了只会糊成一团）。</summary>
    public float MinTextBarPixelSize { get; set; } = DefaultMinTextBarPixelSize;

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

        // 淡化集合同样按图存在：句柄属于上一张图，留着会把新图的图元误判成背景
        _dimmedSnapshot = Array.Empty<(ulong, ulong)>();
        _pickScopeOuter = 0;
        _pickScopeOwns = null;

        // 作用域本体 / 块抬升 / 二级拾取 / 骨架层同样按图存在，句柄和几何都属于上一张图。
        // 漏清最难受的不是画错，而是「看得见却点不中」：本体句柄在新图里撞上别的图元，
        // 命中测试会一直跳过它，用户只会说拾取坏了。
        _scopeBodyOuter = 0;
        _scopeBodyInner = 0;
        _blockScopes.Clear();
        ResetCycle();
        ClearSubPickCore();
        SetScopeSkeleton(null);
        // 粒度开关也必须跟着图走：留着上张图的「单击即选子图元 / 框选按子图元」，
        // 新图上框选会绕过整实体框选，表现成"框了没东西"
        SubPickOnScopeClick = false;
        SubPickOnBand = false;

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
        // 修饰键以按下瞬间为准：抬起时才读 e.KeyModifiers 会被"先松修饰键再松鼠标"打空
        // （注入式点击尤其明显：WM_LBUTTONUP 与 WM_KEYUP 都在队列里，处理时 Shift 已被抬起）
        _modsAtPress = e.KeyModifiers;
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
            // 双击的第二次按下同时也是「单击」。先给宿主一次接管理由（进块 / 进面域），
            // 接管了就不再把这次点击当成框选起点 —— 否则松手会在新作用域里误选一个子图元。
            if (e.ClickCount >= 2 && CanvasDoubleClicked is { } dbl)
            {
                var args = new CanvasDoubleClickEventArgs(pt.Position);
                dbl(this, args);
                if (args.Handled)
                {
                    _bandStart = null;
                    _bandActive = false;
                    _suppressReleaseClick = true;
                    e.Handled = true;
                    base.OnPointerPressed(e);
                    return;
                }
            }
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
        // 宿主已接管的双击：松手不再落到画布上（见 OnPointerPressed 的双击分支）
        if (_suppressReleaseClick)
        {
            _suppressReleaseClick = false;
            _bandStart = null;
            _bandActive = false;
            e.Pointer.Capture(null);
            base.OnPointerReleased(e);
            return;
        }
        if (_bandStart is { } bs)
        {
            var p = e.GetCurrentPoint(this).Position;
            var dx = p.X - bs.X; var dy = p.Y - bs.Y;
            if (dx * dx + dy * dy <= 16)
            {
                var ctrl = _modsAtPress.HasFlag(KeyModifiers.Control);
                var shift = _modsAtPress.HasFlag(KeyModifiers.Shift);
                SelectAt(p, ctrl, shift);
            }
            else if (_modsAtPress.HasFlag(KeyModifiers.Control) && !SubPickOnBand
                     && SubEntitiesInRectResolver != null
                     && (_selectedSnapshot.Count > 0 || _dimmedSnapshot.Length > 0))
            {
                // Ctrl+框选：只框「已选中目标」的子图元（多段线段 / 块内子实体）。
                // 在位面域模式下面域被排除出选中集，此时用淡化项当种子（宿主侧兜底）。
                // 空结果 = 清除二级拾取（事件带空列表，宿主同步清状态）。
                SetSubPicks(SubEntitiesInRectResolver(WorldRectOf(bs, p), p.X >= bs.X));
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
            _hiddenSnapshot, MinTextPixelSize, MinTextBarPixelSize, MinEntityPixelSize,
            _overlaySnapshot, _scopeSkeletonSnapshot));

        if (_bandActive && _bandStart is { } bs)
        {
            var rect = new Rect(
                Math.Min(bs.X, _bandCurrent.X), Math.Min(bs.Y, _bandCurrent.Y),
                Math.Abs(_bandCurrent.X - bs.X), Math.Abs(_bandCurrent.Y - bs.Y));
            bool window = _bandCurrent.X >= bs.X;
            context.FillRectangle(window ? BandWindowFill : BandCrossingFill, rect);
            context.DrawRectangle(window ? BandWindowPen : BandCrossingPen, rect);
            DrawBandCue(context, window);
        }
    }

    /// <summary>
    /// 框选模式提示：蓝实线 = 窗选（完全落在框内才选中），绿虚线 = 窗交（压到边界即选中）。
    /// 两种框只差颜色与线型，色弱 / 反色背景下分不清 —— 贴光标补一行字，省掉"框完才发现选多了"。
    /// </summary>
    private void DrawBandCue(DrawingContext context, bool window)
    {
        var label = new FormattedText(
            window ? "窗选" : "窗交",
            System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            Typeface.Default, 12, window ? BandWindowBorder : BandCrossingBorder);

        var maxX = Math.Max(4, Bounds.Width - label.Width - 12);
        var maxY = Math.Max(4, Bounds.Height - label.Height - 8);
        var x = Math.Clamp(_bandCurrent.X + 14, 4, maxX);
        var y = Math.Clamp(_bandCurrent.Y + 16, 4, maxY);

        context.DrawRectangle(BandCuePlate, null,
            new Rect(x - 5, y - 2, label.Width + 10, label.Height + 4), 3, 3);
        context.DrawText(label, new Point(x, y));
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
        private readonly float _minTextBarPx;
        private readonly float _minEntityPx;
        private readonly OverlayLayer? _overlay;
        private readonly OverlayLayer? _skeleton;
        private readonly SKColor _bg;

        /// <summary>
        /// 本帧是否存在需要按实体改透明度的图元（淡化名单 / 拾取作用域 / 作用域本体）。
        /// 为真时不能走批次：批次把同层同色路径合并成一次 <c>DrawPath</c>，批内改不了
        /// 单个实体的 alpha。开一次后整帧逐实体绘制，代价从 O(批数) 回到 O(可见实体数)。
        /// </summary>
        private readonly bool _hasDim;

        public CadDrawOperation(DrawingView owner, Rect bounds, DisplayList list, float zoom, SKPoint offset,
                                HashSet<string> hidden, float minTextPx, float minTextBarPx, float minEntityPx,
                                OverlayLayer? overlay, OverlayLayer? skeleton)
        {
            _owner = owner;
            _bg = owner.BackgroundColor;
            _bounds = bounds;
            _list = list;
            _zoom = zoom;
            _offset = offset;
            _hidden = hidden;
            _minTextPx = minTextPx;
            _minTextBarPx = minTextBarPx;
            _minEntityPx = minEntityPx;
            _overlay = overlay;
            _skeleton = skeleton;
            _hasDim = owner._dimmedSnapshot.Length > 0
                      || owner._pickScopeOuter != 0
                      || owner._scopeBodyOuter != 0;
            // 绘制操作的生命周期由 Avalonia 管理，而 UI 线程随时可能 SetDisplay 换图。
            // 持有一份引用（在 Dispose 中归还），保证绘制期间几何不会被提前释放。
            list.AddRef();
            overlay?.AddRef();
            skeleton?.AddRef();
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
                _skeleton?.Release();
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

            DiagFillSeen = DiagFillLodSkip = DiagFillDrawn = 0;

            var worldToScreen = new SKMatrix(_zoom, 0, _offset.X, 0, -_zoom, _offset.Y, 0, 0, 1);
            var worldRect = ScreenRectToWorld(w, h);
            float hairline = 1f / MathF.Max(_zoom, 1e-6f);

            // 每帧只分配 3 个 paint（而非每实体一个）。
            // 抗锯齿默认开：实测关闭批次抗锯齿会让这一档从 43.9ms 恶化到 179.2ms
            // （Skia 走不到细线快速路径）。对拍时才用 --no-aa 关（见 Antialias 注释）。
            using var stroke = new SKPaint { IsAntialias = Antialias, Style = SKPaintStyle.Stroke };
            using var fill = new SKPaint { IsAntialias = Antialias, Style = SKPaintStyle.Fill };
            using var textPaint = new SKPaint { IsAntialias = Antialias, Style = SKPaintStyle.Fill };

            var baked = _list.Baked;
            // 分段计时（仅在诊断开关打开时记录；关掉时 diag=false，下面的 if 全部跳过）
            bool diag = RenderSplitEnabled;
            long tMark = diag ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            long tBucket = 0, tText = 0, tPoint = 0, tSelect = 0;

            if (!_hasDim && baked is not null && ShouldUseBaked(baked, worldRect))
            {
                // ---- 批次路径：DrawPath 次数 = 批次数，且按 LOD 整档跳过亚像素几何 ----
                canvas.Save();
                canvas.Concat(worldToScreen);
                DrawBuckets(canvas, stroke, fill, baked, worldRect, hairline);
                // 未合批的不只是文字/点：带裁剪区域（XCLIP）的几何也走这条，必须在这里补画，
                // 否则一旦切到批次路径，被裁剪块里的线条会整块消失（P2-17）。
                DrawStrokeEntities(canvas, stroke, fill, baked.UnbatchedEntities, hairline);
                canvas.Restore();
                if (diag) { long n = System.Diagnostics.Stopwatch.GetTimestamp(); tBucket = n - tMark; tMark = n; }
                DrawTexts(canvas, textPaint, baked.UnbatchedEntities, worldRect);
                if (diag) { long n = System.Diagnostics.Stopwatch.GetTimestamp(); tText = n - tMark; tMark = n; }
                DrawPointMarks(canvas, baked.UnbatchedEntities, worldRect, h);
                DrawTextStrokes(canvas, stroke, baked.UnbatchedEntities, worldRect, worldToScreen, hairline);
                if (diag) { long n = System.Diagnostics.Stopwatch.GetTimestamp(); tPoint = n - tMark; tMark = n; }
            }
            else
            {
                // ---- 逐实体路径：R树剔除后逐条绘制 ----
                var visible = _list.Index.Query(worldRect);
                canvas.Save();
                canvas.Concat(worldToScreen);
                DrawStrokeEntities(canvas, stroke, fill, visible, hairline);
                canvas.Restore();
                if (diag) { long n = System.Diagnostics.Stopwatch.GetTimestamp(); tBucket = n - tMark; tMark = n; }
                DrawTexts(canvas, textPaint, visible, worldRect);
                if (diag) { long n = System.Diagnostics.Stopwatch.GetTimestamp(); tText = n - tMark; tMark = n; }
                DrawPointMarks(canvas, visible, worldRect, h);
                DrawTextStrokes(canvas, stroke, visible, worldRect, worldToScreen, hairline);
                if (diag) { long n = System.Diagnostics.Stopwatch.GetTimestamp(); tPoint = n - tMark; tMark = n; }
            }

            // ---- 选中高亮 pass：叠加在最上层，不走 LOD（选中了就要看得见）----
            // AutoCAD 默认样式：蓝色 glow（宽半透明外圈 + 实线内芯），
            // 实心填充用半透明蓝罩（与 AutoCAD 的选中半透明着色一致）。
            var selected = _owner._selectedSnapshot;
            if (selected.Count > 0)
            {
                // 选中条数过多时降档：glow 外圈 + 半透明罩是每实体多两次 DrawPath，
                // 一次框选上万条后这一趟能吃掉整帧一半以上；核心描边保留，选中范围照样一眼可见。
                int glowLimit = _owner.SelectionGlowLimit;
                bool lean = glowLimit > 0 && selected.Count > glowLimit;
                using var hlGlow = new SKPaint
                {
                    IsAntialias = Antialias,
                    Style = SKPaintStyle.Stroke,
                    Color = new SKColor(0, 120, 215, 80),
                    StrokeWidth = hairline * 6,
                };
                using var hlStroke = new SKPaint
                {
                    IsAntialias = Antialias,
                    Style = SKPaintStyle.Stroke,
                    Color = new SKColor(0, 120, 215),
                    StrokeWidth = hairline * 2,
                };
                using var hlFill = new SKPaint
                {
                    IsAntialias = Antialias,
                    Style = SKPaintStyle.Fill,
                    Color = new SKColor(0, 120, 215, 70),
                };
                canvas.Save();
                canvas.Concat(worldToScreen);
                foreach (var e in selected)
                {
                    // 渲染线程只读不可变快照 _hiddenSnapshot（与主绘制 pass 同一规则）
                    if (_owner._hiddenSnapshot.Count > 0 && _owner._hiddenSnapshot.Contains(e.Layer)) continue;
                    // 视口剔除：整图框选会带上大量框外实体，Skia 虽会裁掉像素，路径遍历照跑
                    // （闭区间判定，零宽零高的直线也要能参与）
                    var linePath = e.Path;
                    var fillPath = e.FillPath;
                    if (linePath is not null && !TouchesViewport(linePath, worldRect)) linePath = null;
                    if (fillPath is not null && !TouchesViewport(fillPath, worldRect)) fillPath = null;
                    if (linePath is null && fillPath is null) continue;

                    if (!lean && fillPath is { } fp) canvas.DrawPath(fp, hlFill);
                    if (linePath is { } pth)
                    {
                        if (!lean) canvas.DrawPath(pth, hlGlow);
                        canvas.DrawPath(pth, hlStroke);
                    }
                    else if (fillPath is { } onlyFill)
                    {
                        // 只有填充几何的实体（SOLID / 实心箭线）：降档后也不能变成"没选中"
                        canvas.DrawPath(onlyFill, hlFill);
                    }
                }
                canvas.Restore();
            }

            // ---- 作用域骨架 pass：可选子对象的弱色底图（在二级拾取高亮之下）----
            // 面域进入子对象模式时一次铺满全部环 / 边：光标没有 hover 预亮机制时，
            // 这层是「哪些边能点」的唯一可视线索。
            if (_skeleton is { Shapes.Count: > 0 })
                DrawOverlayShapes(canvas, worldToScreen, _skeleton.Shapes, worldRect, hairline);

            // ---- 叠加高亮 pass：临时几何（多段线单段 / 填充环 / 块内子实体）----
            // 画在选中高亮之上：叠加层表达的是"当前操作目标"，通常比选中集合更精确，
            // 被选中集盖住就看不见了。与选中高亮一样不走 LOD —— 高亮就得看得见。
            if (_overlay is { Shapes.Count: > 0 })
                DrawOverlayShapes(canvas, worldToScreen, _overlay.Shapes, worldRect, hairline);

            if (diag)
            {
                long n = System.Diagnostics.Stopwatch.GetTimestamp();
                tSelect = n - tMark;
                Interlocked.Add(ref TicksBucketPass, tBucket);
                Interlocked.Add(ref TicksTextPass, tText);
                Interlocked.Add(ref TicksPointPass, tPoint);
                Interlocked.Add(ref TicksSelectPass, tSelect);
            }

            canvas.Restore();
        }

        /// <summary>
        /// 画一层临时几何（世界坐标 → 屏幕）。骨架层与二级拾取层共用。
        ///
        /// <para>逐条按视口剔除：一个面域可以有上千条轮廓边，整层画会白跑几十万个
        /// 落在窗口外的像素。剔除用闭区间重叠判定 —— 竖直 / 水平的零宽包围盒
        /// 用 <see cref="SKRect.IntersectsWith(SKRect)"/> 恒判否，会整条消失。</para>
        /// </summary>
        private void DrawOverlayShapes(SKCanvas canvas, SKMatrix worldToScreen,
                                       IReadOnlyList<OverlayShape> shapes, SKRect worldRect, float hairline)
        {
            using var ovStroke = new SKPaint
            {
                IsAntialias = Antialias,
                Style = SKPaintStyle.Stroke,
            };
            using var ovFill = new SKPaint { IsAntialias = Antialias, Style = SKPaintStyle.Fill };

            // 虚线段长按世界单位给（hairline 的倍数）→ 屏幕观感不随缩放变化。
            SKPathEffect? dash = null;
            try
            {
                canvas.Save();
                canvas.Concat(worldToScreen);
                foreach (var shape in shapes)
                {
                    if (shape?.Path is null) continue;
                    if (!TouchesViewport(shape.Path, worldRect)) continue;
                    var color = new SKColor(shape.ColorArgb);
                    float sw = shape.StrokePixelWidth > 0
                        ? shape.StrokePixelWidth / MathF.Max(_zoom, 1e-6f)
                        : (shape.StrokeWidthWorld > 0 ? shape.StrokeWidthWorld : hairline * 2);

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

        /// <summary>
        /// 视口剔除的包围盒重叠判定（闭区间）：零宽 / 零高的退化包围盒也要能参与。
        /// </summary>
        private static bool TouchesViewport(SKPath path, SKRect worldRect)
        {
            var b = path.Bounds;
            return b.Left <= worldRect.Right && worldRect.Left <= b.Right
                   && b.Top <= worldRect.Bottom && worldRect.Top <= b.Bottom;
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
            bool diag = RenderSplitEnabled;
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
                        if (diag) { long n0 = System.Diagnostics.Stopwatch.GetTimestamp(); canvas.DrawPath(b.StrokePath, stroke); Interlocked.Add(ref TicksBucketStroke, System.Diagnostics.Stopwatch.GetTimestamp() - n0); }
                        else canvas.DrawPath(b.StrokePath, stroke);
                    }
                    if (b.FillPath is not null)
                    {
                        if (diag) { long n0 = System.Diagnostics.Stopwatch.GetTimestamp(); canvas.DrawPath(b.FillPath, fill); Interlocked.Add(ref TicksBucketFill, System.Diagnostics.Stopwatch.GetTimestamp() - n0); }
                        else canvas.DrawPath(b.FillPath, fill);
                    }
                }
            }
        }

        /// <summary>
        /// 该实体的淡化不透明度（0 = 按原色）。没有淡化集时恒 0，省掉逐实体的两次判断。
        /// </summary>
        private int DimOf(CadEntity e) => _hasDim ? _owner.DimAlphaOf(e) : 0;

        /// <summary>
        /// 给颜色套不透明度。<c>0</c> 与 <c>255</c> 原样返回 —— 0 在这里是「不淡化」，
        /// 不是「全透明」；直接写进 ARGB 会把实体整条抹掉。
        /// </summary>
        private static uint WithAlpha(uint color, int alpha)
            => alpha <= 0 || alpha >= 255 ? color : ((color & 0x00FFFFFFu) | ((uint)alpha << 24));

        private void DrawStrokeEntities(SKCanvas canvas, SKPaint stroke, SKPaint fill,
                                        IEnumerable<CadEntity> visible, float hairline)
        {
            bool lw = _owner.ShowLineWeights;
            stroke.StrokeWidth = hairline;
            foreach (var ent in visible)
            {
                if (_hidden.Contains(ent.Layer)) continue;
                // SHX 文字笔画由 DrawTextStrokes 单独画（这里再画一次就重了）
                if (ent.TextStrokes) continue;
                var dim = DimOf(ent);
                // 作用域本体的填充要压（原色填充会把面域糊成实心块、盖住里面的环），
                // 描边保持原色 —— 它就是可编辑范围的骨架。
                int fillDim = _owner.IsScopeBody(ent) ? ScopeBodyFillAlpha : dim;
                if (ent.Path is not null)
                {
                    if (!PassesEntityLod(ent.Bounds)) continue;
                    stroke.Color = new SKColor(WithAlpha(ResolveColor(ent.ColorArgb), dim));
                    float w = lw ? LineWidthWorld(ent.LineWeightMm100) : hairline;
                    stroke.StrokeWidth = w;
                    stroke.PathEffect = _owner.DashEffectOf(ent.DashPattern, _zoom);
                    DrawClipped(canvas, ent.Path, stroke, ent.ClipPath);
                }
                if (ent.FillPath is not null)
                {
                    DiagFillSeen++;
                    if (!PassesEntityLod(ent.Bounds)) { DiagFillLodSkip++; continue; }
                    fill.Color = new SKColor(WithAlpha(ResolveColor(ent.ColorArgb), fillDim));
                    DrawClipped(canvas, ent.FillPath, fill, ent.ClipPath);
                    DiagFillDrawn++;
                }
            }
            stroke.StrokeWidth = hairline;
        }

        /// <summary>
        /// 画一条路径，必要时先挂**画布裁剪**（P2-17：XCLIP / 视口边界）。
        ///
        /// <para>裁剪路径是世界坐标，而本函数在世界矩阵里调用（画笔已 <c>Concat(worldToScreen)</c>），
        /// 所以直接 <c>ClipPath</c> 即可，标边/填充都适用。转换期不做布尔求交 ——
        /// <c>SKPath.Op</c> 对开放路径返回空，会把线条整段删掉。</para>
        /// </summary>
        private static void DrawClipped(SKCanvas canvas, SKPath geometry, SKPaint paint, SKPath? clip)
        {
            if (clip is null)
            {
                canvas.DrawPath(geometry, paint);
                return;
            }

            canvas.Save();
            canvas.ClipPath(clip, SKClipOperation.Intersect, antialias: true);
            canvas.DrawPath(geometry, paint);
            canvas.Restore();
        }

        /// <summary>
        /// SHX 文字笔画：走批次外的描边 pass。
        ///
        /// <para>与 TTF 文本的区别只是"字由路径构成"，位置/旋转/宽度因子/倾斜在转换期已经折进几何，
        /// 所以这里只需套世界矩阵直接描。线宽用 1px 细线（AutoCAD 的 SHX 文字就是单线，
        /// 打开"显示线宽"时文字并不会变粗）。</para>
        /// </summary>
        private void DrawTextStrokes(SKCanvas canvas, SKPaint stroke, IEnumerable<CadEntity> entities,
                                     SKRect worldRect, in SKMatrix worldToScreen, float hairline)
        {
            bool any = false;
            foreach (var e in entities) if (e.TextStrokes) { any = true; break; }
            if (!any) return;

            canvas.Save();
            canvas.Concat(worldToScreen);
            stroke.Style = SKPaintStyle.Stroke;
            stroke.StrokeWidth = hairline;
            foreach (var ent in entities)
            {
                if (!ent.TextStrokes || ent.Path is null) continue;
                if (_hidden.Contains(ent.Layer)) continue;
                if (!ent.Bounds.IntersectsWith(worldRect)) continue;
                stroke.Color = new SKColor(WithAlpha(ResolveColor(ent.ColorArgb), DimOf(ent)));
                DrawClipped(canvas, ent.Path, stroke, ent.ClipPath);
            }
            canvas.Restore();
        }

        /// <summary>
        /// 点标记（POINT）—— 屏幕空间绘制（P2-3）。
        ///
        /// <para>尺寸由 PDMODE/PDSIZE 决定：PDSIZE&gt;0 按世界单位（乘当前缩放），
        /// &lt;0 按视高百分比，=0 取默认 5% 视高。后两种**屏幕尺寸恒定**，
        /// 所以必须在屏幕坐标里画，不能跟着世界矩阵走 —— 否则缩小后缩成亚像素、
        /// 放大后糊满屏（原固定 ±1 世界单位十字的两种表现）。详见 <see cref="PointMarkStyle"/>。</para>
        ///
        /// <para><b>合批（2026-10-22）</b>：原实现逐点 <c>DrawCircle</c>，`3、幕墙横方总汇.dwg`
        /// 全图 4,477 个点标记实测 29.5ms（6.6µs/个，全花在"每条一次 Skia 调用"上）。
        /// 现在按颜色分组：实心点收集成一组 <c>DrawPoints</c>，符号聚合进同一条路径一次
        /// <c>DrawPath</c>，绘制调用从 O(点数) 降到 O(颜色数)。画面完全等价。</para>
        ///
        /// <para>不走实体的 LOD 阈值：点符号的可见性本来就由视口高度定义，
        /// 加一层"世界尺寸不足就丢"会把所有点整批剔掉。</para>
        /// </summary>
        private void DrawPointMarks(SKCanvas canvas, IEnumerable<CadEntity> entities, SKRect worldRect, float viewportH)
        {
            using var paint = new SKPaint { IsAntialias = Antialias, StrokeWidth = 1f };

            Dictionary<(uint Color, int SizeEighth), (List<SKPoint> Pts, HashSet<long> Seen)>? dots = null;
            Dictionary<uint, SKPath>? symbols = null;
            int drawn = 0, hiddenLayer = 0;

            // 没有隐藏图层时直接跳过逐实体查表（大图上这一步是纯开销）。
            // 有隐藏图层时才查 _hidden（OrdinalIgnoreCase 字符串集合，单次约 100ns）。
            bool hasHidden = _hidden.Count > 0;

            foreach (var ent in entities)
            {
                if (ent.Mark is not { } mk) continue;
                if (hasHidden && _hidden.Contains(ent.Layer)) { hiddenLayer++; continue; }

                int baseShape = PointMarkStyle.BaseShape(mk.Mode);
                if (baseShape == PointMarkStyle.ModeInvisible) continue;   // 防御：转换期已滤过

                float size = baseShape == 0
                    ? PointMarkStyle.DotDiameterPx(mk.SizeSpec, _zoom)
                    : PointMarkStyle.SizePx(mk.SizeSpec, _zoom, viewportH);
                float h = size * 0.5f;

                // 剔除要按"符号半径"外扩：点的包围盒是零面积，视图边界上的点
                // (适应窗口后角点正好落在边界)会被严格包含判断丢掉，看起来像实体丢失。
                float pad = (h + 1f) / MathF.Max(_zoom, 1e-6f);
                if (mk.Position.X < worldRect.Left - pad || mk.Position.X > worldRect.Right + pad ||
                    mk.Position.Y < worldRect.Top - pad || mk.Position.Y > worldRect.Bottom + pad) continue;

                var c = new SKPoint(mk.Position.X * _zoom + _offset.X, -mk.Position.Y * _zoom + _offset.Y);
                uint color = WithAlpha(ResolveColor(ent.ColorArgb), DimOf(ent));

                if (baseShape == 0)
                {
                    // 基础形状 0 = 实心点（AutoCAD 的 PDMODE 0 就是"一个点"）
                    var key = (color, (int)MathF.Round(size * 8f));
                    if (dots is null) dots = new();
                    if (!dots.TryGetValue(key, out var grp))
                        dots[key] = grp = (new List<SKPoint>(64), new HashSet<long>());
                    // 亚像素去重：点径只有 1.5px 时，落在同一半像素上的两个点画出来一模一样。
                    // 大图全图视角下几千个点会压到同一像素（实测幕墙图 4,477 → 几十个），
                    // 去重能把逐点光栅化的成本按"可见像素数"而不是"点数"计。
                    long q = ((long)MathF.Round(c.X * 2f) << 32) ^ (uint)MathF.Round(c.Y * 2f);
                    if (grp.Seen.Add(q)) grp.Pts.Add(c);
                    drawn++;
                    continue;
                }

                if (symbols is null) symbols = new();
                if (!symbols.TryGetValue(color, out var path)) symbols[color] = path = new SKPath();

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
                drawn++;
            }

            // 点：每色一次 DrawPoints（圆头笔帽 → 圆点；笔宽即点径）
            if (dots is not null)
            {
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeCap = SKStrokeCap.Round;
                foreach (var (key, grp) in dots)
                {
                    paint.Color = new SKColor(key.Color);
                    paint.StrokeWidth = key.SizeEighth / 8f;
                    canvas.DrawPoints(SKPointMode.Points, grp.Pts.ToArray(), paint);
                }
                paint.StrokeCap = SKStrokeCap.Butt;
            }

            // 符号：每色一次 DrawPath
            if (symbols is not null)
            {
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = 1f;
                foreach (var (color, path) in symbols)
                {
                    using (path)
                    {
                        paint.Color = new SKColor(color);
                        canvas.DrawPath(path, paint);
                    }
                }
            }

            if (RenderSplitEnabled)
            {
                Volatile.Write(ref MarksDrawn, drawn);
                Volatile.Write(ref MarksHiddenLayer, hiddenLayer);
            }
        }

        private void DrawTexts(SKCanvas canvas, SKPaint paint, IEnumerable<CadEntity> entities, SKRect worldRect)
        {
            bool hasHidden = _hidden.Count > 0;
            foreach (var ent in entities)
            {
                if (ent.Text is null) continue;
                if (hasHidden && _hidden.Contains(ent.Layer)) continue;
                if (!ent.Bounds.IntersectsWith(worldRect)) continue;
                DrawText(canvas, paint, ent.Text,
                         WithAlpha(ResolveColor(ent.ColorArgb), DimOf(ent)), ent.ClipPath);
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

        /// <summary>
        /// 世界 → 屏幕矩阵。与各绘制路径里的 <c>x·zoom + offsetX</c> / <c>-y·zoom + offsetY</c>
        /// 必须完全一致 —— 裁剪路径要靠它换算到屏幕空间（P2-17）。
        /// </summary>
        private SKMatrix WorldToScreenMatrix()
        {
            var m = SKMatrix.CreateScale(_zoom, -_zoom);
            m.TransX = _offset.X;
            m.TransY = _offset.Y;
            return m;
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
        private void DrawText(SKCanvas canvas, SKPaint paint, TextDraw td, uint color, SKPath? clip = null)
        {
            float capPx = td.Height * _zoom;
            if (capPx < _minTextPx)
            {
                if (capPx >= _minTextBarPx) DrawTextBar(canvas, paint, td, capPx, color, clip);
                return;
            }

            // 裁剪（P2-17）：文字是屏幕空间绘制的，裁剪路径是世界坐标，先换算到屏幕再挂。
            // 少了这一步，被 XCLIP 的图签文字会整条画到框外面去（AutoCAD 是切掉的）。
            SKPath? screenClip = null;
            if (clip is not null)
            {
                screenClip = new SKPath(clip);
                screenClip.Transform(WorldToScreenMatrix());
                canvas.Save();
                canvas.ClipPath(screenClip, SKClipOperation.Intersect, antialias: true);
            }

            try
            {
                DrawTextCore(canvas, paint, td, color, capPx);
            }
            finally
            {
                if (screenClip is not null)
                {
                    canvas.Restore();
                    screenClip.Dispose();
                }
            }
        }

        /// <summary>文字本体（已在外层处理好剪裁）。</summary>
        private void DrawTextCore(SKCanvas canvas, SKPaint paint, TextDraw td, uint color, float capPx)
        {

            // 字体由转换阶段按样式解析好带在实体上（P2-16）：这里若换成全局默认字体重算，
            // 包围盒（按样式字体量的）会与墨迹错位。字号换算也走同一份 cap/em 比例。
            var font = td.Font.FontForCapHeight(capPx, out float fontScale);

            paint.Color = new SKColor(color);
            float sx = td.Position.X * _zoom + _offset.X;
            float sy = -td.Position.Y * _zoom + _offset.Y;
            float deg = td.RotationRadians * 180f / MathF.PI;

            if (td.Text.IndexOf('\n') < 0)
            {
                canvas.Save();
                canvas.Translate(sx, sy);
                canvas.RotateDegrees(-deg);
                ApplyTextShape(canvas, td, fontScale);
                canvas.DrawText(td.Text, 0, 0, font, paint);
                canvas.Restore();
                return;
            }

            // 多行：行推进方向必须按**世界**方向 (sin r, -cos r)，换成屏幕坐标（y 翻转）是
            // (sin r, cos r)。原来直接在旋转后的局部坐标系里加 (0, i·行距)，等价于
            // (-sin r, cos r) —— 平时（r=0/180°）看不出来，竖排 MTEXT（r=±90°）的行序整个镜像：
            // 实测 rot.dxf 四组同一句话，R90/R270 我们画到锚点的另一侧（与 ODA 相反）。
            float stepX = MathF.Sin(td.RotationRadians);
            float stepY = MathF.Cos(td.RotationRadians);
            float spacingPx = capPx * td.LineSpacingFactor;

            var lines = td.Text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                canvas.Save();
                canvas.Translate(sx + stepX * spacingPx * i, sy + stepY * spacingPx * i);
                canvas.RotateDegrees(-deg);
                ApplyTextShape(canvas, td, fontScale);

                float dx = 0f;
                if (td.LineAlign != TextLineAlign.Left && lines[i].Length > 0)
                {
                    // 行宽按**世界单位**量再换到局部：局部 x 同时被宽度因子与 fontScale 缩放。
                    // 目标屏幕宽度 = W·zoom·wf，局部宽度 = W·zoom/fontScale（wf 两端约掉）。
                    float w = td.Font.MeasureWidth(lines[i], td.Height) * _zoom / fontScale;
                    dx = td.LineAlign == TextLineAlign.Center ? -w * 0.5f : -w;
                }
                canvas.DrawText(lines[i], dx, 0, font, paint);
                canvas.Restore();
            }
        }

        /// <summary>文字形变（倾斜 → 宽度因子 → 字体上限放大），三条文本路径共用。</summary>
        private static void ApplyTextShape(SKCanvas canvas, TextDraw td, float fontScale)
        {
            if (MathF.Abs(td.ObliqueRadians) > 1e-6f)
                canvas.Skew(-MathF.Tan(td.ObliqueRadians), 0f);
            if (td.WidthFactor > 0 && MathF.Abs(td.WidthFactor - 1f) > 1e-6f)
                canvas.Scale(td.WidthFactor, 1f);
            if (MathF.Abs(fontScale - 1f) > 1e-6f)
                canvas.Scale(fontScale, fontScale);   // 超大字号走"上限字体 + 画布放大"
        }

        /// <summary>
        /// 亚像素文字：按外接框画一条等宽色条。
        /// 字已经不可读，但"那里有字"必须留在画面上 —— 否则整图适配时成片的
        /// 表格/标注区会凭空消失（对照 ODA 少三成墨量）。
        /// 宽度走 <see cref="TextMetrics.MeasureWidth"/>（世界单位），乘 zoom 得到屏幕宽度；
        /// 不套 fontScale 与倾斜：这个尺寸下两者都看不出来，套了反而要按 fontScale 除回去。
        /// </summary>
        private void DrawTextBar(SKCanvas canvas, SKPaint paint, TextDraw td, float capPx, uint color,
                                 SKPath? clip = null)
        {
            // 亚像素文字同样要裁（否则框外会出现"色条拖尾"）
            SKPath? screenClip = null;
            if (clip is not null)
            {
                screenClip = new SKPath(clip);
                screenClip.Transform(WorldToScreenMatrix());
                canvas.Save();
                canvas.ClipPath(screenClip, SKClipOperation.Intersect, antialias: true);
            }

            try { DrawTextBarCore(canvas, paint, td, capPx, color); }
            finally
            {
                if (screenClip is not null)
                {
                    canvas.Restore();
                    screenClip.Dispose();
                }
            }
        }

        private void DrawTextBarCore(SKCanvas canvas, SKPaint paint, TextDraw td, float capPx, uint color)
        {
            float thick = MathF.Max(capPx, 0.6f);
            float sx = td.Position.X * _zoom + _offset.X;
            float sy = -td.Position.Y * _zoom + _offset.Y;
            float wf = td.WidthFactor > 0f ? td.WidthFactor : 1f;
            float wfZoom = _zoom * wf;

            paint.Color = new SKColor(color);
            canvas.Save();
            canvas.Translate(sx, sy);
            canvas.RotateDegrees(-td.RotationRadians * 180f / MathF.PI);
            var lines = td.Text.Split('\n');
            float spacing = capPx * td.LineSpacingFactor;
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Length == 0) continue;
                float w = td.Font.MeasureWidth(lines[i], td.Height) * wfZoom;
                if (w <= 0f) continue;
                float dx = 0f;
                if (td.LineAlign == TextLineAlign.Center) dx = -w * 0.5f;
                else if (td.LineAlign == TextLineAlign.Right) dx = -w;
                float y = i * spacing;
                canvas.DrawRect(new SKRect(dx, y - thick, dx + MathF.Max(w, thick * 0.5f), y), paint);
            }
            canvas.Restore();
        }
    }
}
