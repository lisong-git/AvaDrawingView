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

    /// <summary>
    /// 该文字实际使用的字体（P2-16：由样式解析而来）。
    /// **转换阶段的包围盒与渲染阶段的字形必须用同一份字体**，所以这里把解析结果带下来，
    /// 渲染端不允许再按全局默认字体重算 —— 换字体就换 cap/em 比例，包围盒立刻与墨迹错位。
    /// 缺省是默认字体，保证没有样式信息的调用点（编辑器临时文本、测试夹具）行为不变。
    /// </summary>
    public TextFont Font { get; init; } = TextFont.Default;
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
    /// <para>0 = 未知（旧数据或未填）。**同一块定义被插入多次时，各实例的后代 OwnHandle 相同、
    /// Handle 也相同**（Handle 只取到最外层 INSERT）—— 想区分「哪一个插入」只能看
    /// <see cref="InstanceId"/>。</para>
    /// </summary>
    public ulong OwnHandle { get; init; }

    /// <summary>
    /// 所属**最内层**块实例 Id（见 <see cref="InsertInstance"/>）；0 = 模型空间/图纸空间直属。
    ///
    /// <para>转换期用 -1 表示「尚未指派」，<c>GeometryConverter.Convert</c> 收尾会统一归一化为 0，
    /// 因此构建完成的图纸里不出现负值。</para>
    /// </summary>
    public int InstanceId { get; set; } = PendingInstance;

    /// <summary>转换期「尚未指派」标记（不对外出现，见 <see cref="InstanceId"/>）。</summary>
    public const int PendingInstance = -1;

    /// <summary>
    /// 来自图纸标注类实体（TEXT / MTEXT / DIMENSION / LEADER / ATTRIB）的显示单元，
    /// 含块内展开出来的那些。由 <c>GeometryConverter</c> 在转换期回填；
    /// 拾取层可用它把标注排除在选择之外（<c>CadViewControl.SkipAnnotationPick</c>）。
    /// </summary>
    public bool IsAnnotation { get; set; }

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
    /// SHX 单线字体排出的**文字笔画**（几何在 <see cref="Path"/> 里，世界坐标）。
    ///
    /// <para>为什么不走批次：文字一旦进批次就会被 LOD 按尺寸整档丢弃，而"文字突然消失"
    /// 与"图元缺失"在用户眼里没有区别。所以它和 TTF 文本一样进
    /// <c>UnbatchedEntities</c>，只是画法由 <see cref="TextDraw"/> 换成描边。</para>
    /// </summary>
    public bool TextStrokes { get; init; }
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

    /// <summary>
    /// 该图元要裁到的区域（世界坐标，闭合多边形；null = 不裁，P2-17）。
    ///
    /// <para><b>为什么用"渲染期画布裁剪"而不是转换期做布尔求交</b>：Skia 的
    /// <c>SKPath.Op</c> 按**填充区域**求交，开放路径（LINE/ARC/PLINE）面积为零，
    /// 求交结果直接是空路径 —— 实测 <c>line.Op(rect, Intersect)</c> 返回 0 个点。
    /// 之前布局视口的裁剪就是这么写的，等价于把视口里的线条**全部删掉**。
    /// 画布裁剪对标边、填充、文字一视同仁，结果与 AutoCAD 一致。</para>
    ///
    /// <para>代价：带裁剪的图元不能进批次（<c>BakedGeometry</c> 会把它们留在
    /// <c>UnbatchedEntities</c>），所以只有**真正跨边界**的图元才挂这个字段，
    /// 完全在区域内的照常合批。</para>
    ///
    /// <para>可写：转换器在 <c>ConvertEntity</c> 收尾时统一给本层新增的图元挂上，
    /// 这里不逐个分支改。</para>
    /// </summary>
    public SKPath? ClipPath { get; set; }

    /// <summary>
    /// 世界坐标包围盒（用于空间索引与视口裁剪）。
    ///
    /// <para>可写：被裁剪的图元在转换收尾时要把包围盒收窄到裁剪区域，
    /// 否则自适应范围会把框外内容算进去（"适应窗口"缩成一小块）。</para>
    /// </summary>
    public SKRect Bounds { get; set; }
}
