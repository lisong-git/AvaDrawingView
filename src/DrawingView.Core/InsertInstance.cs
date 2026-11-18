namespace DrawingView.Core;

/// <summary>
/// 一次块引用展开：一个 INSERT 实体（含 MINSERT 阵列）实例化一次即一个实例。
///
/// <para><b>为什么需要它</b>：<see cref="CadEntity.Handle"/> 是「最外层 INSERT 的句柄」，
/// <see cref="CadEntity.OwnHandle"/> 是「块定义里的原始句柄」——同一块定义被插入两次时，
/// 两个实例的后代拿到的是完全相同的 (Handle, OwnHandle)，显示层无法区分是哪一个实例。
/// 整张图都在一个顶层 INSERT 里时更极端：所有图元的 Handle 都等于那个 INSERT 的句柄。</para>
///
/// <para><b>为什么用 int 序号而不是路径哈希</b>：序号无碰撞，判祖先只需沿
/// <see cref="Parent"/> 上溯（深度 ≤ <see cref="DisplayList.MaxInstanceDepth"/>）；
/// 哈希撞了会让作用域漏进/漏出，且这种错误查不出来。持久化不用序号（换会话顺序会变），
/// 落库仍用 <see cref="DisplayList.GetInsertChain"/> 给出的句柄链。</para>
/// </summary>
public sealed class InsertInstance
{
    /// <summary>实例 Id，从 1 起连续编号；<see cref="DisplayList.Instances"/> 的下标 = Id - 1。</summary>
    public int Id { get; init; }

    /// <summary>父实例 Id；0 = 直接挂在模型空间（或图纸空间根）。</summary>
    public int Parent { get; init; }

    /// <summary>该 INSERT 在**父块定义**里的句柄 —— 插入路径的一段。</summary>
    public ulong InsertHandle { get; init; }

    /// <summary>块名（诊断用）。</summary>
    public string BlockName { get; init; } = string.Empty;
}
