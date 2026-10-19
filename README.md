# AvaDrawingView — Avalonia 2D CAD 查看器

参考 ReadCAD 的 2D 技术思路（ACadSharp 解析 + Skia GPU 自绘）在 Avalonia 中实现的纯 2D 图纸查看器。**不做 3D**。

## 功能（P0）

- 打开 DWG / DXF（ACadSharp 3.4.9 解析，后台线程）
- 支持 LINE、CIRCLE、ARC、ELLIPSE、LWPOLYLINE（含凸度弧）、POLYLINE2D、SPLINE、TEXT、MTEXT、HATCH（实心填充）、SOLID、LEADER、DIMENSION（标注块）、POINT、INSERT（块递归展开，16 层）、MINSERT（矩形阵列）
- 滚轮缩放（光标锚点）、拖拽平移、双击/按钮适应窗口
- 图层面板（显隐开关）；**冻结层与关闭层默认隐藏**，可在面板中手动打开查看
- ACI 1-255 调色板 + TrueColor，白底白色自动转黑，ByLayer 解析到图层色，**ByBlock 继承所属 INSERT 的颜色**
- NTS STRtree 空间索引：视口裁剪 + 为后续拾取预留
- 按 AutoCAD 语义跳过不可见实体（`Entity.IsInvisible`，DXF 组码 60）
- 显示数据按引用计数释放（`DisplayList.AddRef/Release`），反复开图不泄漏原生 `SKPath`

## 技术栈版本

| 组件 | 版本 | 备注 |
|---|---|---|
| .NET | 8.0（`net8.0`） | Avalonia 12 最低要求 .NET 8，官方推荐 net10 |
| Avalonia | **12.1.2** | 2026-09-16 由 11.3 升级 |
| SkiaSharp | **3.119.4** | 须与 `Avalonia.Skia 12.1.2` 的依赖一致，**不要升到 4.x** |
| ACadSharp | 3.4.9 | DWG/DXF 解析 |
| NetTopologySuite | 2.6.0 | STRtree 空间索引 |

> Avalonia 12 已移除 Windows 专用的 `OpenFileDialog`，文件选择改走跨平台的
> `TopLevel.StorageProvider` + `FilePickerFileType`——因此本查看器现在在 Windows / macOS / Linux
> 上都能正常打开文件。

## 技术要点

- **渲染**：`DrawingView` → `Render` → `context.Custom(ICustomDrawOperation)` → `ISkiaSharpApiLeaseFeature.Lease()` 借用 Avalonia 自带 Skia GPU 上下文直接绘制，**无任何 GL/D3D 互操作层**（ReadCAD 在 WPF 里需要 Vortice+D3DImage，Avalonia 不需要）
- **线程模型**：解析/转换/索引在后台线程完成后产出不可变 `DisplayList`，引用替换交给渲染线程，渲染回调只读无锁
- **资源生命周期**：`DisplayList` 持有原生 `SKPath`（GC 看不见），用 `AddRef`/`Release` 引用计数——绘制操作在构造时 `AddRef`、`Dispose` 时 `Release`，UI 线程换图时 `Release` 旧实例。计数归零才真正释放，避免"渲染线程还在画、UI 线程已释放"的用例
- **精度**：圆/弧用圆锥曲线（任意缩放不失真），椭圆/样条压平
- **文本包围盒**：走 `TextMetrics` 用真实字体度量（`MeasureLocalBounds` + `ToWorldBounds`），与渲染共用同一 `SKTypeface`，保证"适应窗口"的范围与屏幕墨迹一致
- **颜色**：ACI 256 色表 + TrueColor；ByLayer 解析到图层色，ByBlock 继承外层 INSERT 的解析色
- **大图性能**（借鉴 ReadCAD 的批次缓冲 + LOD 思路，实测 5.36MB 图纸全图视角
  **7.3 FPS → 63.6 FPS**（绘制 4.5ms）；按 AutoCAD 语义跳过隐藏实体与冻结/关闭图层后，
  全图可见实体由 13.6 万降至 8.6 万，烘焙为 214 个绘制批次）：
  - 加载时把几何按 (尺寸档, 图层, 颜色) 烘焙合并成少量 SKPath 批次（压缩数百倍）
  - 缩小时走批次路径，放大时走 R 树剔除 + 逐实体路径，两路同一套 LOD 与配色规则
  - 亚像素几何整档剔除（`MinEntityPixelSize`，默认 1.5px，墨迹覆盖率实测仅损失约 2%）
  - 文本按像素高度 LOD（`MinTextPixelSize`），字体按尺寸缓存复用

## 构建运行

```powershell
dotnet build AvaDrawingView.slnx -c Debug
dotnet run --project src\DrawingView          # 启动 GUI
dotnet run --project tools\SmokeTest        # 核心链路冒烟测试（解析→转换→索引，54 项断言）
dotnet run --project tools\UiSmokeTest      # UI 渲染冒烟测试（无头，不弹窗口）
dotnet run --project tools\PerfProbe -- "D:\path\to.dwg"   # 性能基准（--load-only 只测加载；--repeat 8 验内存泄漏）
dotnet run --project tools\SampleGen        # 重新生成 samples\sample_features.dxf
dotnet run --project tools\BlockExport -- "D:\path\to.dwg" "D:\out" --referenced   # 按块导出独立 DWG
dotnet run --project tools\ShapeExport -- "D:\path\to.dwg" "D:\out"               # 按相交分组导出独立 DWG
```

一键跑完构建 + 两项冒烟（结果汇总到 `.regress.log`）：

```powershell
powershell -ExecutionPolicy Bypass -File tools\regress.ps1
```

需要 .NET 8 SDK。

### BlockExport：块导出为独立 DWG

```powershell
dotnet run --project tools\BlockExport -- "D:\src.dwg" "D:\out" --referenced
```

- `--referenced`：只导出模型空间 INSERT **递归**引用到的块（推荐）。只跟 INSERT，不跟
  `Dimension.Block`——否则 5330 个标注会带出 5330 个 `*D` 块（实测踩过）。这些 `*D` 块仍会作为
  「被导出块的内部依赖」随块一起拷进新图，导出的标注不会丢几何
- `--list` 只看清单；`--include-anon` 含匿名块；`--format dxf` 出 DXF；`--only <关键字>` 过滤
- 块内嵌套 INSERT、图层 / 线型 / 文字样式 / 标注样式都会按名字在新图里重建，不会留下悬空句柄
- 每个文件写完自动回读校验：**实体数 + 实体构成 + 包围盒**三项与源块一致才标 OK
- 输出目录会落一份 `_块导出清单.csv`（文件名 ↔ 块名 ↔ 实体数 ↔ 模型空间引用次数）
- 候选块 > 300 个需加 `--force`（保险线）；日志用 `BLOCKEXPORT_LOG=<路径>` 写 UTF-8 文件

### ShapeExport：相交分组导出为独立 DWG

```powershell
dotnet run --project tools\ShapeExport -- "D:\src.dwg" "D:\out"   # 默认输出到 <源图目录>\blocks
```

- 把模型空间的几何图元按**相交关系**聚成组（连通分量：A–B 相交、B–C 相交 ⇒ A/B/C 同组），
  每组导出一个独立 DWG，文件名 `0001.dwg`、`0002.dwg`…（默认按组包围盒面积从大到小）
- 只判几何：LINE / LWPOLYLINE / POLYLINE / ARC / CIRCLE / ELLIPSE / SPLINE / HATCH / SOLID /
  FACE3D / INSERT（块引用会按插入点、缩放、旋转、MINSERT 阵列展开后参与判定）；
  文字、标注、引线默认不参与（否则每个轮廓都会碰到标注线），要带上就加 `--annotations`
- 相交 = 压平后的线段真正相交 / 端点相接 / 搭接，容差 `--tol`（默认 1e-6）
- `--list` 只统计分组；`--min-size N` 只导出 ≥N 个图元的组；`--min-extent` 按尺寸过滤；
  `--origin` 把每组平移到自身左下角为原点（默认保留源坐标）；`--format dxf` 出 DXF
- 输出目录落一份 `_图形分组清单.csv`（序号 ↔ 文件名 ↔ 图元数 ↔ 类型构成 ↔ 包围盒 ↔ 成员句柄）
- 组数 > 3000 会中止（保险线），确认无误加 `--force`；日志用 `SHAPEEXPORT_LOG=<路径>` 写 UTF-8 文件

> 改渲染相关代码后请同时跑 `tools\UiSmokeTest`。`SmokeTest` 不经过 Avalonia 渲染管线，
> 无法拦截"渲染期非法操作"这类缺陷（例如在 `Render` 内调用 `InvalidateVisual`）。
> 改渲染性能相关代码后请跑 `tools\PerfProbe` 并与 `AGENTS.md` 中的基线表对比。
> 两项冒烟测试都会把结果同时写一份 UTF-8 日志文件（`DrawingView_SMOKE_LOG` /
> `DrawingView_UISMOKE_LOG` 指定路径），Windows 下控制台重定向会把中文变成乱码，日志文件更可靠。

## 结构

```
src/DrawingView.Core      # 无 UI 依赖：解析、几何转换、烘焙批次、显示列表、文本度量、空间索引、视口
src/DrawingView           # Avalonia 应用：DrawingView、主窗口、图层面板
tools/SmokeTest         # 样例 DXF 全链路测试（解析→转换→索引，54 项精确断言）
tools/UiSmokeTest       # 无头 Avalonia 渲染测试（真实跑渲染管线）
tools/PerfProbe         # 性能基准（加载分段 / 内存 / 帧耗时 / LOD 扫描 / 墨迹覆盖率 / 泄漏验证）
tools/SampleGen         # 生成 samples\sample_features.dxf（覆盖全部实体类型与边界情况）
tools/BlockExport       # 把图纸里的块定义逐个导出成独立 DWG（WBLOCK 语义，含嵌套块依赖）
tools/ShapeExport       # 把模型空间的图元按相交关系分组，每组导出成独立 DWG（连通分量）
tools/Shared            # 上面两个导出工具共用的读写 / 跨文档拷贝逻辑（CadToolkit.cs，链进各自项目）
tools/regress.ps1       # 一键回归：Debug/Release 构建 + 核心冒烟 + UI 冒烟（构建失败即中止）
tools/Probe             # ACadSharp API 反射探针（开发辅助）
samples/                # 样例图纸
```

## 已知限制 / 后续（P1+）

- 大图纸优化：SKPicture 静态层录制、分块缓存、块实例复用（当前 INSERT 为烘焙克隆）
- 线型（DASHED 等）、线宽映射、SHX 字体（当前走系统字体回退）
- 填充图案（当前 HATCH 只做纯色填充，非实心图案降级为描边边界）、布局空间（PaperSpace）
- TEXT 实体的组码 72/73 对齐方式尚未生效（当前统一按插入点左对齐基线绘制）
- 实体 `Normal`（OCS→WCS）变换未做
- 大坐标（图纸范围 ±1e6）下 float32 精度：深度放大时几何可能抖动
  （ReadCAD 用 `EnableGpuFloatingOrigin` 解决）
- 选择拾取、属性面板、测量、PNG 导出
- 未知实体/异常实体跳过计数（`GeometryConverter.Skipped`），代理实体未支持
