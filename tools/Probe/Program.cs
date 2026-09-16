using System.Reflection;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;

var asm = typeof(CadDocument).Assembly;

void DumpProps(Type? t, string label)
{
    if (t is null) { Console.WriteLine($"== {label}: NOT FOUND"); return; }
    Console.WriteLine($"== {label} ({t.FullName})");
    foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.Name))
        Console.WriteLine($"   {p.PropertyType.Name} {p.Name}");
}

DumpProps(asm.GetType("ACadSharp.Entities.TextEntity"), "TextEntity");
DumpProps(asm.GetType("ACadSharp.Entities.Polyline2D"), "Polyline2D");
DumpProps(asm.GetType("ACadSharp.Entities.Arc"), "Arc");
DumpProps(asm.GetType("ACadSharp.Entities.Ellipse"), "Ellipse");
DumpProps(asm.GetType("ACadSharp.Entities.Circle"), "Circle");
DumpProps(asm.GetType("ACadSharp.Entities.Point"), "Point");
DumpProps(asm.GetType("ACadSharp.Entities.Leader"), "Leader");
DumpProps(asm.GetType("ACadSharp.Blocks.BlockEntity") ?? asm.GetTypes().FirstOrDefault(t => t.Name == "BlockEntity"), "BlockEntity");

// Polyline2D 顶点类型
var vt = typeof(Polyline2D).GetProperty("Vertices")?.PropertyType;
var elem = vt?.GetElementType() ?? vt?.GetGenericArguments().FirstOrDefault();
if (elem != null)
    Console.WriteLine("Polyline2D.Vertex: " + elem.FullName + " props: " + string.Join(", ", elem.GetProperties().Select(p => $"{p.PropertyType.Name} {p.Name}")) + " fields: " + string.Join(", ", elem.GetFields().Select(p => $"{p.FieldType.Name} {p.Name}")));

// Hatch Polyline edge 的 Vertices 元素类型
var hatchType = typeof(Hatch);
var bpType = hatchType.GetNestedTypes().FirstOrDefault(n => n.Name == "BoundaryPath");
var peType = bpType?.GetNestedTypes().FirstOrDefault(n => n.Name == "Polyline");
var peVerts = peType?.GetProperty("Vertices")?.PropertyType;
var peElem = peVerts?.GetElementType() ?? peVerts?.GetGenericArguments().FirstOrDefault();
Console.WriteLine("Hatch.Polyline.Vertices elem: " + peElem?.FullName);
if (peElem != null)
    Console.WriteLine("  props: " + string.Join(", ", peElem.GetProperties().Select(p => $"{p.PropertyType.Name} {p.Name}")) + " fields: " + string.Join(", ", peElem.GetFields().Select(p => $"{p.FieldType.Name} {p.Name}")));

// IText 接口
var itext = asm.GetTypes().FirstOrDefault(t => t.Name == "IText");
if (itext != null)
    Console.WriteLine("IText props: " + string.Join(", ", itext.GetProperties().Select(p => $"{p.PropertyType.Name} {p.Name}")));

// CSMath.XY 成员
var xyType = asm.GetType("CSMath.XY") ?? typeof(Line).Assembly.GetType("CSMath.XY");
if (xyType == null)
{
    var lw = typeof(LwPolyline).GetProperty("Vertices")?.PropertyType;
    var velem = lw?.GetElementType() ?? lw?.GetGenericArguments().FirstOrDefault();
    var locType = velem?.GetProperty("Location")?.PropertyType;
    Console.WriteLine("Location type: " + locType?.FullName);
    xyType = locType;
}
if (xyType != null)
    Console.WriteLine("XY members: " + string.Join(", ", xyType.GetProperties().Select(p => p.Name)));
