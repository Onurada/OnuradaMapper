using System;
using UnityEngine;
using UnityEngine.UI;

namespace OnuradaLyrics
{
    [Serializable]
    public class DustInfo { public float[][] ranges; public float[][] bursts; public float[] beats; public bool cut; public string[] colors; public float[][] dream; public string[] dream_colors; public DustText[] texts; }

    /// <summary>A set of soft glowing dots on one world-space canvas (same approach as HeartFx).</summary>
    internal class DustCloud
    {
        public readonly RawImage[] P;
        readonly GameObject _go;
        static Texture2D _tex;
        internal static Texture2D DotTexture => _tex != null ? _tex : (_tex = MakeDotTexture(64));

        public DustCloud(Transform parent, int layer, int count, string name)
        {
            if (_tex == null) _tex = MakeDotTexture(64);
            _go = new GameObject(name);
            _go.layer = layer;
            _go.transform.SetParent(parent, false);
            var canvas = _go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 4;
            ((RectTransform)_go.transform).sizeDelta = new Vector2(1f, 1f);
            var mat = new Material(Canvas.GetDefaultCanvasMaterial());
            if (mat.HasProperty("_ColorMask")) mat.SetFloat("_ColorMask", 14f);
            P = new RawImage[count];
            for (int i = 0; i < count; i++)
            {
                var go = new GameObject("D" + i);
                go.layer = layer;
                go.transform.SetParent(_go.transform, false);
                var img = go.AddComponent<RawImage>();
                img.texture = _tex; img.material = mat; img.raycastTarget = false;
                img.rectTransform.sizeDelta = new Vector2(1f, 1f);
                img.color = new Color(1, 1, 1, 0);
                P[i] = img;
            }
            _go.SetActive(false);
        }

        public bool Active { set { if (_go.activeSelf != value) _go.SetActive(value); } }

        /// <summary>White dot with a bright core and a wide soft halo, tinted by RawImage.color.</summary>
        static Texture2D MakeDotTexture(int n)
        {
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear };
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float u = ((x + 0.5f) / n - 0.5f) * 2f, v = ((y + 0.5f) / n - 0.5f) * 2f;
                    float r = Mathf.Sqrt(u * u + v * v);
                    float core = Mathf.Clamp01((0.28f - r) / 0.12f);
                    float halo = Mathf.Exp(-r * r * 9f) * 0.75f * Mathf.Clamp01((1f - r) / 0.15f);
                    float a = Mathf.Max(core, halo);
                    px[y * n + x] = new Color32(255, 255, 255, (byte)(a * 255));
                }
            tex.SetPixels32(px);
            tex.Apply(true, true);
            return tex;
        }

        public void Destroy() { if (_go != null) UnityEngine.Object.Destroy(_go); }
    }

    /// <summary>"Toz pembe" dust: floating motes during the given ranges (pulsing on the beat), big puffs on hooks / drops, small puffs on every good cut.</summary>
    public partial class Plugin
    {
        const int AmbientMotes = 110, BurstMotes = 110, CutPuffs = 10, PerPuff = 12;
        DustCloud _dustAmb, _dustBurst, _dustCut;
        Color[] _dustCols, _dreamCols;
        struct Puff { public float t; public Vector3 p; public int c; public float s; public int seed; }
        readonly Puff[] _puffs = new Puff[CutPuffs];
        int _puffCount;

        void InitDust(int layer)
        {
            _dustAmb = _dustBurst = _dustCut = null;
            _puffCount = 0;
            for (int k = 0; k < CutPuffs; k++) _puffs[k] = new Puff { t = -99f };
            var d = _lyrics.dust;
            if (d == null) return;
            var cols = d.colors ?? new string[0];
            _dustCols = new Color[3];
            for (int i = 0; i < 3; i++)
                _dustCols[i] = i < cols.Length && ColorUtility.TryParseHtmlString("#" + cols[i], out var c) ? c : (i == 2 ? Color.white : _hi);
            var dcs = d.dream_colors ?? new string[0];
            _dreamCols = new Color[3];
            for (int i = 0; i < 3; i++) _dreamCols[i] = i < dcs.Length && ColorUtility.TryParseHtmlString("#" + dcs[i], out var c) ? c : _dustCols[i];
            _dustAmb = new DustCloud(_root.transform, layer, AmbientMotes, "OnuradaDustAmbient");
            _dustBurst = new DustCloud(_root.transform, layer, BurstMotes, "OnuradaDustBurst");
            if (d.cut) _dustCut = new DustCloud(_root.transform, layer, CutPuffs * PerPuff, "OnuradaDustCut");
        }

        /// called from the NoteCutCoreEffectsSpawner patch for every good cut
        internal void AddCutPuff(Vector3 worldPos, int colorType, bool chainLink)
        {
            if (_dustCut == null || _root == null || _atsc == null) return;
            float st;
            try { st = (float)_songTimeProp.GetValue(_atsc, null); } catch { return; }
            _puffs[_puffCount % CutPuffs] = new Puff { t = st, p = _root.transform.InverseTransformPoint(worldPos), c = colorType == 1 ? 1 : 0, s = chainLink ? 0.55f : 1f, seed = _puffCount };
            _puffCount++;
        }

        static Vector3 SphereDir(int i, int k)
        {
            float u = Hash(i, k) * 2f - 1f, ph = Hash(i, k + 1) * Mathf.PI * 2f, r = Mathf.Sqrt(1f - u * u);
            return new Vector3(r * Mathf.Cos(ph), u, r * Mathf.Sin(ph));
        }

        void RenderDust(float t)
        {
            var d = _lyrics.dust;
            if (d == null || _dustAmb == null) return;
            // ---- ambient motes ----
            float amb = 0f, dream = 0f, slowed = 0f;
            if (d.ranges != null)
                foreach (var r in d.ranges)
                    if (r.Length >= 2) amb = Mathf.Max(amb, Mathf.Clamp01((t - r[0]) / 1.5f) * Mathf.Clamp01((r[1] - t) / 1.5f));
            if (d.dream != null)
                foreach (var r in d.dream)
                    if (r.Length >= 2)
                    {
                        dream = Mathf.Max(dream, Mathf.Clamp01((t - r[0]) / 2f) * Mathf.Clamp01((r[1] - t) / 0.4f));
                        slowed += Mathf.Clamp(t - r[0], 0f, r[1] - r[0]);
                    }
            amb = Mathf.Max(amb, dream);
            float tm = t - 0.75f * slowed;          // motion clock: dust drifts at a quarter speed in dream sections
            float grey = BreakGrey(t);
            float heart = HeartPulse(t) * dream;   // dream dust throbs with the heartbeat
            _dustAmb.Active = amb > 0.001f;
            if (amb > 0.001f)
            {
                float bp = 0f;
                if (d.beats != null && d.beats.Length > 0)
                {
                    int i0 = Array.BinarySearch(d.beats, t);
                    if (i0 < 0) i0 = ~i0 - 1;
                    if (i0 >= 0 && t - d.beats[i0] < 1f) bp = Mathf.Exp(-(t - d.beats[i0]) * 7f);
                }
                bp = Mathf.Max(bp, heart);
                var P = _dustAmb.P;
                for (int i = 0; i < P.Length; i++)
                {
                    float z = -5f + Hash(i, 13) * 19f;
                    float x = (Hash(i, 11) - 0.5f) * 22f;
                    if (z < 7f && Mathf.Abs(x) < 2.6f) x = Mathf.Sign(x) * (2.6f + Mathf.Abs(x) * 0.8f);   // keep the note lanes clear
                    float y = -2.6f + Mathf.Repeat(Hash(i, 12) * 7.5f + tm * (0.12f + 0.18f * Hash(i, 14)), 7.5f);
                    x += 0.35f * Mathf.Sin(tm * (0.3f + 0.4f * Hash(i, 15)) + i);
                    z += 0.25f * Mathf.Sin(tm * 0.27f + i * 1.7f);
                    float fade = Mathf.Clamp01((y + 2.6f) / 0.8f) * Mathf.Clamp01((4.9f - y) / 0.8f);
                    float tw = 0.55f + 0.45f * Mathf.Sin(t * (1.5f + 2f * Hash(i, 16)) + i * 2.1f);
                    var img = P[i];
                    img.rectTransform.localPosition = new Vector3(x, y, z);
                    img.rectTransform.localScale = Vector3.one * (0.07f + 0.13f * Hash(i, 17)) * (1f + 0.35f * bp) * (1f + 0.6f * dream);
                    var c = Color.Lerp(_dustCols[i % 3], _dreamCols[i % 3], dream);
                    c = Color.Lerp(c, new Color(0.55f, 0.55f, 0.58f), grey);
                    c.a = amb * fade * tw * (0.55f + 0.45f * bp) * 0.85f; img.color = c;
                }
            }
            // ---- big puffs on hooks / drops (latest one wins) ----
            float bd = -1f, bs = 1f;
            if (d.bursts != null)
                foreach (var b in d.bursts)
                    if (b.Length >= 1 && t >= b[0] && t < b[0] + 3.2f) { bd = t - b[0]; bs = b.Length >= 2 ? b[1] : 1f; }
            _dustBurst.Active = bd >= 0f;
            if (bd >= 0f)
            {
                var P = _dustBurst.P;
                int n = Mathf.Min(P.Length, Mathf.RoundToInt(P.Length * Mathf.Clamp01(0.45f + 0.35f * bs)));
                float dist = (1f - Mathf.Exp(-bd * 2.6f)) / 2.6f;   // integrated drag
                for (int i = 0; i < P.Length; i++)
                {
                    var img = P[i];
                    if (i >= n) { img.color = new Color(0, 0, 0, 0); continue; }
                    var dir = SphereDir(i, 21); dir.y *= 0.8f; dir.z *= 0.6f;
                    float spd = (2.2f + 3.2f * Hash(i, 23)) * bs;
                    var pos = new Vector3(0f, 0.2f, 2f) + dir * spd * dist
                              + new Vector3(0.3f * Mathf.Sin(bd * 1.3f + i), -0.12f * bd * bd, 0.2f * Mathf.Cos(bd * 1.1f + i));
                    img.rectTransform.localPosition = pos;
                    img.rectTransform.localScale = Vector3.one * (0.08f + 0.16f * Hash(i, 24)) * Mathf.Sqrt(bs) * (1f + 0.6f * Mathf.Exp(-bd * 5f));
                    float tw = 0.7f + 0.3f * Mathf.Sin(t * (3f + 3f * Hash(i, 25)) + i);
                    var c = Color.Lerp(_dustCols[i % 3], Color.white, Mathf.Exp(-bd * 6f) * 0.6f);
                    c.a = Mathf.Clamp01(bd / 0.05f) * (1f - Mathf.Clamp01((bd - 1.2f) / 2.0f)) * tw;
                    img.color = c;
                }
            }
            // ---- small puffs where notes are cut ----
            if (_dustCut == null) return;
            bool any = false;
            var Q = _dustCut.P;
            for (int k = 0; k < CutPuffs; k++)
            {
                var pf = _puffs[k];
                float pd = t - pf.t;
                bool live = pd >= 0f && pd < 1.3f;
                any |= live;
                for (int j = 0; j < PerPuff; j++)
                {
                    var img = Q[k * PerPuff + j];
                    if (!live) { img.color = new Color(0, 0, 0, 0); continue; }
                    int h = pf.seed * PerPuff + j;
                    float spd = (0.7f + 1.2f * Hash(h, 31)) * pf.s;
                    img.rectTransform.localPosition = pf.p + SphereDir(h, 32) * spd * (1f - Mathf.Exp(-pd * 3.5f)) / 3.5f + new Vector3(0f, 0.25f * pd, 0f);
                    img.rectTransform.localScale = Vector3.one * (0.04f + 0.06f * Hash(h, 34)) * pf.s * (1f + 0.5f * Mathf.Exp(-pd * 8f));
                    var c = Color.Lerp(_dustCols[pf.c], Color.white, 0.25f + 0.35f * Hash(h, 35));
                    c.a = Mathf.Clamp01(pd / 0.04f) * (1f - Mathf.Clamp01((pd - 0.4f) / 0.9f)) * 0.9f;
                    img.color = c;
                }
            }
            _dustCut.Active = any;
        }

        static void CutPostfix(in NoteCutInfo noteCutInfo, NoteController noteController)
        {
            try
            {
                if (Instance == null || !noteCutInfo.allIsOK) return;
                var nd = noteController.noteData;
                Instance.AddCutPuff(noteCutInfo.cutPoint, (int)nd.colorType, nd.gameplayType == NoteData.GameplayType.BurstSliderElement);
            }
            catch (Exception e) { Instance?.Logger.LogError(e); }
        }
    }
}
