using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace OnuradaLyrics
{
    /// <summary>Small self-contained JSON reader (Unity's JsonUtility can't see types from runtime-loaded plugin assemblies).</summary>
    internal static class MiniJson
    {
        public static object Parse(string s) { int i = 0; var v = Value(s, ref i); return v; }

        static void Ws(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

        static object Value(string s, ref int i)
        {
            Ws(s, ref i);
            if (i >= s.Length) return null;
            char c = s[i];
            if (c == '{')
            {
                var d = new Dictionary<string, object>(); i++;
                Ws(s, ref i);
                if (s[i] == '}') { i++; return d; }
                while (true)
                {
                    Ws(s, ref i); var k = Str(s, ref i); Ws(s, ref i); i++; // ':'
                    d[k] = Value(s, ref i); Ws(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    i++; return d; // '}'
                }
            }
            if (c == '[')
            {
                var l = new List<object>(); i++;
                Ws(s, ref i);
                if (s[i] == ']') { i++; return l; }
                while (true)
                {
                    l.Add(Value(s, ref i)); Ws(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    i++; return l; // ']'
                }
            }
            if (c == '"') return Str(s, ref i);
            if (s.Substring(i).StartsWith("true")) { i += 4; return true; }
            if (s.Substring(i).StartsWith("false")) { i += 5; return false; }
            if (s.Substring(i).StartsWith("null")) { i += 4; return null; }
            int st = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            return double.Parse(s.Substring(st, i - st), CultureInfo.InvariantCulture);
        }

        static string Str(string s, ref int i)
        {
            var sb = new StringBuilder(); i++; // opening quote
            while (s[i] != '"')
            {
                if (s[i] == '\\')
                {
                    i++;
                    char e = s[i];
                    if (e == 'n') sb.Append('\n');
                    else if (e == 't') sb.Append('\t');
                    else if (e == 'u') { sb.Append((char)Convert.ToInt32(s.Substring(i + 1, 4), 16)); i += 4; }
                    else sb.Append(e);
                    i++;
                }
                else sb.Append(s[i++]);
            }
            i++;
            return sb.ToString();
        }

        static float F(object o) => o is double d ? (float)d : 0f;
        static string S(object o) => o as string;
        static object G(Dictionary<string, object> d, string k) => d != null && d.TryGetValue(k, out var v) ? v : null;

        public static LyricFile ToLyrics(string json)
        {
            var root = Parse(json) as Dictionary<string, object>;
            if (root == null) return null;
            var lf = new LyricFile { title = S(G(root, "title")), color = S(G(root, "color")), fx = S(G(root, "fx")) };
            lf.hearts = (G(root, "hearts") as List<object> ?? new List<object>()).Select(F).ToArray();
            if (G(root, "video") is Dictionary<string, object> vd)
                lf.video = new VideoInfo { file = S(G(vd, "file")), start = F(G(vd, "start")), end = F(G(vd, "end")), fps = G(vd, "fps") != null ? F(G(vd, "fps")) : 24f, mode = S(G(vd, "mode")) ?? "pingpong", x = F(G(vd, "x")), y = F(G(vd, "y")), z = G(vd, "z") != null ? F(G(vd, "z")) : 22f, height = G(vd, "height") != null ? F(G(vd, "height")) : 7f };
            if (G(root, "intro") is Dictionary<string, object> idd)
                lf.intro = new IntroInfo { artist = S(G(idd, "artist")), title = S(G(idd, "title")), start = F(G(idd, "start")), end = F(G(idd, "end")) };
            if (G(root, "outro") is Dictionary<string, object> od)
                lf.outro = new OutroInfo { text = S(G(od, "text")), start = F(G(od, "start")), end = F(G(od, "end")) };
            lf.bursts = (G(root, "bursts") as List<object> ?? new List<object>()).Select(F).ToArray();
            if (G(root, "dust") is Dictionary<string, object> dd)
                lf.dust = new DustInfo
                {
                    ranges = (G(dd, "ranges") as List<object> ?? new List<object>()).Select(r => (r as List<object> ?? new List<object>()).Select(F).ToArray()).ToArray(),
                    bursts = (G(dd, "bursts") as List<object> ?? new List<object>()).Select(r => (r as List<object> ?? new List<object>()).Select(F).ToArray()).ToArray(),
                    beats = (G(dd, "beats") as List<object> ?? new List<object>()).Select(F).ToArray(),
                    cut = G(dd, "cut") is bool cb && cb,
                    colors = (G(dd, "colors") as List<object> ?? new List<object>()).Select(S).Where(x => x != null).ToArray(),
                    dream = (G(dd, "dream") as List<object> ?? new List<object>()).Select(r => (r as List<object> ?? new List<object>()).Select(F).ToArray()).ToArray(),
                    dream_colors = (G(dd, "dream_colors") as List<object> ?? new List<object>()).Select(S).Where(x => x != null).ToArray(),
                    texts = (G(dd, "texts") as List<object> ?? new List<object>()).OfType<Dictionary<string, object>>().Select(td =>
                    {
                        var pts = (G(td, "points") as List<object> ?? new List<object>()).OfType<List<object>>().Where(p => p.Count >= 2).ToList();
                        return new DustText { start = F(G(td, "start")), end = F(G(td, "end")), y = F(G(td, "y")), exit = S(G(td, "exit")) ?? "burst",
                                              px = pts.Select(p => F(p[0])).ToArray(), py = pts.Select(p => F(p[1])).ToArray() };
                    }).ToArray(),
                };
            if (G(root, "wcolors") is Dictionary<string, object> wcd)
                lf.wcolors = new WColors { scheme = S(G(wcd, "scheme")), w = S(G(wcd, "w")), wb = S(G(wcd, "wb")),
                                           rainbow = (G(wcd, "rainbow") as List<object> ?? new List<object>()).Select(S).Where(x => x != null).ToArray() };
            var hbl = (G(root, "heartbeat") as List<object> ?? new List<object>()).OfType<List<object>>().Where(p => p.Count >= 2).ToList();
            lf.heartbeat = hbl.Select(p => F(p[0])).ToArray();
            lf.heartbeatDub = hbl.Select(p => F(p[1]) > 0.5f).ToArray();
            if (G(root, "combo") is Dictionary<string, object> cd)
                lf.combo = new ComboInfo { milestones = (G(cd, "milestones") as List<object> ?? new List<object>()).Select(o => (int)F(o)).ToArray(), fc = F(G(cd, "fc")) };
            var lines = new List<LyricLine>();
            foreach (var lo in G(root, "lines") as List<object> ?? new List<object>())
            {
                var ld = lo as Dictionary<string, object>;
                if (ld == null) continue;
                var words = new List<LyricWord>();
                foreach (var wo in G(ld, "words") as List<object> ?? new List<object>())
                {
                    var wd = wo as Dictionary<string, object>;
                    if (wd != null) words.Add(new LyricWord { t = F(G(wd, "t")), w = S(G(wd, "w")) ?? "" });
                }
                lines.Add(new LyricLine { start = F(G(ld, "start")), end = F(G(ld, "end")), fx = S(G(ld, "fx")), words = words.ToArray() });
            }
            lf.lines = lines.ToArray();
            return lf;
        }
    }
}
