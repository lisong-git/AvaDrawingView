namespace DrawingView.Core;

/// <summary>MTEXT 内联格式码的解析结果（整条实体只取一套参数，见 <see cref="MTextFormat"/>）。</summary>
/// <param name="WidthExplicit">
/// 字宽因子是否由 <c>\W</c> 显式给出。MTEXT 没有实体级宽度字段，"没写 \W" 不等于 1.0 ——
/// 此时必须回落到 <b>STYLE 的组码 41</b>，见 <see cref="MTextFormat"/> 类注释里的受控基准。
/// </param>
public readonly record struct MTextFormatResult(
    string Text,
    float WidthFactor,
    float Tracking,
    float HeightFactor,
    bool WidthExplicit = false)
{
    public static readonly MTextFormatResult Plain = new(string.Empty, 1f, 1f, 1f);
}

/// <summary>
/// 解析 MTEXT 的内联格式码（<c>\W0.8;T0.75;审核\PReviewed</c> 一类）。
///
/// <para>**为什么必须自己做这件事**：ACadSharp 的 <c>MText.PlainText</c> 只把 <c>{</c>、<c>}</c>、
/// <c>\</c> 去掉、把 <c>\P</c> 换成换行，格式码的**参数体原样留着** ——
/// 图签里的「审核 / Reviewed」会变成 <c>W0.8T0.75;审核</c> 被整串描出来，
/// 而 ODA/AutoCAD 只画「审核」。实测 52陶板节点/9---陶板节点 (05).dwg 的图签里
/// 上百条 MTEXT 全是这种写法，整块图签变成糊字。</para>
///
/// <para>本类把参数体吃掉，并把其中**影响排版**的三项带出来：
/// <c>\W</c> 字宽因子、<c>\T</c> 字间距因子、<c>\H</c> 字高（<c>nx</c> 相对 / 绝对）。
/// 其余（<c>\f</c> 字体、<c>\C</c> 颜色、<c>\Q</c> 倾斜、<c>\A</c> 对齐、<c>\p</c> 段落、
/// 下划线/上划线开关）只消费不落地。</para>
///
/// <para>**作用域口径（2026-11-13 三张基准图实测，与 ODA 对齐）**：整条只取
/// 「第一个可见字符所在作用域」的那一套参数。原因是 ODA/Teigha 对一条 MTEXT 只应用
/// **首个** <c>\H</c>（`mt3.dxf`：`{\H0.76x;中文AB}{\H1.053x;中文AB}` 两段画成等高；
/// `mt2.dxf`：图签"备注"块中文段 0.76x 与英文段 1.053x 也画成等高），
/// 而 <c>\W</c> 是逐 run 生效的（`mt3.dxf` 第三组 `{\W0.8;…}{\W1.2;…}` 两段宽度不同）。
/// 逐 run 排版（连 <c>\H</c> 一起）在 `9---陶板节点 (05)` 上反而更差：英文段被放大 39%，
/// 整块多出 3 行、宽度溢出图签格（onlyA 22,737 → 27,105），已回退。要接这条通道，
/// 必须先拿到 AutoCAD 侧的真值再定档。</para>
///
/// <para>**已知近似**：<c>\T</c> 只压字距、不压字形；我们**不再**把它折进字宽
/// （折进去会让字形窄 20%，见 <see cref="GeometryConverter"/> 的 AddMText 注释）。</para>
/// </summary>
public static class MTextFormat
{
    private const float MinFactor = 0.05f;
    private const float MaxFactor = 20f;

    /// <summary>解析原始 <c>MText.Value</c>；纯文本原样返回（不含格式码时零开销）。</summary>
    public static MTextFormatResult Parse(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return MTextFormatResult.Plain;
        if (raw.IndexOf('\\') < 0 && raw.IndexOf('{') < 0 && raw.IndexOf('}') < 0)
            return new MTextFormatResult(raw, 1f, 1f, 1f);

        var sb = new System.Text.StringBuilder(raw.Length);

        // 组状态（{} 可嵌套，进入时压栈、退出时弹栈 —— AutoCAD 的格式码作用域语义）
        float width = 1f, tracking = 1f, heightFactor = 1f;
        bool widthSet = false;
        var stack = new Stack<(float W, float T, float H, bool WSet)>();

        // 整条只保留「第一个可见字符所在作用域」的参数：我们一条实体只能套一个字宽
        bool captured = false;
        float outW = 1f, outT = 1f, outH = 1f;
        bool outWSet = false;

        int i = 0;
        while (i < raw.Length)
        {
            char ch = raw[i];

            if (ch == '{') { stack.Push((width, tracking, heightFactor, widthSet)); i++; continue; }
            if (ch == '}')
            {
                if (stack.Count > 0) (width, tracking, heightFactor, widthSet) = stack.Pop();
                i++;
                continue;
            }

            if (ch != '\\')
            {
                if (!captured && !char.IsWhiteSpace(ch)) { captured = true; outW = width; outT = tracking; outH = heightFactor; outWSet = widthSet; }
                sb.Append(ch);
                i++;
                continue;
            }

            // ---- 反斜杠开头的格式码 ----
            if (i + 1 >= raw.Length) { i++; break; }
            char code = raw[i + 1];
            int p = i + 2;

            switch (char.ToUpperInvariant(code))
            {
                case 'P':                       // 段落换行
                case 'N':
                    sb.Append('\n');
                    if (!captured) MarkCapture();
                    i = p;
                    continue;

                case '~':                       // 不换行空格
                    sb.Append(' ');
                    i = p;
                    continue;

                case '\\':
                case '{':
                case '}':
                    sb.Append(code);
                    i = p;
                    continue;

                case 'L':                       // 下划线 / 上划线开关：消费掉
                case 'O':
                case 'K':
                    i = p;
                    continue;
            }

            // 带参数、以 ';' 结束的码
            int semi = raw.IndexOf(';', p);
            if (semi < 0)
            {
                // 没有结束符：按"孤立反斜杠"处理，丢掉反斜杠把字母留下，
                // 免得把后面的正文整段吃掉
                sb.Append(code);
                i = p;
                continue;
            }

            string arg = raw.Substring(p, semi - p);
            switch (char.ToUpperInvariant(code))
            {
                case 'W':                       // 字宽因子（绝对值，覆盖样式宽度而非叠乘）
                    if (float.TryParse(arg, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var w))
                    {
                        width = Clamp(w, MinFactor, MaxFactor);
                        widthSet = true;
                    }
                    break;

                case 'T':                       // 字间距因子（相对，1 = 正常）
                    if (float.TryParse(arg, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var t))
                        tracking = Clamp(t, MinFactor, MaxFactor);
                    break;

                case 'H':                       // 字高：2.5x=相对，2.5=绝对
                {
                    bool relative = arg.EndsWith("x", StringComparison.OrdinalIgnoreCase);
                    var num = relative ? arg[..^1] : arg;
                    if (float.TryParse(num, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var hv) && hv > 0)
                        // 绝对字高折成倍数，调用方再乘 mt.Height
                        heightFactor = relative ? Clamp(hv, MinFactor, MaxFactor) : heightFactor * hv;
                    break;
                }

                case 'S':                       // 堆叠：1^2 / 1#2 / a/b —— 取斜杠写法，别丢内容
                    sb.Append(arg.Replace('^', '/').Replace('#', '/'));
                    break;

                // \f 字体、\C/\c 颜色、\Q 倾斜、\A 对齐、\p 段落、\W 之外的排版码：只消费
            }

            i = semi + 1;
        }

        if (!captured) { outW = width; outT = tracking; outH = heightFactor; outWSet = widthSet; }
        return new MTextFormatResult(sb.ToString(), outW, outT, outH, outWSet);

        void MarkCapture()
        {
            if (captured) return;
            captured = true; outW = width; outT = tracking; outH = heightFactor; outWSet = widthSet;
        }
    }

    private static float Clamp(float v, float lo, float hi)
        => float.IsNaN(v) ? 1f : Math.Clamp(v, lo, hi);
}
