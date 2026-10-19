using System.Reflection;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using DrawingView.Core;

// ACadSharp API 反射探针：改代码前先在这里确认属性名，不要猜。
// 用法：dotnet run --project tools/Probe -- [可选 dgw/dxf 路径]
// 设 PROBE_LOG 环境变量可把输出写到 UTF-8 文件（本机 dotnet run 的 stdout 经常整段丢失）。

var asm = typeof(CadDocument).Assembly;

var logPath = Environment.GetEnvironmentVariable("PROBE_LOG");
if (!string.IsNullOrEmpty(logPath))
{
    var sw = new StreamWriter(logPath, false, System.Text.Encoding.UTF8) { AutoFlush = true };
    Console.SetOut(sw);
    Console.SetError(sw);
}

void DumpProps(Type? t, string label)
{
    if (t is null) { Console.WriteLine($"== {label}: NOT FOUND"); return; }
    Console.WriteLine($"== {label} ({t.FullName})");
    foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.Name))
        Console.WriteLine($"   {p.PropertyType.Name} {p.Name}");
}

DumpProps(typeof(Layer), "Layer");
DumpProps(typeof(Insert), "Insert");
DumpProps(typeof(Entity), "Entity");
DumpProps(typeof(TextEntity), "TextEntity");
DumpProps(typeof(MText), "MText");

// LayerFlags 枚举值（判断 Off / Frozen 用什么位）
var flagType = asm.GetTypes().FirstOrDefault(t => t.Name is "LayerFlags" or "LayerFlag");
if (flagType is not null)
    Console.WriteLine("LayerFlags: " + string.Join(", ", Enum.GetNames(flagType)));

// ---------- 样例文件：图层表实况 ----------
var path = args.Length > 0 ? args[0] : @"D:\Work\上海八局\AvaDrawingView\samples\sample_r12.dxf";
Console.WriteLine();
Console.WriteLine($"=== 加载 {path} ===");
if (!File.Exists(path)) { Console.WriteLine("文件不存在"); return; }

CadDocument doc;
if (Path.GetExtension(path).Equals(".dwg", StringComparison.OrdinalIgnoreCase))
{
    using var r = new ACadSharp.IO.DwgReader(path);
    doc = r.Read();
}
else
{
    using var r = new ACadSharp.IO.DxfReader(path);
    doc = r.Read();
}

Console.WriteLine($"doc.Layers 条目数 = {doc.Layers.Count()}");
foreach (var l in doc.Layers)
    Console.WriteLine($"   name='{l.Name}' handle={l.Handle} flags={l.Flags} colorIdx={l.Color.Index} trueColor={l.Color.IsTrueColor}");

// 实体实际引用了哪些图层
var used = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
foreach (var e in doc.ModelSpace.Entities)
{
    var n = e.Layer?.Name ?? "(null)";
    used[n] = used.TryGetValue(n, out var c) ? c + 1 : 1;
}
Console.WriteLine("模型空间实体引用图层：");
foreach (var kv in used)
    Console.WriteLine($"   '{kv.Key}' × {kv.Value}");

var brief = Environment.GetEnvironmentVariable("PROBE_BRIEF") == "1";

var byType = new Dictionary<string, int>();
var hiddenByType = new Dictionary<string, int>();
int invisible = 0;
int minsertCount = 0;
long maxArray = 0;
long totalExpanded = 0;

Console.WriteLine("实体逐个：");
foreach (var e in doc.ModelSpace.Entities)
{
    var typeName = e.GetType().Name;
    byType[typeName] = byType.TryGetValue(typeName, out var cnt) ? cnt + 1 : 1;
    if (e.IsInvisible)
    {
        invisible++;
        hiddenByType[typeName] = hiddenByType.TryGetValue(typeName, out var hc) ? hc + 1 : 1;
    }
    if (e is Insert anyIns && (anyIns.RowCount > 1 || anyIns.ColumnCount > 1))
    {
        minsertCount++;
        long n = (long)anyIns.RowCount * anyIns.ColumnCount;
        if (n > maxArray) maxArray = n;
        totalExpanded += n;
    }

    if (brief) continue;

    Console.WriteLine($"   {typeName,-12} layer={e.Layer?.Name} handle={e.Handle} color={e.Color.Index} byLayer={e.Color.IsByLayer} byBlock={e.Color.IsByBlock} lw={e.LineWeight} invisible={e.IsInvisible}");

    // 角度单位诊断：AddArc/ArcTo 的语义取决于这些值到底是度还是弧度
    if (e is Arc arc)
        Console.WriteLine($"        Arc StartAngle={arc.StartAngle} EndAngle={arc.EndAngle} Sweep={arc.Sweep} "
                          + $"(度制则为 {arc.StartAngle * 180 / Math.PI:F2}° → {arc.EndAngle * 180 / Math.PI:F2}°)");
    if (e is Hatch hatch && hatch.Paths is not null)
    {
        foreach (var p in hatch.Paths)
        {
            if (p?.Edges is null) continue;
            foreach (var edge in p.Edges)
            {
                Console.WriteLine($"        Hatch edge {edge.GetType().Name}");
                if (edge is Hatch.BoundaryPath.Arc ha)
                    Console.WriteLine($"          Arc center={ha.Center} r={ha.Radius} start={ha.StartAngle} end={ha.EndAngle} ccw={ha.CounterClockWise} "
                                      + $"(度制则为 {ha.StartAngle * 180 / Math.PI:F2}° → {ha.EndAngle * 180 / Math.PI:F2}°)");
            }
        }
    }
    if (e is Ellipse el)
        Console.WriteLine($"        Ellipse Start={el.StartParameter} End={el.EndParameter} IsFull={el.IsFullEllipse}");
    if (e is Insert ins2)
        Console.WriteLine($"        Insert rows={ins2.RowCount} cols={ins2.ColumnCount} rowSp={ins2.RowSpacing} colSp={ins2.ColumnSpacing}");
}

Console.WriteLine();
Console.WriteLine($"实体类型统计: {string.Join(", ", byType.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}"))}");
Console.WriteLine($"隐藏实体(IsInvisible=1): {invisible}");
Console.WriteLine($"  隐藏实体分类: {string.Join(", ", hiddenByType.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}"))}");
Console.WriteLine($"MINSERT 阵列数: {minsertCount}，最大单阵列实例数: {maxArray}，阵列展开总数: {totalExpanded}");

// 块表：确认是否有未命名块记录被当成图层
DumpProps(typeof(CadDocument), "CadDocument");

void DumpCtors(Type? t, string label)
{
    if (t is null) { Console.WriteLine($"== {label}: NOT FOUND"); return; }
    Console.WriteLine($"== {label} ctors:");
    foreach (var c in t.GetConstructors())
        Console.WriteLine("   (" + string.Join(", ", c.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}")) + ")");
}

// 生成样例 DXF 需要的 API
DumpCtors(asm.GetType("ACadSharp.IO.DxfWriter"), "DxfWriter");
DumpProps(asm.GetType("ACadSharp.IO.DxfWriter"), "DxfWriter");
DumpProps(asm.GetType("ACadSharp.Blocks.Block"), "Block");
DumpProps(asm.GetType("ACadSharp.Tables.BlockRecord"), "BlockRecord");
DumpProps(typeof(ACadSharp.Entities.Spline), "Spline");
DumpProps(typeof(ACadSharp.Entities.Ellipse), "Ellipse");
DumpProps(typeof(Hatch), "Hatch");
DumpCtors(typeof(Hatch), "Hatch");
DumpProps(typeof(ACadSharp.Entities.Point), "Point");

var entitiesProp = typeof(CadDocument).GetProperty("Entities")?.PropertyType;
DumpProps(entitiesProp, "CadDocument.Entities 类型");
DumpProps(typeof(ACadSharp.Tables.Layer), "Layer");

// ---------- OwnHandle 验证：块内子实体身份 ----------
// 目的：确认 CadEntity.OwnHandle 真的填的是「实体自身句柄」，
// 且块内子实体的 (Handle=最外层块引用, OwnHandle=自身) 配对能被**独立遍历**证实。
Console.WriteLine();
Console.WriteLine("=== OwnHandle 验证（块内子实体身份） ===");

var display = CadDocumentLoader.Load(path);
var dl = display.Entities;

int filled = dl.Count(e => e.OwnHandle != 0);
int inherited = dl.Count(e => e.OwnHandle != e.Handle);   // 块引用展开：Handle 被覆盖
int notInherited = dl.Count(e => e.OwnHandle == e.Handle); // 顶层（或尺寸块内直连）
Console.WriteLine($"显示实体总数 = {dl.Count}，OwnHandle 已填 = {filled}");
Console.WriteLine($"  OwnHandle != Handle（块内继承）= {inherited}");
Console.WriteLine($"  OwnHandle == Handle（未继承）  = {notInherited}");
Console.WriteLine($"  块内实体涉及的不同块引用句柄 = {dl.Where(e => e.OwnHandle != e.Handle).Select(e => e.Handle).Distinct().Count()}");
Console.WriteLine($"  块内实体涉及的不同自身句柄   = {dl.Where(e => e.OwnHandle != e.Handle).Select(e => e.OwnHandle).Distinct().Count()}");

// 独立遍历（不依赖 GeometryConverter）：
//   ① ownerTarget: 全文档所有「会把自身句柄下放给块内几何」的实体 → 它持有的块记录
//      目前有两类：INSERT（块引用）与 DIMENSION（尺寸标注的匿名块，转换器同样下放句柄）
//   ② Reach(块记录): 该块定义内递归可达的全部实体句柄（含嵌套块）
// 二者组合即可判定「外层句柄 H 能否真的到达自身句柄 O」。
var ownerTarget = new Dictionary<ulong, BlockRecord>();
foreach (var br in doc.BlockRecords)
    foreach (var e in br.Entities)
    {
        if (e is Insert ins && ins.Block is not null) ownerTarget[ins.Handle] = ins.Block;
        else if (e is Dimension dim && dim.Block is not null) ownerTarget[dim.Handle] = dim.Block;
    }

var reachCache = new Dictionary<ulong, HashSet<ulong>>();

HashSet<ulong> Reach(BlockRecord br, int depth)
{
    if (reachCache.TryGetValue(br.Handle, out var cached)) return cached;
    var set = new HashSet<ulong>();
    reachCache[br.Handle] = set; // 先占位，防块自引用成环
    if (br.Entities is null || depth > 16) return set;

    foreach (var e in br.Entities)
    {
        if (e.IsInvisible) continue;
        set.Add(e.Handle);
        if (e is Insert ins && ins.Block is not null)
            set.UnionWith(Reach(ins.Block, depth + 1));
    }
    return set;
}

foreach (var br in doc.BlockRecords) Reach(br, 0);

Console.WriteLine($"全文档块记录数 = {doc.BlockRecords.Count()}，句柄下放实体（INSERT+DIMENSION）映射 = {ownerTarget.Count}");

if (inherited == 0)
{
    Console.WriteLine("结果: 本图无块内继承实体，OwnHandle 断言跳过（换一张含块的图纸再验）");
}
else
{
    int ok = 0, bad = 0, noTarget = 0;
    var offenders = new List<string>();
    foreach (var e in dl)
    {
        if (e.OwnHandle == e.Handle) continue;
        if (!ownerTarget.TryGetValue(e.Handle, out var target))
        {
            noTarget++;
            if (offenders.Count < 10)
                offenders.Add($"Handle=0x{e.Handle:X} 既非 INSERT 也非 DIMENSION（无目标块）");
            continue;
        }
        if (Reach(target, 0).Contains(e.OwnHandle)) ok++;
        else
        {
            bad++;
            if (offenders.Count < 10)
                offenders.Add($"Handle=0x{e.Handle:X} OwnHandle=0x{e.OwnHandle:X}"
                              + $" 不在块 '{target.Name}' 的可达集合内");
        }
    }

    Console.WriteLine($"交叉核对：块内实体 {inherited} 条 → 可证实 {ok}，不可达 {bad}，外层句柄无目标块 {noTarget}");
    foreach (var o in offenders) Console.WriteLine("   异常: " + o);
    Console.WriteLine(bad == 0 && noTarget == 0 && filled == dl.Count
        ? "结果: PASS（OwnHandle 全填，且每个块内实体的 (外层, 自身) 配对都被独立遍历证实）"
        : "结果: FAIL");
}

// 抽样：块内实体最多的那个块引用，看看它下面挂了哪些自身句柄
var top = dl.Where(e => e.OwnHandle != e.Handle)
            .GroupBy(e => e.Handle)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();
if (top is not null)
{
    var own = top.Select(e => e.OwnHandle).Distinct().OrderBy(h => h).ToList();
    Console.WriteLine($"块内实体最多的块引用 Handle=0x{top.Key:X}：显示图元 {top.Count()} 个，"
                      + $"对应自身句柄 {own.Count} 个 → {string.Join(" ", own.Take(12).Select(h => "0x" + h.ToString("X")))}"
                      + (own.Count > 12 ? " …" : ""));
}

display.Dispose();
