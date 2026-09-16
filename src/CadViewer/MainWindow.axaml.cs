using System.IO;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using CadViewer.Core;

namespace CadViewer;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async void OnOpenClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "打开 CAD 图纸",
            AllowMultiple = false,
            Filters =
            {
                new FileDialogFilter { Name = "CAD 图纸 (DWG/DXF)", Extensions = { "dwg", "dxf" } },
                new FileDialogFilter { Name = "所有文件", Extensions = { "*" } },
            },
        };
        var files = await dlg.ShowAsync(this);
        if (files is not { Length: > 0 }) return;

        var path = files[0];
        StatusText.Text = $"加载中… {Path.GetFileName(path)}";
        try
        {
            var progress = new Progress<string>(s => StatusText.Text = $"{Path.GetFileName(path)} — {s}");
            var list = await CadDocumentLoader.LoadAsync(path, progress);
            View.SetDisplay(list);
            BuildLayerPanel(list);
            StatusText.Text = $"{Path.GetFileName(path)} — {list.Entities.Count} 个实体，{list.LayerNames.Count} 个图层";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"加载失败: {ex.Message}";
        }
    }

    private void BuildLayerPanel(DisplayList list)
    {
        LayerList.Items.Clear();
        foreach (var name in list.LayerNames)
        {
            var cb = new CheckBox
            {
                Content = name,
                IsChecked = true,
                Margin = new Avalonia.Thickness(0, 2, 0, 2),
            };
            var layer = name;
            cb.IsCheckedChanged += (_, _) =>
            {
                if (cb.IsChecked == true) View.HiddenLayers.Remove(layer);
                else View.HiddenLayers.Add(layer);
                View.InvalidateVisual();
            };
            LayerList.Items.Add(cb);
        }
    }

    private void OnFitClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => View.FitToContent();

    private void OnZoomInClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => View.ZoomBy(1.25f);

    private void OnZoomOutClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => View.ZoomBy(0.8f);
}
