using System.Collections.Concurrent;
using SkiaSharp;

namespace DrawingView.Core;

/// <summary>文字最终用的字体是从哪来的（诊断 / <c>DwgCapture --report</c> 用）。</summary>
public enum TextFontKind
{
    /// <summary>样式里直接写了 TTF/OTF（<c>arial.ttf</c>、<c>SimSun.ttf</c>）—— 直接用。</summary>
    TrueType = 0,

    /// <summary>样式是 SHX，用系统里 AutoCAD 装的同名 TrueType 替身（<c>romans__.ttf</c> / <c>simplex_.ttf</c>）。</summary>
    ShxStandIn = 1,

    /// <summary>样式是 SHX 且没有替身，退到通用中文字体（宋体一族）—— 中文能显示，字宽只是近似。</summary>
    Substitute = 2,

    /// <summary>什么也没找到，用内置默认（微软雅黑）= 2026-11-13 之前的全局行为。</summary>
    Fallback = 3,
}

/// <summary>
/// 一份**已解析的字体**：字形来源 + 实测度量 + 度量/排版 API。
///
/// <para><b>为什么不再用全局单一字体</b>（P2-16）：2026-11-13 之前所有文字一律微软雅黑。
/// 受控基准 <c>textscale.dxf</c> 实测：字高对得上（ODA 107px / 我们 113px），
/// 但**宽度 ODA 226 / 我们 291（+29%）**；图纸样式实际写的是 <c>SimSun.ttf</c>、
/// <c>romans.shx + chinese.shx</c>、<c>arial.ttf</c>，雅黑比它们宽得多，
/// 竖排图签被旋转 90° 之后放大成"糊成一片"。</para>
///
/// <para><b>度量与渲染必须共用同一个对象</b>：CAD 的字高是 cap height，
/// cap/em 比例是**每个字体自己的属性**（雅黑 0.756、宋体 0.86 量级）。
/// 一旦换算比例与画字用的字体不一致，包围盒就会和墨迹错位
/// （历史上出现过"整图 Extents 虚增 34%"和"右侧内容被裁"）。</para>
///
/// <para>线程安全：字形度量缓存都是 <see cref="ConcurrentDictionary{TKey,TValue}"/> 惰性填充，
/// 字体对象一次发布后只读，可在加载/渲染多线程共用。</para>
/// </summary>
public sealed class TextFont
{
    /// <summary>默认字体族：样式没写字体、或字体找不到时的兜底（保持 2026-11-13 之前的行为）。</summary>
    public const string DefaultFamily = "Microsoft YaHei";

    /// <summary>
    /// 度量用的参考 em 尺寸。**测宽一律在这个字号上量、再按比例换算**，
    /// 不按目标字号建字体：世界坐标里的字高可达数千（实测 3、幕墙横方总汇.dwg 有 2160），
    /// 按目标字号建字体既吃缓存又容易撞上量化上限。
    /// </summary>
    private const float RefEmSize = 100f;

    private readonly ConcurrentDictionary<int, SKFont> _fonts = new();
    private readonly ConcurrentDictionary<char, float> _advances = new();
    private readonly SKFont _refFont;
    private bool? _coversCjk;

    internal TextFont(string name, string? source, SKTypeface typeface, TextFontKind kind)
    {
        Name = name;
        Source = source;
        Typeface = typeface;
        Kind = kind;
        _refFont = new SKFont(typeface, RefEmSize);
        CapRatio = ComputeCapRatio(typeface);
        DescentRatio = ComputeDescentRatio(typeface);
    }

    /// <summary>内置默认字体（微软雅黑）。<see cref="TextMetrics"/> 的门面 API 都指向它。</summary>
    public static TextFont Default => DefaultHolder.Value;

    private static class DefaultHolder
    {
        internal static readonly TextFont Value = new(
            DefaultFamily, null,
            SKTypeface.FromFamilyName(DefaultFamily) ?? SKTypeface.Default,
            TextFontKind.Fallback);
    }

    /// <summary>诊断名：字体族名（找不到时是样式里写的名字）。</summary>
    public string Name { get; }

    /// <summary>来源描述：命中的文件名或样式里写的字体名；默认字体为 null。</summary>
    public string? Source { get; }

    public TextFontKind Kind { get; }

    public SKTypeface Typeface { get; }

    /// <summary>cap height / em，**实测自本字体**（不同字体差别很大，不能硬编码）。</summary>
    public float CapRatio { get; }

    /// <summary>descent / em（含降部字符的包围盒下探量）。</summary>
    public float DescentRatio { get; }

    /// <summary>本字体是否有常用汉字的字形（决定混排时该不该换中文字体）。</summary>
    public bool CoversCjk => _coversCjk ??= _refFont.ContainsGlyphs("中文宋体备注");

    /// <summary>CAD 字高 → 字体 em 尺寸。</summary>
    public float EmSizeForCapHeight(float capHeight) => capHeight > 0 ? capHeight / CapRatio : 0f;

    /// <summary>字体 em 尺寸 → 该字体的实际大写字高。</summary>
    public float CapHeightForEmSize(float emSize) => emSize * CapRatio;

    /// <summary>
    /// 渲染用字体 + 需要的画布缩放倍数。字高超过 <see cref="TextMetrics.MaxCachedCapHeight"/> 时
    /// 返回上限尺寸的字体并给出 &gt;1 的缩放倍数（调用方需 <c>canvas.Scale</c>），
    /// 否则超大字号会撑爆字体缓存。
    /// </summary>
    public SKFont FontForCapHeight(float capHeight, out float canvasScale)
    {
        int key = (int)MathF.Round(capHeight);
        if (key < 1) key = 1;
        canvasScale = 1f;
        if (key > TextMetrics.MaxCachedCapHeight)
        {
            canvasScale = (float)key / TextMetrics.MaxCachedCapHeight;
            key = TextMetrics.MaxCachedCapHeight;
        }
        return _fonts.GetOrAdd(key, k => new SKFont(Typeface, EmSizeForCapHeight(k)));
    }

    /// <summary>渲染用字体（不需要画布缩放时）。</summary>
    public SKFont FontForCapHeight(float capHeight) => FontForCapHeight(capHeight, out _);

    /// <summary>
    /// 单字符 advance 宽度（世界单位，未计宽度因子）。供断行使用：逐字累加比"每加一个字就整串量一次"
    /// 快一个量级（一张图 10 万条 MTEXT，断行里外里差几百万次测量）。
    /// </summary>
    public float CharAdvance(char c, float capHeight)
    {
        if (capHeight <= 0) return 0f;
        float refAdv = _advances.GetOrAdd(c, ch => _refFont.MeasureText(ch.ToString()));
        return refAdv * (EmSizeForCapHeight(capHeight) / RefEmSize);
    }

    /// <summary>
    /// 文本宽度（世界单位，未计宽度因子）。取各行的最大 advance 宽度，
    /// 与渲染时 <c>DrawText</c> 的排版宽度一致。
    /// </summary>
    public float MeasureWidth(string text, float capHeight)
    {
        if (string.IsNullOrEmpty(text) || capHeight <= 0) return 0f;
        float max = 0f;
        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0) continue;
            float adv = _refFont.MeasureText(line);
            if (adv > max) max = adv;
        }
        return max * (EmSizeForCapHeight(capHeight) / RefEmSize);
    }

    /// <summary>文本的基线下降量（有降部字符时 &gt; 0，否则 0）。</summary>
    public float DescenderDepth(string text, float capHeight)
    {
        if (string.IsNullOrEmpty(text) || capHeight <= 0) return 0f;
        if (text.IndexOfAny(TextMetrics.DescenderChars.ToCharArray()) < 0) return 0f;
        return EmSizeForCapHeight(capHeight) * DescentRatio;
    }

    /// <summary>
    /// 测量多行文本相对**基线左端**的局部包围盒（未旋转、未计宽度因子/倾斜）。
    /// 插入点按 CAD 惯例视为基线左端，与渲染端 <c>canvas.DrawText(text, 0, 0)</c> 一致。
    /// </summary>
    public SKRect MeasureLocalBounds(string text, float capHeight, float widthFactor = 1f,
                                     float obliqueRadians = 0f)
    {
        if (string.IsNullOrEmpty(text) || capHeight <= 0)
            return SKRect.Create(0, -capHeight, 0, capHeight);

        float descent = DescenderDepth(text, capHeight);
        float lineSpacing = TextMetrics.LineSpacingFor(capHeight);
        var lines = text.Split('\n');
        float width = 0f;
        foreach (var line in lines)
        {
            if (line.Length == 0) continue;
            float adv = _refFont.MeasureText(line);
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
            float shift = skew * capHeight;
            if (shift >= 0) right += shift;
            else left += shift;
        }

        float top = capHeight;
        float bottom = -((lines.Length - 1) * lineSpacing + descent);
        return SKRect.Create(left, bottom, right - left, top - bottom);
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

    /// <summary>实测 descent / em。</summary>
    private static float ComputeDescentRatio(SKTypeface typeface)
    {
        const float probeSize = 100f;
        using var probe = new SKFont(typeface, probeSize);
        float descent = probe.Metrics.Descent;
        if (!float.IsFinite(descent) || descent <= 0f) descent = probeSize * 0.22f;
        return descent / probeSize;
    }

    public override string ToString() => Source is null ? Name : $"{Name} ({Source})";
}
