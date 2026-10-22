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

    /// <summary>
    /// 这个样式能不能真排竖排。
    ///
    /// <para>AutoCAD 的 SHX 竖排要求字体自己带竖排字形变体：西文字体要看头部 modes 的
    /// bit2（实测 <c>simplex</c>=2 支持、<c>gbenor</c>=0 不支持），大字体（gbcbig/hztxt）
    /// 都支持。两个字体都不支持时按横排画 —— ODA 对 <c>gbenor</c> 的"垂直"样式也是这么降级的：
    /// 同一份 DXF 里 simplex 竖排、gbenor 依旧横排。</para>
    /// </summary>
    public static bool SupportsVertical(string? fontFile, string? bigFontFile)
    {
        var (ansi, big) = ResolveStyle(fontFile, bigFontFile);
        return (ansi?.SupportsVertical ?? false) || (big?.SupportsVertical ?? false);
    }

    // ---------- 排版 ----------

    /// <summary>排版结果：局部坐标的描边路径 + 长度（横排 = 宽、竖排 = 向下排的总长）。</summary>
    public readonly record struct Shaped(SKPath Path, float Width, int Missing);

    /// <summary>
    /// 把文本排成**局部坐标**的描边路径：x 向右、y 向上，首行基线在 y=0，字高 = <paramref name="capHeight"/>。
    /// 宽度因子/倾斜/旋转由调用方（转换器）统一施加，与 TTF 那条路保持同一套变换顺序。
    ///
    /// <para><paramref name="vertical"/> = true 时按竖排排：字形**直立不动**、水平方向按墨迹居中，
    /// 整列向下走（ODA 基准：字高 100 的 "A" 占 x=±38.1、y=-100…0，"A" 的墨迹就正好在
    /// 插入点下方一个字高的范围里），所以 <see cref="Shaped.Width"/> 这时是"向下排的总长"。</para>
    ///
    /// <returns>true = 至少排出一个字形，可以用 SHX 渲染；false = 该样式没有 SHX 字体或没有可用字形。</returns>
    /// </summary>
    public static bool TryShape(string text, string? fontFile, string? bigFontFile,
                                float capHeight, float lineSpacingFactor, TextLineAlign lineAlign,
                                out Shaped shaped, bool vertical = false)
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
            if (vertical)
            {
                // 竖排：每一行是一列，按中文阅读习惯**从右往左**换列（与横排往下换行镜像）。
                // ODA 对 MTEXT 竖排的口径没测（图集里一例都没有），这里按"换列 = 左移一个行距"处理。
                float colX = -li * capHeight * lineSpacingFactor;
                float penY = 0f;
                foreach (char c in line)
                {
                    if (c < ' ') continue;
                    var (font, glyph, scale) = PickGlyph(c, ansi, big, ansiScale, bigScale, vertical: true);
                    if (font is null || glyph is null)
                    {
                        missing++;
                        penY -= VerticalCellOf(null, c, ansi, big, ansiScale, bigScale);
                        continue;
                    }
                    if (glyph.Polylines.Count > 0)
                    {
                        // 两种字库要分开处理：
                        //  ① 字体自己会定位（simplex：竖排变体已经是 x[-8,8] y[-21,0]）→ 原样画；
                        //  ② 字体不管（gbcbig 这类大字体没有 0x0E 命令，竖排=横排）→ 我们自己
                        //     按墨迹水平居中、整体上移一个字高，让墨迹挂在插入点下方。
                        bool self = font.SelfPositionsVertical(c);
                        float dx = 0f;
                        if (!self)
                        {
                            // ODA 基准：'A' 墨迹 0…16 单位 → 列两侧各 38.1px，中点正好在插入点
                            // x 上（按前进格居中会偏 14px）。
                            float inkMin = float.MaxValue, inkMax = float.MinValue;
                            foreach (var poly in glyph.Polylines)
                            foreach (var p in poly)
                            {
                                if (p.X < inkMin) inkMin = p.X;
                                if (p.X > inkMax) inkMax = p.X;
                            }
                            dx = inkMax > inkMin ? -0.5f * (inkMin + inkMax) * scale : 0f;
                        }
                        float dy = self ? penY : penY - capHeight;
                        foreach (var poly in glyph.Polylines)
                        {
                            if (poly.Length < 2) continue;
                            path.MoveTo(colX + dx + poly[0].X * scale, dy + poly[0].Y * scale);
                            for (int k = 1; k < poly.Length; k++)
                                path.LineTo(colX + dx + poly[k].X * scale, dy + poly[k].Y * scale);
                        }
                        drawn++;
                    }
                    penY -= VerticalCellOf(font, c, ansi, big, ansiScale, bigScale);
                }
                float run = -penY;
                if (run > maxWidth) maxWidth = run;
                continue;
            }

            float baseY = -li * capHeight * lineSpacingFactor;

            // 先量宽度，才能按行对齐（MTEXT 的 Center/Right 是逐行生效的）
            float w = MeasureLine(line, ansi, big, ansiScale, bigScale, capHeight);
            float dxLine = lineAlign switch
            {
                TextLineAlign.Center => -w * 0.5f,
                TextLineAlign.Right => -w,
                _ => 0f,
            };
            if (w > maxWidth) maxWidth = w;

            float penX = dxLine;
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

    /// <summary>竖排字身格（局部坐标，向下）。字形取自哪个字体就用哪个字体的格子。</summary>
    private static float VerticalCellOf(ShxFont? font, char c, ShxFont? ansi, ShxFont? big,
                                        float ansiScale, float bigScale)
    {
        if (font is not null)
        {
            float s = font.Kind == ShxFont.FontKind.BigFont ? bigScale : ansiScale;
            // 逐字形：竖排前进量是字库按字给的（'A' 31、'1' 31.5、空格 30、连字符 10 单位），
            // 不是字体常量 —— 用常量会让数字串/含空格的长串逐字累积偏差。
            float cell = s > 0f ? font.VerticalAdvance(c) * s : 0f;
            if (cell > 0f) return cell;
        }
        // 缺字兜底：拿一个能用的字体的格子，实在没有就按 1 em
        if (big is not null && bigScale > 0f) return big.VerticalCellUnits * bigScale;
        if (ansi is not null && ansiScale > 0f) return ansi.VerticalCellUnits * ansiScale;
        return 1f;
    }

    /// <summary>
    /// 只量长度（与 <see cref="TryShape"/> 同一套前进量），供对齐/两点定宽使用。
    /// 横排返回行宽；<paramref name="vertical"/> = true 时返回"向下排的总长"。
    /// </summary>
    public static bool TryMeasure(string text, string? fontFile, string? bigFontFile, float capHeight,
                                  out float width, bool vertical = false)
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
            float w;
            if (vertical)
            {
                w = 0f;
                foreach (char c in line)
                {
                    if (c < ' ') continue;
                    var (font, glyph, _s) = PickGlyph(c, ansi, big, ansiScale, bigScale, vertical: true);
                    _ = glyph;
                    w += VerticalCellOf(font, c, ansi, big, ansiScale, bigScale);
                }
            }
            else w = MeasureLine(line, ansi, big, ansiScale, bigScale, capHeight);
            if (w > max) max = w;
        }
        width = max;
        return max > 0f;
    }

    private static (ShxFont? Font, ShxFont.Glyph? Glyph, float Scale) PickGlyph(
        char c, ShxFont? ansi, ShxFont? big, float ansiScale, float bigScale, bool vertical = false)
    {
        // 西文字符优先主字体，中文优先大字体；主字体没有的再用另一个兜底。
        // **有记录就算"有字"，不能要求"有墨迹"** —— 空格的字形是空的，但它有前进量
        // （simplex 空格 19 单位 = 0.905 em）；按 Polylines.Count > 0 判定会把空格当缺字，
        // 落到"半个字高"的兜底上，于是每个空格窄 45%（ODA 基准：整串 "WAVE 2026" 差 4.7%）。
        if (c < 0x80)
        {
            if (ansi?.GetGlyph(c, vertical) is { } ga) return (ansi, ga, ansiScale);
            if (big?.GetGlyph(c, vertical) is { } gb) return (big, gb, bigScale);
            return (null, null, 0f);
        }
        if (big?.GetGlyph(c, vertical) is { } mb) return (big, mb, bigScale);
        if (ansi?.GetGlyph(c, vertical) is { } ma) return (ansi, ma, ansiScale);
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
