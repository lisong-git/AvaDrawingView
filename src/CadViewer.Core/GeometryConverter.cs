using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using SkiaSharp;

namespace CadViewer.Core;

public sealed class GeometryConvertOptions
{
    public bool SkipRootAnnotations { get; init; }
}

/// <summary>
/// 几何转换器：ACadSharp 实体 → 显示基元（SKPath / 文本）。
/// 单个实体转换失败时跳过并计数，不影响整图。
/// 圆/弧用圆锥曲线构造（分辨率无关），椭圆与样条为压平折线。
/// </summary>
public sealed class GeometryConverter
{
    /// <summary>块引用递归深度上限（防止损坏文件造成无限递归）。取值与标注树共用（CadTransform）。</summary>
    private const int MaxInsertDepth = CadTransform.MaxInsertDepth;

    /// <summary>MINSERT 阵列实例总数上限（防止异常文件生成天量几何）。与标注树共用。</summary>
    private const int MaxMInsertInstances = CadTransform.MaxMInsertInstances;

    /// <summary>每处理这么多实体检查一次取消请求（大图取消要能及时响应）。</summary>
    private const int CancellationCheckInterval = 1024;

    private readonly List<CadEntity> _entities = new();
    private readonly Dictionary<string, uint> _layerColors = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _layerOrder = new();
    private readonly HashSet<string> _initiallyHidden = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>当前正在转换的实体句柄（块引用展开的子实体继承块引用句柄）。</summary>
    private ulong _currentHandle;

    /// <summary>
    /// 当前正在转换的实体**自身**句柄（不被块引用覆盖）。
    /// 与 <see cref="_currentHandle"/> 配对写进 CadEntity，用于还原块内子实体身份。
    /// </summary>
    private ulong _currentOwnHandle;

    /// <summary>转换失败 / 有意跳过的实体数（含隐藏实体）。</summary>
    public int Skipped { get; private set; }

    /// <summary>图纸里被关闭或冻结的图层：打开时应默认隐藏。</summary>
    public IReadOnlyCollection<string> InitiallyHiddenLayers => _initiallyHidden;

    public List<CadEntity> Convert(CadDocument doc, CancellationToken ct = default, GeometryConvertOptions? options = null)
    {
        _entities.Clear();
        _layerColors.Clear();
        _layerOrder.Clear();
        _initiallyHidden.Clear();
        Skipped = 0;

        CacheLayerColors(doc);

        int n = 0;
        foreach (var e in EnumerateModelSpace(doc))
        {
            // 大图解析后仍有十几万实体要转换，必须让取消能真正中断（原来只在阶段之间检查）
            if ((++n % CancellationCheckInterval) == 0) ct.ThrowIfCancellationRequested();
            if (options?.SkipRootAnnotations == true && IsRootAnnotation(e)) { Skipped++; continue; }
            ConvertEntity(e, SKMatrix.Identity, 0, inheritedColor: null);
        }

        DropUnreferencedPlaceholderLayers();

        return _entities;
    }

    /// <summary>本图出现的图层名（保持图纸中的出现顺序）。</summary>
    public IReadOnlyList<string> LayerNames => _layerOrder.ToArray();

    // ---------- 入口 ----------

    private static IEnumerable<Entity> EnumerateModelSpace(CadDocument doc)
    {
        var ms = doc.ModelSpace;
        if (ms?.Entities != null)
            foreach (var e in ms.Entities)
                yield return e;
    }

    private static bool IsRootAnnotation(Entity e)
        => e is Dimension || e is Leader || e is TextEntity || e is MText;

    /// <param name="inheritedColor">
    /// 块内实体的 BYBLOCK 继承色：由外层 INSERT 解析后的颜色传入。
    /// 为 null 表示当前不在块内（此时 BYBLOCK 无意义，退化为白色，与 AutoCAD 一致）。
    /// </param>
    /// <param name="handleOverride">
    /// 句柄覆盖：块引用展开时传入块引用句柄，使点选块内任一图元都能定位/标注到块引用整体
    /// （与 WPF 版按 BlockReference 建标注树的语义一致）。嵌套块保持最外层句柄。
    /// </param>
    private void ConvertEntity(Entity e, in SKMatrix parent, int depth, uint? inheritedColor, ulong? handleOverride = null)
    {
        // 实体被显式隐藏（DXF 组码 60 = 1，AutoCAD 亦不显示）。
        // 实测测试图.dwg：7836 个源实体带此标记（其中 1734 个 INSERT），展开约 5 万个显示实体；
        // 过滤后全图墨迹覆盖率 3.06% → 2.03%，经与 AutoCAD 语义核对属于正确行为而非内容丢失。
        if (e.IsInvisible) { Skipped++; return; }

        var prevHandle = _currentHandle;
        var prevOwnHandle = _currentOwnHandle;
        _currentHandle = handleOverride ?? e.Handle;
        _currentOwnHandle = e.Handle;

        try
        {
            uint color = ResolveColor(e, inheritedColor);
            switch (e)
            {
                case Line l: AddLine(l, parent, color); break;
                case Arc a: AddArc(a, parent, color); break;
                case Circle c: AddCircle(c, parent, color); break;
                case Ellipse el: AddEllipse(el, parent, color); break;
                case LwPolyline lw: AddLwPolyline(lw, parent, color); break;
                case Polyline2D p2: AddPolyline2D(p2, parent, color); break;
                case Spline sp: AddSpline(sp, parent, color); break;
                case MText mt:
                {
                    // MText 与 TextEntity 是兄弟类型（is TextEntity 匹配不到 MText，2026-09-25 SmokeTest 实证）
                    var raw = !string.IsNullOrEmpty(mt.PlainText) ? mt.PlainText : CleanMText(mt.Value);
                    AddTextEntity(raw, mt.InsertPoint, mt.Height, mt.Rotation, mt, parent, color);
                    break;
                }
                case TextEntity t: AddTextEntity(t.Value, t.InsertPoint, t.Height, t.Rotation, t, parent, color); break;
                case Insert ins: ConvertInsert(ins, parent, depth, inheritedColor); break;
                case Hatch h: AddHatch(h, parent, color); break;
                case Solid sol: AddSolid(sol, parent, color); break;
                case Leader ld: AddLeader(ld, parent, color); break;
                case Dimension dim: AddDimensionBlock(dim, parent, depth, inheritedColor); break;
                case ACadSharp.Entities.Point pt: AddPointCross(pt, parent, color); break;
                default: Skipped++; break;
            }
        }
        catch
        {
            Skipped++;
        }
        finally
        {
            _currentHandle = prevHandle;
            _currentOwnHandle = prevOwnHandle;
        }
    }

    // ---------- 基础实体 ----------

    private void AddLine(Line l, in SKMatrix m, uint color)
    {
        var path = new SKPath();
        path.MoveTo((float)l.StartPoint.X, (float)l.StartPoint.Y);
        path.LineTo((float)l.EndPoint.X, (float)l.EndPoint.Y);
        AddStroke(path, l, color, m);
    }

    private void AddCircle(Circle c, in SKMatrix m, uint color)
    {
        var path = new SKPath();
        path.AddCircle((float)c.Center.X, (float)c.Center.Y, (float)c.Radius, SKPathDirection.Clockwise);
        AddStroke(path, c, color, m);
    }

    private void AddArc(Arc a, in SKMatrix m, uint color)
    {
        var path = new SKPath();
        var oval = CadGeometry.OvalRect(a.Center.X, a.Center.Y, a.Radius);
        CadGeometry.AppendArcTo(path, oval, a.StartAngle, CadGeometry.ArcSweep(a.StartAngle, a.EndAngle));
        AddStroke(path, a, color, m);
    }

    private void AddEllipse(Ellipse el, in SKMatrix m, uint color)
    {
        // p(t) = center + A·cos(t) + B·sin(t)，A = 长轴端点向量，B = 左法线 × 半径比
        var path = new SKPath();
        CadGeometry.AppendEllipticalArc(path, el.Center.X, el.Center.Y,
            el.MajorAxisEndPoint.X, el.MajorAxisEndPoint.Y,
            -el.MajorAxisEndPoint.Y * el.RadiusRatio,
            el.MajorAxisEndPoint.X * el.RadiusRatio,
            el.StartParameter, el.EndParameter, 128);
        AddStroke(path, el, color, m);
    }

    private void AddLwPolyline(LwPolyline lw, in SKMatrix m, uint color)
    {
        if (lw.Vertices == null || lw.Vertices.Count == 0) { Skipped++; return; }
        var path = new SKPath();
        CadGeometry.AppendLwPolyline(path, lw);
        float lineWidth = lw.ConstantWidth > 0 ? (float)lw.ConstantWidth : 0f;
        AddStroke(path, lw, color, m, lineWidth);
    }

    private void AddPolyline2D(Polyline2D p2, in SKMatrix m, uint color)
    {
        if (p2.Vertices == null || p2.Vertices.Count == 0) { Skipped++; return; }
        var path = new SKPath();
        CadGeometry.AppendPolyline2D(path, p2);
        AddStroke(path, p2, color, m);
    }

    private void AddSpline(Spline sp, in SKMatrix m, uint color)
    {
        if (sp.ControlPoints == null || sp.ControlPoints.Count < 2) { Skipped++; return; }
        var cx = sp.ControlPoints.Select(p => p.X).ToArray();
        var cy = sp.ControlPoints.Select(p => p.Y).ToArray();
        var knots = sp.Knots?.ToArray() ?? Array.Empty<double>();
        var path = SplineFlattener.Flatten(cx, cy, knots, sp.Degree);
        if (path is null) { Skipped++; return; }
        AddStroke(path, sp, color, m);
    }

    private void AddLeader(Leader ld, in SKMatrix m, uint color)
    {
        if (ld.Vertices == null || ld.Vertices.Count == 0) { Skipped++; return; }
        var path = new SKPath();
        bool first = true;
        foreach (var v in ld.Vertices)
        {
            if (first) { path.MoveTo((float)v.X, (float)v.Y); first = false; }
            else path.LineTo((float)v.X, (float)v.Y);
        }
        AddStroke(path, ld, color, m);
    }

    private void AddPointCross(ACadSharp.Entities.Point pt, in SKMatrix m, uint color)
    {
        const float s = 1.0f;
        var path = new SKPath();
        path.MoveTo((float)pt.Location.X - s, (float)pt.Location.Y);
        path.LineTo((float)pt.Location.X + s, (float)pt.Location.Y);
        path.MoveTo((float)pt.Location.X, (float)pt.Location.Y - s);
        path.LineTo((float)pt.Location.X, (float)pt.Location.Y + s);
        AddStroke(path, pt, color, m);
    }

    private void AddSolid(Solid sol, in SKMatrix m, uint color)
    {
        var path = new SKPath();
        path.MoveTo((float)sol.FirstCorner.X, (float)sol.FirstCorner.Y);
        path.LineTo((float)sol.SecondCorner.X, (float)sol.SecondCorner.Y);
        path.LineTo((float)sol.FourthCorner.X, (float)sol.FourthCorner.Y);
        path.LineTo((float)sol.ThirdCorner.X, (float)sol.ThirdCorner.Y);
        path.Close();
        AddFill(path, sol, color, m);
    }

    // ---------- 组合实体 ----------

    /// <summary>
    /// 块引用。支持 MINSERT 阵列（RowCount/ColumnCount &gt; 1）。
    ///
    /// <para>BYBLOCK 颜色继承：块内实体的颜色若为 BYBLOCK，应取"插入所在的实体颜色"，
    /// 即 INSERT 自身的解析颜色。原实现直接把 BYBLOCK 当白色，导致块内线条在白底上被
    /// 翻成黑色（本应跟随插入颜色）。</para>
    /// </summary>
    private void ConvertInsert(Insert ins, in SKMatrix parent, int depth, uint? inheritedColor)
    {
        if (depth >= MaxInsertDepth || ins.Block is null) { Skipped++; return; }
        // 变换链与标注目标树共用（CadTransform），保证「高亮几何」与「显示几何」不会漂移
        if (!CadTransform.TryInsertMatrix(ins, out var m)) return;

        // 子实体继承色：INSERT 自身的颜色（BYLAYER 时已解析到图层色）
        uint blockColor = ResolveColor(ins, inheritedColor);

        CadTransform.ArraySize(ins, out int rows, out int cols, out long total);
        if (total > MaxMInsertInstances)
        {
            // 异常阵列直接放弃整个插入，避免生成天量几何拖垮渲染
            Skipped++;
            return;
        }

        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                // 阵列偏移在父坐标系内生效（与 insert 点同层），故夹在块变换与父变换之间
                var mm = CadTransform.Mul(m, CadTransform.ArrayOffset(ins, r, c));
                var totalM = CadTransform.Mul(mm, parent);
                foreach (var child in ins.Block.Entities)
                    ConvertEntity(child, totalM, depth + 1, blockColor, handleOverride: _currentHandle);
            }
        }
    }

    private void AddDimensionBlock(Dimension dim, in SKMatrix parent, int depth, uint? inheritedColor)
    {
        if (depth >= MaxInsertDepth) { Skipped++; return; }
        var block = dim.Block;
        if (block is null) { Skipped++; return; }
        uint color = ResolveColor(dim, inheritedColor);
        foreach (var child in block.Entities)
            ConvertEntity(child, parent, depth + 1, color, handleOverride: _currentHandle);
    }

    private void AddHatch(Hatch h, in SKMatrix m, uint color)
    {
        if (h.Paths == null) { Skipped++; return; }
        foreach (var bp in h.Paths)
        {
            if (bp?.Edges == null || bp.Edges.Count == 0) continue;
            var path = new SKPath();
            // 边界边（Line/Arc/Ellipse/Polyline）→ 路径，实现与标注分解共用（CadGeometry）
            CadGeometry.AppendHatchLoop(path, bp);
            if (h.IsSolid)
            {
                // 实心填充用 Fill 而非 Stroke（原实现把实心 HATCH 画成了一圈轮廓线）
                path.Close();
                AddFill(path, h, color, m);
            }
            else if (AddHatchPatternLines(path, bp, h, color, m))
            {
                // 图案已生成；边界不描边（AutoCAD/ODA 语义：非实心填充只显示图案线）
                path.Dispose();
            }
            else
            {
                AddStroke(path, h, color, m);
            }
        }
    }

    private const int MaxHatchPatternRows = 4096;
    private const int MaxHatchPatternSegments = 200_000;

    // 诊断计数（RenderShot 排查用）
    public static int DiagHatchPatternOk;
    public static int DiagHatchPatternFallback;
    public static int DiagHatchOddRows;
    public static string DiagHatchLastReason = "";
    // 临时诊断：最后一次图案生成的边界线段与图案路径（RenderShot/HatchProbe 排查用）
    public static System.Collections.Generic.List<(SKPoint a, SKPoint b)> DiagHatchBoundarySegs;
    public static SKPath DiagHatchPatternPath;

    /// <summary>
    /// 非实心 HATCH → 图案线（经典 hatch 线族算法）。
    /// 对图案定义的每条线（角度/基点/行步距/虚线表）生成平行线族：
    /// 行号由边界包围盒在行法线上的投影区间反推（图案基点可能是远处绝对坐标，
    /// 不能从 0 起步遍历）；行线与边界压平线段求交，按奇偶规则取内部区间，
    /// 再按虚线表切段（相位锚定图案基点）。全部坐标在实体空间生成，经 AddStroke
    /// 统一施加 OCS 镜像与父矩阵，与边界路径同一变换链。
    /// 返回 false = 无法生成（渐变填充 / 无图案定义 / 行数或线段数超限），调用方回退边界轮廓。
    /// </summary>
    private bool AddHatchPatternLines(SKPath boundary, Hatch.BoundaryPath bp, Hatch h, uint color, in SKMatrix m)
    {
        var defs = h.Pattern?.Lines;
        if (defs == null || defs.Count == 0) { DiagHatchPatternFallback++; DiagHatchLastReason = "no-defs"; return false; }

        // 首选闭合点列（顶点精确共享 → 行交点奇偶守恒）；
        // 含 Spline 等不支持边时回退 SKPath 压平（奇偶可能被弧端点浮点差破坏）。
        var loop = CadGeometry.FlattenHatchLoopPoints(bp);
        var segs = loop != null
            ? loop.Zip(loop.Skip(1).Append(loop[0]), (a, b) => (a, b)).Take(loop.Count)
                  .Where(s => s.a != s.b).ToList()
            : FlattenForHatch(boundary);
        if (segs.Count == 0) { DiagHatchPatternFallback++; DiagHatchLastReason = "no-segs"; return false; }

        var bbox = boundary.Bounds;
        float pad = MathF.Max(bbox.Width, bbox.Height) * 0.05f + 1f;
        float minX = bbox.Left - pad, maxX = bbox.Right + pad;
        float minY = bbox.Bottom - pad, maxY = bbox.Top + pad;

        var pattern = new SKPath();
        var hits = new List<float>();
        int emitted = 0;
        bool overflow = false;
        try
        {
            foreach (var def in defs)
            {
                double ang = def.Angle + h.PatternAngle;
                float dx = (float)Math.Cos(ang), dy = (float)Math.Sin(ang);
                float nx = -dy, ny = dx;
                float bx = (float)def.BasePoint.X, by = (float)def.BasePoint.Y;
                float ox = (float)def.Offset.X, oy = (float)def.Offset.Y;
                float offN = ox * nx + oy * ny;
                if (MathF.Abs(offN) < 1e-9f) continue;

                float cN0 = minX * nx + minY * ny, cN1 = maxX * nx + minY * ny;
                float cN2 = minX * nx + maxY * ny, cN3 = maxX * nx + maxY * ny;
                float nMin = MathF.Min(MathF.Min(cN0, cN1), MathF.Min(cN2, cN3));
                float nMax = MathF.Max(MathF.Max(cN0, cN1), MathF.Max(cN2, cN3));
                float bN = bx * nx + by * ny;
                int i0 = (int)Math.Floor((nMin - bN) / offN) - 1;
                int i1 = (int)Math.Ceiling((nMax - bN) / offN) + 1;
                if (i0 > i1) (i0, i1) = (i1, i0);   // offN 为负时区间端点颠倒
                if (i1 - i0 > MaxHatchPatternRows) { overflow = true; break; }

                var dashes = def.DashLengths;
                bool dashed = dashes != null && dashes.Count > 0;
                float cycle = 0f;
                if (dashed)
                    foreach (var d in dashes) cycle += MathF.Abs((float)d);

                for (int i = i0; i <= i1; i++)
                {
                    float rX = bx + ox * i, rY = by + oy * i;
                    float rowShiftD = ox * i * dx + oy * i * dy;

                    hits.Clear();
                    foreach (var (a, b) in segs)
                    {
                        // 符号距离穿越法：交点由两端点在行法线上的符号距离线性插值，
                        // 不解方程组 → 行线与边共线/近共线（det≈0）时无假交点。
                        // d=0 归正侧（半开区间），顶点穿越/擦过的奇偶计数自洽。
                        float da = (a.X - rX) * nx + (a.Y - rY) * ny;
                        float db = (b.X - rX) * nx + (b.Y - rY) * ny;
                        bool aneg = da < 0f, bneg = db < 0f;
                        if (aneg == bneg) continue;
                        float tt = da / (da - db);
                        float px = a.X + (b.X - a.X) * tt - rX;
                        float py = a.Y + (b.Y - a.Y) * tt - rY;
                        hits.Add(rowShiftD + px * dx + py * dy);
                    }
                    if (hits.Count < 2) continue;
                    hits.Sort();
                    if (hits.Count % 2 != 0)
                    {
                        DiagHatchOddRows++;
                        continue;
                    }

                    float eps = MathF.Max(bbox.Width, bbox.Height) * 1e-4f + 1e-5f;
                    for (int k = 0; k + 1 < hits.Count; k += 2)
                    {
                        // 相邻交点几乎重合 = 行线擦过顶点，奇偶不可靠，丢弃该对
                        if (hits[k + 1] - hits[k] < eps) continue;
                        emitted += EmitDashes(pattern, rX, rY, dx, dy, hits[k], hits[k + 1],
                            dashed, cycle, dashes, 1f);
                        if (emitted > MaxHatchPatternSegments) { overflow = true; break; }
                    }
                    if (overflow) break;
                }
                if (overflow) break;
            }
        }
        catch
        {
            pattern.Dispose();
            throw;
        }
        if (overflow || pattern.IsEmpty)
        {
            pattern.Dispose();
            DiagHatchPatternFallback++;
            DiagHatchLastReason = overflow ? "overflow" : "empty";
            return false;
        }
        DiagHatchPatternOk++;
        DiagHatchBoundarySegs = segs;
        DiagHatchPatternPath = new SKPath(pattern);
        AddStroke(pattern, h, color, m);
        return true;
    }

    /// <summary>区间 [s0,s1] 内按虚线表切段并写入 path，返回写入的段数（dashed=false 时整段直通）。</summary>
    private static int EmitDashes(SKPath path, float rX, float rY, float dx, float dy,
        float s0, float s1, bool dashed, float cycle, System.Collections.Generic.List<double> dashes, float scale)
    {
        int emitted = 0;
        void Emit(float a, float b)
        {
            path.MoveTo(rX + a * dx, rY + a * dy);
            path.LineTo(rX + b * dx, rY + b * dy);
            emitted++;
        }
        if (!dashed || cycle <= 0f)
        {
            Emit(s0, s1);
            return emitted;
        }
        int k0 = (int)MathF.Floor(s0 / cycle) - 1;
        int k1 = (int)MathF.Ceiling(s1 / cycle) + 1;
        for (int k = k0; k <= k1; k++)
        {
            float t = k * cycle;
            foreach (var d in dashes)
            {
                float len = MathF.Abs((float)(d * scale));
                if (d > 0 && len > 0)
                {
                    float a = MathF.Max(s0, t), b = MathF.Min(s1, t + len);
                    if (b > a) Emit(a, b);
                }
                t += len;
            }
        }
        return emitted;
    }

    /// <summary>路径 → 线段表（曲线按固定密度取样），供图案求交使用。</summary>
    private static List<(SKPoint a, SKPoint b)> FlattenForHatch(SKPath path)
    {
        var result = new List<(SKPoint, SKPoint)>();
        using var it = path.CreateRawIterator();
        var pts = new SKPoint[4];
        SKPoint current = default, start = default;
        SKPathVerb verb;
        while ((verb = it.Next(pts)) != SKPathVerb.Done)
        {
            switch (verb)
            {
                case SKPathVerb.Move:
                    current = start = pts[0];
                    break;
                case SKPathVerb.Line:
                    result.Add((current, pts[1]));
                    current = pts[1];
                    break;
                case SKPathVerb.Quad:
                    FlattenQuad(current, pts[1], pts[2], 12, result);
                    current = pts[2];
                    break;
                case SKPathVerb.Conic:
                    FlattenConic(current, pts[1], pts[2], it.ConicWeight(), 16, result);
                    current = pts[2];
                    break;
                case SKPathVerb.Cubic:
                    FlattenCubic(current, pts[1], pts[2], pts[3], 16, result);
                    current = pts[3];
                    break;
                case SKPathVerb.Close:
                    result.Add((current, start));
                    current = start;
                    break;
            }
        }
        return result;
    }

    private static void FlattenQuad(SKPoint p0, SKPoint p1, SKPoint p2, int n, List<(SKPoint, SKPoint)> outSegs)
    {
        var prev = p0;
        for (int j = 1; j <= n; j++)
        {
            float t = (float)j / n, mt = 1f - t;
            var p = new SKPoint(
                mt * mt * p0.X + 2 * mt * t * p1.X + t * t * p2.X,
                mt * mt * p0.Y + 2 * mt * t * p1.Y + t * t * p2.Y);
            outSegs.Add((prev, p));
            prev = p;
        }
    }

    private static void FlattenConic(SKPoint p0, SKPoint p1, SKPoint p2, float w, int n, List<(SKPoint, SKPoint)> outSegs)
    {
        var prev = p0;
        for (int j = 1; j <= n; j++)
        {
            float t = (float)j / n, mt = 1f - t;
            float u0 = mt * mt, u1 = 2 * mt * t * w, u2 = t * t;
            float sum = u0 + u1 + u2;
            var p = new SKPoint((u0 * p0.X + u1 * p1.X + u2 * p2.X) / sum, (u0 * p0.Y + u1 * p1.Y + u2 * p2.Y) / sum);
            outSegs.Add((prev, p));
            prev = p;
        }
    }

    private static void FlattenCubic(SKPoint p0, SKPoint p1, SKPoint p2, SKPoint p3, int n, List<(SKPoint, SKPoint)> outSegs)
    {
        var prev = p0;
        for (int j = 1; j <= n; j++)
        {
            float t = (float)j / n, mt = 1f - t;
            var p = new SKPoint(
                mt * mt * mt * p0.X + 3 * mt * mt * t * p1.X + 3 * mt * t * t * p2.X + t * t * t * p3.X,
                mt * mt * mt * p0.Y + 3 * mt * mt * t * p1.Y + 3 * mt * t * t * p2.Y + t * t * t * p3.Y);
            outSegs.Add((prev, p));
            prev = p;
        }
    }

    // ---------- 文本 ----------

    /// <summary>
    /// 文本实体 → <see cref="TextDraw"/>。
    ///
    /// 包围盒用 <see cref="TextMetrics"/> 的真实字体度量（原实现用
    /// <c>raw.Length * height * 0.7</c> 粗估，样例里把整图范围左边界从 0 撑到 -53）。
    /// 插入点按 CAD 惯例是基线左端，与渲染端 <c>DrawText(text, 0, 0)</c> 一致。
    /// </summary>
    private void AddTextEntity(string? raw, XYZ pos, double height, double rotationRad, Entity e, in SKMatrix m, uint color)
    {
        if (string.IsNullOrWhiteSpace(raw) || height <= 0) { Skipped++; return; }

        var p = TransformPoint(m, new SKPoint((float)pos.X, (float)pos.Y));
        float det = m.ScaleX * m.ScaleY - m.SkewX * m.SkewY;
        float scale = MathF.Sqrt(MathF.Abs(det));
        if (float.IsNaN(scale) || scale <= 0) scale = 1f;
        float rot = MathF.Atan2(m.SkewY, m.ScaleX);
        float worldHeight = (float)height * scale;
        float rotTotal = rot + (float)rotationRad;

        // 统一换行符，供测量与渲染共用（MTEXT 的 \P 已在 CleanMText 中转为 \n）
        var text = raw.Replace("\r\n", "\n").Replace('\r', '\n');

        var local = TextMetrics.MeasureLocalBounds(text, worldHeight);
        var bounds = TextMetrics.ToWorldBounds(local, p, rotTotal);

        _entities.Add(new CadEntity
        {
            Layer = LayerName(e),
            ColorArgb = color,
            Handle = _currentHandle,
            OwnHandle = _currentOwnHandle,
            Text = new TextDraw { Text = text, Position = p, Height = worldHeight, RotationRadians = rotTotal },
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
            var name = layer.Name ?? string.Empty;
            _layerColors[name] = ToArgb(layer.Color, 7);
            _layerOrder.Add(name);

            // 图层被关闭（IsOn=false）或冻结 → 打开图纸时默认隐藏
            if (!layer.IsOn || layer.Flags.HasFlag(LayerFlags.Frozen))
                _initiallyHidden.Add(name);
        }
    }

    /// <summary>
    /// 剔除"占位图层"。
    ///
    /// 实测：样例 DXF 的 LAYER 表只声明了 "0" 与 "红-测试" 两个图层，
    /// 但 ACadSharp 读出的 <c>doc.Layers</c> 有 3 项，多出一个名为 "unnamed0"、
    /// 无任何实体引用的条目（handle=5，flags=None，颜色=7）。
    /// 直接显示会在图层面板里凭空多出一行，故对"名字为 unnamed* 且无人引用"的条目不予采纳。
    /// 双重条件保证不会误删真实存在、只是恰好叫这名字的图层。
    /// </summary>
    private void DropUnreferencedPlaceholderLayers()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in _entities) used.Add(e.Layer);

        for (int i = _layerOrder.Count - 1; i >= 0; i--)
        {
            var name = _layerOrder[i];
            if (!IsPlaceholderName(name) || used.Contains(name)) continue;
            _layerOrder.RemoveAt(i);
            _layerColors.Remove(name);
            _initiallyHidden.Remove(name);
        }
    }

    private static bool IsPlaceholderName(string name)
        => string.IsNullOrWhiteSpace(name)
           || name.StartsWith("unnamed", StringComparison.OrdinalIgnoreCase);

    private static uint ToArgb(Color c, int fallbackIndex)
    {
        if (c.IsTrueColor)
            return 0xFF000000u | ((uint)c.TrueColor & 0xFFFFFF);
        return AciColorTable.FromAci(c.Index != 0 ? c.Index : fallbackIndex);
    }

    private static string LayerName(Entity e) => e.Layer?.Name ?? "0";

    /// <summary>
    /// 解析实体的显示颜色。
    /// BYBLOCK → 用块继承色（由外层 INSERT 传入），块外无继承色时退化为白色；
    /// BYLAYER → 查图层色表。
    /// </summary>
    private uint ResolveColor(Entity e, uint? inheritedColor)
    {
        var c = e.Color;
        if (c.IsByBlock)
            return inheritedColor ?? AciColorTable.FromAci(7);
        if (c.IsByLayer)
        {
            var name = LayerName(e);
            return _layerColors.TryGetValue(name, out var v) ? v : AciColorTable.FromAci(7);
        }
        return ToArgb(c, 7);
    }

    // 法线 (0,0,-1) 的实体：OCS→WCS 需绕 Y 轴镜像（x→-x）。
    // 不做这步，弧/圆会画到 X 镜像位置（实测 0053datr：41 条圆角弧全跑到 +900，
    // 轮廓每处圆角断开、Extents 被撑大近 20 倍）。2D 图纸法向只有 ±Z 两种。
    // Normal 不在 Entity 基类上（ACadSharp 3.4.9 各具体类自行声明），只能逐类型取。
    private static SKMatrix OcsMirrorMatrix(Entity e) => CadGeometry.OcsMirrorMatrix(e);

    private void AddStroke(SKPath path, Entity e, uint color, in SKMatrix m, float lineWidth = 0)
    {
        var ocs = OcsMirrorMatrix(e);
        if (!ocs.IsIdentity) path.Transform(ocs);
        path.Transform(m);
        var bounds = path.Bounds;
        if (float.IsNaN(bounds.Left)) { path.Dispose(); Skipped++; return; }
        _entities.Add(new CadEntity
        {
            Layer = LayerName(e),
            ColorArgb = color,
            Handle = _currentHandle,
            OwnHandle = _currentOwnHandle,
            Path = path,
            LineWidth = lineWidth,
            Bounds = bounds,
        });
    }

    private void AddFill(SKPath path, Entity e, uint color, in SKMatrix m)
    {
        var ocs = OcsMirrorMatrix(e);
        if (!ocs.IsIdentity) path.Transform(ocs);
        path.Transform(m);
        var bounds = path.Bounds;
        if (float.IsNaN(bounds.Left)) { path.Dispose(); Skipped++; return; }
        _entities.Add(new CadEntity
        {
            Layer = LayerName(e),
            ColorArgb = color,
            Handle = _currentHandle,
            OwnHandle = _currentOwnHandle,
            FillPath = path,
            Bounds = bounds,
        });
    }

    internal static SKPoint TransformPoint(in SKMatrix m, SKPoint p)
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
