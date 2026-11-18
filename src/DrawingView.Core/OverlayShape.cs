using SkiaSharp;
using System.Threading.Tasks;

namespace DrawingView.Core;

/// <summary>
/// 叠加高亮图元：世界坐标的一段几何 + 画法。
///
/// <para><b>为什么需要它</b>：<c>DrawingView.SetSelection</c> 只能选中"整个 CadEntity"，
/// 但标注场景的目标可以比一个实体更细 —— 多段线的某一段、填充的某个环、
/// 块引用内的某个子实体。这些目标在 <see cref="DisplayList"/> 里都不存在独立身份
/// （一条 LwPolyline 就是一个 <see cref="CadEntity"/>），因此必须有一条
/// "任意几何的临时高亮"通路，对应 WPF/Teigha 侧的 TransientManager。</para>
///
/// <para>几何以<b>世界坐标</b>给出，控件渲染时统一套用当前 world→screen 变换，
/// 因此缩放/平移时无需重建，也不会与图纸内容错位。</para>
/// </summary>
public sealed class OverlayShape
{
    /// <summary>高亮几何（世界坐标）。所有权归 <see cref="OverlayLayer"/>。</summary>
    public required SKPath Path { get; init; }

    /// <summary>是否填充（用 <see cref="ColorArgb"/> 的色相 + <see cref="FillAlpha"/> 的透明度）。</summary>
    public bool Fill { get; init; }

    /// <summary>描边颜色（ARGB）。默认橙红，与选中高亮同一色系。</summary>
    public uint ColorArgb { get; init; } = 0xFFFF4500;

    /// <summary>填充透明度 0–255，仅 <see cref="Fill"/> 为 true 时生效。</summary>
    public byte FillAlpha { get; init; } = 96;

    /// <summary>
    /// 描边宽度（世界单位）。&lt;= 0 表示按屏幕固定 2 像素的"发丝线"，
    /// 这样在任意缩放下都保持同样的观感（与选中高亮一致）。
    /// </summary>
    public float StrokeWidthWorld { get; init; }

    /// <summary>
    /// 描边宽度（<b>屏幕像素</b>）。&gt;0 时优先于 <see cref="StrokeWidthWorld"/>。
    ///
    /// <para>用于「弱化底图」这类需要在任意缩放下都比选中高亮细一档的层
    /// （例如面域的环 / 边骨架：一条条并排铺开时，2px 会把整圈糊成一条粗带）。</para>
    /// </summary>
    public float StrokePixelWidth { get; init; }

    /// <summary>
    /// 虚线轮廓。惯例上用于表达"该目标只能整体定位、无法精确到子几何"的降级状态
    /// （例如 Region 在 ACadSharp 上取不到边界，只能整体框住）。
    /// </summary>
    public bool Dashed { get; init; }

    /// <summary>矩形轮廓 —— 文字、以及"只能整体定位"的降级目标的通用高亮形状。</summary>
    public static OverlayShape Box(SKRect rect, bool fill = false,
                                   uint colorArgb = 0xFFFF4500, bool dashed = false)
    {
        var path = new SKPath();
        path.AddRect(rect);
        return new OverlayShape
        {
            Path = path,
            Fill = fill,
            ColorArgb = colorArgb,
            Dashed = dashed,
        };
    }
}

/// <summary>
/// 一批叠加高亮几何，带引用计数（语义与 <see cref="DisplayList"/> 完全一致）。
///
/// <para><b>所有权</b>：构造完成时计数为 1（构建方持有）；交给
/// <c>DrawingView.SetOverlay</c> 即转移所有权给控件，之后不要自行 Release；
/// 绘制操作构造时 AddRef、被 Avalonia 回调 Dispose 时 Release；计数归零才释放
/// 底下的 <see cref="SKPath"/>。</para>
///
/// <para>不做引用计数就会踩与 DisplayList 同一类问题：UI 线程换掉叠加层的那一刻，
/// 渲染线程可能仍在用旧几何 —— 立刻 Dispose 就是释放后使用（原生内存直接崩）。</para>
/// </summary>
public sealed class OverlayLayer : IDisposable
{
    private int _refCount = 1;
    private int _disposedFlag;

    public required IReadOnlyList<OverlayShape> Shapes { get; init; }

    public bool IsDisposed => Volatile.Read(ref _disposedFlag) != 0;

    private static int _disposedCount;
    /// <summary>累计已释放的实例数（诊断与回归断言用）。</summary>
    public static int DisposedCount => Volatile.Read(ref _disposedCount);

    public void AddRef() => Interlocked.Increment(ref _refCount);

    public void Release()
    {
        if (Interlocked.Decrement(ref _refCount) > 0) return;
        if (DisplayList.DisposalDelayMs <= 0)
        {
            DisposeCore();
            return;
        }
        _ = Task.Delay(DisplayList.DisposalDelayMs).ContinueWith(_ => DisposeCore());
    }

    public void Dispose() => Release();

    private void DisposeCore()
    {
        if (Interlocked.Exchange(ref _disposedFlag, 1) != 0) return;
        foreach (var s in Shapes) s.Path?.Dispose();
        Interlocked.Increment(ref _disposedCount);
    }
}
