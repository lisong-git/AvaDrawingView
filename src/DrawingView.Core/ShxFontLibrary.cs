using System.Collections.Concurrent;
using SkiaSharp;

namespace DrawingView.Core;

/// <summary>
/// SHX 字体查找与排版。
///
/// <para>查找顺序按 AutoCAD 的语义：图形所在目录 → 调用方注册的字体目录 →
/// <c>C:\Windows\Fonts</c>（国内常见做法是把 gbcbig/hztxt 拷进系统字体目录）→
/// 环境变量 <c>DrawingView_FONT_PATH</c>（分号分隔）。</para>
///
/// <para><b>一个样式可能有两个字库</b>：STYLE 的"字体"管西文、"大字体"管中文
/// （gbcbig / hztxt）。AutoCAD 按字符码自动分流，这里也一样 —— 西文字符先用主字体，
/// 主字体没有再用大字体，反之亦然。</para>
/// </summary>
public static class ShxFontLibrary
{
    private static readonly List<string> SearchDirs = new();
    private static readonly ConcurrentDictionary<string, ShxFont?> ByName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>环境变量名：额外字体目录（分号分隔）。</summary>
    public const string FontPathEnv = "DrawingView_FONT_PATH";

    /// <summary>
    /// 注册图形所在目录（每份文档调用一次，放最前）。
    /// 图纸自带字体的情形很常见 —— 实测图集里有 11 个 .shx 随图附带。
    /// </summary>
    public static void SetDrawingDirectory(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;
        lock (SearchDirs)
        {
            SearchDirs.RemoveAll(d => string.Equals(d, dir, StringComparison.OrdinalIgnoreCase));
            SearchDirs.Insert(0, dir);
            if (SearchDirs.Count > 8) SearchDirs.RemoveAt(SearchDirs.Count - 1);
        }
        ByName.Clear();   // 目录变了，之前"找不到"的缓存要失效
    }

    /// <summary>追加字体目录（例如 AutoCAD 的 Fonts 目录）。</summary>
    public static void AddSearchDirectory(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;
        lock (SearchDirs)
        {
            if (!SearchDirs.Contains(dir, StringComparer.OrdinalIgnoreCase))
                SearchDirs.Add(dir);
        }
        ByName.Clear();
    }

    /// <summary>当前搜索目录（诊断用）。</summary>
    public static IReadOnlyList<string> Directories
    {
        get
        {
            lock (SearchDirs)
            {
                var list = new List<string>(SearchDirs);
                string? env = Environment.GetEnvironmentVariable(FontPathEnv);
                if (!string.IsNullOrWhiteSpace(env))
                    list.AddRange(env.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                string win = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
                if (Directory.Exists(win)) list.Add(win);
                return list;
            }
        }
    }

    /// <summary>
    /// 样式里写的字体名是不是"该由 SHX 解析"的名字。
    ///
    /// <para>样式可以写 TTF（<c>ARIAL.TTF</c>、<c>simhei.ttf</c>），那种必须走系统字体那条路 ——
    /// 否则会去硬盘上找 <c>ARIAL.TTF.shx</c>，找不到再记一笔"缺字库"，
    /// 状态栏就会把"用 TTF 的图纸"误报成"缺 SHX 字库"（实测 A-00-000：884/884 条全是误报）。</para>
    ///
    /// <para>没有扩展名的（<c>txt</c>、<c>simplex</c>）按 AutoCAD 的老习惯补 <c>.shx</c>，算 SHX。</para>
    /// </summary>
    public static bool IsShxName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        string ext = Path.GetExtension(fileName.Trim().Trim('"'));
        return ext.Length == 0 || ext.Equals(".shx", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 按样式里的文件名找字体。文件名可能是 "simplex.shx"、"SIMPLEX" 或 "simplex"，
    /// 一律补 .shx 后逐个目录找。找不到返回 null。
    /// </summary>
    public static ShxFont? Resolve(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        string name = fileName.Trim().Trim('"');
        if (name.Length == 0) return null;
        if (!IsShxName(name)) return null;   // TTF 交给系统字体那条路

        // 已经是绝对路径就直接用
        if (Path.IsPathRooted(name))
            return ShxFont.TryLoad(name, out _);

        string key = name;
        if (ByName.TryGetValue(key, out var cached)) return cached;

        ShxFont? found = null;
        foreach (string dir in Directories)
        {
            foreach (string candidate in Candidates(name))
            {
                string p = Path.Combine(dir, candidate);
                if (!File.Exists(p)) continue;
                found = ShxFont.TryLoad(p, out _);
                if (found is not null) break;
            }
            if (found is not null) break;
        }
        ByName[key] = found;
        return found;
    }

    private static IEnumerable<string> Candidates(string name)
    {
        if (name.EndsWith(".shx", StringComparison.OrdinalIgnoreCase))
            yield return name;
        else
        {
            yield return name + ".shx";
            yield return name + ".SHX";
            yield return name;          // 极少数图纸里文件名就是完整的（无扩展名也存了字库）
        }
    }

    /// <summary>样式 → (西文字体, 大字体)。两个都可能为 null。</summary>
    public static (ShxFont? Ansi, ShxFont? Big) ResolveStyle(string? fontFile, string? bigFontFile)
        => (Resolve(fontFile), Resolve(bigFontFile));

    // ---------- 排版 ----------

    /// <summary>排版结果：局部坐标的描边路径 + 宽度。</summary>
    public readonly record struct Shaped(SKPath Path, float Width, int Missing);

    /// <summary>
    /// 把文本排成**局部坐标**的描边路径：x 向右、y 向上，首行基线在 y=0，字高 = <paramref name="capHeight"/>。
    /// 宽度因子/倾斜/旋转由调用方（转换器）统一施加，与 TTF 那条路保持同一套变换顺序。
    ///
    /// <returns>true = 至少排出一个字形，可以用 SHX 渲染；false = 该样式没有 SHX 字体或没有可用字形。</returns>
    /// </summary>
    public static bool TryShape(string text, string? fontFile, string? bigFontFile,
                                float capHeight, float lineSpacingFactor, TextLineAlign lineAlign,
                                out Shaped shaped)
    {
        shaped = default;
        if (string.IsNullOrEmpty(text) || capHeight <= 0) return false;

        var (ansi, big) = ResolveStyle(fontFile, bigFontFile);
        if (ansi is null && big is null) return false;

        float ansiScale = ansi is null ? 0f : capHeight / ansi.CapUnits;
        float bigScale = big is null ? 0f : capHeight / big.CapUnits;

        var lines = text.Split('\n');
        var path = new SKPath { FillType = SKPathFillType.Winding };
        float maxWidth = 0f;
        int missing = 0;
        int drawn = 0;

        for (int li = 0; li < lines.Length; li++)
        {
            string line = lines[li];
            float baseY = -li * capHeight * lineSpacingFactor;

            // 先量宽度，才能按行对齐（MTEXT 的 Center/Right 是逐行生效的）
            float w = MeasureLine(line, ansi, big, ansiScale, bigScale, capHeight);
            float dx = lineAlign switch
            {
                TextLineAlign.Center => -w * 0.5f,
                TextLineAlign.Right => -w,
                _ => 0f,
            };
            if (w > maxWidth) maxWidth = w;

            float penX = dx;
            foreach (char c in line)
            {
                if (c < ' ') continue;
                var (font, glyph, scale) = PickGlyph(c, ansi, big, ansiScale, bigScale);
                if (font is null || glyph is null)
                {
                    missing++;
                    penX += FallbackAdvance(c, ansi, big, ansiScale, bigScale);
                    continue;
                }
                if (glyph.Polylines.Count > 0)
                {
                    foreach (var poly in glyph.Polylines)
                    {
                        if (poly.Length < 2) continue;
                        path.MoveTo(penX + poly[0].X * scale, baseY + poly[0].Y * scale);
                        for (int k = 1; k < poly.Length; k++)
                            path.LineTo(penX + poly[k].X * scale, baseY + poly[k].Y * scale);
                    }
                    drawn++;
                }
                // 中文用大字体自己的**固定字宽**（gbcbig ≈ 0.73 em，压缩字体的字形结束点
                // 不代表前进量，按字高 1 em 走会宽出 37%）；西文用字形自个的 Advance。
                float adv = font.Kind == ShxFont.FontKind.BigFont
                    ? font.AdvanceUnits * scale
                    : glyph.Advance * scale;
                if (adv <= 0f) adv = FallbackAdvance(c, ansi, big, ansiScale, bigScale);
                penX += adv;
            }
        }

        if (drawn == 0 || path.IsEmpty)
        {
            path.Dispose();
            return false;
        }
        shaped = new Shaped(path, maxWidth, missing);
        return true;
    }

    /// <summary>只量宽度（与 <see cref="TryShape"/> 同一套前进量），供对齐/两点定宽使用。</summary>
    public static bool TryMeasure(string text, string? fontFile, string? bigFontFile, float capHeight,
                                  out float width)
    {
        width = 0f;
        if (string.IsNullOrEmpty(text) || capHeight <= 0) return false;
        var (ansi, big) = ResolveStyle(fontFile, bigFontFile);
        if (ansi is null && big is null) return false;

        float ansiScale = ansi is null ? 0f : capHeight / ansi.CapUnits;
        float bigScale = big is null ? 0f : capHeight / big.CapUnits;
        float max = 0f;
        foreach (string line in text.Split('\n'))
        {
            float w = MeasureLine(line, ansi, big, ansiScale, bigScale, capHeight);
            if (w > max) max = w;
        }
        width = max;
        return max > 0f;
    }

    private static (ShxFont? Font, ShxFont.Glyph? Glyph, float Scale) PickGlyph(
        char c, ShxFont? ansi, ShxFont? big, float ansiScale, float bigScale)
    {
        // 西文字符优先主字体，中文优先大字体；主字体没有的再用另一个兜底。
        // **有记录就算"有字"，不能要求"有墨迹"** —— 空格的字形是空的，但它有前进量
        // （simplex 空格 19 单位 = 0.905 em）；按 Polylines.Count > 0 判定会把空格当缺字，
        // 落到"半个字高"的兜底上，于是每个空格窄 45%（ODA 基准：整串 "WAVE 2026" 差 4.7%）。
        if (c < 0x80)
        {
            if (ansi?.GetGlyph(c) is { } ga) return (ansi, ga, ansiScale);
            if (big?.GetGlyph(c) is { } gb) return (big, gb, bigScale);
            return (null, null, 0f);
        }
        if (big?.GetGlyph(c) is { } mb) return (big, mb, bigScale);
        if (ansi?.GetGlyph(c) is { } ma) return (ansi, ma, ansiScale);
        return (null, null, 0f);
    }

    private static float MeasureLine(string line, ShxFont? ansi, ShxFont? big,
                                     float ansiScale, float bigScale, float capHeight)
    {
        float x = 0f;
        foreach (char c in line)
        {
            if (c < ' ') continue;
            var (font, glyph, scale) = PickGlyph(c, ansi, big, ansiScale, bigScale);
            if (font is null || glyph is null) { x += FallbackAdvance(c, ansi, big, ansiScale, bigScale); continue; }
            float adv = font.Kind == ShxFont.FontKind.BigFont ? font.AdvanceUnits * scale : glyph.Advance * scale;
            x += adv > 0f ? adv : FallbackAdvance(c, ansi, big, ansiScale, bigScale);
        }
        return x;
    }

    /// <summary>缺字时的占位前进量：中文按大字体的固定字宽，西文按半个字高。</summary>
    private static float FallbackAdvance(char c, ShxFont? ansi, ShxFont? big, float ansiScale, float bigScale)
    {
        if (c >= 0x80)
        {
            if (big is not null && bigScale > 0) return big.AdvanceUnits * bigScale;
            if (ansi is not null && ansiScale > 0) return ansi.CapUnits * ansiScale;
        }
        if (ansi is not null && ansiScale > 0) return ansi.CapUnits * 0.5f * ansiScale;
        if (big is not null && bigScale > 0) return big.CapUnits * bigScale;
        return 0f;
    }
}
