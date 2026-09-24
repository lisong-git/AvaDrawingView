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

    public List<CadEntity> Convert(CadDocument doc, CancellationToken ct = default)
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
                    // MText 继承自 TextEntity，case 必须排在它前面
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
            else
            {
                AddStroke(path, h, color, m);
            }
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

    private void AddStroke(SKPath path, Entity e, uint color, in SKMatrix m, float lineWidth = 0)
    {
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
