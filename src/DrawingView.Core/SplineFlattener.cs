using SkiaSharp;

namespace DrawingView.Core;

/// <summary>B 样条 (NURBS) 压平：de Boor 递推求值。</summary>
public static class SplineFlattener
{
    /// <param name="ctrlX">控制点 X 序列</param>
    /// <param name="ctrlY">控制点 Y 序列</param>
    /// <param name="knots">节点向量（长度应为 ctrl+degree+1）</param>
    /// <param name="degree">次数</param>
    public static SKPath? Flatten(double[] ctrlX, double[] ctrlY, double[] knots, int degree, int segmentsPerSpan = 24)
    {
        int n = ctrlX.Length;
        if (n < 2 || degree < 1 || knots.Length < n + degree + 1) return null;
        double t0 = knots[degree];
        double t1 = knots[n];
        if (t1 <= t0) return null;

        int total = Math.Clamp((n - degree) * segmentsPerSpan, 16, 4096);
        var path = new SKPath();
        bool first = true;
        for (int i = 0; i <= total; i++)
        {
            double t = t0 + (t1 - t0) * i / total;
            var p = DeBoor(t, ctrlX, ctrlY, knots, degree);
            if (double.IsNaN(p.x) || double.IsInfinity(p.x) || double.IsNaN(p.y) || double.IsInfinity(p.y))
                return null;
            if (first) { path.MoveTo((float)p.x, (float)p.y); first = false; }
            else path.LineTo((float)p.x, (float)p.y);
        }
        return path;
    }

    private static (double x, double y) DeBoor(double t, double[] X, double[] Y, double[] knots, int degree)
    {
        int n = X.Length;
        int k = FindSpan(t, knots, degree, n);
        var dx = new double[degree + 1];
        var dy = new double[degree + 1];
        for (int j = 0; j <= degree; j++)
        {
            int idx = Math.Clamp(k - degree + j, 0, n - 1);
            dx[j] = X[idx];
            dy[j] = Y[idx];
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
            }
        }
        return (dx[degree], dy[degree]);
    }

    private static int FindSpan(double t, double[] knots, int degree, int n)
    {
        for (int i = degree; i < n; i++)
            if (t < knots[i + 1]) return i;
        return n - 1;
    }
}
