using SkiaSharp;
using System.Threading.Tasks;

namespace DrawingView.Core;

/// <summary>
/// 整图显示数据：不可变快照。后台构建完成后通过引用替换交付给渲染线程（无锁）。
///
/// <para><b>资源所有权（重要）</b>：本类持有一批 <see cref="SKPath"/>（原生内存，
/// 不在 GC 管辖范围内）。为保证"渲染线程可能仍在用它、而 UI 线程已经换了新图"这段时间里
/// 不会提前释放，采用引用计数：</para>
/// <list type="bullet">
///   <item>构造完成的实例引用计数为 1，代表"构建方持有"；</item>
///   <item>把实例交给 <c>DrawingView.SetDisplay</c> 即**转移所有权**给控件，之后不要再自行 Release；</item>
///   <item>绘制操作（<c>CadDrawOperation</c>）在构造时 <see cref="AddRef"/>、在
///         <see cref="Release"/> 被 Avalonia 调用 Dispose 时归还；</item>
///   <item>计数归零才真正释放 SKPath。</item>
/// </list>
/// <para>若不这样做，每次开图都会泄漏上一张图的全部原生几何（引入烘焙后每张图两份），
/// 连开几张 5MB 级图纸就会吃掉数 GB。</para>
/// </summary>
public sealed class DisplayList : IDisposable
{
    private int _refCount = 1;
    private int _disposedFlag;

    public required IReadOnlyList<CadEntity> Entities { get; init; }
    public required SpatialIndex Index { get; init; }
    public required IReadOnlyList<string> LayerNames { get; init; }

    /// <summary>句柄 → 首个命中实体的世界包围盒（用于标注 zoom-to-handle；句柄 0 不收录）。</summary>
    public IReadOnlyDictionary<ulong, SKRect> HandleBounds { get; init; } = new Dictionary<ulong, SKRect>();
    /// <summary>图纸范围（世界坐标），空图纸时为默认占位范围。</summary>
    public required SKRect Extents { get; init; }
    /// <summary>
    /// 核心区范围：鲁棒化的图纸范围，供「适应窗口」首屏定位（见
    /// <c>CadDocumentLoader.ComputeCoreExtents</c>）。绝大多数图纸与 <see cref="Extents"/>
    /// 一致；含远处孤立实体（插入点垃圾值）的图纸会切掉孤立簇，避免画面被压成一个小点。
    /// 索引 / 句柄查询 / 打印范围仍用 <see cref="Extents"/>。
    /// </summary>
    public required SKRect CoreExtents { get; init; }
    /// <summary>
    /// 烘焙批次（大图才有；小图为 null，直接逐实体绘制）。
    /// 见 <see cref="BakedGeometry"/> 的说明。
    /// </summary>
    public BakedGeometry? Baked { get; init; }

    /// <summary>
    /// 打开图纸时应当默认隐藏的图层（图层被关闭 <c>IsOn=false</c> 或冻结 Frozen）。
    /// 由转换器产出，<c>SetDisplay</c> 会据此初始化隐藏集合。
    /// </summary>
    public IReadOnlyCollection<string> InitiallyHiddenLayers { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 图纸里可切换的布局名（按 TabOrder；不含模型空间）。
    /// 模型空间为空时加载器会自动切到第一个有内容的布局，此时
    /// <see cref="ActiveSpace"/> 就是这里的某一项。
    /// </summary>
    public IReadOnlyList<string> LayoutNames { get; init; } = Array.Empty<string>();

    /// <summary>本实例实际渲染的空间："Model" 或某个布局名。</summary>
    public string ActiveSpace { get; init; } = "Model";

    /// <summary>
    /// 布局/视口诊断（每个参与投影的视口一行：矩形、视高、出图比例）。
    /// 供 <c>DwgCapture --report</c> 与测试定位"布局渲染空白 / 取景离谱"等问题。
    /// </summary>
    public IReadOnlyList<string> SpaceNotes { get; init; } = Array.Empty<string>();

    /// <summary>已绘制的块属性（ATTRIB）条数。</summary>
    public int AttributesDrawn { get; init; }
    /// <summary>因不可见而跳过的属性条数。</summary>
    public int AttributesInvisible { get; init; }
    /// <summary>块定义里的属性占位符（不渲染）条数。</summary>
    public int AttributeDefsSkipped { get; init; }

    /// <summary>解析出非零线宽的实体数（线宽开关打开后才影响画面，此处只做统计）。</summary>
    public int LineWeightEntities { get; init; }

    /// <summary>带几何宽度（多段线全局宽度，不是线宽）的实体数——这些画成填充轮廓。</summary>
    public int WidePolylineEntities { get; init; }

    /// <summary>POINT 点标记实体数（按 PDMODE/PDSIZE 在屏幕空间绘制）。</summary>
    public int PointMarkEntities { get; init; }

    /// <summary>从 ACIS 载荷重建出轮廓的 REGION 数（P2-7）。</summary>
    public int RegionEntities { get; init; }

    /// <summary>REGION 重建出的环总数（外环 + 内环）。</summary>
    public int RegionLoops { get; init; }

    /// <summary>ACIS 载荷无法解析、只能跳过的 REGION 数。</summary>
    public int RegionUnresolved { get; init; }

    /// <summary>是否图纸空间（布局）。线宽显示口径按空间区分，渲染期要用。</summary>
    public bool IsPaperSpace => !string.Equals(ActiveSpace, "Model", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 图纸空间里「1 mm = 多少图纸单位」。布局内线宽按真实单位显示，需要它换算；
    /// 模型空间是固定像素宽，与它无关。
    /// </summary>
    public float WorldUnitsPerMm { get; init; } = 1f;

    /// <summary>共享实例标记（<see cref="Empty"/>）：不参与引用计数与释放。</summary>
    internal bool IsSharedInstance { get; init; }

    /// <summary>实例是否已释放（诊断用）。</summary>
    public bool IsDisposed => Volatile.Read(ref _disposedFlag) != 0;

    private static int _disposedCount;
    /// <summary>累计已释放的实例数（诊断与回归测试断言用）。</summary>
    public static int DisposedCount => Volatile.Read(ref _disposedCount);

    private static int _disposalDelayMs = 300;
    /// <summary>
    /// 引用计数归零后延迟销毁原生几何的毫秒数。
    /// 合成器线程可能仍在回放持有这些 SKPath 的最后一帧绘制流（关闭文档标签页即此场景），
    /// 归零瞬间立即销毁会与回放并发，在 sk_canvas_draw_path 上触发 AccessViolation；
    /// 延迟一段远大于单帧的窗口即可避开。测试/基准工具可置 0 恢复立即释放。
    /// </summary>
    public static int DisposalDelayMs
    {
        get => Volatile.Read(ref _disposalDelayMs);
        set => Volatile.Write(ref _disposalDelayMs, value);
    }

    public static DisplayList Empty { get; } = new()
    {
        Entities = Array.Empty<CadEntity>(),
        Index = SpatialIndex.Build(Array.Empty<CadEntity>()),
        LayerNames = Array.Empty<string>(),
        Extents = SKRect.Create(0, 0, 100, 100),
        CoreExtents = SKRect.Create(0, 0, 100, 100),
        IsSharedInstance = true,
    };

    /// <summary>增加一个引用（渲染线程要跨帧持有时调用）。</summary>
    public void AddRef()
    {
        if (IsSharedInstance) return;
        Interlocked.Increment(ref _refCount);
    }

    /// <summary>归还一个引用；计数归零时释放原生几何。</summary>
    public void Release()
    {
        if (IsSharedInstance) return;
        if (Interlocked.Decrement(ref _refCount) > 0) return;
        if (DisposalDelayMs <= 0)
        {
            DisposeCore();
            return;
        }
        _ = Task.Delay(DisposalDelayMs).ContinueWith(_ => DisposeCore());
    }

    public void Dispose() => Release();

    private void DisposeCore()
    {
        // 可能被 Dispose 与终结器/重复 Release 并发触达，用原子交换保证只走一次。
        if (Interlocked.Exchange(ref _disposedFlag, 1) != 0) return;
        if (IsSharedInstance) return;

        foreach (var e in Entities)
        {
            e.Path?.Dispose();
            e.FillPath?.Dispose();
        }
        Baked?.Dispose();

        Interlocked.Increment(ref _disposedCount);
    }
}
