using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using OnuradaMapper.Services;

namespace OnuradaMapper.Views;

/// <summary>Notes per second over the song for every difficulty (small multiples), tinted by the light-story section.
/// Click to jump the player there.</summary>
public sealed class DensityChart : FrameworkElement
{
    public List<Difficulty> Diffs { get; set; } = [];
    public StoryModel? Story { get; set; }
    public double Pad { get; set; }
    public double Duration { get; set; }
    public AudioPlayer? Player
    {
        get => _player;
        set
        {
            if (_player != null) _player.Tick -= InvalidateVisual;
            _player = value;
            if (_player != null) _player.Tick += InvalidateVisual;
        }
    }
    AudioPlayer? _player;

    const double RowH = 44, LabelW = 86, Bin = 2.0;
    static readonly Typeface Face = new("Segoe UI");

    public DensityChart() { ClipToBounds = true; Cursor = Cursors.Hand; }

    protected override Size MeasureOverride(Size available) => new(0, Math.Max(1, Diffs.Count) * RowH + 18);

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth - LabelW;
        if (w <= 0 || Duration <= 0) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var bins = (int)Math.Ceiling(Duration / Bin);
        var all = Diffs.Select(d =>
        {
            var c = new double[bins];
            foreach (var n in d.Notes) { var i = (int)((n.T - Pad) / Bin); if (i >= 0 && i < bins) c[i] += 1 / Bin; }
            return c;
        }).ToList();
        var max = Math.Max(1, all.SelectMany(x => x).DefaultIfEmpty(1).Max());
        var dim = new SolidColorBrush(Color.FromArgb(150, 200, 200, 200)); dim.Freeze();
        var grid = new Pen(new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)), 1); grid.Freeze();
        for (int r = 0; r < all.Count; r++)
        {
            var top = r * RowH; var baseY = top + RowH - 6;
            dc.DrawText(new FormattedText(Diffs[r].Name, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 12, dim, dpi), new Point(0, top + 8));
            var avg = Diffs[r].Notes.Count / Math.Max(1, Duration);
            dc.DrawText(new FormattedText($"{avg:0.0} nps", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 10.5, dim, dpi), new Point(0, top + 24));
            dc.DrawLine(grid, new Point(LabelW, baseY), new Point(ActualWidth, baseY));
            for (int i = 0; i < bins; i++)
            {
                var v = all[r][i] / max;
                if (v <= 0) continue;
                var t = i * Bin + Bin / 2;
                var lab = Story?.Segments.FirstOrDefault(s => s.Contains(t))?.Label ?? "verse";
                var b = TimelineControl.SegBrush.GetValueOrDefault(lab, Brushes.Gray);
                var x = LabelW + i * Bin / Duration * w;
                dc.DrawRectangle(b, null, new Rect(x, baseY - v * (RowH - 12), Math.Max(1, Bin / Duration * w - 0.6), v * (RowH - 12)));
            }
        }
        // time axis + playhead
        var ty = all.Count * RowH;
        for (int s = 0; s < Duration; s += 30)
            dc.DrawText(new FormattedText($"{s / 60}:{s % 60:00}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 10, dim, dpi), new Point(LabelW + s / Duration * w, ty + 2));
        if (_player != null)
        {
            var x = LabelW + _player.Position / Duration * w;
            dc.DrawLine(new Pen(Brushes.White, 1.2), new Point(x, 0), new Point(x, ty));
        }
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        var x = e.GetPosition(this).X - LabelW;
        var w = ActualWidth - LabelW;
        if (x >= 0 && w > 0) _player?.Seek(x / w * Duration);
    }
}
