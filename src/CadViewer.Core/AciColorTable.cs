namespace CadViewer.Core;

/// <summary>
/// AutoCAD Color Index (ACI 1-255) → RGB 转换。
/// 0=BYBLOCK, 7=白（白底渲染时由渲染器转黑）。
/// </summary>
public static class AciColorTable
{
    public static uint FromAci(int index) => 0xFF000000u | ToRgb(index);

    public static uint ToRgb(int index)
    {
        switch (index)
        {
            case <= 0: return 0xFFFFFF;
            case 1: return 0xFF0000;
            case 2: return 0xFFFF00;
            case 3: return 0x00FF00;
            case 4: return 0x00FFFF;
            case 5: return 0x0000FF;
            case 6: return 0xFF00FF;
            case 7: return 0xFFFFFF;
            case 8: return 0x414141;
            case 9: return 0x808080;
            case >= 250:
            {
                byte v = index switch { 250 => (byte)51, 251 => (byte)80, 252 => (byte)105, 253 => (byte)130, 254 => (byte)190, _ => (byte)255 };
                return (uint)(v << 16 | v << 8 | v);
            }
            default:
            {
                int group = (index - 10) / 10;   // 0-23 个色相组，每组 15°
                int r = (index - 10) % 10;       // 组内明度/饱和度档位
                double hue = (group % 24) * 15.0;
                double val = r < 5 ? 1.0 - 0.2 * r : 1.0 - 0.2 * (r - 5);
                double sat = r < 5 ? 1.0 : 0.5;
                return HsvToRgb(hue, sat, val);
            }
        }
    }

    private static uint HsvToRgb(double h, double s, double v)
    {
        double c = v * s;
        double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        double m = v - c;
        (double r, double g, double b) = (h % 360) switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        byte ri = (byte)Math.Clamp((r + m) * 255, 0, 255);
        byte gi = (byte)Math.Clamp((g + m) * 255, 0, 255);
        byte bi = (byte)Math.Clamp((b + m) * 255, 0, 255);
        return (uint)(ri << 16 | gi << 8 | bi);
    }
}
