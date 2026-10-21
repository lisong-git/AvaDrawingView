using SkiaSharp;

namespace DrawingView.Core;

/// <summary>文字绘制信息（屏幕空间绘制，位置/高度均为世界坐标）。</summary>
public sealed class TextDraw
{
    public required string Text { get; init; }

    /// <summary>
    /// **对齐计算之后**的基线左端（世界坐标）。
    /// 转换阶段已把 72/73 对齐、块变换、文字样式一并折进来，渲染端只管从这点画。
    /// </summary>
    public SKPoint Position { get; init; }

    /// <summary>
    /// CAD 字高（= 大写字高 cap height，世界单位），**不是**字体 em 尺寸。
    /// 渲染端必须经 <see cref="TextMetrics.FontForCapHeight"/> 换算成 em 再取字体。
    /// </summary>
    public float Height { get; init; }

    public float RotationRadians { get; init; }

    /// <summary>宽度因子（组码 41，实体有效值；样式只在缺省时兜底），1 = 原始宽度。</summary>
    public float WidthFactor { get; init; } = 1f;

    /// <summary>倾斜角（弧度，组码 51；样式只在缺省时兜底）。正值 = 顶部向右倒（斜体）。</summary>
    public float ObliqueRadians { get; init; }

    /// <summary>
    /// 行距倍数（相对于字高）。AutoCAD 单倍行距 = 1.667 × 字高；
    /// MTEXT 的 <c>LineSpacing</c>（组码 44）是它的倍数，默认 1.0。
    /// </summary>
    public float LineSpacingFactor { get; init; } = TextMetrics.LineSpacingFactor;

    /// <summary>
    /// 多行文本的**行**对齐方式（MTEXT 的 9 宫格水平分量）。
    /// 与 <see cref="Position"/> 的关系：Position 是第 1 行的基线起点，其余行按本对齐方式在水平方向对齐。
    /// </summary>
    public TextLineAlign LineAlign { get; init; } = TextLineAlign.Left;
}

/// <summary>多行文本的行对齐（MTEXT 用）。单行 TEXT 恒为 Left —— 它的对齐已经在 Position 里折好了。</summary>
public enum TextLineAlign
{
    Left = 0,
    Center = 1,
    Right = 2,
}

/// <summary>
/// 点标记（POINT 实体，P2-3）。尺寸在**渲染期**按视口换算成像素，所以这里只存原值。
///
/// <para>不把十字烘焙成世界坐标几何的原因见 <see cref="PointMarkStyle"/>：
/// PDSIZE 的两种主要口径（视高百分比）本来就是屏幕尺寸恒定的，折成世界单位必然错。</para>
/// </summary>
public sealed class PointMark
{
    /// <summary>点位置（世界坐标）。</summary>
    public SKPoint Position { get; init; }

    /// <summary>PDMODE（文件头变量）：0 点 / 1 不画 / 2 十字 / 3 叉 / 4 竖线，+32 加圆、+64 加框。</summary>
    public int Mode { get; init; }

    /// <summary>PDSIZE（文件头变量）：&gt;0 世界单位、&lt;0 视高百分比、=0 默认 5%。</summary>
    public float SizeSpec { get; init; }
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
    /// <summary>
    /// 点标记（POINT）。渲染期按 PDMODE/PDSIZE 在**屏幕空间**画，
    /// 因此这类实体没有 <see cref="Path"/>（不参与批次合并）。
    /// </summary>
    public PointMark? Mark { get; init; }
    /// <summary>
    /// 解析后的线宽（1/100 mm，AutoCAD 口径：25 = 0.25mm；0 = 不显示线宽，按 1px 细线）。
    ///
    /// <para><b>这里存毫米原值而不是"世界单位宽度"</b>：AutoCAD 的线宽显示口径在两种空间里不同 ——
    /// 模型空间是**固定像素宽**（不随缩放变化），图纸空间是按**真实单位**显示（随缩放变化）。
    /// 同一个毫米值在两种空间要换算成不同的屏幕像素，换算只能在渲染期（知道缩放与空间）做，
    /// 转换期算好的"世界单位宽度"在两种空间里必有一种是错的。</para>
    /// </summary>
    public int LineWeightMm100 { get; init; }
    /// <summary>
    /// 线型虚线间隔（世界单位，按 on/off 交替；null = 实线）。
    /// 值已含 LTSCALE、实体线型比例与块变换缩放，渲染时直接作为 Skia 路径效果使用。
    /// </summary>
    public float[]? DashPattern { get; init; }
    /// <summary>世界坐标包围盒（用于空间索引与视口裁剪）。</summary>
    public SKRect Bounds { get; init; }
}
