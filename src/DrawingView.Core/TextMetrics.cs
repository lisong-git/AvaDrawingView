using System.Collections.Concurrent;
using SkiaSharp;

namespace DrawingView.Core;

/// <summary>
/// 文本度量：转换阶段（算包围盒）与渲染阶段（真正画字）必须用同一字体，
/// 否则包围盒与实际墨迹不符。
///
/// 背景（2026-09-16 实测修正）：原实现用 <c>raw.Length * height * 0.7</c> 粗估宽度，
/// 样例里 "Hello DrawingView"（15 字符 / 字高 6）估出半宽 63，而真实渲染宽度约 40，
/// 直接把整图 Extents 左边界从 0 撑到 -53（宽度虚增 34%），后果是打开图纸时画面明显偏小且左偏。
/// 改用 Skia 真实度量后包围盒与墨迹一致。
/// </summary>
public static class TextMetrics
{
    /// <summary>
    /// 默认字体族。渲染端与度量端共用此常量，避免"量的字体 ≠ 画的字体"。
    /// TODO: 硬编码单一字体在非 Windows 平台会回退到 SKTypeface.Default，
    /// 后续应改为按可用性依次尝试候选字体族。
    /// </summary>
    public const string DefaultFontFamily = "Microsoft YaHei";

    private static readonly SKTypeface Typeface =
        SKTypeface.FromFamilyName(DefaultFontFamily) ?? SKTypeface.Default;

    private static readonly ConcurrentDictionary<int, SKFont> FontCache = new();

    /// <summary>渲染端与度量端共用的字体对象。</summary>
    public static SKTypeface SharedTypeface => Typeface;

    /// <summary>按字高取字体（量化到整数像素，避免为每个连续尺寸建字体对象）。</summary>
    public static SKFont FontFor(float height)
    {
        int key = Math.Clamp((int)MathF.Round(height), 1, 512);
        return FontCache.GetOrAdd(key, k => new SKFont(Typeface, k));
    }

    /// <summary>
    /// 测量多行文本相对插入点的局部包围盒（未旋转）。
    ///
    /// 插入点按 CAD 惯例视为**基线左端**，与渲染端 <c>canvas.DrawText(text, 0, 0)</c> 一致：
    /// 文本向右延伸（x ≥ 0），基线以上为字身、以下为降部。
    /// </summary>
    public static SKRect MeasureLocalBounds(string text, float height)
    {
        if (string.IsNullOrEmpty(text) || height <= 0)
            return SKRect.Create(0, -height, 0, height);

        var font = FontFor(height);
        var fm = font.Metrics;

        // 字体度量在个别字体/字重下可能给出 0 或异常值，用字高兜底，避免包围盒塌陷。
        float ascent = MathF.Max(-fm.Ascent, height * 0.8f);
        float descent = MathF.Max(fm.Descent, height * 0.2f);
        float lineSpacing = font.Spacing > 0 ? font.Spacing : ascent + descent;

        var lines = text.Split('\n');
        float maxAdvance = 0f;
        foreach (var line in lines)
        {
            if (line.Length == 0) continue;
            float adv = font.MeasureText(line);
            if (adv > maxAdvance) maxAdvance = adv;
        }
        if (maxAdvance <= 0) maxAdvance = height * 0.5f;

        // 第一行基线在 y = 0，后续行依次向下（世界 Y 向上，故基线递减）。
        float top = ascent;
        float bottom = -((lines.Length - 1) * lineSpacing + descent);
        return SKRect.Create(0f, bottom, maxAdvance, top - bottom);
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
}
