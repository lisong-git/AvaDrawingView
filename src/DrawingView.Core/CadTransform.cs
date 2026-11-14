using ACadSharp.Entities;
using SkiaSharp;

namespace DrawingView.Core;

/// <summary>
/// 实体变换约定：块定义坐标 → 世界坐标。
///
/// <para><b>这个类型存在的唯一理由是消除重复。</b>标注目标树需要把「块内某个子实体」或
/// 「多段线的第 3 段」单独变换到世界坐标去画高亮，而显示层（<see cref="GeometryConverter"/>）
/// 已经有一套同样的变换。两处各写一遍必然漂移 —— 漂移的表现是「高亮框与图形错位」，
/// 且只在嵌套块 / 带缩放的块上暴露，排查成本极高。因此 <see cref="GeometryConverter"/>
/// 改为调用本类型，约定在物理上只有一份实现。</para>
///
/// <para>约定细节（与转换器完全一致）：矩阵为行向量约定，<see cref="Mul"/> 的语义是
/// 「<c>a</c> 先作用、<c>b</c> 后作用」；点变换为
/// <c>(x*ScaleX + y*SkewX + TransX, x*SkewY + y*ScaleY + TransY)</c>。</para>
/// </summary>
public static class CadTransform
{
    /// <summary>块引用递归深度上限（与转换器共用，避免两处不一致）。</summary>
    public const int MaxInsertDepth = 16;

    /// <summary>MINSERT 阵列展开的实例总数上限（与转换器共用）。</summary>
    public const int MaxMInsertInstances = 10_000;

    // ---------- 矩阵基元 ----------

    /// <summary>矩阵组合（行向量约定：<paramref name="a"/> 先作用，<paramref name="b"/> 后作用）。</summary>
    public static SKMatrix Mul(in SKMatrix a, in SKMatrix b) => GeometryConverter.Mul(a, b);

    public static SKMatrix Scale(float sx, float sy) => GeometryConverter.ScaleM(sx, sy);

    public static SKMatrix Rotate(float radians) => GeometryConverter.RotM(radians);

    public static SKMatrix Translate(float tx, float ty) => GeometryConverter.TransM(tx, ty);

    /// <summary>把点按矩阵变换（与转换器同一实现）。</summary>
    public static SKPoint Apply(in SKMatrix m, SKPoint p) => GeometryConverter.TransformPoint(m, p);

    // ---------- INSERT ----------

    /// <summary>
    /// INSERT 的局部变换：块定义坐标 → 所属容器（父块定义或模型空间）坐标。
    ///
    /// <para>顺序 = 缩放 → 旋转 → 平移(插入点 − 块基点)，与 AutoCAD 的 INSERT 语义一致；
    /// 块基点先减掉，否则基点非零的块整体偏移（这是移植时踩过的坑）。</para>
    /// </summary>
    /// <returns><c>false</c> = 退化插入（XScale 或 YScale 为 0）。转换器会整体跳过它，
    /// 标注树也必须跳过，否则树的节点与显示图元对不上。</returns>
    public static bool TryInsertMatrix(Insert ins, out SKMatrix matrix)
    {
        matrix = SKMatrix.Identity;
        if (ins is null) return false;

        float sx = (float)ins.XScale, sy = (float)ins.YScale;
        if (System.Math.Abs(sx) < 1e-9f || System.Math.Abs(sy) < 1e-9f)
            return false;

        var bp = SKPoint.Empty;
        try
        {
            if (ins.Block?.BlockEntity != null)
                bp = new SKPoint((float)ins.Block.BlockEntity.BasePoint.X,
                                 (float)ins.Block.BlockEntity.BasePoint.Y);
        }
        catch { /* 取不到基点按 0 处理，与转换器一致 */ }

        var m = Scale(sx, sy);
        m = Mul(m, Rotate((float)ins.Rotation));

        // 挤出方向（组码 210）：法向为 (0,0,-1) 的 INSERT，其插入点与块内几何存的是 **OCS** 坐标，
        // 必须经 OCS→WCS 才能落到正确位置。任意轴算法在 N=(0,0,-1) 时退化为「X 取反、Y 不动」。
        // 实测（竖剖节点陶板.dwg：INSERT H=12421 块内嵌套 INSERT H=2379，法向 (0,0,-1)）：
        // 不做这步，那个螺栓被画到世界 x≈-535510（AutoCAD 在 -200067），整图范围左边界
        // 从 -200690 被撑到 -535553，自适应出图直接缩成一条缝。同类图纸 24 张里 4 张受影响。
        var n = ins.Normal;
        bool flipX = n.Z < -1e-9 && System.Math.Abs(n.X) < 1e-9 && System.Math.Abs(n.Y) < 1e-9;
        if (flipX) m = Mul(m, Scale(-1f, 1f));

        var tx = (float)ins.InsertPoint.X - bp.X;
        var ty = (float)ins.InsertPoint.Y - bp.Y;
        if (flipX) tx = -tx;      // 平移量同样在 OCS 里，X 一起取反
        m = Mul(m, Translate(tx, ty));
        matrix = m;
        return true;
    }

    /// <summary>MINSERT 的阵列规模（行/列/总数）。</summary>
    public static void ArraySize(Insert ins, out int rows, out int cols, out long total)
    {
        rows = System.Math.Max(1, (int)ins.RowCount);
        cols = System.Math.Max(1, (int)ins.ColumnCount);
        total = (long)rows * cols;
    }

    /// <summary>
    /// MINSERT 阵列某一实例相对基点的附加偏移。
    /// <para>该偏移在<b>容器坐标系</b>内生效（与插入点同层），所以转换器把它夹在
    /// 「块变换」与「父变换」之间（<c>Mul(mm, parent)</c>），这里给出同一语义。</para>
    /// </summary>
    public static SKMatrix ArrayOffset(Insert ins, int row, int col)
        => (row == 0 && col == 0)
            ? SKMatrix.Identity
            : Translate((float)(col * ins.ColumnSpacing), (float)(row * ins.RowSpacing));
}
