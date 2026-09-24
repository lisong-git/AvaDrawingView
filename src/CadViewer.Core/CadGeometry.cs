using System;
using System.Linq;
using ACadSharp.Entities;
using SkiaSharp;

namespace CadViewer.Core;

/// <summary>
/// 实体几何 → <see cref="SKPath"/>（**局部坐标**，调用方自己施加世界变换）。
///
/// <para>存在的理由与 <see cref="CadTransform"/> 相同：标注目标树要把「多段线的第 3 段」、
/// 「填充的第 2 个环」、「环内某一条边」单独画成高亮，而显示层本来就会画这些几何。
/// 两处各写一遍必然漂移（漂移表现为「高亮位置对但形状不符」，很难发现），
/// 因此 <see cref="GeometryConverter"/> 改为调用本类型 —— 圆弧、凸度、填充边界边
/// 在物理上只有一份实现。</para>
/// </summary>
public static class CadGeometry
{
    // ---------- 圆弧 / 凸度 ----------

    public static SKRect OvalRect(double cx, double cy, double r)
        => new((float)(cx - r), (float)(cy - r), (float)(cx + r), (float)(cy + r));

    /// <summary>
    /// 弧的扫角（弧度，恒为正）。
    ///
    /// <para><b>不要用 ACadSharp 的 <c>Arc.Sweep</c></b>：3.4.9 的实现是 <c>start - end</c>，
    /// 对正常逆时针弧返回<b>负值</b>。历史缺陷：原代码据此判断 <c>sweep &lt;= 0</c> 就按整圆处理，
    /// 结果图纸上每一条弧都被画成了整圆。新增的 sample_features.dxf 精确断言抓到了它。</para>
    /// </summary>
    public static double ArcSweep(double startAngle, double endAngle)
    {
        double sweep = endAngle - startAngle;
        while (sweep <= 0) sweep += 2 * Math.PI;   // 跨越 0° 的弧（如 350°→10°）
        return sweep;
    }

    /// <summary>
    /// 按「起始角 + 扫角」向路径追加一段圆弧（角度单位自动换算）。
    ///
    /// <para>Skia 的 <c>ArcTo</c> 在 sweep 恰为 ±360° 时<b>什么也不画</b>
    /// （实测：整圆边界的实心填充只剩一个点），故整圆退化为 359.999°，
    /// 缺口 0.001° 在屏幕上不可见。</para>
    /// </summary>
    public static void AppendArcTo(SKPath path, SKRect oval, double startRad, double sweepRad)
    {
        double sweepDeg = sweepRad * 180.0 / Math.PI;
        if (sweepDeg >= 360.0 - 1e-6) sweepDeg = 359.999;
        else if (sweepDeg <= -360.0 + 1e-6) sweepDeg = -359.999;
        path.ArcTo(oval, (float)(startRad * 180.0 / Math.PI), (float)sweepDeg, false);
    }

    /// <summary>
    /// 多段线顶点间的凸度圆弧：<paramref name="bulge"/> 是该段的凸度
    /// （<c>tan(包含角/4)</c>，正 = 逆时针）。凸度为 0 或两点重合时退化为直线。
    /// </summary>
    public static void AppendBulgeArc(SKPath path, SKPoint p1, SKPoint p2, double bulge)
    {
        double theta = 4.0 * Math.Atan(bulge);
        double dx = p2.X - p1.X, dy = p2.Y - p1.Y;
        double d = Math.Sqrt(dx * dx + dy * dy);
        if (d < 1e-9 || Math.Abs(theta) >= Math.PI * 2 - 1e-6) { path.LineTo(p2); return; }

        double r = d / (2 * Math.Sin(Math.Abs(theta) / 2));
        double mx = (p1.X + p2.X) / 2, my = (p1.Y + p2.Y) / 2;
        double hSq = r * r - d * d / 4;
        double h = Math.Sqrt(Math.Max(0, hSq));
        double sign = bulge > 0 ? 1 : -1;                 // 逆时针 → 圆心在行进方向左侧
        double cx = mx + (-dy / d) * h * sign;
        double cy = my + (dx / d) * h * sign;
        double a1 = Math.Atan2(p1.Y - cy, p1.X - cx);
        var oval = OvalRect(cx, cy, r);
        AppendArcTo(path, oval, a1, theta);
    }

    /// <summary>
    /// 追加一条折线边：凸度非 0 走圆弧，否则走直线。
    /// <paramref name="bulge"/> 是**本段**的凸度（调用方负责按「起点顶点管本段」
    /// 的语义把它取出来，见 <see cref="AppendLwPolyline"/> 的说明）。
    /// </summary>
    public static void AppendChord(SKPath path, SKPoint a, SKPoint b, double bulge)
    {
        if (Math.Abs(bulge) > 1e-9) AppendBulgeArc(path, a, b, bulge);
        else path.LineTo(b);
    }

    private static SKPoint P(double x, double y) => new((float)x, (float)y);

    /// <summary>
    /// 椭圆（弧）参数方程采样：<c>p(t) = center + A·cos t + B·sin t</c>，
    /// <c>A</c> = 长轴端点向量，<c>B</c> = 长轴左法线 × 半径比。整椭圆传 t0=0 / t1=2π。
    /// </summary>
    public static void AppendEllipticalArc(SKPath path, double cx, double cy,
                                           double ax, double ay, double bx, double by,
                                           double t0, double t1, int segments)
    {
        if (segments < 1) segments = 1;
        if (t1 <= t0) t1 += 2 * Math.PI;
        for (int i = 0; i <= segments; i++)
        {
            double t = t0 + (t1 - t0) * i / segments;
            float x = (float)(cx + ax * Math.Cos(t) + bx * Math.Sin(t));
            float y = (float)(cy + ay * Math.Cos(t) + by * Math.Sin(t));
            if (i == 0) path.MoveTo(x, y); else path.LineTo(x, y);
        }
    }

    // ---------- 多段线 ----------

    /// <summary>
    /// 段数：开放折线 = 顶点数 − 1；闭合折线 = 顶点数（末顶点回到首顶点也算一段）。
    /// 顶点数 &lt; 2 时为 0。
    /// </summary>
    public static int VertexSegmentCount(int vertexCount, bool isClosed)
        => vertexCount < 2 ? 0 : (isClosed ? vertexCount : vertexCount - 1);

    public static int SegmentCount(LwPolyline lw)
        => lw?.Vertices == null ? 0 : VertexSegmentCount(lw.Vertices.Count, lw.IsClosed);

    public static int SegmentCount(Polyline2D p)
        => p?.Vertices == null ? 0 : VertexSegmentCount(p.Vertices.Count, p.IsClosed);

    /// <summary>段的类型名（用于节点命名与 .lab 子路径地址）。</summary>
    public static string SegmentTypeName(double bulge)
        => Math.Abs(bulge) > 1e-9 ? "Arc" : "Line";

    /// <summary>
    /// 整条 LwPolyline 的轮廓。
    ///
    /// <para><b>凸度归属（2026-09-23 定标修正）</b>：DXF 组码 42 挂在顶点上，语义是
    /// 「该顶点到<b>下一顶点</b>那一段的凸度」，闭合折线的最后顶点管收尾段。
    /// 原实现把每段的凸度取成<b>终点顶点</b>的 <c>Bulge</c>，等价于整体错位一段：
    /// 首顶点的凸度被完全丢弃，闭合收尾段的凸度被挪到上一段去。定标证据
    /// （<c>LabelProbe --bulge</c>）：样例里 v0 带 bulge=1 的闭合折线，
    /// 规范与 ACadSharp 都给 (0,140)-(20,170)（半圆），原实现给 (0,150)-(20,170)（纯三角形）。</para>
    /// </summary>
    public static void AppendLwPolyline(SKPath path, LwPolyline lw)
    {
        if (lw?.Vertices == null || lw.Vertices.Count == 0) return;
        var v = lw.Vertices;
        int n = v.Count;
        path.MoveTo(P(v[0].Location.X, v[0].Location.Y));
        for (int i = 1; i < n; i++)
            AppendChord(path, P(v[i - 1].Location.X, v[i - 1].Location.Y),
                              P(v[i].Location.X, v[i].Location.Y), v[i - 1].Bulge);
        if (lw.IsClosed && n > 1)
        {
            if (Math.Abs(v[n - 1].Bulge) > 1e-9)
                AppendBulgeArc(path, P(v[n - 1].Location.X, v[n - 1].Location.Y),
                                     P(v[0].Location.X, v[0].Location.Y), v[n - 1].Bulge);
            else path.Close();   // 收尾段无凸度时保留 Close()，填充语义需要真正闭合
        }
    }

    /// <summary>整条 Polyline2D 的轮廓（凸度归属同 <see cref="AppendLwPolyline"/>）。</summary>
    public static void AppendPolyline2D(SKPath path, Polyline2D p)
    {
        if (p?.Vertices == null || p.Vertices.Count == 0) return;
        var v = p.Vertices;
        int n = v.Count;
        path.MoveTo(P(v[0].Location.X, v[0].Location.Y));
        for (int i = 1; i < n; i++)
            AppendChord(path, P(v[i - 1].Location.X, v[i - 1].Location.Y),
                              P(v[i].Location.X, v[i].Location.Y), v[i - 1].Bulge);
        if (p.IsClosed && n > 1)
        {
            if (Math.Abs(v[n - 1].Bulge) > 1e-9)
                AppendBulgeArc(path, P(v[n - 1].Location.X, v[n - 1].Location.Y),
                                     P(v[0].Location.X, v[0].Location.Y), v[n - 1].Bulge);
            else path.Close();
        }
    }

    /// <summary>
    /// 只追加第 <paramref name="segmentIndex"/> 段（标注按段分解用）。
    /// <paramref name="moveTo"/> = true 时该段自成一个子路径（高亮单段时用）。
    /// </summary>
    /// <returns>false = 索引越界或顶点不足。</returns>
    public static bool AppendLwPolylineSegment(SKPath path, LwPolyline lw, int segmentIndex, bool moveTo)
    {
        if (lw?.Vertices == null) return false;
        if (segmentIndex < 0 || segmentIndex >= SegmentCount(lw)) return false;
        var n = lw.Vertices.Count;
        int i = segmentIndex, j = segmentIndex + 1;
        if (j >= n) j -= n;                       // 闭合段：末顶点 → 首顶点
        var a = P(lw.Vertices[i].Location.X, lw.Vertices[i].Location.Y);
        var b = P(lw.Vertices[j].Location.X, lw.Vertices[j].Location.Y);
        if (moveTo) path.MoveTo(a);
        AppendChord(path, a, b, lw.Vertices[i].Bulge);   // 起点顶点管本段
        return true;
    }

    /// <summary>只追加第 <paramref name="segmentIndex"/> 段（Polyline2D 版）。</summary>
    public static bool AppendPolyline2DSegment(SKPath path, Polyline2D p, int segmentIndex, bool moveTo)
    {
        if (p?.Vertices == null) return false;
        if (segmentIndex < 0 || segmentIndex >= SegmentCount(p)) return false;
        var n = p.Vertices.Count;
        int i = segmentIndex, j = segmentIndex + 1;
        if (j >= n) j -= n;
        var a = P(p.Vertices[i].Location.X, p.Vertices[i].Location.Y);
        var b = P(p.Vertices[j].Location.X, p.Vertices[j].Location.Y);
        if (moveTo) path.MoveTo(a);
        AppendChord(path, a, b, p.Vertices[i].Bulge);
        return true;
    }

    /// <summary>
    /// 第 <paramref name="segmentIndex"/> 段的凸度 = 该段<b>起点顶点</b>的 <c>Bulge</c>。
    /// 闭合折线的收尾段（索引 = 顶点数 − 1）取末顶点的 <c>Bulge</c>，
    /// 因为它的起点就是末顶点 —— 索引直接对应顶点索引，无需绕回。
    /// </summary>
    public static double LwSegmentBulge(LwPolyline lw, int segmentIndex)
    {
        if (lw?.Vertices == null) return 0;
        if (segmentIndex < 0 || segmentIndex >= SegmentCount(lw)) return 0;
        return lw.Vertices[segmentIndex].Bulge;
    }

    /// <summary>第 <paramref name="segmentIndex"/> 段的凸度（Polyline2D 版）。</summary>
    public static double Polyline2DSegmentBulge(Polyline2D p, int segmentIndex)
    {
        if (p?.Vertices == null) return 0;
        if (segmentIndex < 0 || segmentIndex >= SegmentCount(p)) return 0;
        return p.Vertices[segmentIndex].Bulge;
    }

    /// <summary>第 <paramref name="segmentIndex"/> 段的两个端点（局部坐标）。</summary>
    public static bool TryLwSegmentEndpoints(LwPolyline lw, int segmentIndex, out SKPoint a, out SKPoint b)
    {
        a = b = default;
        if (lw?.Vertices == null) return false;
        var n = lw.Vertices.Count;
        if (n < 2 || segmentIndex < 0 || segmentIndex >= SegmentCount(lw)) return false;
        int j = segmentIndex + 1; if (j >= n) j -= n;
        a = new SKPoint((float)lw.Vertices[segmentIndex].Location.X, (float)lw.Vertices[segmentIndex].Location.Y);
        b = new SKPoint((float)lw.Vertices[j].Location.X, (float)lw.Vertices[j].Location.Y);
        return true;
    }

    /// <summary>第 <paramref name="segmentIndex"/> 段的两个端点（Polyline2D 版）。</summary>
    public static bool TryPolyline2DSegmentEndpoints(Polyline2D p, int segmentIndex, out SKPoint a, out SKPoint b)
    {
        a = b = default;
        if (p?.Vertices == null) return false;
        var n = p.Vertices.Count;
        if (n < 2 || segmentIndex < 0 || segmentIndex >= SegmentCount(p)) return false;
        int j = segmentIndex + 1; if (j >= n) j -= n;
        a = new SKPoint((float)p.Vertices[segmentIndex].Location.X, (float)p.Vertices[segmentIndex].Location.Y);
        b = new SKPoint((float)p.Vertices[j].Location.X, (float)p.Vertices[j].Location.Y);
        return true;
    }

    // ---------- 填充边界 ----------
    public static int HatchLoopCount(Hatch h) => h?.Paths?.Count ?? 0;

    public static int HatchEdgeCount(Hatch.BoundaryPath bp) => bp?.Edges?.Count ?? 0;

    /// <summary>
    /// 追加一个边界环的全部边。**注意**：各条边各自 MoveTo 到自身起点，
    /// 所以一个环会形成多个子路径（与原转换器行为一致，保留）。
    /// </summary>
    /// <returns>未能处理的边数（当前仅 <c>Spline</c> 边，历史行为即如此）。</returns>
    public static int AppendHatchLoop(SKPath path, Hatch.BoundaryPath bp)
    {
        if (bp?.Edges == null) return 0;
        int unsupported = 0;
        foreach (var edge in bp.Edges)
            unsupported += AppendHatchEdge(path, edge) ? 0 : 1;
        return unsupported;
    }

    /// <summary>追加一条边界边。</summary>
    /// <returns>false = 该边类型未支持（当前仅 <c>Spline</c>）。</returns>
    public static bool AppendHatchEdge(SKPath path, Hatch.BoundaryPath.Edge edge)
    {
        switch (edge)
        {
            case Hatch.BoundaryPath.Line le:
                path.MoveTo((float)le.Start.X, (float)le.Start.Y);
                path.LineTo((float)le.End.X, (float)le.End.Y);
                return true;

            case Hatch.BoundaryPath.Arc ae:
            {
                // 边界弧：方向由 CounterClockWise 给出，扫角的正负号即代表方向
                double sweep = ae.EndAngle - ae.StartAngle;
                if (ae.CounterClockWise) { while (sweep <= 0) sweep += 2 * Math.PI; }
                else { while (sweep >= 0) sweep -= 2 * Math.PI; }
                var oval = OvalRect(ae.Center.X, ae.Center.Y, ae.Radius);
                AppendArcTo(path, oval, ae.StartAngle, sweep);
                return true;
            }

            case Hatch.BoundaryPath.Ellipse ee:
            {
                var ax = (float)ee.MajorAxisEndPoint.X; var ay = (float)ee.MajorAxisEndPoint.Y;
                var bx = (float)(-ee.MajorAxisEndPoint.Y * ee.MinorToMajorRatio);
                var by = (float)(ee.MajorAxisEndPoint.X * ee.MinorToMajorRatio);
                AppendEllipticalArc(path, ee.Center.X, ee.Center.Y, ax, ay, bx, by,
                                    ee.StartAngle, ee.EndAngle, 48);
                return true;
            }

            case Hatch.BoundaryPath.Polyline pe:
            {
                if (pe.Vertices == null) return true;
                var verts = pe.Vertices;
                var bulges = pe.Bulges?.ToList();   // ACadSharp 暴露的是 IEnumerable，需物化后按下标取
                int n = verts.Count;
                if (n == 0) return true;
                path.MoveTo(P(verts[0].X, verts[0].Y));
                for (int i = 1; i < n; i++)
                {
                    // 凸度归属同多段线：Bulges[i] 是**顶点 i** 的凸度，管 i→i+1 这一段
                    double b = (bulges != null && i - 1 < bulges.Count) ? bulges[i - 1] : 0;
                    AppendChord(path, P(verts[i - 1].X, verts[i - 1].Y), P(verts[i].X, verts[i].Y), b);
                }
                if (pe.IsClosed && n > 1)
                {
                    double bl = (bulges != null && n - 1 < bulges.Count) ? bulges[n - 1] : 0;
                    if (Math.Abs(bl) > 1e-9)
                        AppendBulgeArc(path, P(verts[n - 1].X, verts[n - 1].Y), P(verts[0].X, verts[0].Y), bl);
                    else path.Close();
                }
                return true;
            }

            default:
                // 未支持：Hatch.BoundaryPath.Spline
                return false;
        }
    }
}
