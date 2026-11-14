using System.Collections.Concurrent;
using SkiaSharp;

namespace DrawingView.Core;

/// <summary>
/// 文字样式 → <see cref="TextFont"/> 的解析与缓存（P2-16）。
///
/// <para><b>样式里的"字体"字段到底怎么用</b>。AutoCAD 的 STYLE 有两个槽位：
/// 主字体管西文、大字体（bigfont）管中文；值可能是 <c>simplex.shx</c>（单线形字体）、
/// <c>romans.shx + chinese.shx</c>（西文 + 中文大字体）、<c>arial.ttf</c>、<c>SimSun.ttf</c>。
/// 本类的职责是把这个名字翻成一个能画的 TrueType 字体，**顺序如下**：</para>
/// <list type="number">
/// <item>样式本来写的就是 TTF/OTF → 按文件名找（图形目录 → 注册目录 → 环境变量 → Windows 字体目录），
///       找不到再按家族名问系统；</item>
/// <item>样式是 SHX、但系统装着 AutoCAD 的同名 TrueType 替身（<c>romans.shx → romans__.ttf</c>、
///       <c>simplex.shx → simplex_.ttf</c>、<c>txt.shx → monotxt_.ttf</c>）→ 用替身。
///       这类替身是 AutoCAD 自带的，字形与字宽与 SHX 基本一致，远比随手挑一个 TTF 准；</item>
/// <item>SHX 没有替身 → 中文走通用中文字体（宋体一族，全角等宽，与 hztxt/gbcbig 排布最接近），
///       西文走宋体的西文部分；</item>
/// <item>都没有 → 内置默认（微软雅黑），即 2026-11-13 之前的行为。</item>
/// </list>
///
/// <para><b>为什么要判中文</b>：同一条 MTEXT 常是"备注 Notes"这种混排。AutoCAD 按字符分流
/// （西文用主字体、中文用大字体），我们只有一条字形通道，只能整条选一个字体 ——
/// 含中文就选覆盖中文的那个（否则 Skia 会拿系统默认字体去补，字宽完全失控），
/// 纯西文才用西文替身。这是已知近似，逐字符分流的代价（要把一条文字拆成多段 draw call，
/// 还要重算每段基线）远大于收益。</para>
///
/// <para><b>和 SHX 那条路的关系</b>：<see cref="ShxFontLibrary"/> 能真正解析出 SHX 时优先走它
/// （描出来就是 AutoCAD 的折线字形）；本类只在 SHX 不可用时兜底，两条路量的宽度
/// 都经 <see cref="TextFont"/> / <see cref="ShxFontLibrary"/> 的自洽换算，不会互相打架。</para>
/// </summary>
public static class TextFontLibrary
{
    /// <summary>环境变量名：额外字体目录（分号分隔），与 <see cref="ShxFontLibrary.FontPathEnv"/> 同名同义。</summary>
    public const string FontPathEnv = ShxFontLibrary.FontPathEnv;

    private static readonly ConcurrentDictionary<string, TextFont> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> Registered = new();
    private static readonly object DirLock = new();

    /// <summary>
    /// 注册图形所在目录（每份文档调用一次，放最前）。图纸自带 <c>.ttf</c> 的情形比想象中多，
    /// 而且必须优先于系统字体 —— 同名不同版本的宋体，字宽会差几个百分点。
    /// </summary>
    public static void SetDrawingDirectory(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;
        lock (DirLock)
        {
            Registered.RemoveAll(d => string.Equals(d, dir, StringComparison.OrdinalIgnoreCase));
            Registered.Insert(0, dir);
            if (Registered.Count > 8) Registered.RemoveAt(Registered.Count - 1);
        }
        Cache.Clear();   // 目录变了，之前"找不到"的解析结果要失效
    }

    /// <summary>追加字体目录（例如 AutoCAD 的 Fonts 目录）。</summary>
    public static void AddSearchDirectory(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;
        lock (DirLock)
        {
            if (!Registered.Contains(dir, StringComparer.OrdinalIgnoreCase))
                Registered.Add(dir);
        }
        Cache.Clear();
    }

    /// <summary>当前搜索目录（诊断用）。系统字体目录与环境变量总是追加在末尾。</summary>
    public static IReadOnlyList<string> Directories
    {
        get
        {
            var list = new List<string>();
            lock (DirLock) list.AddRange(Registered);
            string? env = Environment.GetEnvironmentVariable(FontPathEnv);
            if (!string.IsNullOrWhiteSpace(env))
                list.AddRange(env.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            string win = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
            if (Directory.Exists(win)) list.Add(win);
            return list;
        }
    }

    /// <summary>
    /// 按样式解析字体。<paramref name="text"/> 只用来判断"要不要中文字体"，
    /// 传空串按西文处理。
    /// </summary>
    public static TextFont Resolve(string? styleFont, string? bigFont, string? text)
        => Resolve(styleFont, bigFont, HasCjk(text));

    /// <summary>按样式解析字体，显式指定是否需要中文字形。</summary>
    public static TextFont Resolve(string? styleFont, string? bigFont, bool prefersCjk)
    {
        string key = $"{styleFont ?? string.Empty}\u0001{bigFont ?? string.Empty}\u0001{(prefersCjk ? "cjk" : "lat")}";
        return Cache.GetOrAdd(key, _ => Build(styleFont, bigFont, prefersCjk));
    }

    /// <summary>文本里有没有汉字（含全角标点）。用于决定混排时选哪个字体。</summary>
    public static bool HasCjk(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (char c in text)
            if (IsCjk(c)) return true;
        return false;
    }

    private static bool IsCjk(char c)
        => (c >= 0x4E00 && c <= 0x9FFF)     // 基本区
        || (c >= 0x3400 && c <= 0x4DBF)     // 扩展 A
        || (c >= 0xF900 && c <= 0xFAFF)     // 兼容表意
        || (c >= 0x3000 && c <= 0x303F)     // 中文标点
        || (c >= 0xFF00 && c <= 0xFFEF);    // 全角

    private static TextFont Build(string? styleFont, string? bigFont, bool prefersCjk)
    {
        var ansi = TryResolveSlot(styleFont, preferCjk: false);
        var big = TryResolveSlot(bigFont, preferCjk: true);

        if (prefersCjk)
        {
            // 混排：谁覆盖得住中文就用谁；都不行才退回通用中文字体
            if (big is { CoversCjk: true }) return big;
            if (ansi is { CoversCjk: true }) return ansi;
            if (TryCommonCjk() is { } cjk) return cjk;
        }

        if (ansi is not null) return ansi;
        if (big is not null) return big;
        if (TryCommonCjk() is { } any) return any;
        return TextFont.Default;
    }

    /// <summary>解析一个槽位（主字体或大字体）。返回 null = 这个名字完全对不上任何可用字体。</summary>
    private static TextFont? TryResolveSlot(string? name, bool preferCjk)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string raw = name.Trim().Trim('"');
        if (raw.Length == 0) return null;

        // 已经是绝对路径：直接按文件加载
        if (Path.IsPathRooted(raw) && File.Exists(raw))
            return FromFile(raw, FileStem(raw), TextFontKind.TrueType);

        string stem = FileStem(raw);
        if (stem.Length == 0) return null;

        if (ShxFontLibrary.IsShxName(raw))
            return ResolveShxStandIn(stem, preferCjk);

        // 样式写的是 TTF/OTF
        return ResolveTrueTypeFile(raw, stem);
    }

    /// <summary>样式写 TTF：先按文件名在字体目录里找，再按家族名问系统。</summary>
    private static TextFont? ResolveTrueTypeFile(string raw, string stem)
    {
        foreach (string candidate in TrueTypeCandidates(raw, stem))
        {
            foreach (string dir in Directories)
            {
                string path = Path.Combine(dir, candidate);
                if (File.Exists(path)) return FromFile(path, stem, TextFontKind.TrueType);
            }
        }

        // 文件没找到 ≠ 字体没装（Windows 的字体文件名与家族名常不同，如 simsun.ttc → "SimSun"）
        return FromFamily(stem, raw, TextFontKind.TrueType);
    }

    private static IEnumerable<string> TrueTypeCandidates(string raw, string stem)
    {
        yield return raw;                       // 样式里带了正确扩展名
        if (FileAliases.TryGetValue(stem, out var known))
            foreach (string f in known) yield return f;
        yield return stem + ".ttf";
        yield return stem + ".ttc";
        yield return stem + ".otf";
    }

    /// <summary>样式写 SHX：找 AutoCAD 的同名 TrueType 替身，大字体槽位直接给中文字体。</summary>
    private static TextFont? ResolveShxStandIn(string stem, bool preferCjk)
    {
        // 大字体（中文）槽位：hztxt / gbcbig / chinese 这些本来就是中文字库，
        // 找不到 SHX 时用宋体顶替最接近（全角等宽）。
        if (preferCjk && BigFontHints.Contains(stem))
            return TryCommonCjk();

        foreach (string candidate in ShxStandInCandidates(stem))
        {
            foreach (string dir in Directories)
            {
                string path = Path.Combine(dir, candidate);
                if (File.Exists(path)) return FromFile(path, stem, TextFontKind.ShxStandIn);
            }
        }

        // 没有替身：先看这个 SHX 名字是不是中文字库（那就给中文 TTF）
        if (BigFontHints.Contains(stem)) return TryCommonCjk();

        // 西文 SHX 没替身：退到系统里最像的窄体字，宁可近似也不要雅黑那么宽
        foreach (string candidate in LatinFallbacks)
            foreach (string dir in Directories)
            {
                string path = Path.Combine(dir, candidate);
                if (File.Exists(path)) return FromFile(path, stem, TextFontKind.Substitute);
            }

        return FromFamily("Arial", stem, TextFontKind.Substitute);
    }

    private static IEnumerable<string> ShxStandInCandidates(string stem)
    {
        if (ShxStandIn.TryGetValue(stem, out var exact))
            foreach (string f in exact) yield return f;
        yield return stem + "_.ttf";     // AutoCAD 的命名习惯：romans.shx → romans__.ttf
        yield return stem + ".ttf";
    }

    /// <summary>通用中文字体：按"更像 CAD 图纸"的顺序试。宋体 > 微软雅黑 > 黑体。</summary>
    private static TextFont? TryCommonCjk()
    {
        foreach (string candidate in CjkFallbacks)
            foreach (string dir in Directories)
            {
                string path = Path.Combine(dir, candidate);
                if (File.Exists(path)) return FromFile(path, Path.GetFileNameWithoutExtension(candidate), TextFontKind.Substitute);
            }
        return FromFamily("SimSun", "simsun", TextFontKind.Substitute);
    }

    private static TextFont? FromFile(string path, string stem, TextFontKind kind)
    {
        try
        {
            var tf = SKTypeface.FromFile(path, 0);
            if (tf is null) return null;
            return new TextFont(tf.FamilyName ?? stem, Path.GetFileName(path), tf, kind);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 按家族名问系统。Skia 找不到时会返回**默认字体**而不是 null，
    /// 所以必须核对家族名，否则"Arial"永远"找得到"，把兜底逻辑全部吃掉。
    /// </summary>
    private static TextFont? FromFamily(string family, string stem, TextFontKind kind)
    {
        try
        {
            var tf = SKTypeface.FromFamilyName(family);
            if (tf is null) return null;
            string got = (tf.FamilyName ?? string.Empty).Replace(" ", string.Empty);
            string want = family.Replace(" ", string.Empty);
            bool matches = got.Equals(want, StringComparison.OrdinalIgnoreCase)
                           || got.StartsWith(want, StringComparison.OrdinalIgnoreCase);
            if (!matches && kind == TextFontKind.TrueType) return null;   // 样式点名要的字体没装
            return new TextFont(tf.FamilyName ?? stem, stem, tf, kind);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>去掉引号 / 目录 / <c>@</c> 前缀（竖排 TTF 的命名习惯）/ 扩展名。</summary>
    private static string FileStem(string name)
    {
        string s = name.Trim().Trim('"');
        int slash = s.LastIndexOfAny(['\\', '/']);
        if (slash >= 0) s = s[(slash + 1)..];
        if (s.StartsWith('@')) s = s[1..];
        int dot = s.LastIndexOf('.');
        if (dot > 0) s = s[..dot];
        return s.Trim();
    }

    /// <summary>文件名（不带扩展名）→ 实际字体文件候选。只列**文件名与家族名不同**的那些。</summary>
    private static readonly Dictionary<string, string[]> FileAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["simsun"] = ["simsun.ttc", "simsun.ttf"],
        ["宋体"] = ["simsun.ttc"],
        ["新宋体"] = ["simsun.ttc"],
        ["msyh"] = ["msyh.ttc"],
        ["微软雅黑"] = ["msyh.ttc"],
        ["simhei"] = ["simhei.ttf"],
        ["黑体"] = ["simhei.ttf"],
        ["simkai"] = ["simkai.ttf"],
        ["kaiti"] = ["simkai.ttf"],
        ["楷体"] = ["simkai.ttf"],
        ["simfang"] = ["simfang.ttf"],
        ["fangsong"] = ["simfang.ttf"],
        ["仿宋"] = ["simfang.ttf"],
        ["times"] = ["times.ttf"],
        ["timesnewroman"] = ["times.ttf"],
    };

    /// <summary>SHX 名（去扩展名）→ AutoCAD 自带的 TrueType 替身。</summary>
    private static readonly Dictionary<string, string[]> ShxStandIn = new(StringComparer.OrdinalIgnoreCase)
    {
        ["simplex"] = ["simplex_.ttf"],
        ["romans"] = ["romans__.ttf"],
        ["romant"] = ["romans__.ttf"],
        ["romand"] = ["romans__.ttf"],
        ["italic"] = ["romans__.ttf"],
        ["complex"] = ["romans__.ttf", "simplex_.ttf"],
        ["txt"] = ["monotxt_.ttf", "simplex_.ttf"],
        ["monotxt"] = ["monotxt_.ttf"],
        ["isocp"] = ["simplex_.ttf"],
        ["isocpeur"] = ["simplex_.ttf"],
    };

    /// <summary>名字看着就是中文字库的 SHX（大字体槽位遇到它们直接给中文字体）。</summary>
    private static readonly HashSet<string> BigFontHints = new(StringComparer.OrdinalIgnoreCase)
    {
        "hztxt", "hztxts", "hzdx", "hzfs", "hzk", "hztxt_e",
        "gbcbig", "gbteigl", "chineset", "chinese", "bigfont", "whgtxt", "whtgtxt", "whgdtxt",
        "tssdchn", "jdyan", "hzd", "sly",
    };

    /// <summary>通用中文字体候选（也是"SHX 大字体没有替身"的兜底）。宋体优先：图签、说明块绝大多数是宋体。</summary>
    private static readonly string[] CjkFallbacks =
    [
        "simsun.ttc", "simsun.ttf", "msyh.ttc", "simhei.ttf", "Deng.ttf", "NotoSansSC-VF.ttf",
    ];

    /// <summary>西文 SHX 没有替身时的近似字体：按"窄 → 宽"排序，宁可窄一点也不要用雅黑。</summary>
    private static readonly string[] LatinFallbacks =
    [
        "simplex_.ttf", "romans__.ttf", "monotxt_.ttf", "arial.ttf",
    ];

    /// <summary>清空缓存（测试 / 切换字体目录后用）。</summary>
    public static void ClearCache() => Cache.Clear();
}
