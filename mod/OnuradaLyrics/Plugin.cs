using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace OnuradaLyrics
{
    [Serializable]
    public class LyricWord { public float t; public string w; }

    [Serializable]
    public class LyricLine { public float start; public float end; public LyricWord[] words; public string fx; }

    [Serializable]
    public class LyricFile { public string title; public LyricLine[] lines; public float[] hearts; public float[] bursts; public VideoInfo video; public IntroInfo intro; public OutroInfo outro; public DustInfo dust; public WColors wcolors; public ComboInfo combo; public float[] heartbeat; public bool[] heartbeatDub; public string color; public string fx; }

    [BepInPlugin("onurada.lyrics", "Onurada Lyrics", "1.9.0")]
    public partial class Plugin : BaseUnityPlugin
    {
        internal static Plugin Instance;
        internal static string CurrentSongName;

        ConfigEntry<float> cfgHeight, cfgDistance, cfgSize, cfgLead;
        ConfigEntry<int> cfgLayer;

        readonly Dictionary<string, string> _index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Type _atscType;
        PropertyInfo _songTimeProp;
        Component _atsc;
        LyricFile _lyrics;
        float _nextSearch;
        GameObject _root;
        Color _hi = new Color(1f, 0.45f, 0.75f);

        // animated line slots: current line + preview of the next one
        class Slot
        {
            public TextMeshPro tmp;
            public int line = -2;
            public int[] charWord;          // char index -> word index
            public float[] charFrac;        // char position inside its word 0..1
            public TMP_MeshInfo[] cache;    // untouched vertex positions
        }
        Slot _cur, _prev, _next;
        HeartFx _fx;
        Transform _textRoot;
        VideoBillboard _video;
        internal void Log(string s) => Logger.LogInfo(s);
        static float Hash(int i, int k) { uint h = (uint)(i * 374761393 + k * 668265263); h = (h ^ (h >> 13)) * 1274126177u; return ((h ^ (h >> 16)) & 0xFFFF) / 65535f; }

        void Awake()
        {
            Instance = this;
            cfgHeight = Config.Bind("Display", "Height", 3.1f, "Text height above the floor (m)");
            cfgDistance = Config.Bind("Display", "Distance", 9f, "Distance in front of the player (m)");
            cfgSize = Config.Bind("Display", "FontSize", 7f, "Font size of the current line");
            cfgLead = Config.Bind("Display", "LineLeadSeconds", 0.2f, "Show a line this many seconds before it is sung");
            cfgLayer = Config.Bind("Display", "Layer", -1, "Unity layer for the text (-1 = automatic)");
            _atscType = AccessTools.TypeByName("AudioTimeSyncController");
            _songTimeProp = _atscType?.GetProperty("songTime");
            try
            {
                var setupType = AccessTools.TypeByName("GameplayCoreSceneSetupData");
                var h = new Harmony("onurada.lyrics");
                foreach (var ctor in setupType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    h.Patch(ctor, postfix: new HarmonyMethod(typeof(Plugin).GetMethod(nameof(SetupPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                Logger.LogInfo("Patched GameplayCoreSceneSetupData ctor(s)");
                h.Patch(AccessTools.Method(typeof(NoteCutCoreEffectsSpawner), "SpawnNoteCutEffect"),
                        postfix: new HarmonyMethod(typeof(Plugin).GetMethod(nameof(CutPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                Logger.LogInfo("Patched NoteCutCoreEffectsSpawner.SpawnNoteCutEffect");
                h.Patch(AccessTools.Method(typeof(ColorManager), nameof(ColorManager.ColorForType), new[] { typeof(EnvironmentColorType), typeof(bool) }),
                        postfix: new HarmonyMethod(typeof(Plugin).GetMethod(nameof(ColorForTypePostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                Logger.LogInfo("Patched ColorManager.ColorForType");
            }
            catch (Exception e) { Logger.LogError("Patch failed: " + e); }
            BuildIndex();
        }

        static void SetupPostfix(object __instance)
        {
            try
            {
                var lvl = __instance.GetType().GetField("beatmapLevel")?.GetValue(__instance);
                CurrentSongName = lvl?.GetType().GetField("songName")?.GetValue(lvl) as string;
                Instance?.Logger.LogInfo("Level: " + CurrentSongName);
                Instance?.PrepareLevel(CurrentSongName);
            }
            catch (Exception e) { Instance?.Logger.LogError(e); }
        }

        void BuildIndex()
        {
            _index.Clear();
            try
            {
                var dir = Path.Combine(Application.dataPath, "CustomLevels");
                if (!Directory.Exists(dir)) return;
                foreach (var f in Directory.GetFiles(dir, "lyrics.json", SearchOption.AllDirectories))
                {
                    try
                    {
                        var lf = MiniJson.ToLyrics(File.ReadAllText(f, Encoding.UTF8));
                        if (lf != null && !string.IsNullOrEmpty(lf.title)) _index[lf.title.Trim()] = f;
                    }
                    catch (Exception e) { Logger.LogWarning("Bad lyrics file " + f + ": " + e.Message); }
                }
                Logger.LogInfo("Lyrics files indexed: " + _index.Count);
            }
            catch (Exception e) { Logger.LogError(e); }
        }

        void Update()
        {
            if (_atsc == null)
            {
                if (_root != null)
                {
                    _video?.Destroy(); _video = null; Destroy(_root); _root = null; WCol = WBCol = null;
                    if (_comboCtl != null) { _comboCtl.comboDidChangeEvent -= OnComboChanged; _comboCtl.comboBreakingEventHappenedEvent -= OnComboBroken; }
                    _comboCtl = null;
                }
                if (Time.unscaledTime < _nextSearch || _atscType == null) return;
                _nextSearch = Time.unscaledTime + 0.1f;
                var found = FindObjectsByType(_atscType, FindObjectsSortMode.None);
                if (found == null || found.Length == 0) return;
                _atsc = found[0] as Component;
                StartSession();
                return;
            }
            if (_lyrics == null || _root == null) return;
            FindCombo();
            try { float st = (float)_songTimeProp.GetValue(_atsc, null); Render(st); _video?.Update(st, _root.transform); }
            catch (Exception e) { Logger.LogError(e); _lyrics = null; }
        }

        void StartSession()
        {
            _lyrics = null;
            var name = (CurrentSongName ?? "").Trim();
            if (!_index.ContainsKey(name)) BuildIndex();
            if (!_index.TryGetValue(name, out var path)) { Logger.LogInfo("No lyrics for '" + name + "'"); return; }
            try { _lyrics = MiniJson.ToLyrics(File.ReadAllText(path, Encoding.UTF8)); }
            catch (Exception e) { Logger.LogError(e); return; }
            if (_lyrics.lines == null) _lyrics.lines = new LyricLine[0];
            // intro title card (also a quick visual check that the display works)
            if (_lyrics.intro == null && (_lyrics.lines.Length == 0 || _lyrics.lines[0].start > 5f))
            {
                var title = System.Text.RegularExpressions.Regex.Replace(name, @"\s*\(by [^)]*\)\s*$", "").Trim().ToUpperInvariant();
                var ws = title.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                              .Select((w, i) => new LyricWord { t = 0.9f + i * 0.25f, w = w }).ToArray();
                var card = new LyricLine { start = 0.9f, end = 3.6f, words = ws, fx = "" };
                _lyrics.lines = new[] { card }.Concat(_lyrics.lines).ToArray();
            }
            if (!string.IsNullOrEmpty(_lyrics.color) && ColorUtility.TryParseHtmlString("#" + _lyrics.color, out var c)) _hi = c;
            CreateObjects();
            _video?.Destroy(); _video = null;
            if (_lyrics.video != null && !string.IsNullOrEmpty(_lyrics.video.file))
            {
                var vp = Path.Combine(Path.GetDirectoryName(path), _lyrics.video.file);
                if (File.Exists(vp)) { _video = new VideoBillboard(_lyrics.video, vp, _root.transform, _root.layer); Logger.LogInfo("Video: " + vp); }
                else Logger.LogWarning("Video file missing: " + vp);
            }
            Logger.LogInfo("Lyrics loaded for '" + name + "': " + _lyrics.lines.Length + " lines");
        }

        TMP_FontAsset PickFont()
        {
            var fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
            return fonts.FirstOrDefault(f => f.name.IndexOf("Teko", StringComparison.OrdinalIgnoreCase) >= 0 && f.name.IndexOf("Medium", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? fonts.FirstOrDefault(f => f.name.IndexOf("Teko", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? fonts.FirstOrDefault();
        }

        TextMeshPro MakeText(string n, float size, TMP_FontAsset font)
        {
            var go = new GameObject(n);
            go.transform.SetParent(_root.transform, false);
            var tmp = go.AddComponent<TextMeshPro>();
            if (font != null) tmp.font = font;
            tmp.fontSize = size;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            tmp.richText = true;
            tmp.rectTransform.sizeDelta = new Vector2(40f, 4f);
            tmp.color = Color.white;
            tmp.text = "";
            try { tmp.outlineWidth = 0.2f; tmp.outlineColor = new Color32(0, 0, 0, 230); } catch { }
            return tmp;
        }

        void CreateObjects()
        {
            if (_root != null) Destroy(_root);
            _root = new GameObject("OnuradaLyricsRoot");
            _root.transform.position = new Vector3(0f, cfgHeight.Value, cfgDistance.Value);
            var font = PickFont();
            Logger.LogInfo("Font: " + (font != null ? font.name : "none"));
            float s = cfgSize.Value;
            _cur = new Slot { tmp = MakeText("LyricCur", s, font) };
            _prev = new Slot { tmp = MakeText("LyricPrev", s, font) };
            _next = new Slot { tmp = MakeText("LyricNext", s * 0.55f, font) };
            int layer = cfgLayer.Value;
            if (layer < 0)
            {
                layer = 0;
                try
                {
                    var texts = FindObjectsByType<TMP_Text>(FindObjectsSortMode.None);
                    var t0 = texts.FirstOrDefault(x => x.transform.root != _root.transform && x.gameObject.activeInHierarchy && !(x is TextMeshProUGUI));
                    if (t0 != null) layer = t0.gameObject.layer;
                }
                catch { }
            }
            foreach (var tr in _root.GetComponentsInChildren<Transform>(true)) tr.gameObject.layer = layer;
            Logger.LogInfo("Lyrics layer: " + layer + " (" + LayerMask.LayerToName(layer) + ")");
            _textRoot = new GameObject("LyricText").transform;
            _textRoot.SetParent(_root.transform, false);
            _textRoot.gameObject.layer = layer;
            foreach (var sl in new[] { _cur, _prev, _next }) sl.tmp.transform.SetParent(_textRoot, false);
            _fx = new HeartFx(_root.transform, layer, 36);
            InitIntroOutro(font, layer);
            InitDust(layer);
            InitShow(font, layer);
            InitHeartbeat();
        }

        // ---------------- animation ----------------
        static float Ease(float x) { x = Mathf.Clamp01(x); return 1f - (1f - x) * (1f - x) * (1f - x); }

        void SetLine(Slot sl, int li)
        {
            if (sl.line == li) return;
            sl.line = li;
            if (li < 0) { sl.tmp.text = ""; sl.cache = null; return; }
            var L = _lyrics.lines[li];
            var sb = new StringBuilder();
            var cw = new List<int>(); var cf = new List<float>();
            for (int k = 0; k < L.words.Length; k++)
            {
                if (k > 0) { sb.Append(' '); cw.Add(-1); cf.Add(0); }
                var w = L.words[k].w ?? "";
                for (int c = 0; c < w.Length; c++) { sb.Append(w[c]); cw.Add(k); cf.Add(w.Length > 1 ? c / (float)(w.Length - 1) : 0f); }
            }
            sl.tmp.text = sb.ToString();
            sl.charWord = cw.ToArray(); sl.charFrac = cf.ToArray();
            sl.tmp.ForceMeshUpdate();
            sl.cache = sl.tmp.textInfo.CopyMeshInfoVertexData();
        }

        float WordDur(LyricLine L, int k)
        {
            float nxt = k + 1 < L.words.Length ? L.words[k + 1].t : L.end;
            return Mathf.Clamp(nxt - L.words[k].t, 0.12f, 0.9f);
        }

        void Animate(Slot sl, float t, float lineAlpha, Vector3 lineOffset, float lineScale, bool preview)
        {
            if (sl.line < 0 || sl.cache == null) return;
            var L = _lyrics.lines[sl.line];
            var ti = sl.tmp.textInfo;
            string fx = !string.IsNullOrEmpty(L.fx) ? L.fx : _lyrics.fx;
            Color32 hi = _hi;
            for (int i = 0; i < ti.characterCount && i < sl.charWord.Length; i++)
            {
                var ci = ti.characterInfo[i];
                if (!ci.isVisible) continue;
                int m = ci.materialReferenceIndex, v = ci.vertexIndex;
                var src = sl.cache[m].vertices;
                var dst = ti.meshInfo[m].vertices;
                var cols = ti.meshInfo[m].colors32;
                int k = sl.charWord[i];
                float scale = lineScale, lift = 0f, alpha = lineAlpha, fill = 0f;
                if (!preview && k >= 0)
                {
                    var w = L.words[k];
                    float dt = t - w.t;
                    float dur = WordDur(L, k);
                    fill = Mathf.Clamp01((dt / dur) * 1.4f - sl.charFrac[i] * 0.4f);   // karaoke wipe through the word
                    if (fx == "slam")
                    {
                        // word slams in from big as it is cut
                        float a = Mathf.Clamp01((dt + 0.05f) / 0.18f);
                        scale *= dt < -0.05f ? 0f : Mathf.Lerp(2.2f, 1f, Ease(a));
                        alpha *= dt < -0.05f ? 0f : 1f;
                        fill = dt >= -0.05f ? 1f : 0f;
                    }
                    else
                    {
                        // pop + bounce when the word is sung
                        float p = dt > -0.05f && dt < 0.6f ? Mathf.Exp(-Mathf.Pow((dt - 0.07f) / 0.13f, 2f)) : 0f;
                        scale *= 1f + 0.32f * p;
                        lift = 0.22f * p;
                        if (dt < -0.05f) alpha *= 0.62f;
                    }
                    if (fx == "flash" && dt >= 0f && dt < 1.4f)
                        alpha *= (Mathf.Repeat(t * 14f, 1f) < 0.5f) ? 1f : 0.25f;
                    // gentle wave on sung words
                    if (dt > 0f) lift += 0.05f * Mathf.Sin((t * 5f) + i * 0.6f) * Mathf.Clamp01(dt * 2f);
                }
                Vector3 c = (src[v] + src[v + 2]) * 0.5f;
                for (int q = 0; q < 4; q++)
                    dst[v + q] = c + (src[v + q] - c) * scale + new Vector3(0f, lift, 0f) + lineOffset;
                Color32 col = fill > 0.5f ? hi : new Color32(255, 255, 255, 255);
                col.a = (byte)Mathf.Clamp(alpha * 255f, 0f, 255f);
                for (int q = 0; q < 4; q++) cols[v + q] = col;
            }
            sl.tmp.UpdateVertexData(TMP_VertexDataUpdateFlags.Vertices | TMP_VertexDataUpdateFlags.Colors32);
        }

        int _lastCur = -1;
        float _curSince;

        void Render(float t)
        {
            var lines = _lyrics.lines;
            // while the figure video plays, the lyrics move to the right side so she stays visible
            float vk = 0f;
            if (_lyrics.video != null && _video != null)
            {
                var v = _lyrics.video;
                vk = Ease((t - (v.start - 0.6f)) / 0.6f) * (1f - Ease((t - (v.end + 0.2f)) / 0.6f));
            }
            _textRoot.localPosition = Vector3.Lerp(Vector3.zero, new Vector3(5.2f, -0.5f, -1.2f), vk);
            _textRoot.localRotation = Quaternion.Euler(0f, 32f * vk, 0f);
            _textRoot.localScale = Vector3.one * Mathf.Lerp(1f, 0.72f, vk);
            float lead = cfgLead.Value;
            float sz = cfgSize.Value / 7f;
            int cur = -1;
            for (int i = 0; i < lines.Length; i++)
                if (t >= lines[i].start - lead && t <= lines[i].end + 0.4f) cur = i;
            if (cur != _lastCur)
            {
                // the old current line flies away in the "prev" slot
                if (_lastCur >= 0) { SetLine(_prev, _lastCur); }
                _lastCur = cur; _curSince = t;
            }
            SetLine(_cur, cur);
            // current line: slide up + fade in, then fade out after its end
            if (cur >= 0)
            {
                var L = lines[cur];
                float inA = Ease((t - (L.start - lead)) / 0.3f);
                float outA = 1f - Mathf.Clamp01((t - (L.end + 0.1f)) / 0.3f);
                Animate(_cur, t, inA * outA, new Vector3(0f, -0.45f * sz * (1f - inA), 0f), 0.85f + 0.15f * inA, false);
            }
            // previous line: drifts up and fades quickly
            if (_prev.line >= 0)
            {
                float a = Mathf.Clamp01((t - _curSince) / 0.3f);
                if (a >= 1f || _prev.line == cur) SetLine(_prev, -1);
                else Animate(_prev, t, (1f - a) * 0.8f, new Vector3(0f, 0.9f * sz * Ease(a), 0f), 1f - 0.15f * a, true);
            }
            // preview of the upcoming line, small and dim under the current one
            int nx = -1;
            for (int i = 0; i < lines.Length; i++)
                if (lines[i].start - lead > t) { if (lines[i].start - lead - t < 1.6f) nx = i; break; }
            SetLine(_next, nx);
            if (nx >= 0)
            {
                float a = Ease(1f - (lines[nx].start - lead - t) / 1.6f);
                Animate(_next, t, 0.45f * a, new Vector3(0f, -0.95f * sz, 0f), 1f, true);
            }
            // ---- hearts (texture based) ----
            float hs = 0f, ha = 0f;
            if (_lyrics.hearts != null)
                foreach (var h in _lyrics.hearts)
                {
                    float d = t - h;
                    if (d < -0.15f || d > 2.2f) continue;
                    ha = Mathf.Clamp01((d + 0.15f) / 0.2f) * (1f - Mathf.Clamp01((d - 1.7f) / 0.5f));
                    float beat = Mathf.Repeat(d, 0.55f);
                    hs = Mathf.Lerp(1.6f, 1f, Ease((d + 0.15f) / 0.25f)) + 0.18f * Mathf.Exp(-beat * 9f) + 0.1f * Mathf.Exp(-Mathf.Abs(beat - 0.16f) * 14f);
                    break;
                }
            // heart explosions: deterministic in song time so pausing / seeking stays correct
            float bd = -1f;
            if (_lyrics.bursts != null)
                foreach (var b in _lyrics.bursts) if (t >= b && t < b + 2.4f) { bd = t - b; break; }
            var P = _fx.Particles;
            for (int i = 0; i < P.Length; i++)
            {
                var img = P[i];
                if (bd < 0f) { HeartFx.Show(img, false); continue; }
                HeartFx.Show(img, true);
                float ang = (i / (float)P.Length) * Mathf.PI * 2f + Hash(i, 1) * 0.5f;
                float spd = (2.6f + 3.4f * Hash(i, 2)) * sz;
                float d = bd * (1f - 0.18f * bd);   // drag
                var pos = new Vector3(Mathf.Cos(ang) * spd * d, Mathf.Sin(ang) * spd * d * 0.8f + 1.4f * bd - 1.7f * bd * bd + 1.15f * sz, -0.3f * Hash(i, 4));
                img.rectTransform.localPosition = pos;
                img.rectTransform.localRotation = Quaternion.Euler(0f, 0f, (Hash(i, 5) - 0.5f) * 260f * bd);
                float a = Mathf.Clamp01(bd / 0.06f) * (1f - Mathf.Clamp01((bd - 1.3f) / 1.0f));
                img.rectTransform.localScale = Vector3.one * (0.6f + 0.8f * Hash(i, 3)) * Mathf.Lerp(0.3f, 1f, Ease(bd / 0.25f)) * (1f - 0.35f * Mathf.Clamp01(bd / 2.4f));
                Color c = Color.Lerp(_hi, i % 3 == 0 ? Color.white : new Color(1f, 0.25f, 0.55f), Hash(i, 6) * 0.6f);
                c.a = a; img.color = c;
            }
            if (bd >= 0f && ha <= 0f)
            {
                // big centre heart "explodes" open and keeps beating
                ha = 1f - Mathf.Clamp01((bd - 1.5f) / 0.8f);
                hs = Mathf.Lerp(0.2f, 1.6f, Ease(bd / 0.18f)) - 0.35f * Ease((bd - 0.18f) / 0.5f) + 0.15f * Mathf.Exp(-Mathf.Repeat(bd, 0.5f) * 8f);
            }
            HeartFx.Show(_fx.Big, ha > 0.001f);
            if (ha > 0.001f)
            {
                _fx.Big.rectTransform.localPosition = new Vector3(0f, 1.15f * sz, 0f);
                _fx.Big.rectTransform.localScale = Vector3.one * hs;
                var hc = _hi; hc.a = ha; _fx.Big.color = hc;
            }
            RenderIntroOutro(t);
            RenderDust(t);
            RenderShow(t);
            RenderHeartbeat(t);
        }
    }
}
