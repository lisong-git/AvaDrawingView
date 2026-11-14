using SkiaSharp;

namespace DrawingView.Core;

/// <summary>
/// 从 REGION / 3DSOLID / BODY 的 ACIS 载荷里重建 2D 轮廓（P2-7）。
///
/// <para><b>问题</b>：ACadSharp 只把 ACIS 原样交出，R2013+ 的 DWG 里它是 SAB 二进制，
/// 于是这批实体在查看器里一条线都画不出来（实测 <c>3、幕墙横方总汇.dwg</c> 有 162 条 REGION）。</para>
///
/// <para><b>做法</b>：ACIS 的边界表示是一张图 ——
/// <c>body → lump → shell → face → loop → coedge(链) → edge → vertex → point</c>，
/// 而 edge 还引用一条曲线（<c>straight</c> 或 <c>ellipse</c>）。把 loop 里的 coedge 链走一遍，
/// 顺次取出每条边的起终点就得到闭合轮廓；曲线类型决定这一小段是直线还是椭圆弧。</para>
///
/// <para><b>只描边、不填充</b>：AutoCAD 在二维线框下把 REGION 显示为轮廓线，
/// 且 REGION 可能带内环（洞），描边天然正确，填充还要判奇偶——没必要。</para>
/// </summary>
/// <summary>环内一条 ACIS 边的几何（环坐标，未做 OCS / 块变换）。</summary>
/// <remarks>
/// 边没有 DWG 句柄，只能用 <c>ring:i/edge:j</c> 序号定位。序号 = 该环 coedge 链上的遍历位置，
/// 图纸不变时稳定；在 AutoCAD 里重存后 ACIS 记录顺序可能变化，序号会错位。
/// 调用方可拿 <see cref="Mid"/> / <see cref="Length"/> 配合 <see cref="RegionFrame"/>
/// 算几何指纹，用来在序号错位后重新定位。
/// </remarks>
public sealed class RegionEdgeOutline
{
    public int LoopIndex { get; init; }
    public int EdgeIndex { get; init; }
    public SKPoint Start { get; init; }
    public SKPoint End { get; init; }
    public bool IsArc { get; init; }

    /// <summary>这一条边的路径（椭圆弧按 5° 采样，因此可能含多段）。</summary>
    public SKPath Path { get; init; } = new();

    /// <summary>路径包围盒中心（直线边 = 端点中点）。</summary>
    public SKPoint Mid
    {
        get
        {
            var b = Path.Bounds;
            return new SKPoint(b.MidX, b.MidY);
        }
    }

    /// <summary>折线长度（椭圆弧为采样后的弧长）。</summary>
    public float Length
    {
        get
        {
            var pts = Path.Points;
            if (pts == null || pts.Length < 2) return 0;
            double sum = 0;
            for (int i = 1; i < pts.Length; i++)
            {
                double dx = pts[i].X - pts[i - 1].X;
                double dy = pts[i].Y - pts[i - 1].Y;
                sum += Math.Sqrt(dx * dx + dy * dy);
            }
            return (float)sum;
        }
    }
}

/// <summary>
/// 面域轮廓的归一化参照框（环坐标，已应用 body transform）。
/// 边/环的位置与尺寸除以 <see cref="Diagonal"/> 后，对平移与等比缩放不敏感 ——
/// 图纸整体移动、或重存时 ACIS 把 transform 烘进几何，指纹都不变。
/// </summary>
public readonly record struct RegionFrame(float CenterX, float CenterY, float Diagonal)
{
    public bool IsValid
        => float.IsFinite(CenterX) && float.IsFinite(CenterY)
           && float.IsFinite(Diagonal) && Diagonal > 1e-9f;

    /// <summary>把一个环坐标点换算成参照框内的归一化坐标。</summary>
    public SKPoint Normalize(SKPoint p)
        => IsValid ? new SKPoint((p.X - CenterX) / Diagonal, (p.Y - CenterY) / Diagonal) : p;

    /// <summary>长度换算成对角线的比例。</summary>
    public float NormalizeLength(float len) => IsValid ? len / Diagonal : len;
}

public static class RegionOutline
{
    /// <summary>椭圆弧每个采样段的角度跨度（弧度）。5° 在整图视角下远小于一个像素。</summary>
    private const double ArcStep = 5 * Math.PI / 180;

    /// <summary>
    /// 重建轮廓。返回 null = 无法解析（原因写 <paramref name="reason"/>，调用方据此计数）。
    /// <para>多个 loop（外环 + 内环）会返回多条路径，调用方逐条描边即可。</para>
    /// </summary>
    public static List<SKPath>? TryBuild(byte[]? acis, out string? reason)
        => TryBuild(acis, out reason, out _, out _);

    /// <summary>
    /// 同 <see cref="TryBuild(byte[]?, out string?)"/>，并额外输出每条边的拓扑：
    /// <c>edges[loopIndex][edgeIndex]</c>，与返回的环路径<b>严格同序</b>
    /// （同一次遍历产出，不会错位）。边级目标（<c>ring:i/edge:j</c>）与命中判定用这份数据。
    /// </summary>
    public static List<SKPath>? TryBuild(byte[]? acis, out string? reason,
                                         out List<List<RegionEdgeOutline>>? edges)
        => TryBuild(acis, out reason, out edges, out _);

    /// <summary>
    /// 再额外输出轮廓的归一化参照框（<see cref="RegionFrame"/>），供调用方计算几何指纹。
    /// </summary>
    public static List<SKPath>? TryBuild(byte[]? acis, out string? reason,
                                         out List<List<RegionEdgeOutline>>? edges,
                                         out RegionFrame frame)
    {
        reason = null;
        edges = null;
        frame = default;
        if (acis is null || acis.Length == 0) { reason = "无 ACIS 数据"; return null; }

        var recs = AcisSab.Parse(acis);
        if (recs is null) { reason = "SAB 解析失败"; return null; }

        var loops = new List<SKPath>();
        var allEdges = new List<List<RegionEdgeOutline>>();
        int broken = 0;
        var bodyTransform = BodyTransform(recs);

        for (int i = 0; i < recs.Count; i++)
        {
            if (recs[i].TypeName != "loop") continue;
            var loopEdges = new List<RegionEdgeOutline>();
            var path = WalkLoop(recs, i, loops.Count, loopEdges);
            if (path is null) broken++;
            else if (!path.IsEmpty)
            {
                if (bodyTransform is { } xf)
                {
                    path.Transform(xf);
                    foreach (var e in loopEdges) e.Path.Transform(xf);
                }
                loops.Add(path);
                allEdges.Add(loopEdges);
            }
        }

        if (loops.Count == 0)
        {
            reason = broken > 0 ? $"loop 链断裂（{broken} 个）" : "没有 loop 记录";
            return null;
        }

        var box = loops[0].Bounds;
        for (int i = 1; i < loops.Count; i++) box = SKRect.Union(box, loops[i].Bounds);
        double diag = Math.Sqrt((double)box.Width * box.Width + (double)box.Height * box.Height);
        frame = new RegionFrame(box.MidX, box.MidY, (float)diag);

        edges = allEdges;
        return loops;
    }

    /// <summary>
    /// 沿一条 loop 的 coedge 链走一圈，生成闭合路径；<paramref name="edges"/> 非空时
    /// 顺带把每条边单独记一份（环坐标），供边级目标与命中判定使用。
    /// </summary>
    private static SKPath? WalkLoop(List<AcisRecord> recs, int loopIdx, int loopIndex,
                                    List<RegionEdgeOutline> edges)
    {
        // loop 记录：id, attrib, ?, prev_loop, first_coedge, face
        if (!TryPtr(recs[loopIdx], 4, out int coedgeId)) return null;

        var path = new SKPath();
        var visited = new HashSet<int>();
        int start = coedgeId;
        int guard = 0;
        bool began = false;

        while (coedgeId >= 0 && visited.Add(coedgeId) && guard++ < 100_000)
        {
            if (coedgeId >= recs.Count) return null;
            var ce = recs[coedgeId];

            // coedge：id, attrib, ?, next, prev, partner, edge, sense, loop, ?
            if (!TryPtr(ce, 6, out int edgeId) || edgeId >= recs.Count) return null;
            bool coedgeForward = ce.Values.Count > 7 && ce.Values[7].Kind == AcisKind.Bool
                                 && ce.Values[7].I != 0;

            if (!TryResolveEdge(recs, edgeId, coedgeForward, out var shape)) return null;
            if (!began) { path.MoveTo(shape.From); began = true; }
            else path.LineTo(shape.From);          // 兜底：上一条边终点与这条起点不闭合时用直线接上
            AppendEdgeShape(shape, path);

            if (edges is not null)
            {
                var one = new SKPath();
                one.MoveTo(shape.From);
                AppendEdgeShape(shape, one);
                edges.Add(new RegionEdgeOutline
                {
                    LoopIndex = loopIndex,
                    EdgeIndex = edges.Count,
                    Start = shape.From,
                    End = shape.To,
                    IsArc = shape.IsArc,
                    Path = one,
                });
            }

            if (!TryPtr(ce, 3, out int nextId) || nextId == start) break;
            coedgeId = nextId;
        }

        if (!began) return null;
        path.Close();
        return path;
    }

    /// <summary>一条 edge 解析出的几何参数（尚未写入路径）。</summary>
    private readonly struct EdgeShape
    {
        public readonly SKPoint From;
        public readonly SKPoint To;
        public readonly double StartParam;
        public readonly double EndParam;
        public readonly AcisRecord Curve;
        public readonly bool IsArc;

        public EdgeShape(SKPoint from, SKPoint to, double sp, double ep,
                         AcisRecord curve, bool isArc)
        {
            From = from;
            To = to;
            StartParam = sp;
            EndParam = ep;
            Curve = curve;
            IsArc = isArc;
        }
    }

    /// <summary>把一条 edge 解析成几何参数（直线或椭圆弧），端点方向按 vertex 坐标校正。</summary>
    private static bool TryResolveEdge(List<AcisRecord> recs, int edgeId, bool coedgeForward,
                                       out EdgeShape shape)
    {
        shape = default;
        var e = recs[edgeId];
        // edge（ACIS 7.0 布局）：id, attrib, ?, start_vertex, start_param, end_vertex, end_param,
        //                        coedge, curve, sense, convexity
        if (!TryPtr(e, 3, out int svId) || !TryPtr(e, 5, out int evId)) return false;
        double sp = Num(e, 4), ep = Num(e, 6);
        int curveId = TryPtr(e, 8, out int c) ? c : -1;
        bool edgeSense = e.Values.Count > 9 && e.Values[9].Kind == AcisKind.Bool
                         && e.Values[9].I != 0;

        if (!TryVertexPoint(recs, svId, out var ps) || !TryVertexPoint(recs, evId, out var pe))
            return false;

        // coedge 的 sense 表示"与 edge 同向/反向"；edge 的 sense 表示"与底层曲线反向"。
        // 两者叠加后仍可能与实际顶点顺序不符（不同内核写法不一），故最终以**端点坐标**为准：
        // 曲线求值出的起点更靠近哪个 vertex，就从那个方向走。
        var from = coedgeForward ? ps : pe;
        var to = coedgeForward ? pe : ps;

        var curve = curveId >= 0 && curveId < recs.Count ? recs[curveId] : null;
        shape = new EdgeShape(from, to, sp, ep, curve, curve is { TypeName: "ellipse" });

        _ = edgeSense;
        return true;
    }

    /// <summary>把解析好的边几何追加到路径（调用方负责先 MoveTo 起点）。</summary>
    private static void AppendEdgeShape(in EdgeShape shape, SKPath path)
    {
        if (shape.Curve is not null && shape.Curve.TypeName == "ellipse")
            AppendEllipseArc(shape.Curve, shape.StartParam, shape.EndParam, shape.From, shape.To, path);
        else
            path.LineTo(shape.To);    // 直线（含 curve 缺失、curve 是 straight 的情况）
    }

    /// <summary>
    /// 椭圆弧：<c>ellipse</c> 记录给 center、normal、major_axis（长度 = 半长轴）、ratio，
    /// 参数即角度 ⇒ <c>p(t) = C + A·cos t + B·sin t</c>，<c>B = N × A · ratio</c>。
    /// 参数区间取 edge 的 start/end_param，方向按"起点更靠近哪个 vertex"取正或取反。
    /// </summary>
    private static void AppendEllipseArc(AcisRecord curve, double sp, double ep,
                                         SKPoint from, SKPoint to, SKPath path)
    {
        // ellipse：id, attrib, ?, center, normal, major_axis, ratio, ...
        if (!TryVec(curve, 3, out var center) || !TryVec(curve, 4, out var normal)
            || !TryVec(curve, 5, out var major))
        {
            path.LineTo(to);
            return;
        }
        double ratio = Num(curve, 6, 1);

        var n = Normalize(normal);
        var a = new Vec3(major.X, major.Y, major.Z);        // 半长轴向量（已含长度）
        var b = Cross(n, a) * ratio;                        // 半短轴向量

        if (Math.Abs(n.Z) < 0.05)                            // 平面近乎竖直 → 投影退化
        {
            path.LineTo(to);
            return;
        }

        double t0 = sp, t1 = ep;
        if (!(Math.Abs(t1 - t0) > 1e-9))
        {
            path.LineTo(to);                                 // 参数退化（极端情况）
            return;
        }

        // 方向校正：算出两端参数对应的点，选与 from 更近的那个作为起点
        var p0 = Eval(t0, center, a, b);
        var p1 = Eval(t1, center, a, b);
        double d00 = Dist2(p0, from), d01 = Dist2(p0, to);
        double d10 = Dist2(p1, from), d11 = Dist2(p1, to);
        bool startAtT0 = (d00 + d11) <= (d10 + d01);
        if (!startAtT0) { (t0, t1) = (t1, t0); }

        double sweep = t1 - t0;
        int steps = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) / ArcStep));
        for (int k = 1; k <= steps; k++)
        {
            var p = Eval(t0 + sweep * k / steps, center, a, b);
            path.LineTo((float)p.X, (float)p.Y);
        }
    }

    private static Vec3 Eval(double t, Vec3 c, Vec3 a, Vec3 b)
        => new(c.X + a.X * Math.Cos(t) + b.X * Math.Sin(t),
               c.Y + a.Y * Math.Cos(t) + b.Y * Math.Sin(t),
               c.Z + a.Z * Math.Cos(t) + b.Z * Math.Sin(t));

    /// <summary>body → transform：ACIS 把整体摆放放在 transform 记录里，坐标是列的三个轴向量 + 平移 + 缩放。</summary>
    private static SKMatrix? BodyTransform(List<AcisRecord> recs)
    {
        AcisRecord? body = null;
        foreach (var r in recs)
        {
            if (r.TypeName != "body") continue;
            if (body is not null) return null;
            body = r;
        }
        if (body is not { } b) return null;

        if (!TryPtr(b, 5, out int id) || id < 0 || id >= recs.Count) return null;
        var xf = recs[id];
        if (xf.TypeName != "transform") return null;
        if (!TryVec(xf, 2, out var a0) || !TryVec(xf, 3, out var a1)
            || !TryVec(xf, 5, out var t)) return null;

        double s = Num(xf, 6, 1);
        if (!double.IsFinite(s) || s <= 0) s = 1;
        return new SKMatrix(
            (float)(a0.X * s), (float)(a1.X * s), (float)t.X,
            (float)(a0.Y * s), (float)(a1.Y * s), (float)t.Y,
            0, 0, 1);
    }

    private readonly record struct Vec3(double X, double Y, double Z)
    {
        public static Vec3 operator *(Vec3 v, double k) => new(v.X * k, v.Y * k, v.Z * k);
        public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    }

    private static Vec3 Cross(Vec3 a, Vec3 b)
        => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    private static Vec3 Normalize(Vec3 v)
    {
        double len = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        return len > 1e-12 ? new Vec3(v.X / len, v.Y / len, v.Z / len) : new Vec3(0, 0, 1);
    }

    private static double Dist2(Vec3 p, SKPoint q)
        => (p.X - q.X) * (p.X - q.X) + (p.Y - q.Y) * (p.Y - q.Y);

    // ---------------------------------------------------------------- 记录取值辅助

    private static bool TryPtr(AcisRecord r, int idx, out int target)
    {
        target = -1;
        if (idx < 0 || idx >= r.Values.Count) return false;
        var v = r.Values[idx];
        if (v.Kind != AcisKind.Pointer || v.I < 0) return false;
        target = (int)v.I;
        return true;
    }

    private static double Num(AcisRecord r, int idx, double dflt = 0)
        => idx >= 0 && idx < r.Values.Count && r.Values[idx].Kind == AcisKind.Double
            ? r.Values[idx].D : dflt;

    private static bool TryVec(AcisRecord r, int idx, out Vec3 v)
    {
        v = default;
        if (idx < 0 || idx >= r.Values.Count) return false;
        var a = r.Values[idx];
        if (a.Kind != AcisKind.Vector) return false;
        v = new Vec3(a.X, a.Y, a.Z);
        return true;
    }

    /// <summary>vertex → point：vertex 记录里最后一个指针就是它所在的 point。</summary>
    private static bool TryVertexPoint(List<AcisRecord> recs, int vertexId, out SKPoint pt)
    {
        pt = default;
        if (vertexId < 0 || vertexId >= recs.Count) return false;
        var v = recs[vertexId];
        int pointId = -1;
        foreach (var a in v.Values)
            if (a.Kind == AcisKind.Pointer && a.I >= 0) pointId = (int)a.I;
        if (pointId < 0 || pointId >= recs.Count) return false;

        var p = recs[pointId];
        foreach (var a in p.Values)
        {
            if (a.Kind != AcisKind.Vector) continue;
            pt = new SKPoint((float)a.X, (float)a.Y);
            return true;
        }
        return false;
    }
}
