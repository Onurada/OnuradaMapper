using System.ComponentModel;
using System.Windows;
using OnuradaMapper.Services;
using OnuradaMapper.ViewModels;

namespace OnuradaMapper;

public partial class MainWindow : Window
{
    readonly MainViewModel _vm = new();
    public string Version { get; } = "v" + typeof(MainWindow).Assembly.GetName().Version?.ToString(3);

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        Loaded += async (_, _) => await _vm.StartAsync();
    }

    /// <summary>--shots DIR: renders every page to PNG and quits (for checking the UI without clicking around).</summary>
    public async Task CaptureAsync(string dir)
    {
        System.IO.Directory.CreateDirectory(dir);
        await Task.Delay(2500);
        foreach (var n in _vm.Nav)
        {
            _vm.Selected = n;
            await Task.Delay(2500);
            Shot(System.IO.Path.Combine(dir, n.Page.GetType().Name.Replace("ViewModel", "") + ".png"));
        }
        Application.Current.Shutdown();
    }

    void Shot(string path) => Shot(this, path);

    public static void Shot(Window win, string path)
    {
        var root = (System.Windows.Media.Visual)win.Content;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(win);
        var w = (int)(win.ActualWidth * dpi.DpiScaleX); var h = (int)(win.ActualHeight * dpi.DpiScaleY);
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
        var bg = new System.Windows.Media.DrawingVisual();
        using (var dc = bg.RenderOpen()) dc.DrawRectangle((System.Windows.Media.Brush?)win.TryFindResource("ApplicationBackgroundBrush") ?? win.Background ?? System.Windows.Media.Brushes.White, null, new Rect(0, 0, win.ActualWidth, win.ActualHeight));
        bmp.Render(bg);
        bmp.Render(root);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var f = System.IO.File.Create(path);
        enc.Save(f);
    }

    static string? DropText(DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0) return files[0];
        var t = e.Data.GetData(DataFormats.UnicodeText) as string ?? e.Data.GetData(DataFormats.Text) as string;
        return t != null && t.Trim().StartsWith("http") ? t.Trim().Split('\n')[0] : null;
    }

    void OnDragOver(object sender, DragEventArgs e)
    {
        var ok = DropText(e) != null;
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        DropHint.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    protected override void OnDragLeave(DragEventArgs e) { DropHint.Visibility = Visibility.Collapsed; base.OnDragLeave(e); }

    void OnDrop(object sender, DragEventArgs e)
    {
        DropHint.Visibility = Visibility.Collapsed;
        if (DropText(e) is string s) _vm.Drop(s);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (JobService.I.IsRunning &&
            MessageBox.Show(Loc.T("ConfirmQuit"), "Onurada Mapper", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            e.Cancel = true;
        else if (JobService.I.IsRunning) JobService.I.CancelCommand.Execute(null);
        base.OnClosing(e);
    }
}
