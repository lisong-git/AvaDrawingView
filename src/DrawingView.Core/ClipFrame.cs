using SkiaSharp;

namespace DrawingView.Core;

/// <summary>
/// 块引用的**裁剪边界**（XCLIP / ACAD_FILTER→SPATIAL 的边界线）。
///
/// <para>AutoCAD 按 <c>XCLIPFRAME</c> 决定是否把它画出来：<c>0</c> 不显示，<c>1</c> 显示且打印，
/// <c>2</c> 显示但不打印（2023 默认）。它**不是几何**：不在模型空间实体表里、不参与拾取、
/// 不参与打印，所以不能混进 <see cref="DisplayList.Entities"/>，否则会被点选、被标注、
/// 被"适合窗口"当成内容。这里单独成表，由画布在内容之上画一圈细线。</para>
///
/// <para>位置口径与裁剪口径**同源**：<see cref="Path"/> 就是转换器真正用来裁内容的那个边界
/// （<c>TryGetSpatialClip</c> 的产物），因此框画在哪里、内容就被切在哪里，不会出现
/// "框在这、内容却按别处裁"的错位（实测 0陶板节点_块层级验收测试.dwg 的框范围与
/// AutoCAD 的 EXTMIN/EXTMAX 完全一致：(26188.9,5197.4)-(27548.6,6345.1)）。</para>
/// </summary>
public sealed class ClipFrame
{
    /// <summary>带裁剪的那个块引用的句柄（同一个块被插多次时每处一条）。</summary>
    public required ulong Handle { get; init; }

    /// <summary>边界线（世界坐标，闭合）。所有权随 <see cref="DisplayList.ClipPaths"/> 一起释放。</summary>
    public required SKPath Path { get; init; }

    /// <summary>边界包围盒（世界坐标）。</summary>
    public SKRect Bounds { get; init; }
}
