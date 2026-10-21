using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using CSMath;

// 样例图纸生成器。
//
// 动机：原来只有 samples/sample_r12.dxf（2 条线 + 圆 + 弧 + 文字 + 填充），
// 15 类受支持实体里有 10 类**零测试覆盖**，导致"改坏了也发现不了"。
// 本工具生成一份覆盖全部实体类型 + 各类边界情况的样例，供 SmokeTest 做几何断言与回归。
//
// 用法：dotnet run --project tools/SampleGen [输出路径]
// 重新生成后请提交 samples/sample_features.dxf（SmokeTest 依赖它）。

var outPath = args.Length > 0 ? args[0] : DefaultOutputPath();
Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

var doc = new CadDocument();

// ---------- 图层 ----------
// "0" 由 CadDocument 自动创建
var layerOutline = new Layer("轮廓") { Color = new Color((short)1) };          // ACI 1 = 红
var layerFrozen = new Layer("冻结层") { Flags = LayerFlags.Frozen };           // 冻结 → 打开时应默认隐藏
var layerOff = new Layer("关闭层") { IsOn = false };                           // 关闭 → 打开时应默认隐藏
doc.Layers.Add(layerOutline);
doc.Layers.Add(layerFrozen);
doc.Layers.Add(layerOff);

// ---------- 基本实体 ----------

// 1. LINE：从 (0,0) 到 (100,0)，落在"轮廓"图层 → 颜色应解析为红色（BYLAYER）
doc.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(100, 0, 0)) { Layer = layerOutline });

// 2. CIRCLE：圆心 (50,50) 半径 20
doc.Entities.Add(new Circle { Center = new XYZ(50, 50, 0), Radius = 20, Layer = layerOutline });

// 3. ARC：圆心 (0,60) 半径 15，0° → 90°（世界系逆时针四分之一圆）
doc.Entities.Add(new Arc
{
    Center = new XYZ(0, 60, 0),
    Radius = 15,
    StartAngle = 0,
    EndAngle = Math.PI / 2,
});

// 4. ELLIPSE：整椭圆，长轴半径 20、短轴比 0.5
doc.Entities.Add(new Ellipse
{
    Center = new XYZ(0, 100, 0),
    MajorAxisEndPoint = new XYZ(20, 0, 0),
    RadiusRatio = 0.5,
    StartParameter = 0,
    EndParameter = Math.PI * 2,
});

// 5. LWPOLYLINE：带 bulge 的折线（bulge=1 表示半圆），并闭合
var lw = new LwPolyline { IsClosed = true };
lw.Vertices.Add(new LwPolyline.Vertex(new XY(0, 150)) { Bulge = 1.0 });
lw.Vertices.Add(new LwPolyline.Vertex(new XY(20, 150)) { Bulge = 0 });
lw.Vertices.Add(new LwPolyline.Vertex(new XY(20, 170)) { Bulge = 0 });
doc.Entities.Add(lw);

// 6. POLYLINE2D：老式折线（R12 风格）
var p2 = new Polyline2D();
p2.Vertices.Add(new Vertex2D { Location = new XYZ(40, 150, 0) });
p2.Vertices.Add(new Vertex2D { Location = new XYZ(60, 150, 0) });
p2.Vertices.Add(new Vertex2D { Location = new XYZ(60, 170, 0) });
doc.Entities.Add(p2);

// 7. SPLINE：3 次样条，4 个控制点（knot 数须为 n + degree + 1 = 8）
var spline = new Spline { Degree = 3 };
spline.ControlPoints.Add(new XYZ(80, 150, 0));
spline.ControlPoints.Add(new XYZ(90, 175, 0));
spline.ControlPoints.Add(new XYZ(105, 145, 0));
spline.ControlPoints.Add(new XYZ(120, 170, 0));
foreach (var k in new double[] { 0, 0, 0, 0, 1, 1, 1, 1 })
    spline.Knots.Add(k);
doc.Entities.Add(spline);

// 8. TEXT：单行
doc.Entities.Add(new TextEntity
{
    Value = "TEXT 单行",
    InsertPoint = new XYZ(0, 200, 0),
    Height = 5,
});

// 9. MTEXT：多行（\P 是 AutoCAD 的换行符，转换器会转成 \n）
doc.Entities.Add(new MText
{
    Value = "第一行\\P第二行\\P第三行",
    InsertPoint = new XYZ(0, 220, 0),
    Height = 5,
});

// 10. POINT
doc.Entities.Add(new ACadSharp.Entities.Point { Location = new XYZ(100, 200, 0) });

// 11. SOLID：填充四边形
doc.Entities.Add(new Solid
{
    FirstCorner = new XYZ(120, 195, 0),
    SecondCorner = new XYZ(140, 195, 0),
    ThirdCorner = new XYZ(140, 205, 0),
    FourthCorner = new XYZ(120, 205, 0),
});

// 12. HATCH：实心填充圆
var hatch = new Hatch { IsSolid = true };
var hatchPath = new Hatch.BoundaryPath();
hatchPath.Edges.Add(new Hatch.BoundaryPath.Arc
{
    Center = new XY(150, 200),
    Radius = 8,
    CounterClockWise = true,
    StartAngle = 0,
    EndAngle = Math.PI * 2,
});
hatch.Paths.Add(hatchPath);
doc.Entities.Add(hatch);

// 13. LEADER：引线
var leader = new Leader();
leader.Vertices.Add(new XYZ(150, 150, 0));
leader.Vertices.Add(new XYZ(170, 150, 0));
leader.Vertices.Add(new XYZ(180, 165, 0));
doc.Entities.Add(leader);

// ---------- 块与插入 ----------

// 14. INSERT：块内实体颜色分别为 BYBLOCK（应继承插入颜色）与 BYLAYER（应用自身图层色）
var valve = new BlockRecord("VALVE");
valve.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(10, 0, 0))
{
    Color = Color.ByBlock,       // 关键：BYBLOCK 必须继承插入实体的颜色
});
valve.Entities.Add(new Circle
{
    Center = new XYZ(5, 5, 0),
    Radius = 3,
    // 颜色留默认 BYLAYER → 块定义在 "0" 层 → 白色
});
doc.BlockRecords.Add(valve);

var insert = new Insert(valve)
{
    InsertPoint = new XYZ(0, 0, 0),
    Color = new Color((short)3),   // ACI 3 = 绿：块内 BYBLOCK 的线应显示为绿色
};
doc.Entities.Add(insert);

// 15. MINSERT：2 行 3 列阵列（验证行列阵列偏移）
var minsert = new Insert(valve)
{
    InsertPoint = new XYZ(0, -40, 0),
    RowCount = 2,
    ColumnCount = 3,
    RowSpacing = 20,
    ColumnSpacing = 20,
    // 注意：Insert.IsMultiple 是只读的（由 RowCount/ColumnCount 推导），不能赋值
};
doc.Entities.Add(minsert);

// ---------- 边界情况 ----------

// 16. 显式隐藏的实体（DXF 组码 60=1）→ 转换时应跳过
doc.Entities.Add(new Line(new XYZ(0, -100, 0), new XYZ(50, -100, 0)) { IsInvisible = true });

// 17. 冻结图层上的实体 → 图层应默认隐藏
doc.Entities.Add(new Line(new XYZ(0, -150, 0), new XYZ(50, -150, 0)) { Layer = layerFrozen });

// 18. 关闭图层上的实体 → 图层应默认隐藏
doc.Entities.Add(new Line(new XYZ(0, -170, 0), new XYZ(50, -170, 0)) { Layer = layerOff });

using (var writer = new DxfWriter(outPath, doc, false))
{
    writer.OnNotification += (_, e) => Console.WriteLine($"  [{e.NotificationType}] {e.Message}");
    writer.Write();
}

WriteTextSample(Path.Combine(Path.GetDirectoryName(outPath)!, "sample_text.dxf"));
WriteHatchSample(Path.Combine(Path.GetDirectoryName(outPath)!, "sample_hatch.dxf"));
WriteLineWeightSample(Path.Combine(Path.GetDirectoryName(outPath)!, "sample_lineweight.dxf"));
WriteWidePolylineSample(Path.Combine(Path.GetDirectoryName(outPath)!, "sample_widepolyline.dxf"));
WriteMTextSample(Path.Combine(Path.GetDirectoryName(outPath)!, "sample_mtext.dxf"));
WritePointSample(Path.Combine(Path.GetDirectoryName(outPath)!, "sample_point.dxf"));
WriteOcsSample(Path.Combine(Path.GetDirectoryName(outPath)!, "sample_ocs.dxf"));
WriteSplineSample(Path.Combine(Path.GetDirectoryName(outPath)!, "sample_spline.dxf"));
WriteNonPlotSample(Path.Combine(Path.GetDirectoryName(outPath)!, "sample_nonplot.dxf"));

// ACadSharp 的 DXF 写出端把 HATCH 边界弧角度按弧度原样写进度字段（普通 ARC 实体写得对），
// 而读入端已按规范把度换弧度 → 整圆边界读回来变成 2π° ≈ 0.11 rad 的一小段，实心填充塌成缝。
// 真实 DWG 走二进制弧度不受影响，只有本工具产出的 DXF 需要就地修正。
FixHatchArcDegrees(outPath);

void FixHatchArcDegrees(string path)
{
    var lines = File.ReadAllLines(path);
    int fixedCount = 0;
    for (int i = 0; i < lines.Length - 3; i++)
    {
        if (lines[i].Trim() != "50" || lines[i + 2].Trim() != "51") continue;
        if (!double.TryParse(lines[i + 3], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var end)) continue;
        if (!double.TryParse(lines[i + 1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var start)) continue;
        if (Math.Abs(end - 2 * Math.PI) < 1e-9 && Math.Abs(start) < 1e-9) { lines[i + 3] = "360.0"; fixedCount++; }
        else if (Math.Abs(end) < 1e-9 && Math.Abs(start) < 1e-9) { lines[i + 3] = "360.0"; fixedCount++; }
    }
    if (fixedCount > 0) File.WriteAllLines(path, lines);
    Console.WriteLine($"填充边界角度修正: {fixedCount} 处");
}

/// <summary>
/// 文字口径样例（sample_text.dxf）—— 供 SmokeTest 锁定 P0-2/P0-3 的口径：
/// 字高 = 大写字高、宽度因子 0.5 真的变窄、倾斜 30° 真的变宽、72/73 对齐真的生效、MTEXT 多点锚。
///
/// 为什么单独一个文件：往 sample_features.dxf 里加文字会改变它的实体计数与包围盒断言，
/// 那套断言是"精确值"基线，不该因为加文字回归而抖动。
/// </summary>
void WriteTextSample(string path)
{    var d = new CadDocument();
    d.Entities.Add(new TextEntity { Value = "HHHH", InsertPoint = new XYZ(0, 0, 0), Height = 10 });
    d.Entities.Add(new TextEntity { Value = "HHHH", InsertPoint = new XYZ(0, 50, 0), Height = 10, WidthFactor = 0.5 });
    d.Entities.Add(new TextEntity
    {
        Value = "HHHH",
        InsertPoint = new XYZ(0, 100, 0),
        Height = 10,
        ObliqueAngle = 30 * Math.PI / 180,   // ACadSharp 内部角度一律弧度
    });
    d.Entities.Add(new TextEntity
    {
        Value = "HHHH",
        InsertPoint = new XYZ(200, 0, 0),
        AlignmentPoint = new XYZ(200, 0, 0),
        Height = 10,
        HorizontalAlignment = TextHorizontalAlignment.Center,
        VerticalAlignment = TextVerticalAlignmentType.Middle,
    });
    d.Entities.Add(new TextEntity
    {
        Value = "HHHH",
        InsertPoint = new XYZ(200, 50, 0),
        AlignmentPoint = new XYZ(200, 50, 0),
        Height = 10,
        HorizontalAlignment = TextHorizontalAlignment.Right,
    });
    d.Entities.Add(new MText
    {
        Value = "AA\\PBB",
        InsertPoint = new XYZ(0, 150, 0),
        Height = 10,
        AttachmentPoint = AttachmentPointType.TopLeft,
    });

    using var w = new DxfWriter(path, d, false);
    w.Write();
    Console.WriteLine($"已生成文字样例: {path}（{d.Entities.Count()} 个实体）");
}

// HATCH 孤岛样例（B1）：
//   1) 实心 + 方孤岛      → 中间必须是洞
//   2) 45° 图案 + 方孤岛  → 图案线必须绕开洞
//   3) 实心无孤岛         → 回归对照（不能因此变成洞）
// 光看"有没有画出来"判断不了孤岛对不对（把洞填实了也照样"画出来了"），
// 所以 SmokeTest 里断言的是"孤岛内某点是否被覆盖""图案段是否跨过洞"。
void WriteHatchSample(string path)
{
    var d = new CadDocument();

    static Hatch.BoundaryPath Square(double x0, double y0, double x1, double y1)
    {
        var bp = new Hatch.BoundaryPath();
        void Edge(double ax, double ay, double bx, double by)
            => bp.Edges.Add(new Hatch.BoundaryPath.Line
            {
                Start = new CSMath.XY(ax, ay),
                End = new CSMath.XY(bx, by),
            });
        Edge(x0, y0, x1, y0);
        Edge(x1, y0, x1, y1);
        Edge(x1, y1, x0, y1);
        Edge(x0, y1, x0, y0);
        return bp;
    }

    var solid = new Hatch { IsSolid = true };
    solid.Paths.Add(Square(0, 0, 100, 100));
    solid.Paths.Add(Square(30, 30, 70, 70));
    d.Entities.Add(solid);

    var pattern = new Hatch
    {
        IsSolid = false,
        Pattern = new HatchPattern("U") { Description = "45 度用户图案" },
    };
    pattern.Pattern.Lines.Add(new HatchPattern.Line
    {
        Angle = Math.PI / 4,
        BasePoint = new CSMath.XY(0, 0),
        Offset = new CSMath.XY(0, 10),
    });
    pattern.Paths.Add(Square(150, 0, 250, 100));
    pattern.Paths.Add(Square(180, 30, 220, 70));
    d.Entities.Add(pattern);

    var plain = new Hatch { IsSolid = true };
    plain.Paths.Add(Square(0, 150, 100, 250));
    d.Entities.Add(plain);

    using var w = new DxfWriter(path, d, false);
    w.Write();
    Console.WriteLine($"已生成填充样例: {path}（{d.Entities.Count()} 个实体）");
}


/// <summary>
/// 线宽样例（sample_lineweight.dxf）—— 锁"图层线宽 / 实体显式线宽 / 块内 BYBLOCK 继承"三件事，
/// 并给 DwgCapture 的 <c>--lineweights</c> 开关提供可量化的 A/B 底图。
///
/// 五条等长水平线，间距 40，从下往上：0.13mm / 0.50mm / 2.11mm（都由图层给出）、
/// 实体显式 1.00mm（图层是 0.13mm）、块内 BYBLOCK（块引用在 0.50mm 层）。
/// 归一化宽度 = 2.11 : 1 → 屏幕上肉眼可辨，墨量统计也能稳定区分。
/// </summary>
void WriteLineWeightSample(string path)
{
    var d = new CadDocument();

    var w13 = new Layer("LW13") { LineWeight = ACadSharp.LineWeightType.W13 };
    var w50 = new Layer("LW50") { LineWeight = ACadSharp.LineWeightType.W50 };
    var w211 = new Layer("LW211") { LineWeight = ACadSharp.LineWeightType.W211 };
    d.Layers.Add(w13);
    d.Layers.Add(w50);
    d.Layers.Add(w211);

    Line Row(double y, Layer layer) => new(new XYZ(0, y, 0), new XYZ(200, y, 0))
    {
        Layer = layer,
        LineWeight = ACadSharp.LineWeightType.ByLayer,
    };

    d.Entities.Add(Row(0, w13));
    d.Entities.Add(Row(40, w50));
    d.Entities.Add(Row(80, w211));

    // 实体显式线宽覆盖图层
    var explicitW100 = Row(120, w13);
    explicitW100.LineWeight = ACadSharp.LineWeightType.W100;
    d.Entities.Add(explicitW100);

    // 块内 BYBLOCK：继承块引用（放在 0.50mm 层）的有效线宽
    var blk = new BlockRecord("LWB");
    blk.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(200, 0, 0))
    {
        LineWeight = ACadSharp.LineWeightType.ByBlock,
    });
    d.BlockRecords.Add(blk);
    var ins = new Insert(blk) { InsertPoint = new XYZ(0, 160, 0), Layer = w50 };
    ins.LineWeight = ACadSharp.LineWeightType.ByLayer;
    d.Entities.Add(ins);

    using var w = new DxfWriter(path, d, false);
    w.Write();
    Console.WriteLine($"已生成线宽样例: {path}（{d.Entities.Count()} 个实体）");
}

/// <summary>
/// 变宽多段线样例（sample_widepolyline.dxf）—— 锁 DXF 组码 40/41 的读回口径。
///
/// 两条水平折线：上面一条等宽 4，下面一条从 0 锥形到 8。
/// 断言只看轮廓包围盒高度（4 / 8），与长度、位置解耦，重排布局也不会误报。
/// </summary>
void WriteWidePolylineSample(string path)
{
    var d = new CadDocument();

    var even = new LwPolyline();
    even.Vertices.Add(new LwPolyline.Vertex(new XY(0, 60)) { StartWidth = 4, EndWidth = 4 });
    even.Vertices.Add(new LwPolyline.Vertex(new XY(100, 60)) { StartWidth = 4, EndWidth = 4 });
    d.Entities.Add(even);

    var taper = new LwPolyline();
    taper.Vertices.Add(new LwPolyline.Vertex(new XY(0, 20)) { StartWidth = 0, EndWidth = 8 });
    taper.Vertices.Add(new LwPolyline.Vertex(new XY(100, 20)) { StartWidth = 8, EndWidth = 8 });
    d.Entities.Add(taper);

    using var w = new DxfWriter(path, d, false);
    w.Write();
    Console.WriteLine($"已生成宽多段线样例: {path}（{d.Entities.Count()} 个实体）");
}

/// <summary>
/// MTEXT 排版样例（sample_mtext.dxf）—— 锁 P2-4 的三处口径，各自一个实体：
///   1) 栏宽断行：RectangleWidth=60，一长串超过 60 的字 → 必须折成多行且每行 ≤ 60；
///   2) 逐行对齐：AttachmentPoint=CenterTop 的 3 行（宽度差很大）→ 每行各自居中；
///   3) 行距倍数：LineSpacing=2.0 → 基线间距 = 1.667 × 2 × 字高。
/// 三块的锚点故意拉开（y=400/400/200，x=0/200/0）：折行块有 10 行、竖向跨 150，
/// 挤在一起会让 UiSmokeTest 的"按包围盒取行带"互相串行。
/// 不并进 sample_text.dxf 是因为那套断言全是精确计数与精确包围盒基线，
/// 加实体就会抖，混在一起看不出是谁坏的。
/// </summary>
void WriteMTextSample(string path)
{
    var d = new CadDocument();

    // 1) 栏宽断行：40 个 W（宽字），字高 10 → 自然宽度远超 60
    d.Entities.Add(new MText
    {
        Value = "WWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWW",
        InsertPoint = new XYZ(0, 400, 0),
        Height = 10,
        AttachmentPoint = AttachmentPointType.TopLeft,
        RectangleWidth = 60,
    });

    // 2) 逐行居中：三行宽度差异大，居中后各行的中心应都落在 x=200
    d.Entities.Add(new MText
    {
        Value = "AAAAAAAAAA\\PA\\PAAAAAA",
        InsertPoint = new XYZ(200, 400, 0),
        Height = 10,
        AttachmentPoint = AttachmentPointType.TopCenter,
    });

    // 3) 行距 2 倍：两行 → 包围盒高度 = 字高 + 2×1.667×字高
    d.Entities.Add(new MText
    {
        Value = "AAAA\\PAAAA",
        InsertPoint = new XYZ(0, 200, 0),
        Height = 10,
        AttachmentPoint = AttachmentPointType.TopLeft,
        LineSpacing = 2.0,
    });

    using var w = new DxfWriter(path, d, false);
    w.Write();
    Console.WriteLine($"已生成 MTEXT 排版样例: {path}（{d.Entities.Count()} 个实体）");
}

/// <summary>
/// POINT 样例（sample_point.dxf）—— 锁 P2-3：点的**形状与尺寸**都来自文件头变量，
/// 不是实体自己的字段，所以只能靠整份文件表达：
///   PDMODE = 34（基础 2 = 十字，+32 = 套圆）、PDSIZE = 0（默认 5% 视高）。
/// 5% 视高意味着"屏幕尺寸恒定"：两个缩放级别下圆环直径必须是同一个像素值，
/// 这是 UiSmokeTest 的判据，也是原实现（固定 ±1 世界单位十字）做不到的。
/// </summary>
void WritePointSample(string path)
{
    var d = new CadDocument();
    // PDMODE/PDSIZE 是头变量：ACadSharp 的 CadHeader 上是整数/浮点字段
    d.Header.PointDisplayMode = 34;
    d.Header.PointDisplaySize = 0;

    d.Entities.Add(new ACadSharp.Entities.Point { Location = new XYZ(0, 0, 0) });
    d.Entities.Add(new ACadSharp.Entities.Point { Location = new XYZ(100, 0, 0) });
    d.Entities.Add(new ACadSharp.Entities.Point { Location = new XYZ(100, 100, 0) });

    using var w = new DxfWriter(path, d, false);
    w.Write();
    Console.WriteLine($"已生成点样式样例: {path}（{d.Entities.Count()} 个实体，PDMODE={d.Header.PointDisplayMode}, PDSIZE={d.Header.PointDisplaySize}）");
}

/// <summary>
/// OCS 样例（sample_ocs.dxf）—— 锁 P2-5：三个半径 10 的圆，法向各不同。
/// 期望（手算，任意轴算法，与实现无关）：
/// <list type="number">
/// <item>N=(0,0,−1)，圆心 (0,0)：x 取反 → 仍是 20×20 的圆，圆心 (0,0)。</item>
/// <item>N=(0.6,0,0.8)，圆心 (100,0)：OCS 的 x 轴 → 世界 (0,1)，y 轴 → (−0.8,0)
///   ⇒ 圆心移到 (0,100)，投影成 16 宽 × 20 高的椭圆。</item>
/// <item>N=(1,0,0)，圆心 (300,0)：x 轴 → 世界 (0,1)，y 轴 → 世界 Z（看不见）
///   ⇒ 退化成 20×0 的竖直线段，圆心 (0,300)。</item>
/// </list>
/// 旧实现只处理 Z&lt;0 的镜像，后两个圆会原样出现在 (100,0) 与 (300,0) —— 一眼可辨。
/// </summary>
void WriteOcsSample(string path)
{
    var d = new CadDocument();

    d.Entities.Add(new Circle
    {
        Center = new XYZ(0, 0, 0), Radius = 10,
        Normal = new XYZ(0, 0, -1),
    });
    d.Entities.Add(new Circle
    {
        Center = new XYZ(100, 0, 0), Radius = 10,
        Normal = new XYZ(0.6, 0, 0.8),
    });
    d.Entities.Add(new Circle
    {
        Center = new XYZ(300, 0, 0), Radius = 10,
        Normal = new XYZ(1, 0, 0),
    });

    using var w = new DxfWriter(path, d, false);
    w.Write();
    Console.WriteLine($"已生成 OCS 样例: {path}（{d.Entities.Count()} 个实体）");
}

/// <summary>
/// 样条样例（sample_spline.dxf）—— 锁 P2-6 的两件事：
/// <list type="number">
/// <item><b>有理权重</b>：degree 2、控制点 (10,0),(10,10),(0,10)、权重 (1, √2/2, 1)
///   是**精确的四分之一圆**（半径 10，圆心原点）。忽略权重时它退化成一抛物线，
///   中点会跑到 (7.5,7.5)（离圆心 10.6，偏 6%），肉眼能看出鼓出来。</item>
/// <item><b>闭合</b>：IsClosed 的样条要 Close()，否则末点到首点之间少一段。</item>
/// </list>
/// </summary>
void WriteSplineSample(string path)
{
    var d = new CadDocument();

    // 1) 有理样条 = 精确四分之一圆
    var quarter = new Spline { Degree = 2 };
    // Weights 是只读集合（往里 Add），不是可赋值属性
    quarter.Weights.Add(1);
    quarter.Weights.Add(Math.Sqrt(0.5));
    quarter.Weights.Add(1);
    quarter.ControlPoints.Add(new XYZ(10, 0, 0));
    quarter.ControlPoints.Add(new XYZ(10, 10, 0));
    quarter.ControlPoints.Add(new XYZ(0, 10, 0));
    quarter.Knots.Add(0); quarter.Knots.Add(0); quarter.Knots.Add(0);
    quarter.Knots.Add(1); quarter.Knots.Add(1); quarter.Knots.Add(1);
    d.Entities.Add(quarter);

    // 2) 闭合样条（控制点不首尾重合，靠标志位闭合）
    var closed = new Spline
    {
        Degree = 2,
        IsClosed = true,
    };
    closed.ControlPoints.Add(new XYZ(100, 0, 0));
    closed.ControlPoints.Add(new XYZ(140, 0, 0));
    closed.ControlPoints.Add(new XYZ(140, 40, 0));
    closed.ControlPoints.Add(new XYZ(100, 40, 0));
    closed.Knots.Add(0); closed.Knots.Add(1); closed.Knots.Add(2);
    closed.Knots.Add(3); closed.Knots.Add(4); closed.Knots.Add(5); closed.Knots.Add(6);
    d.Entities.Add(closed);

    using var w = new DxfWriter(path, d, false);
    w.Write();
    Console.WriteLine($"已生成样条样例: {path}（{d.Entities.Count()} 个实体）");
}

/// <summary>
/// 不打印图层样例（sample_nonplot.dxf）—— 锁"DEFPOINTS 一类图层默认隐藏"。
///
/// <para>来历：3、幕墙横方总汇.dwg 有 15,992 个 POINT 全在 DEFPOINTS 上，Teigha 出图里
/// 一个都看不见，我们画出来却是满屏点阵 —— 说明参照实现按**打印语义**过滤了不打印图层。
/// 这条样例保证那条规则不会在后续重构里悄悄失效（关掉之后整张图会被定义点淹没）。</para>
/// </summary>
void WriteNonPlotSample(string path)
{
    var d = new CadDocument();
    var defpoints = new Layer("DEFPOINTS") { PlotFlag = false };
    d.Layers.Add(defpoints);

    d.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(100, 0, 0)));
    d.Entities.Add(new Line(new XYZ(0, 20, 0), new XYZ(100, 20, 0)) { Layer = defpoints });

    using var w = new DxfWriter(path, d, false);
    w.Write();
    Console.WriteLine($"已生成不打印图层样例: {path}（{d.Entities.Count()} 个实体）");
}

static string DefaultOutputPath()
{
    // 从输出目录向上找 samples/，避免写死盘符
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        var samples = Path.Combine(dir.FullName, "samples");
        if (Directory.Exists(samples)) return Path.Combine(samples, "sample_features.dxf");
        dir = dir.Parent;
    }
    return Path.Combine(AppContext.BaseDirectory, "sample_features.dxf");
}
