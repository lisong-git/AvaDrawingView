using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using SkiaSharp;

namespace CadViewer.Core;

/// <summary>
/// 几何转换器：ACadSharp 实体 → 显示基元（SKPath / 文本）。
/// 单个实体转换失败时跳过并计数，不影响整图。
/// 圆/弧用圆锥曲线构造（分辨率无关），椭圆与样条为压平折线。
/// </summary>
public sealed class GeometryConverter
{
    private readonly List<CadEntity> _entities = new();
    private readonly Dictionary<string, uint> _layerColors = new(StringComparer.OrdinalIgnoreCase);
    public int Skipped { get; private set; }

    public List<CadEntity> Convert(CadDocument doc)
    {
        _entities.Clear();
        _layerColors.Clear();
        Skipped = 0;

        CacheLayerColors(doc);

        foreach (var e in EnumerateModelSpace(doc))
            ConvertEntity(e, SKMatrix.Identity, 0);

        return _entities;
    }

    public IReadOnlyList<string> LayerNames => _layerColors.Keys.ToArray();

    // ---------- 入口 ----------

    private static IEnumerable<Entity> EnumerateModelSpace(CadDocument doc)
    {
        var ms = doc.ModelSpace;
        if (ms?.Entities != null)
            foreach (var e in ms.Entities)
                yield return e;
    }

    private void ConvertEntity(Entity e, in SKMatrix parent, int depth)
    {
        try
        {
            switch (e)
            {
                case Line l: AddLine(l, parent); break;
                case Arc a: AddArc(a, parent); break;
                case Circle c: AddCircle(c, parent); break;
                case Ellipse el: AddEllipse(el, parent); break;
                case LwPolyline lw: AddLwPolyline(lw, parent); break;
                case Polyline2D p2: AddPolyline2D(p2, parent); break;
                case Spline sp: AddSpline(sp, parent); break;
                case MText mt:
                {
                    var raw = !string.IsNullOrEmpty(mt.PlainText) ? mt.PlainText : CleanMText(mt.Value);
                    AddTextEntity(raw, mt.InsertPoint, mt.Height, mt.Rotation, mt, parent);
                    break;
                }
                case TextEntity t: AddTextEntity(t.Value, t.InsertPoint, t.Height, t.Rotation, t, parent); break;
                case Insert ins: ConvertInsert(ins, parent, depth); break;
                case Hatch h: AddHatch(h, parent); break;
                case Solid sol: AddSolid(sol, parent); break;
                case Leader ld: AddLeader(ld, parent); break;
                case Dimension dim: AddDimensionBlock(dim, parent, depth); break;
                case ACadSharp.Entities.Point pt: AddPointCross(pt, parent); break;
                default: Skipped++; break;
            }
        }
        catch
        {
            Skipped++;
        }
    }

    // ---------- 基础实体 ----------

    private void AddLine(Line l, in SKMatrix m)
    {
        var path = new SKPath();
        path.MoveTo((float)l.StartPoint.X, (float)l.StartPoint.Y);
        path.LineTo((float)l.EndPoint.X, (float)l.EndPoint.Y);
        AddStroke(path, l, m);
    }

    private void AddCircle(Circle c, in SKMatrix m)
    {
        var path = new SKPath();
        path.AddCircle((float)c.Center.X, (float)c.Center.Y, (float)c.Radius, SKPathDirection.Clockwise);
        AddStroke(path, c, m);
    }

    private void AddArc(Arc a, in SKMatrix m)
    {
        double sweep = a.Sweep;
        if (sweep <= 0 || sweep >= 2 * Math.PI) sweep = 2 * Math.PI - 1e-4;
        var path = new SKPath();
        var oval = OvalRect(a.Center.X, a.Center.Y, a.Radius);
        path.AddArc(oval, (float)(a.StartAngle * 180.0 / Math.PI), (float)(sweep * 180.0 / Math.PI));
        AddStroke(path, a, m);
    }

    private void AddEllipse(Ellipse el, in SKMatrix m)
    {
        // p(t) = center + A·cos(t) + B·sin(t)，A = 长轴端点向量，B = 左法线 × 半径比
        var ax = (float)el.MajorAxisEndPoint.X;
        var ay = (float)el.MajorAxisEndPoint.Y;
        var bx = (float)(-el.MajorAxisEndPoint.Y * el.RadiusRatio);
        var by = (float)(el.MajorAxisEndPoint.X * el.RadiusRatio);
        double t0 = el.StartParameter, t1 = el.EndParameter;
        if (t1 <= t0) t1 += 2 * Math.PI;

        var path = new SKPath();
        const int seg = 128;
        for (int i = 0; i <= seg; i++)
        {
            double t = t0 + (t1 - t0) * i / seg;
            float x = (float)el.Center.X + ax * (float)Math.Cos(t) + bx * (float)Math.Sin(t);
            float y = (float)el.Center.Y + ay * (float)Math.Cos(t) + by * (float)Math.Sin(t);
            if (i == 0) path.MoveTo(x, y); else path.LineTo(x, y);
        }
        AddStroke(path, el, m);
    }

    private void AddLwPolyline(LwPolyline lw, in SKMatrix m)
    {
        if (lw.Vertices == null || lw.Vertices.Count == 0) { Skipped++; return; }
        var path = new SKPath();
        var prev = default(SKPoint?);
        foreach (var v in lw.Vertices)
        {
            var cur = new SKPoint((float)v.Location.X, (float)v.Location.Y);
            if (prev is null) { path.MoveTo(cur); }
            else if (Math.Abs(v.Bulge) > 1e-9) AddBulgeArc(path, prev.Value, cur, v.Bulge);
            else path.LineTo(cur);
            prev = cur;
        }
        if (lw.IsClosed && prev is not null) path.Close();
        float lineWidth = lw.ConstantWidth > 0 ? (float)lw.ConstantWidth : 0f;
        AddStroke(path, lw, m, lineWidth);
    }

    private void AddPolyline2D(Polyline2D p2, in SKMatrix m)
    {
        if (p2.Vertices == null || p2.Vertices.Count == 0) { Skipped++; return; }
        var path = new SKPath();
        var prev = default(SKPoint?);
        foreach (var v in p2.Vertices)
        {
            var cur = new SKPoint((float)v.Location.X, (float)v.Location.Y);
            if (prev is null) { path.MoveTo(cur); }
            else if (Math.Abs(v.Bulge) > 1e-9) AddBulgeArc(path, prev.Value, cur, v.Bulge);
            else path.LineTo(cur);
            prev = cur;
        }
        if (p2.IsClosed && prev is not null) path.Close();
        AddStroke(path, p2, m);
    }

    private void AddSpline(Spline sp, in SKMatrix m)
    {
        if (sp.ControlPoints == null || sp.ControlPoints.Count < 2) { Skipped++; return; }
        var cx = sp.ControlPoints.Select(p => p.X).ToArray();
        var cy = sp.ControlPoints.Select(p => p.Y).ToArray();
        var knots = sp.Knots?.ToArray() ?? Array.Empty<double>();
        var path = SplineFlattener.Flatten(cx, cy, knots, sp.Degree);
        if (path is null) { Skipped++; return; }
        AddStroke(path, sp, m);
    }

    private void AddLeader(Leader ld, in SKMatrix m)
    {
        if (ld.Vertices == null || ld.Vertices.Count == 0) { Skipped++; return; }
        var path = new SKPath();
        bool first = true;
        foreach (var v in ld.Vertices)
        {
            if (first) { path.MoveTo((float)v.X, (float)v.Y); first = false; }
            else path.LineTo((float)v.X, (float)v.Y);
        }
        AddStroke(path, ld, m);
    }

    private void AddPointCross(ACadSharp.Entities.Point pt, in SKMatrix m)
    {
        const float s = 1.0f;
        var path = new SKPath();
        path.MoveTo((float)pt.Location.X - s, (float)pt.Location.Y);
        path.LineTo((float)pt.Location.X + s, (float)pt.Location.Y);
        path.MoveTo((float)pt.Location.X, (float)pt.Location.Y - s);
        path.LineTo((float)pt.Location.X, (float)pt.Location.Y + s);
        AddStroke(path, pt, m);
    }

    private void AddSolid(Solid sol, in SKMatrix m)
    {
        var path = new SKPath();
        path.MoveTo((float)sol.FirstCorner.X, (float)sol.FirstCorner.Y);
        path.LineTo((float)sol.SecondCorner.X, (float)sol.SecondCorner.Y);
        path.LineTo((float)sol.FourthCorner.X, (float)sol.FourthCorner.Y);
        path.LineTo((float)sol.ThirdCorner.X, (float)sol.ThirdCorner.Y);
        path.Close();
        AddFill(path, sol, m);
    }

    // ---------- 组合实体 ----------

    private void ConvertInsert(Insert ins, in SKMatrix parent, int depth)
    {
        if (depth > 16 || ins.Block is null) { Skipped++; return; }
        float sx = (float)ins.XScale, sy = (float)ins.YScale;
        if (Math.Abs(sx) < 1e-9f || Math.Abs(sy) < 1e-9f) return;

        var bp = SKPoint.Empty;
        try { if (ins.Block.BlockEntity != null) bp = new SKPoint((float)ins.Block.BlockEntity.BasePoint.X, (float)ins.Block.BlockEntity.BasePoint.Y); } catch { }

        var m = ScaleM(sx, sy);
        m = Mul(m, RotM((float)ins.Rotation));
        m = Mul(m, TransM((float)ins.InsertPoint.X - bp.X, (float)ins.InsertPoint.Y - bp.Y));
        var total = Mul(m, parent);

        foreach (var child in ins.Block.Entities)
            ConvertEntity(child, total, depth + 1);
    }

    private void AddDimensionBlock(Dimension dim, in SKMatrix parent, int depth)
    {
        var block = dim.Block;
        if (block is null) { Skipped++; return; }
        foreach (var child in block.Entities)
            ConvertEntity(child, parent, depth + 1);
    }

    private void AddHatch(Hatch h, in SKMatrix m)
    {
        if (h.Paths == null) { Skipped++; return; }
        foreach (var bp in h.Paths)
        {
            if (bp?.Edges == null || bp.Edges.Count == 0) continue;
            var path = new SKPath();
            foreach (var edge in bp.Edges)
            {
                switch (edge)
                {
                    case Hatch.BoundaryPath.Line le:
                        path.MoveTo((float)le.Start.X, (float)le.Start.Y);
                        path.LineTo((float)le.End.X, (float)le.End.Y);
                        break;
                    case Hatch.BoundaryPath.Arc ae:
                    {
                        double start = ae.StartAngle, end = ae.EndAngle;
                        if (ae.CounterClockWise) { while (end <= start) end += 2 * Math.PI; }
                        else { while (start <= end) start += 2 * Math.PI; }
                        double sweep = end - start;
                        var oval = OvalRect(ae.Center.X, ae.Center.Y, ae.Radius);
                        path.ArcTo(oval, (float)(start * 180.0 / Math.PI), (float)(sweep * 180.0 / Math.PI), false);
                        break;
                    }
                    case Hatch.BoundaryPath.Ellipse ee:
                    {
                        var ax = (float)ee.MajorAxisEndPoint.X; var ay = (float)ee.MajorAxisEndPoint.Y;
                        var bx = (float)(-ee.MajorAxisEndPoint.Y * ee.MinorToMajorRatio);
                        var by = (float)(ee.MajorAxisEndPoint.X * ee.MinorToMajorRatio);
                        double t0 = ee.StartAngle, t1 = ee.EndAngle;
                        if (t1 <= t0) t1 += 2 * Math.PI;
                        for (int i = 0; i <= 48; i++)
                        {
                            double t = t0 + (t1 - t0) * i / 48;
                            float x = (float)ee.Center.X + ax * (float)Math.Cos(t) + bx * (float)Math.Sin(t);
                            float y = (float)ee.Center.Y + ay * (float)Math.Cos(t) + by * (float)Math.Sin(t);
                            if (i == 0) path.MoveTo(x, y); else path.LineTo(x, y);
                        }
                        break;
                    }
                    case Hatch.BoundaryPath.Polyline pe:
                    {
                        if (pe.Vertices == null) break;
                        var verts = pe.Vertices.ToList();
                        var bulges = (pe.Bulges ?? Array.Empty<double>()).ToList();
                        SKPoint? prev = null;
                        for (int i = 0; i < verts.Count; i++)
                        {
                            var cur = new SKPoint((float)verts[i].X, (float)verts[i].Y);
                            double b = i < bulges.Count ? bulges[i] : 0;
                            if (prev is null) path.MoveTo(cur);
                            else if (Math.Abs(b) > 1e-9) AddBulgeArc(path, prev.Value, cur, b);
                            else path.LineTo(cur);
                            prev = cur;
                        }
                        if (pe.IsClosed && prev is not null) path.Close();
                        break;
                    }
                }
            }
            AddStroke(path, h, m);
        }
    }

    // ---------- 凸弧 / 工具 ----------

    /// <summary>凸度 (bulge) 圆弧：θ = 4·atan(bulge)，正凸度 = 世界系逆时针。</summary>
    private static void AddBulgeArc(SKPath path, SKPoint p1, SKPoint p2, double bulge)
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
        path.ArcTo(oval, (float)(a1 * 180.0 / Math.PI), (float)(theta * 180.0 / Math.PI), false);
    }

    private static SKRect OvalRect(double cx, double cy, double r)
        => new((float)(cx - r), (float)(cy - r), (float)(cx + r), (float)(cy + r));

    // ---------- 文本 ----------

    private void AddTextEntity(string? raw, XYZ pos, double height, double rotationRad, Entity e, in SKMatrix m)
    {
        if (string.IsNullOrWhiteSpace(raw) || height <= 0) { Skipped++; return; }
        var p = TransformPoint(m, new SKPoint((float)pos.X, (float)pos.Y));
        float det = m.ScaleX * m.ScaleY - m.SkewX * m.SkewY;
        float scale = MathF.Sqrt(MathF.Abs(det));
        if (float.IsNaN(scale) || scale <= 0) scale = 1f;
        float rot = MathF.Atan2(m.SkewY, m.ScaleX);
        float worldHeight = (float)height * scale;
        float rotTotal = rot + (float)rotationRad;
        float halfW = MathF.Max(worldHeight, raw.Length * worldHeight * 0.7f);
        var bounds = SKRect.Create(p.X - halfW, p.Y - worldHeight, halfW * 2, worldHeight * 2.5f);
        _entities.Add(new CadEntity
        {
            Layer = LayerName(e),
            ColorArgb = ResolveColor(e),
            Text = new TextDraw { Text = raw, Position = p, Height = worldHeight, RotationRadians = rotTotal },
            Bounds = bounds,
        });
    }

    public static string CleanMText(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        s = s.Replace("\\P", "\n").Replace("\\~", " ");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\{([^{}]*)\}", "$1");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\\[A-Za-z][^;\\]*;?", "");
        return s.TrimEnd('\0');
    }

    // ---------- 颜色 / 通用 ----------

    private void CacheLayerColors(CadDocument doc)
    {
        foreach (var layer in doc.Layers)
        {
            var c = layer.Color;
            _layerColors[layer.Name] = ToArgb(c, 7);
        }
    }

    private static uint ToArgb(Color c, int fallbackIndex)
    {
        if (c.IsTrueColor)
            return 0xFF000000u | ((uint)c.TrueColor & 0xFFFFFF);
        return AciColorTable.FromAci(c.Index != 0 ? c.Index : fallbackIndex);
    }

    private static string LayerName(Entity e) => e.Layer?.Name ?? "0";

    private uint ResolveColor(Entity e)
    {
        var c = e.Color;
        if (c.IsByLayer)
        {
            var name = LayerName(e);
            return _layerColors.TryGetValue(name, out var v) ? v : AciColorTable.FromAci(7);
        }
        if (c.IsByBlock) return AciColorTable.FromAci(0);
        return ToArgb(c, 7);
    }

    private void AddStroke(SKPath path, Entity e, in SKMatrix m, float lineWidth = 0)
    {
        path.Transform(m);
        var bounds = path.Bounds;
        if (float.IsNaN(bounds.Left)) { path.Dispose(); Skipped++; return; }
        _entities.Add(new CadEntity
        {
            Layer = LayerName(e),
            ColorArgb = ResolveColor(e),
            Path = path,
            LineWidth = lineWidth,
            Bounds = bounds,
        });
    }

    private void AddFill(SKPath path, Entity e, in SKMatrix m)
    {
        path.Transform(m);
        var bounds = path.Bounds;
        _entities.Add(new CadEntity
        {
            Layer = LayerName(e),
            ColorArgb = ResolveColor(e),
            FillPath = path,
            Bounds = bounds,
        });
    }

    private static SKPoint TransformPoint(in SKMatrix m, SKPoint p)
        => new(p.X * m.ScaleX + p.Y * m.SkewX + m.TransX,
               p.X * m.SkewY + p.Y * m.ScaleY + m.TransY);

    // 矩阵组合（行向量约定，a 先 b 后）
    internal static SKMatrix Mul(in SKMatrix a, in SKMatrix b) => new(
        a.ScaleX * b.ScaleX + a.SkewY * b.SkewX,
        a.SkewX * b.ScaleX + a.ScaleY * b.SkewX,
        a.TransX * b.ScaleX + a.TransY * b.SkewX + b.TransX,
        a.ScaleX * b.SkewY + a.SkewY * b.ScaleY,
        a.SkewX * b.SkewY + a.ScaleY * b.ScaleY,
        a.TransX * b.SkewY + a.TransY * b.ScaleY + b.TransY,
        0, 0, 1);

    internal static SKMatrix ScaleM(float sx, float sy) => new(sx, 0, 0, 0, sy, 0, 0, 0, 1);
    internal static SKMatrix RotM(float rad)
    {
        float c = MathF.Cos(rad), s = MathF.Sin(rad);
        return new SKMatrix(c, -s, 0, s, c, 0, 0, 0, 1);
    }
    internal static SKMatrix TransM(float tx, float ty) => new(1, 0, tx, 0, 1, ty, 0, 0, 1);
}
