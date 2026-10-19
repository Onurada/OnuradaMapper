using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OnuradaMapper.Services;

namespace OnuradaMapper.Views;

/// <summary>Waveform + story lanes. Drag edges/markers (snapped to bars, Alt = free), right-click to add or remove,
/// click the waveform to seek, wheel = zoom, Shift+wheel or middle-drag = scroll.</summary>
public sealed class TimelineControl : FrameworkElement
{
    public StoryModel? Model { get => _model; set { _model = value; InvalidateVisual(); } }
    public float[] Peaks { get => _peaks; set { _peaks = value; InvalidateVisual(); } }
    public List<(double t, string w)> Words { get; set; } = [];
    public Analysis? Grid { get; set; }
    public AudioPlayer? Player
    {
        get => _player;
        set
        {
            if (_player != null) _player.Tick -= OnTick;
            _player = value;
            if (_player != null) _player.Tick += OnTick;
        }
    }

    StoryModel? _model;
    float[] _peaks = [];
    AudioPlayer? _player;
    double _pps = 8;       // pixels per second
    double _off;           // seconds at the left edge
    bool _fitted;

    const double Ruler = 22, LaneH = 30;
    double WaveH => Math.Max(90, ActualHeight - Ruler - 5 * LaneH - 6);
    enum Lane { Ruler, Wave, Chorus, Dream, Drops, Light, Words, None }
    static readonly string[] LaneNames = ["", "", "TlChorus", "TlDream", "TlDrops", "TlLight", "TlWords"];

    static readonly Typeface Face = new("Segoe UI");
    static readonly Typeface FaceBold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    static Brush B(string hex, byte a = 255) { var c = (Color)ColorConverter.ConvertFromString(hex); c.A = a; var b = new SolidColorBrush(c); b.Freeze(); return b; }
    static readonly Brush ChorusB = B("#FF4FA3", 200), ChorusFill = B("#FF4FA3", 40), DreamB = B("#B48CFF", 210), DreamFill = B("#B48CFF", 46),
        DropB = B("#FFD21F"), WaveB = B("#9AA7C7", 200), GridB = B("#808080", 40), TextB = B("#FFFFFF", 230), DimText = B("#C8C8C8", 170),
        PlayB = B("#FFFFFF"), LaneBg = B("#000000", 40), WordB = B("#4FC3FF", 200);
    public static readonly Dictionary<string, Brush> SegBrush = new()
    {
        ["intro"] = B("#5B6B8C"), ["verse"] = B("#3E8EDE"), ["calm"] = B("#2BB6A3"), ["build"] = B("#FFA94D"), ["break"] = B("#E5484D"),
        ["chorus"] = B("#FF4FA3"), ["hype"] = B("#FF7A00"), ["dream"] = B("#B48CFF"), ["outro"] = B("#6E6E6E"),
    };

    public TimelineControl()
    {
        ClipToBounds = true;
        Focusable = true;
        MinHeight = Ruler + 90 + 5 * LaneH + 6;
        SizeChanged += (_, _) => { if (!_fitted) Fit(); InvalidateVisual(); };
    }

    double Dur => Math.Max(1, Model?.Duration ?? (_peaks.Length / (double)Waveform.Rate));
    double X(double t) => (t - _off) * _pps;
    double T(double x) => x / _pps + _off;

    public void Fit()
    {
        if (ActualWidth <= 0) return;
        _pps = ActualWidth / (Dur + 2); _off = -1; _fitted = true; InvalidateVisual();
    }

    void OnTick()
    {
        if (_player == null) return;
        var x = X(_player.Position);
        if (_player.IsPlaying && (x < 0 || x > ActualWidth - 40)) _off = _player.Position - 40 / _pps;
        InvalidateVisual();
    }

    double LaneTop(Lane l) => l switch
    {
        Lane.Ruler => 0, Lane.Wave => Ruler, _ => Ruler + WaveH + ((int)l - 2) * LaneH,
    };

    Lane LaneAt(double y)
    {
        if (y < Ruler) return Lane.Ruler;
        if (y < Ruler + WaveH) return Lane.Wave;
        var i = (int)((y - Ruler - WaveH) / LaneH);
        return i is >= 0 and < 5 ? (Lane)(i + 2) : Lane.None;
    }

    FormattedText Text(string s, double size, Brush b, bool bold = false) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, bold ? FaceBold : Face, size, b, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth; var h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        if (Model == null) return;
        // ruler + bar grid
        var step = new[] { 1, 2, 5, 10, 15, 30, 60 }.FirstOrDefault(s => s * _pps >= 60, 60);
        for (var t = Math.Floor(Math.Max(0, _off) / step) * step; t < T(w); t += step)
        {
            var x = X(t);
            dc.DrawLine(new Pen(GridB, 1), new Point(x, Ruler - 6), new Point(x, h));
            dc.DrawText(Text($"{(int)t / 60}:{(int)t % 60:00}", 10.5, DimText), new Point(x + 3, 3));
        }
        if (Grid != null && _pps > 20)
            foreach (var b in Grid.Bars)
            {
                var x = X(b);
                if (x >= 0 && x <= w) dc.DrawLine(new Pen(GridB, 0.6), new Point(x, Ruler), new Point(x, Ruler + WaveH));
            }
        // waveform with chorus / dream tint
        var wy = Ruler; var mid = wy + WaveH / 2;
        foreach (var s in Model.Chorus) dc.DrawRectangle(ChorusFill, null, new Rect(X(s.A), wy, Math.Max(0, (s.B - s.A) * _pps), WaveH));
        foreach (var s in Model.Dream) dc.DrawRectangle(DreamFill, null, new Rect(X(s.A), wy, Math.Max(0, (s.B - s.A) * _pps), WaveH));
        if (_peaks.Length > 0)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                bool first = true;
                for (int px = 0; px < (int)w; px++)
                {
                    int i0 = (int)(T(px) * Waveform.Rate), i1 = Math.Max(i0 + 1, (int)(T(px + 1) * Waveform.Rate));
                    if (i1 <= 0 || i0 >= _peaks.Length) continue;
                    float m = 0;
                    for (int i = Math.Max(0, i0); i < Math.Min(_peaks.Length, i1); i++) m = Math.Max(m, _peaks[i]);
                    var a = m * (WaveH / 2 - 4);
                    if (first) { c.BeginFigure(new Point(px, mid - a), false, false); first = false; }
                    c.LineTo(new Point(px, mid - a), true, false);
                    c.LineTo(new Point(px, mid + a), true, false);
                }
            }
            g.Freeze();
            dc.DrawGeometry(null, new Pen(WaveB, 1), g);
        }
        // lanes
        for (int l = 2; l <= 6; l++)
        {
            var y = LaneTop((Lane)l);
            dc.DrawRectangle(LaneBg, null, new Rect(0, y + 1, w, LaneH - 2));
        }
        DrawRanges(dc, Model.Chorus, Lane.Chorus, ChorusB, Loc.T("TlChorus"));
        DrawRanges(dc, Model.Dream, Lane.Dream, DreamB, Loc.T("TlDream"));
        var dy = LaneTop(Lane.Drops);
        foreach (var d in Model.Drops)
        {
            var x = X(d);
            dc.DrawLine(new Pen(DropB, 1.2) { DashStyle = DashStyles.Dash }, new Point(x, Ruler), new Point(x, Ruler + WaveH));
            var dia = new StreamGeometry();
            using (var c = dia.Open()) { c.BeginFigure(new Point(x, dy + 4), true, true); c.LineTo(new Point(x + 9, dy + LaneH / 2), true, false); c.LineTo(new Point(x, dy + LaneH - 4), true, false); c.LineTo(new Point(x - 9, dy + LaneH / 2), true, false); }
            dc.DrawGeometry(DropB, null, dia);
        }
        var ly = LaneTop(Lane.Light);
        foreach (var s in Model.Segments)
        {
            var r = new Rect(X(s.A), ly + 3, Math.Max(0, (s.B - s.A) * _pps - 1), LaneH - 6);
            dc.DrawRoundedRectangle(SegBrush.GetValueOrDefault(s.Label, WaveB), null, r, 3, 3);
            if (r.Width > 30) { dc.PushClip(new RectangleGeometry(r)); dc.DrawText(Text(s.Label, 11, TextB, true), new Point(r.X + 5, r.Y + 4)); dc.Pop(); }
        }
        var wyy = LaneTop(Lane.Words);
        double lastX = -999;
        foreach (var (t, word) in Words)
        {
            var x = X(t);
            if (x < -50 || x > w) continue;
            dc.DrawLine(new Pen(WordB, 1), new Point(x, wyy + 4), new Point(x, wyy + 10));
            if (x - lastX > 8 && _pps > 30)
            {
                var ft = Text(word, 10.5, DimText);
                if (x - lastX > ft.Width * 0.3) { dc.DrawText(ft, new Point(x + 1, wyy + 11)); lastX = x + ft.Width; }
            }
        }
        // lane labels
        for (int l = 2; l <= 6; l++)
        {
            var ft = Text(Loc.T(LaneNames[l]), 10, DimText);
            dc.DrawRectangle(LaneBg, null, new Rect(0, LaneTop((Lane)l) + 1, ft.Width + 10, 14));
            dc.DrawText(ft, new Point(4, LaneTop((Lane)l) + 1));
        }
        // playhead
        if (_player != null)
        {
            var x = X(_player.Position);
            dc.DrawLine(new Pen(PlayB, 1.5), new Point(x, 0), new Point(x, h));
            var tri = new StreamGeometry();
            using (var c = tri.Open()) { c.BeginFigure(new Point(x - 6, 0), true, true); c.LineTo(new Point(x + 6, 0), true, false); c.LineTo(new Point(x, 8), true, false); }
            dc.DrawGeometry(PlayB, null, tri);
        }
        if (_hover is double hx) dc.DrawLine(new Pen(DimText, 0.7), new Point(hx, Ruler), new Point(hx, h));
    }

    void DrawRanges(DrawingContext dc, List<Span> spans, Lane lane, Brush b, string label)
    {
        var y = LaneTop(lane);
        foreach (var s in spans)
        {
            var r = new Rect(X(s.A), y + 3, Math.Max(2, (s.B - s.A) * _pps), LaneH - 6);
            dc.DrawRoundedRectangle(b, null, r, 4, 4);
            if (r.Width > 60)
            {
                dc.PushClip(new RectangleGeometry(r));
                dc.DrawText(Text($"{Fmt(s.A)} – {Fmt(s.B)}", 10.5, TextB, true), new Point(r.X + 6, r.Y + 4));
                dc.Pop();
            }
        }
    }

    public static string Fmt(double t) => $"{(int)t / 60}:{t % 60:00.0}";

    // ---------------- interaction ----------------
    enum Drag { None, Seek, Pan, EdgeA, EdgeB, Move, Drop, SegEdge }
    Drag _drag;
    Span? _span; int _dropIdx; int _segIdx;
    double _grab, _panFrom, _offFrom;
    double? _hover;

    double SnapT(double t) => Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) || Grid == null ? t : Grid.Snap(t);

    List<Span> SpansOf(Lane l) => l == Lane.Chorus ? Model!.Chorus : Model!.Dream;

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        Focus();
        if (Model == null) return;
        var p = e.GetPosition(this);
        var lane = LaneAt(p.Y);
        var t = T(p.X);
        if (e.ChangedButton == MouseButton.Middle) { _drag = Drag.Pan; _panFrom = p.X; _offFrom = _off; CaptureMouse(); return; }
        if (e.ChangedButton == MouseButton.Right) { ShowMenu(lane, t, p.X); e.Handled = true; return; }
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2 && lane == Lane.Words && Words.Count > 0)
        {
            var near = Words.MinBy(x => Math.Abs(x.t - t));
            _player?.Seek(near.t - 0.3);
            return;
        }
        switch (lane)
        {
            case Lane.Chorus or Lane.Dream:
                foreach (var s in SpansOf(lane))
                {
                    if (Math.Abs(X(s.A) - p.X) < 7) { _drag = Drag.EdgeA; _span = s; break; }
                    if (Math.Abs(X(s.B) - p.X) < 7) { _drag = Drag.EdgeB; _span = s; break; }
                    if (s.Contains(t)) { _drag = Drag.Move; _span = s; _grab = t - s.A; break; }
                }
                break;
            case Lane.Drops:
                for (int i = 0; i < Model.Drops.Count; i++)
                    if (Math.Abs(X(Model.Drops[i]) - p.X) < 10) { _drag = Drag.Drop; _dropIdx = i; break; }
                break;
            case Lane.Light:
                for (int i = 1; i < Model.Segments.Count; i++)
                    if (Math.Abs(X(Model.Segments[i].A) - p.X) < 6) { _drag = Drag.SegEdge; _segIdx = i; break; }
                break;
        }
        if (_drag == Drag.None && lane is Lane.Ruler or Lane.Wave or Lane.Words) { _drag = Drag.Seek; _player?.Seek(t); }
        if (_drag != Drag.None) CaptureMouse();
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        _hover = p.X;
        if (Model == null) return;
        var t = T(p.X);
        var lane = LaneAt(p.Y);
        Cursor = _drag switch
        {
            Drag.EdgeA or Drag.EdgeB or Drag.SegEdge or Drag.Drop => Cursors.SizeWE,
            Drag.Move or Drag.Pan => Cursors.ScrollWE,
            _ => HoverCursor(lane, p.X, t),
        };
        switch (_drag)
        {
            case Drag.Seek: _player?.Seek(Math.Max(0, t)); break;
            case Drag.Pan: _off = _offFrom - (p.X - _panFrom) / _pps; break;
            case Drag.EdgeA: _span!.A = Math.Min(SnapT(t), _span.B - 1); Model.Touch(); break;
            case Drag.EdgeB: _span!.B = Math.Max(SnapT(t), _span.A + 1); Model.Touch(); break;
            case Drag.Move:
                var len = _span!.B - _span.A;
                _span.A = Math.Max(0, SnapT(t - _grab)); _span.B = _span.A + len; Model.Touch(); break;
            case Drag.Drop: Model.Drops[_dropIdx] = Math.Max(0, SnapT(t)); Model.Touch(); break;
            case Drag.SegEdge:
                var prev = Model.Segments[_segIdx - 1]; var cur = Model.Segments[_segIdx];
                var nt = Math.Clamp(SnapT(t), prev.A + 0.5, cur.B - 0.5);
                prev.B = nt; cur.A = nt; Model.Touch(); break;
        }
        InvalidateVisual();
    }

    Cursor HoverCursor(Lane lane, double x, double t)
    {
        if (Model == null) return Cursors.Arrow;
        if (lane is Lane.Chorus or Lane.Dream)
            foreach (var s in SpansOf(lane))
            {
                if (Math.Abs(X(s.A) - x) < 7 || Math.Abs(X(s.B) - x) < 7) return Cursors.SizeWE;
                if (s.Contains(t)) return Cursors.Hand;
            }
        if (lane == Lane.Drops && Model.Drops.Any(d => Math.Abs(X(d) - x) < 10)) return Cursors.SizeWE;
        if (lane == Lane.Light && Model.Segments.Skip(1).Any(s => Math.Abs(X(s.A) - x) < 6)) return Cursors.SizeWE;
        return lane is Lane.Ruler or Lane.Wave ? Cursors.IBeam : Cursors.Arrow;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (_drag is Drag.EdgeA or Drag.EdgeB or Drag.Move && Model != null) { Model.Resync(); Model.Touch(); }
        _drag = Drag.None; _span = null;
        ReleaseMouseCapture();
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e) { _hover = null; InvalidateVisual(); }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var p = e.GetPosition(this);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) _off -= e.Delta / 120.0 * 60 / _pps;
        else
        {
            var t = T(p.X);
            _pps = Math.Clamp(_pps * (e.Delta > 0 ? 1.25 : 0.8), ActualWidth / (Dur + 2) * 0.9, 600);
            _off = t - p.X / _pps;
        }
        e.Handled = true;
        InvalidateVisual();
    }

    void ShowMenu(Lane lane, double t, double x)
    {
        if (Model == null) return;
        var m = new ContextMenu();
        void Add(string key, Action a) { var mi = new MenuItem { Header = Loc.T(key) }; mi.Click += (_, _) => { a(); Model.Touch(); InvalidateVisual(); }; m.Items.Add(mi); }
        var st = SnapT(t);
        switch (lane)
        {
            case Lane.Chorus or Lane.Dream:
                var list = SpansOf(lane);
                var hit = list.FirstOrDefault(s => s.Contains(t));
                if (hit != null) Add("TlRemove", () => { list.Remove(hit); Model.Resync(); });
                else
                {
                    var end = Grid != null ? Grid.Snap(st + 16) : st + 16;
                    Add(lane == Lane.Chorus ? "TlAddChorus" : "TlSetDream", () =>
                    {
                        if (lane == Lane.Dream) list.Clear();
                        list.Add(new Span(st, Math.Min(end, Dur))); Model.Resync();
                    });
                }
                break;
            case Lane.Drops:
                var di = Model.Drops.FindIndex(d => Math.Abs(X(d) - x) < 10);
                if (di >= 0) Add("TlRemove", () => Model.Drops.RemoveAt(di));
                else Add("TlAddDrop", () => Model.Drops.Add(st));
                break;
            case Lane.Light:
                var seg = Model.Segments.FirstOrDefault(s => s.Contains(t));
                if (seg == null) return;
                foreach (var lab in StoryModel.Labels)
                {
                    var mi = new MenuItem { Header = lab, IsChecked = seg.Label == lab, Foreground = SegBrush[lab] };
                    mi.Click += (_, _) => { seg.Label = lab; Model.Touch(); InvalidateVisual(); };
                    m.Items.Add(mi);
                }
                m.Items.Add(new Separator());
                Add("TlSplit", () =>
                {
                    if (st - seg.A < 0.5 || seg.B - st < 0.5) return;
                    var i = Model.Segments.IndexOf(seg);
                    Model.Segments.Insert(i + 1, new Span(st, seg.B, seg.Label)); seg.B = st;
                });
                var idx = Model.Segments.IndexOf(seg);
                if (idx > 0) Add("TlMerge", () => { Model.Segments[idx - 1].B = seg.B; Model.Segments.Remove(seg); });
                break;
            default:
                Add("TlPlayHere", () => _player?.Seek(t));
                break;
        }
        m.Items.Add(new Separator());
        var fit = new MenuItem { Header = Loc.T("TlFit") }; fit.Click += (_, _) => Fit(); m.Items.Add(fit);
        m.PlacementTarget = this; m.IsOpen = true;
    }
}
