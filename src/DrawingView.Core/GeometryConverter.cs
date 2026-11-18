using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Objects;
using ACadSharp.Tables;
using CSMath;
using SkiaSharp;

namespace DrawingView.Core;

public sealed class GeometryConvertOptions
{
    public bool SkipRootAnnotations { get; init; }

    /// <summary>
    /// 要渲染的布局名（大小写不敏感）。null = 模型空间。
    /// 指定布局时按图纸空间渲染：先画布局自身几何（图框/标题栏），
    /// 再按每个视口的「视口中心/视高/扭转角」把模型空间几何变换到图纸坐标并裁剪到视口矩形。
    /// </summary>
    public string? LayoutName { get; init; }

    /// <summary>
    /// 模型空间为空时自动切到第一个有内容的布局。
    /// Inventor / SolidWorks 导出的机械图模型空间本来就是空的（几何全在布局的视图块里），
    /// 不开这个开关的表现是"整张白屏"，比"渲染了布局"更容易被误判成打不开。
    /// </summary>
    public bool AutoPaperSpaceWhenModelEmpty { get; init; } = true;

    /// <summary>
    /// 转换期按图层名跳过（大小写不敏感）。null / 空 = 不过滤。
    ///
    /// <para>与渲染期 <c>SetLayerHidden</c> 的区别：后者只对合批几何生效，
    /// <c>UnbatchedEntities</c>（文字、点标记、带裁剪图元）不受影响；诊断"多出来一块东西
    /// 到底是哪个图层画的"必须用这个开关。</para>
    /// </summary>
    public IReadOnlyCollection<string>? HideLayers { get; init; }

    /// <summary>
    /// 这些顶层实体的句柄，其"边界失效的空间过滤器"一律**整块丢弃**（覆盖默认的框内兜底）。
    /// 与 <c>DrawingView_CLIP_DROP_HANDLES</c> 叠加生效，用于已用 AutoCAD 实测确认过的图纸。
    /// </summary>
    public IReadOnlyCollection<ulong>? DropClipHandles { get; init; }
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
    private readonly Dictionary<string, int> _layerWeights = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _layerOrder = new();
    private readonly HashSet<string> _initiallyHidden = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>当前正在转换的实体句柄（块引用展开的子实体继承块引用句柄）。</summary>
    private ulong _currentHandle;

    /// <summary>
    /// 当前正在转换的实体**自身**句柄（不被块引用覆盖）。
    /// 与 <see cref="_currentHandle"/> 配对写进 CadEntity，用于还原块内子实体身份。
    /// </summary>
    private ulong _currentOwnHandle;

    /// <summary>当前正在展开的块实例 Id（<see cref="CadEntity.PendingInstance"/> 表示未指派）。</summary>
    private int _currentInstance = CadEntity.PendingInstance;

    /// <summary>本次转换产出的块实例表（Id 从 1 起）。</summary>
    private readonly List<InsertInstance> _instances = new();

    /// <summary>本次转换的块实例表；<see cref="Convert"/> 每次重建。</summary>
    public IReadOnlyList<InsertInstance> Instances => _instances;

    /// <summary>全局线型比例（LTSCALE，文件头）；<=0 时按 1。</summary>
    private double _globalLtScale = 1.0;

    /// <summary>
    /// POINT 的显示模式与尺寸（文件头变量 PDMODE / PDSIZE）。
    /// 二者都是**整张图**级别的设置，实体本身不带（DXF 里 POINT 没有自己的模式字段）。
    /// </summary>
    private int _pointDisplayMode;
    private float _pointDisplaySize;

    /// <summary>因 PDMODE=1 而画不出来的点数。</summary>
    public int PointMarksHidden { get; private set; }

    /// <summary>标记为"不打印"的图层数（DEFPOINTS 一类；默认隐藏，见 <see cref="CacheLayerColors"/>）。</summary>
    public int NonPlottingLayers { get; private set; }
    /// <summary>产出点标记的实体数。</summary>
    public int PointMarkEntities { get; private set; }

    /// <summary>成功重建出轮廓的 REGION 实体数。</summary>
    public int RegionEntities { get; private set; }

    /// <summary>REGION 重建出的环总数（外环 + 内环）。</summary>
    public int RegionLoops { get; private set; }

    /// <summary>ACIS 载荷无法重建轮廓、只能跳过的 REGION 数。</summary>
    public int RegionUnresolved { get; private set; }

    /// <summary>用 SHX 单线字体排出笔画的文本实体数（走描边，不走 TTF）。</summary>
    public int ShxTextEntities { get; private set; }

    /// <summary>样式引用 .shx、但本机找不到字库文件而回退 TTF 的文本实体数。</summary>
    public int ShxTextMissingFont { get; private set; }

    /// <summary>按竖排排版的文本实体数（诊断：图集里到底有没有竖排）。</summary>
    public int ShxVerticalTextEntities { get; private set; }

    /// <summary>样式名 → (字体文件, 大字体文件, 是否竖排)。SHX 排版要用它把文字映射到字库。</summary>
    private readonly Dictionary<string, (string Font, string Big, bool Vertical)> _styleFonts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 样式名 → 组码 41 的宽度因子。**只有 MTEXT 用它**：MTEXT 在 DXF 里没有实体级字宽字段，
    /// 没有内联 <c>\W</c> 时 AutoCAD/ODA 就是拿样式宽度作字宽（受控基准 <c>.compare/mtstyle.dxf</c>）。
    /// TEXT 反过来只认实体 41，见 <see cref="AddText"/> 的注释。
    /// </summary>
    private readonly Dictionary<string, float> _styleWidths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 字体使用统计：字体名（含来源标注）→ 用它画了多少条 TTF 文字。
    /// 用途是让 <c>DwgCapture --report</c> 能直接看出"这张图到底用了哪些字体、多少条是兜底"。
    /// SHX 走描边那条路，不计入这里。
    /// </summary>
    private readonly Dictionary<string, int> _fontUsage = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>本图用到的字体及其条数（诊断用）。</summary>
    public IReadOnlyDictionary<string, int> FontUsage => _fontUsage;
    /// <summary>文件头的 PDMODE（诊断用：0 点 / 1 不画 / 2 十字 / 3 叉 / 4 竖线，+32 圆 +64 方）。</summary>
    public int PointDisplayMode => _pointDisplayMode;
    /// <summary>文件头的 PDSIZE（诊断用：&gt;0 世界单位 / &lt;0 视高百分比 / =0 默认 5%）。</summary>
    public float PointDisplaySize => _pointDisplaySize;

    /// <summary>虚线间隔数组池：等值的线型共用同一实例，便于烘焙按引用分桶。</summary>
    private readonly Dictionary<string, float[]> _dashPool = new(StringComparer.Ordinal);

    /// <summary>转换失败 / 有意跳过的实体数（含隐藏实体）。</summary>
    public int Skipped { get; private set; }

    /// <summary>视口裁剪掉的实体数（布局渲染诊断用）。</summary>
    public int ClippedAway { get; private set; }

    /// <summary>
    /// 被**实体级空间过滤器**（<c>XDictionary: ACAD_FILTER → SPATIAL</c>，即 XCLIP / 局部打开
    /// 遗留的裁剪边界）整块裁掉的实体数。
    /// </summary>
    public int SpatialClipped { get; private set; }

    /// <summary>
    /// 是否按实体级空间过滤器裁剪。<c>DrawingView_NO_SPATIAL_CLIP=1</c> 可关闭，
    /// 用于与不认裁剪的第三方渲染器做 A/B 对照。
    /// </summary>
    private static readonly bool SpatialClipEnabled =
        Environment.GetEnvironmentVariable("DrawingView_NO_SPATIAL_CLIP") != "1";

    /// <summary>
    /// MTEXT 的 <c>\T</c>（字间距）是否折进字宽。默认折；<c>DrawingView_MTEXT_TRACKING=raw</c> 不折。
    ///
    /// <para>AutoCAD 的 <c>\T</c> 语义是"首个字之后每个字压缩"，不是"整串缩放"：
    /// 受控基准 `mt.dxf` 反解 ODA —— `\W0.8` 把宽度压到 0.80，`\T0.75` 只压到 **0.936**
    /// （2 个字只有 1 个间隙可压）；按同一模型，20 个字应压到 0.76。我们没有逐字排版通道，
    /// 只能整串缩，于是**短串偏压、长串刚好**。`(05)` 图纸主体 ±2px 命中率实测：
    /// 折 95.8% / 不折 94.0%（该图 \`\T\` 多出现在长串说明文字上）→ 默认折。</para>
    /// </summary>
    private static readonly bool s_mtextFoldTracking =
        Environment.GetEnvironmentVariable("DrawingView_MTEXT_TRACKING") != "raw";

    /// <summary>转换期跳过的图层名（大写）；null = 不过滤。<see cref="GeometryConvertOptions.HideLayers"/> 落在这里。</summary>
    private HashSet<string>? _hideLayers;

    /// <summary>被转换期图层过滤跳过的实体数（含块内展开的）。</summary>
    public int LayerFiltered { get; private set; }

    /// <summary>本轮生效的"整块丢弃"句柄集合（环境变量 ∪ <see cref="GeometryConvertOptions.DropClipHandles"/>）。</summary>
    private HashSet<ulong> _clipDropHandles = ClipDropHandles;

    /// <summary>
    /// 裁剪边界与本实体包围盒完全不相交时怎么处理（说明过滤器坐标系没读出来 —— ACadSharp 对
    /// 一部分旧格式过滤器取到的 `Normal` 不是单位向量、`InsertTransform` 恒为单位阵）。
    ///
    /// <list type="bullet">
    ///   <item><c>frame</c>（默认）：一律保留（退化为按图纸范围裁）—— 宁可多画；</item>
    ///   <item><c>drop</c>：一律整块丢弃；</item>
    ///   <item><c>auto</c>：按"实体有多大比例落在图纸范围内"决定
    ///   （<see cref="ClipUnresolvedMinCoverage"/>）。</item>
    /// </list>
    ///
    /// <para>为什么要分开留着：<c>9---陶板节点 (0).dwg</c> 有 12 个共用同一失效边界的 INSERT
    /// （详图零件，其中一块石材 bbox 8804×8189 只有 0.4% 落在图纸内），AutoCAD 照画；整块丢弃
    /// 会**丢掉两组零件**，`auto` 用覆盖率判也会把它们判掉。<c>9---陶板节点 (05).dwg</c> 的
    /// `H=4301 → *U33` 方向相反 —— 9 个成员（`00--填充` 5 段竖条 + `00--标注` 4 个文字）
    /// 大部分在图外，最下段探进图框上方，AutoCAD 与 ODA 该处均为空，保留兜底就会画出一道
    /// 多余的折角符号。两种情况覆盖率**反相关**（0.4% 要留、17.5% 要丢），说明真正的判据不在这里，
    /// 而在 DWG 里还没解析出来的 XCLIP 状态/边界换算 —— 故默认取保守的 <c>frame</c>。</para>
    /// </summary>
    private static readonly string ClipUnresolvedMode =
        (Environment.GetEnvironmentVariable("DrawingView_CLIP_UNRESOLVED") ?? "frame").Trim().ToLowerInvariant();

    /// <summary>
    /// 手工豁免：这些句柄（十进制，逗号分隔）的"无解裁剪"一律**整块丢弃**，
    /// 覆盖 <see cref="ClipUnresolvedMode"/> 的判断。环境变量
    /// <c>DrawingView_CLIP_DROP_HANDLES=4301,4302</c>。
    ///
    /// <para>为什么需要它：XCLIP 的启用状态在 ACadSharp 暴露的字段里读不出来（两边的
    /// `DisplayBoundary / ClipFrontPlane / ClipBackPlane / BoundaryPoints` 完全一致，XDATA 为空，
    /// 扩展字典都只有 `ACAD_FILTER/SPATIAL`），AutoCAD 对同一种数据却给出相反结果。
    /// 拿到实测结论的个别图纸用它兜住，比全局猜一个阈值安全。</para>
    /// </summary>
    private static readonly HashSet<ulong> ClipDropHandles = ParseHandleSet(
        Environment.GetEnvironmentVariable("DrawingView_CLIP_DROP_HANDLES"));

    private static HashSet<ulong> ParseHandleSet(string? raw)
    {
        var set = new HashSet<ulong>();
        if (string.IsNullOrWhiteSpace(raw)) return set;
        foreach (var part in raw.Split(',', ';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (ulong.TryParse(part, out var h)) set.Add(h);
        return set;
    }

    /// <summary>
    /// <see cref="ClipUnresolvedMode"/> 取 <c>auto</c> 时的判据：实体包围盒落在图纸范围内的
    /// **面积占比**门槛，低于它按"被 XCLIP 裁掉了"处理、整块丢弃。
    ///
    /// <para>实测覆盖率：`(05)` 的 `H=4301` 17.5%，`(0)` 的 12 个 INSERT 里最小的一块石材 0.4%。
    /// 需要丢的那个覆盖率反而更高 —— 判据反相关，`auto` 只能当诊断档用。
    /// 环境变量 <c>DrawingView_CLIP_MIN_COVER</c> 可改（0~1）。</para>
    /// </summary>
    private static readonly float ClipUnresolvedMinCoverage = ResolveMinCoverage();

    private static float ResolveMinCoverage()
    {
        var raw = Environment.GetEnvironmentVariable("DrawingView_CLIP_MIN_COVER");
        if (float.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= 0 && v <= 1)
            return v;
        return 0.3f;
    }

    /// <summary>
    /// 裁剪边界的坐标取法（P2-17 的标定开关，默认 <see cref="ClipXformMode.Raw"/>）：
    /// <list type="bullet">
    ///   <item><c>raw</c>：边界点当世界坐标直接用（实测 PE0101/PE0203 这类图纸就是这样）；</item>
    ///   <item><c>tf</c>：只过 <c>SpatialFilter.InsertTransform</c>；</item>
    ///   <item><c>full</c>：再过实体自身的插入矩阵（旧行为，THSYB1-11 曾按它标定）。</item>
    /// </list>
    /// 环境变量 <c>DrawingView_CLIP_XFORM=raw|tf|full</c>。
    /// </summary>
    private enum ClipXformMode { Auto, Raw, Tf, Full }

    /// <summary>法向不可信的过滤器是否退化为"按图纸范围裁"（默认开）。</summary>
    private static readonly bool BadNormalClipToSheet =
        Environment.GetEnvironmentVariable("DrawingView_CLIP_BAD_NORMAL") != "skip";

    /// <summary>
    /// <c>InsertTransform</c> 读成单位阵（= ACadSharp 没解出来）时，改用
    /// <c>InverseInsertTransform</c> 把边界点换算回块坐标 —— 实测这两个字段**只有一个是对的**。
    ///
    /// <para>实测 <c>9---陶板节点 (05).dwg</c> 的 <c>H=4301</c>：正向是单位阵，反向是
    /// <c>translate(-54114.93, +5341.49)</c>。边界点 <c>(54024.93,-5747.26)-(54599.11,-5067.42)</c>
    /// 过一遍反向矩阵落到块内的 <c>(-90.0,-405.8)-(484.2,274.1)</c>（x 正好整数 -90，不是巧合），
    /// 再加插入点得到世界矩形 <c>(422.7,432.4)-(996.9,1112.2)</c> —— 正好是图框内一个合理的
    /// XCLIP 矩形，且把图框上方那道多余折角（y≈1128）切在外面，与 AutoCAD/ODA 一致。
    /// 而不换算时边界落在 <c>(54025,-5747)</c>（离图形 5.3 万单位），只能落到"无解兜底"。</para>
    ///
    /// <para>默认**开**：两张实测图纸（`(05)` 与 `(0)`，共 24 个过滤器）都是"正向坏、反向好"，
    /// 换算后与 ODA 的匹配度同时上升：`(05)` 折角消失且不多丢内容（仅我方 5,860→5,238，
    /// 而整块丢弃会丢到缺失 11,613→13,732）；`(0)` 覆盖率 95.3%→97.7%、命中 92.6%→98.5%、
    /// 仅我方 13,547→2,627。换算出来落在离谱位置的那些（`(0)` 里有 (121756,…)/(174917,…)）
    /// 与实体不相交，自然退回 <see cref="ClipUnresolvedMode"/> 的兜底，不会误裁。
    /// 环境变量 <c>DrawingView_CLIP_INV_XFORM=0</c> 可关掉对照。</para>
    /// </summary>
    private static readonly bool ClipUseInverseXform =
        Environment.GetEnvironmentVariable("DrawingView_CLIP_INV_XFORM") != "0";

    /// <summary>
    /// 块内（嵌套块定义里的）XCLIP 是否也参与裁剪。默认**开**（2026-11-14 修）。
    ///
    /// <para>此前这里是硬跳过（<c>parent.IsIdentity</c>），因为"块内边界到底在哪个坐标系"
    /// 没标定出来，猜错会误藏正确内容。用 `52陶板节点` 的 `TK-A3-NT/qwe`（H=1498）标定后
    /// 换算链已经闭合（见 <see cref="TryGetSpatialClip"/> 的说明），故放开。</para>
    ///
    /// <para>环境变量 <c>DrawingView_CLIP_IN_BLOCK=0</c> 可回到"块内一律不裁"的旧行为做 A/B。</para>
    /// </summary>
    private static readonly bool ClipInBlock =
        Environment.GetEnvironmentVariable("DrawingView_CLIP_IN_BLOCK") != "0";

    /// <summary>打印每个空间过滤器的换算结果（诊断"这块为什么被裁/没裁"）。<c>DrawingView_CLIP_DEBUG=1</c>。</summary>
    private static readonly bool ClipDebug =
        Environment.GetEnvironmentVariable("DrawingView_CLIP_DEBUG") == "1";

    private static readonly ClipXformMode ClipXform = Enum.TryParse<ClipXformMode>(
        Environment.GetEnvironmentVariable("DrawingView_CLIP_XFORM"), ignoreCase: true, out var xf)
        ? xf
        : ClipXformMode.Auto;

    /// <summary>实际渲染的空间名（"Model" 或布局名）。</summary>
    public string ActiveSpace { get; private set; } = "Model";

    /// <summary>图纸里可切换的布局名（按 TabOrder 排序，不含模型空间）。</summary>
    public IReadOnlyList<string> LayoutNames { get; private set; } = Array.Empty<string>();

    /// <summary>视口内容的裁剪矩形（图纸坐标）；null = 不裁剪（模型空间或布局自身几何）。</summary>
    private SKRect? _clipRect;

    /// <summary>
    /// 当前生效的**空间过滤器边界多边形**（世界坐标，P2-17）。null = 不裁。
    /// 与 <see cref="_clipRect"/> 的区别：视口永远是矩形，而 XCLIP 的边界可以是任意多边形
    /// （实测 `THSYB1-12` 是 8 点台阶形，面积只有外接矩形的 74%）—— 按外接矩形裁会把
    /// 缺口里的内容画出来，与 AutoCAD 不一致。
    /// </summary>
    private SKPath? _spatialClipPath;

    private SKRect _spatialClipBounds;
    private bool _spatialClipIsRect;

    /// <summary>
    /// 视口矩形 ∩ 空间过滤器多边形 = 本层图元真正的裁剪区域（世界坐标，闭合）。
    /// 由 <see cref="RebuildScopeClip"/> 维护；null = 该层不裁。
    /// </summary>
    private SKPath? _scopeClip;
    private SKRect _scopeClipBounds;
    private bool _scopeClipIsRect;

    /// <summary>本图创建的全部裁剪路径（所有权转移给 DisplayList，见 <see cref="ClipPaths"/>）。</summary>
    private readonly List<SKPath> _clipPaths = new();

    /// <summary>本图用到的裁剪区域，交给 <c>DisplayList</c> 统一释放。</summary>
    public IReadOnlyList<SKPath> ClipPaths => _clipPaths;

    private readonly List<ClipFrame> _clipFrames = new();

    /// <summary>裁剪边界线（AutoCAD 的 XCLIP 边框）。只为**边界可信**的裁剪登记，见 TryGetSpatialClip。</summary>
    public IReadOnlyList<ClipFrame> ClipFrames => _clipFrames;

    /// <summary>
    /// 图纸头里的 <c>$XCLIPFRAME</c>（0 = 不显示，1 = 显示并打印，2 = 显示不打印）。
    /// **这是每张图自己的设定**，不是全局默认：同一批图纸里实测一半是 2、一半是 0，
    /// 所以不能像原来那样一律画出来。
    /// </summary>
    public int XClipFrameMode { get; private set; }

    /// <summary>
    /// 图纸范围（文件头 EXTMIN/EXTMAX）。只用于**边界与图形脱节**的兜底：
    /// 裁剪边界经两次变换后仍与实体不相交时，按这个范围裁 —— 与 AutoCAD 的实测表现一致
    /// （见 <see cref="TryGetSpatialClip"/> 的标定说明与 P2-13 条目）。
    /// </summary>
    private SKRect? _sheetRect;

    /// <summary>当前视口冻结的图层（AutoCAD：视口冻结只影响该视口）。</summary>
    private HashSet<string>? _clipFrozenLayers;

    /// <summary>
    /// 布局里的视口（先收集，最后统一按视口画模型内容）。
    /// 带上「视口所在坐标系 → 图纸」的矩阵：图框成块时视口藏在 INSERT 里，
    /// 只认顶层 Viewport 会漏掉绝大多数视口（实测一张 47 视口的图只认出 1 个）。
    /// </summary>
    private readonly List<(Viewport Vp, SKMatrix ToPaper)> _viewports = new();

    private readonly List<string> _spaceNotes = new();

    /// <summary>布局/视口诊断（仅在 DrawingView_CONV_TRACE=1 时填充）。</summary>
    public IReadOnlyList<string> SpaceNotes => _spaceNotes;

    // 块引用展开的丢弃原因计数（诊断用：定位「块内容整片消失」）
    public int DroppedNoBlock { get; private set; }
    public int DroppedByDepth { get; private set; }
    public int DroppedByMatrix { get; private set; }
    public int DroppedByMInsert { get; private set; }

    // 块属性（ATTRIB）计数（诊断用：区分「块里没文字」与「属性没画出来」）
    /// <summary>已绘制的属性值条数。</summary>
    public int AttributeDrawn { get; private set; }
    /// <summary>因不可见（实体 60 / 属性标志位 1）而跳过的属性条数。</summary>
    public int AttributeInvisible { get; private set; }
    /// <summary>块定义里的占位符（AttributeDefinition）条数——这些**不应该**出现在图面上。</summary>
    public int AttributeDefsSkipped { get; private set; }

    /// <summary>填充里被判定为非法（未闭合）而忽略的边界环数。</summary>
    public int HatchBadLoops { get; private set; }
    /// <summary>填充里完全重合、被去重掉的边界环数。</summary>
    public int HatchDuplicateLoops { get; private set; }

    /// <summary>
    /// 当前块引用上下文里的「随块（ByBlock）」线宽（1/100 mm，&gt;0 才算有值）。
    /// 块内实体写 BYBLOCK 时继承它，与 <c>_inheritedColor</c> 的语义完全对称。
    /// </summary>
    private int _inheritedLineWeight;

    /// <summary>解析出非零线宽的实体数（诊断用：判断线宽开关到底有没有东西可画）。</summary>
    public int LineWeightEntities { get; private set; }

    /// <summary>带宽度（几何宽度，不是线宽）的多段线实体数——这些画成填充轮廓。</summary>
    public int WidePolylineEntities { get; private set; }

    /// <summary>图纸里被关闭或冻结的图层：打开时应默认隐藏。</summary>
    public IReadOnlyCollection<string> InitiallyHiddenLayers => _initiallyHidden;

    private static readonly bool ConvTrace = Environment.GetEnvironmentVariable("DrawingView_CONV_TRACE") == "1";
    private static readonly bool ShowInvisible = Environment.GetEnvironmentVariable("DrawingView_SHOW_INVISIBLE") == "1";

    /// <summary>
    /// REGION 轮廓重建开关（默认开）。置 <c>DrawingView_NO_REGION=1</c> 可关掉，
    /// 用于「有/无 REGION」的像素对照 —— 判断渲染差异到底来自 REGION 还是别的东西。
    /// </summary>
    private static readonly bool ShowRegions = Environment.GetEnvironmentVariable("DrawingView_NO_REGION") != "1";

    /// <summary>
    /// 不打印图层（PlotFlag=false，如 DEFPOINTS）是否默认隐藏 —— 对齐 ODA/打印语义。
    /// 判据本身不可全信：ACadSharp 对部分外部导出图会把整表都读成不打印，
    /// 因此只在「不打印图层不超过全表 1/4」时才启用（见 <c>CacheLayerColors</c>）。
    /// 置 <c>DrawingView_SHOW_NONPLOT=1</c> 可完全关闭，用于对照实验。
    /// </summary>
    private static readonly bool HideNonPlotLayers = Environment.GetEnvironmentVariable("DrawingView_SHOW_NONPLOT") != "1";
    private static readonly string TracePath = Path.Combine(Path.GetTempPath(), "conv_trace.log");
    private static int _traceSeq;

    private static void Trace(string m)
    {
        if (!ConvTrace) return;
        try
        {
            File.AppendAllText(TracePath, $"[{_traceSeq++}] {DateTime.Now:HH:mm:ss.fff} {m}{Environment.NewLine}");
        }
        catch { }
    }

    public List<CadEntity> Convert(CadDocument doc, CancellationToken ct = default, GeometryConvertOptions? options = null)
    {
        _entities.Clear();
        _fontUsage.Clear();
        _layerColors.Clear();
        _layerOrder.Clear();
        _initiallyHidden.Clear();
        _viewports.Clear();
        _instances.Clear();
        _currentInstance = 0;
        _clipRect = null;
        _clipFrozenLayers = null;
        _spatialClipPath = null;
        _spatialClipBounds = default;
        _spatialClipIsRect = false;
        _scopeClip = null;
        _scopeClipBounds = default;
        _scopeClipIsRect = false;
        _clipPaths.Clear();   // 上一轮建的裁剪路径已随旧 DisplayList 释放
        _clipFrames.Clear();
        _sheetRect = CadDocumentLoader.HeaderModelSpaceRect(doc);
        // $XCLIPFRAME 是每张图自己的设定（0 不显示 / 1 显示并打印 / 2 显示不打印），
        // AutoCAD 只在 ≠ 0 时画裁剪边界：同一批图纸实测一半 2 一半 0，不能一律画。
        XClipFrameMode = (int)doc.Header.ExternalReferenceClippingBoundaryType;
        _hideLayers = options?.HideLayers is { Count: > 0 } hl
            ? new HashSet<string>(hl, StringComparer.OrdinalIgnoreCase)
            : null;
        _clipDropHandles = options?.DropClipHandles is { Count: > 0 } dh
            ? new HashSet<ulong>(ClipDropHandles) { }
            : new HashSet<ulong>(ClipDropHandles);
        if (options?.DropClipHandles != null)
            foreach (var h in options.DropClipHandles) _clipDropHandles.Add(h);
        LayerFiltered = 0;
        ClippedAway = 0;
        SpatialClipped = 0;
        Skipped = 0;
        AttributeDrawn = 0;
        AttributeInvisible = 0;
        AttributeDefsSkipped = 0;
        HatchBadLoops = 0;
        HatchDuplicateLoops = 0;
        LineWeightEntities = 0;
        WidePolylineEntities = 0;
        _inheritedLineWeight = 0;
        PointMarksHidden = 0;
        PointMarkEntities = 0;
        RationalSplineEntities = 0;
        NonPlottingLayers = 0;
        RegionEntities = 0;
        RegionLoops = 0;
        RegionUnresolved = 0;
        ShxTextEntities = 0;
        ShxTextMissingFont = 0;
        ShxVerticalTextEntities = 0;
        _pointDisplayMode = 0;
        _pointDisplaySize = 0f;

        CacheLayerColors(doc);
        CacheTextStyles(doc);
        try
        {
            double lts = doc.Header.LineTypeScale;
            if (lts > 0 && !double.IsNaN(lts) && !double.IsInfinity(lts)) _globalLtScale = lts;
        }
        catch { }
        try
        {
            // PDMODE / PDSIZE：不读的后果是"点全按同一个固定十字画"，
            // 缩放后要么看不见要么糊满屏（见 PointMarkStyle 的说明）。
            _pointDisplayMode = (int)doc.Header.PointDisplayMode;
            _pointDisplaySize = (float)doc.Header.PointDisplaySize;
            if (!float.IsFinite(_pointDisplaySize)) _pointDisplaySize = 0f;
        }
        catch { }

        if (ConvTrace)
        {
            try { File.Delete(TracePath); } catch { }
            _traceSeq = 0;
        }

        var layouts = EnumerateLayouts(doc);
        LayoutNames = layouts.Select(l => l.Name ?? string.Empty).Where(n => n.Length > 0).ToArray();

        var target = ResolveLayout(doc, layouts, options);
        if (target is not null)
        {
            ActiveSpace = target.Name ?? "Layout";
            ConvertLayout(doc, target, ct, options);
        }
        else
        {
            ActiveSpace = "Model";
            int n = 0;
            foreach (var e in EnumerateModelSpace(doc))
            {
                // 大图解析后仍有十几万实体要转换，必须让取消能真正中断（原来只在阶段之间检查）
                if ((++n % CancellationCheckInterval) == 0) ct.ThrowIfCancellationRequested();
                if (options?.SkipRootAnnotations == true && IsRootAnnotation(e)) { Skipped++; continue; }
                ConvertEntity(e, SKMatrix.Identity, 0, inheritedColor: null);
            }
        }

        DropUnreferencedPlaceholderLayers();

        // 未指派（异常路径或没有 ConvertEntity 包住的产出）统一归到模型空间，对外不出现负值
        for (int i = 0; i < _entities.Count; i++)
        {
            if (_entities[i].InstanceId < 0) _entities[i].InstanceId = 0;
        }

        return _entities;
    }

    /// <summary>本图出现的图层名（保持图纸中的出现顺序）。</summary>
    public IReadOnlyList<string> LayerNames => _layerOrder.ToArray();

    // ---------- 布局（图纸空间） ----------

    /// <summary>图纸里的布局（按 TabOrder 排序，排除模型空间布局）。</summary>
    private static List<Layout> EnumerateLayouts(CadDocument doc)
    {
        var result = new List<Layout>();
        try
        {
            foreach (var l in doc.Layouts)
            {
                if (l is null) continue;
                // 模型空间也是一个 Layout 对象（名字通常叫 "Model"），不要当成可切换的图纸
                if (!l.IsPaperSpace) continue;
                result.Add(l);
            }
        }
        catch { /* 个别文件布局表损坏：当作没有布局，退回模型空间 */ }
        return result.OrderBy(l => l.TabOrder).ToList();
    }

    /// <summary>
    /// 选目标空间：显式指定的布局 &gt; 模型空间（非空）&gt; 第一个有视口内容的布局。
    /// 最后这一条是"整张白屏"的解药：Inventor 导出的机械图模型空间 0 实体，
    /// 几何全在布局的视图块里，不自动切过去就什么都看不见。
    /// </summary>
    private static Layout? ResolveLayout(CadDocument doc, List<Layout> layouts, GeometryConvertOptions? options)
    {
        if (!string.IsNullOrWhiteSpace(options?.LayoutName))
        {
            var wanted = options!.LayoutName!;
            return layouts.FirstOrDefault(l => string.Equals(l.Name, wanted, StringComparison.OrdinalIgnoreCase));
        }

        if (options?.AutoPaperSpaceWhenModelEmpty == false) return null;

        bool modelEmpty;
        try { modelEmpty = doc.ModelSpace?.Entities is null || !doc.ModelSpace.Entities.Any(); }
        catch { modelEmpty = true; }
        if (!modelEmpty) return null;

        // 有视口（Id != 1 且尺寸有效）= 该布局确实"看得见东西"
        return layouts.FirstOrDefault(l =>
        {
            try
            {
                return l.Viewports.Any(v => v is not null && v.Id != 1 && v.Width > 1e-6 && v.Height > 1e-6);
            }
            catch { return false; }
        });
    }

    /// <summary>
    /// 图纸空间转换：先画布局自身几何（图框、标题栏、视图块引用），
    /// 再逐个视口把模型空间几何变换到图纸坐标并裁剪。
    /// </summary>
    private void ConvertLayout(CadDocument doc, Layout layout, CancellationToken ct, GeometryConvertOptions? options)
    {
        _viewports.Clear();
        _spaceNotes.Clear();

        var block = layout.AssociatedBlock;
        if (block?.Entities is not null)
        {
            int n = 0;
            foreach (var e in block.Entities)
            {
                if ((++n % CancellationCheckInterval) == 0) ct.ThrowIfCancellationRequested();
                // 视口自身不画边框：AutoCAD 里视口边框属于非打印的 Defpoints 语义，
                // 打印/出图看不到；画出来反而在图纸四周多出一圈不该有的矩形。
                if (e is Viewport vp) { if (vp.Id != 1) _viewports.Add((vp, SKMatrix.Identity)); continue; }
                if (options?.SkipRootAnnotations == true && IsRootAnnotation(e)) { Skipped++; continue; }
                ConvertEntity(e, SKMatrix.Identity, 0, inheritedColor: null);
            }
        }

        var model = doc.ModelSpace;
        if (model?.Entities is null) return;

        foreach (var (vp, toPaper) in _viewports)
            ConvertViewport(model, vp, toPaper, ct);
    }

    /// <summary>
    /// 单个视口：模型空间几何 → 图纸坐标。
    ///
    /// 变换链（世界→图纸）：先平移到视口视图中心，按 <c>视高/视口高</c> 缩放，
    /// 按扭转角旋转，再平移到视口中心；最后裁剪到视口矩形。
    /// <para>视口中心是图纸坐标（mm），视高是模型单位，二者之比就是出图比例。</para>
    /// </summary>
    private void ConvertViewport(BlockRecord model, Viewport vp, in SKMatrix toPaper, CancellationToken ct)
    {
        if (!LayoutProjection.IsDrawable(vp)) return;

        // 模型 → 视口所在坐标系 → 图纸（视口可能挂在图框块里）
        var m = Mul(LayoutProjection.ModelToPaper(vp), toPaper);
        var clip = LayoutProjection.TransformRect(LayoutProjection.PaperRect(vp), toPaper);

        HashSet<string>? frozen = null;
        try
        {
            if (vp.FrozenLayers is { Count: > 0 })
                frozen = new HashSet<string>(vp.FrozenLayers.Select(l => l.Name ?? string.Empty),
                                             StringComparer.OrdinalIgnoreCase);
        }
        catch { }

        _clipRect = clip;
        _clipFrozenLayers = frozen;
        int before = _entities.Count;
        try
        {
            int n = 0;
            foreach (var e in model.Entities)
            {
                if ((++n % CancellationCheckInterval) == 0) ct.ThrowIfCancellationRequested();
                ConvertEntity(e, m, 0, inheritedColor: null);
            }
        }
        finally
        {
            _clipRect = null;
            _clipFrozenLayers = null;
            RebuildScopeClip();
        }
        _spaceNotes.Add($"vp id={vp.Id} paper={LayoutProjection.PaperRect(vp)} " +
                            $"viewH={vp.ViewHeight:F1} scale={LayoutProjection.Scale(vp):F4} " +
                            $"clip={clip} kept={_entities.Count - before}");
    }

    /// <summary>
    /// 重新计算本层的裁剪区域 = 视口矩形 ∩ 空间过滤器多边形；两者都没有时不裁。
    ///
    /// <para>裁剪改为**渲染期画布裁剪**（<see cref="CadEntity.ClipPath"/>）：Skia 的
    /// <c>SKPath.Op</c> 按填充区域求交，开放路径（LINE/ARC/PLINE）面积为 0、求交结果为空，
    /// 之前那条"路径求交"通道等于把视口/裁剪框里的线条全部删掉（实测
    /// <c>line.Op(rect, Intersect)</c> 返回 0 点）。画布裁剪对标边、填充、文字一视同仁。</para>
    /// </summary>
    private void RebuildScopeClip()
    {
        _scopeClip = null;
        _scopeClipBounds = default;
        _scopeClipIsRect = false;

        if (_clipRect is { } rect)
        {
            var p = RectPath(rect);
            _clipPaths.Add(p);
            _scopeClip = p;
            _scopeClipBounds = rect;
            _scopeClipIsRect = true;
        }

        if (_spatialClipPath is { } sp)
        {
            if (_scopeClip is not { } outer)
            {
                _scopeClip = sp;
                _scopeClipBounds = _spatialClipBounds;
                _scopeClipIsRect = _spatialClipIsRect;
                return;
            }

            // 两条闭合路径求交：都有面积，Op 在这里是可靠的
            var inter = outer.Op(sp, SKPathOp.Intersect);
            _clipPaths.Add(inter ?? new SKPath());
            _scopeClip = inter;
            _scopeClipBounds = SKRect.Intersect(_scopeClipBounds, _spatialClipBounds);
            _scopeClipIsRect = _scopeClipIsRect && _spatialClipIsRect;
            if (inter is null || inter.IsEmpty)
            {
                // 视口与裁剪框不相交 → 这一层什么也不画
                _scopeClipBounds = SKRect.Create(_scopeClipBounds.Left, _scopeClipBounds.Top, 0, 0);
            }
        }
    }

    private static SKPath RectPath(SKRect r)
    {
        var path = new SKPath();
        path.AddRect(r);
        return path;
    }

    /// <summary>包围盒是否可能落在裁剪区域内（快速剔除；精确裁剪在渲染期做）。</summary>
    private bool InsideClip(SKRect bounds)
        => _scopeClip is null || bounds.IntersectsWith(_scopeClipBounds);

    /// <summary>
    /// 实体包围盒有多大比例落在 <paramref name="sheet"/> 内（面积比，0~1）。
    /// 用于判断一条"边界失效"的裁剪引用是不是绝大部分在图纸外（见 <see cref="ClipUnresolvedMode"/>）。
    /// 任一边退化为 0（轴对齐直线）时按 1 处理，交给调用方的相交判断去决定。
    /// </summary>
    private static float BoundsCoverage(SKRect entity, SKRect? sheet)
    {
        if (sheet is not { } frame) return 1f;
        float area = entity.Width * entity.Height;
        var inter = SKRect.Intersect(entity, frame);
        float hit = MathF.Max(0f, inter.Width) * MathF.Max(0f, inter.Height);
        if (!(area > 0)) return hit > 0 ? 1f : 0f;
        return Math.Clamp(hit / area, 0f, 1f);
    }

    /// <summary>
    /// 给 [fromIndex, _entities.Count) 内**尚未认领**的图元挂当前块实例身份。
    ///
    /// <para>块内子实体在各自的内层 <c>ConvertEntity</c> 收尾时就已写好自己更内层的实例 Id，
    /// 这里只补本层直接产出、以及内层因异常没写到的那些 —— 所以判据是「未指派」而不是「本层」。</para>
    /// </summary>
    private void ApplyInstance(int fromIndex)
    {
        for (int i = fromIndex; i < _entities.Count; i++)
        {
            var e = _entities[i];
            if (e.InstanceId < 0) e.InstanceId = _currentInstance;
        }
    }

    /// <summary>
    /// 登记一个块实例并返回其 Id（同父同句柄重复登记返回已有 Id）。
    /// MINSERT 阵列只登记**一次**：AutoCAD 里一条阵列插入就是一条实体。
    /// </summary>
    private int AddInstance(ulong insertHandle, string? blockName)
    {
        int parent = _currentInstance < 0 ? 0 : _currentInstance;
        for (int i = 0; i < _instances.Count; i++)
        {
            var existing = _instances[i];
            if (existing.Parent == parent && existing.InsertHandle == insertHandle) return existing.Id;
        }

        int id = _instances.Count + 1;
        _instances.Add(new InsertInstance
        {
            Id = id,
            Parent = parent,
            InsertHandle = insertHandle,
            BlockName = blockName ?? string.Empty,
        });
        return id;
    }

    /// <summary>
    /// 给 [fromIndex, _entities.Count) 内新增的图元挂裁剪区域（P2-17）。
    ///
    /// <para>只有**跨边界**的图元才真的挂 <see cref="CadEntity.ClipPath"/>：完全落在区域内
    /// 的照常参与合批（挂上就得逐条画，大块裁剪会掉帧）；完全在区域外的直接丢弃。
    /// 包围盒一律收窄到裁剪区域，否则自适应范围会把框外内容算进去。</para>
    /// </summary>
    private void ApplyScopeClip(int fromIndex)
    {
        if (_scopeClip is null) return;
        for (int i = fromIndex; i < _entities.Count; i++)
        {
            var e = _entities[i];
            if (e.ClipPath is not null) continue;      // 外层已经挂过

            if (!e.Bounds.IntersectsWith(_scopeClipBounds))
            {
                // 完全在裁剪区域外：直接丢弃，别让它污染范围（路径是原生内存，得手动释放）
                ClippedAway++;
                e.Path?.Dispose();
                e.FillPath?.Dispose();
                _entities.RemoveAt(i--);
                continue;
            }

            if (!ScopeContainsBounds(e.Bounds))
                e.ClipPath = _scopeClip;

            e.Bounds = SKRect.Intersect(e.Bounds, _scopeClipBounds);
        }
    }

    /// <summary>
    /// 包围盒是否整个落在裁剪区域里（= 不必逐条裁剪）。
    /// 矩形区域直接比矩形；多边形区域用"四角都在里面"判定 —— 凹多边形的极端情形可能
    /// 误判成完全在内而漏裁一小块，换来的是绝大多数图元仍然能合批。
    /// </summary>
    private bool ScopeContainsBounds(SKRect bounds)
    {
        if (!_scopeClipBounds.Contains(bounds)) return false;
        if (_scopeClipIsRect) return true;
        if (_scopeClip is not { } clip) return true;
        return clip.Contains(bounds.Left, bounds.Top)
            && clip.Contains(bounds.Right, bounds.Top)
            && clip.Contains(bounds.Right, bounds.Bottom)
            && clip.Contains(bounds.Left, bounds.Bottom);
    }

    private bool IsFrozenInViewport(Entity e)
        => _clipFrozenLayers is { Count: > 0 } set && set.Contains(LayerName(e));

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
        // 诊断用图层过滤：在**转换期**丢弃，连文字/点标记/带裁剪图元一起走
        // （渲染期 SetLayerHidden 只管合批几何，定位"多出来的东西"时会漏）。
        if (_hideLayers is { } hidden && e.Layer is { } lay && hidden.Contains(lay.Name))
        {
            LayerFiltered++;
            Skipped++;
            return;
        }

        // 实体被显式隐藏（DXF 组码 60 = 1，AutoCAD 亦不显示）。
        // 实测测试图.dwg：7836 个源实体带此标记（其中 1734 个 INSERT），展开约 5 万个显示实体；
        // 过滤后全图墨迹覆盖率 3.06% → 2.03%，经与 AutoCAD 语义核对属于正确行为而非内容丢失。
        // DrawingView_SHOW_INVISIBLE=1 可关闭过滤（仅供与第三方渲染器对照基准用）。
        if (e.IsInvisible && !ShowInvisible)
        {
            // 属性有独立的不可见标志（组码 70 bit 1 或实体 60），单独计数便于诊断
            // "块里属性没显示"到底是不可见、还是没画。
            if (e is AttributeEntity) AttributeInvisible++;
            Skipped++;
            return;
        }

        // 视口冻结（VPFREEZE）：只在该视口内不显示，与全局图层冻结不同
        if (IsFrozenInViewport(e)) { Skipped++; return; }

        // 实体级空间过滤器（XDictionary: ACAD_FILTER → SPATIAL）——XCLIP 与「局部打开」留下的
        // 裁剪边界。AutoCAD 对带裁剪边界的块引用/外部参照**只画框内部分**，框外连 EXTMAX 都不算。
        //
        // 实测 52陶板节点/9---陶板节点 (05).dwg：模型空间一个 26 成员的裁剪块（INSERT H=4301 →
        // *U33，边界落在 (54025,-5747)-(54599,-5067)，离图形 5.3 万单位）。AutoCAD 整块不画、
        // ZOOM E 的 VIEWSIZE=849；我们照画 26 份，自适应范围从 y=1162 被拉到 y=2878，
        // 于是"适应窗口"把整张图缩到 1/3，上方拖出一条锯齿长条。
        //
        // 只在顶层（parent 为单位阵）或已标定块内换算（见 ClipInBlock）时生效。
        SKPath? prevSpatialClip = _spatialClipPath;
        SKRect prevSpatialBounds = _spatialClipBounds;
        bool prevSpatialIsRect = _spatialClipIsRect;
        bool clipChanged = false;
        int entityStart = _entities.Count;
        if (SpatialClipEnabled && (parent.IsIdentity || ClipInBlock) &&
            TryGetSpatialClip(e, parent, out var clipPath, out var clipBounds, out var clipIsRect,
                             out var frameKnown))
        {
            if (TryGetEntityBounds(e, parent, out var entityBounds) && !entityBounds.IntersectsWith(clipBounds))
            {
                // 边界与实体完全不相交 = 过滤器坐标系没读出来（ACadSharp 对一部分旧格式过滤器
                // 的 Normal/InsertTransform 不可信）。这时边界位置本身没有意义，只能按"这条引用
                // 有多大比例落在图纸范围内"猜 AutoCAD 会不会画它 —— 详见 ClipUnresolvedMode。
                bool keepInFrame = ClipUnresolvedMode switch
                {
                    "drop" => false,
                    "frame" => true,
                    _ => BoundsCoverage(entityBounds, _sheetRect) >= ClipUnresolvedMinCoverage,
                };
                if (_clipDropHandles.Contains(e.Handle)) keepInFrame = false;   // 手工豁免（见字段说明）
                if (!keepInFrame || _sheetRect is not { } sheet || !entityBounds.IntersectsWith(sheet))
                {
                    clipPath.Dispose();
                    SpatialClipped++;
                    Skipped++;
                    return;
                }

                clipPath.Dispose();
                _spatialClipPath = RectPath(sheet);
                _clipPaths.Add(_spatialClipPath);
                _spatialClipBounds = sheet;
                _spatialClipIsRect = true;
            }
            else
            {
                _spatialClipPath = clipPath;
                _clipPaths.Add(clipPath);
                _spatialClipBounds = clipBounds;
                _spatialClipIsRect = clipIsRect;
                // 边界点可信 = 这就是 AutoCAD 会画出来的 XCLIP 边框（XCLIPFRAME≠0 时）。
                // 兜底那两条（退回图纸范围 / 坏法向）不算：那不是块自己的框。
                if (frameKnown)
                    _clipFrames.Add(new ClipFrame
                    {
                        Handle = e.Handle,
                        Path = clipPath,
                        Bounds = clipBounds,
                    });
            }

            RebuildScopeClip();
            clipChanged = true;
        }

        var prevHandle = _currentHandle;
        var prevOwnHandle = _currentOwnHandle;
        _currentHandle = handleOverride ?? e.Handle;
        _currentOwnHandle = e.Handle;

        if (ConvTrace)
            Trace($"depth={depth} {e.GetType().Name} H={e.Handle:X} L={e.Layer?.Name}");

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
                    // **必须解析原始 Value**：PlainText 会把 \W0.8\T0.75; 这类格式码的参数体留在正文里，
                    // 图签会整块糊掉（见 MTextFormat 的说明）。
                    var fmt = MTextFormat.Parse(mt.Value);
                    var raw = string.IsNullOrEmpty(fmt.Text) ? CleanMText(mt.Value) : fmt.Text;
                    AddMText(mt, raw, parent, color, fmt);
                    break;
                }
                // 块定义里的属性**占位符**（AttributeDefinition）不画：块参照显示的是 ATTRIB 的值，
                // 占位符（TAG）只在块编辑器里可见。它继承自 TextEntity，不拦住会被当成正文画出来。
                case AttributeDefinition:
                    AttributeDefsSkipped++;
                    break;
                // 属性值（ATTRIB）：ConvertInsert 会把 Insert.Attributes 按实例送进来，
                // 位置/字高随 INSERT 变换，与块内正文走同一条文本管线。
                case AttributeEntity att:
                    if (att.Flags.HasFlag(AttributeFlags.Hidden)) { AttributeInvisible++; break; }
                    AddText(att, parent, color);
                    AttributeDrawn++;
                    break;
                case TextEntity t: AddText(t, parent, color); break;
                case Insert ins: ConvertInsert(ins, parent, depth, inheritedColor); break;
                case Viewport vp:
                    // 图框常被做成块，视口就藏在块里：记下「视口坐标系→图纸」的矩阵，
                    // 布局收尾时统一展开。视口本身不画边框（同顶层视口）。
                    if (vp.Id != 1) _viewports.Add((vp, parent));
                    break;
                case Hatch h: AddHatch(h, parent, color); break;
                case Solid sol: AddSolid(sol, parent, color); break;
                case Leader ld: AddLeader(ld, parent, color); break;
                case Dimension dim: AddDimensionBlock(dim, parent, depth, inheritedColor); break;
                case ACadSharp.Entities.Point pt: AddPointCross(pt, parent, color); break;
                // REGION：ACadSharp 只给 ACIS 载荷（R2013+ 是二进制 SAB），几何要从 B-rep 拓扑重建，
                // 见 RegionOutline（P2-7）。3DSOLID/BODY/SURFACE 同为 ModelerGeometry，暂不处理：
                // 三维实体的线框投影到 XY 会得到一堆互相穿插的边，反而干扰识图。
                case Region rg: AddRegion(rg, parent, color); break;
                default: Skipped++; break;
            }
        }
        catch (Exception ex)
        {
            Skipped++;
            if (ConvTrace) Trace($"THROW depth={depth} H={_currentHandle:X} {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            // 本层新增的图元统一挂裁剪区域、收窄包围盒（含文字/点标记）
            ApplyScopeClip(entityStart);
            // 身份只填「还没有人认领」的：块内子实体已由内层 ConvertEntity 写成自己的实例
            ApplyInstance(entityStart);

            _currentHandle = prevHandle;
            _currentOwnHandle = prevOwnHandle;
            if (clipChanged)
            {
                _spatialClipPath = prevSpatialClip;
                _spatialClipBounds = prevSpatialBounds;
                _spatialClipIsRect = prevSpatialIsRect;
                RebuildScopeClip();
            }
        }
    }

    /// <summary>
    /// 读实体级裁剪边界（<c>XDictionary["ACAD_FILTER"]["SPATIAL"]</c>），返回**世界坐标多边形**。
    ///
    /// <para><b>坐标要换两次</b>（2026-11-13 用 AutoCAD 标定，见下）：</para>
    /// <list type="number">
    ///   <item>边界点 → <b>块空间</b>：由 <see cref="SpatialFilter.InsertTransform"/> 完成（组码 40 的 3×4 矩阵）；</item>
    ///   <item>块空间 → <b>世界</b>：由该实体自身的插入矩阵完成（INSERT 的缩放/旋转/插入点 + 挤出方向）。</item>
    /// </list>
    /// <para>标定证据（THSYB1-11 标准竖剖.dwg，H=758，AutoCAD 只留该实体后 EXTMIN/EXTMAX =
    /// (-100.02,-25.04)-(200.05,38.02)）：边界点 (1325,-1288.5)-(1625,-1225.5)，矩阵 #1 平移
    /// (-1025,+1263.5) → 块空间 (300,-25)-(600,38)，插入点 (-400,0) → 世界 (-100,-25)-(200,38)，逐位吻合。
    /// 只做第一步会得到 (300,-25)（偏 400），两步都不做会得到 (1325,-1288.5)（偏 1400+）。</para>
    ///
    /// <para><b>返回多边形而不是外接矩形</b>（P2-17）：实测 <c>THSYB1-12 层间竖剖.dwg</c> 的边界是
    /// 8 点台阶形，面积只有外接矩形的 74%；按矩形裁会把缺口里的内容画出来（AutoCAD 是抠掉的）。</para>
    ///
    /// <para><b>块内（2026-11-14 补）</b>：上面这条链在块里还要再乘**父链矩阵**（块坐标→世界），
    /// 即 <c>world = parent × selfInsert × inverse(boundary)</c>。之前这里靠
    /// <c>parent.IsIdentity</c> 把块内一律跳过，理由是"边界到底在哪个坐标系没标定"；用
    /// <c>52陶板节点</c> 的 <c>TK-A3-NT/qwe</c>（H=1498）标定后这条链是闭合的：边界
    /// (432.741,123.331)-(478.741,150.029) 过 <c>inv</c>（缩放 304.2042 + 平移
    /// -133156.85/-27416.02）落回 qwe 块坐标 (-1515,10102)-(12478,18223)（与块内容 754..13748
    /// 同一量级），再乘 qwe 的插入（scale 0.00329 = inv 缩放的倒数，互为逆）得父块坐标
    /// (367.6,116.3)-(413.6,143.0)，最后乘 TK 的插入（-90° + scale 2）得世界
    /// (645.2,335.0)-(698.6,427.0)。这个窗口把图签缩略平面图的左半切掉，与 ODA / AutoCAD 的显示一致（实测该区多画 2153→46 px）。</para>
    /// </summary>
    private bool TryGetSpatialClip(Entity e, in SKMatrix parent, out SKPath clipPath, out SKRect bounds, out bool isRect)
    {
        return TryGetSpatialClip(e, parent, out clipPath, out bounds, out isRect, out _);
    }

    /// <param name="frameKnown">
    /// 裁剪边界是否**来自文件里的边界点**（而不是"边界与内容脱节，退回图纸范围"的兜底）。
    /// 只有为真时才能把这条边界当 XCLIP 边框画出来 —— 兜底那条是图纸范围，不是块自己的框。
    /// </param>
    private bool TryGetSpatialClip(Entity e, in SKMatrix parent, out SKPath clipPath, out SKRect bounds,
                                   out bool isRect, out bool frameKnown)
    {
        clipPath = null!;
        bounds = default;
        isRect = false;
        frameKnown = false;

        var xdict = e.XDictionary;
        if (xdict is null) return false;
        if (!xdict.TryGetEntry<CadDictionary>("ACAD_FILTER", out var filter)) return false;
        if (!filter.TryGetEntry<SpatialFilter>("SPATIAL", out var sf)) return false;

        var points = sf.BoundaryPoints;
        if (points is null || points.Count == 0) return false;

        // 先验可信度：ACadSharp 对**旧格式**的 SPATIAL_FILTER 会解析错位（法向读出 (0,0,1.8e6)、
        // 原点读出 (330084,31081) 这种量级，边界点也跟着偏）。这类记录宁可整条不用 ——
        // 猜错了会**把内容裁没**，不裁最多是多画一点。
        // 实测：9---陶板节点 (03)/(04) 的过滤器法向都是坏的，按坏边界裁会把整块裁空。
        var n = sf.Normal;
        if (System.Math.Abs(System.Math.Abs(n.Z) - 1.0) > 1e-3 ||
            System.Math.Abs(n.X) > 1e-6 || System.Math.Abs(n.Y) > 1e-6)
        {
            // 数据不可信（ACadSharp 读不出矩阵）时，边界点也一起偏。AutoCAD 照样会裁，
            // 只是我们算不出裁到哪儿；退而求其次按**图纸范围**（文件头 EXTMIN/EXTMAX，
            // 本来就是 AutoCAD 自己算的）裁 —— 实测 THSYB1-12 的 H=845 靠这条把
            // 探到 x=-803 的块内容收回到图框内，与 AutoCAD 的 extents 对得上。
            if (BadNormalClipToSheet && _sheetRect is { } sheet)
            {
                clipPath = RectPath(sheet);
                bounds = sheet;
                isRect = true;
                return true;
            }
            return false;
        }

        // 坐标取法（见 ClipXformMode 的说明）：Auto 下"矩阵是单位阵"就当边界已经是世界坐标，
        // 否则先过 InsertTransform 再过实体插入矩阵。
        var toBlock = sf.InsertTransform;
        bool tfIdentity = IsIdentityMatrix(toBlock);

        // 正向矩阵读成单位阵、反向矩阵却有内容 ⇒ 换用反向矩阵（见 ClipUseInverseXform）。
        //
        // 注意平移分量的位置：ACadSharp 读这类旧记录的 4x4 是**先转置再填**的，平移落在
        // 最后一列（M03/M13），而行优先约定该在的 M30/M31 是 0 —— 直接 `Matrix4 * XYZ`
        // 会把它丢掉（实测结果里边界点原样未动）。这里自己按**列向量**约定乘：
        // x' = M00·x + M01·y + M03，y' = M10·x + M11·y + M13（线性部分也一起用上，
        // `竖剖节点陶板.dwg` 的 H=36849 反向矩阵是 M11 = -1 的镜像，只取平移会算歪）。
        Matrix4? invToBlock = null;
        if (tfIdentity && ClipUseInverseXform)
        {
            var inv = sf.InverseInsertTransform;
            if (!IsIdentityMatrix(inv))
            {
                invToBlock = inv;
                tfIdentity = false;
            }
        }

        bool hasToBlock = ClipXform switch
        {
            ClipXformMode.Raw => false,
            ClipXformMode.Tf => !tfIdentity,
            ClipXformMode.Full => !tfIdentity,
            _ => !tfIdentity,                       // Auto
        };

        // 实体自身的块→世界矩阵；非 INSERT（或退化插入）时按单位阵处理
        SKMatrix toWorld = SKMatrix.Identity;
        bool useEntityMatrix = ClipXform switch
        {
            ClipXformMode.Full => true,
            ClipXformMode.Auto => !tfIdentity,
            _ => false,
        };
        bool hasToWorld = useEntityMatrix
                          && e is Insert ins && CadTransform.TryInsertMatrix(ins, out toWorld);
        if (!hasToWorld) toWorld = SKMatrix.Identity;

        var world = new List<SKPoint>(points.Count);
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var p in points)
        {
            double x = p.X, y = p.Y;
            if (hasToBlock)
            {
                if (invToBlock is { } m)
                {
                    // 反向矩阵（世界→块）：ACadSharp 把平移填在最后一列，按列向量约定乘
                    double nx = m.M00 * x + m.M01 * y + m.M02 * 0 + m.M03;
                    double ny = m.M10 * x + m.M11 * y + m.M12 * 0 + m.M13;
                    x = nx;
                    y = ny;
                }
                else
                {
                    var block = toBlock * new XYZ(x, y, 0);
                    x = block.X;
                    y = block.Y;
                }
            }

            var w = CadTransform.Apply(toWorld, new SKPoint((float)x, (float)y));
            // 块内：再沿父链回到世界（顶层 parent 为单位阵，这一步是恒等，行为不变）
            if (!parent.IsIdentity) w = CadTransform.Apply(parent, w);
            x = w.X;
            y = w.Y;

            if (double.IsNaN(x) || double.IsNaN(y) || double.IsInfinity(x) || double.IsInfinity(y)) return false;
            world.Add(new SKPoint((float)x, (float)y));
            minX = MathF.Min(minX, (float)x);
            minY = MathF.Min(minY, (float)y);
            maxX = MathF.Max(maxX, (float)x);
            maxY = MathF.Max(maxY, (float)y);
        }

        if (!(maxX > minX) || !(maxY > minY)) return false;   // 退化边界（点/线）当没有

        var rect = new SKRect(minX, minY, maxX, maxY);
        if (ClipDebug)
            Console.Error.WriteLine($"[clip] H={e.Handle} inBlock={!parent.IsIdentity} inv={(invToBlock is not null ? "yes" : "no")} hasToWorld={hasToWorld} rect=({rect.Left:F1},{rect.Top:F1})-({rect.Right:F1},{rect.Bottom:F1})");

        // 只有 1~2 个点（实测 THSYB1-12 的 H=845/H=850 就是 2 点对角）画不出多边形，
        // 按外接矩形处理：这类记录本身是坏的，但 AutoCAD 照样按它裁 —— 与实体不相交时
        // 走"按图纸范围裁"的兜底，效果与 AutoCAD 一致（内容被切到图框内）。
        if (world.Count < 3)
        {
            clipPath = RectPath(rect);
            bounds = rect;
            isRect = true;
            frameKnown = true;
            return true;
        }

        // 边界顶点全落在外接矩形的边上 ⇒ 这个多边形就是矩形（轴对齐 XCLIP 的常见情形），
        // 下游可以走"矩形包含"快路径，不必逐条裁剪。
        const float eps = 1e-3f;
        isRect = world.All(pt =>
            MathF.Abs(pt.X - rect.Left) < eps || MathF.Abs(pt.X - rect.Right) < eps ||
            MathF.Abs(pt.Y - rect.Top) < eps || MathF.Abs(pt.Y - rect.Bottom) < eps);

        // 顶点顺序不可信时退回外接矩形：自相交 / 面积远小于外接矩形（实测 PE0101 的 11 点
        // 边界按存储顺序连出来会自交，照它裁会**切掉本该显示的内容** —— 那比多画一点糟得多）。
        if (!isRect && !IsUsablePolygon(world, rect))
        {
            clipPath = RectPath(rect);
            bounds = rect;
            isRect = true;
            frameKnown = true;
            return true;
        }

        var path = new SKPath();
        path.MoveTo(world[0]);
        for (int i = 1; i < world.Count; i++) path.LineTo(world[i]);
        path.Close();

        clipPath = path;
        bounds = rect;
        frameKnown = true;
        return true;
    }

    /// <summary>
    /// 边界顶点能否当作简单多边形用：不自相交，且面积不小于外接矩形的 40%。
    /// 不满足就退回外接矩形 —— 宁可多画一点，也不按坏顺序把内容切掉。
    /// </summary>
    private static bool IsUsablePolygon(List<SKPoint> pts, SKRect rect)
    {
        int n = pts.Count;
        if (n < 3) return false;

        // 1) 自相交检测（跳过共端点的相邻边）
        for (int i = 0; i < n; i++)
        {
            var a1 = pts[i];
            var a2 = pts[(i + 1) % n];
            for (int j = i + 2; j < n; j++)
            {
                if (i == 0 && j == n - 1) continue;          // 首尾相邻
                var b1 = pts[j];
                var b2 = pts[(j + 1) % n];
                if (SegmentsCross(a1, a2, b1, b2)) return false;
            }
        }

        // 2) 面积占比（鞋带公式）
        double area = 0;
        for (int i = 0; i < n; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % n];
            area += (double)a.X * b.Y - (double)b.X * a.Y;
        }
        area = Math.Abs(area) * 0.5;
        double bboxArea = (double)rect.Width * rect.Height;
        return bboxArea > 0 && area >= bboxArea * 0.4;
    }

    private static bool SegmentsCross(SKPoint p1, SKPoint p2, SKPoint p3, SKPoint p4)
    {
        static float Cross(SKPoint o, SKPoint a, SKPoint b)
            => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

        float d1 = Cross(p3, p4, p1);
        float d2 = Cross(p3, p4, p2);
        float d3 = Cross(p1, p2, p3);
        float d4 = Cross(p1, p2, p4);
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0))
            && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }

    /// <summary>实体包围盒（**世界坐标**）；空盒/取不到时返回 false（此时不参与裁剪判定）。</summary>
    ///
    /// <para><paramref name="parent"/>：实体所在容器的块坐标→世界矩阵。ACadSharp 的
    /// <c>GetBoundingBox()</c> 给的是**父容器坐标**（块内实体是块定义坐标，Insert 是父块坐标，
    /// 模型空间实体是世界坐标），块内实体不乘这一步就会拿"块坐标的盒子"去和"世界的裁剪窗口"比，
    /// 永远不相交 —— 表现是整块被当成"裁掉"丢掉（2026-11-14 块内 XCLIP 就是这个坑：qwe 的
    /// 692 个展开图元全丢）。</para>
    private static bool TryGetEntityBounds(Entity e, in SKMatrix parent, out SKRect bounds)
    {
        bounds = default;
        BoundingBox bb;
        try { bb = e.GetBoundingBox(); }
        catch { return false; }

        double x0 = bb.Min.X, y0 = bb.Min.Y, x1 = bb.Max.X, y1 = bb.Max.Y;
        if (double.IsNaN(x0) || double.IsNaN(y0) || double.IsNaN(x1) || double.IsNaN(y1)) return false;
        if (double.IsInfinity(x0) || double.IsInfinity(y0) || double.IsInfinity(x1) || double.IsInfinity(y1)) return false;
        if (x1 < x0) (x0, x1) = (x1, x0);
        if (y1 < y0) (y0, y1) = (y1, y0);

        var box = SKRect.Create((float)x0, (float)y0, (float)(x1 - x0), (float)(y1 - y0));

        // ACadSharp 的 Insert.GetBoundingBox() 已经含它自己的插入变换、给的是**父容器坐标**
        // （实测 TK-A3-NT/qwe H=1498：(375.1,54.9)-(417.8,139.7)，与父块坐标一致），
        // 所以这里只需要沿父链回到世界，不要再乘一次自身矩阵（会缩成 0.003 倍、误判不相交）。
        if (!parent.IsIdentity)
            box = TransformRect(box, parent);

        bounds = box;
        return true;
    }

    /// <summary>轴对齐盒子过仿射变换（取四角的外接矩形）。</summary>
    private static SKRect TransformRect(SKRect r, in SKMatrix m)
    {
        var pts = new[]
        {
            new SKPoint(r.Left, r.Top),
            new SKPoint(r.Right, r.Top),
            new SKPoint(r.Right, r.Bottom),
            new SKPoint(r.Left, r.Bottom),
        };
        pts = m.MapPoints(pts);   // SkiaSharp 的这个重载返回新数组，不原地改
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var p in pts)
        {
            if (p.X < minX) minX = p.X;
            if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.Y > maxY) maxY = p.Y;
        }
        return SKRect.Create(minX, minY, maxX - minX, maxY - minY);
    }

    private static bool IsIdentityMatrix(in Matrix4 m)
    {
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
                if (Math.Abs(m[r, c] - (r == c ? 1.0 : 0.0)) > 1e-9) return false;
        return true;
    }

    /// <summary>两个裁剪矩形的交集（空集时退化成零面积矩形，效果等于"全裁掉"）。</summary>
    private static SKRect IntersectRect(SKRect a, SKRect b)
    {
        float left = MathF.Max(a.Left, b.Left);
        float top = MathF.Max(a.Top, b.Top);
        float right = MathF.Max(left, MathF.Min(a.Right, b.Right));
        float bottom = MathF.Max(top, MathF.Min(a.Bottom, b.Bottom));
        return new SKRect(left, top, right, bottom);
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
        var path = CadGeometry.BuildEllipticalArc(el.Center.X, el.Center.Y,
            el.MajorAxisEndPoint.X, el.MajorAxisEndPoint.Y,
            -el.MajorAxisEndPoint.Y * el.RadiusRatio,
            el.MajorAxisEndPoint.X * el.RadiusRatio,
            el.StartParameter, el.EndParameter);
        if (path is null) { Skipped++; return; }
        AddStroke(path, el, color, m);
    }

    /// <summary>
    /// LWPOLYLINE，带**宽度轮廓**（B4）。
    ///
    /// <para>宽度是几何不是线宽：有宽度时生成闭合轮廓交给填充（并与 AutoCAD 一致地忽略线宽），
    /// 全段宽度为 0 时才退回描边。宽度取值遵循 DXF 口径——组码 40/41 挂在**起点顶点**上，
    /// 描述"从该顶点出发那一段"的起/终宽度；两者都为 0 时回落到组码 43（ConstantWidth）。</para>
    /// </summary>
    private void AddLwPolyline(LwPolyline lw, in SKMatrix m, uint color)
    {
        if (lw.Vertices == null || lw.Vertices.Count == 0) { Skipped++; return; }
        float constant = (float)lw.ConstantWidth;

        if (TryAddWidePolyline(lw.Vertices.Count, lw.IsClosed, i =>
            {
                var v = lw.Vertices[i];
                var next = lw.Vertices[(i + 1) % lw.Vertices.Count];
                var (w0, w1) = ResolveSegmentWidths((float)v.StartWidth, (float)v.EndWidth, constant);
                return new WidePolyline.Piece(
                    new SKPoint((float)v.Location.X, (float)v.Location.Y),
                    new SKPoint((float)next.Location.X, (float)next.Location.Y),
                    w0, w1, v.Bulge);
            }, lw, color, m))
            return;

        var path = new SKPath();
        CadGeometry.AppendLwPolyline(path, lw);
        AddStroke(path, lw, color, m);
    }

    /// <summary>POLYLINE2D（老式折线），宽度口径同 <see cref="AddLwPolyline"/>。</summary>
    private void AddPolyline2D(Polyline2D p2, in SKMatrix m, uint color)
    {
        if (p2.Vertices == null || p2.Vertices.Count == 0) { Skipped++; return; }

        if (TryAddWidePolyline(p2.Vertices.Count, p2.IsClosed, i =>
            {
                var v = p2.Vertices[i];
                var next = p2.Vertices[(i + 1) % p2.Vertices.Count];
                var (w0, w1) = ResolveSegmentWidths((float)v.StartWidth, (float)v.EndWidth, 0f);
                return new WidePolyline.Piece(
                    new SKPoint((float)v.Location.X, (float)v.Location.Y),
                    new SKPoint((float)next.Location.X, (float)next.Location.Y),
                    w0, w1, v.Bulge);
            }, p2, color, m))
            return;

        var path = new SKPath();
        CadGeometry.AppendPolyline2D(path, p2);
        AddStroke(path, p2, color, m);
    }

    /// <summary>
    /// 每段的起/终宽度（世界单位）。DXF 组码 40/41 挂在起点顶点上；
    /// 只给了起点宽度（41=0）时按等宽处理，两者都 0 时用 ConstantWidth。
    /// </summary>
    private static (float Start, float End) ResolveSegmentWidths(float startWidth, float endWidth, float constantWidth)
    {
        if (startWidth <= 0f && endWidth <= 0f) return (constantWidth, constantWidth);
        if (endWidth <= 0f) return (startWidth, startWidth);
        return (startWidth, endWidth);
    }

    /// <summary>
    /// 有宽度就生成轮廓填充并计数；返回 false = 所有段宽度都是 0（调用方按普通折线描边）。
    /// </summary>
    private bool TryAddWidePolyline(int vertexCount, bool closed,
                                    Func<int, WidePolyline.Piece> pieceOf, Entity e, uint color, in SKMatrix m)
    {
        int segs = CadGeometry.VertexSegmentCount(vertexCount, closed);
        if (segs <= 0) return false;

        var pieces = new List<WidePolyline.Piece>(segs);
        bool anyWidth = false;
        for (int i = 0; i < segs; i++)
        {
            var pc = pieceOf(i);
            if (pc.StartWidth > 0f || pc.EndWidth > 0f) anyWidth = true;
            pieces.Add(pc);
        }
        if (!anyWidth) return false;

        var fill = WidePolyline.Build(pieces, closed);
        if (fill is null)
        {
            // 顶点太少/退化：仍然算"有宽度"，但画不出来，记一次跳过便于定位
            Skipped++;
            WidePolylineEntities++;
            return true;
        }
        AddFill(fill, e, color, m);
        WidePolylineEntities++;
        return true;
    }

    private void AddSpline(Spline sp, in SKMatrix m, uint color)
    {
        if (sp.ControlPoints == null || sp.ControlPoints.Count < 2) { Skipped++; return; }
        var cx = sp.ControlPoints.Select(p => p.X).ToArray();
        var cy = sp.ControlPoints.Select(p => p.Y).ToArray();
        var knots = sp.Knots?.ToArray() ?? Array.Empty<double>();

        // 权重（组码 41）与闭合标志（组码 70 的 bit1）都必须吃进来（P2-6）：
        //   有理权重是"精确画圆/椭圆"的唯一途径，忽略它形状会鼓/瘪；
        //   闭合样条不 Close 会少最后一段，看起来像"描边缺个口"。
        double[]? weights = sp.Weights is { Count: > 0 } w ? w.ToArray() : null;
        bool closed = sp.IsClosed;

        var path = SplineFlattener.Flatten(cx, cy, knots, sp.Degree, weights: weights, closed: closed);
        if (path is null) { Skipped++; return; }
        if (weights is not null) RationalSplineEntities++;
        AddStroke(path, sp, color, m);
    }

    /// <summary>按有理（带权重）求值的样条条数。</summary>
    public int RationalSplineEntities { get; private set; }

    /// <summary>
    /// REGION → 轮廓路径（P2-7）。
    ///
    /// <para>REGION 的几何不在实体字段里，而在 ACIS 的 B-rep 载荷中；ACadSharp 只把载荷原样
    /// 交出（R2013+ 是二进制 SAB，<c>GetAcisText()</c> 返回 null）。所以这里走
    /// <see cref="RegionOutline"/>，把 loop→coedge→edge→vertex→point 的链走一遍重建闭合轮廓。</para>
    ///
    /// <para>只描边、不填充：AutoCAD 在二维线框下就是这样显示 REGION 的，内环（洞）也天然正确。</para>
    /// </summary>
    private void AddRegion(Region rg, in SKMatrix m, uint color)
    {
        if (!ShowRegions) { Skipped++; return; }
        var loops = RegionOutline.TryBuild(rg.AcisData, out _);
        if (loops is null) { RegionUnresolved++; Skipped++; return; }

        int drawn = 0;
        foreach (var loop in loops)
        {
            if (loop.PointCount < 2) { loop.Dispose(); continue; }
            AddStroke(loop, rg, color, m);
            drawn++;
        }
        if (drawn == 0) { RegionUnresolved++; Skipped++; return; }
        RegionEntities++;
        RegionLoops += drawn;
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

    /// <summary>
    /// POINT → 点标记（P2-3）。
    ///
    /// <para>原实现画固定 ±1 世界单位的十字：在 1:100 的图纸里那是 0.02mm，屏幕上永远看不见；
    /// 深放大时又变成糊满屏的巨大十字。根因是点符号的尺寸本质上是**屏幕尺寸**
    /// （PDSIZE 的两种主要口径都是视高百分比），世界坐标里没有唯一答案。</para>
    ///
    /// <para>所以这里只存 PDMODE/PDSIZE 原值（<see cref="PointMark"/>），
    /// 由渲染端按当前视口换算成像素再画。包围盒退化为一个点（AutoCAD 的 POINT
    /// 也不撑大图面范围），因此它不参与批次合并。</para>
    /// </summary>
    private void AddPointCross(ACadSharp.Entities.Point pt, in SKMatrix m, uint color)
    {
        int mode = _pointDisplayMode;
        if (mode == PointMarkStyle.ModeInvisible) { PointMarksHidden++; return; }

        // 与其它实体走同一条 OCS → 块/布局变换链（顺序与 AddStroke 一致：先 OCS、后块矩阵）
        var ocs = CadGeometry.OcsToWcs(pt);
        var local = new SKPoint((float)pt.Location.X, (float)pt.Location.Y);
        if (!ocs.IsIdentity) local = ocs.MapPoint(local);
        var world = TransformPoint(m, local);

        if (!InsideClip(SKRect.Create(world.X, world.Y, 0, 0))) { ClippedAway++; return; }

        PointMarkEntities++;
        _entities.Add(new CadEntity
        {
            Layer = LayerName(pt),
            ColorArgb = color,
            Handle = _currentHandle,
            OwnHandle = _currentOwnHandle,
            Mark = new PointMark { Position = world, Mode = mode, SizeSpec = _pointDisplaySize },
            Bounds = SKRect.Create(world.X, world.Y, 0f, 0f),
        });
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
        if (ins.Block is null) { DroppedNoBlock++; Skipped++; return; }
        if (depth >= MaxInsertDepth) { DroppedByDepth++; Skipped++; return; }
        // 变换链与标注目标树共用（CadTransform），保证「高亮几何」与「显示几何」不会漂移
        if (!CadTransform.TryInsertMatrix(ins, out var m)) { DroppedByMatrix++; Skipped++; return; }

        // 子实体继承色：INSERT 自身的颜色（BYLAYER 时已解析到图层色）
        uint blockColor = ResolveColor(ins, inheritedColor);

        if (ConvTrace)
            Trace($"EXPAND depth={depth} H={ins.Handle:X} block={ins.Block?.Name} blockH={ins.Block?.Handle:X} ents={ins.Block?.Entities.Count} scale=({ins.XScale:F2},{ins.YScale:F2}) rot={ins.Rotation:F3}");

        CadTransform.ArraySize(ins, out int rows, out int cols, out long total);
        if (total > MaxMInsertInstances)
        {
            // 异常阵列直接放弃整个插入，避免生成天量几何拖垮渲染
            DroppedByMInsert++;
            Skipped++;
            return;
        }

        // 随块线宽：块内写 BYBLOCK 的实体继承本 INSERT 的有效线宽（与上方随块颜色同构）。
        // INSERT 自己是 BYLAYER → 解析出图层线宽；自己是 BYBLOCK → 继承外层块引用，层层上溯。
        int outerWeight = _inheritedLineWeight;
        int blockWeight = ResolveLineWeight(ins, outerWeight);
        int prevInstance = _currentInstance;
        _inheritedLineWeight = blockWeight;
        _currentInstance = AddInstance(ins.Handle, ins.Block.Name);
        try
        {
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

            // 属性值（ATTRIB）：值存在 ATTRIB 上（块定义里同名的是 AttributeDefinition 占位符）。
            //
            // **关键**：DWG/DXF 里 ATTRIB 的插入点与字高**已经过本插入的变换**，不能再乘一次。
            // 实测（01 1#1层平面.dwg 的 DYC-A1 图框）：块内 ATTDEF 项目名称 (781.77, 76.09) 高 5，
            // 插入点 (-23173.3,-30200.3) 比例 64 → ATTRIB 存的是 (26054.58,-25330.69)、高 320，
            // 与 (-23173.3 + 781.77×64, -30200.3 + 76.09×64) 分毫不差。再乘一次会飞到 1.6e6 外。
            // 因此这里只叠加**外层**矩阵（本插入若在别的块里，属性坐标仍在那一层的空间里）。
            if (ins.Attributes is { Count: > 0 })
            {
                foreach (var att in ins.Attributes)
                {
                    if (att is null) continue;
                    ConvertEntity(att, parent, depth + 1, blockColor, handleOverride: _currentHandle);
                }
            }
        }
        finally
        {
            _currentInstance = prevInstance;
            _inheritedLineWeight = outerWeight;
        }
    }

    private void AddDimensionBlock(Dimension dim, in SKMatrix parent, int depth, uint? inheritedColor)
    {
        if (depth >= MaxInsertDepth) { Skipped++; return; }
        var block = dim.Block;
        if (block is null) { Skipped++; return; }
        uint color = ResolveColor(dim, inheritedColor);
        // 标注块里的几何几乎全是 BYBLOCK：继承标注自身的线宽，否则会掉回 0.25mm 默认值
        int outerWeight = _inheritedLineWeight;
        int prevInstance = _currentInstance;
        _inheritedLineWeight = ResolveLineWeight(dim, outerWeight);
        _currentInstance = AddInstance(dim.Handle, block.Name);
        try
        {
            foreach (var child in block.Entities)
                ConvertEntity(child, parent, depth + 1, color, handleOverride: _currentHandle);
        }
        finally
        {
            _currentInstance = prevInstance;
            _inheritedLineWeight = outerWeight;
        }
    }

    /// <summary>
    /// HATCH → 填充/图案线。
    ///
    /// <para><b>孤岛语义（CAD 口径）</b>：<see cref="Hatch.Paths"/> 是"外边界 + 若干孤岛"的
    /// **环集合**，不是一个环一段独立填充。全部环必须合并成一个路径按**奇偶规则**填充：
    /// 第 1/3/5… 层实心、第 2/4/6… 层是洞（洞中洞又实心），这才是 AutoCAD/ODA 的显示。
    /// 图案线同样要对**全部环**求奇偶，否则图案会穿过孤岛。</para>
    ///
    /// <para>旧实现逐环独立填充 + 逐环独立生成图案，导致孤岛被填实、图案线穿洞；
    /// 且边界路径是"每条边一个子路径"（<see cref="CadGeometry.AppendHatchLoop"/>），
    /// 用 Fill 画多段直线环会退化成零面积 → 正方形实心填充整块不显示。
    /// 这里改用压平后的**闭合点列**重建轮廓，实心与图案两条路径都正确。</para>
    /// </summary>
    private void AddHatch(Hatch h, in SKMatrix m, uint color)
    {
        if (h.Paths == null) { Skipped++; return; }

        var boundary = new SKPath { FillType = SKPathFillType.EvenOdd };
        var segs = new List<(SKPoint a, SKPoint b)>();
        var seenContours = new HashSet<string>(StringComparer.Ordinal);
        int contours = 0;

        foreach (var bp in h.Paths)
        {
            if (bp?.Edges == null || bp.Edges.Count == 0) continue;

            // 非法/重复边界不是"可填充的环"，AutoCAD 也不拿它们参与填充：
            //   NotClosed —— 边界没闭合（实测剪力墙填充里有个覆盖全图的 4 边环带此标志，
            //                当外边界用会把整层楼填成一片灰，墙反而变成洞）；
            //   Duplicate —— 同一轮廓出现两次，进奇偶计数会把这块直接变成洞。
            if (bp.Flags.HasFlag(BoundaryPathFlags.NotClosed)) { HatchBadLoops++; continue; }

            foreach (var poly in FlattenHatchContours(bp))
            {
                if (poly.Count < 3) continue;
                // 完全重合的环只保留一份（SameContour 用点序+"规格化"后的坐标串比对）
                if (!seenContours.Add(ContourKey(poly))) { HatchDuplicateLoops++; continue; }
                contours++;
                boundary.MoveTo(poly[0]);
                for (int i = 1; i < poly.Count; i++) boundary.LineTo(poly[i]);
                boundary.Close();
                for (int i = 0; i < poly.Count; i++)
                {
                    var a = poly[i];
                    var b = poly[(i + 1) % poly.Count];
                    if (a != b) segs.Add((a, b));
                }
            }
        }

        if (contours == 0 || boundary.IsEmpty) { boundary.Dispose(); Skipped++; return; }

        // 渐变填充没有图案定义，AutoCAD/ODA 按渐变色铺满整个边界；
        // 这里至少用渐变首色做实心填充（完整双向渐变留待 P2）。
        if (h.IsSolid || IsGradientHatch(h))
        {
            AddFill(boundary, h, IsGradientHatch(h) ? GradientFillColor(h, color) : color, m);
            return;
        }

        if (AddHatchPatternLines(segs, boundary.Bounds, h, color, m))
        {
            // 图案已生成；边界不描边（AutoCAD/ODA 语义：非实心填充只显示图案线）
            boundary.Dispose();
            return;
        }
        AddStroke(boundary, h, color, m);
    }

    /// <summary>是否渐变 HATCH（ACadSharp 没有 IsGradient 属性，看 GradientColor.Enabled）。</summary>
    private static bool IsGradientHatch(Hatch h)
    {
        try { return h.GradientColor is { Enabled: true }; }
        catch { return false; }
    }

    /// <summary>渐变 HATCH 的填充色：取渐变首色，取不到时退回实体/图层色。</summary>
    private static uint GradientFillColor(Hatch h, uint fallback)
    {
        try
        {
            var colors = h.GradientColor?.Colors;
            if (colors is { Count: > 0 })
            {
                var c = colors[0].Color;
                if (!c.IsByLayer && !c.IsByBlock) return ToArgb(c, 7);
            }
        }
        catch { }
        return fallback;
    }

    /// <summary>轮廓的"规格化"键：从最小点开始按原方向排列，用于剔除完全重合的重复环。</summary>
    private static string ContourKey(List<SKPoint> poly)
    {
        int n = poly.Count, best = 0;
        for (int i = 1; i < n; i++)
        {
            var a = poly[i];
            var b = poly[best];
            if (a.X < b.X - 1e-3f || (MathF.Abs(a.X - b.X) <= 1e-3f && a.Y < b.Y - 1e-3f)) best = i;
        }
        var sb = new System.Text.StringBuilder(n * 14);
        for (int k = 0; k < n; k++)
        {
            var p = poly[(best + k) % n];
            sb.Append(MathF.Round(p.X, 2).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
              .Append(MathF.Round(p.Y, 2).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)).Append(';');
        }
        return sb.ToString();
    }

    /// <summary>
    /// 把一个边界环压平成若干**闭合多边形点列**（通常 1 条）。
    /// 优先用边源数据（<see cref="CadGeometry.FlattenHatchLoopPoints"/>，顶点精确共享，
    /// 图案行扫描的奇偶计数才可靠）；含 Spline 边时回退 SKPath 压平。
    /// </summary>
    private static List<List<SKPoint>> FlattenHatchContours(Hatch.BoundaryPath bp)
    {
        var direct = CadGeometry.FlattenHatchLoopPoints(bp);
        if (direct is { Count: >= 3 }) return new List<List<SKPoint>> { direct };

        using var loopPath = new SKPath();
        CadGeometry.AppendHatchLoop(loopPath, bp);
        return loopPath.IsEmpty ? new List<List<SKPoint>>() : FlattenContours(loopPath);
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
    ///
    /// <para><paramref name="segs"/> 是**全部环**（外边界 + 孤岛）的闭合线段表：
    /// 一次穿越计数就把孤岛算成"外部"，图案线不会穿洞。</para>
    /// 返回 false = 无法生成（无图案定义 / 行数或线段数超限），调用方回退边界轮廓。
    /// </summary>
    private bool AddHatchPatternLines(List<(SKPoint a, SKPoint b)> segs, SKRect bbox, Hatch h, uint color, in SKMatrix m)
    {
        var defs = h.Pattern?.Lines;
        if (defs == null || defs.Count == 0) { DiagHatchPatternFallback++; DiagHatchLastReason = "no-defs"; return false; }

        if (segs.Count == 0) { DiagHatchPatternFallback++; DiagHatchLastReason = "no-segs"; return false; }

        float pad = MathF.Max(bbox.Width, bbox.Height) * 0.05f + 1f;
        float minX = bbox.Left - pad, maxX = bbox.Right + pad;
        float minY = bbox.Bottom - pad, maxY = bbox.Top + pad;

        var pattern = new SKPath();
        var hits = new List<float>();
        int emitted = 0;
        int diagRows = 0, diagHits = 0, diagOdd = 0, diagEpsKill = 0;
        bool overflow = false;
        try
        {
            foreach (var def in defs)
            {
                double ang = def.Angle + h.PatternAngle;
                double dx = Math.Cos(ang), dy = Math.Sin(ang);
                double nx = -dy, ny = dx;
                double ox = def.Offset.X, oy = def.Offset.Y;
                double bx = def.BasePoint.X, by = def.BasePoint.Y;
                double offN = ox * nx + oy * ny;
                if (!double.IsFinite(offN) || Math.Abs(offN) < 1e-9) continue;

                double cN0 = minX * nx + minY * ny, cN1 = maxX * nx + minY * ny;
                double cN2 = minX * nx + maxY * ny, cN3 = maxX * nx + maxY * ny;
                double nMin = Math.Min(Math.Min(cN0, cN1), Math.Min(cN2, cN3));
                double nMax = Math.Max(Math.Max(cN0, cN1), Math.Max(cN2, cN3));
                if (!double.IsFinite(nMin) || !double.IsFinite(nMax) || nMax <= nMin) continue;
                double bN = bx * nx + by * ny;

                // 行号原点折进边界带（整数倍 offset 不改变行集合）；基点太远折不动时
                // （标准图案的历史垃圾坐标可达 1e81，double 加法都吃不掉 offset）
                // 以带中心重锚：只丢法向相位（≤半行距），行集合保持有界。
                double kFold = Math.Floor((nMin - bN) / offN);
                if (!double.IsFinite(kFold) || Math.Abs(kFold) > MaxHatchPatternRows)
                {
                    double cx = (minX + maxX) * 0.5, cy = (minY + maxY) * 0.5;
                    double cn = cx * nx + cy * ny;
                    bN = (nMin + nMax) * 0.5;
                    bx = cx + (bN - cn) * nx;
                    by = cy + (bN - cn) * ny;
                    kFold = 0;
                }
                else if (kFold != 0)
                {
                    bx += ox * kFold; by += oy * kFold; bN += offN * kFold;
                }

                long i0 = (long)Math.Floor((nMin - bN) / offN) - 1;
                long i1 = (long)Math.Ceiling((nMax - bN) / offN) + 1;
                if (i0 > i1) (i0, i1) = (i1, i0);
                if (i1 - i0 > MaxHatchPatternRows) { overflow = true; break; }

                var dashes = def.DashLengths;
                bool dashed = dashes != null && dashes.Count > 0;
                float cycle = 0f;
                if (dashed)
                    foreach (var d in dashes) cycle += MathF.Abs((float)d);

                for (long i = i0; i <= i1; i++)
                {
                    float rX = (float)(bx + ox * i), rY = (float)(by + oy * i);

                    hits.Clear();
                    foreach (var (a, b) in segs)
                    {
                        // 符号距离穿越法：交点由两端点在行法线上的符号距离线性插值，
                        // 不解方程组 → 行线与边共线/近共线（det≈0）时无假交点。
                        // d=0 归正侧（半开区间），顶点穿越/擦过的奇偶计数自洽。
                        double da = (a.X - rX) * nx + (a.Y - rY) * ny;
                        double db = (b.X - rX) * nx + (b.Y - rY) * ny;
                        bool aneg = da < 0, bneg = db < 0;
                        if (aneg == bneg) continue;
                        double tt = da / (da - db);
                        double px = a.X + (b.X - a.X) * tt - rX;
                        double py = a.Y + (b.Y - a.Y) * tt - rY;
                        hits.Add((float)(px * dx + py * dy));
                    }
                    if (hits.Count < 2) continue;
                    diagRows++;
                    diagHits += hits.Count;
                    hits.Sort();
                    if (hits.Count % 2 != 0)
                    {
                        DiagHatchOddRows++;
                        diagOdd++;
                        continue;
                    }

                    float eps = MathF.Max(bbox.Width, bbox.Height) * 1e-4f + 1e-5f;
                    float dotLen = MathF.Max(MathF.Sqrt(bbox.Width * bbox.Width + bbox.Height * bbox.Height) * 5e-4f, 1e-3f);
                    for (int k = 0; k + 1 < hits.Count; k += 2)
                    {
                        // 相邻交点几乎重合 = 行线擦过顶点，奇偶不可靠，丢弃该对
                        if (hits[k + 1] - hits[k] < eps) { diagEpsKill++; continue; }
                        emitted += EmitDashes(pattern, rX, rY, (float)dx, (float)dy, hits[k], hits[k + 1],
                            dashed, cycle, dashes, 1f, dotLen);
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
            DiagHatchLastReason = overflow
                ? $"overflow(H={h.Handle:X})"
                : $"empty(H={h.Handle:X}):segs={segs.Count} rows={diagRows} hits={diagHits} odd={diagOdd} epsKill={diagEpsKill} emitted={emitted}";
            return false;
        }
        DiagHatchPatternOk++;
        DiagHatchBoundarySegs = segs;
        DiagHatchPatternPath = new SKPath(pattern);
        AddStroke(pattern, h, color, m);
        return true;
    }

    /// <summary>区间 [s0,s1] 内按虚线表切段并写入 path，返回写入的段数（dashed=false 时整段直通）。</summary>
    /// <para>DWG 虚线表语义：正值=实线段，负值=空段，0=点。点按 <paramref name="dotLen"/> 长的
    /// 最短线段绘制（DisplayList 预转换、无缩放信息，取边界尺度的固定比例，放大后清晰可见）。</para>
    private static int EmitDashes(SKPath path, float rX, float rY, float dx, float dy,
        float s0, float s1, bool dashed, float cycle, System.Collections.Generic.List<double> dashes, float scale, float dotLen)
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
        long k0 = (long)MathF.Floor(s0 / cycle) - 1;
        long k1 = (long)MathF.Ceiling(s1 / cycle) + 1;
        if (k0 > k1) (k0, k1) = (k1, k0);
        if (k1 - k0 > 262144) return emitted;
        for (long k = k0; k <= k1; k++)
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
                else if (d == 0)
                {
                    float a = MathF.Max(s0, t), b = MathF.Min(s1, t + dotLen);
                    if (b > a) Emit(a, b);
                }
                t += len;
            }
        }
        return emitted;
    }

    /// <summary>
    /// 路径 → 每条子路径的压平点列（曲线按固定密度取样）。
    /// 与"线段表"版本的区别：保留子路径边界，调用方可以重建闭合轮廓
    /// （填充需要真正的多边形，一堆独立线段填不出面积）。
    /// </summary>
    private static List<List<SKPoint>> FlattenContours(SKPath path)
    {
        var result = new List<List<SKPoint>>();
        using var it = path.CreateRawIterator();
        var pts = new SKPoint[4];
        SKPoint current = default, start = default;
        var cur = new List<SKPoint>();
        SKPathVerb verb;

        void Flush()
        {
            if (cur.Count >= 3)
            {
                // 与起点重合的收尾点去掉（后续按环形取边）
                if (cur.Count > 3 && MathF.Abs(cur[^1].X - cur[0].X) < 1e-4f && MathF.Abs(cur[^1].Y - cur[0].Y) < 1e-4f)
                    cur.RemoveAt(cur.Count - 1);
                result.Add(cur);
            }
            cur = new List<SKPoint>();
        }

        void Poly(params SKPoint[] p)
        {
            foreach (var q in p)
            {
                if (cur.Count > 0)
                {
                    var l = cur[^1];
                    if (MathF.Abs(l.X - q.X) < 1e-6f && MathF.Abs(l.Y - q.Y) < 1e-6f) continue;
                }
                cur.Add(q);
            }
        }

        while ((verb = it.Next(pts)) != SKPathVerb.Done)
        {
            switch (verb)
            {
                case SKPathVerb.Move:
                    Flush();
                    current = start = pts[0];
                    cur.Add(current);
                    break;
                case SKPathVerb.Line:
                    Poly(pts[1]);
                    current = pts[1];
                    break;
                case SKPathVerb.Quad:
                {
                    var t = new List<SKPoint>();
                    SampleQuad(current, pts[1], pts[2], 12, t);
                    Poly(t.ToArray());
                    current = pts[2];
                    break;
                }
                case SKPathVerb.Conic:
                {
                    var t = new List<SKPoint>();
                    SampleConic(current, pts[1], pts[2], it.ConicWeight(), 16, t);
                    Poly(t.ToArray());
                    current = pts[2];
                    break;
                }
                case SKPathVerb.Cubic:
                {
                    var t = new List<SKPoint>();
                    SampleCubic(current, pts[1], pts[2], pts[3], 16, t);
                    Poly(t.ToArray());
                    current = pts[3];
                    break;
                }
                case SKPathVerb.Close:
                    if (cur.Count > 0) Poly(start);
                    Flush();
                    current = start;
                    break;
            }
        }
        Flush();
        return result;
    }

    private static void SampleQuad(SKPoint p0, SKPoint p1, SKPoint p2, int n, List<SKPoint> outp)
    {
        for (int j = 1; j <= n; j++)
        {
            float t = (float)j / n, mt = 1f - t;
            outp.Add(new SKPoint(
                mt * mt * p0.X + 2 * mt * t * p1.X + t * t * p2.X,
                mt * mt * p0.Y + 2 * mt * t * p1.Y + t * t * p2.Y));
        }
    }

    private static void SampleConic(SKPoint p0, SKPoint p1, SKPoint p2, float w, int n, List<SKPoint> outp)
    {
        for (int j = 1; j <= n; j++)
        {
            float t = (float)j / n, mt = 1f - t;
            float u0 = mt * mt, u1 = 2 * mt * t * w, u2 = t * t;
            float sum = u0 + u1 + u2;
            outp.Add(new SKPoint((u0 * p0.X + u1 * p1.X + u2 * p2.X) / sum,
                                 (u0 * p0.Y + u1 * p1.Y + u2 * p2.Y) / sum));
        }
    }

    private static void SampleCubic(SKPoint p0, SKPoint p1, SKPoint p2, SKPoint p3, int n, List<SKPoint> outp)
    {
        for (int j = 1; j <= n; j++)
        {
            float t = (float)j / n, mt = 1f - t;
            outp.Add(new SKPoint(
                mt * mt * mt * p0.X + 3 * mt * mt * t * p1.X + 3 * mt * t * t * p2.X + t * t * t * p3.X,
                mt * mt * mt * p0.Y + 3 * mt * mt * t * p1.Y + 3 * mt * t * t * p2.Y + t * t * t * p3.Y));
        }
    }

    // ---------- 文本 ----------

    /// <summary>
    /// TEXT（单行）→ <see cref="TextDraw"/>。
    ///
    /// 与 ODA/AutoCAD 对齐的口径（2026-10-20 定标）：
    /// <list type="bullet">
    ///   <item>字高 = **大写字高**（cap height），不是字体 em 尺寸 —— 见 <see cref="TextMetrics"/>；</item>
    ///   <item>宽度因子 = 组码 41（实体有效值，样式仅兜底）；</item>
    ///   <item>倾斜 = 组码 51（弧度，样式仅兜底）；</item>
    ///   <item>72/73 对齐：非「左 + 基线」时以组码 11 的定位点为锚，按 9 宫格算局部偏移。</item>
    /// </list>
    /// </summary>
    private void AddText(TextEntity t, in SKMatrix m, uint color)
    {
        double height = t.Height;

        // **字高/宽度因子/倾斜一律以实体值为准，样式参数不参与渲染** ——
        // 受控基准图 .compare/fixture_style.dxf 五组对照实证（同一张图两路渲染）：
        //   样式宽 0.5 + 实体 1.0 → ODA 按 1.0 画（293.2）；实体 0.5 → 147.6 ✔ 只有实体生效
        //   样式倾斜 30° + 实体 0 → ODA 不倾斜；实体倾斜 30° → bbox 变宽 339.9 ✔ 同上
        //   样式固定字高 40 + 实体 80 → ODA 按 80 画 ✔ 同上
        // 原因：AutoCAD 在创建文字时就把样式值抄进实体，DXF 的 40/41/51 是**有效值**；
        // 再乘/叠加样式值会双重压缩（0.7×0.7=0.49），字距与表格排版会整体错位。
        double widthFactor = t.WidthFactor > 0 ? t.WidthFactor : 1.0;
        double oblique = t.ObliqueAngle;   // ACadSharp 读出即为弧度

        AddTextEntity(t.Value, t.InsertPoint, t.AlignmentPoint, height, t.Rotation,
            widthFactor, oblique, t.HorizontalAlignment, t.VerticalAlignment, t, m, color);
    }

    /// <summary>
    /// MTEXT → <see cref="TextDraw"/>。
    /// 锚点按 <c>AttachmentPoint</c> 的 9 宫格换算成「基线左端」，与 TEXT 走同一条对齐管线。
    /// （换行/断行见 P2-4，本步不做宽度断行。）
    /// </summary>
    private void AddMText(MText mt, string? raw, in SKMatrix m, uint color, in MTextFormatResult fmt)
    {
        // MTEXT 没有"字宽因子"字段，宽度靠内联码 \W（字宽）与 \T（字间距）表达。
        // **\T 不进字宽**：AutoCAD 的 \T 只压字与字之间的间隙，不压字形本身。
        // 受控基准 `mt.dxf`（`\W0.8\T0.75;` / `\W0.8;` / 无 / `\T0.75;` 四条同一句话）
        // 以 ODA 反解（按 30 字高的行距归一化到世界单位）：
        //   \W0.8 把宽度压到 0.80，\T0.75 只压到 0.936 —— 说明它作用在"字距"上，
        //   而折进字宽会压到 0.75，比 AutoCAD 窄 20%（图签整段会明显偏瘦）。
        //
        // **没写 \W 时必须回落到 STYLE 的组码 41**（2026-11-14 受控基准 `.compare/mtstyle.dxf`）：
        //   M1 样式1.0 无\W → ODA 300｜M2 样式0.5 无\W → ODA 150（我方当时画 300，宽一倍）
        //   M3 样式1.0 \W0.5 → ODA 150｜M4 样式0.5 \W0.5 → ODA 150
        //   → \W 是**覆盖**样式宽度，不是叠乘。同图 9---陶板节点 (05).dwg 的标注样式 SGT-S
        //     宽度 0.300、全部 DIMENSION 的 MTEXT 都没写 \W，于是"30/100/DIM"被画宽 3.3 倍。
        double wfWidth = fmt.WidthExplicit
            ? Math.Max(0.05, fmt.WidthFactor)
            : Math.Max(0.05, StyleWidthOf(mt));
        double widthFactor = s_mtextFoldTracking ? Math.Max(0.05, wfWidth * fmt.Tracking) : wfWidth;
        double oblique = 0;
        double height = mt.Height * Math.Max(0.05, fmt.HeightFactor);

        // 断行：组码 41（RectangleWidth）是 AutoCAD 的排版栏宽，超过就折行 ----
        // 断行按**本条文字实际会用的字体**逐字量宽：用默认字体的宽度断行，
        // 换成宋体之后行会断得太早（宋体比雅黑窄两成），排版立刻跑偏。
        var text = raw ?? string.Empty;
        var (wrapAnsi, wrapBig, _) = StyleFontsOf(mt);
        var textFont = TextFontLibrary.Resolve(wrapAnsi, wrapBig, text);
        float rectWidth = (float)mt.RectangleWidth;
        if (rectWidth > 0 && height > 0 && text.Length > 0)
            // 断行必须带 **\W 字宽因子**：栏宽是"排版宽度"，而字宽被 \W 压缩后同样的栏宽
            // 能放更多字。忽略 \W 会让整段提前折行 —— 实测 52陶板节点/9---陶板节点 (05).dwg
            // 图签"备注"块：\W0.8 时每行只排 21 字（应为 25 字），整块多出 7 行、长度多 43%，
            // 与 ODA 差出半个图签格。\T（字间距）不参与：CJK 字距本来就是全角，
            // ODA 反解得 \W0.8 的每字步进 = em×0.8，没有 \T 那一层。
            //
            // **必须传 widthFactor 而不是 wfWidth**（2026-11-14 修）：\T 折进绘制宽度后
            // 实际画字用的是 wfWidth×Tracking（备注块 = 0.8×0.8 = 0.64），断行若仍按 0.8 量，
            // 会以为每行更宽 → 提前折行。实测该块折成 19 行（ODA 10 行）、整块多出 9 列、
            // 与 ODA 差出半个图签格。断行与画字必须同一个字宽。
            text = TextWrap.Wrap(text, (float)height, rectWidth, (float)widthFactor, textFont);

        // 行距：AutoCAD 单倍 = 1.667 × 字高；组码 44（LineSpacing）是倍数，默认 1.0
        float spacingFactor = TextMetrics.LineSpacingFactor
                              * (mt.LineSpacing > 0 ? (float)mt.LineSpacing : 1f);
        int lineCount = 1;
        foreach (char ch in text) if (ch == '\n') lineCount++;

        // 1..9 = 左上→右下；列决定水平对齐，行决定垂直对齐
        int ap = (int)mt.AttachmentPoint;
        if (ap < 1 || ap > 9) ap = 1;
        int col = (ap - 1) % 3, row = (ap - 1) / 3;
        var h = col switch
        {
            1 => TextHorizontalAlignment.Center,
            2 => TextHorizontalAlignment.Right,
            _ => TextHorizontalAlignment.Left,
        };
        var v = row switch
        {
            1 => TextVerticalAlignmentType.Middle,
            2 => TextVerticalAlignmentType.Bottom,
            _ => TextVerticalAlignmentType.Top,   // MTEXT 默认左上 = 首行顶部，不是基线
        };

        // MTEXT 的水平对齐作用于**每一行**（AutoCAD 行为），因此交给渲染端逐行对齐，
        // 锚点偏移不再承担水平量；垂直方向仍按整块高度算首行基线。
        var lineAlign = h switch
        {
            ACadSharp.Entities.TextHorizontalAlignment.Center => TextLineAlign.Center,
            ACadSharp.Entities.TextHorizontalAlignment.Right => TextLineAlign.Right,
            _ => TextLineAlign.Left,
        };

        // 注意：MTEXT 的组码 11 是「X 轴方向向量」而不是对齐点（ACadSharp 也照此映射到
        // AlignmentPoint），因此锚点恒为 InsertPoint，垂直/水平对齐由 9 宫格偏移表达。
        AddTextEntity(text, mt.InsertPoint, mt.InsertPoint, height, mt.Rotation,
            widthFactor, oblique, h, v, mt, m, color,
            lineCount: lineCount, lineSpacingFactor: spacingFactor,
            lineAlign: lineAlign, alignPerLine: true, font: textFont);
    }

    /// <summary>
    /// 文本实体 → <see cref="TextDraw"/>。转换阶段完成全部布局计算（对齐偏移、两点定宽），
    /// 渲染端只负责「从 Position 按 Rotation 画、套 WidthFactor 与 Oblique」。
    /// </summary>
    private void AddTextEntity(string? raw, XYZ insert, XYZ align, double height, double rotation,
        double widthFactor, double obliqueRadians,
        ACadSharp.Entities.TextHorizontalAlignment h, TextVerticalAlignmentType v,
        Entity e, in SKMatrix m, uint color,
        int lineCount = 1, float lineSpacingFactor = TextMetrics.LineSpacingFactor,
        TextLineAlign lineAlign = TextLineAlign.Left, bool alignPerLine = false,
        TextFont? font = null)
    {
        if (string.IsNullOrWhiteSpace(raw) || height <= 0) { Skipped++; return; }

        // 统一换行符，供测量与渲染共用（MTEXT 的 \P 已在 CleanMText 中转为 \n）；
        // %% 控制码也在这里展开，保证"量宽度"和"描边"看到的是同一串字符。
        var text = TextCodes.Expand(raw.Replace("\r\n", "\n").Replace('\r', '\n'));
        if (text.TrimEnd('\n').Length == 0) { Skipped++; return; }

        float wf = (float)(widthFactor > 0 ? widthFactor : 1.0);
        float obl = (float)obliqueRadians;

        var basePt = new SKPoint((float)insert.X, (float)insert.Y);
        var alignPt = new SKPoint((float)align.X, (float)align.Y);

        // 需要定位点（组码 11）的对齐方式：中心/右/中/两点式
        bool needsAlignPoint = h != ACadSharp.Entities.TextHorizontalAlignment.Left
                               || v != TextVerticalAlignmentType.Baseline;

        // 老图纸常常写了 72/73 却漏写组码 11（读到 0,0），此时若直接用会把文字甩到原点。
        // 判据：定位点在原点而插入点不在 → 视为缺失，退回插入点并把对齐降级为左/基线。
        if (needsAlignPoint && IsOrigin(alignPt) && !IsOrigin(basePt))
        {
            needsAlignPoint = false;
            h = ACadSharp.Entities.TextHorizontalAlignment.Left;
            v = TextVerticalAlignmentType.Baseline;
        }

        // ---- 两点定宽（Aligned / Fit）：方向与宽度都由「插入点 → 定位点」决定 ----
        float extraRot = 0f;
        float alignWidth = -1f;
        if (h is ACadSharp.Entities.TextHorizontalAlignment.Aligned
              or ACadSharp.Entities.TextHorizontalAlignment.Fit)
        {
            float dx0 = alignPt.X - basePt.X, dy0 = alignPt.Y - basePt.Y;
            float dist = MathF.Sqrt(dx0 * dx0 + dy0 * dy0);
            if (dist > 1e-6f)
            {
                extraRot = MathF.Atan2(dy0, dx0) - (float)rotation;
                alignWidth = dist;
            }
        }

        float capE = (float)height;

        // ---- SHX 单线字体优先 ----
        // 样式指向可用的 .shx 时必须走它：TTF 回退的字宽与字形都对不上（受控基准实测
        // 数字窄 17.5%、W 宽 20%），而 SHX 本身就是折线，描出来就是 AutoCAD 的形状。
        var (styleFont, styleBig, styleVertical) = StyleFontsOf(e);

        // TTF 字体族（P2-16）：样式写的就是 TTF、或 SHX 缺字库落到这条路上时，
        // 按样式解析出真正的字体（含"要不要中文字形"的判断）。
        // 这份对象一路带到 TextDraw，保证**包围盒与字形用的是同一份度量**。
        font ??= TextFontLibrary.Resolve(styleFont, styleBig, text);
        // 样式写 TTF（ARIAL.TTF / simhei.ttf）的不算 SHX，也不该记"缺字库"
        bool wantsShx = ShxFontLibrary.IsShxName(styleFont) || ShxFontLibrary.IsShxName(styleBig);
        // 竖排：样式勾了"垂直"才排竖排，且要求字体支持（西文看 modes bit2，大字体都支持）。
        // 不支持就按横排画 —— ODA 对 gbenor 这类没有竖排变体的字体也是这么处理的。
        bool vertical = styleVertical && wantsShx && ShxFontLibrary.SupportsVertical(styleFont, styleBig);
        float shxWidth = 0f;
        bool shxMeasured = wantsShx &&
            ShxFontLibrary.TryMeasure(text, styleFont, styleBig, capE, out shxWidth, vertical);
        if (wantsShx && !shxMeasured) ShxTextMissingFont++;

        float measured = (shxMeasured ? shxWidth : font.MeasureWidth(text, capE)) * wf;
        if (alignWidth > 0 && measured > 1e-6f && !vertical)
        {
            float ratio = alignWidth / measured;
            if (h == ACadSharp.Entities.TextHorizontalAlignment.Aligned)
            {
                // 两点对齐：字高等比缩放，宽度因子保持
                capE *= ratio;
                measured = alignWidth;
            }
            else
            {
                // 布满两点：只改宽度因子
                wf *= ratio;
                measured = alignWidth;
            }
        }

        // 降部深度只影响"底部对齐"的上抬量与块高；SHX 那条路仍用默认字体的比例
        // （SHX 字库自己的 below 已在字形里，包围盒口径不能跟着换，否则老断言全变）
        float desc = shxMeasured ? TextMetrics.DescenderDepth(text, capE) : font.DescenderDepth(text, capE);

        // ---- 9 宫格局部偏移（世界坐标，y 向上；文本从基线左端向右上延伸）----
        // 逐行对齐时水平量由渲染端按行分配（MTEXT），这里不再偏移，
        // 否则"块居中"与"行居中"会叠加成二次偏移。
        //
        // 竖排要换一套口径：整串从锚点**向下**排，"长度" measured 落在 y 上、列宽就是字高。
        float offX, offY;
        if (vertical)
        {
            // 列宽按字高估（各字形墨迹宽窄不同，但整列宽度就是一个字身）；
            // 横向对齐移动整列，纵向对齐移动"整串的起笔位置"。
            float colW = capE;
            offX = alignPerLine ? 0f : h switch
            {
                ACadSharp.Entities.TextHorizontalAlignment.Center => -colW * 0.5f,
                ACadSharp.Entities.TextHorizontalAlignment.Middle => -colW * 0.5f,
                ACadSharp.Entities.TextHorizontalAlignment.Right => -colW,
                _ => 0f,
            };
            offY = v switch
            {
                TextVerticalAlignmentType.Top => 0f,        // 顶部 = 起笔就在锚点，往下排
                TextVerticalAlignmentType.Middle => -measured * 0.5f,
                TextVerticalAlignmentType.Bottom => -measured,
                _ => 0f,                                     // 基线 = 同上（竖排没有传统基线）
            };
        }
        else
        {
            offX = alignPerLine ? 0f : h switch
            {
                ACadSharp.Entities.TextHorizontalAlignment.Center => -measured * 0.5f,
                ACadSharp.Entities.TextHorizontalAlignment.Middle => -measured * 0.5f,
                ACadSharp.Entities.TextHorizontalAlignment.Right => -measured,
                _ => 0f,
            };

            // 多行文本的垂直对齐按**整块**算（AutoCAD MTEXT 的 Middle/Bottom 指整个文字块）：
            //   块高 = 字高 + (n-1)×行距 + 降部
            //   Top    → 首行基线 = 锚点 − 字高（首行 cap 顶端贴锚点）
            //   Middle → 首行基线 = 锚点 − 字高 + 块高/2
            //   Bottom → 首行基线 = 锚点 + (n-1)×行距 + 降部
            float lineH = capE * lineSpacingFactor;
            float blockH = capE + (lineCount - 1) * lineH + desc;
            offY = v switch
            {
                TextVerticalAlignmentType.Top => -capE,             // 顶部对齐 → 基线在锚点下方一个字高
                TextVerticalAlignmentType.Middle => -capE + blockH * 0.5f,
                TextVerticalAlignmentType.Bottom => (lineCount - 1) * lineH + desc,
                _ => 0f,                                            // 基线对齐
            };
        }

        var anchor = needsAlignPoint ? alignPt : basePt;
        float rotEntity = (float)rotation + extraRot;
        float cos = MathF.Cos(rotEntity), sin = MathF.Sin(rotEntity);
        var entityPos = new SKPoint(
            anchor.X + offX * cos - offY * sin,
            anchor.Y + offX * sin + offY * cos);

        // ---- 块变换 ----
        float det = m.ScaleX * m.ScaleY - m.SkewX * m.SkewY;
        float scale = MathF.Sqrt(MathF.Abs(det));
        if (float.IsNaN(scale) || scale <= 0) scale = 1f;
        float blockRot = MathF.Atan2(m.SkewY, m.ScaleX);

        var worldPos = TransformPoint(m, entityPos);
        float worldCap = capE * scale;
        float rotTotal = blockRot + rotEntity;

        // ---- SHX 文字：排成描边几何（局部 → 世界）----
        // 变换顺序必须与 TTF 那条路一致：先宽度因子、再倾斜、再旋转、最后平移
        // （对应 DrawText 里 canvas.Scale(wf) / Skew / Rotate / Translate 的次序）。
        if (shxMeasured &&
            ShxFontLibrary.TryShape(text, styleFont, styleBig, worldCap,
                                    lineSpacingFactor, lineAlign, out var shaped, vertical))
        {
            var sp = shaped.Path;
            sp.Transform(SKMatrix.CreateScale(wf, 1f));
            if (MathF.Abs(obl) > 1e-6f) sp.Transform(SKMatrix.CreateSkew(MathF.Tan(obl), 0f));
            if (MathF.Abs(rotTotal) > 1e-6f) sp.Transform(SKMatrix.CreateRotation(rotTotal));
            sp.Transform(SKMatrix.CreateTranslation(worldPos.X, worldPos.Y));

            var shxBounds = sp.Bounds;
            if (!InsideClip(shxBounds)) { sp.Dispose(); ClippedAway++; return; }

            _entities.Add(new CadEntity
            {
                Layer = LayerName(e),
                ColorArgb = color,
                Handle = _currentHandle,
                OwnHandle = _currentOwnHandle,
                Path = sp,
                TextStrokes = true,
                LineWeightMm100 = ResolveLineWeight(e, _inheritedLineWeight),
                Bounds = shxBounds,
            });
            ShxTextEntities++;
            if (vertical) ShxVerticalTextEntities++;
            return;
        }

        // 走到这儿说明用的是 TTF 那条路 —— 记一笔，报告里能看到每张图实际用了哪些字体
        NoteFontUse(font);

        // 逐行对齐时包围盒按"整块矩形"算（各行首尾落在 ±块宽/2 或 −块宽 上）；
        // 否则沿用字形级精确包围盒（TEXT 的口径不变，历史断言依赖它）。
        SKRect local;
        if (alignPerLine)
        {
            float left = lineAlign switch
            {
                TextLineAlign.Center => -measured * 0.5f,
                TextLineAlign.Right => -measured,
                _ => 0f,
            };
            float bottomLocal = -(lineCount - 1) * (worldCap * lineSpacingFactor)
                                - font.DescenderDepth(text, worldCap);
            local = new SKRect(left, bottomLocal, left + measured, worldCap);
        }
        else
        {
            local = font.MeasureLocalBounds(text, worldCap, wf, obl);
        }
        var bounds = TextMetrics.ToWorldBounds(local, worldPos, rotTotal);

        // 视口裁剪：文字不在裁剪区内就整条跳过（压边的少量溢出可接受，
        // 逐字裁剪要拆字形，代价远高于收益）
        if (!InsideClip(bounds)) { ClippedAway++; return; }

        _entities.Add(new CadEntity
        {
            Layer = LayerName(e),
            ColorArgb = color,
            Handle = _currentHandle,
            OwnHandle = _currentOwnHandle,
            Text = new TextDraw
            {
                Text = text,
                Position = worldPos,
                Height = worldCap,
                RotationRadians = rotTotal,
                WidthFactor = wf,
                ObliqueRadians = obl,
                LineSpacingFactor = lineSpacingFactor,
                LineAlign = lineAlign,
                Font = font,
            },
            Bounds = bounds,
        });
    }

    private static bool IsOrigin(SKPoint p) => MathF.Abs(p.X) < 1e-9f && MathF.Abs(p.Y) < 1e-9f;

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
        // 不打印图层的判据要先看整表占比：ACadSharp 对部分文件（Revit/外部导出）会把
        // 大部分甚至**全部**图层读成 PlotFlag=false（实证 AD-103-三层平面图 58/58），
        // 照此隐藏会整张图空白。超过 3/4 就认为这份 PlotFlag 不可信，整表放弃。
        int layerTotal = 0, noPlotTotal = 0;
        foreach (var layer in doc.Layers)
        {
            layerTotal++;
            if (!layer.PlotFlag) noPlotTotal++;
        }
        bool trustPlotFlag = HideNonPlotLayers && noPlotTotal * 4 <= layerTotal * 3;

        foreach (var layer in doc.Layers)
        {
            var name = layer.Name ?? string.Empty;
            _layerColors[name] = ToArgb(layer.Color, 7);
            _layerOrder.Add(name);

            // 图层线宽（1/100 mm）：图层的线宽也可能是 ByLayer/Default，此时用默认 0.25mm
            int lw = Mm100(layer.LineWeight);
            _layerWeights[name] = lw > 0 ? lw : DefaultLineWeightMm100;

            if (!layer.PlotFlag) NonPlottingLayers++;

            // 图层被关闭（IsOn=false）或冻结 → 打开图纸时默认隐藏
            if (!layer.IsOn || layer.Flags.HasFlag(LayerFlags.Frozen))
                _initiallyHidden.Add(name);
            else if (trustPlotFlag && !layer.PlotFlag)
            {
                // 不打印图层（AutoCAD 的 DEFPOINTS 就是这一类）→ 也默认隐藏。
                //
                // 判据来自 ODA 对照（2026-10-21，3、幕墙横方总汇.dwg）：该图 15,992 个 POINT
                // 全部落在 DEFPOINTS 上，Teigha 出图里一个都看不到，而我们按 PDMODE/PDSIZE
                // 画出来是满屏的点阵 —— 说明参照实现走的是**打印语义**。
                // 处理成"默认隐藏、可在图层面板打开"，比直接丢弃更保守：
                // 编辑器里核对时仍能看到，也能随时对比有无。
                //
                // 2026-10-23：加整表占比守卫（见 trustPlotFlag）。ACadSharp 对部分外部导出图
                // 会把全部图层读成不打印，照此隐藏会整张空白；Teigha 对这类图照样显示内容。
                _initiallyHidden.Add(name);
            }
        }
    }

    /// <summary>autoCAD 默认线宽 0.25mm（头变量 LWDEFAULT；ACadSharp 未暴露，用行业默认值）。</summary>
    private const int DefaultLineWeightMm100 = 25;

    /// <summary>
    /// 缓存文字样式 → 字库文件名（+ 竖排标志）。
    ///
    /// <para>**必须走 doc.TextStyles 而不是实体的 Style 属性**：DWG 里的实体只带样式名，
    /// ACadSharp 交出来的 Style 往往是只填了 Name 的占位对象，直接读它的 Filename 永远拿到空。</para>
    /// </summary>
    private void CacheTextStyles(CadDocument doc)
    {
        _styleFonts.Clear();
        _styleWidths.Clear();
        try
        {
            foreach (var st in doc.TextStyles)
            {
                string name = st.Name ?? string.Empty;
                if (name.Length == 0) continue;
                // 竖排是**样式级**开关（DXF STYLE 组码 70 的 bit4），不是字体名的 '@' 前缀 ——
                // '@' 那套是 TrueType 字体的约定，SHX 靠这个标志位 + 字体自带的 modes bit2。
                bool vertical = (st.Flags & StyleFlags.VerticalText) != 0;
                _styleFonts[name] = (st.Filename ?? string.Empty, st.BigFontFilename ?? string.Empty, vertical);
                // 组码 41 = 宽度因子；0/负数在 AutoCAD 里按 1.0 处理
                _styleWidths[name] = st.Width > 0.01f ? (float)Math.Clamp(st.Width, 0.01f, 100f) : 1f;
            }
        }
        catch
        {
            // 样式表损坏不影响其他实体
        }
    }

    /// <summary>取实体所用样式的 (字体, 大字体, 竖排) ；取不到返回空串 + 非竖排。</summary>
    private (string Font, string Big, bool Vertical) StyleFontsOf(Entity e)
    {
        string styleName = StyleNameOf(e);
        return styleName.Length > 0 && _styleFonts.TryGetValue(styleName, out var f)
            ? f
            : (string.Empty, string.Empty, false);
    }

    /// <summary>取实体所用样式名（DWG 实体只带名字，样式表才有效）。</summary>
    private static string StyleNameOf(Entity e)
        => (e switch
        {
            AttributeEntity a => a.Style?.Name,   // 必须排在 TextEntity 前面：它是其子类
            TextEntity t => t.Style?.Name,
            MText m => m.Style?.Name,
            _ => null,
        }) ?? string.Empty;

    /// <summary>
    /// 取实体所用样式的宽度因子（组码 41）；样式缺失时返回 1.0。
    /// 只给 MTEXT 用 —— TEXT/ATTDEF 有实体级 41，样式值在 AutoCAD 建字时已抄进实体。
    /// </summary>
    private float StyleWidthOf(Entity e)
    {
        string styleName = StyleNameOf(e);
        return styleName.Length > 0 && _styleWidths.TryGetValue(styleName, out var w) ? w : 1f;
    }

    /// <summary>
    /// 记一条"某个字体画了多少条文字"。名字按来源标注，报告里一眼能看出有没有落到兜底。
    /// </summary>
    private void NoteFontUse(TextFont font)
    {
        string key = font.Kind switch
        {
            TextFontKind.TrueType => font.Name,
            TextFontKind.ShxStandIn => $"{font.Name}（SHX 替身）",
            TextFontKind.Substitute => $"{font.Name}（顶替）",
            _ => $"{font.Name}（默认兜底）",
        };
        _fontUsage[key] = _fontUsage.TryGetValue(key, out var n) ? n + 1 : 1;
    }

    /// <summary>
    /// <see cref="LineWeightType"/> → 1/100 mm。
    /// 枚举名直接编码了线宽（W25 = 0.25mm、W211 = 2.11mm），负值那几种是"随层/随块/默认/设备像素"。
    /// </summary>
    private static int Mm100(LineWeightType lw) => lw switch
    {
        LineWeightType.ByLayer => -1,
        LineWeightType.ByBlock => -2,
        LineWeightType.Default => -3,
        LineWeightType.ByDIPs => -4,
        _ => int.TryParse(lw.ToString().AsSpan(1), out var v) ? v : -3,
    };

    /// <summary>
    /// 解析实体线宽（1/100 mm）。ByBlock 继承块引用线宽，ByLayer 取图层线宽，
    /// Default 用 0.25mm。返回 0 = 不显示线宽（按细线画）。
    /// </summary>
    private int ResolveLineWeight(Entity e, int inheritedMm100)
    {
        int lw = Mm100(e.LineWeight);
        switch (lw)
        {
            case -2: return inheritedMm100 > 0 ? inheritedMm100 : DefaultLineWeightMm100;   // ByBlock
            case -4: return 0;                                                              // ByDIPs：按像素，不参与
            case -1:
            {
                var name = LayerName(e);
                if (_layerWeights.TryGetValue(name, out var v)) return v;
                return DefaultLineWeightMm100;
            }
            case -3: return DefaultLineWeightMm100;
            default: return lw;
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

    private void AddStroke(SKPath path, Entity e, uint color, in SKMatrix m)
    {
        var ocs = CadGeometry.OcsToWcs(e);
        if (!ocs.IsIdentity) path.Transform(ocs);
        path.Transform(m);
        // 裁剪不在转换期做（SKPath.Op 会把开放的线条整段删掉），改为给图元挂裁剪区域、
        // 渲染期用画布裁剪 —— 见 ApplyScopeClip / CadEntity.ClipPath。
        var bounds = path.Bounds;
        if (float.IsNaN(bounds.Left)) { path.Dispose(); Skipped++; return; }

        // 线宽：实体/图层/块的解析结果（1/100 mm），换算成屏幕像素留给渲染期
        // （模型空间是固定像素、图纸空间按真实单位，见 CadEntity.LineWeightMm100）。
        int lw100 = ResolveLineWeight(e, _inheritedLineWeight);
        if (lw100 > 0) LineWeightEntities++;

        _entities.Add(new CadEntity
        {
            Layer = LayerName(e),
            ColorArgb = color,
            Handle = _currentHandle,
            OwnHandle = _currentOwnHandle,
            Path = path,
            LineWeightMm100 = lw100,
            DashPattern = ResolveDashPattern(e, m),
            Bounds = bounds,
        });
    }

    /// <summary>
    /// 图纸空间里「1 mm = 多少图纸单位」。按 1:1 mm 出图时取 1；英制或缩放图纸可调。
    /// 只影响**布局**里的线宽显示（那里线宽按真实单位画）；模型空间是固定像素宽，用不到它。
    /// </summary>
    public float WorldUnitsPerMm { get; set; } = 1f;

    private static bool IsByLayerLineType(ACadSharp.Tables.LineType? lt)
    {
        if (lt is null) return true;
        var n = lt.Name;
        return string.IsNullOrEmpty(n) || n.Equals("ByLayer", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 解析实体的显示线型为 Skia 虚线间隔（on/off 交替，世界单位）。
    /// 实线（Continuous / ByBlock / 无段 / 全为实段）返回 null。
    ///
    /// <para>**ByLayer 必须回落到图层的线型**：ACadSharp 对组码 6 = <c>ByLayer</c> 的实体
    /// 返回一个**名叫 ByLayer、段表为空**的 Linetype 对象（不是 null），照着它算就是"实线"。
    /// 实测 `9---陶板节点 (05).dwg` 的 `00-SGT-透视虚线`：86 处隐藏边全部画成实线，
    /// 而图层线型是 DASHED —— 剖面图里最显眼的那批虚线整批丢失。</para>
    /// </summary>
    private float[]? ResolveDashPattern(Entity e, in SKMatrix m)
    {
        var lt = e.LineType;
        if (IsByLayerLineType(lt)) lt = e.Layer?.LineType;   // 组码 6 = ByLayer → 用图层线型
        if (lt == null || IsByLayerLineType(lt) || lt.Segments == null) return null;

        double unit = _globalLtScale * (e.LineTypeScale > 0 ? e.LineTypeScale : 1.0);
        double det = Math.Abs((double)m.ScaleX * m.ScaleY - (double)m.SkewX * m.SkewY);
        if (det > 0 && !double.IsNaN(det) && !double.IsInfinity(det)) unit *= Math.Sqrt(det);
        if (unit <= 0) return null;

        var segs = lt.Segments.ToList();
        if (segs.Count == 0) return null;

        double total = 0;
        foreach (var s in segs) total += Math.Abs(s.Length);
        if (total <= 0) return null;

        // 段长带符号（负 = 空档），同时用 IsSpace 兜底；点段按极短实段画，保证交替序列合法。
        var list = new List<double>();
        foreach (var s in segs)
        {
            double len = Math.Abs(s.Length) * unit;
            bool on = !s.IsSpace && !s.IsText && !s.IsShape;
            if (on && len <= 0) len = Math.Max(total * unit * 0.02, 1e-6);
            if (len <= 0) continue;
            bool expectOn = list.Count % 2 == 0;
            if (expectOn == on) list.Add(len);
            else if (list.Count > 0) list[^1] += len;   // 同类型相邻 → 合并，保持 on/off 交替
            else list.Add(len);
        }
        if (list.Count < 2) return null;

        var arr = new float[list.Count];
        for (int i = 0; i < list.Count; i++) arr[i] = (float)Math.Max(list[i], 1e-6);
        return PoolDash(arr);
    }

    /// <summary>等值线型复用同一数组实例（烘焙按引用分桶、渲染按引用缓存路径效果）。</summary>
    private float[] PoolDash(float[] pattern)
    {
        var key = string.Join("|", Array.ConvertAll(pattern, v => v.ToString("R")));
        if (_dashPool.TryGetValue(key, out var hit)) return hit;
        _dashPool[key] = pattern;
        return pattern;
    }

    private void AddFill(SKPath path, Entity e, uint color, in SKMatrix m)
    {
        var ocs = CadGeometry.OcsToWcs(e);
        if (!ocs.IsIdentity) path.Transform(ocs);
        path.Transform(m);
        // 裁剪不在转换期做（SKPath.Op 会把开放的线条整段删掉），改为给图元挂裁剪区域、
        // 渲染期用画布裁剪 —— 见 ApplyScopeClip / CadEntity.ClipPath。
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
