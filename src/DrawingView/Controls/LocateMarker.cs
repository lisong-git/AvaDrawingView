using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace DrawingView.Controls;

/// <summary>
/// 「已定位」标记：叠在 <see cref="DrawingView"/> 之上的一层薄控件，在目标位置画一个方框，
/// 短暂停留后消失。
///
/// <para><b>为什么单独一个控件</b>：本控件可以「选择不重绘」，而画布不行。实测（tools/AnimProbe
/// 的 locate 模式）：画布静止时叠层重绘一次，<c>CadDrawOperation</c> 就被重跑一次
/// （7 帧 +10 次，空转对照 +0 次）—— <c>CadDrawOperation.Equals</c> 恒为 false，合成器
/// 无从复用，凡是有像素压在画布上就得整幅重画。所以反馈层能省的不是"不重画"，而是
/// **减少重画的帧数**：见 <see cref="Flash"/> 的三档策略。</para>
///
/// <para>用法：和画布放进同一个 Grid（两者同尺寸），宿主收到 <see cref="DrawingView.Located"/>
/// 后调用 <see cref="Flash"/>。自身 <see cref="InputElement.IsHitTestVisible"/> 恒为 false，
/// 不抢画布的鼠标事件。</para>
/// </summary>
public class LocateMarker : Control
{
    /// <summary>平滑淡出的时长（毫秒）。</summary>
    public double DurationMs { get; set; } = 260;

    /// <summary>低开销档的停留时长（毫秒）。</summary>
    public double HoldMs { get; set; } = 900;

    /// <summary>环线宽（像素）。</summary>
    public double RingThickness { get; set; } = 2;

    /// <summary>环的颜色。默认琥珀色：黑底图纸和亮底图纸都能看清，也不与蓝色选中高亮混淆。</summary>
    public Color RingColor { get; set; } = Color.FromRgb(0xFF, 0xB4, 0x2E);

    /// <summary>
    /// 平滑淡出的代价上限（毫秒/帧，上一帧绘制耗时）。超过就退成"出现-停留-消失"两帧。
    ///
    /// 依据：100 张样本单帧重绘中位 0.5ms、90 分位 41.6ms。12ms 以下做 16 帧淡出总代价
    /// 不到 200ms；再重的图 16 帧就是 0.5~7 秒的白卡。
    /// </summary>
    public double SmoothMaxDrawMs { get; set; } = 12;

    /// <summary>连两帧都嫌贵的上限：超过就只有状态栏文字，不画环。</summary>
    public double DrawMaxDrawMs { get; set; } = 60;

    private Rect _target;
    private bool _active;
    private bool _smooth;
    private readonly Stopwatch _clock = new();
    private DispatcherTimer? _timer;

    public LocateMarker()
    {
        IsHitTestVisible = false;
    }

    /// <summary>是否正在显示。</summary>
    public bool IsFlashing => _active;

    /// <summary>最近一次实际采用的档位（诊断用）。</summary>
    public string LastMode { get; private set; } = "none";

    /// <summary>
    /// 在给定屏幕矩形处显示定位标记。
    /// </summary>
    /// <param name="screenRect">目标在控件坐标里的矩形。</param>
    /// <param name="drawCostMs">
    /// 上一帧的画布绘制耗时（<see cref="DrawingView.LastDrawMs"/>）。据此选档：
    /// ≤ <see cref="SmoothMaxDrawMs"/> 平滑淡出；≤ <see cref="DrawMaxDrawMs"/> 出现+停留+消失
    /// （全程 2 帧重绘）；更贵则完全不画，交给状态栏文字。
    /// </param>
    public void Flash(Rect screenRect, double drawCostMs = 0)
    {
        if (!(screenRect.Width >= 0) || !(screenRect.Height >= 0)) return;
        if (screenRect.Width <= 0 && screenRect.Height <= 0) return;
        if (drawCostMs > DrawMaxDrawMs) { Hide(); LastMode = "off"; return; }

        _target = screenRect;
        _smooth = drawCostMs <= SmoothMaxDrawMs;
        LastMode = _smooth ? "smooth" : "hold";
        _active = true;
        _clock.Restart();

        _timer ??= CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(_smooth ? 16 : HoldMs);
        if (!_timer.IsEnabled) _timer.Start();
        InvalidateVisual();
    }

    private DispatcherTimer CreateTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Render);
        timer.Tick += (_, _) => Tick();
        return timer;
    }

    /// <summary>立即结束（换图时清掉过期提示）。</summary>
    public void Hide()
    {
        _timer?.Stop();
        if (!_active) return;
        _active = false;
        InvalidateVisual();
    }

    private void Tick()
    {
        if (!_active) { _timer?.Stop(); return; }
        if (!_smooth || _clock.Elapsed.TotalMilliseconds >= DurationMs) { Hide(); return; }
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (!_active) return;

        double alpha = 1;
        double grow = 1;
        if (_smooth)
        {
            double t = Math.Clamp(_clock.Elapsed.TotalMilliseconds / Math.Max(1, DurationMs), 0, 1);
            // 前段保持不透明、后段淡出：跳转瞬间"亮一下"比均匀淡出更抓眼
            alpha = t < 0.25 ? 1 : 1 - (t - 0.25) / 0.75;
            grow = 1 + 0.08 * t;
        }
        if (alpha <= 0.01) return;

        var r = new Rect(
            _target.Center.X - _target.Width * grow / 2,
            _target.Center.Y - _target.Height * grow / 2,
            Math.Max(_target.Width * grow, 1),
            Math.Max(_target.Height * grow, 1));
        // 角半径跟着尺寸走，超小目标也能看出是个框
        double radius = Math.Min(6, Math.Min(r.Width, r.Height) / 3);

        // 底下垫一圈近黑，亮底图纸上琥珀色不至于糊掉
        var shadow = new SolidColorBrush(Color.FromArgb((byte)(130 * alpha), 0, 0, 0));
        context.DrawRectangle(null, new Pen(shadow, RingThickness * 2.6), r, radius, radius);

        var ring = new SolidColorBrush(Color.FromArgb((byte)(255 * alpha), RingColor.R, RingColor.G, RingColor.B));
        context.DrawRectangle(null, new Pen(ring, RingThickness), r, radius, radius);
    }
}
