using SkiaSharp;

namespace CadViewer.Core;

/// <summary>
/// 二级拾取结果：比一个 <see cref="CadEntity"/> 更细的画布点选目标
/// （多段线的某一段）。地址与 .lab 目标一致：(Handle, OwnHandle, SubPath)。
/// </summary>
public sealed class SubEntityPick
{
    public required ulong Handle { get; init; }

    public required ulong OwnHandle { get; init; }

    /// <summary>实体内部子路径地址，如 <c>seg:3</c>。</summary>
    public required string SubPath { get; init; }

    /// <summary>
    /// 高亮几何（世界坐标）。所有权随本对象移交 —— 控件交给
    /// <see cref="OverlayLayer"/> 托管，调用方不要再持有/释放。
    /// </summary>
    public required IReadOnlyList<OverlayShape> Highlight { get; init; }

    /// <summary>高亮几何的世界坐标包围盒。</summary>
    public SKRect Bounds { get; init; }
}
