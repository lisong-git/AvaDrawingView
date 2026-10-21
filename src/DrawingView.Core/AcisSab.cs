using System.Text;

namespace DrawingView.Core;

/// <summary>
/// ACIS <b>SAB</b>（Standard ACIS Binary）记录流的读取器 —— 用于把 REGION / 3DSOLID / BODY
/// 里那段二进制载荷还原成实体记录表，进而取出顶点与边。
///
/// <para><b>为什么需要它</b>（P2-7）：ACadSharp 只把 ACIS 载荷原样交出
/// （<c>ModelerGeometry.AcisData</c>），R2013+ 的 DWG 里这段载荷是二进制 SAB，
/// <c>GetAcisText()</c> 返回 null —— 于是这批 REGION 在查看器里完全画不出来。
/// 实测 <c>3、幕墙横方总汇.dwg</c> 的 162 条 REGION **全是 SAB**，没有一条能取到 SAT 文本。</para>
///
/// <para><b>格式</b>：SAB 是 SAT 的二进制版，记录结构相同（<c>记录名 + 属性…</c>），
/// 只是每个属性前加一个类型标记字节。标记表（与 ACIS 文档一致）：</para>
/// <code>
/// 0x02 char(1)   0x03 short(2)   0x04 int(4)    0x05 float(4)   0x06 double(8)
/// 0x07 str(len1) 0x08 str(len2)  0x09 str(len4) 0x0A false     0x0B true
/// 0x0C 指针(4)   0x0D 记录名     0x0E 子类型前缀 0x11 记录结束
/// 0x13 坐标(3×f64) 0x14 方向(3×f64) 0x15 枚举(4) 0x16 uv(2×f64) 0x17 int64(8)
/// </code>
///
/// <para>文件头（三行 SAT 头）不是记录形式，故从第一条记录 <c>asmheader</c> 开始解析；
/// 头部里那三个字符串（产品/版本/日期）与三个容差不需要。</para>
/// </summary>
public static class AcisSab
{
    /// <summary>SAB 载荷的魔数（15 字节）。</summary>
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("ACIS BinaryFile");

    public static bool LooksLikeSab(byte[]? data)
        => data is not null && data.Length > Magic.Length
           && data.AsSpan(0, Magic.Length).SequenceEqual(Magic);

    /// <summary>
    /// 解析整段 SAB 载荷。返回 null = 不是 SAB 或中途遇到未知标记（宁可整条放弃，
    /// 也不要交出半截错位的点表 —— 画错线比不画更误导）。
    /// </summary>
    public static List<AcisRecord>? Parse(byte[]? data)
    {
        if (data is null || data.Length < 32) return null;

        int i = FindFirstRecord(data);
        if (i < 0) return null;

        var records = new List<AcisRecord>(64);
        AcisRecord? cur = null;
        bool pendingBase = false;

        while (i < data.Length)
        {
            byte tag = data[i];

            // 记录头是「子类型 + 基类名」两段：`0x0E <具体类型>` 紧接 `0x0D <基类名>`
            // （如 0E "straight" + 0D "curve"、0E "plane" + 0D "surface"），随后才是属性。
            // **两段属于同一个实体**，只能产生一条记录 —— 拆成两条会让后面所有实体的
            // 索引整体偏移，指针（@N）全部错位（实测表现为 edge 的 start_vertex 指向另一条 edge）。
            if (tag is 0x0D or 0x0E)
            {
                if (i + 2 > data.Length) return null;
                int nameLen = data[i + 1];
                if (i + 2 + nameLen > data.Length) return null;
                var name = Encoding.ASCII.GetString(data, i + 2, nameLen);

                // 两种顺序都要认：「子类型 → 基类」与「基类 → 子类型」。判据是当前记录
                // 还没读到任何属性 —— 只要属性已开始，就说明它已是独立实体。
                bool mergeable = cur is not null && cur.Values.Count == 0;

                if (tag == 0x0E)
                {
                    if (mergeable) cur!.Subtype = name;      // 基类名先出现
                    else
                    {
                        cur = new AcisRecord { Name = name, Subtype = name };
                        records.Add(cur);
                    }
                }
                else if (mergeable && cur!.Subtype is not null)
                {
                    cur.BaseName = name;                     // 子类型名先出现
                }
                else
                {
                    cur = new AcisRecord { Name = name };
                    records.Add(cur);
                }
                i += 2 + nameLen;
                continue;
            }

            if (tag == 0x11) { i++; cur = null; pendingBase = false; continue; }   // 记录结束

            // 属性必须挂在某条记录上；没有记录就接受（头部残留的杂项）
            if (!TryReadValue(data, ref i, out var v)) return null;
            cur?.Values.Add(v);
            pendingBase = false;
        }

        return records.Count > 0 ? records : null;
    }

    /// <summary>从头部之后找第一条记录（<c>asmheader</c>）的 0x0D 标记位置。</summary>
    private static int FindFirstRecord(byte[] d)
    {
        var name = Encoding.ASCII.GetBytes("asmheader");
        for (int i = Magic.Length; i + 2 + name.Length <= d.Length; i++)
        {
            if (d[i] != 0x0D || d[i + 1] != name.Length) continue;
            if (d.AsSpan(i + 2, name.Length).SequenceEqual(name)) return i;
        }
        return -1;
    }

    private static bool TryReadValue(byte[] d, ref int i, out AcisValue v)
    {
        v = default;
        if (i >= d.Length) return false;
        byte tag = d[i];
        switch (tag)
        {
            case 0x02:                                        // char
                if (i + 2 > d.Length) return false;
                v = new AcisValue(AcisKind.Int, i: (sbyte)d[i + 1]);
                i += 2; return true;

            case 0x03:                                        // short
                if (i + 3 > d.Length) return false;
                v = new AcisValue(AcisKind.Int, i: BitConverter.ToInt16(d, i + 1));
                i += 3; return true;

            case 0x04:                                        // int
                if (i + 5 > d.Length) return false;
                v = new AcisValue(AcisKind.Int, i: BitConverter.ToInt32(d, i + 1));
                i += 5; return true;

            case 0x05:                                        // float
                if (i + 5 > d.Length) return false;
                v = new AcisValue(AcisKind.Double, d: BitConverter.ToSingle(d, i + 1));
                i += 5; return true;

            case 0x06:                                        // double
                if (i + 9 > d.Length) return false;
                v = new AcisValue(AcisKind.Double, d: BitConverter.ToDouble(d, i + 1));
                i += 9; return true;

            case 0x07:
            case 0x08:
            case 0x09:                                        // string（长度 1/2/4 字节）
            {
                int lenBytes = tag - 0x06;                    // 1 / 2 / 4
                if (i + 1 + lenBytes > d.Length) return false;
                int len = lenBytes == 1 ? d[i + 1]
                        : lenBytes == 2 ? BitConverter.ToUInt16(d, i + 1)
                        : BitConverter.ToInt32(d, i + 1);
                if (len < 0 || i + 1 + lenBytes + len > d.Length) return false;
                v = new AcisValue(AcisKind.Str, s: Encoding.UTF8.GetString(d, i + 1 + lenBytes, len));
                i += 1 + lenBytes + len; return true;
            }

            case 0x0A: v = new AcisValue(AcisKind.Bool, i: 0); i++; return true;   // false / reversed
            case 0x0B: v = new AcisValue(AcisKind.Bool, i: 1); i++; return true;   // true / forward

            case 0x0C:                                        // 实体指针
                if (i + 5 > d.Length) return false;
                v = new AcisValue(AcisKind.Pointer, i: BitConverter.ToInt32(d, i + 1));
                i += 5; return true;

            case 0x13:                                        // 坐标 x,y,z
            case 0x14:                                        // 方向 x,y,z
                if (i + 25 > d.Length) return false;
                v = new AcisValue(AcisKind.Vector,
                    x: BitConverter.ToDouble(d, i + 1),
                    y: BitConverter.ToDouble(d, i + 9),
                    z: BitConverter.ToDouble(d, i + 17));
                i += 25; return true;

            case 0x15:                                        // 枚举
                if (i + 5 > d.Length) return false;
                v = new AcisValue(AcisKind.Enum, i: BitConverter.ToInt32(d, i + 1));
                i += 5; return true;

            case 0x16:                                        // uv
                if (i + 17 > d.Length) return false;
                v = new AcisValue(AcisKind.Vector,
                    x: BitConverter.ToDouble(d, i + 1),
                    y: BitConverter.ToDouble(d, i + 9), z: 0);
                i += 17; return true;

            case 0x17:                                        // int64
                if (i + 9 > d.Length) return false;
                v = new AcisValue(AcisKind.Int, i: BitConverter.ToInt64(d, i + 1));
                i += 9; return true;

            default:
                return false;
        }
    }
}

public enum AcisKind { Int, Double, Str, Bool, Pointer, Vector, Enum }

/// <summary>一条 ACIS 记录：记录名 + 属性序列。</summary>
public sealed class AcisRecord
{
    public required string Name { get; init; }

    /// <summary>0x0E 标出的具体子类型（如 straight / ellipse / plane）；无则 null。</summary>
    public string? Subtype { get; set; }

    /// <summary>紧随子类型的基类名（curve / surface 等），仅用于诊断。</summary>
    public string? BaseName { get; set; }

    /// <summary>用于显示的类型名（有子类型时用子类型）。</summary>
    public string TypeName => Subtype ?? Name;

    public List<AcisValue> Values { get; } = new();
}

/// <summary>ACIS 属性值（按 <see cref="Kind"/> 取对应字段）。</summary>
public readonly struct AcisValue
{
    public AcisValue(AcisKind kind, long i = 0, double d = 0, string? s = null,
                     double x = 0, double y = 0, double z = 0)
    {
        Kind = kind;
        I = i;
        D = d;
        S = s;
        X = x; Y = y; Z = z;
    }

    public AcisKind Kind { get; }
    public long I { get; }
    public double D { get; }
    public string? S { get; }
    public double X { get; }
    public double Y { get; }
    public double Z { get; }

    public bool IsNullPointer => Kind == AcisKind.Pointer && I < 0;

    public override string ToString() => Kind switch
    {
        AcisKind.Int => I.ToString(),
        AcisKind.Double => D.ToString("G6"),
        AcisKind.Str => $"\"{S}\"",
        AcisKind.Bool => I != 0 ? "T" : "F",
        AcisKind.Pointer => $"@{I}",
        AcisKind.Vector => $"({X:G6},{Y:G6},{Z:G6})",
        AcisKind.Enum => $"E{I}",
        _ => "?",
    };
}
