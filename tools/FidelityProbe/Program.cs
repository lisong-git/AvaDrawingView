using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using ACadSharp;
using ACadSharp.IO;

namespace FidelityProbe;

/// <summary>
/// 渲染保真度体检：逐张图纸统计「当前渲染器未实现 / 实现不完整」的属性出现次数，
/// 用来把"缺什么"从定性说法变成可排优先级的数字。
///
/// 全部用反射取属性 —— ACadSharp 3.4.9 的属性名/枚举名与文档差异大，
/// 反射可以"取不到就跳过"，不会因为某个版本少了字段就编译不过。
///
///   FidelityProbe &lt;dwg/dxf&gt;...            （也可以直接给目录，递归找 *.dwg/*.dxf）
///   FIDELITY_LOG=&lt;path&gt;  指定 UTF-8 日志（Windows 控制台重定向中文会乱码）
/// </summary>
internal static class Program
{
    private static readonly StringBuilder Log = new();

    private static void W(string line = "")
    {
        Log.AppendLine(line);
        Console.WriteLine(line);
    }

    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("用法: FidelityProbe <dwg|dxf|目录>...   |   FidelityProbe --blocks <dwg>...");
            return 1;
        }

        if (args[0] == "--blocks")
        {
            foreach (var f in args.Skip(1))
            {
                W($"==== {f}");
                try
                {
                    CadDocument doc;
                    using (IDisposableReader reader = f.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase)
                        ? new DxfReaderAdapter(f)
                        : new DwgReaderAdapter(f))
                    {
                        doc = reader.Read();
                    }
                    W("  doc props: " + string.Join(", ", doc.GetType().GetProperties().Select(x => x.Name)));
                    W($"  ModelSpace.Entities = {Count(doc.ModelSpace)}");
                    W($"  PaperSpace.Entities = {Count(Prop(doc, "PaperSpace"))}");
                    if (Prop(doc, "PaperSpace") is { } psp && Prop(psp, "Entities") is IEnumerable pes)
                        foreach (var pe in pes)
                            W($"    paper: {pe.GetType().Name} layer={Prop(pe, "Layer")}");
                    W($"  doc.Entities = {Count(doc)}");
                    if (Prop(doc, "BlockRecords") is IEnumerable blocks)
                    {
                        foreach (var b in blocks)
                        {
                            var n = Count(b);
                            W($"  block {Prop(b, "Name"),-28} ents={n,7}  flags={Prop(b, "Flags")}");
                        }
                    }
                    if (Prop(doc, "Layouts") is IEnumerable layouts)
                    {
                        foreach (var l in layouts)
                            W($"  layout {Prop(l, "Name"),-24} tabOrder={Prop(l, "TabOrder")} block={Prop(Prop(l, "Block"), "Name")}");
                    }
                }
                catch (Exception ex) { W($"  FAIL {ex.GetType().Name}: {ex.Message}"); }
            }
            var logPath0 = Environment.GetEnvironmentVariable("FIDELITY_LOG");
            if (!string.IsNullOrWhiteSpace(logPath0))
            {
                try { File.WriteAllText(logPath0, Log.ToString(), new UTF8Encoding(false)); } catch { }
            }
            return 0;
        }

        var files = new List<string>();
        foreach (var a in args)
        {
            if (Directory.Exists(a))
            {
                files.AddRange(Directory.EnumerateFiles(a, "*.*", SearchOption.AllDirectories)
                    .Where(f => f.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase)
                             || f.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase)));
            }
            else if (File.Exists(a)) files.Add(a);
        }

        ProgramTotals total = new();
        var rows = new List<(string name, ProgramTotals t)>();

        foreach (var f in files)
        {
            W($"==== {f}");
            try
            {
                CadDocument doc;
                using (IDisposableReader reader = f.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase)
                    ? new DxfReaderAdapter(f)
                    : new DwgReaderAdapter(f))
                {
                    doc = reader.Read();
                }

                var t = new ProgramTotals();
                WalkBlock(doc.ModelSpace, t, depth: 0);
                t.PaperEntities = CountPaperSpace(doc);
                total.Add(t);
                rows.Add((Path.GetFileName(f), t));
                W(t.Report());
            }
            catch (Exception ex)
            {
                W($"  READ_FAIL {ex.GetType().Name}: {ex.Message}");
            }
        }

        W();
        W("================ 汇总 ================");
        W($"{"文件",-40}{"实体",8}{"文字",7}{"填充",7}{"块",6}{"布局",6}");
        foreach (var (name, t) in rows)
            W($"{Trim(name, 38),-40}{t.Total,8}{t.Text,7}{t.Hatch,7}{t.Insert,6}{t.PaperEntities,6}");
        W();
        W(total.Report());

        var logPath = Environment.GetEnvironmentVariable("FIDELITY_LOG");
        if (!string.IsNullOrWhiteSpace(logPath))
        {
            try { File.WriteAllText(logPath, Log.ToString(), new UTF8Encoding(false)); }
            catch { }
        }
        return 0;
    }

    private static int Count(object? block)
        => block is not null && Prop(block, "Entities") is IEnumerable e ? e.Cast<object>().Count() : 0;

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n];

    // ---------------- 遍历 ----------------

    private static void WalkBlock(object? block, ProgramTotals t, int depth)
    {
        if (block is null || depth > 16) return;
        if (Prop(block, "Entities") is not IEnumerable ents) return;
        foreach (var e in ents)
        {
            if (e is null) continue;
            t.Total++;
            var tn = e.GetType().Name;
            t.Types[tn] = t.Types.GetValueOrDefault(tn) + 1;

            if (PropBool(e, "IsInvisible")) t.Invisible++;

            CheckNormal(e, t);
            CheckLineWeight(e, t);

            switch (tn)
            {
                case "TextEntity":
                case "Text":
                    CheckText(e, t);
                    break;
                case "MText":
                    CheckMText(e, t);
                    break;
                case "Hatch":
                    CheckHatch(e, t);
                    break;
                case "Spline":
                    CheckSpline(e, t);
                    break;
                case "Insert":
                {
                    t.Insert++;
                    if (Prop(e, "Attributes") is IEnumerable attrs)
                    {
                        int n = attrs.Cast<object>().Count();
                        if (n > 0) { t.InsertWithAttrib++; t.AttribCount += n; }
                    }
                    WalkBlock(Prop(e, "Block"), t, depth + 1);
                    break;
                }
                case "Dimension":
                {
                    t.Dimension++;
                    WalkBlock(Prop(e, "Block"), t, depth + 1);
                    break;
                }
                case "LwPolyline":
                case "Polyline2D":
                case "Polyline":
                    CheckPolyWidth(e, t);
                    break;
                case "Region":
                    t.Region++;
                    break;
            }

            if (tn.Contains("Proxy")) t.Proxy++;
        }
    }

    private static int CountPaperSpace(CadDocument doc)
    {
        try
        {
            var ps = Prop(doc, "PaperSpace");
            if (ps is not null && Prop(ps, "Entities") is IEnumerable e1) return e1.Cast<object>().Count();
            if (Prop(doc, "Blocks") is IEnumerable blocks)
            {
                foreach (var b in blocks)
                {
                    if (Prop(b, "Name") is string n && n.Equals("*Paper_Space", StringComparison.OrdinalIgnoreCase)
                        && Prop(b, "Entities") is IEnumerable e2)
                        return e2.Cast<object>().Count();
                }
            }
        }
        catch { }
        return 0;
    }

    // ---------------- 各项检查 ----------------

    private static void CheckNormal(object e, ProgramTotals t)
    {
        if (Prop(e, "Normal") is not { } n) return;
        var z = Num(Prop(n, "Z"));
        var x = Num(Prop(n, "X"));
        var y = Num(Prop(n, "Y"));
        if (double.IsNaN(z)) return;
        if (z < 0 && Math.Abs(x) < 1e-9 && Math.Abs(y) < 1e-9) t.NormalNegZ++;
        else if (Math.Abs(z - 1) > 1e-9 || Math.Abs(x) > 1e-9 || Math.Abs(y) > 1e-9) t.NormalOther++;
    }

    private static void CheckLineWeight(object e, ProgramTotals t)
    {
        var lw = Prop(e, "LineWeight")?.ToString();
        if (lw is null) return;
        if (lw is "ByLayer" or "ByBlock" or "Default") return;
        t.LineWeightExplicit++;
        t.LineWeightValues[lw] = t.LineWeightValues.GetValueOrDefault(lw) + 1;
    }

    private static void CheckText(object e, ProgramTotals t)
    {
        t.Text++;
        var h = Num(Prop(e, "Height"));
        if (!double.IsNaN(h)) t.TextHeightSum += h;

        var wf = Num(Prop(e, "WidthFactor"));
        if (!double.IsNaN(wf) && Math.Abs(wf - 1.0) > 1e-6) t.TextWidthFactor++;

        var ob = Num(Prop(e, "ObliqueAngle"));
        if (!double.IsNaN(ob) && Math.Abs(ob) > 1e-9) t.TextOblique++;

        var rot = Num(Prop(e, "Rotation"));
        if (!double.IsNaN(rot) && Math.Abs(rot) > 1e-9) t.TextRotated++;

        var ha = Prop(e, "HorizontalAlignment")?.ToString();
        if (ha is not null && ha is not "Left" and not "None") t.TextHAlign++;
        var va = Prop(e, "VerticalAlignment")?.ToString();
        if (va is not null && va is not "Baseline" and not "None") t.TextVAlign++;

        var style = Prop(e, "Style");
        if (style is not null)
        {
            var font = Prop(style, "Filename") as string ?? Prop(style, "Name") as string ?? "";
            t.TextFonts[font] = t.TextFonts.GetValueOrDefault(font) + 1;
        }
    }

    private static void CheckMText(object e, ProgramTotals t)
    {
        t.MText++;
        var ap = Prop(e, "AttachmentPoint")?.ToString();
        if (ap is not null && ap is not "TopLeft") t.MTextAttachment++;
        var w = Num(Prop(e, "RectangleWidth"));
        if (!double.IsNaN(w) && w > 0) t.MTextWidth++;
        var rot = Num(Prop(e, "Rotation"));
        if (!double.IsNaN(rot) && Math.Abs(rot) > 1e-9) t.MTextRotated++;
        var wf = Num(Prop(e, "WidthFactor"));
        if (!double.IsNaN(wf) && Math.Abs(wf - 1.0) > 1e-6) t.MTextWidthFactor++;
    }

    private static void CheckHatch(object e, ProgramTotals t)
    {
        t.Hatch++;
        if (PropBool(e, "IsSolid")) { t.HatchSolid++; return; }
        t.HatchPattern++;
        if (Prop(e, "Paths") is ICollection paths && paths.Count > 1) t.HatchMultiPath++;
        var pattern = Prop(e, "Pattern");
        if (pattern is null) t.HatchNoPatternDef++;
    }

    private static void CheckSpline(object e, ProgramTotals t)
    {
        t.Spline++;
        if (Prop(e, "Weights") is IEnumerable w)
        {
            var list = w.Cast<object>().Select(Num).Where(v => !double.IsNaN(v)).ToList();
            if (list.Count > 0 && list.Any(v => Math.Abs(v - 1.0) > 1e-9)) t.SplineRational++;
        }
        if (PropBool(e, "IsClosed")) t.SplineClosed++;
    }

    private static void CheckPolyWidth(object e, ProgramTotals t)
    {
        t.Polyline++;
        if (Prop(e, "Vertices") is not IEnumerable verts) return;
        bool anyWidth = false, taper = false;
        foreach (var v in verts)
        {
            var sw = Num(Prop(v, "StartWidth"));
            var ew = Num(Prop(v, "EndWidth"));
            if (double.IsNaN(sw) || double.IsNaN(ew)) continue;
            if (sw > 0 || ew > 0) anyWidth = true;
            if (sw > 0 && ew > 0 && Math.Abs(sw - ew) > 1e-9) taper = true;
        }
        double cw = Num(Prop(e, "ConstantWidth"));
        if (!double.IsNaN(cw) && cw > 0) anyWidth = true;
        if (anyWidth) t.PolyWidth++;
        if (taper) t.PolyTaper++;
    }

    // ---------------- 反射工具 ----------------

    private static object? Prop(object o, string name)
    {
        try { return o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(o); }
        catch { return null; }
    }

    private static bool PropBool(object o, string name)
    {
        var v = Prop(o, name);
        return v is bool b && b;
    }

    private static double Num(object? v)
    {
        try
        {
            return v switch
            {
                null => double.NaN,
                double d => d,
                float f => f,
                int i => i,
                short s => s,
                decimal m => (double)m,
                _ => Convert.ToDouble(v, CultureInfo.InvariantCulture),
            };
        }
        catch { return double.NaN; }
    }

    private interface IDisposableReader : IDisposable { CadDocument Read(); }
    private sealed class DwgReaderAdapter : IDisposableReader
    {
        private readonly ACadSharp.IO.DwgReader _r;
        public DwgReaderAdapter(string p) => _r = new ACadSharp.IO.DwgReader(p);
        public CadDocument Read() => _r.Read();
        public void Dispose() => _r.Dispose();
    }
    private sealed class DxfReaderAdapter : IDisposableReader
    {
        private readonly ACadSharp.IO.DxfReader _r;
        public DxfReaderAdapter(string p) => _r = new ACadSharp.IO.DxfReader(p);
        public CadDocument Read() => _r.Read();
        public void Dispose() => _r.Dispose();
    }
}

internal sealed class ProgramTotals
{
    public int Total;
    public int Invisible;
    public int NormalNegZ;
    public int NormalOther;
    public int LineWeightExplicit;
    public int Text;
    public double TextHeightSum;
    public int TextWidthFactor;
    public int TextOblique;
    public int TextRotated;
    public int TextHAlign;
    public int TextVAlign;
    public int MText;
    public int MTextAttachment;
    public int MTextWidth;
    public int MTextRotated;
    public int MTextWidthFactor;
    public int Hatch;
    public int HatchSolid;
    public int HatchPattern;
    public int HatchMultiPath;
    public int HatchNoPatternDef;
    public int Spline;
    public int SplineRational;
    public int SplineClosed;
    public int Polyline;
    public int PolyWidth;
    public int PolyTaper;
    public int Insert;
    public int InsertWithAttrib;
    public int AttribCount;
    public int Dimension;
    public int Region;
    public int Proxy;
    public int PaperEntities;

    public readonly Dictionary<string, int> Types = new();
    public readonly Dictionary<string, int> LineWeightValues = new();
    public readonly Dictionary<string, int> TextFonts = new();

    public void Add(ProgramTotals o)
    {
        Total += o.Total; Invisible += o.Invisible; NormalNegZ += o.NormalNegZ; NormalOther += o.NormalOther;
        LineWeightExplicit += o.LineWeightExplicit; Text += o.Text; TextHeightSum += o.TextHeightSum;
        TextWidthFactor += o.TextWidthFactor; TextOblique += o.TextOblique; TextRotated += o.TextRotated;
        TextHAlign += o.TextHAlign; TextVAlign += o.TextVAlign; MText += o.MText;
        MTextAttachment += o.MTextAttachment; MTextWidth += o.MTextWidth; MTextRotated += o.MTextRotated;
        MTextWidthFactor += o.MTextWidthFactor; Hatch += o.Hatch; HatchSolid += o.HatchSolid;
        HatchPattern += o.HatchPattern; HatchMultiPath += o.HatchMultiPath;
        HatchNoPatternDef += o.HatchNoPatternDef; Spline += o.Spline; SplineRational += o.SplineRational;
        SplineClosed += o.SplineClosed; Polyline += o.Polyline; PolyWidth += o.PolyWidth; PolyTaper += o.PolyTaper;
        Insert += o.Insert; InsertWithAttrib += o.InsertWithAttrib; AttribCount += o.AttribCount;
        Dimension += o.Dimension; Region += o.Region; Proxy += o.Proxy; PaperEntities += o.PaperEntities;
        foreach (var kv in o.Types) Types[kv.Key] = Types.GetValueOrDefault(kv.Key) + kv.Value;
        foreach (var kv in o.LineWeightValues) LineWeightValues[kv.Key] = LineWeightValues.GetValueOrDefault(kv.Key) + kv.Value;
        foreach (var kv in o.TextFonts) TextFonts[kv.Key] = TextFonts.GetValueOrDefault(kv.Key) + kv.Value;
    }

    public string Report()
    {
        var sb = new StringBuilder();
        void L(string label, int v, string note, int denom = 0)
        {
            var pct = denom > 0 ? $" ({100.0 * v / Math.Max(denom, 1):F1}%)" : "";
            sb.AppendLine($"  {label,-26}{v,8}{pct,-9} {note}");
        }
        sb.AppendLine($"  实体合计 {Total}；布局空间实体 {PaperEntities}");
        L("隐藏实体(60=1)", Invisible, "已正确处理（跳过）");
        L("法线 -Z", NormalNegZ, "仅 Arc/Circle/Polyline 做了镜像", Total);
        L("法线 其它方向", NormalOther, "完全未处理（OCS→WCS 只认 ±Z）", Total);
        L("显式线宽", LineWeightExplicit, "渲染器完全忽略 LineWeight", Total);
        L("TEXT", Text, $"宽度因子≠1 {TextWidthFactor} / 倾斜≠0 {TextOblique} / 旋转≠0 {TextRotated}");
        L("  └ 水平对齐≠左", TextHAlign, "已实现（对齐点换算到基线左）", Text);
        L("  └ 垂直对齐≠基线", TextVAlign, "已实现（对齐点换算到基线左）", Text);
        L("MTEXT", MText, $"对齐点≠左上 {MTextAttachment} / 有宽度 {MTextWidth} / 宽度因子≠1 {MTextWidthFactor}");
        L("HATCH", Hatch, $"实心 {HatchSolid} / 图案 {HatchPattern}");
        L("  └ 多环(含孤岛)", HatchMultiPath, "已实现（合并成单路径 + 奇偶规则）", Hatch);
        L("SPLINE", Spline, $"有理(权重≠1) {SplineRational} / 闭合 {SplineClosed}");
        L("多段线", Polyline, $"有宽度 {PolyWidth} / 宽度渐变 {PolyTaper}", Polyline);
        L("INSERT", Insert, $"带属性 {InsertWithAttrib}（属性文字 {AttribCount} 条，当前被忽略）", Insert);
        L("DIMENSION", Dimension, "按块内几何绘制（不含 DIMSCALE 处理）");
        L("REGION", Region, "完全不渲染（ACadSharp 不读 ACIS）");
        L("代理实体", Proxy, "未支持");
        if (LineWeightValues.Count > 0)
            sb.AppendLine("  线宽分布: " + string.Join(", ", LineWeightValues.OrderByDescending(k => k.Value).Take(8).Select(k => $"{k.Key}×{k.Value}")));
        if (TextFonts.Count > 0)
            sb.AppendLine("  字体: " + string.Join(", ", TextFonts.OrderByDescending(k => k.Value).Take(6).Select(k => $"{k.Key}×{k.Value}")));
        return sb.ToString();
    }
}
