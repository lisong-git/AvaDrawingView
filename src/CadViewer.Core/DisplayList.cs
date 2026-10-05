using SkiaSharp;
using System.Threading.Tasks;

namespace CadViewer.Core;

/// <summary>
/// 整图显示数据：不可变快照。后台构建完成后通过引用替换交付给渲染线程（无锁）。
///
/// <para><b>资源所有权（重要）</b>：本类持有一批 <see cref="SKPath"/>（原生内存，
/// 不在 GC 管辖范围内）。为保证"渲染线程可能仍在用它、而 UI 线程已经换了新图"这段时间里
/// 不会提前释放，采用引用计数：</para>
/// <list type="bullet">
///   <item>构造完成的实例引用计数为 1，代表"构建方持有"；</item>
///   <item>把实例交给 <c>CadViewControl.SetDisplay</c> 即**转移所有权**给控件，之后不要再自行 Release；</item>
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
    /// 烘焙批次（大图才有；小图为 null，直接逐实体绘制）。
    /// 见 <see cref="BakedGeometry"/> 的说明。
    /// </summary>
    public BakedGeometry? Baked { get; init; }

    /// <summary>
    /// 打开图纸时应当默认隐藏的图层（图层被关闭 <c>IsOn=false</c> 或冻结 Frozen）。
    /// 由转换器产出，<c>SetDisplay</c> 会据此初始化隐藏集合。
    /// </summary>
    public IReadOnlyCollection<string> InitiallyHiddenLayers { get; init; } = Array.Empty<string>();

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
