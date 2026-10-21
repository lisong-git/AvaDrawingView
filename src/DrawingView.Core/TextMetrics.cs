using System.Collections.Concurrent;
using SkiaSharp;

namespace DrawingView.Core;

/// <summary>
/// 文本度量：转换阶段（算包围盒）与渲染阶段（真正画字）必须用同一字体、同一套
/// "CAD 字高 ↔ 字体尺寸"换算，否则包围盒与实际墨迹不符。
///
/// 背景一（2026-09-16 实测）：原实现用 <c>raw.Length * height * 0.7</c> 粗估宽度，
/// "Hello DrawingView"（15 字符 / 字高 6）估出半宽 63，真实宽度约 40，
/// 直接把整图 Extents 左边界从 0 撑到 -53（宽度虚增 34%）。改用 Skia 真实度量后一致。
///
/// 背景二（2026-10-20 定标）：CAD 的「字高」是**大写字母高（cap height）**，
/// 不是字体 em 尺寸。原实现把字高直接当 <see cref="SKFont"/> 的 em 大小，
/// 受控基准图实测墨迹只有声明值的 0.744 倍（字高 80 画出来 59.5，Teigha 是 78.4）。
/// 现在统一走 <see cref="EmSizeForCapHeight"/>：em = 字高 / capRatio，
/// 度量端与渲染端共用同一换算（不一致就会重演"包围盒撑大"那类事故）。
/// </summary>
public static class TextMetrics
{
    /// <summary>
    /// 默认字体族。渲染端与度量端共用此常量，避免"量的字体 ≠ 画的字体"。
    /// TODO: 硬编码单一字体在非 Windows 平台会回退到 SKTypeface.Default，
    /// 后续应改为按可用性依次尝试候选字体族（SHX → TTF 的映射见 P2）。
    /// </summary>
    public const string DefaultFontFamily = "Microsoft YaHei";

    /// <summary>
    /// MTEXT / 多行 TEXT 的单倍行距系数（AutoCAD 语义：单倍行距 = 1.667 × 字高，
    /// 是"基线到基线"的间距，不是字体行高）。度量与渲染共用，否则多行文本会错行。
    /// </summary>
    public const float LineSpacingFactor = 1.667f;

    /// <summary>含降部（基线以下）的字符集合：这些字符把包围盒向下延伸一个 descent。</summary>
    private const string DescenderChars = "gjpqy,;()[]{}|@_%Q";

    private static readonly SKTypeface Typeface =
        SKTypeface.FromFamilyName(DefaultFontFamily) ?? SKTypeface.Default;

    /// <summary>
    /// cap height / em 比例，**实测自当前字体**（雅黑 ≈ 0.756）。换字体就变，
    /// 所以不能硬编码 0.744 —— 那个数字是"雅黑 + 特定字号"下的巧合。
    /// </summary>
    private static readonly float CapRatio = ComputeCapRatio(Typeface);

    /// <summary>descent / em 比例（含降部字符的包围盒下探量）。</summary>
    private static readonly float DescentRatio = ComputeDescentRatio(Typeface);

    private static readonly SKFont RefFont = new(Typeface, RefEmSize);

    private static readonly ConcurrentDictionary<int, SKFont> FontCache = new();

    /// <summary>
    /// 度量用的参考 em 尺寸。**测宽一律在这个字号上量、再按比例换算**，
    /// 不按目标字号建字体：世界坐标里的字高可达数千（实测 3、幕墙横方总汇.dwg 有 2160），
    /// 按目标字号建字体既吃缓存又容易撞上量化上限，一旦"量的字号 ≠ 声称的字号"，
    /// 包围盒就会比墨迹窄，整图 Extents 被算小、右侧内容被裁掉。
    /// </summary>
    private const float RefEmSize = 100f;

    /// <summary>渲染端缓存字体的字高上限（屏幕像素）。超过则用该尺寸 + 画布缩放。</summary>
    public const int MaxCachedCapHeight = 4096;

    /// <summary>渲染端与度量端共用的字体对象。</summary>
    public static SKTypeface SharedTypeface => Typeface;

    /// <summary>cap height / em（诊断用；正常绘制不需要关心）。</summary>
    public static float CapHeightRatio => CapRatio;

    /// <summary>CAD 字高 → 字体 em 尺寸。字高 80、比例 0.756 时 em ≈ 105.8。</summary>
    public static float EmSizeForCapHeight(float capHeight)
        => capHeight > 0 ? capHeight / CapRatio : 0f;

    /// <summary>字体 em 尺寸 → 该字体的实际大写字高。</summary>
    public static float CapHeightForEmSize(float emSize) => emSize * CapRatio;

    /// <summary>单倍行距（世界单位）：基线到基线。</summary>
    public static float LineSpacingFor(float capHeight) => capHeight * LineSpacingFactor;

    /// <summary>
    /// 按 **CAD 字高**取渲染用字体（内部换算成 em 尺寸并量化到整数，避免为每个连续尺寸建字体对象）。
    /// 度量端不要用它 —— 度量走 <see cref="RefFont"/> + 比例换算，见 <see cref="MeasureWidth"/>。
    /// </summary>
    public static SKFont FontForCapHeight(float capHeight) => FontForCapHeight(capHeight, out _);

    /// <summary>
    /// 渲染用字体 + 需要的画布缩放倍数。字高超过 <see cref="MaxCachedCapHeight"/> 时
    /// 返回上限尺寸的字体并给出 &gt;1 的缩放倍数（调用方需 <c>canvas.Scale</c>），
    /// 否则超大字号会撑爆字体缓存（早期版本直接 <c>Clamp(1,1024)</c> 把字号**截断**，
    /// 画出来的字比要求的字高小一半以上）。
    /// </summary>
    public static SKFont FontForCapHeight(float capHeight, out float canvasScale)
    {
        int key = (int)MathF.Round(capHeight);
        if (key < 1) key = 1;
        canvasScale = 1f;
        if (key > MaxCachedCapHeight)
        {
            canvasScale = (float)key / MaxCachedCapHeight;
            key = MaxCachedCapHeight;
        }
        return FontCache.GetOrAdd(key, k => new SKFont(Typeface, EmSizeForCapHeight(k)));
    }

    /// <summary>
    /// 单字符 advance 宽度（世界单位，未计宽度因子）。供断行使用：逐字累加比"每加一个字就整串量一次"
    /// 快一个量级（一张图 10 万条 MTEXT，断行里外里差几百万次测量）。
    /// 不计 kerning，与整串测量的差异在字宽的 1% 量级，对断行判断足够。
    /// </summary>
    public static float CharAdvance(char c, float capHeight)
    {
        if (capHeight <= 0) return 0f;
        float refAdv = CharAdvanceCache.GetOrAdd(c, ch => RefFont.MeasureText(ch.ToString()));
        return refAdv * (EmSizeForCapHeight(capHeight) / RefEmSize);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<char, float> CharAdvanceCache = new();

    /// <summary>
    /// 文本宽度（世界单位，未计宽度因子）。
    /// 取各行的**最大 advance 宽度**：与渲染时 <c>DrawText</c> 的排版宽度一致。
    /// 在 <see cref="RefFont"/> 上量再按 em 比例换算，任意字高都精确。
    /// </summary>
    public static float MeasureWidth(string text, float capHeight)
    {
        if (string.IsNullOrEmpty(text) || capHeight <= 0) return 0f;
        float max = 0f;
        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0) continue;
            float adv = RefFont.MeasureText(line);
            if (adv > max) max = adv;
        }
        return max * (EmSizeForCapHeight(capHeight) / RefEmSize);
    }

    /// <summary>
    /// 文本的基线下降量（有降部字符时 &gt; 0，否则 0）。对齐方式为 Bottom 时用它上抬。
    /// </summary>
    public static float DescenderDepth(string text, float capHeight)
    {
        if (string.IsNullOrEmpty(text) || capHeight <= 0) return 0f;
        if (text.IndexOfAny(DescenderChars.ToCharArray()) < 0) return 0f;
        return EmSizeForCapHeight(capHeight) * DescentRatio;
    }

    /// <summary>
    /// 测量多行文本相对**基线左端**的局部包围盒（未旋转、未计宽度因子/倾斜）。
    ///
    /// 插入点按 CAD 惯例视为基线左端，与渲染端 <c>canvas.DrawText(text, 0, 0)</c> 一致：
    /// 文本向右延伸（x ≥ 0），基线以上为 cap height（= 字高），
    /// 有降部字符时向下延伸一个 descent。与 AutoCAD/ODA 的"字高 = 大写字高"口径一致：
    /// 无降部的单行文本，包围盒高度正好等于字高。
    /// </summary>
    public static SKRect MeasureLocalBounds(string text, float capHeight, float widthFactor = 1f,
                                            float obliqueRadians = 0f)
    {
        if (string.IsNullOrEmpty(text) || capHeight <= 0)
            return SKRect.Create(0, -capHeight, 0, capHeight);

        var font = RefFont;
        float cap = MathF.Max(capHeight, capHeight * 0.5f);
        float descent = DescenderDepth(text, capHeight);

        float lineSpacing = LineSpacingFor(capHeight);
        var lines = text.Split('\n');
        float width = 0f;
        foreach (var line in lines)
        {
            if (line.Length == 0) continue;
            float adv = font.MeasureText(line);
            if (adv > width) width = adv;
        }
        width *= EmSizeForCapHeight(capHeight) / RefEmSize;
        if (width <= 0) width = capHeight * 0.5f;

        float wf = widthFactor > 0 ? widthFactor : 1f;
        width *= wf;

        // 倾斜（斜体）：顶部右移 tan(θ)·cap，底部不动 → 包围盒左右各扩一点。
        float skew = MathF.Tan(obliqueRadians);
        float left = 0f, right = width;
        if (MathF.Abs(skew) > 1e-6f)
        {
            float shift = skew * cap;
            if (shift >= 0) right += shift;
            else left += shift;
        }

        float top = cap;
        float bottom = -((lines.Length - 1) * lineSpacing + descent);
        return SKRect.Create(left, bottom, right - left, top - bottom);
    }

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

    /// <summary>实测 cap height / em。字体没给出 CapHeight 时退回大写 H 的紧致包围盒。</summary>
    private static float ComputeCapRatio(SKTypeface typeface)
    {
        const float probeSize = 100f;
        using var probe = new SKFont(typeface, probeSize);
        float cap = MathF.Abs(probe.Metrics.CapHeight);
        if (cap <= 1e-3f)
        {
            using var path = probe.GetTextPath("H", new SKPoint(0, 0));
            cap = path.TightBounds.Height;
        }
        if (cap <= 1e-3f) cap = probeSize * 0.7f;   // 极端兜底：字体无任何可用度量
        return cap / probeSize;
    }

    /// <summary>实测 descent / em（字体的降部深度，用于有降部字符的包围盒）。</summary>
    private static float ComputeDescentRatio(SKTypeface typeface)
    {
        const float probeSize = 100f;
        using var probe = new SKFont(typeface, probeSize);
        float descent = probe.Metrics.Descent;
        if (!float.IsFinite(descent) || descent <= 0f) descent = probeSize * 0.22f;
        return descent / probeSize;
    }
}
