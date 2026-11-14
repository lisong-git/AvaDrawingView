using SkiaSharp;

namespace DrawingView.Core;

/// <summary>
/// 文本度量的门面：常量 + **默认字体**（微软雅黑）上的便捷 API。
///
/// <para><b>2026-11-13 起字体是按样式解析的</b>（P2-16）：每条文字由
/// <see cref="TextFontLibrary"/> 解析出 <see cref="TextFont"/>，量和画都用同一个对象。
/// 本类保留下来是为了不动"没有样式信息"的老调用点（编辑器里的临时文本、单元测试、
/// 诊断工具）—— 它们拿到的仍是雅黑，与 2026-11-13 之前完全一致。</para>
///
/// <para><b>两条铁律</b>（都踩过坑，别再改回去）：</para>
/// <list type="number">
/// <item>换算口径：CAD 的"字高"是**大写字高（cap height）**，不是字体 em 尺寸。
/// 把字高直接当 em 用，墨迹只有声明值的 0.744 倍（受控基准实测：字高 80 画出来 59.5，Teigha 78.4）。</item>
/// <item>度量端与渲染端必须用**同一个字体、同一个 cap/em 换算**。不一致就会重演
/// "包围盒虚增 34%（Extents 从 0 撑到 -53）"或"右侧内容被裁"。</item>
/// </list>
/// </summary>
public static class TextMetrics
{
    /// <summary>默认字体族（与 <see cref="TextFont.DefaultFamily"/> 同源）。</summary>
    public const string DefaultFontFamily = TextFont.DefaultFamily;

    /// <summary>
    /// MTEXT / 多行 TEXT 的单倍行距系数（AutoCAD 语义：单倍行距 = 1.667 × 字高，
    /// 是"基线到基线"的间距，不是字体行高）。度量与渲染共用，否则多行文本会错行。
    /// </summary>
    public const float LineSpacingFactor = 1.667f;

    /// <summary>含降部（基线以下）的字符集合：这些字符把包围盒向下延伸一个 descent。</summary>
    internal const string DescenderChars = "gjpqy,;()[]{}|@_%Q";

    /// <summary>渲染端缓存字体的字高上限（屏幕像素）。超过则用该尺寸 + 画布缩放。</summary>
    public const int MaxCachedCapHeight = 4096;

    /// <summary>默认字体对象（样式信息缺失时的兜底）。</summary>
    public static TextFont Default => TextFont.Default;

    /// <summary>默认字体共享的字形来源。</summary>
    public static SKTypeface SharedTypeface => TextFont.Default.Typeface;

    /// <summary>默认字体的 cap height / em（诊断用）。</summary>
    public static float CapHeightRatio => TextFont.Default.CapRatio;

    /// <summary>CAD 字高 → 字体 em 尺寸（默认字体口径）。</summary>
    public static float EmSizeForCapHeight(float capHeight) => TextFont.Default.EmSizeForCapHeight(capHeight);

    /// <summary>字体 em 尺寸 → 实际大写字高（默认字体口径）。</summary>
    public static float CapHeightForEmSize(float emSize) => TextFont.Default.CapHeightForEmSize(emSize);

    /// <summary>单倍行距（世界单位）：基线到基线。</summary>
    public static float LineSpacingFor(float capHeight) => capHeight * LineSpacingFactor;

    /// <summary>按 CAD 字高取渲染用字体（内部换算成 em 并量化）。</summary>
    public static SKFont FontForCapHeight(float capHeight) => TextFont.Default.FontForCapHeight(capHeight, out _);

    /// <summary>按 CAD 字高取渲染用字体 + 画布缩放倍数（超大字号走"上限字体 + 放大"）。</summary>
    public static SKFont FontForCapHeight(float capHeight, out float canvasScale)
        => TextFont.Default.FontForCapHeight(capHeight, out canvasScale);

    /// <summary>单字符 advance 宽度（世界单位，未计宽度因子）。</summary>
    public static float CharAdvance(char c, float capHeight) => TextFont.Default.CharAdvance(c, capHeight);

    /// <summary>文本宽度（世界单位，未计宽度因子）。</summary>
    public static float MeasureWidth(string text, float capHeight) => TextFont.Default.MeasureWidth(text, capHeight);

    /// <summary>文本的基线下降量。</summary>
    public static float DescenderDepth(string text, float capHeight)
        => TextFont.Default.DescenderDepth(text, capHeight);

    /// <summary>多行文本相对基线左端的局部包围盒（未旋转）。</summary>
    public static SKRect MeasureLocalBounds(string text, float capHeight, float widthFactor = 1f,
                                            float obliqueRadians = 0f)
        => TextFont.Default.MeasureLocalBounds(text, capHeight, widthFactor, obliqueRadians);

    /// <summary>把局部包围盒按旋转角变换到世界坐标（旋转是在世界系逆时针，与屏幕 Y 翻转一致）。</summary>
    public static SKRect ToWorldBounds(SKRect local, SKPoint origin, float rotationRadians)
    {
        if (rotationRadians == 0f)
            return SKRect.Create(origin.X + local.Left, origin.Y + local.Top, local.Width, local.Height);

        float cos = MathF.Cos(rotationRadians), sin = MathF.Sin(rotationRadians);
        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;

        Span<SKPoint> corners = stackalloc SKPoint[4]
        {
            new(local.Left, local.Top),
            new(local.Right, local.Top),
            new(local.Right, local.Bottom),
            new(local.Left, local.Bottom),
        };

        foreach (var c in corners)
        {
            float x = origin.X + c.X * cos - c.Y * sin;
            float y = origin.Y + c.X * sin + c.Y * cos;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }
        return SKRect.Create(minX, minY, maxX - minX, maxY - minY);
    }
}
