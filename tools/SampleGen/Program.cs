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

Console.WriteLine($"已生成: {outPath}");
Console.WriteLine($"模型空间实体: {doc.Entities.Count()}");
Console.WriteLine($"图层: {string.Join(", ", doc.Layers.Select(l => l.Name))}");
Console.WriteLine($"块: {string.Join(", ", doc.BlockRecords.Select(b => b.Name))}");
return 0;

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
