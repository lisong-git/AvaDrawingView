using SkiaSharp;

namespace CadViewer.Core;

/// <summary>
/// 整图显示数据：不可变快照。后台构建完成后通过引用替换交付给渲染线程（无锁）。
/// </summary>
public sealed class DisplayList
{
    public required IReadOnlyList<CadEntity> Entities { get; init; }
    public required SpatialIndex Index { get; init; }
    public required IReadOnlyList<string> LayerNames { get; init; }
    /// <summary>图纸范围（世界坐标），空图纸时为默认占位范围。</summary>
    public required SKRect Extents { get; init; }

    public static DisplayList Empty { get; } = new()
    {
        Entities = Array.Empty<CadEntity>(),
        Index = SpatialIndex.Build(Array.Empty<CadEntity>()),
        LayerNames = Array.Empty<string>(),
        Extents = SKRect.Create(0, 0, 100, 100),
    };
}
