using System.Text.Json.Nodes;

namespace OnuradaMapper.Services;

public sealed class Span(double a, double b, string label = "")
{
    public double A = a, B = b;
    public string Label = label;
    public bool Contains(double t) => t >= A && t < B;
}

/// <summary>The editable "story" of a song (song seconds): choruses, dream, drops and the light-story segments.</summary>
public sealed class StoryModel
{
    public static readonly string[] Labels = ["intro", "verse", "calm", "build", "break", "chorus", "hype", "dream", "outro"];

    public List<Span> Chorus = [];
    public List<Span> Dream = [];
    public List<double> Drops = [];
    public List<Span> Segments = [];
    public double Duration;
    public event Action? Changed;
    public void Touch() => Changed?.Invoke();

    static List<Span> Ranges(JsonNode? n) =>
        n?.AsArray().Select(r => new Span((double)r![0]!, (double)r[1]!)).OrderBy(s => s.A).ToList() ?? [];

    public static StoryModel From(JsonObject cfg, double duration)
    {
        var m = new StoryModel
        {
            Duration = duration,
            Chorus = Ranges(cfg["chorus"]),
            Dream = Ranges(cfg["dream"]),
            Drops = cfg["drops"]?.AsArray().Select(x => (double)x!).OrderBy(x => x).ToList() ?? [],
        };
        foreach (var s in cfg["lightstory"]?["segments"]?.AsArray() ?? [])
            m.Segments.Add(new Span((double)s![0]!, (double)s[1]!, (string?)s[2] ?? "verse"));
        if (m.Segments.Count == 0) m.Segments.Add(new Span(0, duration + 10, "verse"));
        return m;
    }

    static JsonArray Arr(IEnumerable<Span> s) => new(s.OrderBy(x => x.A).Select(x => (JsonNode)new JsonArray(Math.Round(x.A, 2), Math.Round(x.B, 2))).ToArray());

    /// <summary>Writes the story back into the song config, keeping the fields that follow it in step.</summary>
    public void ApplyTo(JsonObject cfg)
    {
        var oldDream = cfg["dream"]?.ToJsonString();
        cfg["chorus"] = Arr(Chorus);
        if (Dream.Count > 0)
        {
            cfg["dream"] = Arr(Dream);
            if (cfg["video"] is JsonObject v) { v["start"] = Math.Round(Dream[0].A, 2); v["end"] = Math.Round(Dream[0].B, 2); }
        }
        else { cfg.Remove("dream"); cfg.Remove("video"); }
        if (cfg["lyrics"] is JsonObject ly && ly["ranges"] != null && ly["ranges"]!.ToJsonString() == oldDream)
        {
            if (Dream.Count > 0) ly["ranges"] = Arr(Dream); else ly.Remove("ranges");
        }
        var drops = Drops.OrderBy(x => x).Select(x => Math.Round(x, 2)).ToList();
        cfg["drops"] = new JsonArray(drops.Select(x => (JsonNode)x).ToArray());
        cfg["burst_times"] = new JsonArray(drops.Select(x => (JsonNode)Math.Round(x - 0.1, 2)).ToArray());
        var ls = cfg["lightstory"] as JsonObject ?? [];
        ls["segments"] = new JsonArray(Segments.OrderBy(s => s.A).Select(s => (JsonNode)new JsonArray(Math.Round(s.A, 2), Math.Round(s.B, 2), s.Label)).ToArray());
        ls["flashes"] = new JsonArray(drops.Select(x => (JsonNode)x).ToArray());
        cfg["lightstory"] = ls;
    }

    /// <summary>After choruses or the dream moved: relabel the light story so it follows them (dream > outro > chorus > the rest).</summary>
    public void Resync()
    {
        Normalize(Chorus); Normalize(Dream);
        var cuts = new SortedSet<double>();
        foreach (var s in Segments.Concat(Chorus).Concat(Dream)) { cuts.Add(s.A); cuts.Add(s.B); }
        var start = Segments.Count > 0 ? Segments.Min(s => s.A) : 0;
        var end = Segments.Count > 0 ? Segments.Max(s => s.B) : Duration;
        var pts = cuts.Where(c => c >= start && c <= end).ToList();
        var res = new List<Span>();
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            double a = pts[i], b = pts[i + 1], m = (a + b) / 2;
            if (b - a < 0.01) continue;
            var old = Segments.FirstOrDefault(s => s.Contains(m))?.Label ?? "verse";
            string lab = Dream.Any(s => s.Contains(m)) ? "dream"
                       : old == "outro" ? "outro"
                       : Chorus.Any(s => s.Contains(m)) ? "chorus"
                       : old is "chorus" or "dream" ? "verse" : old;
            if (res.Count > 0 && res[^1].Label == lab) res[^1].B = b;
            else res.Add(new Span(a, b, lab));
        }
        if (res.Count > 0) Segments = res;
    }

    /// <summary>Sorts and merges overlapping ranges.</summary>
    public static void Normalize(List<Span> l)
    {
        l.RemoveAll(s => s.B - s.A < 0.5);
        l.Sort((x, y) => x.A.CompareTo(y.A));
        for (int i = 1; i < l.Count; i++)
            if (l[i].A <= l[i - 1].B) { l[i - 1].B = Math.Max(l[i - 1].B, l[i].B); l.RemoveAt(i--); }
    }
}
