namespace DrawingView.Core;

/// <summary>
/// 展开 AutoCAD 文字里的 <c>%%</c> 控制码。
///
/// <para>图纸里这些码是随手写的，不展开就会原样画出来 —— t3.dwg 的配电箱参数块写的是
/// <c>Cos%%C=0.60</c>，ODA 画成 <c>Cosφ=0.60</c>，我们此前把 <c>%%C</c> 当三个字符描出来，
/// 整行比参照宽 2 个字位，"=0.60" 被顶到框外。</para>
///
/// <para>规则按 AutoCAD 语义：<c>%%%</c> → <c>%</c>；<c>%%d</c> 度、<c>%%p</c> 正负、
/// <c>%%c</c> 直径；<c>%%u</c>/<c>%%o</c> 是下划线/上划线开关，直接丢掉（我们不做这两条装饰线）；
/// <c>%%nnn</c> 取十进制码位。认不出的 <c>%%x</c> 原样保留，免得把正常文字吃掉。</para>
/// </summary>
public static class TextCodes
{
    public static string Expand(string s)
    {
        if (s.IndexOf("%%", StringComparison.Ordinal) < 0) return s;

        var sb = new System.Text.StringBuilder(s.Length);
        int i = 0;
        while (i < s.Length)
        {
            if (s[i] != '%' || i + 1 >= s.Length || s[i + 1] != '%')
            {
                sb.Append(s[i++]);
                continue;
            }

            int j = i + 2;
            if (j >= s.Length) { sb.Append("%%"); i = j; continue; }

            char c = s[j];
            switch (char.ToLowerInvariant(c))
            {
                case '%': sb.Append('%'); i = j + 1; break;
                case 'd': sb.Append('\u00B0'); i = j + 1; break;
                case 'p': sb.Append('\u00B1'); i = j + 1; break;
                case 'c': sb.Append('\u00D8'); i = j + 1; break;
                case 'u':
                case 'o': i = j + 1; break;
                default:
                    if (char.IsAsciiDigit(c))
                    {
                        int k = j, val = 0;
                        while (k < s.Length && k < j + 3 && char.IsAsciiDigit(s[k]))
                            val = val * 10 + (s[k++] - '0');
                        sb.Append((char)val);
                        i = k;
                    }
                    else
                    {
                        sb.Append("%%");
                        i = j;
                    }
                    break;
            }
        }
        return sb.ToString();
    }
}
