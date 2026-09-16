# Avalonia2dDWG — AI Agent 上下文

跨平台 2D CAD 查看器（DWG/DXF），参考 ReadCAD 的自绘渲染思路，**明确不做 3D**。

## 技术栈

- .NET 8 (`net8.0`) + Avalonia 11.3 + SkiaSharp 3.x
- ACadSharp 3.4.9（DWG/DXF 解析）
- NetTopologySuite（STRtree 空间索引，视口裁剪）
- 渲染核心：`Control.Render` → `ICustomDrawOperation` → `ISkiaSharpApiLeaseFeature.Lease()` 直接借 Avalonia 的 Skia GPU 上下文，**零互操作层**
- 显示数据为不可变快照，原子替换，渲染线程无锁

## 项目结构

```
src/CadViewer.Core/   核心库：解析、几何转换、空间索引、视口
src/CadViewer/        Avalonia 应用：CadViewControl 渲染控件 + 主窗口
tools/SmokeTest/      冒烟测试（解析→转换→索引全链路）
tools/Probe/          ACadSharp API 反射探针（摸 API 用，可删）
samples/              样例 DXF
```

## 构建与运行

```bash
dotnet build Avalonia2dDWG.slnx        # 注意是 .slnx，不是 .sln
dotnet run --project src/CadViewer     # 启动 GUI
dotnet run --project tools/SmokeTest   # 冒烟测试（不要加 --nologo，会被当应用参数）
```

## 已知踩坑（重要）

### ACadSharp 3.4.9 API（与旧版/文档差异大）
- 点类型：`CSMath.XYZ`（不在 ACadSharp 根命名空间）
- `Text` 实体类改名为 `TextEntity`；`MText` **继承自** `TextEntity`（switch-case 中 MText 必须放在 TextEntity 前面）
- `Arc` **继承自** `Circle`（Circle 的 case 必须放在 Arc 后面）
- `Color` 是**结构体**（非引用类型），`IsByLayer`/`IsByBlock`/`Index` 直接访问
- `CadDocument` 不是 IDisposable；`DwgReader.Read()` 不接收 CancellationToken
- `Insert` 缩放属性是 `XScale/YScale/ZScale`
- `BlockRecord` 在 `Tables` 命名空间
- 不确定 API 时用 `tools/Probe`（反射枚举）验证，不要猜

### Avalonia 11.3
- `ImmediateDrawingContext.TryGetFeature` 是**非泛型**的（`TryGetFeature(typeof(ISkiaSharpApiLeaseFeature))`）
- `OnPointerMoved` 重写签名用基类 `PointerEventArgs`（不是 PointerMovedEventArgs）
- 没有 `OnDoubleTapped` 虚方法，需在构造函数订阅 `DoubleTapped` 事件
- `dotnet new sln`（SDK 10）默认生成 `.slnx` 新格式

### 环境注意
- 部分会话中 Bash/PowerShell **输出捕获异常**（stdout 为空）：把命令输出重定向到日志文件再用 Read 读取
- 对**同一文件的多次 Edit 必须串行执行**，并行会互相覆盖、只有最后一个生效
- 构建命令输出量大时，统一 `> .workbuddy日志文件` 再读取

## 后续路线

P1：SKPicture 静态缓存、线型/填充图案、SHX 字体、文字/块完善；
P2：拾取选择、属性面板、测量、PNG 导出；P3：10 万实体 ≥45fps。
完整方案见 WorkBuddy 工作区《Avalonia-2D-CAD查看器实施方案.md》。
