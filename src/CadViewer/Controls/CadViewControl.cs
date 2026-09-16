using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using CadViewer.Core;
using SkiaSharp;

namespace CadViewer.Controls;

/// <summary>
/// 2D CAD 视图控件。
/// 渲染：Render → context.Custom → ICustomDrawOperation → 借用 Avalonia 自带的
/// SkiaSharp GPU 上下文（ISkiaSharpApiLeaseFeature）直接绘制，零互操作层。
/// 交互：滚轮缩放（光标锚点）、拖拽平移、双击适应窗口。
/// </summary>
public class CadViewControl : Control
{
    private static readonly SKTypeface TextTypeface =
        SKTypeface.FromFamilyName("Microsoft YaHei") ?? SKTypeface.Default;

    private DisplayList? _display;
    private bool _pendingFit;
    private Point? _panStart;
    private SKPoint _panOffsetStart;

    public ViewportState Viewport { get; } = new();
    public HashSet<string> HiddenLayers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public CadViewControl()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    public void SetDisplay(DisplayList? display)
    {
        _display = display;
        HiddenLayers.Clear();
        _pendingFit = display is not null;
        InvalidateVisual();
    }

    public void FitToContent()
    {
        if (_display is null) return;
        Viewport.Fit(_display.Extents, (float)Bounds.Width, (float)Bounds.Height);
        InvalidateVisual();
    }

    public void ZoomBy(float factor)
    {
        Viewport.ZoomAt(factor, new SKPoint((float)Bounds.Width / 2, (float)Bounds.Height / 2));
        InvalidateVisual();
    }

    // ---------- 输入 ----------

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        var factor = MathF.Pow(1.2f, (float)e.Delta.Y);
        var p = e.GetCurrentPoint(this).Position;
        Viewport.ZoomAt(factor, new SKPoint((float)p.X, (float)p.Y));
        InvalidateVisual();
        e.Handled = true;
        base.OnPointerWheelChanged(e);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var pt = e.GetCurrentPoint(this);
        if (pt.Properties.IsLeftButtonPressed || pt.Properties.IsMiddleButtonPressed || pt.Properties.IsRightButtonPressed)
        {
            _panStart = pt.Position;
            _panOffsetStart = Viewport.Offset;
            e.Pointer.Capture(this);
            e.Handled = true;
        }
        base.OnPointerPressed(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_panStart is { } start)
        {
            var p = e.GetCurrentPoint(this).Position;
            Viewport.Offset = new SKPoint(
                _panOffsetStart.X + (float)(p.X - start.X),
                _panOffsetStart.Y + (float)(p.Y - start.Y));
            InvalidateVisual();
            e.Handled = true;
        }
        base.OnPointerMoved(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        _panStart = null;
        e.Pointer.Capture(null);
        base.OnPointerReleased(e);
    }

    private void OnDoubleTappedHandler(object? sender, TappedEventArgs e)
    {
        FitToContent();
        e.Handled = true;
    }

    // ---------- 渲染 ----------

    public override void Render(DrawingContext context)
    {
        if (_pendingFit && Bounds.Width > 1 && Bounds.Height > 1)
        {
            _pendingFit = false;
            FitToContent();
        }

        if (_display is null)
        {
            var hint = new FormattedText(
                "打开 DWG / DXF 文件开始查看\n滚轮缩放 · 拖拽平移 · 双击适应窗口",
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, Typeface.Default, 14, Brushes.Gray);
            context.DrawText(hint, new Point(16, 16));
            return;
        }

        context.Custom(new CadDrawOperation(
            new Rect(Bounds.Size), _display, Viewport.Zoom, Viewport.Offset, HiddenLayers));
    }

    private sealed class CadDrawOperation : ICustomDrawOperation
    {
        private readonly Rect _bounds;
        private readonly DisplayList _list;
        private readonly float _zoom;
        private readonly SKPoint _offset;
        private readonly HashSet<string> _hidden;

        public CadDrawOperation(Rect bounds, DisplayList list, float zoom, SKPoint offset, HashSet<string> hidden)
        {
            _bounds = bounds;
            _list = list;
            _zoom = zoom;
            _offset = offset;
            _hidden = hidden;
        }

        public Rect Bounds => _bounds;
        public bool HitTest(Point p) => _bounds.Contains(p);
        public bool Equals(ICustomDrawOperation? other) => false;
        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            var leaseFeature = context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) as ISkiaSharpApiLeaseFeature;
            if (leaseFeature is null) return; // 软件渲染环境：P0 暂不绘制

            using var lease = leaseFeature.Lease();
            var canvas = lease.SkCanvas;
            float w = (float)_bounds.Width, h = (float)_bounds.Height;

            canvas.Save();
            canvas.Clear(SKColors.White);
            canvas.ClipRect(SKRect.Create(0, 0, w, h));

            var worldToScreen = new SKMatrix(_zoom, 0, _offset.X, 0, -_zoom, _offset.Y, 0, 0, 1);

            // 世界空间几何
            var worldRect = ScreenRectToWorld(w, h);
            var visible = _list.Index.Query(worldRect);
            float hairline = 1f / MathF.Max(_zoom, 1e-6f);

            using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke };
            using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
            var texts = new List<TextDraw>();
            var textColors = new List<uint>();

            canvas.Save();
            canvas.Concat(worldToScreen);
            foreach (var ent in visible)
            {
                if (_hidden.Contains(ent.Layer)) continue;
                uint color = ent.ColorArgb;
                if ((color & 0x00FFFFFF) == 0x00FFFFFF) color = 0xFF000000u; // 白 → 黑（白底）

                if (ent.Path is not null)
                {
                    stroke.Color = new SKColor(color);
                    stroke.StrokeWidth = ent.LineWidth > 0 ? ent.LineWidth : hairline;
                    canvas.DrawPath(ent.Path, stroke);
                }
                if (ent.FillPath is not null)
                {
                    fill.Color = new SKColor(color);
                    canvas.DrawPath(ent.FillPath, fill);
                }
                if (ent.Text is not null)
                {
                    texts.Add(ent.Text);
                    textColors.Add(color);
                }
            }
            canvas.Restore();

            // 文本（屏幕空间绘制，清晰度不随缩放损失）
            for (int i = 0; i < texts.Count; i++)
                DrawText(canvas, texts[i], textColors[i]);

            canvas.Restore();
        }

        private SKRect ScreenRectToWorld(float w, float h)
        {
            var p0 = new SKPoint((0 - _offset.X) / _zoom, (_offset.Y - 0) / _zoom);
            var p1 = new SKPoint((w - _offset.X) / _zoom, (_offset.Y - h) / _zoom);
            return SKRect.Create(
                MathF.Min(p0.X, p1.X), MathF.Min(p0.Y, p1.Y),
                MathF.Abs(p1.X - p0.X), MathF.Abs(p1.Y - p0.Y));
        }

        private void DrawText(SKCanvas canvas, TextDraw td, uint color)
        {
            float px = td.Height * _zoom;
            if (px < 1.2f) return; // 过小降级
            float sx = td.Position.X * _zoom + _offset.X;
            float sy = -td.Position.Y * _zoom + _offset.Y;
            var paint = new SKPaint { IsAntialias = true, Color = new SKColor(color), Style = SKPaintStyle.Fill };
            using var font = new SKFont(TextTypeface, px);
            canvas.Save();
            canvas.Translate(sx, sy);
            canvas.RotateDegrees(td.RotationRadians * 180f / MathF.PI);
            canvas.DrawText(td.Text, 0, 0, font, paint);
            canvas.Restore();
        }
    }
}
