using System;
using SkiaSharp;

namespace DrawingView.Core
{
    /// <summary>
    /// 框选（窗选 / 窗交）的命中判定。
    ///
    /// <para><b>AutoCAD 口径：看图元真实几何，不看包围盒</b>。包围盒只用于预筛 ——
    /// 用包围盒当判定会把「框落在包围盒里但离几何很远」的对象一起选上：
    /// 圆角矩形的方角、圆弧的包围盒空角、有洞填充的洞内空白，本图实测窗交
    /// 有一半的命中是这种假命中。反过来窗选（完全包含）用包围盒是精确的
    /// （几何 ⊆ 包围盒，包围盒在内 ⇒ 几何在内），所以那一侧不需要几何判定。</para>
    /// </summary>
    public static class RectPick
    {
        /// <summary>包围盒是否有重叠（含边界接触；零宽 / 零高的线也算）。</summary>
        public static bool BoundsOverlap(SKRect a, SKRect b)
            => a.Left <= b.Right && b.Left <= a.Right && a.Top <= b.Bottom && b.Top <= a.Bottom;

        /// <summary>包围盒是否完全落在框内（含边界）。</summary>
        public static bool BoundsContained(SKRect inner, SKRect outer)
            => outer.Left <= inner.Left && outer.Right >= inner.Right
               && outer.Top <= inner.Top && outer.Bottom >= inner.Bottom;

        /// <summary>
        /// 实体是否被框选命中。<paramref name="window"/> = true 左→右（完全包含），
        /// false 右→左（相交即选）。
        /// </summary>
        public static bool EntityHit(CadEntity e, SKRect rect, bool window)
        {
            if (e is null) return false;
            var bounds = e.Bounds;
            if (window) {
                if (!BoundsContained(bounds, rect)) return false;
                // 包围盒 ⊇ 几何 ⇒ 包含包围盒即包含几何。例外是带裁剪区的图元：
                // 转换时包围盒被 `SKRect.Intersect(e.Bounds, _scopeClipBounds)` 收窄过，
                // 几何本身没裁 → 必须按几何重判，否则窗选会漏掉伸出裁剪框的部分。
                if (e.ClipPath is null) return true;
                if (e.FillPath is { } wf && !PathInside(wf, rect)) return false;
                if (e.Path is { } ws && !PathInside(ws, rect)) return false;
                return true;
            }
            if (!BoundsOverlap(bounds, rect)) return false;
            if (BoundsContained(bounds, rect)) return true;      // 整个在框内，不必再算几何

            // 文字（TTF / SHX 笔画）与点标记：AutoCAD 按文字范围框 / 点位置判，
            // 不按字形轮廓 —— 框压在字间空白上也算选中文字
            if (e.Text != null || e.Mark != null) return true;

            // 填充体（SOLID / 带宽多段线 / HATCH）：几何是「面」，
            // 框压在填充区内就算命中（AutoCAD 里这类图元点内部就能选中）
            if (e.FillPath is { } fill
                && (PathCrosses(fill, rect) || fill.Contains(rect.MidX, rect.MidY)))
                return true;

            if (e.Path is { } stroke) return PathCrosses(stroke, rect);

            // 既没有几何也没压中：保留命中（只有包围盒可判的图元）
            return e.FillPath is null;
        }

        /// <summary>
        /// 折线 / 曲线几何是否与矩形相碰（有交，或任一端点在矩形内）。
        ///
        /// <para>只判<b>描边几何</b>：闭合轮廓（圆、闭合多段线）框落在它围出的空白里
        /// 不算命中 —— 与 AutoCAD 一致。曲线按弦逼近（弦在曲线的控制多边形内，
        /// 半径按矩形尺寸分档，误差远小于拾取容差）。</para>
        /// </summary>
        public static bool PathCrosses(SKPath path, SKRect rect)
        {
            if (path is null) return false;
            var pts = new SKPoint[4];
            SKPoint cur = default, start = default;
            using var it = path.CreateRawIterator();
            SKPathVerb verb;
            while ((verb = it.Next(pts)) != SKPathVerb.Done) {
                switch (verb) {
                    case SKPathVerb.Move:
                        cur = start = pts[0];
                        if (InRect(pts[0], rect)) return true;
                        break;
                    case SKPathVerb.Line:
                        if (SegHits(cur, pts[1], rect)) return true;
                        cur = pts[1];
                        break;
                    case SKPathVerb.Quad:
                        if (QuadHits(cur, pts[1], pts[2], rect)) return true;
                        cur = pts[2];
                        break;
                    case SKPathVerb.Conic:
                        if (ConicHits(cur, pts[1], pts[2], pts[3].X, rect)) return true;
                        cur = pts[2];
                        break;
                    case SKPathVerb.Cubic:
                        if (CurveHits(cur, pts[1], pts[2], pts[3], rect)) return true;
                        cur = pts[3];
                        break;
                    case SKPathVerb.Close:
                        if (SegHits(cur, start, rect)) return true;
                        cur = start;
                        break;
                }
            }
            return false;
        }

        /// <summary>
        /// 整条几何是否完全落在框内（只用于带裁剪区的图元）。
        /// 曲线按控制点判：矩形是凸的，控制点全在框内 ⇒ 曲线全在框内（不会误判成在内）。
        /// </summary>
        public static bool PathInside(SKPath path, SKRect rect)
        {
            if (path is null) return true;
            var pts = new SKPoint[4];
            using var it = path.CreateRawIterator();
            SKPathVerb verb;
            while ((verb = it.Next(pts)) != SKPathVerb.Done) {
                int n = verb switch {
                    SKPathVerb.Move => 1,
                    SKPathVerb.Line => 2,
                    SKPathVerb.Quad => 3,
                    SKPathVerb.Conic => 3,
                    SKPathVerb.Cubic => 4,
                    _ => 0,
                };
                for (int i = 0; i < n; i++)
                    if (!InRect(pts[i], rect)) return false;
            }
            return true;
        }

        /// <summary>点是否在矩形内（闭区间：贴边也算，与包围盒判定同一口径）。</summary>
        private static bool InRect(SKPoint p, SKRect r)
            => p.X >= r.Left && p.X <= r.Right && p.Y >= r.Top && p.Y <= r.Bottom;

        /// <summary>线段与矩形是否相碰：端点在内，或线段穿过矩形（Liang–Barsky 裁剪）。</summary>
        private static bool SegHits(SKPoint a, SKPoint b, SKRect r)
        {
            if (InRect(a, r) || InRect(b, r)) return true;
            float dx = b.X - a.X, dy = b.Y - a.Y;
            float t0 = 0f, t1 = 1f;
            return Clip(-dx, a.X - r.Left, ref t0, ref t1)
                && Clip(dx, r.Right - a.X, ref t0, ref t1)
                && Clip(-dy, a.Y - r.Top, ref t0, ref t1)
                && Clip(dy, r.Bottom - a.Y, ref t0, ref t1);
        }

        private static bool Clip(float p, float q, ref float t0, ref float t1)
        {
            if (MathF.Abs(p) < 1e-12f) return q >= 0f;
            float t = q / p;
            if (p < 0f) {
                if (t > t1) return false;
                if (t > t0) t0 = t;
            } else {
                if (t < t0) return false;
                if (t < t1) t1 = t;
            }
            return true;
        }

        /// <summary>二阶贝塞尔：先精确升阶成三阶（几何完全等价）再按弦逼近。</summary>
        private static bool QuadHits(SKPoint p0, SKPoint c, SKPoint p1, SKRect r)
        {
            var c1 = new SKPoint(p0.X + (c.X - p0.X) * 2f / 3f, p0.Y + (c.Y - p0.Y) * 2f / 3f);
            var c2 = new SKPoint(p1.X + (c.X - p1.X) * 2f / 3f, p1.Y + (c.Y - p1.Y) * 2f / 3f);
            return CurveHits(p0, c1, c2, p1, r);
        }

        /// <summary>
        /// 圆锥曲线段（Skia 用 conic 画圆与圆弧）：按有理二次曲线采样。
        /// 权重不能忽略 —— 圆是 4 段 w≈0.707 的 conic，当普通二次曲线逼近
        /// 会让半径偏大，框压在弧附近时判定不准。
        /// </summary>
        private static bool ConicHits(SKPoint p0, SKPoint c, SKPoint p1, float w, SKRect r)
        {
            float len = Dist(p0, c) + Dist(c, p1);
            float span = MathF.Max(MathF.Max(r.Width, r.Height), 1e-4f);
            int n = Math.Clamp((int)MathF.Ceiling(len / (span / 8f)), 1, 32);
            var prev = p0;
            for (int i = 1; i <= n; i++) {
                float t = (float)i / n, u = 1f - t;
                float a = u * u, b = 2f * w * t * u, d = t * t;
                float den = a + b + d;
                var p = new SKPoint(
                    (a * p0.X + b * c.X + d * p1.X) / den,
                    (a * p0.Y + b * c.Y + d * p1.Y) / den);
                if (SegHits(prev, p, r)) return true;
                prev = p;
            }
            return false;
        }

        /// <summary>二阶 / 三阶曲线按弦逼近（弦在控制多边形内，误差远小于拾取容差）。</summary>
        private static bool CurveHits(SKPoint p0, SKPoint c1, SKPoint c2, SKPoint p1, SKRect r)
        {
            float span = MathF.Max(MathF.Max(r.Width, r.Height), 1e-4f);
            float len = Dist(p0, c1) + Dist(c1, c2) + Dist(c2, p1);
            int n = Math.Clamp((int)MathF.Ceiling(len / (span / 8f)), 1, 32);
            var prev = p0;
            for (int i = 1; i <= n; i++) {
                float t = (float)i / n, u = 1f - t;
                float a = u * u * u, b = 3f * u * u * t, c = 3f * u * t * t, d = t * t * t;
                var p = new SKPoint(
                    a * p0.X + b * c1.X + c * c2.X + d * p1.X,
                    a * p0.Y + b * c1.Y + c * c2.Y + d * p1.Y);
                if (SegHits(prev, p, r)) return true;
                prev = p;
            }
            return false;
        }

        private static float Dist(SKPoint a, SKPoint b)
        {
            float dx = b.X - a.X, dy = b.Y - a.Y;
            return MathF.Sqrt(dx * dx + dy * dy);
        }
    }
}
