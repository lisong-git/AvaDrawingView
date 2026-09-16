using SkiaSharp;

namespace CadViewer.Core;

/// <summary>文字绘制信息（屏幕空间绘制，位置/高度均为世界坐标）。</summary>
public sealed class TextDraw
{
    public required string Text { get; init; }
    public SKPoint Position { get; init; }
    public float Height { get; init; }
    public float RotationRadians { get; init; }
}

/// <summary>
/// 显示实体：CAD 图元转换后的显示单元。
/// 路径在世界坐标系；构建完成后不可变，渲染线程只读。
/// </summary>
public sealed class CadEntity
{
    public required string Layer { get; init; }
    public uint ColorArgb { get; init; }
    /// <summary>描边几何（世界坐标），与 FillPath 互斥使用。</summary>
    public SKPath? Path { get; init; }
    /// <summary>填充几何（世界坐标），如 SOLID。</summary>
    public SKPath? FillPath { get; init; }
    public TextDraw? Text { get; init; }
    /// <summary>世界单位线宽，0 = 细线（随缩放保持 1px）。</summary>
    public float LineWidth { get; init; }
    /// <summary>世界坐标包围盒（用于空间索引与视口裁剪）。</summary>
    public SKRect Bounds { get; init; }
}
