// MTEXT 行距 / \H / 汉字的受控基准生成器（AGENTS 第 21 节那组数的出处）。
//
// 三份基准各有分工，都带 L 形标定线（4 条独立 LINE 拼成 3000×1800 的框）：
//   sample_mtext_line_spacing.dxf  A 无 \H / B {\H0.5x;…} / C 实体 h=20 / D 组码 44=0.96
//   sample_mtext_cjk_h.dxf         汉字 {\H0.76x;田田}（GBK 字节 + $DWGCODEPAGE=ANSI_936）
//   sample_mtext_block_h.dxf       块内 height=2.2 + \H0.76x，插入 scale=2 rot=-90（复刻图签备注块）
//
// 用法：dotnet run --project tools/MTextGen -c Release -- [输出目录，默认仓库的 samples/]
//
// 两个坑（都踩过）：
//   1. R12(AC1009) 的 DXF 里 ODA 不画 MTEXT，只出图框 —— 必须 AC1015。
//   2. Polyline 画的外框在 ODA 里 bbox 比例不对（1219×465），必须用独立 LINE 才能拿回 3000×1800。
//   3. ACadSharp 写 DXF 会把中文写成 '?'，所以汉字走"占位符 + 事后按字节替换成 GBK"。

using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;

string outDir = args.Length > 0 ? args[0] : FindSamplesDir();
Directory.CreateDirectory(outDir);

// ---------- 行距 / \H（西文）----------
{
    (var doc, var st) = NewDoc();

    // A：基准，无 \H，h=40
    MText(doc, st, 200, 1600, @"L1\PL2\PL3\PL4");
    // B：同 h，内联 \H0.5x —— ODA 只缩字不缩行距（实测 66.6）
    MText(doc, st, 1200, 1600, @"{\H0.5x;L1\PL2\PL3\PL4}");
    // C：控制组，实体 h=20 —— 若 B 的行距等于 C，说明 \H 缩了行距
    MText(doc, st, 2200, 1600, @"L1\PL2\PL3\PL4", 20);
    // D：组码 44 = 0.96，验证行距常数 1.667
    MText(doc, st, 200, 700, @"L1\PL2\PL3\PL4").LineSpacing = 0.96;

    Save(doc, Path.Combine(outDir, "sample_mtext_line_spacing.dxf"));
}

// ---------- 汉字 + \H ----------
{
    (var doc, var st) = NewDoc();
    // 占位符在这一步换成 GBK 字节（见下方 PatchCjk）
    MText(doc, st, 200, 1600, @"{\H0.76x;@CN1@\P@CN2@\P@CN3@}", 40);
    // 对照组：同一串但不带 \H，看字宽是否只由 \W 决定
    MText(doc, st, 1200, 1600, @"@CN1@\P@CN2@\P@CN3@", 40);

    string path = Path.Combine(outDir, "sample_mtext_cjk_h.dxf");
    Save(doc, path);
    PatchCjk(path);
}

// ---------- 块内小字高 + \H ----------
{
    (var doc, var st) = NewDoc();

    var br = new BlockRecord("B1");
    doc.BlockRecords.Add(br);
    var inner = new MText
    {
        InsertPoint = new CSMath.XYZ(0, 0, 0),
        Height = 2.2,
        Value = @"{\H0.76x;L1\PL2\PL3}",
        AttachmentPoint = AttachmentPointType.TopLeft,
        Style = st,
        Layer = doc.Layers["0"],
    };
    br.Entities.Add(inner);
    doc.Entities.Add(new Insert(br)
    {
        InsertPoint = new CSMath.XYZ(600, 900, 0),
        XScale = 2,
        YScale = 2,
        ZScale = 2,
        Rotation = -Math.PI / 2,
        Layer = doc.Layers["0"],
    });

    // 同参数、不进块的对照
    MText(doc, st, 1500, 900, @"{\H0.76x;L1\PL2\PL3}", 2.2);

    Save(doc, Path.Combine(outDir, "sample_mtext_block_h.dxf"));
}

Console.WriteLine("done -> " + outDir);


// ================= 辅助 =================

static (CadDocument Doc, TextStyle Style) NewDoc()
{
    var doc = new CadDocument();
    doc.Header.Version = ACadVersion.AC1015;

    var st = new TextStyle("S1") { Filename = "txt.shx", BigFontFilename = "", Height = 0, Width = 1.0 };
    doc.TextStyles.Add(st);
    var layer0 = doc.Layers["0"];

    // 标定线：4 条独立 LINE，墨迹 bbox 精确等于 (0,0)-(3000,1800)
    void L(double x0, double y0, double x1, double y1) =>
        doc.Entities.Add(new Line
        {
            StartPoint = new CSMath.XYZ(x0, y0, 0),
            EndPoint = new CSMath.XYZ(x1, y1, 0),
            Layer = layer0,
        });

    L(0, 0, 3000, 0);
    L(0, 0, 0, 1800);
    L(3000, 0, 3000, 1800);
    L(0, 1800, 3000, 1800);

    return (doc, st);
}

static MText MText(CadDocument doc, TextStyle st, double x, double y, string value, double height = 40)
{
    var m = new MText
    {
        InsertPoint = new CSMath.XYZ(x, y, 0),
        Height = height,
        Value = value,
        AttachmentPoint = AttachmentPointType.TopLeft,
        Style = st,
        Layer = doc.Layers["0"],
    };
    doc.Entities.Add(m);
    return m;
}

static void Save(CadDocument doc, string path)
{
    DxfWriter.Write(path, doc, false);
    Console.WriteLine("written " + path);
}

/// <summary>占位符换成 GBK 汉字，并把代码页从 1252 改成 936（ODA 与我方都按这个读）。</summary>
static void PatchCjk(string path)
{
    var bytes = File.ReadAllBytes(path);
    var enc = System.Text.Encoding.GetEncoding(936);

    // 代码页：ACadSharp 写的是 ANSI_1252，改成 936 才能按双字节读中文
    var head = bytes.AsSpan().IndexOf("ANSI_1252"u8);
    if (head < 0) throw new InvalidOperationException("DXF 里找不到 ANSI_1252，代码页补丁失效");
    ReplaceOnce(ref bytes, head, "ANSI_1252"u8, "ANSI_936"u8);

    for (int i = 1; i <= 3; i++)
        bytes = ReplaceAll(bytes, System.Text.Encoding.ASCII.GetBytes($"@CN{i}@"), enc.GetBytes("田田田田"));

    File.WriteAllBytes(path, bytes);
}

static void ReplaceOnce(ref byte[] data, int at, ReadOnlySpan<byte> from, ReadOnlySpan<byte> to)
{
    var outBuf = new List<byte>(data.Length);
    outBuf.AddRange(data.AsSpan(0, at).ToArray());
    outBuf.AddRange(to.ToArray());
    outBuf.AddRange(data.AsSpan(at + from.Length).ToArray());
    data = outBuf.ToArray();
}

static byte[] ReplaceAll(byte[] data, byte[] from, byte[] to)
{
    var outBuf = new List<byte>(data.Length + 64);
    for (int i = 0; i < data.Length;)
    {
        if (i + from.Length <= data.Length && data.AsSpan(i, from.Length).SequenceEqual(from))
        {
            outBuf.AddRange(to);
            i += from.Length;
        }
        else
        {
            outBuf.Add(data[i]);
            i++;
        }
    }
    return outBuf.ToArray();
}

static string FindSamplesDir()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "samples")))
        dir = dir.Parent;
    return dir is null
        ? Directory.GetCurrentDirectory()
        : Path.Combine(dir.FullName, "samples");
}
