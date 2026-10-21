using ACadSharp.Entities;
using SkiaSharp;

namespace DrawingView.Core;

/// <summary>
/// 图纸空间（布局）投影：把一个浮动视口的「模型坐标 → 图纸坐标」换算与裁剪矩形算出来。
///
/// <para>抽成纯函数是为了可单测：视口这四个数（中心、尺寸、视点、视高）在 DWG 里
/// 全是独立字段，串错一个（比如把视高当视口高）画面就整体缩放错，而"看截图"
/// 很难分辨是"图变大了"还是"视口选错了"。</para>
///
/// <para>AutoCAD 语义：<c>ViewHeight</c> 是视口里**能看到多高的模型空间**，
/// <c>Height</c> 是该视口在图纸上的高度，两者之比 = 出图比例（图纸单位 / 模型单位）。</para>
/// </summary>
public static class LayoutProjection
{
    /// <summary>视口参数是否可用于投影（图纸空间的"整体视口"、零尺寸视口都不算）。</summary>
    public static bool IsDrawable(Viewport? vp)
    {
        if (vp is null) return false;
        if (vp.Id == 1) return false;                 // Id=1 是图纸整体视口，不代表模型视图
        if (vp.RepresentsPaper) return false;
        if (vp.Width <= 1e-6 || vp.Height <= 1e-6) return false;
        if (vp.ViewHeight <= 1e-9) return false;      // 视高为 0 = 空视口
        return true;
    }

    /// <summary>出图比例分母（模型单位 / 图纸单位），如 208.99 表示 1:209。仅供显示。</summary>
    public static float Scale(Viewport vp) => (float)(vp.ViewHeight / vp.Height);

    /// <summary>
    /// 缩放系数（**图纸单位 / 模型单位**），变换时乘这个。
    /// 方向极易搞反：视高 300 的模型塞进 120 高的视口，是缩小 0.4 倍，不是放大 2.5 倍。
    /// </summary>
    public static float Zoom(Viewport vp) => (float)(vp.Height / vp.ViewHeight);

    /// <summary>
    /// 模型 → 图纸的相似变换：先平移到视点中心、按 <see cref="Zoom"/> 缩放、按扭转角反向旋转，
    /// 再平移到视口中心。（扭转角的正负号在实测图纸里全是 0，按"视图内容反向旋转"取负。）
    /// </summary>
    public static SKMatrix ModelToPaper(Viewport vp)
    {
        float s = Zoom(vp);
        var center = new SKPoint((float)vp.Center.X, (float)vp.Center.Y);
        var viewCenter = new SKPoint((float)vp.ViewCenter.X, (float)vp.ViewCenter.Y);
        float twist = (float)vp.TwistAngle;

        var local = Mul(Mul(Mul(Trans(-viewCenter.X, -viewCenter.Y), ScaleM(s, s)), Rot(-twist)),
                        Trans(center.X, center.Y));

        // 视口自己可能挂在块里（图框成块），再叠一层父变换
        return local;
    }

    /// <summary>视口在图纸坐标里的矩形（裁剪用）。</summary>
    public static SKRect PaperRect(Viewport vp)
    {
        float w = (float)vp.Width, h = (float)vp.Height;
        float cx = (float)vp.Center.X, cy = (float)vp.Center.Y;
        return SKRect.Create(cx - w * 0.5f, cy - h * 0.5f, w, h);
    }

    /// <summary>矩形四角经矩阵变换后的包围盒（块内旋转时略微外扩，可接受）。</summary>
    public static SKRect TransformRect(SKRect rect, in SKMatrix m)
    {
        var p0 = Transform(m, new SKPoint(rect.Left, rect.Top));
        var p1 = Transform(m, new SKPoint(rect.Right, rect.Top));
        var p2 = Transform(m, new SKPoint(rect.Right, rect.Bottom));
        var p3 = Transform(m, new SKPoint(rect.Left, rect.Bottom));
        float l = MathF.Min(MathF.Min(p0.X, p1.X), MathF.Min(p2.X, p3.X));
        float r = MathF.Max(MathF.Max(p0.X, p1.X), MathF.Max(p2.X, p3.X));
        float t = MathF.Min(MathF.Min(p0.Y, p1.Y), MathF.Min(p2.Y, p3.Y));
        float b = MathF.Max(MathF.Max(p0.Y, p1.Y), MathF.Max(p2.Y, p3.Y));
        return SKRect.Create(l, t, r - l, b - t);
    }

    public static SKPoint Transform(in SKMatrix m, SKPoint p)
        => new(p.X * m.ScaleX + p.Y * m.SkewX + m.TransX,
               p.X * m.SkewY + p.Y * m.ScaleY + m.TransY);

    // ---- 与 GeometryConverter 同一套矩阵约定（行向量，a 先 b 后）----

    private static SKMatrix Mul(in SKMatrix a, in SKMatrix b) => new(
        a.ScaleX * b.ScaleX + a.SkewY * b.SkewX,
        a.SkewX * b.ScaleX + a.ScaleY * b.SkewX,
        a.TransX * b.ScaleX + a.TransY * b.SkewX + b.TransX,
        a.ScaleX * b.SkewY + a.SkewY * b.ScaleY,
        a.SkewX * b.SkewY + a.ScaleY * b.ScaleY,
        a.TransX * b.SkewY + a.TransY * b.ScaleY + b.TransY,
        0, 0, 1);

    private static SKMatrix ScaleM(float sx, float sy) => new(sx, 0, 0, 0, sy, 0, 0, 0, 1);

    private static SKMatrix Rot(float rad)
    {
        float c = MathF.Cos(rad), s = MathF.Sin(rad);
        return new SKMatrix(c, -s, 0, s, c, 0, 0, 0, 1);
    }

    private static SKMatrix Trans(float tx, float ty) => new(1, 0, tx, 0, 1, ty, 0, 0, 1);
}
