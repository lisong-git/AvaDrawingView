using CadViewer.Core;
using SkiaSharp;

// 冒烟测试：解析样例 DXF → 转换 → 索引 → 查询，验证全链路
var path = args.Length > 0 ? args[0] : @"D:\Work\上海八局\Avalonia2dDWG\samples\sample_r12.dxf";

if (!File.Exists(path))
{
    Console.WriteLine($"FAIL: 找不到样例文件 {path}");
    return 1;
}

Console.WriteLine($"加载: {path}");
var list = await CadDocumentLoader.LoadAsync(path, new Progress<string>(s => Console.WriteLine($"  [{s}]")));

Console.WriteLine($"实体数: {list.Entities.Count}");
Console.WriteLine($"图层数: {list.LayerNames.Count} [{string.Join(", ", list.LayerNames)}]");
Console.WriteLine($"范围: L={list.Extents.Left:F2}, T={list.Extents.Top:F2}, R={list.Extents.Right:F2}, B={list.Extents.Bottom:F2}");

int fails = 0;
void Check(bool ok, string name)
{
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} - {name}");
    if (!ok) fails++;
}

Check(list.Entities.Count >= 5, "实体数量 >= 5");
Check(!float.IsInfinity(list.Extents.Width) && list.Extents.Width > 0, "范围有效");

// 视口裁剪：全图查询应命中全部，极小角落查询应少于全图
var all = list.Index.Query(list.Extents);
Check(all.Count >= list.Entities.Count / 2, "全图查询命中过半");

var corner = SKRect.Create(list.Extents.Left - 1, list.Extents.Top - 1, 2, 2);
var few = list.Index.Query(corner);
Check(few.Count < all.Count, "角落查询少于全图（索引有效）");

// 实体几何健全性
int pathCount = list.Entities.Count(e => e.Path != null || e.FillPath != null);
int textCount = list.Entities.Count(e => e.Text != null);
Console.WriteLine($"几何实体: {pathCount}, 文本实体: {textCount}");
Check(pathCount >= 4, "几何实体 >= 4");
Check(textCount >= 1, "文本实体 >= 1");

if (fails == 0)
{
    Console.WriteLine("SMOKE TEST PASSED");
    return 0;
}
Console.WriteLine("SMOKE TEST FAILED");
return 1;
