using SkiaSharp;

namespace DrawingView.Core;

/// <summary>
/// 变宽多段线的**宽度轮廓**生成器（B4）。
///
/// <para><b>为什么不能当线宽画</b>：多段线的"宽度"是<b>几何</b>（AutoCAD 里叫全局宽度，
/// 是一条有面积的带子，有端点、能被捕捉、能参与布尔运算），而 LineWeight 是<b>显示属性</b>。
/// 两者在 AutoCAD 里是互斥的：宽多段线忽略线宽（帮助原话："具有'全局宽度'的多段线将忽略应用的线宽"）。
/// 因此这里生成闭合轮廓交给 <c>AddFill</c>，而不是加粗描边——否则缩小看会变成一根粗线，
/// 放大看又永远只有像素宽，接头的形状也全错。</para>
///
/// <para><b>接头</b>：相邻两段在顶点处按 AutoCAD 的**斜接（miter）**求交——
/// 两侧偏移线各求交点，得到一个尖角。夹角过尖（内积 &lt; 0.2，接近 180° 折返）时改用
/// 两个偏移点（斜切 / bevel），避免生成几千单位长的尖刺。宽度在顶点处发生跳变时
/// （前段 EndWidth ≠ 后段 StartWidth）取两者平均，视觉上等同于一个极短的锥形过渡。</para>
///
/// <para><b>端点</b>：AutoCAD 宽多段线的端头是**垂直端面**（不平头、不圆头），
/// 即两侧偏移点直接连一条垂直于轴线的小边，本实现取同样的口径。</para>
/// </summary>
public static class WidePolyline
{
    /// <summary>一段：起点/终点、两端宽度（世界单位，0 = 尖）、凸度（0 = 直线）。</summary>
    public readonly record struct Piece(SKPoint A, SKPoint B, float StartWidth, float EndWidth, double Bulge);

    /// <summary>每段圆弧的采样密度上限（角度步长 5°，与 <see cref="CadGeometry.AppendBulgeArc"/> 一致）。</summary>
    private const double MaxArcStepRad = 5 * Math.PI / 180;

    /// <summary>斜接长度的上限倍数（超过就用斜切，防尖刺）。</summary>
    private const float MiterLimit = 8f;

    /// <summary>
    /// 生成宽度轮廓。返回 null = 所有段宽度都是 0（调用方按普通折线描边）。
    /// </summary>
    public static SKPath? Build(IReadOnlyList<Piece> pieces, bool closed)
    {
        if (pieces.Count == 0) return null;

        bool anyWidth = false;
        foreach (var pc in pieces)
            if (pc.StartWidth > 0f || pc.EndWidth > 0f) { anyWidth = true; break; }
        if (!anyWidth) return null;

        // ① 压平成"带半宽的顶点序列"。圆弧按弧长插值宽度，直线一段一个间隔。
        var pts = new List<SKPoint>(32);
        var halfs = new List<float>(32);

        void Push(SKPoint p, float halfWidth)
        {
            int n = pts.Count;
            if (n > 0)
            {
                var q = pts[n - 1];
                if (MathF.Abs(q.X - p.X) < 1e-6f && MathF.Abs(q.Y - p.Y) < 1e-6f)
                {
                    // 同一位置的重复顶点（宽度跳变处）：取平均，避免零长度段把方向算成 NaN
                    halfs[n - 1] = 0.5f * (halfs[n - 1] + halfWidth);
                    return;
                }
            }
            pts.Add(p);
            halfs.Add(halfWidth);
        }

        for (int i = 0; i < pieces.Count; i++)
        {
            var pc = pieces[i];
            float hw0 = pc.StartWidth * 0.5f, hw1 = pc.EndWidth * 0.5f;

            SKPoint a = pc.A, b = pc.B;
            if (Math.Abs(pc.Bulge) <= 1e-9 ||
                !CadGeometry.TryBulgeArc(a, b, pc.Bulge, out var center, out var radius, out var startRad, out var sweep))
            {
                Push(a, hw0);
                Push(b, hw1);   // 下一段的起点与它重合时会被上面那段代码合并取平均
                continue;
            }

            int steps = Math.Max(2, (int)Math.Ceiling(Math.Abs(sweep) / MaxArcStepRad));
            for (int s = 0; s <= steps; s++)
            {
                float t = s / (float)steps;
                double ang = startRad + sweep * t;
                var p = new SKPoint((float)(center.X + radius * Math.Cos(ang)),
                                    (float)(center.Y + radius * Math.Sin(ang)));
                // 首尾用精确端点，避免累积误差让接头错位
                if (s == 0) p = a;
                else if (s == steps) p = b;
                Push(p, hw0 + (hw1 - hw0) * t);
            }
        }

        if (pts.Count < 2) return null;

        // 闭合折线：末点与首点重合（同一条边被算两次）时去掉重复的那个
        if (closed && pts.Count > 2)
        {
            var first = pts[0];
            var last = pts[pts.Count - 1];
            if (MathF.Abs(first.X - last.X) < 1e-6f && MathF.Abs(first.Y - last.Y) < 1e-6f)
            {
                pts.RemoveAt(pts.Count - 1);
                halfs.RemoveAt(halfs.Count - 1);
            }
        }

        var left = new List<SKPoint>(pts.Count + 4);
        var right = new List<SKPoint>(pts.Count + 4);
        BuildSides(pts, halfs, closed, left, right);
        if (left.Count < 2 || right.Count < 2) return null;

        var path = new SKPath();
        path.MoveTo(left[0]);
        for (int i = 1; i < left.Count; i++) path.LineTo(left[i]);
        for (int i = right.Count - 1; i >= 0; i--) path.LineTo(right[i]);
        path.Close();
        return path;
    }

    /// <summary>
    /// 沿两侧各走一遍，得到左/右两条偏移折线（顶点处斜接）。
    /// 用"全局顶点序列"而不是逐段生成矩形，是为了让接头真正连成一条带子而不是留下一道缝。
    /// </summary>
    private static void BuildSides(List<SKPoint> pts, List<float> halfs, bool closed,
                                   List<SKPoint> left, List<SKPoint> right)
    {
        int n = pts.Count;
        for (int i = 0; i < n; i++)
        {
            var p = pts[i];
            float hw = halfs[i];

            bool hasIn = i > 0 || closed;
            bool hasOut = i < n - 1 || closed;
            int iIn = (i - 1 + n) % n, iOut = (i + 1) % n;

            if (!hasIn || !hasOut)
            {
                // 开放折线的端点：垂直端面。
                // 方向必须沿**行进方向**取：起点用"从本点出发"，终点用"进入本点"，
                // 取反了左右两侧会互换 → 轮廓自交成蝴蝶结（等宽带渲染成中间收腰的错觉）。
                var d = hasOut ? Dir(p, pts[iOut]) : Dir(pts[iIn], p);
                if (d is null) continue;
                var nv = Normal(d.Value);
                left.Add(new SKPoint(p.X + nv.X * hw, p.Y + nv.Y * hw));
                right.Add(new SKPoint(p.X - nv.X * hw, p.Y - nv.Y * hw));
                continue;
            }

            var dIn = Dir(pts[iIn], p);
            var dOut = Dir(p, pts[iOut]);
            if (dIn is null && dOut is null) continue;
            if (dIn is null) dIn = dOut;
            if (dOut is null) dOut = dIn;

            var nIn = Normal(dIn!.Value);
            var nOut = Normal(dOut!.Value);

            // 同侧偏移法线取平均方向 → 斜接
            var mLeft = Bisector(nIn, nOut);
            var mRight = Bisector(Neg(nIn), Neg(nOut));

            if (mLeft is not null && MathF.Abs(hw) > 0f)
                AddMitered(left, p, nIn, nOut, mLeft.Value, hw);
            else if (hw > 0f)
            {
                left.Add(new SKPoint(p.X + nIn.X * hw, p.Y + nIn.Y * hw));
                left.Add(new SKPoint(p.X + nOut.X * hw, p.Y + nOut.Y * hw));
            }
            else left.Add(p);

            if (mRight is not null && MathF.Abs(hw) > 0f)
                AddMitered(right, p, Neg(nIn), Neg(nOut), mRight.Value, hw);
            else if (hw > 0f)
            {
                right.Add(new SKPoint(p.X - nIn.X * hw, p.Y - nIn.Y * hw));
                right.Add(new SKPoint(p.X - nOut.X * hw, p.Y - nOut.Y * hw));
            }
            else right.Add(p);
        }
    }

    private static void AddMitered(List<SKPoint> side, SKPoint p, SKPoint nA, SKPoint nB,
                                   SKPoint bisector, float halfWidth)
    {
        // 斜接点沿角平分线走 halfWidth / cos(θ/2)，cos(θ/2) = bisector·nA
        float denom = bisector.X * nA.X + bisector.Y * nA.Y;
        if (denom > 1f / MiterLimit)
        {
            float k = halfWidth / denom;
            side.Add(new SKPoint(p.X + bisector.X * k, p.Y + bisector.Y * k));
            return;
        }
        // 夹角太尖：退化成斜切，两个偏移点各画一次，不生成尖刺
        side.Add(new SKPoint(p.X + nA.X * halfWidth, p.Y + nA.Y * halfWidth));
        side.Add(new SKPoint(p.X + nB.X * halfWidth, p.Y + nB.Y * halfWidth));
    }

    /// <summary>单位方向向量（两点重合返回 null）。</summary>
    private static SKPoint? Dir(SKPoint from, SKPoint to)
    {
        float dx = to.X - from.X, dy = to.Y - from.Y;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 1e-9f) return null;
        return new SKPoint(dx / len, dy / len);
    }

    /// <summary>反向向量（SKPoint 没有一元负号运算符）。</summary>
    private static SKPoint Neg(SKPoint v) => new(-v.X, -v.Y);

    /// <summary>左法线（逆时针转 90°）。</summary>
    private static SKPoint Normal(SKPoint dir) => new(-dir.Y, dir.X);

    /// <summary>两个单位法线的角平分线（近似反向时返回 null，交给斜切兜底）。</summary>
    private static SKPoint? Bisector(SKPoint a, SKPoint b)
    {
        float sx = a.X + b.X, sy = a.Y + b.Y;
        float len = MathF.Sqrt(sx * sx + sy * sy);
        if (len < 1e-4f) return null;
        return new SKPoint(sx / len, sy / len);
    }
}
