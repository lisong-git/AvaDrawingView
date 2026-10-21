using SkiaSharp;

namespace DrawingView.Core;

/// <summary>
/// B 样条（NURBS）压平：de Boor 递推求值。
///
/// <para><b>为什么要支持权重（P2-6）</b>：<c>weights</c> 不全为 1 时是**有理**样条，
/// 它才能表示精确的圆/椭圆锥线；忽略权重的后果不是"画不出来"，而是形状整体偏了 ——
/// 例如权重 0.7071 的四点圆弧样条会变成非圆的自由曲线，肉眼能看出鼓/瘪。</para>
///
/// <para>有理求值的标准做法是在**齐次坐标**里做 de Boor：控制点取 (w·x, w·y, w)，
/// 求值后除以 w 回到欧氏空间。权重全为 1 时与普通 B 样条完全等价，故走同一条代码路径。</para>
/// </summary>
public static class SplineFlattener
{
    /// <param name="ctrlX">控制点 X 序列</param>
    /// <param name="ctrlY">控制点 Y 序列</param>
    /// <param name="knots">节点向量（长度应为 ctrl+degree+1）</param>
    /// <param name="degree">次数</param>
    /// <param name="segmentsPerSpan">每节点区间的采样段数（越大越平滑）</param>
    /// <param name="weights">有理权重；null 或长度不足则按等权（普通 B 样条）处理</param>
    /// <param name="closed">是否闭合（末点接回首点）</param>
    public static SKPath? Flatten(double[] ctrlX, double[] ctrlY, double[] knots, int degree,
                                  int segmentsPerSpan = 24, double[]? weights = null, bool closed = false)
    {
        int n = ctrlX.Length;
        if (n < 2 || degree < 1 || knots.Length < n + degree + 1) return null;
        double t0 = knots[degree];
        double t1 = knots[n];
        if (t1 <= t0) return null;

        // 有理样条：控制点先升到齐次坐标 (w·x, w·y, w)，求值后再除以 w。
        // 等权时 w 恒为 1，结果与普通 B 样条逐位相同。
        bool rational = weights is not null && weights.Length >= n;
        if (rational)
        {
            bool allOne = true;
            for (int i = 0; i < n; i++)
                if (Math.Abs(weights[i] - 1.0) > 1e-12) { allOne = false; break; }
            rational = !allOne;
        }

        double[] hx = ctrlX, hy = ctrlY, hw = Array.Empty<double>();
        if (rational)
        {
            hx = new double[n]; hy = new double[n]; hw = new double[n];
            for (int i = 0; i < n; i++)
            {
                double w = weights![i];
                // 权重 <= 0 是坏数据：按等权处理那一个点，避免除零产生 NaN 整条丢失
                if (!(w > 0) || double.IsNaN(w) || double.IsInfinity(w)) w = 1.0;
                hw[i] = w;
                hx[i] = ctrlX[i] * w;
                hy[i] = ctrlY[i] * w;
            }
        }

        int total = Math.Clamp((n - degree) * segmentsPerSpan, 16, 4096);
        var path = new SKPath();
        bool first = true;
        for (int i = 0; i <= total; i++)
        {
            double t = t0 + (t1 - t0) * i / total;
            double x, y;
            if (rational)
            {
                var hp = DeBoor(t, hx, hy, knots, degree, hw);
                double w = hp.w;
                if (!(Math.Abs(w) > 1e-12)) return null;
                x = hp.x / w; y = hp.y / w;
            }
            else
            {
                var p = DeBoor(t, hx, hy, knots, degree);
                x = p.x; y = p.y;
            }
            if (double.IsNaN(x) || double.IsInfinity(x) || double.IsNaN(y) || double.IsInfinity(y))
                return null;
            if (first) { path.MoveTo((float)x, (float)y); first = false; }
            else path.LineTo((float)x, (float)y);
        }
        if (closed) path.Close();
        return path;
    }

    private static (double x, double y, double w) DeBoor(double t, double[] X, double[] Y,
                                                        double[] knots, int degree, double[]? W = null)
    {
        int n = X.Length;
        int k = FindSpan(t, knots, degree, n);
        var dx = new double[degree + 1];
        var dy = new double[degree + 1];
        var dw = new double[degree + 1];
        for (int j = 0; j <= degree; j++)
        {
            int idx = Math.Clamp(k - degree + j, 0, n - 1);
            dx[j] = X[idx];
            dy[j] = Y[idx];
            dw[j] = W is not null && idx < W.Length ? W[idx] : 1.0;
        }
        for (int r = 1; r <= degree; r++)
        {
            for (int j = degree; j >= r; j--)
            {
                int i = k - degree + j;
                if (i < 0 || i + degree - r + 1 >= knots.Length) continue;
                double denom = knots[i + degree - r + 1] - knots[i];
                double alpha = denom == 0 ? 0 : (t - knots[i]) / denom;
                dx[j] = (1 - alpha) * dx[j - 1] + alpha * dx[j];
                dy[j] = (1 - alpha) * dy[j - 1] + alpha * dy[j];
                dw[j] = (1 - alpha) * dw[j - 1] + alpha * dw[j];
            }
        }
        return (dx[degree], dy[degree], dw[degree]);
    }

    private static int FindSpan(double t, double[] knots, int degree, int n)
    {
        for (int i = degree; i < n; i++)
            if (t < knots[i + 1]) return i;
        return n - 1;
    }
}
