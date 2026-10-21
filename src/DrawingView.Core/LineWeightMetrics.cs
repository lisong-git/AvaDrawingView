namespace DrawingView.Core;

/// <summary>
/// 线宽（mm 1/100）→ 屏幕像素的换算。
///
/// <para>AutoCAD 的线宽显示口径**按空间不同**，这是最容易做错的一处：</para>
/// <list type="bullet">
/// <item><b>模型空间</b>：线宽是**固定像素宽**，与缩放无关（0 → 1px，其余按 mm 成正比）。
/// Autodesk 帮助原话："在模型空间中显示的线宽不随缩放比例而变化。"所以要"看起来更粗"只需缩小视角。</item>
/// <item><b>图纸空间（布局）</b>：线宽按**真实单位**显示，随缩放变化
/// （1:1 出图的布局里 0.5mm 的线在纸上正好 0.5mm）。</item>
/// </list>
///
/// <para>推论：转换期**不能**把线宽折算成"世界单位宽度"存进几何——
/// 同一个毫米值在两种空间要两种换算，而"哪种空间 / 当前缩放是多少"只有渲染期才知道。
/// 因此几何里只保留毫米原值（<see cref="CadEntity.LineWeightMm100"/>），这里统一换算。</para>
/// </summary>
public static class LineWeightMetrics
{
    /// <summary>
    /// 模型空间的 mm→像素基准：AutoCAD 按 96dpi 折算（0.13mm≈0.5px、0.25mm≈1px、0.50mm≈2px、2.11mm≈8px）。
    /// 该值决定"线宽开关打开后画面有多粗"，与 DPI 无关（AutoCAD 也是固定这套比例）。
    /// </summary>
    public const float ModelSpacePixelsPerMm = 96f / 25.4f;

    /// <summary>
    /// 线宽的屏幕像素宽度（含 1px 下限：细线不因缩小而消失）。
    /// </summary>
    /// <param name="mm100">线宽，1/100 mm；&lt;=0 表示不显示线宽（按 1px）。</param>
    /// <param name="paperSpace">true = 图纸空间布局，false = 模型空间。</param>
    /// <param name="zoom">当前缩放（图纸/世界单位 → 像素）。</param>
    /// <param name="worldUnitsPerMm">图纸空间里 1mm 等于多少图纸单位（1:1 mm 出图时为 1）。</param>
    public static float ToPixels(int mm100, bool paperSpace, float zoom, float worldUnitsPerMm)
    {
        float z = zoom > 1e-6f ? zoom : 1e-6f;
        float wuPerMm = worldUnitsPerMm > 0f ? worldUnitsPerMm : 1f;
        if (mm100 <= 0) return 1f;

        float mm = mm100 * 0.01f;
        float px = paperSpace
            ? mm * wuPerMm * z          // 图纸空间：毫米折成图纸单位，再乘缩放（随缩放变粗）
            : mm * ModelSpacePixelsPerMm; // 模型空间：固定像素，与缩放无关
        return px < 1f ? 1f : px;
    }

    /// <summary>
    /// 换算成"世界单位描边宽度"（画布已 concat 世界→屏幕矩阵，Skia 的 StrokeWidth 走世界单位）。
    /// </summary>
    public static float ToWorldStroke(int mm100, bool paperSpace, float zoom, float worldUnitsPerMm)
        => ToPixels(mm100, paperSpace, zoom, worldUnitsPerMm) / (zoom > 1e-6f ? zoom : 1e-6f);
}
