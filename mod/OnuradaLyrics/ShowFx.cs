using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace OnuradaLyrics
{
    [Serializable]
    public class WColors { public string scheme; public string w; public string wb; public string[] rainbow; }

    [Serializable]
    public class ComboInfo { public int[] milestones; public float fc; }

    [Serializable]
    public class DustText { public float start; public float end; public float y; public string exit; public float[] px; public float[] py; }

    /// <summary>Many soft dots drawn as one mesh (one UI graphic instead of hundreds of RawImages).</summary>
    internal class DotField : MaskableGraphic
    {
        public Texture tex;
        public Vector3[] pos;
        public Color32[] col;
        public float[] size;

        public override Texture mainTexture => tex;

        public static DotField Create(Transform parent, int layer, int count, string name, Material mat, Texture tex)
        {
            var go = new GameObject(name);
            go.layer = layer;
            go.transform.SetParent(parent, false);
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 4;
            ((RectTransform)go.transform).sizeDelta = new Vector2(1f, 1f);
            var child = new GameObject("Dots");
            child.layer = layer;
            child.transform.SetParent(go.transform, false);
            var f = child.AddComponent<DotField>();
            f.raycastTarget = false;
            f.material = mat;
            f.tex = tex;
            f.rectTransform.sizeDelta = new Vector2(1f, 1f);
            f.pos = new Vector3[count]; f.col = new Color32[count]; f.size = new float[count];
            go.SetActive(false);
            return f;
        }

        public bool Active { set { var g = transform.parent.gameObject; if (g.activeSelf != value) g.SetActive(value); } }

        public void Push() { SetVerticesDirty(); }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            if (pos == null) return;
            var v = UIVertex.simpleVert;
            int q = 0;
            for (int i = 0; i < pos.Length; i++)
            {
                if (col[i].a == 0 || size[i] <= 0f) continue;
                float h = size[i] * 0.5f; var p = pos[i];
                v.color = col[i];
                v.position = new Vector3(p.x - h, p.y - h, p.z); v.uv0 = new Vector4(0, 0, 0, 0); vh.AddVert(v);
                v.position = new Vector3(p.x - h, p.y + h, p.z); v.uv0 = new Vector4(0, 1, 0, 0); vh.AddVert(v);
                v.position = new Vector3(p.x + h, p.y + h, p.z); v.uv0 = new Vector4(1, 1, 0, 0); vh.AddVert(v);
                v.position = new Vector3(p.x + h, p.y - h, p.z); v.uv0 = new Vector4(1, 0, 0, 0); vh.AddVert(v);
                vh.AddTriangle(q, q + 1, q + 2); vh.AddTriangle(q + 2, q + 3, q);
                q += 4;
            }
        }
    }

    /// <summary>Six-colour lights (W colour restore), dust-formed titles, combo milestones and the full-combo finale.</summary>
    public partial class Plugin
    {
        internal static Color? WCol, WBCol;
        Material _dotMat;
        DotField[] _textFields = new DotField[0];
        DotField _ring;
        Color[] _rainbow;
        CharAnim _comboText, _fcText;
        TMP_FontAsset _font;
        int _layer;
        HeartFx _fcRain;
        ComboController _comboCtl;
        float _nextComboSearch, _ringAt = -99f, _comboAt = -99f, _breakAt = -99f, _fcAt = -99f;
        int _lastMilestone, _maxComboSeen;
        bool _broken;

        // ---------------- six colours: the game forces W to white for custom levels ----------------
        static void ColorForTypePostfix(EnvironmentColorType type, bool boost, ref Color __result)
        {
            if (type == EnvironmentColorType.ColorW && WCol.HasValue) __result = boost ? WBCol.Value : WCol.Value;
        }

        static Color Hex(string h, Color fallback) => !string.IsNullOrEmpty(h) && ColorUtility.TryParseHtmlString("#" + h, out var c) ? c : fallback;

        internal void ApplyWColors(LyricFile lf)
        {
            WCol = WBCol = null;
            if (lf?.wcolors == null || string.IsNullOrEmpty(lf.wcolors.w)) return;
            WCol = Hex(lf.wcolors.w, Color.white); WBCol = Hex(lf.wcolors.wb, WCol.Value);
        }

        /// called from the level setup patch, before the gameplay scene builds its lights
        internal void PrepareLevel(string name)
        {
            WCol = WBCol = null;
            try
            {
                name = (name ?? "").Trim();
                if (!_index.ContainsKey(name)) BuildIndex();
                if (_index.TryGetValue(name, out var path)) ApplyWColors(MiniJson.ToLyrics(System.IO.File.ReadAllText(path, System.Text.Encoding.UTF8)));
            }
            catch (Exception e) { Logger.LogError(e); }
        }

        // ---------------- setup ----------------
        void InitShow(TMP_FontAsset font, int layer)
        {
            _font = font; _layer = layer;
            _textFields = new DotField[0]; _ring = null; _comboText = _fcText = null; _fcRain = null; _comboCtl = null;
            _ringAt = _comboAt = _breakAt = _fcAt = -99f; _lastMilestone = 0; _maxComboSeen = 0; _broken = false;
            ApplyWColors(_lyrics);
            var rb = _lyrics.wcolors?.rainbow ?? new string[0];
            var def = new[] { "FF4FA3", "FF1F3D", "FFD21F", "2BFF6A", "2E7BFF", "9B3DFF" };
            _rainbow = Enumerable.Range(0, 6).Select(i => Hex(i < rb.Length ? rb[i] : def[i], Color.white)).ToArray();
            _dotMat = new Material(Canvas.GetDefaultCanvasMaterial());
            if (_dotMat.HasProperty("_ColorMask")) _dotMat.SetFloat("_ColorMask", 14f);
            var tex = DustCloud.DotTexture;
            var texts = _lyrics.dust?.texts ?? new DustText[0];
            _textFields = texts.Select((d, i) => DotField.Create(_root.transform, layer, d.px.Length, "OnuradaDustText" + i, _dotMat, tex)).ToArray();
            if (_lyrics.combo != null)
            {
                _ring = DotField.Create(_root.transform, layer, 180, "OnuradaComboRing", _dotMat, tex);
                _fcRain = new HeartFx(_root.transform, layer, 40);
            }
        }

        bool HasDustText(string exit) => _lyrics?.dust?.texts != null && _lyrics.dust.texts.Any(x => x.exit == exit);

        // ---------------- combo ----------------
        void FindCombo()
        {
            if (_comboCtl != null || _lyrics?.combo == null || Time.unscaledTime < _nextComboSearch) return;
            _nextComboSearch = Time.unscaledTime + 0.25f;
            var c = FindObjectsByType<ComboController>(FindObjectsSortMode.None);
            if (c == null || c.Length == 0) return;
            _comboCtl = c[0];
            _comboCtl.comboDidChangeEvent += OnComboChanged;
            _comboCtl.comboBreakingEventHappenedEvent += OnComboBroken;
            Logger.LogInfo("Combo controller hooked");
        }

        float SongTime() { try { return (float)_songTimeProp.GetValue(_atsc, null); } catch { return 0f; } }

        void OnComboChanged(int combo)
        {
            if (_root == null || _lyrics?.combo?.milestones == null) return;
            _maxComboSeen = Math.Max(_maxComboSeen, combo);
            if (combo <= _lastMilestone || Array.IndexOf(_lyrics.combo.milestones, combo) < 0) return;
            _lastMilestone = combo;
            _comboAt = _ringAt = SongTime();
            if (_comboText != null) Destroy(_comboText.tmp.gameObject);
            var tt = MakeText("ComboText", cfgSize.Value * 0.8f, _font);
            tt.gameObject.layer = _layer; tt.transform.localPosition = new Vector3(0f, 2.1f, 0f);
            _comboText = new CharAnim(tt, combo + " KOMBO");
        }

        void OnComboBroken()
        {
            if (_root == null) return;
            _breakAt = SongTime(); _broken = true; _lastMilestone = 0;
        }

        // ---------------- render ----------------
        void RenderShow(float t)
        {
            RenderDustTexts(t);
            if (_lyrics.combo == null) return;
            // milestone: number pops above the lyrics, a six-colour dust ring expands around it
            if (_comboText != null)
            {
                bool on = t >= _comboAt && t < _comboAt + 1.7f;
                if (_comboText.tmp.gameObject.activeSelf != on) _comboText.tmp.gameObject.SetActive(on);
                if (on) _comboText.Apply(t, _comboAt, 0.035f, _comboAt + 1.2f, _comboAt + 1.7f, _rainbow[(_lastMilestone / 50) % 6], 1);
            }
            // full combo: once, after the last note, if the combo never broke
            if (_fcAt < 0f && !_broken && _maxComboSeen > 0 && t >= _lyrics.combo.fc)
            {
                _fcAt = _ringAt = t;
                var ft = MakeText("FullCombo", cfgSize.Value * 1.5f, _font);
                ft.gameObject.layer = _layer; ft.transform.localPosition = new Vector3(0f, 2.3f, 0f);
                _fcText = new CharAnim(ft, "FULL COMBO");
            }
            if (_fcText != null)
            {
                bool on = t >= _fcAt && t < _fcAt + 7f;
                if (_fcText.tmp.gameObject.activeSelf != on) _fcText.tmp.gameObject.SetActive(on);
                if (on) _fcText.Apply(t, _fcAt, 0.07f, _fcAt + 6f, _fcAt + 7f, _rainbow[(int)((t - _fcAt) * 4f) % 6], 0);
                var P = _fcRain.Particles;
                for (int i = 0; i < P.Length; i++)
                {
                    float u = t - _fcAt - Hash(i, 51) * 1.5f;
                    bool vis = u > 0f && u < 4.5f && t < _fcAt + 7f;
                    HeartFx.Show(P[i], vis);
                    if (!vis) continue;
                    P[i].rectTransform.localPosition = new Vector3((Hash(i, 52) - 0.5f) * 14f, 6f - u * (2f + Hash(i, 53)), -1f + Hash(i, 54) * 6f);
                    P[i].rectTransform.localRotation = Quaternion.Euler(0f, 0f, 30f * Mathf.Sin(u * 2f + i));
                    P[i].rectTransform.localScale = Vector3.one * (0.8f + Hash(i, 55));
                    var c = _rainbow[i % 6]; c.a = Mathf.Clamp01(u / 0.3f) * Mathf.Clamp01((4.5f - u) / 0.6f); P[i].color = c;
                }
            }
            float rd = t - _ringAt;
            bool ringOn = rd >= 0f && rd < 1.6f;
            _ring.Active = ringOn;
            if (ringOn)
            {
                bool big = _fcAt >= 0f && Mathf.Abs(_ringAt - _fcAt) < 0.01f;
                float R = (big ? 6f : 3.2f) * (1f - Mathf.Exp(-rd * 4f));
                for (int i = 0; i < _ring.pos.Length; i++)
                {
                    float ang = i / (float)_ring.pos.Length * Mathf.PI * 2f + Hash(i, 61) * 0.05f;
                    float r = R * (0.85f + 0.3f * Hash(i, 62));
                    _ring.pos[i] = new Vector3(Mathf.Cos(ang) * r, 2.1f + Mathf.Sin(ang) * r * 0.75f, 0.2f * Hash(i, 63));
                    _ring.size[i] = (0.1f + 0.08f * Hash(i, 64)) * (big ? 1.4f : 1f);
                    Color c = _rainbow[(i * 6) / _ring.pos.Length]; c.a = Mathf.Clamp01(rd / 0.05f) * (1f - Mathf.Clamp01((rd - 0.6f) / 1.0f));
                    _ring.col[i] = c;
                }
                _ring.Push();
            }
        }

        /// grey-out factor for the ambient dust right after a combo break
        float BreakGrey(float t) => t >= _breakAt && t - _breakAt < 2f ? 0.85f * Mathf.Exp(-(t - _breakAt) * 1.6f) : 0f;

        void RenderDustTexts(float t)
        {
            var texts = _lyrics.dust?.texts;
            if (texts == null) return;
            for (int k = 0; k < texts.Length && k < _textFields.Length; k++)
            {
                var d = texts[k]; var f = _textFields[k];
                bool on = t > d.start - 0.1f && t < d.end + 0.1f;
                f.Active = on;
                if (!on) continue;
                float el = t - d.start;
                bool wind = d.exit == "wind";
                float exitDur = wind ? 2.6f : 1.1f, exitStart = d.end - exitDur;
                for (int i = 0; i < d.px.Length; i++)
                {
                    var target = new Vector3(d.px[i], d.py[i] + d.y, 0f);
                    var origin = (wind ? new Vector3(0f, 4f, 2f) : new Vector3(0f, 0.5f, 3f)) + SphereDir(i, 41) * (4f + 6f * Hash(i, 42));
                    float st = Hash(i, 43) * 0.7f;
                    float a = Ease(Mathf.Clamp01((el - st) / 1.3f));
                    var swirl = new Vector3(Mathf.Sin(t * 2.3f + i), Mathf.Cos(t * 1.9f + i * 1.3f), 0f) * 0.8f * (1f - a);
                    var p = Vector3.Lerp(origin, target, a) + swirl + new Vector3(0f, 0.02f * Mathf.Sin(t * 3f + i), 0f);
                    float alpha = Mathf.Clamp01((el - st) / 0.35f);
                    float sz = 0.075f * (0.75f + 0.55f * Hash(i, 44)) * (1f + 0.8f * (1f - a));
                    if (t > exitStart)
                    {
                        if (wind)
                        {
                            // blown away left to right, like dust off a shelf
                            float e2 = Mathf.Clamp01((t - exitStart - (d.px[i] + 6f) / 12f * 1.2f) / 1.3f);
                            p += new Vector3(e2 * e2 * 7f, e2 * 1.6f + 0.4f * Mathf.Sin(e2 * 6f + i), -e2 * 1.5f);
                            alpha *= 1f - e2;
                        }
                        else
                        {
                            float ex = Mathf.Clamp01((t - exitStart) / exitDur);
                            p += (SphereDir(i, 45) + new Vector3(target.x * 0.15f, 0f, 0f)) * 7f * (1f - Mathf.Exp(-ex * 3f));
                            alpha *= 1f - ex * ex;
                            sz *= 1f + ex;
                        }
                    }
                    float tw = 0.75f + 0.25f * Mathf.Sin(t * (3f + 4f * Hash(i, 46)) + i);
                    Color c = Color.Lerp(_dustCols != null ? _dustCols[i % 3] : _hi, Color.white, 0.25f * tw + 0.4f * (1f - a));
                    c.a = alpha * tw;
                    f.pos[i] = p; f.size[i] = sz; f.col[i] = c;
                }
                f.Push();
            }
        }
    }
}
