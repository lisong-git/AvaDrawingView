# AvaDrawingView — AI Agent 上下文

跨平台 2D CAD 查看器（DWG/DXF），参考 ReadCAD 的自绘渲染思路，**明确不做 3D**。

## 技术栈

- .NET 8 (`net8.0`) + **Avalonia 12.1.2** + SkiaSharp 3.119.4（2026-09-16 由 Avalonia 11.3 升级）
- ACadSharp 3.4.9（DWG/DXF 解析）
- NetTopologySuite（STRtree 空间索引，视口裁剪）
- 渲染核心：`Control.Render` → `ICustomDrawOperation` → `ISkiaSharpApiLeaseFeature.Lease()` 直接借 Avalonia 的 Skia GPU 上下文，**零互操作层**
- 显示数据为不可变快照，原子替换，渲染线程无锁；原生几何（`SKPath`）由 `DisplayList` 引用计数释放
  （`AddRef`/`Release`，见「显示保真与资源释放」第 4 节），**GC 管不到原生内存**
- 画布背景：`DrawingView.BackgroundColor`（StyledProperty，默认 **黑**）；清屏与「与背景同色实体反转」
  （黑底画黑线 → 反转成白）都以它为准，两条渲染路径必须用同一套规则

## 项目结构

```
src/DrawingView.Core/   核心库：解析、几何转换、空间索引、视口、文本度量、烘焙批次
src/DrawingView/        Avalonia 应用：DrawingView 渲染控件 + 主窗口
tools/SmokeTest/      冒烟测试（解析→转换→索引全链路，不经过渲染管线，332 项精确断言）
tools/UiSmokeTest/    UI 冒烟测试（无头 Avalonia 真实跑渲染管线，拦截"渲染期非法操作"类缺陷，40 项断言含像素级校验）
tools/PerfProbe/      性能基准（加载分段耗时 / 内存 / 逐缩放帧耗时 / LOD 扫描 / 墨迹覆盖率 / 泄漏验证）
tools/SampleGen/      生成 samples/sample_features.dxf（覆盖全部实体类型与边界情况）
tools/BlockExport/    把图纸里的块定义逐个导出成独立 DWG（WBLOCK 语义，含嵌套块依赖 + 回读校验）
tools/ShapeExport/    把模型空间图元按相交关系分组（连通分量），每组导出成独立 DWG
tools/DwgCapture/     无头 Avalonia + Skia 渲染 DWG/DXF 为 PNG（文件交接，可被 DSH 插件调用）
tools/Shared/         CadToolkit.cs：上面两个导出工具共用的读写 + 跨文档拷贝（<Compile Include> 链进各自项目，不单独建程序集）
tools/Probe/          ACadSharp API 反射探针（摸 API 用，可删）
tools/regress.ps1     一键回归：Debug/Release 构建 + 核心冒烟 + UI 冒烟，汇总到 .regress.log
samples/              样例 DXF
docs/                 分析报告等技术文档
```

## 构建与运行

```bash
dotnet build AvaDrawingView.slnx        # 注意是 .slnx，不是 .sln
dotnet run --project src/DrawingView     # 启动 GUI
dotnet run --project tools/SmokeTest   # 核心链路冒烟（不要加 --nologo，会被当应用参数）
dotnet run --project tools/UiSmokeTest # UI 渲染冒烟（无头，不弹窗口）
dotnet run --project tools/PerfProbe -- "D:\path\to.dwg"   # 性能基准（--load-only 只测加载，--repeat 8 验泄漏）
dotnet run --project tools/SampleGen   # 重新生成 samples/ 下的样例图纸（含 sample_far.dxf 大坐标样例）
dotnet run --project tools/BlockExport -- "D:\src.dwg" "D:\out" --referenced  # 块导出为独立 DWG
dotnet run --project tools/ShapeExport -- "D:\src.dwg" "D:\out"               # 相交分组导出为独立 DWG
dotnet run --project tools/DwgCapture -- "D:\src.dwg" "D:\out\shot.png"      # 无头渲染 PNG（--report 可落 JSON）
```

**渲染对照 / 实体诊断开关**（P2-7、P2-8 就是这么量出来的）：

- `DwgCapture --rect <left,top,right,bottom>`：只渲染指定世界坐标窗口（内部 `ZoomTo`，
  **padding=1 且抵消 `Viewport.Fit` 内建的 0.9 留白**，即 1 像素 = 1 世界单位，可与
  `OdaShot --rect` 同 rect 同画布逐像素对拍）；`--rect-pad` 可改回留白；
  `--report` 的 JSON 多出
  `regionEntities / regionLoops / regionUnresolved / pointMarkEntities / spatialClipped`
- `DwgCapture --background`：默认 **black**（与 AutoCAD 模型空间 / OdaShot 一致，方便直接对拍）；
  要白底打印观感或复现旧基线时显式传 `--background white`
- `DwgCapture --hide-layer <图层名>`（可重复）：按图层隐藏后出图，用于"这块多余内容是谁画的"
  逐层排除。走**转换期**过滤（`GeometryConvertOptions.HideLayers`，`ConvertEntity` 入口直接跳过），
  文字 / 点标记 / 带裁剪图元一并生效，`--report` 的 `layerFiltered` 记录跳过的实体数。
  **注意**：隐藏图层会改变 `coreExtents`，自适应出图的缩放/平移随之变化 —— 用固定像素矩形比较
  墨迹会得到假信号，务必配 `--rect`（世界坐标固定）再做逐层排除
- `PerfProbe --types`：实体类型直方图 + ACIS 载荷概览（前 14 条 SAB 记录，指针后标目标类型）；
  `--dump-sab <out>` 导出首条载荷（就是这么落的 `samples/sample_region.sab`）；`--points` 列点实体
- `DrawingView_NO_REGION=1`：关掉 REGION 轮廓重建，与默认渲染做像素级 A/B

**字体（P2-16）**：文字字体按 STYLE 解析，不再全局硬编码。查找顺序：
图形目录 → `DrawingView_FONT_PATH`（分号分隔）→ `C:\Windows\Fonts`；
样式写 TTF 就直接用，写 SHX 就找 AutoCAD 的同名 TrueType 替身（`romans.shx → romans__.ttf`、
`simplex.shx → simplex_.ttf`、`txt.shx → monotxt_.ttf`），中文（大字体）落到宋体一族。
`DwgCapture --report` 的 `fonts` 字段列出本图实际用的字体与条数（带「SHX 替身 / 顶替 / 默认兜底」标注），
状态栏加载进度也会打印一行"字体：…"。诊断用 `TextFontLibrary.Directories` / `ClearCache()`。
- `DrawingView_NO_SPATIAL_CLIP=1`：关掉**实体级空间过滤器**裁剪（`XDictionary: ACAD_FILTER → SPATIAL`，
  即 XCLIP / 局部打开遗留的裁剪边界），与不认裁剪的渲染器做 A/B；`--report` 的 JSON 有 `spatialClipped`
  记录被整块裁掉的实体数
- `DrawingView_CLIP_XFORM=auto|raw|tf|full`：裁剪边界点的坐标取法（P2-17 标定用，默认 `auto`）。
  边界 `InsertTransform` 是单位阵时当世界坐标直接用，否则先过它再过实体插入矩阵。
  遇到"裁剪位置明显不对"的图纸，把它切成 `raw` / `full` 各渲一遍再对照 AutoCAD 的 `ZOOM E` 就能定案
- `DrawingView_CLIP_UNRESOLVED=frame|drop|auto`：裁剪边界与实体**完全不相交**（= 过滤器坐标系读不出来）
  时怎么办。默认 `frame`：保留框内部分、退化为按文件头 `EXTMIN/EXTMAX` 裁；`drop`：整块丢弃；
  `auto`：按实体落在图纸范围内的面积占比判（阈值 `DrawingView_CLIP_MIN_COVER`，默认 0.3）。
  注意：这里已经是**真正的兜底**了 —— 2026-11-14 起先试"反向矩阵换算"（见显示保真第 17 节），
  换算后能相交就不会走到这条。剩下走到这里的，才是真读不出来的。
  为什么默认仍保守取 `frame`：`9---陶板节点 (0).dwg` 的 12 个 INSERT 边界全失效、AutoCAD 照画
  （丢不得，其中一块覆盖率只有 0.4%）；`9---陶板节点 (05).dwg` 的 `H=4301` 相反、AutoCAD 全不画
  （覆盖率 17.5%）。两者覆盖率**反相关**，没有可靠判据 —— 详见整改方案 P2-22；
  第 17 节那条修好之后，这两处都靠"换算出真矩形"解决，不再依赖这里
- `DrawingView_CLIP_INV_XFORM=0`：关掉"用反向矩阵换算边界"（第 17 节），回到"正向矩阵是单位阵
  就当边界已是世界坐标"的旧行为，用于对照实验
- `DrawingView_CLIP_DEBUG=1`：打印每个空间过滤器的换算结果 `[clip] H=… inv=… rect=…`，
  诊断"这块为什么被裁/没裁"
- `DwgCapture --drop-clip-handle <十进制句柄>`（可重复）/ `DrawingView_CLIP_DROP_HANDLES=4301,4302`：
  把指定顶层实体的"边界失效过滤器"按**整块丢弃**处理，覆盖上面那条的默认兜底。用于已经用
  AutoCAD/ODA 实测确认"该处应当为空"的个别图纸（例：`9---陶板节点 (05).dwg` 的 `H=4301`，
  加它之后图框左上角那道多余折角符号消失，`onlyA` 22,7xx → 22,126）。句柄是文件内的十进制值，
  与 `--report` / 诊断打印的 `H=` 一致
- `DrawingView_CLIP_BAD_NORMAL=skip`：过滤器 `Normal` 不是单位向量（ACadSharp 解析不可信）时**不裁**；
  默认退化按图纸范围裁（`THSYB1-12` 的 H=845 靠这条把探出 800 单位的内容收回来）

**改完代码先跑一键回归**（省掉手敲上面四条，且构建失败会 fail-fast）：
```powershell
powershell -ExecutionPolicy Bypass -File tools\regress.ps1   # 明细写仓库根 .regress.log
```

两项冒烟测试都会把结果**同时**写 Console 和一份 UTF-8 日志文件
（`DrawingView_SMOKE_LOG` / `DrawingView_UISMOKE_LOG` 指定路径，默认落 `%TEMP%`）。
Windows 下必须读日志文件：PowerShell 重定向原生命令 stdout 用的是 ANSI 码页，
dotnet 的 UTF-8 中文会整体变乱码（`[Console]::OutputEncoding` 在 PS 5.1 非交互重定向下不生效）。

**改渲染相关代码后必须跑 `UiSmokeTest`**：`SmokeTest` 只走 解析→转换→索引，完全不进
Avalonia 的渲染管线，所以像"渲染期调用 InvalidateVisual"这类缺陷它 100% 漏掉（2026-09-16 实际漏过一次）。
`UiSmokeTest` 用 `Avalonia.Headless` + `UseHeadlessDrawing=false` + `UseSkia()` 真实跑一遍
`SetDisplay → 布局 → 渲染`，并用 `CaptureRenderedFrame()` 逐帧驱动。

**改渲染性能相关代码后必须跑 `PerfProbe` 并与下表基线对比**（详见「大图性能」一节）。

**`CadDrawOperation` 里绝不能读 Avalonia 依赖属性**（2026-09-24 实测踩中）：`Render` 跑在渲染线程，
在渲染期读 `_owner.<StyledProperty>` 会让**整个窗口停止渲染**（内容全空 = 宿主窗口看起来"透明"，
且不抛异常、不写崩溃日志，只能靠截图对比发现）。需要宿主状态时一律在**构造函数里快照成字段**
（`_bg` / `_hidden` / `_selectedSnapshot` 都是这个模式），`Render` 期间只读字段。

## 大图性能架构（借鉴 ReadCAD，2026-09-16 实测落地）

参考对象：`C:\Program Files (x86)\ReadCAD`（未混淆，PDB 可提取类型名；
`AIConfig\config\render_settings.json` 是它的真实渲染配置，极具参考价值）。
其思路 = `Cached*` 实体预计算 → 按图元类型打包 `Gpu*Buffer` 大缓冲 → 每帧视口过滤 + LOD + 提交缓冲。

我们的 Skia 等价物（零互操作栈）：

1. **几何烘焙合并**（`DrawingView.Core/BakedGeometry.cs`，Skia 版 GpuBuffer）
   加载线程把实体按 **(世界尺寸档, 图层, 颜色)** 合并成少量 SKPath。
   实测 5.36MB 图纸（85,515 个可显示实体）→ **214 个批次**（压缩 399 倍），烘焙仅 38–46ms。
   每帧 DrawPath 次数从 8.5 万降到批次数级，且同色批次连续绘制不再抖动 SKPaint 状态。
   有线宽（8,210 个）与文本（9,999 个）实体不合并，走逐实体 + LOD 路径。
2. **双路渲染**（`DrawingView`）
   可见比例高（缩小/全图）→ 批次路径；可见比例低（放大）→ R树剔除 + 逐实体路径。
   用"视口面积/图纸面积"估算可见数做切换，避免为决策先跑一次十几万条目的 R树查询。
   **两条路必须施加同一套 LOD 与配色规则**，否则切换瞬间画面会跳变。
3. **尺寸分档 LOD**（对应 ReadCAD 的 `MinEntityPixelSize`）
   档界按 4 倍递增（1/4/16/…/1M），渲染时"整档最大像素尺寸 < 阈值"则整档跳过。
   因为比较的是档上界，**永远不会剔掉屏幕上可见的实体**，最坏只是多留一档。
4. **文本 LOD + 字体缓存**（`MinTextPixelSize=5`；SKFont 按整数像素缓存复用，
   修掉了原实现每字 new SKPaint 且不释放的泄漏）。
5. **性能自观测**：`DrawingView.LastDrawMs / AverageDrawMs / DrawCount`
   （对应 ReadCAD 的 PerformanceMonitorService），由绘制操作用 Interlocked 写入。

### 实测基线（5.36MB `3、幕墙横方总汇.dwg` / 1600x900 无头，12 帧中位）

**2026-10-22 重测**（`tools/regress.ps1` 同版本，Release，本机负载约 40%——墙钟偏高，比代码看"绘制"列）：

| 缩放 | 可见实体 | 墙钟 | 绘制耗时 | FPS(墙钟) | 分段（批次/文本/点标记） |
|---|---|---|---|---|---|
| 1x（全图） | 85,544 | 17.0ms | **6.7ms** | **58.8** | 3.0 / 1.1 / 4.5 |
| 2x | 72,960 | 19.3ms | **8.6ms** | **51.7** | 3.9 / 1.1 / 5.2 |
| 4x | 8,883 | 16.3ms | **6.1ms** | **61.3** | 4.4 / 1.1 / 1.7 |
| 平移（8px/帧） | — | 18.9ms | — | **52.9** | — |

同日修掉两个 5 倍级的耗时回退，修复前 1x 绘制 **22–26ms（27FPS）**、2x **33–48ms（18–21FPS）**：

1. **点标记逐点 `DrawCircle`**：11,401 个 POINT（图上是 DEFPOINTS 定义点，默认隐藏）里有 4,477 个
   在可见图层，PDSIZE=0 按"5% 视高"算成 **45px** 实心圆，每个 6.6µs → 单这一趟 **29.5ms**。
   已改为「点模式固定 1.5px + 按颜色合批 + 亚像素去重」，见 `PointMarkStyle.DotDiameterPx`
   与 `DrawPointMarks`。修复后点标记 pass 4.5ms，且**与修复前渲染逐像素零差异**（该图上这些点
   本来就被别的几何压住）。
2. **2x/4x 误走逐实体路径**：`BucketVsEntityCostRatio` 从 200 降到 20（实测标定）。
   2x 时估算可见 2.1 万 < 344×200 → 逐实体画 7.3 万条 = 33ms；切批次后 8.6ms。

> **历史基线不可直接比**：上表 4.5/5.4/5.8ms 是 P1/P2 保真改造**之前**的数（当时宽多段线是细线、
> 没有 9,183 条轮廓填充、没有按线宽拆批）。现在多画了真实几何，1x 6.7ms 相当于"多干 5 成活还快 30%"。
> 判退化请用**同一天、同一台机器**的两次测量比，不要跨版本对表。

无头捕获本身有 **~11ms 固定开销**（16x 时 0 个实体可见、绘制仅 0.3ms，墙钟仍 11.4ms），
所以 4x 及更深的墙钟列已被固定开销主导，只有"绘制耗时"能反映代码变化。
真实 GUI 下 FPS 高于表中数值。**看"绘制耗时"列对比渲染代码，看"墙钟"对比体感。**

> 拆一下贡献：LOD 关闭时 1x 为 16.6ms / 36.3 FPS，LOD 1.5 后 7.0ms / 59.9 FPS。
> 即"可见性过滤 + 烘焙合并"贡献大头，LOD 再补最后一截；没有过滤，光靠 LOD 也到不了这个数。

### 性能调参（实测依据）

- `MinEntityPixelSize` 默认 **1.5**。LOD 扫描（12 帧中位绘制耗时 + 墨迹覆盖率）：
  | 阈值 | 1x 绘制 | 2x 绘制 | 1x 墨迹覆盖 | 2x 墨迹覆盖 |
  |---|---|---|---|---|
  | 0（关） | 16.6ms | 19.1ms | 2.09% | 3.79% |
  | 1.0 | 6.6ms | 11.6ms | 2.06% | 3.76% |
  | **1.5** | **7.0ms** | **8.2ms** | **2.06%** | **3.71%** |
  | 3.0（ReadCAD 值） | 4.8ms | 8.0ms | 1.85% | 3.71% |
  取 1.5 而非 3.0 的原因：3.0 会再丢 10% 墨迹（2.06% → 1.85%），换来的 2ms 不值。
  > 墨迹覆盖率的口径 2026-10-22 修过一次：旧实现写死"非白像素 = 墨迹"，而黑底捕获时
  > 几乎全屏都算墨迹（恒报 98%~99.9%），指标形同虚设。现在以**左上角像素为背景色**做差。
  > 数字与本表历史值（2.07%/3.54%）同一量级，可以对照。

  续：尺寸档按 4 倍递增，"256–1024 单位"档在 2x 下最大像素尺寸约 1.19px —— 阈值 1.0 必须整档保留
  （19 万点），1.5 才能整档跳过。1.5 与 1.0 在 1x 下墨迹覆盖率几乎相同（2.06% vs 2.06%），
  而 2x 绘制从 11.6ms 降到 8.2ms；阈值 3.0 会再省一点，但 1x 墨迹掉到 1.85%，不取。
- **不要照搬 ReadCAD 的 `EnableBulkLineAntialias=false`**：在 Skia 栈下实测
  **反向优化**（1x 从 43.9ms 恶化到 179.2ms，Skia 走不到细线快速路径）。
  借鉴别人配置必须逐项实测，不能默认成立。
- 烘焙阈值 `GeometryBaker.DefaultMinEntityCount = 20000`：低于此逐实体已达标（~20ms），
  不烘焙省一份内存。内存代价实测：工作集 477 → 542MB（+65MB，强制 GC 后托管堆仅 25MB）。
- **泄漏验证**（`PerfProbe --repeat 8`，同一 5.36MB 图纸连开 8 次）：
  工作集 594 → 642 → 645 → 662 → 657 → 664 → 638 → 652 MB（**平台期，不线性增长**），
  托管堆恒为 47.8MB，`DisplayList.DisposedCount` 每轮 +1（0→7）。
  若未释放，8 次应累加约 8×550MB。这条是"引用计数释放真的生效"的量化证据。
- 测量方法论（教训）：无预热/不隔离 GC 的帧测量，同一配置两次能差 25%——
  必须预热 3 帧 + `GC.Collect` + ≥12 帧 + 中位数；对比配置看"绘制耗时"而非墙钟。

## 显示保真与资源释放（2026-09-16 修复；断言数实测 2026-10-22：332 项核心 + 40 项 UI）

### 1. `Arc.Sweep` 在 ACadSharp 3.4.9 里是**负值**（最隐蔽的真缺陷）
`Arc.Sweep` 返回的是 `startAngle - endAngle`（正常逆时针弧为负），原实现写
`if (sweep <= 0) sweep = 2π;`，于是**每一段弧都被画成整圆**。样例里的四分之一圆
`(0,60)–(15,75)` 被画成整圆，肉眼很难发现（大面积弧重叠时更像"图纸本来是圆的"）。
固定做法是自己算扫角，并规避 Skia 在 ±360° 时 `ArcTo` 什么都不画的坑：
```csharp
private static double ArcSweep(double startAngle, double endAngle)
{
    double sweep = endAngle - startAngle;
    while (sweep <= 0) sweep += 2 * Math.PI;   // 归一到 (0, 2π]
    return sweep;
}

private static void AddArcTo(SKPath path, SKRect oval, double startRad, double sweepRad)
{
    double sweepDeg = sweepRad * 180.0 / Math.PI;
    if (sweepDeg >= 360.0 - 1e-6) sweepDeg = 359.999;    // 恰好 360° 时 ArcTo 不画
    else if (sweepDeg <= -360.0 + 1e-6) sweepDeg = -359.999;
    path.ArcTo(oval, (float)(startRad * 180.0 / Math.PI), (float)sweepDeg, false);
}
```

### 2. 文本包围盒必须走真实字体度量（`TextMetrics`）
原实现用 `raw.Length * height * 0.7` 估宽度，样例里 15 字符 × 字号 6 估出 halfW≈63
（真实渲染宽约 49），把 `Extents` 左边界从 0 虚增到 **-53** → 开图后 `Fit()` 画面偏小偏左。
现统一走 `DrawingView.Core/TextMetrics.cs`：
- 插入点语义 = **基线左端**，与 `canvas.DrawText(text, 0, 0, font, paint)` 一致；
- `MeasureLocalBounds(text, height)` → `ToWorldBounds(local, origin, rotationRad)`；
- 度量与渲染**共用同一个 `TextMetrics.SharedTypeface`**，否则"量出来的框"和"画出来的字"对不上；
- `SKTypeface` / `SKFont` 按整数像素高度缓存复用，字体度量返回 0 时做兜底钳制。

> `Extents` 现在与屏幕墨迹一致，这条约束由冒烟测试锁住：`范围左边界 == 0`（不是 -53）、
> `文本宽度 49.1`（不是 126）。

### 3. 按 AutoCAD 语义决定"画什么"
| 情况 | 处理 | 依据 |
|---|---|---|
| `Entity.IsInvisible`（DXF 组码 60） | 转换阶段直接跳过 | 实体自身不可见 |
| 图层 `!Layer.IsOn`（关闭） | 仍转换，但进 `InitiallyHiddenLayers` | 用户可在图层面板手动打开 |
| 图层 `LayerFlags.Frozen`（冻结） | 同上 | 同上 |
| 块内 `Color.IsByBlock` | 继承外层 INSERT 的**解析后颜色** | 原实现当白色 → 白底上直接消失 |
| `Hatch.IsSolid` | 走 `AddFill` 填充，而非描边边界 | 实心填充原来被画成空心轮廓 |
| `Insert` 的 `RowCount/ColumnCount` > 1 | 展开为 MINSERT 阵列 | 见下 |

**MINSERT 语义**：行间距沿 **+Y**、列间距沿 **+X** 递进（AutoCAD 约定，容易搞反）。
`Insert.IsMultiple` 是**只读属性**（由行列数派生，不能赋值，赋值编译不过）。
实例数上限 `MaxMInsertInstances = 10_000`，防止畸形图纸炸内存。

**幽灵图层**：ACadSharp 会注入一个无实体引用的占位图层（样例里叫 `unnamed0`，handle=5，flags=None），
`doc.Layers` 因此比声明数多。过滤条件是**双重**的：名字以 `unnamed` 开头 **且** 零实体引用，
只按名字过滤会误删用户真名叫 `unnamed_xxx` 的图层。

### 4. `DisplayList` 引用计数释放（原生内存不能指望 GC）
`SKPath` 是原生内存，GC 完全看不见；引入几何烘焙后原生几何又翻了一份，不释放会随开图线性增长。
现在 `DisplayList : IDisposable` 用引用计数：
- `CadDocumentLoader.Load()` 返回时计数 = 1；
- `CadDrawOperation` 构造时 `AddRef()`、`Dispose()` 时 `Release()`（Avalonia 会在绘制操作销毁时调用）；
- `DrawingView.SetDisplay()` 换图时 `Release()` 旧实例；`ReferenceEquals` 相同实例不重复释放；
- 计数归零才 `DisposeCore()`：释放全部实体的 `Path`/`FillPath` + `BakedGeometry`；
- `DisplayList.Empty` 是共享单例（`IsSharedInstance`），`AddRef/Release` 对它空操作；
- `DisplayList.DisposedCount` 是静态计数，专供测试断言"确实释放了"。

**`BakedGeometry.Dispose()` 只释放合并后的 `StrokePath`/`FillPath`**，不要释放
WideStroke/Text 实体——那些是 `DisplayList.Entities` 里的同一批对象引用，由 `DisplayList` 统一释放，
重复释放会二次释放原生句柄。

**计数归零后延迟 300ms 才真正销毁（`DisposalDelayMs`，2026-09-25 修复崩溃）**：
合成器线程可能仍在回放持有这些 SKPath 的最后一帧绘制流，归零瞬间销毁会与回放并发，
在 `sk_canvas_draw_path` 上 AccessViolation 直接杀进程（宿主"关闭文档标签页崩溃"的根因，
WER 事件 0xc0000005 实证）。`OverlayLayer` 共用同一延迟。测试/基准工具置 0 恢复立即释放，
但断言要用 `IsDisposed` 轮询等待。

### 5. 取消令牌真的生效
`Convert` / `SpatialIndex.Build` / `GeometryBaker.Bake` 都收 `CancellationToken ct = default`，
按 1024（转换）或 4096（烘焙）条实体周期检查一次 `ct.ThrowIfCancellationRequested()`。
注意 `DwgReader.Read()` **不接收** CancellationToken（解析阶段无法中断）。

### 6. 块内实体身份：`CadEntity.OwnHandle`
扁平模型空间里，块引用展开出的每个子实体各自是一个 `CadEntity`，但 `Handle` 被
`handleOverride` 覆盖成**最外层** INSERT 的句柄（这样点选块内任一图元都能定位到块引用整体）。
代价是"块内哪个子实体"没了身份。

`CadEntity.OwnHandle` 补齐这一层：填实体的**自身**句柄（块定义层），不做继承。
- 顶层实体 `OwnHandle == Handle`；块内子实体 `OwnHandle != Handle`；
- 同一个子实体被插入多次 → 多个 `CadEntity`，**`OwnHandle` 相同而 `Handle` 不同**；
  要区分"哪一个插入"就用 `(Handle, OwnHandle)` 组合；
- 尺寸标注的匿名块也是下放句柄的（`AddDimensionBlock` 传 `handleOverride`），
  所以它的块内几何归属到 **Dimension 实体的句柄**上，同样适用本规则；
- `0` = 未填（旧数据）。**不要**用 `OwnHandle` 替代 `Handle` 做定位：
  `DisplayList.HandleBounds` 只收 `Handle`。

回归防线：SmokeTest 有 6 条断言（全部实体已填 / 块内 14 条 / 非块内 15 条 /
2 个外层句柄 / 2 个自身句柄 / 每个外层句柄下可见全部子实体）；
`tools/Probe` 结尾还有一段「OwnHandle 验证」，用**独立遍历块表**的方式
逐条证实 `(外层句柄 → 目标块 → 自身句柄)` 的可达性（实测 70881 实体 / 68669 条块内实体全通过）。

### 7. 叠加高亮通道：`DrawingView.SetOverlay`
`SetSelection` 只能选中**整个 `CadEntity`**。但标注场景的目标可以更细 ——
多段线的某一段、填充的某个环、块引用内的某个子实体 —— 这些在 `DisplayList`
里没有独立身份（一条 `LwPolyline` 就是一个 `CadEntity`），因此需要一条
"任意几何的临时高亮"通路，对应 WPF/Teigha 侧的 `TransientManager`。

用法（几何给**世界坐标**，控件套用当前 world→screen 变换）：
```csharp
var layer = new OverlayLayer { Shapes = new[] { OverlayShape.Box(worldRect, colorArgb: 0xFFFF00FF) } };
view.SetOverlay(layer);      // 所有权转移给控件，之后不要自行 Release
view.ZoomToOverlay();        // 缩放到叠加几何（用 TightBounds，圆弧不会被控制点撑大）
view.ClearOverlay();
```
- `OverlayShape`：`Path`（SKPath，世界坐标）+ `Fill`/`FillAlpha`/`ColorArgb`/
  `StrokeWidthWorld`（`<= 0` = 屏幕 2 像素发丝线）/ `Dashed`（惯例用于表达"只能整体定位"的降级目标）。
- **不走 LOD**：叠加表达的是"当前操作目标"，被 LOD 剔掉等于功能失效。
  它画在选中高亮**之上**（目标通常比选中集合更精确）。
- **引用计数与 `DisplayList` 同款**：UI 线程换层的那一刻渲染线程可能仍在用旧几何,
  立刻 Dispose 就是释放后使用。`CadDrawOperation` 构造时 `AddRef`、`Dispose` 时 `Release`。
- **`SetDisplay` 会自动清空叠加层**：它的几何是上一张图的世界坐标，留着会画到错误位置。
- 回归防线：UiSmokeTest 有 10 条断言，**像素级**（`CaptureRenderedFrame` 里数洋红像素）——
  画面上确有叠加层、LOD 全剔除后仍在、清除后归零、换图后自动清空且原生几何已归还。
  `DrawingView_UISMOKE_PNG=<路径>` 可导出该帧 PNG 供人工核对观感。

### 8. 多段线凸度归属：42 组码管「本顶点 → 下一顶点」（2026-09-23 修正）

**结论**：DXF 组码 42 挂在顶点上，语义是该顶点到**下一顶点**那一段的凸度；
闭合折线的**最后顶点**管收尾段（末顶点 → 首顶点）。

**原实现错在哪**：每段的凸度取成了**终点顶点**的 `Bulge`，等于整体错位一段 ——
① 首顶点的凸度**被完全丢弃**；② 闭合折线收尾段的凸度被挪到上一段去（收尾段原本只会
被 `Close()` 画成直线）。`LwPolyline` / `Polyline2D` / 填充的折线边界三处都是这个写法。

**为什么长期没被发现**：`sample_features.dxf` 里那条样例折线只有 v0 带 `bulge=1`，
原实现把半圆**整个丢掉**画成纯三角形；而当时的黄金断言是"中心 + 尺寸窗口"
（`HasEntity(list, 10, 160, 19, 21)`），**恰好容得下三角形**，所以两条都"通过"。这正是
"断言只覆盖到能通过的那一档"的典型。

**定标方法（可复用）**：ASCII DXF 的组码可直读 —— 文件自己就声明了凸度挂在哪，
于是一条只有 v0 带 bulge 的折线构成一个**无需争议的判据**：
正确渲染应下探到 y=140（半圆），错误渲染停在 y=150（纯三角形）。
三方比对（`LabelProbe --bulge`）：画布几何 / 按规范重建 / **ACadSharp 自己的
`GetBoundingBox`**（另一份独立凸度实现，站规范这边）。

**实测规模与结果**（`9---陶板节点 (1).dwg`）：含凸度多段线 604 条 →
与 `GetBoundingBox` 吻合：规范解释 **585** ／ 旧约定 161，只有规范吻合 **440** 条。
修正后 585 / 585（旧约定分支自然也对齐，因为实现只剩一份）。
扭曲幅度可直观看到：`0x84` 规范是 `(-275,0)-(275,30)`，旧实现给 `(-280,-113)-(280,143)`。

**回归防线**：SmokeTest 现在按**句柄定位 + 精确包围盒**钉死半圆
（`0x4E` 应为 `(0,140)-(20,170)`），不再用宽容的"中心 + 尺寸窗口"。

### 9. `Region` 在画布上不渲染是"没数据"，不是"不想画"（2026-09-23 探明）

`GeometryConverter.ConvertEntity` 的 switch 不处理 `Region`，走 `default` 跳过 →
面域在画布上**根本不存在**。原以为补个渲染即可，实测是数据源头就空的：

- 结构上有得取：`Region.Wires` 元素类型是 `ModelerGeometry+Wire`，带 `Points`、
  `XAxis/YAxis/ZAxis/Translation/Scale`；`Silhouettes` 也带 `Wires`。
- 但实测（`LabelProbe --region`，`9---陶板节点 (1).dwg`）39 个面域：
  **有 `Wires` 的 0 个、Wire 总数 0、`Silhouettes` 0、`ProprietaryData` 长度 0**。
  另一张 `02-设计说明.dwg` 面域数本就是 0。
- 即 **ACadSharp 3.4.9 的读取器根本没有把 ACIS 专有数据读进来**（字段在，永远是空）。

> **2026-10-21 结论已更新（P2-7）**：升级到 ACadSharp **3.7.1** 之后 `ModelerGeometry.AcisData`
> 里有完整载荷（R2013+ 是二进制 SAB），只是 `GetAcisText()` 返回 null —— 所以"没数据"是
> 版本问题，不是格式问题。现由 `AcisSab`（SAB 记录流）+ `RegionOutline`（loop→coedge→edge→
> vertex→point 走 B-rep 链）重建轮廓，幕墙图 162/162 全成功。`Wires` 那套 API 仍然为空，
> 不要走它。

### 10. 根层标注过滤：`GeometryConvertOptions.SkipRootAnnotations`（2026-09-25）

- `GeometryConverter.Convert(doc, ct, options)` / `CadDocumentLoader.Load(path, progress, ct, options)`：
  `SkipRootAnnotations = true` 时跳过模型空间**根层**的 Dimension / Leader / TextEntity / MText
  （只读等价于 WPF 标注页 `ClearModelSpaceDimension` 的真删除；块内同名实体不受影响；
  Table 本就不渲染无需处理）。
- 断言在 SmokeTest 功能样例：总数 29 → 26、根层 15 → 12、块内仍 14，全部精确值。
- 排查时踩掉的假事实：`MText` **不**继承 `TextEntity`（见踩坑节），过滤集合必须显式带上 MText。

### 11. 法线 (0,0,-1) 的实体必须做 OCS→WCS 镜像（2026-09-25 修复，最隐蔽第二名的真缺陷）

- DXF/DWG 里实体可带**挤出方向（法线）**：`Normal = (0,0,-1)` 时实体坐标存的是 OCS，
  2D 情形 OCS→WCS = **绕 Y 轴镜像（x→-x）**。不做这步，实体被画到 X 镜像位置。
- 实测 0053datr.dwg：41 条轮廓圆角弧 `Normal=(0,0,-1)`，圆心 OCS x≈+900，
  WCS 应在 x≈-900 —— 全部画到 +900 一侧 → **轮廓每处圆角断开（"线段不连贯"）、
  右侧多出一撮杂点、Extents 被撑大 1867.8 → 94.7（近 20 倍）导致"适应窗口不能占满"**。
  一个缺陷同时解释两个用户报障。
- 修复：`GeometryConverter.AddStroke/AddFill` 汇聚点统一 `OcsMirrorMatrix(e)`
  （`Normal.Z < 0` → `SKMatrix.CreateScale(-1,1)`，在块插入矩阵之前先镜像）。
  **Normal 不在 Entity 基类上**（各具体类自行声明：Line/Arc/Circle/IPolyline/Ellipse…），
  只能 switch 逐类型取；任意轴法向（非 ±Z）2D 图纸未遇到，未处理。
- 验证：端点连通性分析（BlockScan `--gaps`）悬空端点 120 → **0**；
  手工验算 0x9E：WCS 圆心 (-900.372,-13516.825) r=0.5 两端精确接上邻接线段。
- 通用教训：**线段端点对不上的"断线"，先查法线再怀疑转换器**——源数据几乎总是对的。

### 12. 画布鼠标语义：左键点选/框选，平移走中键/右键（2026-09-25）

- `DrawingView` 输入约定（对齐 WPF/Teigha 的 `MouseMode.Selection`）：
  **左键单击 = 点选**（释放时位移 ≤4px），**左键按下拖动 = 框选**（橡皮筋矩形），
  **中键/右键拖动 = 平移**，**Esc = 取消**（框选拖动中只取消橡皮筋，否则清空选择），
  **双击中键 = 适应窗口（Zoom Extents）**。旧实现"左键拖动 = 平移"、"左键双击 = 适应窗口"
  已移除——双击左键在 AutoCAD 里是编辑实体，查看器无编辑能力，故无操作（两次点选自然退化）。
  双击判定自实现（`IsMiddleDoubleClick`：500ms + 4px 内两次中键按下），因 Avalonia 的
  DoubleTapped 手势只认左键，且 12 的 `TopLevel` 无 `PlatformSettings` 可取系统双击间隔。
- 框选方向语义沿 CAD 惯例：**左→右 = 窗选**（完全包含才选中，蓝色实线框），
  **右→左 = 叉选**（与包围盒相交即选中，绿色虚线框）。判定用 `CadEntity.Bounds`，
  命中候选来自 `DisplayList.Index.Query(worldRect)`，并过滤隐藏图层（与 `HitTest` 同规则）。
- 框选零命中 = 清空选择（与点选空白处一致）。公开 API：`SelectByRectangle(SKRect worldRect, bool window)`
  供程序化调用；橡皮筋矩形在 `Render` 里画（屏幕坐标，`_bandStart/_bandCurrent/_bandActive`）。
- **点选命中 = AutoCAD 拾取语义（2026-09-25 修正）**：填充实体点在内部才算命中；描边实体
  把容差膨胀进描边几何（`SKPaint.GetFillPath`，宽度 = 2×容差）压线才算——**点在矩形/圆
  内部空白不再选中**（旧实现"包围盒内距离按 0"会让图框/大矩形吃掉内部所有点击，
  双击"莫名选中所在的矩形"即此根因，双击本身只是两次点选）。无几何实体才退回包围盒距离。
- 世界矩形换算注意：`Viewport.ScreenToWorld` 屏 Y 向下 → 世界 Y 向上，两角换算后取
  Min/Max 归一成合法 `SKRect`（Top&lt;Bottom），不要直接拿屏幕矩形套。
- **选中高亮样式 = AutoCAD 默认（2016+ glow）**：蓝色 (0,120,215)——描边两趟
  （宽 `hairline×6`、α=80 外圈模拟光晕 + `hairline×2` 实线内芯），实心填充罩半透明蓝
  （α=70）。框选带同色系（窗选蓝实线 / 叉选绿虚线）。旧橙红高亮已废弃。

### 13. PDMODE 0（点）不吃「5% 视高」，屏幕恒定的东西必须合批（2026-10-22）

两件事捆在一起，因为同一个坑：`PointMarkStyle.SizePx` 的"PDSIZE≤0 → 视高 5%"只对**符号模式**
（基础形状 2/3/4 及 32/64 叠加）成立；基础形状 0 是"一个点"，AutoCAD/ODA 画出来就是像素级小点。

- 反例数据：幕墙图 4,477 个可见 PDMODE=0/PDSIZE=0 的点，按 5% 视高算 = **45px 实心圆**，
  逐点 `DrawCircle` 花 **29.5ms**（6.6µs/个）。ODA 参考图（`.compare/oda_3幕.png`）同位置什么都没有。
- 修法：新增 `PointMarkStyle.DotDiameterPx` —— 点模式固定 `DefaultDotPx = 1.5px`，
  但 `PDSIZE>0`（用户显式给了世界单位）仍然尊重。
- **同时必须合批**：屏幕空间逐实体绘制的成本全在"每条一次 Skia 调用"上。现在按颜色分组，
  实心点走一次 `DrawPoints`（圆头笔帽），符号聚合进同一条 `SKPath` 一次 `DrawPath`，
  并做**亚像素去重**（点径 1.5px 时同一半像素上的点画出来完全一样，全图视角下 4,477 → 几十）。
  点标记 pass 29.5ms → 4.5ms，且与修复前逐像素零差异。
- 教训：**凡"屏幕空间绘制"的实体（文本 / POINT / 线宽），只要数量是万级就必须合批**，
  否则单条调用的固定开销（~2–7µs）会直接压垮帧时间。

### 14. SHX 单线字体：字节命令流 + 三个反直觉的坑（2026-10-22）

SHX 不是"字体文件"，是**按笔画的字节码程序**：`AutoCAD-86 <kind> <ver>` 头 → 字形索引 →
每条字形一段命令流（高 4 位长度、低 4 位方向的 16 向线段，以及 PEN_UP/DOWN、八分弧、分数弧、
凸度弧、子形、压栈/出栈）。实现见 `ShxFont.cs`（解析 + Pen 解释器）与 `ShxFontLibrary.cs`
（搜索链 / 中英文分流 / 量宽排版）。

三个**反直觉**的坑（都踩过，且症状都不指向根因）：

1. **`0x0E`（仅竖排生效）要把下一条命令连参数一起丢弃**，不是"只忽略这条指令"。
   只忽略指令本身，字形起点会被错移到 `(0, -above)` —— simplex 表现为**整个字跑到画布外**，
   量出来的宽度还是 0，看着像"解析失败"，其实是执行错位。
2. **八分弧的 bit7 是"顺时针"，应当"起点不动、终点减 span"**。参考实现（shxparser）把起点取负，
   等于把弧**镜像**到另一侧：simplex 全是逆时针弧所以看不出问题，gbenor 的 `C`/`G` 立刻变成锥形。
3. **大字体子形是双字节内码，找不到就"不画"，绝不能退回单字节**。gbcbig 每条记录都是
   `<内码2> 00 07 8e 05 <命令流> 07 8f 00`（7016/7018 条尾部一致），`0x8F00`/`0x8E05` 都不是有效内码
   —— 是编码器留下的空子形引用。退回单字节会命中 `0x8F`（一张"静夜思"字形），
   于是**每个汉字底下多出一坨星形乱线**。
4. **字高要取头部的 `above`，不能取"实测大写字高"**（2026-10-22 纠正了更早的错误结论）。
   早先看到 gbenor above=48 而 `H` 上沿只有 38，就以为 above 不可信、改用实测上沿 —— 方向反了：
   AutoCAD（用 ODA 验证）就是按 `above` 缩放 TEXT 高度，所以 gbenor 的"H"在字高 400 时只有 316 高，
   **字宽/字高比例恒为 38/48 = 0.7917**（79 个字符 78 个精确命中）。simplex 的 above=21 恰好等于
   上沿，所以这个坑在 simplex 上完全看不出来。大字体头部没有 above，仍用实测上沿（ODA 反推 90.67，
   与实测 `中`=90 差 0.7%）。
5. **被 `0x0E` 跳过的矢量不能移动画笔**（2026-10-22）。这一条和"忽略指令"是两件事：跳过时
   必须**连位移一起丢**，否则字形起点偏移。反例：simplex 的 `t`/`v`/`-`/`_` 前进量偏小
   （`t` 只有 0.19 em），4 个连字符会糊成一条线。以前的代码里方向矢量分支把 `pen.X +=` 写在
   `if (run)` 外面、而 XY 位移分支整条忽略 —— 两种写法自相矛盾，改完 simplex 的 79 个字符
   与 ODA 基准从 72 个精确命中升到 78 个。
6. **大字体是"压缩字身框"，字宽不等于字高**（2026-10-22 补）。gbcbig 的字身框 ≈ 66×90 单位，
   也就是一个字宽 0.73 em —— 按"字高一个方块"走，每行宽出 37%。反过来也不能用字形结束点：
   `中` 的结束点 12、`图` 28（画完就 PEN_UP 了）。正确做法是用"汉字在字身框内水平居中"
   反推框宽 = **左沿中位数 + 右沿中位数**（gbcbig 实测 5+61=66，见 `ShxFont.AdvanceUnits`），
   只采样 240 个字形就够，标点/窄字都被中位数挡掉。
7. **竖排（SHX vertical text）的三条结论**（2026-10-22 补，ODA 逐项校准）：

   **(a) 竖排是"样式级开关"，不是字体名。** 判据是 DXF `STYLE` 组码 70 的 **bit4**
   （`StyleFlags.VerticalText`）。字体名的 `@` 前缀是 **TrueType** 的约定，
   对 SHX 完全无效 —— `@simplex.shx` 在磁盘上根本不存在，ODA 直接当"找不到字体"处理。
   另外字体自身还得支持：unifont 头部 `modes` 的 **bit2**（simplex/romans 有，gbenor/gbeitc 没有）；
   不支持的字体 ODA 也降级成横排，所以我们的降级行为与参照一致。

   **(b) 竖排前进量（每字向下走多少）是"逐字形"的，取竖排变体收笔的 |y|。**
   字库在竖排变体末尾用一条 **`0x0E` 保护的位移**把笔送回 `(0, -前进量)`；横排时这条位移
   连参数一起丢掉，笔停在 `(前进量, 0)` —— 所以同一个 `Glyph` 的 `Advance` 是横排前进量、
   `AdvanceY` 是竖直前进量。simplex 实测（字高 100）：`A` 31 单位、`1` **31.5**、`0` 31、
   空格 **30**、连字符 **10**。早先把它当成"字体常量 31/21 em"，数字串每字差 2.4px、
   含空格的串每字差 4.8px，`A-101` 这类图号会越排越歪。
   **大字体没有 `0x0E`**，收笔 y 只是"画完停哪儿"的残留（gbcbig 的 `中` 34、`图` 43，
   拿它当前进量会严重叠字），所以回退到字体常量 1 em。gbcbig 的实测常量是 **1.235 em**
   （123.53/字高 100），用 1 em 会紧 19% —— 已知偏差，见下条。

   **(c) 竖排的值不值得继续抠：图集里没有竖排。** `PerfProbe --vertical` 用 bit4 判据扫
   《测试文件/图纸集》抽样 400 张（文本实体 488,141 条、解析失败 0）：

   | 口径 | 条数 | 占比 |
   |---|---|---|
   | ① 样式标志位竖排（bit4） | **0** | 0.00% |
   | ② 实体自身旋转 ±90° | **93,838** | **19.22%** |
   | ③ 两者同时 | 0 | —— |
   | 对照：`'@'` 前缀样式（旧口径） | 82 | 0.017% |

   400 张里只有 1 张**定义了**竖排样式、其下 0 条文本。所以 gbcbig 那 19% 先挂账：
   只有在真遇到竖排中文图纸时才有必要按"每字体实测格高"打表。
   真正高频的是**旋转 90° 的文本（19.22%）**，那是变换矩阵的事（已支持）；
   `'@'` 前缀的 TTF 样式（`@SimSun.ttf` / `@hztxt.shx` 共 82 条）在 AutoCAD 里是"逐字直立堆叠"的
   TTF 竖排，**我们目前按横排画**，量小（0.017%）暂不做。


数据侧结论（`PerfProbe --survey`）：SHX 文本实体占 69%（62,359/90,195），主字体 36% 是 SIMPLEX、
中文大字体 32% 是 GBCBIG；但**本机只有 9 个 .shx**，`tssdeng.shx` / `hztxt.shx` / `jrs.shx` 都缺
（这三者是国内图纸的常客），所以必须"缺字库回退 TTF + 状态栏报数"，不能装作支持。

> **ODA 可以当 SHX 基准 —— 但要自己给它字体路径**（2026-10-22 最终结论，前面两版结论都不对）。
> `OdaShot.exe` 是 ODA 的示例程序，**只搜 `Windows\Fonts`**，所以 `simplex/gbenor/romans/txt`
> 这类不在系统字体目录里的西文 .shx 一律回退成内置字形（于是它们与故意写错的名字宽度完全相同，
> 看起来像"ODA 不解析 .shx"，其实是"找不到文件"）；gbcbig 恰好在 `Windows\Fonts` 里，所以大字体
> 看起来"能用"。
>
> 正确做法：用 Teigha 的**托管**封装 `TD_Mgd_22.5_16.dll` 自己写个控制台程序，
> ① 先 `Services.odActivate(...)` 再 `new Services()`（不激活会直接抛 `SEHException`）；
> ② `HostApplicationServices.Current = 自定义子类`，覆写 `FindFile(file, db, FindFileHint.FontFile)`
> 返回自己的 .shx 路径；③ 读图后取 `DBText.GeometricExtents`。这样西文/大字体**都能**测到真值：
> simplex 与 gbenor 逐字符前进量比值从此精确等于 1.0000。工具在 `.compare/odabase/`（x86、net48）。
>
> 字宽取法：同一字符出 `n=10` 和 `n=9` 两条 TEXT，`W(10) - W(9)` 就是前进量（右侧边距自动抵消）。
> 老办法（`.compare/gen_ruler2.py` 外框标尺 + `measure_box.py` 像素量墨迹）现在只用于事后目视核对。
> 两个坑：**ODA 读 GBK 的 DXF、我们的读取器读 UTF-8**，同一份基准要出两个编码版本；
> 控制台输出也是 GBK，用 Python 分析时要按 GBK 解码，否则多个汉字会塌成同一个替换字符。

### 15. 文字旋转必须取负号再交给画布（2026-11-14 修复）

世界坐标 y 向上、屏幕 y 向下，`canvas.RotateDegrees(+deg)` 画出来的是**世界 −deg**。
0°/180° 对此不敏感（±180 等价），只有 90°/270° 会整段反 180° —— 所以这个错能活很久：

- 受控基准 `.compare/05/rotmtext.dxf`（块内只有 MTEXT，插入 0/90/180/270，`--rect 0,0,860,360` 两边 1px=1单位）：
  修前 90°/270° 两处文字与 ODA **反 180°**，0°/180° 正常；同一张图里块的**几何**（LINE）四档全对，
  说明块变换没问题，差的只是文字旋转量的符号。
- 影响面：走 **TTF 路径**的字（`DrawTextCore` / 亚像素 `DrawTextBarCore`）。
  SHX 那条路是先在**世界坐标**里 `SKMatrix.CreateRotation(rotTotal)` 生成描边几何，不经过画布旋转，
  所以一直是正确的 —— 这也是为什么以前"看着没问题"：多数图纸的文字是 SHX。
- `9---陶板节点 (05).dwg` 实测：图签/备注是 `TK-A3-NT` 块里的 MTEXT（块插入累计 270°）+ 宋体一族，
  整块文字反 180°。修后全图 ODA 覆盖率 77.5% → **94.7%**，缺失墨迹 36,278 → **13,193** px，
  图签区墨迹 14,926 → 26,044（ODA 33,590）。
- 行推进 `stepX = sin r, stepY = cos r` **不用改**：它算的就是"文字局部下方"在屏幕里的方向。
- 断言层不变（`SmokeTest` 的 MTEXT 居中/行距/断行全部 PASS=397 FAIL=0）。

### 16. MTEXT 的字宽取自哪里：\W 覆盖、样式兜底（2026-11-14 修复）

**TEXT 与 MTEXT 的宽度因子来源不一样，不能一把梭**：

| 实体 | 字宽来源 | 依据 |
|---|---|---|
| `TEXT` / `ATTDEF` | **只认实体组码 41** | `.compare/fixture_style.dxf`：样式 0.5 + 实体 1.0 → ODA 按 1.0 画；样式 1.0 + 实体 0.5 → 按 0.5。AutoCAD 建字时把样式值抄进了实体，再乘一次会双重压缩 |
| `MTEXT` | **有 `\W` 用 `\W`，没有就用 STYLE 组码 41** | `.compare/mtstyle.dxf`（AC1015，ACadSharp `DxfWriter` 造）四组实测 ↓ |

受控基准（`txt.shx`，h=80，同一串 `ABCD`，ODA 与我方同图同 `--rect`）：

| 组 | 样式宽 | 内联 `\W` | ODA 宽 | 修前我方 | 修后我方 |
|---|---|---|---|---|---|
| M1 | 1.0 | 无 | 300 | 300 ✔ | 300 ✔ |
| M2 | 0.5 | 无 | **150** | **300 ✘** | 150 ✔ |
| M3 | 1.0 | 0.5 | 150 | 152 ✔ | 152 ✔ |
| M4 | 0.5 | 0.5 | **150** | 152 ✔ | 152 ✔ |

关键两点：

1. **`\W` 是覆盖，不是叠乘** —— M4 样式 0.5 再写 `\W0.5`，ODA 给 150（=0.5），不是 75（=0.25）。
2. **"没写 `\W`" ≠ 1.0** —— MTEXT 在 DXF 里根本没有实体级字宽字段，不写就是样式值说了算。

因此 `MTextFormat.Parse` 要带出 `WidthExplicit`（首个可见字符所在作用域里有没有出现过 `\W`），
`AddMText` 里 `widthFactor = WidthExplicit ? \W : StyleWidthOf(mt)`，断行 `TextWrap.Wrap` 用同一个值。

**实测影响**（`9---陶板节点 (05).dwg`，样式 `SGT-S` 宽度 **0.300**，全部 DIMENSION 的 MTEXT 都没写 `\W`）：
标注文字「30 / 100 / DIM」被画宽 **3.3 倍**，右侧竖排那两个字面 "DIM"（`*D58`/`*D59`）比 ODA 长出 43px。
修前 63px → 修后 20px，与 ODA 的 19px 对齐。

> 这个错很"安静"：样式宽度 = 1.0 的图完全看不出来，只有用了 0.3/0.7/0.8 这类压缩样式的图纸才暴露，
> 而且表现为"文字位置偏了半格"而不是"字变宽了"，很容易误判成对齐锚点问题。

### 17. XCLIP：正向矩阵读不出来时，反向矩阵是好的（2026-11-14 修复）

`SPATIAL_FILTER` 里存了一对矩阵，ACadSharp 3.4.9 **只解对了反向那个**，`InsertTransform` 一律
读成单位阵。原来的代码见"矩阵是单位阵"就当边界点已经是世界坐标 —— 于是边界落在离图形几万单位的
地方、和实体完全不相交，只能落到 `DrawingView_CLIP_UNRESOLVED` 的兜底（默认 `frame` = 整块照画）。

**修法**：正向是单位阵、反向有平移时，用反向矩阵把边界点换算回块坐标，再过实体插入矩阵：

```
世界矩形 = 插入矩阵 × (边界点 + 反向矩阵平移)
```

两个坑：

1. **平移分量在 `M03/M13`，不在 `M30/M31`**。ACadSharp 对这类旧记录是"先转置再填"，用
   `Matrix4 * XYZ`（行优先约定）取不到平移，结果边界点原样未动、白换算一遍。必须自己按
   **列向量**约定乘：`x' = M00·x + M01·y + M03`、`y' = M10·x + M11·y + M13`。
   **线性部分也要一起用** —— `竖剖节点陶板.dwg` 的 `H=36849` 反向矩阵是 `M11 = -1` 的镜像，
   只取平移会把矩形算到错的一侧（实测该图覆盖率 96.5%→95.2%）。
2. **"反向矩阵不可用"不是判据**。`9---陶板节点 (0).dwg` 里有用反向矩阵换算后落到
   `(121756,…)`、`(174917,…)` 这种离谱位置的过滤器 —— 它们与实体不相交，自然退回兜底，
   不会误裁。所以不需要额外阈值。

顺带一条：有些图纸的 `反向矩阵平移 ≈ -插入点`（两者互相抵消），此时换算结果等于原始点，
即"边界点本来就是世界坐标"。这时新旧行为一致，不会因为这条改动而变。

实测（1400×1980 / 1600×2262，与 ODA `OdaShot` 逐像素 3px 容差）：

| 图纸 | 指标 | 修前 | 修后 |
|---|---|---|---|
| `9---陶板节点 (05).dwg` H=4301 | 图框左上多余折角 | 802 px | **420 px**（与整块丢弃一致） |
| | 仅我方多画 | 5,860 px | **5,238 px** |
| | 我方缺失 | 11,608 px | 11,613 px（不丢内容） |
| `9---陶板节点 (0).dwg` 23 个过滤器 | ODA 墨迹被覆盖 | 95.3% | **97.7%** |
| | 我方命中 | 92.6% | **98.5%** |
| | 仅我方多画 | 13,547 px | **2,627 px** |

对比一下"整块丢弃"（`--drop-clip-handle 4301`）：折角也消失，但缺失从 11,608 涨到 **13,732** ——
那 2,000 px 是被 AutoCAD 照画的合法内容。所以正确做法是**裁**而不是**丢**。

`52陶板节点` 全目录 16 份图纸（1400×1980，ODA 为基准，口径「ODA 墨迹覆盖率 / 我方命中率」）：

| 图纸 | 关（旧） | 开（新） |
|---|---|---|
| `9---陶板节点 (0).dwg` | 96.7 / 94.7 | **97.8 / 99.0** |
| `9---陶板节点 (1).dwg` | 99.7 / 91.9 | **99.7 / 96.5** |
| `9---陶板节点 (05).dwg` | 94.7 / 97.0 | 94.7 / **97.4** |
| `竖剖节点陶板.dwg` | 96.5 / 99.1 | 95.2 / **100.0** ← 唯一退步，见下 |
| 其余 12 份 | — | 完全一致 |
| **平均** | 83.10 / 86.77 | 83.09 / **87.41** |

唯一退步的 `竖剖节点陶板.dwg`（覆盖率 −1.3、命中率 +0.9）：`H=16605` 的换算矩形
`(-190245.1,320795.0)-(-188960.1,322282.9)` 把块底部 y&lt;320795 的一小段切掉了，而 ODA 画全。
也就是说这张图的这条 XCLIP 在 AutoCAD 里**没生效**（可能是 XCLIP 关掉了 —— 这个开关
ACadSharp 读不出来，见 P2-22）。属于"两个都是小量、方向相反"的取舍，已记录，暂不为它加特例。

`DrawingView_CLIP_INV_XFORM=0` 可关掉做对照；`DrawingView_CLIP_DEBUG=1` 会打印每个过滤器的
换算结果（`[clip] H=… inv=… rect=…`），诊断"这块为什么被裁/没裁"用它。

`(05)` 的 `H=4301` 从此不再需要 `DrawingView_CLIP_DROP_HANDLES`。

### 18. MTEXT 断行：折行的字宽必须等于画字的字宽（2026-11-14 修复）

`AddMText` 里断行和画字是两条独立的度量路径，字宽因子必须**同一个值**：

```
widthFactor = \W（显式）或 STYLE 的组码 41 ─┐
      └ 若把 \T 折进宽度（默认，见下）→ widthFactor ×= \T
断行  TextWrap.Wrap(text, height, rectWidth, widthFactor, font)   ← 传 widthFactor
画字  TextDraw.WidthFactor = widthFactor
```

修之前断行传的是 `wfWidth`（只含 `\W`），而画字用的是 `wfWidth × \T`。`52陶板节点/9---陶板节点 (05).dwg`
图签"备注"块 `\H0.76x;\W0.8;\T0.8;`：画字按 0.64、断行按 0.80，断行以为每行更宽 →
**折成 19 行，ODA 只有 10 行**，整块比参照多出 9 列、高度铺满图签格。改成同一个值后 19→16 行，
ODA 覆盖率 92.8%→93.7%、我方漏画 11,613→10,162（回归 PASS=397 FAIL=0）。

**顺带证伪一条旧建议**：不要为了"图签文字偏小"去做**逐 run `\H`**。受控基准
`.compare/mt3.dxf`（`{\H0.76x;中文AB}{\H1.053x;中文AB}` 与 `\H0.76x;…\P\H1.053x;…` 两组）
在 ODA 上实测：**两段等高，只认首个 `\H`**；`\W`（`.compare/mt3.dxf` 第三组 0.8/1.2）才是逐 run。
`MTextFormat` 现在"整条取首个可见字符所在作用域"的口径是对的，别再改。

**另一条已知差异（不要当 bug 修）**：受控基准 `.compare/wrap4.dxf`（29 个汉字 / 字高 40 /
组码 41 栏宽 400）在 ODA 上是**一行不折**（单位宽 2,272），我们是折成 6 行。也就是说
**ODA/Teigha 的 MTEXT 不做自动折行**。我们保留折行是跟随 AutoCAD 语义（`RectangleWidth` 就是
排版栏宽），代价是图签说明块的列数会比 ODA 多。判断"折行有没有问题"只能看
① 同一实体的行数是否与 `RectangleWidth` 自洽 ② 整图指标有没有变差，不能拿 ODA 的行数当标准。

### 19. 像素对拍要关抗锯齿（`--no-aa`，2026-11-14）

ODA 出图几乎不做抗锯齿：同一张 `(05)` 的 161,529 个非零像素里 **99.8% 亮度 >40**；我们开 AA
后 230,480 个非零像素里有 **65,261 落在 7~40**（每条细线两侧的灰边）。3px 容差 + 阈值 40 的
口径下，这批灰边会被判成「漏画」—— 实测关掉 AA 后 ODA 覆盖率 93.7%→**95.4%**、漏画
10,162→**7,492**，多画几乎不动（5,402→5,102）。

```powershell
dotnet run --project tools\DwgCapture -c Release -- --in a.dwg --out a.png --no-aa   # 或 DrawingView_NO_AA=1
```

`DrawingView.Antialias` 默认 `true`，**产品里不要关**：关闭后批次路径从 43.9ms 恶化到 179.2ms
（Skia 走不到细线快速路径）。它只服务"与 ODA 逐像素对拍"这一个用途。

### 20. 块内 XCLIP：能读也能裁，卡住的是我们自己的两处代码（2026-11-14 修复）

`(05)`/`(0)`/`(1)` 三份图共用同一个图框模板：`TK-A3-NT` 块里有个 `qwe` 块插入
（handle 随图纸变：1498 / 1749），带 SpatialFilter，边界两点恒为
**(432.741,123.331)-(478.741,150.029)**，`InsertTransform` 是单位阵、`InverseInsertTransform`
是 **缩放 304.2042 + 平移 (-133156.85,-27416.02)**。图签里那张缩略平面图（D1/D3/D4 标号框、
树木、场地线）在 ODA 里被切掉大半、我们整块照画 —— 这是该区多画 24.5k px 的唯一来源。

**上一版结论"坐标读不出来、别硬裁"是错的**。数据一直在（ACadSharp 读得出 `BoundaryPoints` /
`InverseInsertTransform`），换算链也是闭合的：

```
world = parent(父链) × selfInsert(自身插入) × inverse(boundaryPoints)
```

代进去：边界过 `inv` 落回 qwe 块坐标 (-1515,10102)-(12478,18223)（与块内容 754..13748 同量级，
`inv` 的 304.2042 正好是 qwe 插入 scale 0.00329 的倒数），再乘 qwe 插入得父块坐标
(367.6,116.3)-(413.6,143.0)，最后乘 TK 的插入（-90° + scale 2）得世界
**(645.2,335.0)-(698.6,427.0)**。实测 ODA 在 y1900~2140 这一带的保留边界正好是像素 650
（= 世界 645.2），逐位吻合。

**真正挡住修复的是两处代码**：

1. `ConvertEntity` 里的 `parent.IsIdentity` 门把块内裁剪整个跳过。放开后仍然不生效 —— 因为：
2. **判断"实体是否在窗口内"时，`Entity.GetBoundingBox()` 给的是块定义/父容器坐标，却被拿去
   和世界窗口比**。`Insert.GetBoundingBox()` 尤其坑：它**已经含自己的插入变换**（实测
   qwe H=1498 → (375.1,54.9)-(417.8,139.7)，就是父块坐标），所以只需再乘 `parent`，
   **不要再乘一次自身插入矩阵**（会缩成 0.003 倍 → 与窗口永不相交 → 整块被当"裁掉"丢弃）。
   症状很好认：`--report` 的 `entities` 从 1784 掉到 1092、`spatialClipped` 从 0 变 1。

还有一个只影响调试的坑：**`SKMatrix.MapPoints(SKPoint[])` 返回新数组，不原地改**。
写成 `m.MapPoints(pts);` 编译不报错、图上一个像素都不变（实测改了 4 轮都"没生效"）。

实测（1600×2262、固定 `--rect "368.13,259.21,1051.23,1225.21"` = EXTMIN/EXTMAX × 1.15，
与 ODA 的 `ZoomExtents(1.15)` 同尺度，墨迹 bbox (104,147)-(1495,2114) 对 (105,148)-(1495,2114)）：

| `(05).dwg` | ODA 覆盖 | 我方准确 | 漏画 | 多画 |
|---|---|---|---|---|
| 块内不裁（`CLIP_IN_BLOCK=0`） | 94.4% | 96.2% | 9,052 | 6,668 |
| 块内裁（默认） | 94.0% | **97.9%** | 9,620 | **3,536** |

图签缩略平面图局部（x560-820,y1900-2140）：覆盖 98.3%→93.7%、准确 84.7%→**99.5%**、
多画 **2,153→46**。

16 份 `52陶板节点` 批量（1400×1980、3px、bbox 对齐）：只有 `02`/`07` 两份含块内 XCLIP，
多画 1,613→651、4,191→1,585，漏画 2,633→2,870、5,921→6,385；其余 14 份逐像素不变。
平均多画 3,206→2,983、漏画 3,044→3,088、准确 87.4%→87.5%。

**窗口边界别再外扩**：做过 `DrawingView_CLIP_PAD` 扫描（1→1.1→1.2→1.4→1.6→2.0→3.0），
覆盖率 94.0%→94.3% 只涨 0.3，多画 3,129→7,358 翻倍 —— 说明剩下的漏画不是"窗口窄"造成的
（是字体/非 qwe 实体），外扩纯属拟合。

`DrawingView_CLIP_IN_BLOCK=0` 可回到"块内一律不裁"的旧行为做 A/B。

**仍不确定的一条**：ODA 的保留边界比我们的窗口左沿再往左约 20 世界单位（世界 624 vs 645），
但那一段的差在指标上表现为"多画 46 / 漏画几百"，且外扩实验证明补回来不划算 ——
所以先按当前窗口定案，不再用反推去凑。

### 21. MTEXT 行距与内联 `\H`：基准测出的口径，但(05)的备注块反着来（2026-11-15，未改代码）

**夹具**：`.compare/genmt`（ACadSharp `DxfWriter` 写 AC1015）+ `.compare/fixture_mt_lh2.dxf`。
尺度靠 L 形四条直线标定（墨迹 bbox 即 (0,0)-(3000,1800)，x/y 两个方向都能反解 px 每世界单位）。
汉字版 `fixture_mt_cjk.dxf` 是把 ACadSharp 写出的 `MARK1\MARK2` 占位替成 **GBK 字节**再改
`$DWGCODEPAGE` 为 `ANSI_936` —— ODA 与我方都能正确显示（R12 的 `.dxf` 里 ODA 不画 MTEXT，
所以夹具必须是 AC1015）。

实测（h=40，`txt.shx`，单位=世界）：

| 组 | 内容 | ODA 行距 | ODA 字高 | 我方行距 | 我方字高 |
|---|---|---|---|---|---|
| A | 无 `\H` | 66.6 | 41.9 | 66.6 | 44 |
| B | `{\H0.5x;…}` | **66.6** | 22.2 | **33.3** | 23 |
| C | 实体 h=20 | 33.3 | 22.2 | 33.3 | 22 |
| D | 无 `\H`、LS(44)=0.96 | 64.0 | 42 | 63.4 | 43 |
| E | 汉字 `{\H0.76x;田田…}` | **66.6** | 34.5 | **49.9** | 35.7 |

两条：①**行距 = 1.667 × 实体字高 × LS**，我们的常数没错（D 组 0.96 倍精确对上）；
②**ODA 的 `\H` 只缩字、不缩行距**，我们却按 `\H` 后的字高算行距（B、E 两行数）。

**但拿②去改 `AddMText`，(05) 反而变差**：94.7 / 98.8 / 漏 8,611 / 多 2,089 →
94.5 / 98.4 / 漏 8,904 / 多 2,751，已回滚。用自相关测图签备注格的真实行距：ODA **14 px**、
改前我方 13 px、改后 16 px —— 说明这一格 ODA 的行为既不是"缩"也不是"不缩"。

**这一格的数更像"ODA 用了更大的字高、再按它缩行距"**：ODA 字面 9 px（=1.9 块单位）、
行距 14.2 px（=1.6×1.9），而 1.9 = **2.5 × 0.76**，实体组码 40 却是 2.2。同图**不带 `\H`** 的
图签 MTEXT（`设计/Designed`，h=2）两边墨迹框完全一致（36×25 px），所以偏差只出在带 `\H`
的这一个实体上，不是全局字号问题。

**待查**：该 MTEXT 的"定义字高"（组码 43，ACadSharp 3.7.1 没暴露这个属性）是不是 2.5。
在拿到真值之前**别动 `AddMText` 的行距**——现有写法在该图上更接近 ODA。

**顺带纠正一条**：`\W0.5/0.6` 的标注没有"汉字不吃 `\W`"的问题。实测两边汉字步进都是
0.88~0.9 em（h=6 时 5.3 世界单位/字），标注行尾的差异来自别的（笔画粗细 303 vs 174 px 墨量、
以及衬线/字面差异），不是 `\W`。


## 已知踩坑（重要）

### ACadSharp 3.4.9 API（与旧版/文档差异大）
- 点类型：`CSMath.XYZ`（不在 ACadSharp 根命名空间）
- `Text` 实体类改名为 `TextEntity`；**`MText` 与 `TextEntity` 是兄弟类型**（都直接继承 Entity）——
  `is TextEntity` 匹配不到 MText（2026-09-25 SmokeTest 实证：根层过滤 29 实体只少 2 不少 3）。
  旧文档写的「MText 继承自 TextEntity」是错的，switch-case 顺序也无实际约束。
- `Arc` **继承自** `Circle`（Circle 的 case 必须放在 Arc 后面）
- `Color` 是**结构体**（非引用类型），`IsByLayer`/`IsByBlock`/`Index` 直接访问
- `CadDocument` 不是 IDisposable；`DwgReader.Read()` 不接收 CancellationToken
- **`Arc.Sweep` 返回 `startAngle - endAngle`（正常逆时针弧是负值）**，直接拿它当扫角会把
  每段弧画成整圆；`StartAngle/EndAngle` 本身已经是**弧度**。见「显示保真」第 1 节
- `Hatch.IsSolid` 才是实心填充；`Hatch.BoundaryPath.Arc` 的 `StartAngle/EndAngle` 带
  `DxfReferenceType.IsAngle` 标注（读取时已角度→弧度），代码里再 `×180/π` 换回角度是**对的**
- `Insert` 缩放属性是 `XScale/YScale/ZScale`；阵列参数是 `RowCount/ColumnCount/RowSpacing/ColumnSpacing`
  （**行沿 +Y、列沿 +X**），而 `Insert.IsMultiple` 是**只读**派生属性，赋值直接 CS0200
- `Entity.IsInvisible`（DXF 组码 60）；图层可见性看 `Layer.IsOn` 与 `Layer.Flags.HasFlag(LayerFlags.Frozen)`
- 图层颜色查 `Layer.Color.Index`，但**颜色表里可能没有该图层**（要兜底默认色）
- `BlockRecord` 在 `Tables` 命名空间
- 写 DXF 用 `new DxfWriter(filename, doc, binary)`（`tools/SampleGen` 就是这么造样例的）；
  **3.4.9 也能写 DWG**（`new DwgWriter(path, doc).Write()`），实测 AC1032/AC1027/…/AC1015 都能落盘
- **`Insert.Block` / `Dimension.Block` 是只读属性**（无公开 setter，`IsMultiple` 同理）。
  跨文档克隆实体后要把块引用指向新文档的 BlockRecord：只能反射取非公开 setter 或后备字段
  （`tools/BlockExport` 的 `RefSetter.TrySet` 就是干这个的）；构造期指定块只有 `new Insert(BlockRecord)` 这条路
- `CadObject.Clone()` 可用于 Entity / BlockRecord / 表项（Layer、LineType、TextStyle…），
  克隆体加进新文档后句柄由新文档重新分配；属性实体（`Insert.Attributes`）要单独克隆再挂回去
- `BlockRecord.GetBoundingBox()` 返回的是**值类型** `BoundingBox`，判空前必须装箱
- 统计「被引用的块」时**只跟 `Insert.Block`，不要跟 `Dimension.Block`**：尺寸标注实体每个都引用一个
  `*D` 匿名块，实测 5330 个标注会带出 5330 个块（`tools/BlockExport` 的 `--referenced` 只走 INSERT）
- 不确定 API 时用 `tools/Probe`（反射枚举）验证，不要猜

### Avalonia 12.1（2026-09-16 由 11.3 升级，实升踩坑）
- **包版本必须成套对齐**：`Avalonia` / `Avalonia.Desktop` / `Avalonia.Themes.Fluent` 统一 `12.1.2`；
  SkiaSharp 锁 **3.119.4**（与 `Avalonia.Skia` 12.1.2 的依赖声明一致）。**不要升到 SkiaSharp 4.x**，会撞原生库。
- **`OpenFileDialog` / `SaveFileDialog` / `FolderDialog` / `FileDialogFilter` 在 12 中已彻底移除**
  （11 时代即为 Windows 专用 API）。文件选择统一改走跨平台的
  `TopLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { ... })` + `FilePickerFileType`，
  取本地路径用 `IStorageFile.TryGetLocalPath()`（可能返回 null，要显式判空）。
- **Avalonia 12 不再支持 .NET Framework / .NET Standard**，最低 .NET 8。本项目 `net8.0` 仍受支持（nuspec 有 net8.0 目标），官方推荐 net10。
- **编译绑定默认开启**（`AvaloniaUseCompiledBindingsByDefault` 默认 true）。本项目 XAML 无绑定，暂无影响；将来写 `{Binding}` 需配 `x:DataType`。
- **文本整形器与渲染器解耦**：只有显式 `UseSkia()` 时才需补 `.UseHarfBuzz()`。本项目用 `UsePlatformDetect()`，无需改动。
- `Avalonia.Diagnostics` 已移除，改用 `AvaloniaUI.DiagnosticsSupport` + `AttachDeveloperTools()`（本项目未用，无影响）。
- `Window.SystemDecorations` → `WindowDecorations`；`TopLevel` 不再保证是视觉树根，用 `TopLevel.GetTopLevel(visual)`。
- `IDataObject` 已移除，剪贴板/拖放改走 `IAsyncDataTransfer` / `DataTransfer`。
- ✅ **自绘渲染链在 12 中零改动**：`ICustomDrawOperation`、`ImmediateDrawingContext.TryGetFeature`、
  `ISkiaSharpApiLeaseFeature.Lease()` 全部保持兼容——升级只改了 3 处文件选择器相关代码。

### Avalonia 通用踩坑（11 / 12 均适用）
- **渲染过程中严禁使 Visual 失效**（本项目 2026-09-16 实际踩中）：在 `Render(DrawingContext)` 内部
  调用 `InvalidateVisual()` 会抛
  `InvalidOperationException: Visual was invalidated during the render pass`
  （由 `CompositingRenderer.AddDirty` 抛出）。
  → 凡是"渲染时才需要更新状态"的逻辑（如首次显示时按布局尺寸做适应窗口），必须把
  **改状态** 与 **请求重绘** 拆成两个方法：`UpdateFit()`（只改视口）供 `Render` 调用，
  `FitToContent()`（改视口 + `InvalidateVisual`）供按钮/双击等管线外调用。
- **`context.Custom(...)` 里传出去的引用必须是不可变快照**：绘制操作在渲染线程执行，
  而 UI 线程随时会改那些集合（如 `HiddenLayers`），共享可变引用等于无锁并发读写。
- **`SkCanvas.Clear()` 无视裁剪区**：先 `ClipRect` 再 `Clear`，顺序颠倒会刷到控件外部。
- **`SKPaint` / `SKFont` 是 IDisposable**，`using` 包住；漏掉会造成原生内存泄漏。
- `ImmediateDrawingContext.TryGetFeature` 是**非泛型**的（`TryGetFeature(typeof(ISkiaSharpApiLeaseFeature))`）
- `OnPointerMoved` 重写签名用基类 `PointerEventArgs`（不是 PointerMovedEventArgs）
- 没有 `OnDoubleTapped` 虚方法，需在构造函数订阅 `DoubleTapped` 事件
- `dotnet new sln`（SDK 10）默认生成 `.slnx` 新格式

### 环境注意
- **部分会话 Bash 工具完全不可用**：PortableGit 的 `dirname`/`ls`/`cat` 全部报 `command not found`（PATH 未注入），
  `cd <路径> && ...` 也会失败。此时**一律改用 PowerShell**，并把输出重定向到 `.tmp` 日志文件再用 Read 读取：
  ```powershell
  $log = "D:\...\.build.tmp.log"
  (dotnet build AvaDrawingView.slnx -c Debug --nologo) 2>&1 | Out-File -Encoding utf8 $log
  ```
- 部分会话中 PowerShell **输出捕获异常**（stdout 为空）：同样重定向到日志文件再 Read
- **`dotnet run` 跑控制台程序时 stdout 经常整段丢失**（本会话实测：只拿到第一行就没了）。
  可靠做法是**让被测程序自己写 UTF-8 日志文件**，再 Read 那个文件——`tools/UiSmokeTest`、
  `tools/SmokeTest`（`DrawingView_UISMOKE_LOG` / `DrawingView_SMOKE_LOG`）与 `tools/PerfProbe`
  都是这么做的，`tools/Probe` 用 `PROBE_LOG`。
- **PowerShell 重定向原生命令 stdout 会按 ANSI 码页解码**，dotnet 输出的 UTF-8 中文变成
  双重编码乱码（`实` → `瀹炰綋`）；`[Console]::OutputEncoding = UTF8` 在 PS 5.1 非交互重定向下
  **不生效**（不是设错了，是设了没用）。所以：`dotnet build` 之类纯 ASCII 输出可以重定向，
  **含中文的测试输出必须走程序自写日志文件**，再用 `Get-Content -Encoding utf8` 读。
  PowerShell 5.1 里 `& exe > file` 写出的是 **UTF-16LE**，Read 会当成二进制文件，别用错解码方式。
- **`.ps1` 脚本文件里绝对不要写中文路径**：PowerShell 5.1 按 ANSI 读脚本，`上海八局` 会被
  mojibake 成 `涓婃捣鍏眬`，然后报"未能找到路径 XXX 的一部分"。脚本里用 `$PSScriptRoot`
  （`tools/regress.ps1` 就是这么定位仓库根的），确实需要中文路径时改用**内联命令**
  （工具层传参编码是正确的），或用 `[char]0x6D4B` 拼。脚本注释里的中文不影响执行，但别写路径。
- **构建失败 + `--no-build` = 悄悄跑到旧二进制**（最阴的坑）：本次实际踩中——SmokeTest 编译
  失败，回归脚本仍 `--no-build` 跑通了**上一版**SmokeTest，得到"绿"的假结果。
  回归脚本必须在 Debug 构建失败时**立即退出**，且断言输出里要能看到程序自己的新日志文件确实生成。
- **`Remove-Item` 可能被沙箱的 safe-delete 包装拦截**（报 `SAFE_DELETE_FAIL_CLOSED` / `trash-failed`）。
  遇到时改用 .NET API：`[System.IO.File]::Delete($path)`
- 对**同一文件的多次 Edit 必须串行执行**，并行会互相覆盖、只有最后一个生效
- 临时 `.tmp` 日志文件用完即删，不要留在仓库根目录（`.gitignore` 已忽略 `*.tmp`/`*.log`，但仍应清理）

## 后续路线

**已完成（2026-10-22，回归 PASS=372 FAIL=0 = 332 核心 + 40 UI）**：线型虚线、非实心填充图案（含孤岛）、
TEXT 组码 72/73 对齐、实体 `Normal` 的通用 OCS→WCS（任意轴算法）、线宽显示（`LineWeightMetrics`，
模型空间固定像素 / 图纸空间真实单位）、变宽多段线轮廓填充、布局空间渲染、块属性 ATTRIB、
MTEXT 断行 / 行距 / 逐行对齐、POINT 按 PDMODE/PDSIZE（点模式固定 1.5px，见下）、SPLINE 权重与闭合、
REGION 轮廓重建（`AcisSab` 解 SAB + `RegionOutline` 走 B-rep 链）、不打印图层默认隐藏、
**SHX 单线字体**（`ShxFont` 解析 + Pen 解释器，缺字库回退 TTF 并报数，见第 14 节）。
详见 `docs/渲染保真对照与整改方案-2026-10-20.md`（只剩 P2-8 大坐标：已量化未修，
1e6 处顶点误差 0.025 单位、zoom=1000 时屏幕误差 39px，样例 `samples/sample_far.dxf`）。

### 下一步的优先级（2026-10-22 用 `PerfProbe --survey` 的图集普查定，不再靠感觉）

抽样 80 张 ≥200KB 的真实图纸（206 万实体）：

| 项 | 实测 | 结论 |
|---|---|---|
| 3DSOLID | 3 个（1 张图） | **不做**。BODY/SURFACE 为 0；占比 1.5e-6，画出来还是互相穿插的线框 |
| BODY / SURFACE | 0 / 0 | 同上 |
| 代理实体 ACAD_PROXY_ENTITY | 166 个（少数图） | 低优先，真遇到再说 |
| SHX 单线字体 | 样式 **54.9%**、含 SHX 的图 **67.5%**、文本实体 **69%**（62,359/90,195） | **已做（2026-10-22，见第 14 节）** |

复现：`dotnet run --project tools/PerfProbe -- --survey "<图集目录>" --limit 80 --min-kb 200`
（CSV 落 `%TEMP%\DrawingView_survey.csv`）。**别把 `--min-kb` 去掉**：目录里有几万个
2–5 个实体的碎块（我们自己导出的块），混进去会把 SHX 占比压到 20%，结论就反了。

P1：SKPicture 静态缓存、~~SHX / 单线字体~~（**已完成 2026-10-22**：`ShxFont`+`ShxFontLibrary`，A-20-101 实测 325 条文字按字库描线）、~~SHX 大字体压缩字宽~~（**已完成 2026-10-22**：`ShxFont.AdvanceUnits` 用字身框左沿+右沿中位数 反推，gbcbig = 0.73 em，与 ODA 参考渲染的 0.727 em 差 0.9%）；
P2：拾取选择、属性面板、测量、GUI 内 PNG 导出（命令行 `tools/DwgCapture` 已提供无头 PNG）；
P3：大坐标（±1e6）float32 精度（ReadCAD 用 `EnableGpuFloatingOrigin` 解决）。

**P3 的"10 万实体 ≥45fps"已达成**：85,544 可见实体全图 58.8 FPS（绘制 6.7ms，2026-10-22 复测）。
完整方案见 WorkBuddy 工作区《Avalonia-2D-CAD查看器实施方案.md》。
