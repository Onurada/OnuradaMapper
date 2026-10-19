using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using OnuradaMapper.Services;

namespace OnuradaMapper.Views;

/// <summary>A 2.5D preview of a built difficulty: blocks fly in on the 4×3 grid in sync with the song,
/// with arcs, chains, bombs, walls, the karaoke line and light pulses from the story.</summary>
public sealed class PreviewControl : FrameworkElement
{
    public BuiltLevel? Level { get; set; }
    public Difficulty? Diff { get => _diff; set { _diff = value; InvalidateVisual(); } }
    public StoryModel? Story { get; set; }
    public double Pad { get; set; }          // song.egg seconds = song seconds + Pad
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
    public double Lookahead = 1.4;

    Difficulty? _diff;
    AudioPlayer? _player;
    static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    public PreviewControl() { ClipToBounds = true; }

    // projection: depth z in [0,1] (0 = at the player), vanishing point above the grid centre
    Point P(double gx, double gy, double z, out double s)
    {
        var w = ActualWidth; var h = ActualHeight;
        var cell = Math.Min(w / 6.5, h / 5.2);
        s = 1 / (1 + z * 5.5);
        var nx = w / 2 + (gx - 1.5) * cell;
        var ny = h * 0.80 - gy * cell;
        var vx = w / 2; var vy = h * 0.30;
        return new Point(vx + (nx - vx) * s, vy + (ny - vy) * s);
    }

    double Cell => Math.Min(ActualWidth / 6.5, ActualHeight / 5.2);

    static Color Mix(Color a, Color b, double t) => Color.FromArgb(255, (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
    static Brush Solid(Color c, byte a = 255) { c.A = a; var b = new SolidColorBrush(c); b.Freeze(); return b; }

    static readonly double[] Angle = [180, 0, 90, 270, 135, 225, 45, 315, 0];   // cut direction → rotation of the arrow (pointing down = 0)

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth; var h = ActualHeight;
        var L = Level;
        var now = (_player?.Position ?? 0) + Pad;      // song.egg seconds
        var songT = now - Pad;
        var label = Story?.Segments.FirstOrDefault(s => s.Contains(songT))?.Label ?? "verse";
        var e0 = L?.Env0 ?? Colors.HotPink; var e1 = L?.Env1 ?? Colors.DodgerBlue;
        // background + light pulses (stronger in choruses / after drops, purple in the dream)
        var energy = label switch { "chorus" or "hype" => 1.0, "build" => 0.7, "dream" => 0.55, "calm" or "intro" or "outro" => 0.25, _ => 0.45 };
        var beat = L != null ? (now / (60 / L.Bpm)) % 1 : 0;
        var pulse = energy * Math.Exp(-beat * 4);
        var sinceDrop = Story?.Drops.Select(d => songT - d).Where(x => x >= 0).DefaultIfEmpty(99).Min() ?? 99;
        var flash = sinceDrop < 0.6 ? 1 - sinceDrop / 0.6 : 0;
        var bgTop = label == "dream" ? Color.FromRgb(0x24, 0x10, 0x3A) : Color.FromRgb(0x0B, 0x0B, 0x16);
        dc.DrawRectangle(new LinearGradientBrush(bgTop, Color.FromRgb(0x05, 0x05, 0x09), 90), null, new Rect(0, 0, w, h));
        var lc0 = label == "dream" ? Color.FromRgb(0xB4, 0x8C, 0xFF) : e0; var lc1 = label == "dream" ? Color.FromRgb(0x9B, 0x3D, 0xFF) : e1;
        for (int side = 0; side < 2; side++)
        {
            var c = side == 0 ? lc0 : lc1;
            var a = (byte)Math.Clamp(40 + 170 * pulse + 80 * flash, 0, 255);
            var x = side == 0 ? w * 0.08 : w * 0.92;
            var g = new RadialGradientBrush(Color.FromArgb(a, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B)) { RadiusX = 0.5, RadiusY = 0.5 };
            dc.DrawEllipse(g, null, new Point(x, h * 0.35), w * 0.28, h * 0.55);
            for (int k = 0; k < 4; k++)   // lasers
            {
                var ang = (k - 1.5) * 0.18 + Math.Sin(now * (0.7 + k * 0.2) + side) * 0.25 * energy;
                var pen = new Pen(Solid(c, (byte)Math.Clamp(30 + 150 * pulse, 0, 255)), 2 + 2 * flash);
                dc.DrawLine(pen, new Point(x, h * 0.95), new Point(x + Math.Sin(ang) * h * (side == 0 ? 1 : -1), h * 0.95 - Math.Cos(ang) * h));
            }
        }
        if (flash > 0) dc.DrawRectangle(Solid(Colors.White, (byte)(70 * flash)), null, new Rect(0, 0, w, h));
        // floor lines
        var floorPen = new Pen(Solid(Colors.White, 28), 1);
        for (int i = 0; i <= 4; i++) dc.DrawLine(floorPen, P(i - 0.5, -0.5, 0, out _), P(i - 0.5, -0.5, 1.2, out _));
        var D = Diff;
        if (D == null || L == null) { DrawLyrics(dc, now); return; }
        double Z(double t) => (t - now) / Lookahead;
        // walls
        foreach (var o in D.Walls)
        {
            if (o.T > now + Lookahead || o.T + o.Dur < now) continue;
            var z0 = Math.Max(0, Z(o.T)); var z1 = Math.Min(1.2, Z(o.T + o.Dur));
            var a = P(o.X - 0.5, o.Y - 0.5 + o.H, z0, out _); var b = P(o.X - 0.5 + o.W, o.Y - 0.5, z0, out _);
            var a2 = P(o.X - 0.5, o.Y - 0.5 + o.H, z1, out _); var b2 = P(o.X - 0.5 + o.W, o.Y - 0.5, z1, out _);
            var geo = new StreamGeometry();
            using (var c = geo.Open())
            {
                c.BeginFigure(a, true, true); c.LineTo(a2, true, false); c.LineTo(new Point(b2.X, a2.Y), true, false);
                c.LineTo(b2, true, false); c.LineTo(b, true, false); c.LineTo(new Point(b.X, a.Y), true, false);
            }
            dc.DrawGeometry(Solid(L.Wall, 50), new Pen(Solid(L.Wall, 200), 1.5), geo);
        }
        // arcs (head → tail curve)
        foreach (var r in D.Arcs)
        {
            if (r.T0 > now + Lookahead || r.T1 < now) continue;
            var col = r.C == 0 ? L.SaberA : L.SaberB;
            var geo = new StreamGeometry();
            using (var c = geo.Open())
            {
                bool first = true;
                for (int k = 0; k <= 24; k++)
                {
                    var u = k / 24.0; var t = r.T0 + (r.T1 - r.T0) * u;
                    if (t < now) continue;
                    var e = u * u * (3 - 2 * u);
                    var pt = P(r.X0 + (r.X1 - r.X0) * e, r.Y0 + (r.Y1 - r.Y0) * e, Z(t), out _);
                    if (Z(t) > 1.2) break;
                    if (first) { c.BeginFigure(pt, false, false); first = false; } else c.LineTo(pt, true, false);
                }
            }
            dc.DrawGeometry(null, new Pen(Solid(col, 170), 3), geo);
        }
        // chains: head block drawn below, links here
        foreach (var ch in D.Chains)
        {
            if (ch.T0 > now + Lookahead || ch.T1 < now) continue;
            var col = ch.C == 0 ? L.SaberA : L.SaberB;
            for (int k = 1; k < ch.Slices; k++)
            {
                var u = k / (double)(ch.Slices - 1);
                var t = ch.T0 + (ch.T1 - ch.T0) * u;
                if (t < now || Z(t) > 1) continue;
                var pt = P(ch.X0 + (ch.X1 - ch.X0) * u, ch.Y0 + (ch.Y1 - ch.Y0) * u, Z(t), out var s);
                var sz = Cell * 0.55 * s;
                dc.DrawRoundedRectangle(Solid(col, 230), null, new Rect(pt.X - sz / 2, pt.Y - sz / 6, sz, sz / 3), 2, 2);
            }
        }
        // bombs + notes, far to near
        foreach (var b in D.Bombs)
        {
            var z = Z(b.T);
            if (z < -0.05 || z > 1) continue;
            var pt = P(b.X, b.Y, Math.Max(0, z), out var s);
            var r = Cell * 0.32 * s;
            dc.DrawEllipse(Solid(Color.FromRgb(0x22, 0x22, 0x28)), new Pen(Solid(Color.FromRgb(0x77, 0x77, 0x88)), 1.5), pt, r, r);
        }
        var lo = LowerBound(D.Notes, now - 0.15);
        var vis = new List<Note>();
        for (int i = lo; i < D.Notes.Count && D.Notes[i].T <= now + Lookahead; i++) vis.Add(D.Notes[i]);
        vis.Reverse();
        foreach (var n in vis)
        {
            var z = Z(n.T);
            var pt = P(n.X, n.Y, Math.Max(0, z), out var s);
            var sz = Cell * 0.78 * s;
            var col = n.C == 0 ? L.SaberA : L.SaberB;
            byte alpha = z < 0 ? (byte)Math.Max(0, 255 + z / 0.15 * 255) : (byte)255;
            if (z < 0) { sz *= 1 - z * 3; }   // just cut: burst outwards and fade
            var fade = Math.Clamp(1 - (z - 0.7) / 0.3, 0.25, 1);
            var body = Mix(Color.FromRgb(0x10, 0x10, 0x18), col, fade);
            dc.PushTransform(new RotateTransform(Angle[Math.Clamp(n.D, 0, 8)] + n.A, pt.X, pt.Y));
            dc.DrawRoundedRectangle(Solid(body, alpha), new Pen(Solid(Mix(col, Colors.White, 0.35), alpha), Math.Max(1, 2 * s)),
                                    new Rect(pt.X - sz / 2, pt.Y - sz / 2, sz, sz), sz * 0.18, sz * 0.18);
            var white = Solid(Colors.White, alpha);
            if (n.D == 8) dc.DrawEllipse(white, null, pt, sz * 0.13, sz * 0.13);
            else
            {
                var tri = new StreamGeometry();
                using (var c = tri.Open())
                {
                    c.BeginFigure(new Point(pt.X - sz * 0.32, pt.Y + sz * 0.12), true, true);
                    c.LineTo(new Point(pt.X + sz * 0.32, pt.Y + sz * 0.12), true, false);
                    c.LineTo(new Point(pt.X, pt.Y + sz * 0.36), true, false);
                }
                dc.DrawGeometry(white, null, tri);
            }
            dc.Pop();
        }
        DrawLyrics(dc, now);
        // HUD
        var nps = D.Notes.Count(x => x.T >= now - 1 && x.T < now + 1) / 2.0;
        var hud = new FormattedText($"{D.Name}   {nps:0.0} nps   {label}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 12,
                                    Solid(Colors.White, 160), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(hud, new Point(12, h - 22));
    }

    void DrawLyrics(DrawingContext dc, double now)
    {
        var lines = Level?.Lyrics?["lines"]?.AsArray();
        if (lines == null) return;
        var line = lines.FirstOrDefault(l => (double?)l?["start"] <= now && now <= (double?)l?["end"] + 0.2);
        if (line == null) return;
        var words = line["words"]?.AsArray() ?? [];
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var size = Math.Clamp(ActualWidth / 26, 16, 34);
        var pieces = words.Select(wd => new FormattedText(((string?)wd?["w"] ?? "") + " ", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size,
            (double?)wd?["t"] <= now ? Solid(Level!.SaberA) : Solid(Colors.White, 140), dpi)).ToList();
        var total = pieces.Sum(p => p.WidthIncludingTrailingWhitespace);
        var x = (ActualWidth - total) / 2;
        foreach (var p in pieces) { dc.DrawText(p, new Point(x, ActualHeight * 0.07)); x += p.WidthIncludingTrailingWhitespace; }
    }

    static int LowerBound(List<Note> l, double t)
    {
        int a = 0, b = l.Count;
        while (a < b) { var m = (a + b) / 2; if (l[m].T < t) a = m + 1; else b = m; }
        return a;
    }
}
