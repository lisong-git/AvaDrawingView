namespace DrawingView.Core;

/// <summary>
/// MTEXT 断行（P2-4）。
///
/// <para><b>为什么必须做</b>：MTEXT 的排版宽度由组码 41（<c>RectangleWidth</c>）给出，
/// AutoCAD 会把文字**折到这一栏宽度以内**。本项目的实测样本里 107,032 条 MTEXT 全部带宽度，
/// 不折行的后果是一条说明文字从图签左侧一直横穿到图纸外面，看起来像"文字溢出"。</para>
/// <para><b>断行规则</b>（与 AutoCAD 的观感一致，不追求逐像素相同）：</para>
/// <list type="bullet">
/// <item>手动换行（<c>\P</c> 已转成 <c>\n</c>）优先，逐行再折。</item>
/// <item>中日韩字符、全角标点：**任意两字之间**都可断（西文不允许，否则会把单词劈开）。</item>
/// <item>西文：在空格处断，行尾空格丢弃。</item>
/// <item>单个"词"本身就超宽：硬断，否则会整行溢出。</item>
/// </list>
/// </summary>
public static class TextWrap
{
    /// <summary>
    /// 按 <paramref name="maxWidth"/>（世界单位）折行。maxWidth &lt;= 0 时原样返回。
    /// </summary>
    /// <param name="capHeight">字高（CAD 口径 = 大写字高），用于度量。</param>
    /// <param name="widthFactor">宽度因子（TEXT 用；MTEXT 恒为 1，保留参数以便复用）。</param>
    /// <param name="font">
    /// 度量用字体。必须传**这条文字实际会用的那个**：宋体比雅黑窄两成，
    /// 用默认字体断行会让整段文字提前折行（P2-16）。
    /// </param>
    public static string Wrap(string text, float capHeight, float maxWidth, float widthFactor = 1f,
                              TextFont? font = null)
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 0f || capHeight <= 0f) return text;

        float wf = widthFactor > 0f ? widthFactor : 1f;
        var lines = text.Split('\n');
        var result = new System.Text.StringBuilder(text.Length + 8);

        for (int li = 0; li < lines.Length; li++)
        {
            if (li > 0) result.Append('\n');
            WrapLine(lines[li], capHeight, maxWidth / wf, result, font ?? TextFont.Default);
        }
        return result.ToString();
    }

    private static void WrapLine(string line, float capHeight, float maxWidth, System.Text.StringBuilder sink,
                                 TextFont font)
    {
        if (line.Length == 0) return;

        int start = 0;          // 当前行起点（字符下标）
        int lastBreak = -1;     // 最近的可断位置（断点之后的位置）
        float acc = 0f;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            float adv = font.CharAdvance(c, capHeight);

            // 超过栏宽：在最近的可断点处断，没有可断点就硬断
            if (acc + adv > maxWidth && i > start)
            {
                int cut = lastBreak > start ? lastBreak : i;
                int end = cut;
                // 断点落在空格上时，空格归前行尾（渲染时不可见），从下一行起跳过它
                sink.Append(line, start, end - start);
                sink.Append('\n');
                start = cut;
                while (start < line.Length && line[start] == ' ') start++;
                // 重扫新行的起点：宽度从头累计，可断点清空
                i = start - 1;
                lastBreak = -1;
                acc = 0f;
                continue;
            }

            acc += adv;
            if (IsBreakable(c)) lastBreak = i + 1;
        }

        if (start < line.Length) sink.Append(line, start, line.Length - start);
    }

    /// <summary>
    /// 该字符之后是否允许断行。
    /// 空格一定可断；CJK（含全角标点、假名、谚文）任意字之间可断；其余西文字母数字不可断。
    /// </summary>
    private static bool IsBreakable(char c)
    {
        if (c == ' ' || c == '\t') return true;
        // CJK 统一表意文字、扩展 A、兼容表意、假名、谚文、CJK 标点与全角符号
        return (c >= 0x2E80 && c <= 0x9FFF)      // CJK 部首/标点/假名/统一表意
            || (c >= 0xAC00 && c <= 0xD7AF)      // 谚文音节
            || (c >= 0xF900 && c <= 0xFAFF)      // CJK 兼容表意
            || (c >= 0xFE30 && c <= 0xFE4F)      // 兼容形式（竖排标点等）
            || (c >= 0xFF00 && c <= 0xFFEF);     // 全角/半角形式
    }
}
