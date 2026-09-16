# Avalonia2dDWG — Avalonia 2D CAD 查看器

参考 ReadCAD 的 2D 技术思路（ACadSharp 解析 + Skia GPU 自绘）在 Avalonia 中实现的纯 2D 图纸查看器。**不做 3D**。

## 功能（P0）

- 打开 DWG / DXF（ACadSharp 3.4.9 解析，后台线程）
- 支持 LINE、CIRCLE、ARC、ELLIPSE、LWPOLYLINE（含凸度弧）、POLYLINE2D、SPLINE、TEXT、MTEXT、HATCH（边界）、SOLID、LEADER、DIMENSION（标注块）、POINT、INSERT（块递归展开，16 层）
- 滚轮缩放（光标锚点）、拖拽平移、双击/按钮适应窗口
- 图层面板（显隐开关）
- ACI 1-255 调色板 + TrueColor，白底白色自动转黑
- NTS STRtree 空间索引：视口裁剪 + 为后续拾取预留

## 技术要点

- **渲染**：`CadViewControl` → `Render` → `context.Custom(ICustomDrawOperation)` → `ISkiaSharpApiLeaseFeature.Lease()` 借用 Avalonia 自带 Skia GPU 上下文直接绘制，**无任何 GL/D3D 互操作层**（ReadCAD 在 WPF 里需要 Vortice+D3DImage，Avalonia 不需要）
- **线程模型**：解析/转换/索引在后台线程完成后产出不可变 `DisplayList`，引用替换交给渲染线程，渲染回调只读无锁
- **精度**：圆/弧用圆锥曲线（任意缩放不失真），椭圆/样条压平
- **颜色**：ACI 256 色表 + TrueColor；ByLayer 解析到图层色

## 构建运行

```powershell
dotnet build Avalonia2dDWG.slnx -c Debug
dotnet run --project src\CadViewer          # 启动 GUI
dotnet run --project tools\SmokeTest       # 全链路冒烟测试
```

需要 .NET 8 SDK。

## 结构

```
src/CadViewer.Core      # 无 UI 依赖：解析、几何转换、显示列表、空间索引、视口
src/CadViewer           # Avalonia 应用：CadViewControl、主窗口、图层面板
tools/SmokeTest         # 样例 DXF 全链路测试
tools/Probe             # ACadSharp API 反射探针（开发辅助）
samples/                # 样例图纸
```

## 已知限制 / 后续（P1+）

- 大图纸优化：SKPicture 静态层录制、分块缓存、块实例复用（当前 INSERT 为烘焙克隆）
- 线型（DASHED 等）、线宽映射、SHX 字体（当前走系统字体回退）
- 填充图案（当前仅描边边界）、布局空间（PaperSpace）
- 选择拾取、属性面板、测量、PNG 导出
- 未知实体/异常实体跳过计数（`GeometryConverter.Skipped`），代理实体未支持
