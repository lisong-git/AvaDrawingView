using System.Collections.Concurrent;
using System.Text;
using SkiaSharp;

namespace DrawingView.Core;

/// <summary>
/// SHX（AutoCAD 编译形/字体）解析与描边解释器。
///
/// <para><b>为什么要有它</b>：国内图纸绝大多数文字挂在 SHX 单线字体上（图集普查：
/// 文本实体的 69% 落在 .shx 样式上），而 TTF 回退的字宽、字形都与之一致性差
/// （受控基准实测：数字窄 17.5%、W 宽 20%）。SHX 字形本身就是**折线/圆弧序列**，
/// 直接按矢量描出来，字宽与字形都能和 AutoCAD 对齐，且不必再猜"哪个 TTF 像它"。</para>
///
/// <para><b>文件结构</b>（实测自 simplex / gbenor / gbcbig / whgtxt / chineset）：</para>
/// <code>
/// 头部   "AutoCAD-86 &lt;kind&gt; &lt;ver&gt;" + 0D 0A 1A     （kind = shapes / unifont / bigfont）
/// unifont   count  u32 | idxLen u16 | 名称(0 结尾) | above below modes enc emb pad(6B)
///           然后 count-1 条：code u16 | len u16 | 数据 len 字节（数据前带 "A\0" 名字）
/// bigfont   前导 u16(实测 8) | count u16 | nChange u16 | nChange×(start,end) u16
///           然后 count 条：code u16 | len u16 | offset u32（数据区内联前 6 字节前缀）
/// </code>
/// <para>自校验：unifont 走完索引必须正好到文件尾；bigfont 的"索引尾 == 首条数据偏移"。</para>
///
/// <para><b>解释器</b>：每个字节 = 高 4 位长度 + 低 4 位方向（16 个 22.5° 方向），
/// 长度为 0 时低 4 位是特殊指令（0x0 结束 / 0x1 落笔 / 0x2 抬笔 / 0x3 除 / 0x4 乘 /
/// 0x5 压栈 / 0x6 出栈 / 0x7 子形 / 0x8 XY 位移 / 0x9 多段 XY / 0xA 八分弧 /
/// 0xB 分数弧 / 0xC 凸度弧 / 0xD 多段凸度弧 / 0xE 仅垂直排版时执行下一条）。</para>
///
/// <para>两个反直觉的点（都踩过，见 .compare/shx_probe.py 的注释）：
/// ① unifont 字形数据带 ASCII 名字前缀，有的还以 1~2 个 0x00 开头，必须剥；
/// ② 0xE 在水平排版下要求"下一条命令连参数一起丢弃"，只忽略指令本身会把字形
/// 起点错移到 (0, -above)；③ 八分弧的 bit7 是**终点方向**（顺时针减 c），
/// 把起点取负会把弧镜像到另一侧 —— simplex 全是逆时针所以看不出来，gbenor 的 C/G 立刻变形。</para>
///
/// <para>线程安全：解析与字形缓存都是只读发布（<see cref="ConcurrentDictionary{TKey,TValue}"/> 惰性填充），
/// 可在渲染/加载多线程同时用。</para>
/// </summary>
public sealed class ShxFont
{
    /// <summary>字形数据里"无效/缺字"的返回值。</summary>
    public sealed class Glyph
    {
        /// <summary>折线（字体单位，y 向上，原点在基线左端）。圆弧已按 1/8 圆采样成折线。</summary>
        public required IReadOnlyList<SKPoint[]> Polylines { get; init; }

        /// <summary>前进量（字体单位）。等于解释器执行完最后一个命令后的 x。</summary>
        public float Advance { get; init; }
    }

    public enum FontKind { Shapes, Unifont, BigFont }

    private readonly Dictionary<int, byte[]> _raw;
    private readonly ConcurrentDictionary<int, Glyph?> _cache = new();
    private readonly Encoding? _gbk;

    public FontKind Kind { get; }

    /// <summary>字体内部名（unifont 才有，bigfont 一般没有）。</summary>
    public string Name { get; } = string.Empty;

    /// <summary>基线上方可用的矢量长度（字体单位）。</summary>
    public int Above { get; }

    /// <summary>基线下方可用的矢量长度（字体单位）。</summary>
    public int Below { get; }

    /// <summary>字形数量。</summary>
    public int GlyphCount => _raw.Count;

    /// <summary>字体文件路径（诊断用）。</summary>
    public string Path { get; }

    private ShxFont(string path, FontKind kind, Dictionary<int, byte[]> raw, int above, int below, string name)
    {
        Path = path;
        Kind = kind;
        _raw = raw;
        Above = above;
        Below = below;
        Name = name;
        _gbk = kind == FontKind.BigFont ? TryGetGbk() : null;
    }

    // ---------- 载入 ----------

    private static readonly ConcurrentDictionary<string, ShxFont?> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>按路径载入并缓存（失败缓存为 null，避免每张图重复尝试同一个坏文件）。</summary>
    public static ShxFont? TryLoad(string path, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(path)) { error = "路径为空"; return null; }
        string full;
        try { full = System.IO.Path.GetFullPath(path); }
        catch (Exception ex) { error = ex.Message; return null; }
        if (!File.Exists(full)) { error = "文件不存在"; return null; }

        var font = Cache.GetOrAdd(full, p => Load(p, out _));
        if (font is null) { error = "解析失败"; return null; }
        return font;
    }

    private static ShxFont? Load(string path, out string? error)
    {
        error = null;
        byte[] d;
        try { d = File.ReadAllBytes(path); }
        catch (Exception ex) { error = ex.Message; return null; }
        if (d.Length < 32) { error = "文件过短"; return null; }

        // ---- 头部：以 0x0D 结尾的 "AutoCAD-86 <kind> <ver>" ----
        int cr = Array.IndexOf(d, (byte)0x0D);
        if (cr < 10 || cr > 40) { error = "缺少 AutoCAD-86 头部"; return null; }
        string header = Encoding.ASCII.GetString(d, 0, cr);
        var parts = header.Split(' ');
        if (parts.Length != 3 || !parts[0].StartsWith("AutoCAD-86", StringComparison.Ordinal))
        { error = $"头部格式不认识：{header}"; return null; }

        var kind = parts[1] switch
        {
            "unifont" => FontKind.Unifont,
            "bigfont" => FontKind.BigFont,
            "shapes" => FontKind.Shapes,
            _ => (FontKind)(-1),
        };
        if ((int)kind < 0) { error = $"字体类型不支持：{parts[1]}"; return null; }

        int i = cr + 1;
        while (i < d.Length && d[i] is 0x0D or 0x0A or 0x1A) i++;

        var raw = new Dictionary<int, byte[]>(1024);
        int above = 0, below = 0;
        string name = string.Empty;

        try
        {
            if (kind is FontKind.Unifont or FontKind.Shapes)
            {
                int count = (int)ReadU32(d, i); i += 4;
                i += 2;                                   // 索引区字节数（未用）
                int z = Array.IndexOf(d, (byte)0, i);
                if (z < 0) { error = "字体名未结束"; return null; }
                name = Encoding.Latin1.GetString(d, i, z - i);
                i = z + 1;
                if (i + 6 > d.Length) { error = "字体参数缺失"; return null; }
                above = d[i]; below = d[i + 1];
                i += 6;                                   // above/below/modes/enc/emb/保留
                for (int k = 0; k < count - 1 && i + 4 <= d.Length; k++)
                {
                    int code = d[i] | (d[i + 1] << 8);
                    int len = d[i + 2] | (d[i + 3] << 8);
                    i += 4;
                    if (len <= 0 || i + len > d.Length) break;
                    raw[code] = StripGlyphPrefix(d, i, len, code);
                    i += len;
                }
                if (i != d.Length && raw.Count > 0 && i > d.Length)
                { error = "索引越界"; return null; }
            }
            else
            {
                i += 2;                                   // 前导 u16（实测恒 8，用途未知）
                int count = d[i] | (d[i + 1] << 8); i += 2;
                int nChange = d[i] | (d[i + 1] << 8); i += 2;
                i += 4 * nChange;                         // 内码区间表（渲染不用）
                if (count <= 0 || i + 8L * count > d.Length) { error = "大字体索引长度不合理"; return null; }
                int firstOffset = int.MaxValue;
                var refs = new (int Code, int Len, int Off)[count];
                for (int k = 0; k < count; k++)
                {
                    int code = d[i] | (d[i + 1] << 8);
                    int len = d[i + 2] | (d[i + 3] << 8);
                    int off = (int)ReadU32(d, i + 4);
                    i += 8;
                    refs[k] = (code, len, off);
                    if (off > 0) firstOffset = Math.Min(firstOffset, off);
                }
                // 自校验：索引区尾部应当紧接着第一条字形数据
                if (firstOffset != int.MaxValue && firstOffset != i)
                {
                    error = $"索引尾 {i} 与首条数据 {firstOffset} 不吻合";
                    return null;
                }
                foreach (var (code, len, off) in refs)
                {
                    if (code == 0 || len <= 0 || off <= 0 || off + len > d.Length) continue;
                    raw[code] = StripGlyphPrefix(d, off, len, code);
                }
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }

        if (raw.Count == 0) { error = "没有解出任何字形"; return null; }
        return new ShxFont(path, kind, raw, above, below, name);
    }

    /// <summary>
    /// 剥掉字形数据前缀。
    /// unifont：可打印 ASCII 名字 + 0x00（"A\0"），有的还直接以 1~2 个 0x00 开头；
    /// bigfont：内码(2) + 00 07 8e 05 共 6 字节固定前缀。
    /// 判据都要求"前缀里出现 0x00"——命令流不会以这种组合开头（首条通常是 0x01/0x02）。
    /// </summary>
    private static byte[] StripGlyphPrefix(byte[] d, int off, int len, int code)
    {
        int start = off, end = off + len;
        if (end - start > 6 && d[start] == (byte)(code >> 8) && d[start + 1] == (byte)(code & 0xFF) && d[start + 2] == 0)
            start += 6;
        else
        {
            if (d[start] is >= 0x20 and <= 0x7E)
            {
                for (int k = start; k < Math.Min(start + 3, end); k++)
                {
                    if (d[k] != 0) continue;
                    start = k + 1;
                    break;
                }
            }
            int z = 0;
            while (z < 2 && start + z < end && d[start + z] == 0) z++;
            start += z;
        }
        var body = new byte[end - start];
        Array.Copy(d, start, body, 0, body.Length);
        return body;
    }

    private static uint ReadU32(byte[] d, int i)
        => (uint)(d[i] | (d[i + 1] << 8) | (d[i + 2] << 16) | (d[i + 3] << 24));

    // ---------- 字高换算 ----------

    private float? _capUnits;

    /// <summary>
    /// 一个"字高"对应多少字体单位（惰性实测）。
    ///
    /// <para>拿几个典型字形的**上沿 y** 取最大：ANSI 字体用 H/E/X，大字体用常用汉字。
    /// 直接用头部的 above 不行 —— 实测 gbenor 的 above=48 而 'H' 上沿只有 38
    /// （above 是字身框，不是大写字高），按 above 换算字会小 21%。</para>
    /// </summary>
    public float CapUnits
    {
        get
        {
            if (_capUnits is { } v) return v;
            ReadOnlySpan<char> sample = Kind == FontKind.BigFont
                ? "国工日中田山大小上建设说明平面图口"
                : "HEXN0A";
            float max = 0f;
            foreach (char c in sample)
            {
                if (GetGlyph(c) is not { } g) continue;
                foreach (var poly in g.Polylines)
                foreach (var p in poly)
                    if (p.Y > max) max = p.Y;
            }
            if (max < 1f) max = Kind == FontKind.BigFont ? 100f : Math.Max(Above, 1);
            _capUnits = max;
            return max;
        }
    }

    // ---------- 取字形 ----------

    /// <summary>按字符取字形（缓存）。字符不在字体里返回 null。</summary>
    public Glyph? GetGlyph(char c)
    {
        int code = ToCode(c, out _);
        if (code < 0) return null;
        return _cache.GetOrAdd(code, k => _raw.TryGetValue(k, out var body) ? Interpret(body) : null);
    }

    /// <summary>按原始代码取字形（测试/诊断用）。</summary>
    public Glyph? GetGlyphByCode(int code)
        => _cache.GetOrAdd(code, k => _raw.TryGetValue(k, out var body) ? Interpret(body) : null);

    /// <summary>字体里是否有这个字符。</summary>
    public bool Has(char c)
    {
        int code = ToCode(c, out _);
        return code >= 0 && _raw.ContainsKey(code);
    }

    /// <summary>字符 → 文件里的字形代码。返回 -1 表示该字体不管这个字符。</summary>
    private int ToCode(char c, out int raw)
    {
        raw = c;
        if (Kind == FontKind.BigFont)
        {
            // 大字体按 GB2312/GBK 内码索引。文件里是"低字节在前"存的双字节，
            // 而字符侧按正常顺序取高/低字节（实测：'施' = CA A9 → 键 0xCAA9）。
            if (_gbk is null) return -1;
            Span<char> chs = stackalloc char[1];
            chs[0] = c;
            Span<byte> buf = stackalloc byte[2];
            try
            {
                return _gbk.GetBytes(chs, buf) == 2 ? (buf[0] << 8) | buf[1] : -1;
            }
            catch
            {
                return -1;      // 非 GBK 字符
            }
        }
        return c;
    }

    private static Encoding? TryGetGbk()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback);
        }
        catch
        {
            return null;   // 没有 936 代码页：大字体不支持，调用方回退 TTF
        }
    }

    // ---------- 解释执行 ----------

    private sealed class Pen
    {
        public readonly List<SKPoint[]> Polys = new();
        public List<SKPoint> Cur = new();
        public readonly List<SKPoint> Stack = new();
        public float X, Y, Scale;
        public bool Down = true;

        public void Move()
        {
            if (Cur.Count > 1) Polys.Add(Cur.ToArray());
            Cur = new List<SKPoint>(8) { new(X, Y) };
        }

        public void Line() => Cur.Add(new SKPoint(X, Y));

        public void Emit()
        {
            if (Down) Line();
            else Move();
        }

        public void Flush()
        {
            if (Cur.Count > 1) Polys.Add(Cur.ToArray());
            Cur.Clear();
        }
    }

    private Glyph? Interpret(byte[] body)
    {
        var pen = new Pen { Scale = 1f };
        try
        {
            Run(body, pen, 0);
        }
        catch
        {
            return null;   // 命令流截断/越界：当作缺字，不抛到渲染层
        }
        pen.Flush();
        return new Glyph { Polylines = pen.Polys, Advance = pen.X };
    }

    /// <summary>
    /// 执行一段命令流。<paramref name="pen"/> 的坐标/笔/比例在子形之间共享，
    /// 与 AutoCAD 的"子形接着父形当前位置继续画"一致。
    /// </summary>
    private void Run(byte[] g, Pen pen, int depth)
    {
        if (depth > 4) return;     // 子形递归保护（真字体不会超过 2 层）

        int i = 0;
        bool skip = false;
        while (i < g.Length)
        {
            byte b = g[i++];
            bool run = !skip;
            skip = false;
            int dir = b & 0x0F;
            int len = (b & 0xF0) >> 4;

            if (len != 0)
            {
                // 16 个方向按 22.5° 细分：dx/dy ∈ {-1,-0.5,0,0.5,1}
                float dx = dir switch
                {
                    0x0 or 0x1 or 0x2 or 0xE or 0xF => 1f,
                    0x3 or 0xD => 0.5f,
                    0x4 or 0xC => 0f,
                    0x5 or 0xB => -0.5f,
                    _ => -1f,
                };
                float dy = dir switch
                {
                    0x2 or 0x3 or 0x4 or 0x5 or 0x6 => 1f,
                    0x1 or 0x7 => 0.5f,
                    0x0 or 0x8 => 0f,
                    0x9 or 0xF => -0.5f,
                    _ => -1f,
                };
                pen.X += dx * len * pen.Scale;
                pen.Y += dy * len * pen.Scale;
                if (run) pen.Emit();
                continue;
            }

            switch (dir)
            {
                case 0x0:   // END_OF_SHAPE：吃掉到 0 为止的填充
                    while (i < g.Length && g[i++] != 0) { }
                    return;

                case 0x1:   // PEN_DOWN
                    pen.Down = true;
                    if (run) pen.Move();
                    break;

                case 0x2:   // PEN_UP
                    pen.Down = false;
                    break;

                case 0x3:   // DIVIDE_VECTOR
                {
                    if (i >= g.Length) return;
                    byte f = g[i++];
                    if (run && f != 0) pen.Scale /= f;
                    break;
                }

                case 0x4:   // MULTIPLY_VECTOR
                {
                    if (i >= g.Length) return;
                    byte f = g[i++];
                    if (run && f != 0) pen.Scale *= f;
                    break;
                }

                case 0x5:   // PUSH_STACK
                    if (run) pen.Stack.Add(new SKPoint(pen.X, pen.Y));
                    break;

                case 0x6:   // POP_STACK
                    if (run && pen.Stack.Count > 0)
                    {
                        var p = pen.Stack[^1];
                        pen.Stack.RemoveAt(pen.Stack.Count - 1);
                        pen.X = p.X;
                        pen.Y = p.Y;
                        pen.Move();
                    }
                    break;

                case 0x7:   // DRAW_SUBSHAPE
                {
                    int sub = -1;
                    if (Kind == FontKind.Unifont)
                    {
                        if (i + 1 >= g.Length) return;
                        sub = (g[i] << 8) | g[i + 1];
                        i += 2;
                    }
                    else if (Kind == FontKind.BigFont)
                    {
                        // 大字体子形是**双字节内码**，且找不到就"不画"，不能退回单字节。
                        //
                        // gbcbig 的每条记录是 "<内码> 00 07 8e 05 <命令流> 07 8f 00"（7016/7018 条
                        // 尾部都是这 3 个字节），其中 0x8F00 / 0x8E05 都不是有效内码 —— 是编码器
                        // 留下的空子形引用。若退回单字节读成 0x8F，会命中 0x008F（"静夜思"整首诗
                        // 的字形），于是每个汉字底下多出一坨星形图案。
                        if (i + 1 >= g.Length) return;
                        sub = (g[i] << 8) | g[i + 1];
                        i += 2;
                    }
                    else
                    {
                        if (i >= g.Length) return;
                        sub = g[i++];
                    }
                    if (run && sub >= 0 && _raw.TryGetValue(sub, out var sg)) Run(sg, pen, depth + 1);
                    break;
                }

                case 0x8:   // XY_DISPLACEMENT
                {
                    if (i + 1 >= g.Length) return;
                    float dx = (sbyte)g[i] * pen.Scale;
                    float dy = (sbyte)g[i + 1] * pen.Scale;
                    i += 2;
                    if (run)
                    {
                        pen.X += dx;
                        pen.Y += dy;
                        pen.Emit();
                    }
                    break;
                }

                case 0x9:   // POLY_XY_DISPLACEMENT（(0,0) 结束）
                    while (i + 1 < g.Length)
                    {
                        float dx = (sbyte)g[i] * pen.Scale;
                        float dy = (sbyte)g[i + 1] * pen.Scale;
                        i += 2;
                        if (dx == 0 && dy == 0) break;
                        if (!run) continue;
                        pen.X += dx;
                        pen.Y += dy;
                        pen.Emit();
                    }
                    break;

                case 0xA:   // OCTANT_ARC：radius u8 + (start<<4 | span) u8
                case 0xB:   // FRACTIONAL_ARC：startOff u8 + endOff u8 + radius u16 + 同上
                {
                    float r, a0 = 0f, a1 = 0f;
                    if (dir == 0xA)
                    {
                        if (i + 1 >= g.Length) return;
                        r = g[i] * pen.Scale;
                        i += 1;
                    }
                    else
                    {
                        if (i + 3 >= g.Length) return;
                        a0 = 45f * g[i] / 256f;
                        a1 = 45f * g[i + 1] / 256f;
                        r = ((g[i + 2] << 8) | g[i + 3]) * pen.Scale;
                        i += 4;
                    }
                    if (i >= g.Length) return;
                    byte sc = g[i++];
                    if (run) OctantArc(pen, r, a0, a1, sc);
                    break;
                }

                case 0xC:   // BULGE_ARC：dx u8 + dy u8 + bulge u8
                {
                    if (i + 2 >= g.Length) return;
                    float dx = (sbyte)g[i] * pen.Scale;
                    float dy = (sbyte)g[i + 1] * pen.Scale;
                    int h = (sbyte)g[i + 2];
                    i += 3;
                    if (run) Bulge(pen, dx, dy, h);
                    break;
                }

                case 0xD:   // POLY_BULGE_ARC
                    while (i + 2 < g.Length)
                    {
                        float dx = (sbyte)g[i] * pen.Scale;
                        float dy = (sbyte)g[i + 1] * pen.Scale;
                        int h = (sbyte)g[i + 2];
                        i += 3;
                        if (dx == 0 && dy == 0) break;
                        if (run) Bulge(pen, dx, dy, h);
                    }
                    break;

                case 0xE:   // COND_MODE_2：水平排版时"下一条命令连参数一起丢弃"
                    skip = true;
                    break;
            }
        }
    }

    /// <summary>
    /// 八分弧：<paramref name="sc"/> 高 4 位是起始八分圆号、低 4 位是跨的八分圆数、
    /// bit7 是方向（1 = 顺时针，终点减 span）。圆心在起点的径向内侧。
    /// </summary>
    private static void OctantArc(Pen pen, float r, float a0, float a1, byte sc)
    {
        int s = (sc >> 4) & 0x7;
        int span = sc & 0x7;
        bool cw = (sc & 0x80) != 0;
        if (span == 0) span = 8;

        // 顺时针：起点不动、终点减 span（取负起点会把弧镜像到另一侧 —— gbenor 的 C/G 就是这么坏的）
        float startDeg = a0 + 45f * s;
        float endDeg = a1 + 45f * (cw ? s - span : s + span);

        double start = startDeg * Math.PI / 180.0;
        double end = endDeg * Math.PI / 180.0;
        float cx = pen.X - r * (float)Math.Cos(start);
        float cy = pen.Y - r * (float)Math.Sin(start);

        const int Steps = 12;
        for (int k = 1; k <= Steps; k++)
        {
            double a = start + (end - start) * k / Steps;
            pen.X = cx + r * (float)Math.Cos(a);
            pen.Y = cy + r * (float)Math.Sin(a);
            pen.Line();
        }
    }

    /// <summary>凸度弧：bulge = 矢高 / 半弦长，按二次曲线采样（与圆弧在端点相切）。</summary>
    private static void Bulge(Pen pen, float dx, float dy, int h)
    {
        if (h == 0 || (dx == 0 && dy == 0))
        {
            pen.X += dx;
            pen.Y += dy;
            pen.Line();
            return;
        }
        float len = MathF.Sqrt(dx * dx + dy * dy);
        float sag = h / 127f * (len * 0.5f);
        float mx = pen.X + dx * 0.5f, my = pen.Y + dy * 0.5f;
        float nx = -dy / len, ny = dx / len;
        float qx = mx + nx * 2f * sag, qy = my + ny * 2f * sag;

        float x0 = pen.X, y0 = pen.Y;
        const int Steps = 8;
        for (int k = 1; k <= Steps; k++)
        {
            float t = k / (float)Steps;
            float u = 1f - t;
            pen.X = u * u * x0 + 2f * u * t * qx + t * t * (x0 + dx);
            pen.Y = u * u * y0 + 2f * u * t * qy + t * t * (y0 + dy);
            pen.Line();
        }
        pen.X = x0 + dx;
        pen.Y = y0 + dy;
    }
}
