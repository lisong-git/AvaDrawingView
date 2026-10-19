// 2D 几何工具：把 ACadSharp 实体压平成世界坐标下的线段，并做线段级相交判定。
// 只依赖 ACadSharp，不引 SkiaSharp（图形界面那套渲染管线不参与，避免把 UI 依赖拖进来）。
//
// 精度取向：这里只判「有没有接触」，用线段距离 <= 容差 实现，端点相接 / 共线搭接 / 十字相交
// 都能命中；圆、弧、椭圆、样条按角度密度采样后进入同一套线段判定。

using ACadSharp.Entities;

namespace DrawingView.Tools.ShapeExport;

/// <summary>2D 仿射变换：x' = A·x + C·y + E，y' = B·x + D·y + F。</summary>
internal readonly struct Xform
{
    public readonly double A, B, C, D, E, F;

    public Xform(double a, double b, double c, double d, double e, double f)
    {
        A = a; B = b; C = c; D = d; E = e; F = f;
    }

    public static Xform Identity => new(1, 0, 0, 1, 0, 0);

    /// <summary>this ∘ o：先应用 o，再应用 this。</summary>
    public Xform Mul(Xform o) => new(
        A * o.A + C * o.B,
        B * o.A + D * o.B,
        A * o.C + C * o.D,
        B * o.C + D * o.D,
        A * o.E + C * o.F + E,
        B * o.E + D * o.F + F);

    public (double X, double Y) Apply(double x, double y) => (A * x + C * y + E, B * x + D * y + F);

    public static Xform Translate(double tx, double ty) => new(1, 0, 0, 1, tx, ty);

    public static Xform Scale(double sx, double sy) => new(sx, 0, 0, sy, 0, 0);

    public static Xform Rotate(double rad)
    {
        var c = Math.Cos(rad);
        var s = Math.Sin(rad);
        return new Xform(c, s, -s, c, 0, 0);
    }
}

/// <summary>压平 + 相交判定。</summary>
internal static class Geometry
{
    /// <summary>一段线段（世界坐标）。</summary>
    public readonly struct Seg
    {
        public readonly double X1, Y1, X2, Y2;

        public Seg(double x1, double y1, double x2, double y2)
        {
            X1 = x1; Y1 = y1; X2 = x2; Y2 = y2;
        }
    }

    private const double TwoPi = Math.PI * 2;
    private const double ArcDegPerSegment = 6.0;   // 圆弧采样密度：每 ~6° 一段
    private const int MinArcSegments = 4;
    private const int MaxInsertDepth = 8;          // 块引用递归上限（防坏图无限递归）
    private const int MaxArrayInstances = 256;     // MINSERT 阵列实例上限

    // ---------- 对外入口 ----------

    /// <summary>实体 → 世界坐标线段。无几何 / 不支持的类型返回空表。</summary>
    public static List<Seg> Flatten(Entity e)
    {
        var segs = new List<Seg>();
        try
        {
            FlattenEntity(e, Xform.Identity, 0, segs);
        }
        catch
        {
            // 单个实体压平失败不影响整图（坏图常见）
        }
        return segs;
    }

    /// <summary>两组线段是否相交（容差 tol，世界单位）。</summary>
    public static bool Intersects(List<Seg> a, List<Seg> b, double tol)
    {
        foreach (var s in a)
        {
            var sMinX = Math.Min(s.X1, s.X2); var sMaxX = Math.Max(s.X1, s.X2);
            var sMinY = Math.Min(s.Y1, s.Y2); var sMaxY = Math.Max(s.Y1, s.Y2);
            foreach (var t in b)
            {
                // 线段级快速排斥（绝大多数对在这里就被挡掉）
                if (sMinX - tol > Math.Max(t.X1, t.X2)) continue;
                if (sMaxX + tol < Math.Min(t.X1, t.X2)) continue;
                if (sMinY - tol > Math.Max(t.Y1, t.Y2)) continue;
                if (sMaxY + tol < Math.Min(t.Y1, t.Y2)) continue;
                if (SegSegDistance(s, t) <= tol) return true;
            }
        }
        return false;
    }

    // ---------- 压平 ----------

    private static void FlattenEntity(Entity e, Xform m, int depth, List<Seg> segs)
    {
        switch (e)
        {
            case Line l:
                AddSeg(segs, m, l.StartPoint.X, l.StartPoint.Y, l.EndPoint.X, l.EndPoint.Y);
                break;

            // 注意：ACadSharp 里 Arc 继承自 Circle，分支顺序必须是 Arc 在前
            case Arc a:
            {
                var sweep = a.EndAngle - a.StartAngle;
                while (sweep <= 0) sweep += TwoPi;          // 跨 0° 的弧（350°→10°）
                if (sweep < 1e-9 || sweep > TwoPi) sweep = TwoPi;
                AddEllipseArc(segs, m, a.Center.X, a.Center.Y, a.Radius, 0, 0, a.Radius, a.StartAngle, a.StartAngle + sweep);
                break;
            }

            case Ellipse el:
            {
                var ax = el.MajorAxisEndPoint.X; var ay = el.MajorAxisEndPoint.Y;
                var bx = -el.MajorAxisEndPoint.Y * el.RadiusRatio;
                var by = el.MajorAxisEndPoint.X * el.RadiusRatio;
                var t0 = el.StartParameter;
                var t1 = el.EndParameter;
                if (el.IsFullEllipse || Math.Abs(t1 - t0) < 1e-9) { t0 = 0; t1 = TwoPi; }
                else while (t1 <= t0) t1 += TwoPi;
                AddEllipseArc(segs, m, el.Center.X, el.Center.Y, ax, ay, bx, by, t0, t1);
                break;
            }

            // 同理：Ellipse 也继承自 Circle，Circle 必须排在最后
            case Circle c:
                AddEllipseArc(segs, m, c.Center.X, c.Center.Y, c.Radius, 0, 0, c.Radius, 0, TwoPi);
                break;

            case LwPolyline lw:
            {
                var v = lw.Vertices;
                if (v is null || v.Count < 2) break;
                for (var i = 1; i < v.Count; i++)
                    AddChord(segs, m, v[i - 1].Location.X, v[i - 1].Location.Y,
                                      v[i].Location.X, v[i].Location.Y, v[i - 1].Bulge);
                if (lw.IsClosed)
                    AddChord(segs, m, v[v.Count - 1].Location.X, v[v.Count - 1].Location.Y,
                                      v[0].Location.X, v[0].Location.Y, v[v.Count - 1].Bulge);
                break;
            }

            case Polyline2D p2:
            {
                var v = p2.Vertices;
                if (v is null || v.Count < 2) break;
                for (var i = 1; i < v.Count; i++)
                    AddChord(segs, m, v[i - 1].Location.X, v[i - 1].Location.Y,
                                      v[i].Location.X, v[i].Location.Y, v[i - 1].Bulge);
                if (p2.IsClosed)
                    AddChord(segs, m, v[v.Count - 1].Location.X, v[v.Count - 1].Location.Y,
                                      v[0].Location.X, v[0].Location.Y, v[v.Count - 1].Bulge);
                break;
            }

            case Polyline3D p3:
            {
                var v = p3.Vertices;
                if (v is null || v.Count < 2) break;
                for (var i = 1; i < v.Count; i++)
                    AddSeg(segs, m, v[i - 1].Location.X, v[i - 1].Location.Y,
                                    v[i].Location.X, v[i].Location.Y);
                break;
            }

            case Spline sp:
                FlattenSpline(sp, m, segs);
                break;

            case Solid sol:
                AddLoop(segs, m, new[]
                {
                    (sol.FirstCorner.X, sol.FirstCorner.Y),
                    (sol.SecondCorner.X, sol.SecondCorner.Y),
                    (sol.FourthCorner.X, sol.FourthCorner.Y),
                    (sol.ThirdCorner.X, sol.ThirdCorner.Y),
                });
                break;

            case Face3D f3:
                AddLoop(segs, m, new[]
                {
                    (f3.FirstCorner.X, f3.FirstCorner.Y),
                    (f3.SecondCorner.X, f3.SecondCorner.Y),
                    (f3.FourthCorner.X, f3.FourthCorner.Y),
                    (f3.ThirdCorner.X, f3.ThirdCorner.Y),
                });
                break;

            case Hatch h:
                FlattenHatch(h, m, depth, segs);
                break;

            case Insert ins:
                FlattenInsert(ins, m, depth, segs);
                break;
        }
    }

    private static void AddSeg(List<Seg> segs, Xform m, double x1, double y1, double x2, double y2)
    {
        var (ax, ay) = m.Apply(x1, y1);
        var (bx, by) = m.Apply(x2, y2);
        segs.Add(new Seg(ax, ay, bx, by));
    }

    /// <summary>折线一段：有凸度走圆弧，否则直线。</summary>
    private static void AddChord(List<Seg> segs, Xform m, double x1, double y1, double x2, double y2, double bulge)
    {
        if (Math.Abs(bulge) < 1e-9) { AddSeg(segs, m, x1, y1, x2, y2); return; }

        // bulge = tan(θ/4)，θ 为包角（正 = 从 P1 逆时针到 P2）
        var theta = 4.0 * Math.Atan(bulge);
        var dx = x2 - x1; var dy = y2 - y1;
        var chord = Math.Sqrt(dx * dx + dy * dy);
        if (chord < 1e-12) return;

        var r = chord / (2.0 * Math.Sin(theta / 2.0));
        var d = r * Math.Cos(theta / 2.0);
        var cx = (x1 + x2) / 2 - d * dy / chord;
        var cy = (y1 + y2) / 2 + d * dx / chord;
        var rad = Math.Abs(r);
        var a0 = Math.Atan2(y1 - cy, x1 - cx);

        var n = SampleCount(theta);
        var px = x1; var py = y1;
        for (var i = 1; i <= n; i++)
        {
            var t = a0 + theta * i / n;
            var x = cx + rad * Math.Cos(t);
            var y = cy + rad * Math.Sin(t);
            AddSeg(segs, m, px, py, x, y);
            px = x; py = y;
        }
    }

    /// <summary>参数方程 p(t) = 中心 + A·cos t + B·sin t 的椭圆弧（圆是 A=(r,0)、B=(0,r) 的特例）。</summary>
    private static void AddEllipseArc(List<Seg> segs, Xform m, double cx, double cy,
                                      double ax, double ay, double bx, double by,
                                      double t0, double t1)
    {
        var n = SampleCount(t1 - t0);
        var x0 = cx + ax * Math.Cos(t0) + bx * Math.Sin(t0);
        var y0 = cy + ay * Math.Cos(t0) + by * Math.Sin(t0);
        var px = x0; var py = y0;
        for (var i = 1; i <= n; i++)
        {
            var t = t0 + (t1 - t0) * i / n;
            var x = cx + ax * Math.Cos(t) + bx * Math.Sin(t);
            var y = cy + ay * Math.Cos(t) + by * Math.Sin(t);
            AddSeg(segs, m, px, py, x, y);
            px = x; py = y;
        }
    }

    private static void AddLoop(List<Seg> segs, Xform m, (double X, double Y)[] pts)
    {
        for (var i = 0; i < pts.Length; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % pts.Length];
            AddSeg(segs, m, a.X, a.Y, b.X, b.Y);
        }
    }

    private static int SampleCount(double sweep)
    {
        var deg = Math.Abs(sweep) * 180.0 / Math.PI;
        return Math.Max(MinArcSegments, (int)Math.Ceiling(deg / ArcDegPerSegment));
    }

    // ---------- 样条 ----------

    private static void FlattenSpline(Spline sp, Xform m, List<Seg> segs)
    {
        var cps = sp.ControlPoints;
        var fits = sp.FitPoints;
        var knots = sp.Knots?.ToArray();

        // 控制点 + 节点向量齐全时按 B 样条精确采样
        if (cps is not null && cps.Count >= 2 && knots is not null &&
            knots.Length >= cps.Count + sp.Degree + 1 && sp.Degree >= 1)
        {
            var degree = sp.Degree;
            var t0 = knots[degree];
            var t1 = knots[cps.Count];
            if (t1 > t0)
            {
                var spans = Math.Max(1, cps.Count - degree);
                var n = Math.Min(4096, Math.Max(16, spans * 24));
                var prevX = 0.0; var prevY = 0.0;
                for (var i = 0; i <= n; i++)
                {
                    var t = t0 + (t1 - t0) * i / n;
                    var (x, y) = DeBoor(t, degree, knots, cps);
                    if (i > 0) AddSeg(segs, m, prevX, prevY, x, y);
                    prevX = x; prevY = y;
                }
                return;
            }
        }

        // 退路：拟合点连线 → 控制点连线
        var fallback = fits is not null && fits.Count >= 2 ? fits : cps;
        if (fallback is null || fallback.Count < 2) return;
        for (var i = 1; i < fallback.Count; i++)
            AddSeg(segs, m, fallback[i - 1].X, fallback[i - 1].Y, fallback[i].X, fallback[i].Y);
    }

    /// <summary>de Boor 求值（2D）。</summary>
    private static (double X, double Y) DeBoor(double t, int degree, double[] knots, IList<CSMath.XYZ> cps)
    {
        var n = cps.Count - 1;
        var k = degree;
        while (k < n && !(t >= knots[k] && t < knots[k + 1])) k++;
        if (k > n) k = n;

        var dx = new double[degree + 1];
        var dy = new double[degree + 1];
        for (var j = 0; j <= degree; j++)
        {
            var idx = Math.Clamp(k - degree + j, 0, n);
            dx[j] = cps[idx].X;
            dy[j] = cps[idx].Y;
        }

        for (var r = 1; r <= degree; r++)
            for (var j = degree; j >= r; j--)
            {
                var idx = k - degree + j;
                var lo = Math.Clamp(idx, 0, Math.Max(0, knots.Length - 1));
                var hi = Math.Clamp(idx + degree - r + 1, 0, Math.Max(0, knots.Length - 1));
                var denom = knots[hi] - knots[lo];
                var a = Math.Abs(denom) > 1e-12 ? (t - knots[lo]) / denom : 0.0;
                dx[j] = (1 - a) * dx[j - 1] + a * dx[j];
                dy[j] = (1 - a) * dy[j - 1] + a * dy[j];
            }

        return (dx[degree], dy[degree]);
    }

    // ---------- 填充 ----------

    private static void FlattenHatch(Hatch h, Xform m, int depth, List<Seg> segs)
    {
        if (h.Paths is null) return;
        foreach (var bp in h.Paths)
        {
            if (bp?.Edges is null) continue;
            foreach (var edge in bp.Edges)
            {
                switch (edge)
                {
                    case Hatch.BoundaryPath.Line le:
                        AddSeg(segs, m, le.Start.X, le.Start.Y, le.End.X, le.End.Y);
                        break;

                    case Hatch.BoundaryPath.Polyline pe:
                    {
                        var verts = pe.Vertices;
                        var bulges = pe.Bulges?.ToList();
                        if (verts is null || verts.Count < 2) break;
                        double BulgeAt(int i) => bulges is not null && i < bulges.Count ? bulges[i] : 0;
                        for (var i = 1; i < verts.Count; i++)
                            AddChord(segs, m, verts[i - 1].X, verts[i - 1].Y, verts[i].X, verts[i].Y, BulgeAt(i - 1));
                        if (pe.IsClosed)
                            AddChord(segs, m, verts[verts.Count - 1].X, verts[verts.Count - 1].Y, verts[0].X, verts[0].Y,
                                     BulgeAt(verts.Count - 1));
                        break;
                    }

                    case Hatch.BoundaryPath.Arc ae:
                    {
                        // ACadSharp 的 CW 边界弧按顺时针参数化给角：点(θ)=c+r(cosθ, −sinθ)；
                        // 顺时针 start→end ≡ 标准参数下 −start → −end，等价于 y 取反
                        double t0, t1;
                        if (ae.CounterClockWise) { t0 = ae.StartAngle; t1 = ae.EndAngle; }
                        else { t0 = -ae.StartAngle; t1 = -ae.EndAngle; }
                        if (t1 <= t0) t1 += TwoPi;
                        var bySign = ae.CounterClockWise ? 1.0 : -1.0;
                        AddEllipseArc(segs, m, ae.Center.X, ae.Center.Y, ae.Radius, 0, 0, ae.Radius * bySign, t0, t1);
                        break;
                    }

                    case Hatch.BoundaryPath.Ellipse ee:
                    {
                        var mx = ee.MajorAxisEndPoint.X; var my = ee.MajorAxisEndPoint.Y;
                        var ratio = ee.MinorToMajorRatio;
                        var t0 = ee.StartAngle; var t1 = ee.EndAngle;
                        if (t1 <= t0) t1 += TwoPi;
                        AddEllipseArc(segs, m, ee.Center.X, ee.Center.Y,
                                      mx, my, -my * ratio, mx * ratio, t0, t1);
                        break;
                    }

                    case Hatch.BoundaryPath.Spline se:
                    {
                        var cps = se.ControlPoints;
                        if (cps is null || cps.Count < 2) break;
                        for (var i = 1; i < cps.Count; i++)
                            AddSeg(segs, m, cps[i - 1].X, cps[i - 1].Y, cps[i].X, cps[i].Y);
                        break;
                    }
                }
            }
        }
    }

    // ---------- 块引用 ----------

    private static void FlattenInsert(Insert ins, Xform parent, int depth, List<Seg> segs)
    {
        if (depth >= MaxInsertDepth || ins.Block is null) return;

        var local = Xform.Translate(ins.InsertPoint.X, ins.InsertPoint.Y)
                         .Mul(Xform.Rotate(ins.Rotation))
                         .Mul(Xform.Scale(ins.XScale, ins.YScale));

        var rows = Math.Max(1, (int)ins.RowCount);
        var cols = Math.Max(1, (int)ins.ColumnCount);
        if ((long)rows * cols > MaxArrayInstances) { rows = 1; cols = 1; }

        for (var r = 0; r < rows; r++)
            for (var c = 0; c < cols; c++)
            {
                // 阵列偏移在插入自身的坐标系里量取（随 Rotation 旋转，不随 Scale 缩放）
                var ox = c * ins.ColumnSpacing;
                var oy = r * ins.RowSpacing;
                var ca = Math.Cos(ins.Rotation); var sa = Math.Sin(ins.Rotation);
                var array = Xform.Translate(ox * ca - oy * sa, ox * sa + oy * ca);
                var total = parent.Mul(array).Mul(local);
                foreach (var child in ins.Block.Entities)
                    FlattenEntity(child, total, depth + 1, segs);
            }
    }

    // ---------- 线段距离 ----------

    private static double SegSegDistance(in Seg a, in Seg b)
    {
        if (SegmentsIntersect(a, b)) return 0;

        var d = PointSegDistance(a.X1, a.Y1, b);
        d = Math.Min(d, PointSegDistance(a.X2, a.Y2, b));
        d = Math.Min(d, PointSegDistance(b.X1, b.Y1, a));
        d = Math.Min(d, PointSegDistance(b.X2, b.Y2, a));
        return d;
    }

    private static bool SegmentsIntersect(in Seg a, in Seg b)
    {
        var d1 = Cross(a, b.X1, b.Y1);
        var d2 = Cross(a, b.X2, b.Y2);
        var d3 = Cross(b, a.X1, a.Y1);
        var d4 = Cross(b, a.X2, a.Y2);
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
               ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }

    private static double Cross(in Seg s, double px, double py)
        => (s.X2 - s.X1) * (py - s.Y1) - (s.Y2 - s.Y1) * (px - s.X1);

    private static double PointSegDistance(double px, double py, in Seg s)
    {
        var dx = s.X2 - s.X1; var dy = s.Y2 - s.Y1;
        var len2 = dx * dx + dy * dy;
        double cx, cy;
        if (len2 < 1e-18)
        {
            cx = s.X1; cy = s.Y1;
        }
        else
        {
            var t = ((px - s.X1) * dx + (py - s.Y1) * dy) / len2;
            t = Math.Clamp(t, 0, 1);
            cx = s.X1 + t * dx; cy = s.Y1 + t * dy;
        }
        var ex = px - cx; var ey = py - cy;
        return Math.Sqrt(ex * ex + ey * ey);
    }
}
