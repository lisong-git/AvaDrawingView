using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using DrawingView.Controls;
using DrawingView.Core;
using SkiaSharp;

namespace DwgCapture;

/// <summary>
/// 无头 DWG/DXF → PNG 截图工具。
///
/// 复用 DrawingView 的真实 Skia 渲染管线（Avalonia.Headless + UseSkia），
/// 不显示窗口，适合被 DSH 插件以文件交接方式调用：
///
///   DwgCapture --in drawing.dwg --out shot.png --report shot.json
///              [--width 1600] [--height 900] [--background black|white] [--no-aa]
///              [--all] [--lod 0] [--hide-layer 图层名] [--drop-clip-handle 句柄]
///
/// 背景默认黑色（与 AutoCAD 模型空间一致，方便和 OdaShot 的同 rect 图直接对比）。
///
/// 退出码：0 成功；2 参数错误；3 输入不存在；4 解析失败；5 渲染/写文件失败。
/// 报告文件始终尽力写出，调用方据此拿到结构化错误。
/// </summary>
internal static class Program
{
    private const string ToolName = "ava-dwg-capture";
    private const string ToolVersion = "1.0.0";

    private static int Main(string[] args)
    {
        var started = DateTime.UtcNow;
        var options = new CaptureOptions();
        try
        {
            options = ParseArgs(args);
        }
        catch (ArgumentException ex)
        {
            WriteReport(options.ReportPath, Failure("BAD_ARGS", ex.Message, options, started));
            Console.Error.WriteLine($"{ToolName}: {ex.Message}");
            return 2;
        }

        if (options.Help)
        {
            Console.WriteLine(Usage());
            return 0;
        }

        if (string.IsNullOrWhiteSpace(options.InputPath))
        {
            WriteReport(options.ReportPath, Failure("BAD_ARGS", "missing --in <dwg path>", options, started));
            return 2;
        }
        if (string.IsNullOrWhiteSpace(options.OutputPath))
        {
            WriteReport(options.ReportPath, Failure("BAD_ARGS", "missing --out <png path>", options, started));
            return 2;
        }

        var input = Path.GetFullPath(options.InputPath);
        if (!File.Exists(input))
        {
            WriteReport(options.ReportPath, Failure("FILE_NOT_FOUND", $"input file does not exist: {input}", options, started));
            return 3;
        }

        var output = Path.GetFullPath(options.OutputPath);
        try
        {
            var dir = Path.GetDirectoryName(output);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            WriteReport(options.ReportPath, Failure("WRITE_FAILED", $"cannot create output directory: {ex.Message}", options, started));
            return 5;
        }

        DisplayList list;
        try
        {
            AppBuilder.Configure<DrawingView.App>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .UseSkia()
                .SetupWithoutStarting();

            var view = new DrawingView.Controls.DrawingView
            {
                BackgroundColor = options.Background,
                MinEntityPixelSize = options.Lod,
                MinTextPixelSize = options.Lod <= 0 ? 0 : DrawingView.Controls.DrawingView.DefaultMinTextPixelSize,
                ShowLineWeights = options.LineWeights,
            };
            var window = new Window
            {
                Width = options.Width,
                Height = options.Height,
                Content = view,
            };
            window.Show();

            // --hide-layer 走**转换期**过滤：渲染期 SetLayerHidden 只对合批几何生效，
            // UnbatchedEntities（文字/点标记/带裁剪图元）会漏，定位"多出来的东西"时不靠谱。
            var convertOptions = string.IsNullOrWhiteSpace(options.Layout)
                                 && options.HideLayers.Count == 0
                                 && options.DropClipHandles.Count == 0
                ? null
                : new GeometryConvertOptions
                {
                    LayoutName = options.Layout,
                    HideLayers = options.HideLayers.Count == 0 ? null : options.HideLayers,
                    DropClipHandles = options.DropClipHandles.Count == 0 ? null : options.DropClipHandles,
                };
            list = CadDocumentLoader.LoadAsync(input, null, default, convertOptions).GetAwaiter().GetResult();
            view.SetDisplay(list);
            window.CaptureRenderedFrame(); // 完成布局并触达首帧
            // padding=1：--rect 出图的映射就是「世界矩形 = 画面」，一像素一世界单位，
            // 才能和 ODA 的 --rect 逐像素对拍（1.2 的留白只能靠猜比例还原）
            if (options.Rect is { } focus)
            {
                view.ZoomTo(focus, options.RectPadding);
                // Viewport.Fit 里带一个固定的 0.9「留白」系数，抵消掉才能做到
                // 「1 像素 = 1 世界单位」，与 ODA --rect 的映射严格一致
                view.ZoomBy(1f / 0.9f);
            }
            else
            {
                view.FitToContent(coreOnly: !options.AllExtents);
                // 适应窗口后再按倍数缩放：线宽这类"屏幕口径"的东西必须放大到 1px 以上才看得见
                if (Math.Abs(options.Zoom - 1f) > 1e-6f) view.ZoomBy(options.Zoom);
            }
            window.CaptureRenderedFrame();
            window.CaptureRenderedFrame();

            var frame = window.CaptureRenderedFrame();
            if (frame is null)
            {
                WriteReport(options.ReportPath, Failure("RENDER_FAILED", "CaptureRenderedFrame returned null", options, started, input, list));
                return 5;
            }

            frame.Save(output, new PngBitmapEncoderOptions());
        }
        catch (Exception ex)
        {
            WriteReport(options.ReportPath, Failure("RENDER_FAILED", $"{ex.GetType().Name}: {ex.Message}", options, started));
            return 5;
        }

        var bytes = new FileInfo(output).Length;
        WriteReport(options.ReportPath, new
        {
            ok = true,
            tool = ToolName,
            version = ToolVersion,
            input,
            output,
            width = options.Width,
            height = options.Height,
            background = options.Background == SKColors.White ? "white" : "black",
            lod = options.Lod,
            zoom = options.Zoom,
            entities = list.Entities.Count,
            layers = list.LayerNames,
            activeSpace = list.ActiveSpace,
            layouts = list.LayoutNames,
            spaceNotes = list.SpaceNotes,
            attributes = new
            {
                drawn = list.AttributesDrawn,
                invisible = list.AttributesInvisible,
                defsSkipped = list.AttributeDefsSkipped,
            },
            lineWeights = new
            {
                shown = options.LineWeights,
                entities = list.LineWeightEntities,
            },
            widePolylineEntities = list.WidePolylineEntities,
            pointMarkEntities = list.PointMarkEntities,
            regionEntities = list.RegionEntities,
            regionLoops = list.RegionLoops,
            regionUnresolved = list.RegionUnresolved,
            spatialClipped = list.SpatialClipped,
            layerFiltered = list.LayerFiltered,
            fonts = list.FontUsage,
            extents = Rect(list.Extents),
            coreExtents = Rect(list.CoreExtents),
            pngBytes = bytes,
            durationMs = (long)(DateTime.UtcNow - started).TotalMilliseconds,
            errors = Array.Empty<object>(),
        });
        Console.WriteLine($"{ToolName}: {output} ({bytes} bytes)");
        return 0;
    }

    private static object Failure(
        string code,
        string message,
        CaptureOptions options,
        DateTime started,
        string? input = null,
        DisplayList? list = null) => new
    {
        ok = false,
        tool = ToolName,
        version = ToolVersion,
        input = input ?? options.InputPath,
        output = options.OutputPath,
        width = options.Width,
        height = options.Height,
        background = options.Background == SKColors.White ? "white" : "black",
        lod = options.Lod,
        entities = list?.Entities.Count,
        layers = list?.LayerNames,
        durationMs = (long)(DateTime.UtcNow - started).TotalMilliseconds,
        errors = new[] { new { code, message } },
    };

    private static object Rect(SKRect r) => new
    {
        left = Math.Round(r.Left, 2),
        top = Math.Round(r.Top, 2),
        right = Math.Round(r.Right, 2),
        bottom = Math.Round(r.Bottom, 2),
        width = Math.Round(r.Width, 2),
        height = Math.Round(r.Height, 2),
    };

    private static void WriteReport(string? path, object value)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var full = Path.GetFullPath(path);
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var temp = full + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
            }));
            if (File.Exists(full)) File.Delete(full);
            File.Move(temp, full);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{ToolName}: cannot write report {path}: {ex.Message}");
        }
    }

    private static string Usage() =>
        """
        DwgCapture --in <dwg path> --out <png path> [--report <json path>]
                   [--width 1600] [--height 900] [--background black|white]
                   [--all] [--lod 0] [--hide-layer 图层名] [--drop-clip-handle 句柄] [--layout <布局名>] [--lineweights] [--zoom 1]
                     [--rect left,top,right,bottom] [--rect-pad 1.0] [--no-aa]

        Exit codes: 0 success, 2 bad arguments, 3 input missing, 4 parse failure,
        5 render/write failure. The report (if requested) is written for failures too.
        """;

    private static CaptureOptions ParseArgs(string[] args)
    {
        var options = new CaptureOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            string Next() => ++i < args.Length ? args[i] : throw new ArgumentException($"missing value after {key}");
            switch (key)
            {
                case "--in": options.InputPath = Next(); break;
                case "--out": options.OutputPath = Next(); break;
                case "--report": options.ReportPath = Next(); break;
                case "--width":
                    if (!int.TryParse(Next(), out var width) || width < 64 || width > 16384)
                        throw new ArgumentException("--width must be an integer in 64..16384");
                    options.Width = width;
                    break;
                case "--height":
                    if (!int.TryParse(Next(), out var height) || height < 64 || height > 16384)
                        throw new ArgumentException("--height must be an integer in 64..16384");
                    options.Height = height;
                    break;
                case "--background":
                    var background = Next().ToLowerInvariant();
                    options.Background = background switch
                    {
                        "white" => SKColors.White,
                        "black" => SKColors.Black,
                        _ => throw new ArgumentException("--background must be white or black"),
                    };
                    break;
                case "--all": options.AllExtents = true; break;
                case "--lineweights": options.LineWeights = true; break;
                case "--zoom":
                    if (!float.TryParse(Next(), out var zoom) || zoom <= 0 || zoom > 10000)
                        throw new ArgumentException("--zoom must be a number in (0, 10000]");
                    options.Zoom = zoom;
                    break;
                case "--layout": options.Layout = Next(); break;
                case "--rect":
                {
                    // 世界矩形 l,t,r,b —— 定点放大对照用（ODA 只能出整图，本工具可以只出某一块）
                    var parts = Next().Split(',');
                    if (parts.Length != 4
                        || !float.TryParse(parts[0], out var l) || !float.TryParse(parts[1], out var t)
                        || !float.TryParse(parts[2], out var r2) || !float.TryParse(parts[3], out var b2))
                        throw new ArgumentException("--rect needs 'left,top,right,bottom'");
                    options.Rect = SKRect.Create(l, t, MathF.Abs(r2 - l), MathF.Abs(b2 - t));
                    break;
                }
                case "--rect-pad":
                    if (!float.TryParse(Next(), out var pad) || pad < 1f || pad > 10f)
                        throw new ArgumentException("--rect-pad must be a number in [1, 10]");
                    options.RectPadding = pad;
                    break;
                case "--hide-layer":
                    options.HideLayers.Add(Next());
                    break;
                case "--no-aa":
                    // 像素对拍专用：ODA 出图几乎不做抗锯齿，我们开 AA 后细线四周带灰边，
                    // 阈值一卡就把"画了但更淡"判成漏画。产品里别关（批次路径会慢 4 倍）。
                    DrawingView.Controls.DrawingView.Antialias = false;
                    break;
                case "--drop-clip-handle":
                    // 十进制句柄（与 --report / 诊断输出里的 H= 一致，非十六进制）
                    if (!ulong.TryParse(Next(), out var dropHandle)) throw new ArgumentException("--drop-clip-handle 需要十进制句柄");
                    options.DropClipHandles.Add(dropHandle);
                    break;
                case "--lod":
                    if (!float.TryParse(Next(), out var lod) || lod < 0 || lod > 10000)
                        throw new ArgumentException("--lod must be a number in 0..10000");
                    options.Lod = lod;
                    break;
                case "--help":
                case "-h": options.Help = true; break;
                default: throw new ArgumentException($"unknown argument '{key}'");
            }
        }
        return options;
    }

    private sealed class CaptureOptions
    {
        public string? InputPath;
        public string? OutputPath;
        public string? ReportPath;
        public int Width = 1600;
        public int Height = 900;
        /// <summary>
        /// 默认黑底：与 AutoCAD 模型空间 / ODA 参考渲染一致，出图可直接对拍。
        /// 需要白底（打印稿观感、旧基线复现）时显式传 <c>--background white</c>。
        /// </summary>
        public SKColor Background = SKColors.Black;
        public SKRect? Rect;
        public float RectPadding = 1f;
        public bool AllExtents;
        public float Lod;
        public string? Layout;
        public bool LineWeights;
        public float Zoom = 1f;
        /// <summary>诊断用：按图层名隐藏（可重复）。定位"哪个图层画出了多余内容"时逐层排除。</summary>
        public List<string> HideLayers = new();
        /// <summary>这些句柄的空间过滤器边界不可信时整块丢弃（AutoCAD 实测确认过时才加）。</summary>
        public List<ulong> DropClipHandles = new();
        public bool Help;
    }
}
