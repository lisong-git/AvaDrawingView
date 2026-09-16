using SkiaSharp;

namespace CadViewer.Core;

/// <summary>
/// 视口状态：世界 → 屏幕的缩放/平移。
/// 约定：世界坐标 Y 轴向上（CAD 习惯），屏幕 Y 轴向下。
/// 矩阵采用 Skia 行向量约定：p' = p · M。
/// 仅 UI 线程修改；渲染线程通过 DrawOperation 捕获值副本。
/// </summary>
public sealed class ViewportState
{
    public float Zoom { get; set; } = 1f;
    public SKPoint Offset { get; set; }

    public SKPoint ScreenToWorld(SKPoint p) => new((p.X - Offset.X) / Zoom, (Offset.Y - p.Y) / Zoom);

    public SKPoint WorldToScreen(SKPoint p) => new(p.X * Zoom + Offset.X, -p.Y * Zoom + Offset.Y);

    /// <summary>以光标为锚点缩放，保持光标下的世界点不动。</summary>
    public void ZoomAt(float factor, SKPoint screenPoint)
    {
        var world = ScreenToWorld(screenPoint);
        Zoom = Math.Clamp(Zoom * factor, 1e-6f, 1e6f);
        Offset = new SKPoint(screenPoint.X - world.X * Zoom, screenPoint.Y + world.Y * Zoom);
    }

    /// <summary>适应指定世界范围到视口。</summary>
    public void Fit(SKRect bounds, float width, float height)
    {
        if (width < 1 || height < 1) return;
        if (bounds.Width <= 0 || bounds.Height <= 0 ||
            float.IsInfinity(bounds.Width) || float.IsInfinity(bounds.Height) ||
            float.IsNaN(bounds.Width) || float.IsNaN(bounds.Height))
        {
            Zoom = 1f;
            Offset = new SKPoint(width / 2f, height / 2f);
            return;
        }
        Zoom = MathF.Min(width / bounds.Width, height / bounds.Height) * 0.9f;
        if (Zoom <= 0 || float.IsInfinity(Zoom)) Zoom = 1f;
        var cx = bounds.Left + bounds.Width / 2f;
        var cy = bounds.Top + bounds.Height / 2f;
        Offset = new SKPoint(width / 2f - cx * Zoom, height / 2f + cy * Zoom);
    }

    /// <summary>世界 → 屏幕矩阵（行向量约定）。</summary>
    public SKMatrix ToMatrix() => new(Zoom, 0, Offset.X, 0, -Zoom, Offset.Y, 0, 0, 1);

    /// <summary>当前视口对应的世界矩形。</summary>
    public SKRect WorldRect(float width, float height)
    {
        var p0 = ScreenToWorld(new SKPoint(0, 0));
        var p1 = ScreenToWorld(new SKPoint(width, height));
        return SKRect.Create(
            MathF.Min(p0.X, p1.X), MathF.Min(p0.Y, p1.Y),
            MathF.Abs(p1.X - p0.X), MathF.Abs(p1.Y - p0.Y));
    }
}
