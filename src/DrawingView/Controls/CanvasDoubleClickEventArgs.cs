using Avalonia;

namespace DrawingView.Controls;

/// <summary>
/// 一次左键双击（控件内像素坐标）。
///
/// <para><b>为什么由画布抛、而不是宿主自己判</b>：连击计数在输入层
/// （<see cref="Avalonia.Input.PointerPressedEventArgs.ClickCount"/>），宿主拿到的
/// PointerMoved / PointerReleased 里没有；自己按时间+距离凑一套，会和系统双击间隔
/// 不一致（触摸板、远程桌面下尤其明显）。</para>
///
/// <para><b>为什么要 <see cref="Handled"/></b>：双击的第二次按下同时也是"单击"，
/// 默认会走框选起点 + 松手点选。宿主若把双击当成「进入」动作（进块 / 进面域），
/// 松手那一下就会顺手在新作用域里选到一个子图元 —— 置 Handled = true 即由画布
/// 吞掉这次点击，这次双击在画布上不留任何选中副作用。</para>
/// </summary>
public sealed class CanvasDoubleClickEventArgs : EventArgs
{
    public CanvasDoubleClickEventArgs(Point position) => Position = position;

    /// <summary>双击位置（控件坐标，像素）。</summary>
    public Point Position { get; }

    /// <summary>宿主是否已把这次双击当成自己的动作（true = 画布不再处理这次点击）。</summary>
    public bool Handled { get; set; }
}
