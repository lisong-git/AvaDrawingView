using System;
using System.Collections.Generic;

namespace DrawingView.Core;

/// <summary>
/// 一次「点块 = 选块」的抬升结果。
///
/// <para>显示层把块引用展平成独立条目（<see cref="CadEntity.Handle"/> 指向最外层 INSERT、
/// <see cref="CadEntity.OwnHandle"/> 指向自己，INSERT 自身没有几何条目），所以
/// 「选中了哪个块实例」既不能从选中集合反推，也不一定能用单个句柄表达 ——
/// 块内嵌套块就是这种情况：它的 INSERT 句柄不出现在任何显示条目上。</para>
///
/// <para><see cref="Members"/> 为空表示<b>不抬升</b>（就选命中的那一项）：
/// 宿主在「在位编辑块」模式下靠这个把作用域块的直接子元素留在元素粒度。</para>
///
/// <para><see cref="InstanceId"/> 是抬升到的那个块实例（0 = 未知）。
/// 同一定义被插多次时句柄完全一样，只有它能区分「点中的是哪个副本」。</para>
/// </summary>
public readonly record struct BlockLift(ulong Outer, ulong Inner, IReadOnlyList<CadEntity> Members, int InstanceId = 0)
{
    /// <summary>不抬升。</summary>
    public static BlockLift None => new(0, 0, Array.Empty<CadEntity>());
}
