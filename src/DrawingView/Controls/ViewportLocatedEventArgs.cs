using Avalonia;
using SkiaSharp;

namespace DrawingView.Controls;

/// <summary>
/// 一次「显式定位」完成后的数据：目标的世界包围盒、它在控件内的屏幕矩形与当时的缩放。
///
/// 用途是把"已跳转"的反馈交给画布**外面**的一层（<see cref="LocateMarker"/>）去做：
/// 反馈层能自己决定画几帧（0 / 2 / 十几帧），而画在画布里的东西每次都要跟着整幅重画。
/// 实测任何压在画布上的重绘都会让 <c>CadDrawOperation</c> 重跑一遍，所以"省"的关键是帧数。
/// </summary>
public sealed class ViewportLocatedEventArgs : EventArgs
{
    public ViewportLocatedEventArgs(SKRect world, Rect screen, float zoom)
    {
        World = world;
        Screen = screen;
        Zoom = zoom;
    }

    /// <summary>定位目标的世界包围盒（未含 padding）。</summary>
    public SKRect World { get; }

    /// <summary>目标在控件坐标系里的矩形（由世界包围盒换算，未含 padding；可能超出控件边界）。</summary>
    public Rect Screen { get; }

    /// <summary>定位后的缩放。</summary>
    public float Zoom { get; }
}
