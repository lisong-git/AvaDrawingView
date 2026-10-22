namespace DrawingView.Core;

/// <summary>
/// POINT 的显示口径（P2-3）：<c>PDMODE</c> 决定形状，<c>PDSIZE</c> 决定尺寸。
///
/// <para><b>为什么必须做成"屏幕空间"</b>：AutoCAD 的 <c>PDSIZE</c> 语义是
/// 「点符号尺寸」——<c>&gt;0</c> 按**世界单位**、<c>&lt;0</c> 按**视口高度的百分比**、
/// <c>=0</c> 取默认 5% 视高。后两种都依赖当前视口，**屏幕尺寸恒定、不随缩放变化**，
/// 所以尺寸只能在渲染期算：转换期把它折成"世界单位宽度"后，一旦缩放就必然
/// 要么缩成一个亚像素点看不见、要么放大到糊满屏（原实现固定 ±1 世界单位十字，
/// 正是这两种表现的成因）。</para>
///
/// <para>这里只放纯函数（不依赖任何渲染对象），便于数值断言：
/// 形状解析与像素尺寸各一个入口。</para>
/// </summary>
public static class PointMarkStyle
{
    /// <summary>PDMODE = 1：完全不画（AutoCAD 的"空白"档，用于临时隐藏所有点）。</summary>
    public const int ModeInvisible = 1;

    /// <summary>PDSIZE = 0 时的默认尺寸：视口高度的 5%（AutoCAD 出厂值）。</summary>
    public const float DefaultViewportPercent = 5f;

    /// <summary>PDMODE 的"基础形状"位（低 5 位）。</summary>
    public static int BaseShape(int mode) => mode & 31;

    /// <summary>PDMODE 是否叠加圆圈（位 32）。</summary>
    public static bool HasCircle(int mode) => (mode & 32) != 0;

    /// <summary>PDMODE 是否叠加方框（位 64）。</summary>
    public static bool HasSquare(int mode) => (mode & 64) != 0;

    /// <summary>
    /// 点符号的**全尺寸屏幕像素**。
    /// </summary>
    /// <param name="pdsize">
    /// 组码 40 的原值：<c>&gt;0</c> 世界单位；<c>&lt;0</c> 视口高度百分比（取绝对值）；
    /// <c>=0</c> 默认 5%。
    /// </param>
    /// <param name="zoom">当前缩放（屏幕像素 / 世界单位），用于把世界尺寸换成像素。</param>
    /// <param name="viewportHeightPx">视口高度（像素），百分比口径的基准。</param>
    public static float SizePx(float pdsize, float zoom, float viewportHeightPx)
    {
        float px;
        if (pdsize > 0f)
        {
            px = pdsize * (zoom > 1e-6f ? zoom : 1e-6f);
        }
        else
        {
            float percent = pdsize < 0f ? -pdsize : DefaultViewportPercent;
            px = viewportHeightPx * percent * 0.01f;
        }
        // 至少 1px：再小就完全看不见，而"看不见"会让人误判成实体丢失
        return px < 1f ? 1f : px;
    }

    /// <summary>PDMODE 基础形状 0（"点"）的默认直径：1.5 设备像素。</summary>
    public const float DefaultDotPx = 1.5f;

    /// <summary>
    /// PDMODE 基础形状 0（点）的屏幕直径。
    ///
    /// <para><b>为什么不套 <see cref="SizePx"/> 的 5% 规则</b>：实测 `3、幕墙横方总汇.dwg`
    /// 有 4,477 个 PDMODE=0 / PDSIZE=0 的点，按 5% 视高算每个直径 45px —— 全图会糊成一片点阵，
    /// 而 ODA 参考出图（`.compare/oda_3幕.png`）里这些点放大到当前视口只剩一个像素级小点。
    /// 结论：「5% 视高」是**符号模式**（2/3/4 及 32/64 叠加）的尺寸口径，点模式就是一个点，
    /// 不随视口放大。这是 P2-3 首版实现漏掉的一档，也是渲染耗时回退的直接来源（45px 抗锯齿
    /// 实心圆逐个绘制）。</para>
    ///
    /// <para>PDSIZE &gt; 0 是用户显式给了世界单位尺寸，仍然尊重（乘缩放换算成像素），
    /// 只是不小于 1px。</para>
    /// </summary>
    public static float DotDiameterPx(float pdsize, float zoom)
    {
        if (pdsize > 0f) return SizePx(pdsize, zoom, 0f);
        return DefaultDotPx;
    }
}
