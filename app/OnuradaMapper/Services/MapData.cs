using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Media;

namespace OnuradaMapper.Services;

/// <summary>Loudness envelope (RMS) of a WAV file (PCM 16/24/32 or float), 100 values per second, scaled so the loudest part is 1.
/// RMS rather than peaks: mastered songs hit full scale everywhere, RMS still shows verses, choruses and breaks.</summary>
public static class Waveform
{
    public const int Rate = 100;

    public static float[] Load(string path)
    {
        using var f = File.OpenRead(path);
        using var r = new BinaryReader(f);
        if (new string(r.ReadChars(4)) != "RIFF") throw new InvalidDataException("not a WAV file");
        r.ReadInt32();
        if (new string(r.ReadChars(4)) != "WAVE") throw new InvalidDataException("not a WAV file");
        int fmt = 1, ch = 2, sr = 44100, bits = 16;
        while (f.Position < f.Length - 8)
        {
            var id = new string(r.ReadChars(4));
            var size = r.ReadUInt32();
            var next = f.Position + size + (size & 1);
            if (id == "fmt ")
            {
                fmt = r.ReadUInt16(); ch = r.ReadUInt16(); sr = r.ReadInt32(); r.ReadInt32(); r.ReadUInt16(); bits = r.ReadUInt16();
                if (fmt == 0xFFFE && size >= 40) { r.ReadUInt16(); r.ReadUInt16(); r.ReadUInt32(); fmt = r.ReadUInt16(); }
            }
            else if (id == "data")
            {
                var bps = bits / 8;
                long frames = (size == 0xFFFFFFFF ? f.Length - f.Position : size) / (bps * ch);
                var hop = Math.Max(1, sr / Rate);
                var peaks = new float[frames / hop + 1];
                var buf = new byte[hop * bps * ch];
                for (long i = 0; i < peaks.Length; i++)
                {
                    var n = f.Read(buf, 0, buf.Length);
                    if (n <= 0) { Array.Resize(ref peaks, (int)i); break; }
                    double sq = 0; int cnt = 0;
                    for (int o = 0; o + bps <= n; o += bps)
                    {
                        float v = (fmt, bps) switch
                        {
                            (3, 4) => BitConverter.ToSingle(buf, o),
                            (_, 2) => BitConverter.ToInt16(buf, o) / 32768f,
                            (_, 3) => ((buf[o] << 8 | buf[o + 1] << 16 | buf[o + 2] << 24) >> 8) / 8388608f,
                            (_, 4) => BitConverter.ToInt32(buf, o) / 2147483648f,
                            _ => (buf[o] - 128) / 128f,
                        };
                        sq += v * v; cnt++;
                    }
                    peaks[i] = (float)Math.Sqrt(sq / Math.Max(1, cnt));
                }
                var top = peaks.Length > 0 ? peaks.OrderBy(x => x).ElementAt((int)(peaks.Length * 0.995)) : 1;
                for (int i = 0; i < peaks.Length; i++) peaks[i] = Math.Min(1f, peaks[i] / Math.Max(1e-4f, top));
                return peaks;
            }
            f.Position = next;
        }
        return [];
    }
}

public sealed record Note(double T, int X, int Y, int C, int D, int A);
public sealed record Bomb(double T, int X, int Y);
public sealed record Wall(double T, double Dur, int X, int Y, int W, int H);
public sealed record Arc(double T0, int X0, int Y0, int D0, double T1, int X1, int Y1, int C);
public sealed record Chain(double T0, int X0, int Y0, int D0, double T1, int X1, int Y1, int C, int Slices);

/// <summary>A built difficulty, with every time converted to song.egg seconds.</summary>
public sealed class Difficulty
{
    public string Name = "";
    public double Njs;
    public List<Note> Notes = [];
    public List<Bomb> Bombs = [];
    public List<Wall> Walls = [];
    public List<Arc> Arcs = [];
    public List<Chain> Chains = [];
}

/// <summary>Reads a built level folder (Info.dat, AudioData.dat, *.dat, lyrics.json).</summary>
public sealed class BuiltLevel
{
    public string Dir = "";
    public Color SaberA = Color.FromRgb(0xFF, 0x4F, 0xA3), SaberB = Color.FromRgb(0x4F, 0xC3, 0xFF);
    public Color Env0 = Color.FromRgb(0xFF, 0x4F, 0xA3), Env1 = Color.FromRgb(0x2E, 0x7B, 0xFF), Wall = Color.FromRgb(0xFF, 0xB3, 0xD4);
    public List<(string name, string file, double njs)> Diffs = [];
    public double Bpm = 120;
    List<(double si, double ei, double sb, double eb)> _regions = [];
    public JsonObject? Lyrics;

    public static BuiltLevel? Load(string dir)
    {
        var info = Path.Combine(dir, "Info.dat");
        if (!File.Exists(info)) return null;
        var L = new BuiltLevel { Dir = dir };
        var j = JsonNode.Parse(File.ReadAllText(info))!;
        L.Bpm = (double?)j["audio"]?["bpm"] ?? 120;
        var cs = j["colorSchemes"]?[0];
        if (cs != null)
        {
            L.SaberA = Hex((string?)cs["saberAColor"], L.SaberA); L.SaberB = Hex((string?)cs["saberBColor"], L.SaberB);
            L.Env0 = Hex((string?)cs["environmentColor0"], L.Env0); L.Env1 = Hex((string?)cs["environmentColor1"], L.Env1);
            L.Wall = Hex((string?)cs["obstaclesColor"], L.Wall);
        }
        foreach (var d in j["difficultyBeatmaps"]?.AsArray() ?? [])
            L.Diffs.Add(((string?)d!["difficulty"] ?? "?", (string?)d["beatmapDataFilename"] ?? "", (double?)d["noteJumpMovementSpeed"] ?? 16));
        var ad = Path.Combine(dir, "AudioData.dat");
        if (File.Exists(ad))
            foreach (var r in JsonNode.Parse(File.ReadAllText(ad))?["bpmData"]?.AsArray() ?? [])
                L._regions.Add(((double)r!["si"]!, (double)r["ei"]!, (double)r["sb"]!, (double)r["eb"]!));
        var ly = Path.Combine(dir, "lyrics.json");
        if (File.Exists(ly)) try { L.Lyrics = JsonNode.Parse(File.ReadAllText(ly)) as JsonObject; } catch { }
        return L;
    }

    static Color Hex(string? h, Color fallback)
    {
        try { return h == null ? fallback : (Color)ColorConverter.ConvertFromString("#" + (h.Length == 8 ? h[6..] + h[..6] : h)); }
        catch { return fallback; }
    }

    /// <summary>Map beat → song.egg seconds through the bpmData regions.</summary>
    public double BeatToSec(double b)
    {
        if (_regions.Count == 0) return b * 60 / Bpm;
        var r = _regions[^1];
        foreach (var x in _regions) if (b < x.eb) { r = x; break; }
        return (r.si + (b - r.sb) / Math.Max(1e-9, r.eb - r.sb) * (r.ei - r.si)) / 44100;
    }

    public Difficulty? LoadDiff(int i)
    {
        if (i < 0 || i >= Diffs.Count) return null;
        var (name, file, njs) = Diffs[i];
        var p = Path.Combine(Dir, file);
        if (!File.Exists(p)) return null;
        var j = JsonNode.Parse(File.ReadAllText(p))!;
        var D = new Difficulty { Name = name, Njs = njs };
        var nd = j["colorNotesData"]?.AsArray() ?? []; var bd = j["bombNotesData"]?.AsArray() ?? [];
        var od = j["obstaclesData"]?.AsArray() ?? []; var cd = j["chainsData"]?.AsArray() ?? [];
        static int I(JsonNode? n, string k, int def = 0) => (int?)n?[k] ?? def;
        static double F(JsonNode? n, string k, double def = 0) => (double?)n?[k] ?? def;
        JsonNode? At(JsonArray a, int i) => i >= 0 && i < a.Count ? a[i] : null;
        foreach (var o in j["colorNotes"]?.AsArray() ?? [])
        {
            var d = At(nd, I(o, "i"));
            D.Notes.Add(new Note(BeatToSec(F(o, "b")), I(d, "x"), I(d, "y"), I(d, "c"), I(d, "d"), I(d, "a")));
        }
        foreach (var o in j["bombNotes"]?.AsArray() ?? [])
        {
            var d = At(bd, I(o, "i"));
            D.Bombs.Add(new Bomb(BeatToSec(F(o, "b")), I(d, "x"), I(d, "y")));
        }
        foreach (var o in j["obstacles"]?.AsArray() ?? [])
        {
            var d = At(od, I(o, "i"));
            var b = F(o, "b");
            var t0 = BeatToSec(b);
            D.Walls.Add(new Wall(t0, BeatToSec(b + F(d, "d")) - t0, I(d, "x"), I(d, "y"), I(d, "w", 1), I(d, "h", 1)));
        }
        foreach (var o in j["arcs"]?.AsArray() ?? [])
        {
            var h = At(nd, I(o, "hi")); var t = At(nd, I(o, "ti"));
            D.Arcs.Add(new Arc(BeatToSec(F(o, "hb")), I(h, "x"), I(h, "y"), I(h, "d"), BeatToSec(F(o, "tb")), I(t, "x"), I(t, "y"), I(h, "c")));
        }
        foreach (var o in j["chains"]?.AsArray() ?? [])
        {
            var h = At(nd, I(o, "i")); var c = At(cd, I(o, "ci"));
            D.Chains.Add(new Chain(BeatToSec(F(o, "hb")), I(h, "x"), I(h, "y"), I(h, "d"), BeatToSec(F(o, "tb")), I(c, "tx"), I(c, "ty"), I(h, "c"), I(c, "c", 3)));
        }
        D.Notes.Sort((a, b) => a.T.CompareTo(b.T));
        return D;
    }
}

/// <summary>Per-song analysis (ana/NN.json): beat grid for snapping.</summary>
public sealed class Analysis
{
    public double[] Beats = [];
    public double Dur;
    public double[] Bars => Enumerable.Range(0, Beats.Length / 4 + 1).Select(i => Beats[Math.Min(4 * i, Beats.Length - 1)]).Distinct().ToArray();

    public static Analysis? Load(string id)
    {
        var p = AppEnv.WS("ana", id + ".json");
        if (!File.Exists(p)) return null;
        var j = JsonNode.Parse(File.ReadAllText(p))!;
        return new Analysis
        {
            Beats = j["beat_times"]?.AsArray().Select(x => (double)x!).ToArray() ?? [],
            Dur = (double?)j["dur"] ?? 0,
        };
    }

    public double Snap(double t, bool bars = true)
    {
        var grid = bars ? Bars : Beats;
        if (grid.Length == 0) return t;
        var i = Array.BinarySearch(grid, t);
        if (i >= 0) return grid[i];
        i = ~i;
        if (i == 0) return grid[0];
        if (i >= grid.Length) return grid[^1];
        return t - grid[i - 1] < grid[i] - t ? grid[i - 1] : grid[i];
    }
}
