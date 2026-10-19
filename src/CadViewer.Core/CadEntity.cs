using SkiaSharp;

namespace CadViewer.Core;

/// <summary>文字绘制信息（屏幕空间绘制，位置/高度均为世界坐标）。</summary>
public sealed class TextDraw
{
    public required string Text { get; init; }
    public SKPoint Position { get; init; }
    public float Height { get; init; }
    public float RotationRadians { get; init; }
}

/// <summary>
/// 显示实体：CAD 图元转换后的显示单元。
/// 路径在世界坐标系；构建完成后不可变，渲染线程只读。
/// </summary>
public sealed class CadEntity
{
    public required string Layer { get; init; }
    public uint ColorArgb { get; init; }
    /// <summary>ACadSharp 实体句柄（块引用展开的子实体继承块引用句柄）；0 = 无身份。</summary>
    public ulong Handle { get; init; }
    /// <summary>
    /// 实体**自身**句柄（不做块引用继承）。
    ///
    /// <para>块内子实体在扁平模型空间里没有可定位句柄，<see cref="Handle"/> 会被覆盖成
    /// 最外层 INSERT 的句柄；本字段保留块定义层那个原始句柄，用于还原
    /// 「这个显示图元对应块内哪个子实体」。顶层实体二者相等。</para>
    ///
    /// <para>0 = 未知（旧数据或未填）。**同一实体被插入多次时会得到多个 CadEntity，
    /// 它们的 OwnHandle 相同而 Handle 不同** —— 需要区分「哪一个插入」时用
    /// (Handle, OwnHandle) 组合。</para>
    /// </summary>
    public ulong OwnHandle { get; init; }
    /// <summary>描边几何（世界坐标），与 FillPath 互斥使用。</summary>
    public SKPath? Path { get; init; }
    /// <summary>填充几何（世界坐标），如 SOLID。</summary>
    public SKPath? FillPath { get; init; }
    public TextDraw? Text { get; init; }
    /// <summary>世界单位线宽，0 = 细线（随缩放保持 1px）。</summary>
    public float LineWidth { get; init; }
    /// <summary>
    /// 线型虚线间隔（世界单位，按 on/off 交替；null = 实线）。
    /// 值已含 LTSCALE、实体线型比例与块变换缩放，渲染时直接作为 Skia 路径效果使用。
    /// </summary>
    public float[]? DashPattern { get; init; }
    /// <summary>世界坐标包围盒（用于空间索引与视口裁剪）。</summary>
    public SKRect Bounds { get; init; }
}
