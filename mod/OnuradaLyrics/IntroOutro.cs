using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

namespace OnuradaLyrics
{
    [Serializable]
    public class IntroInfo { public string artist; public string title; public float start; public float end; }

    [Serializable]
    public class OutroInfo { public string text; public float start; public float end; }

    /// <summary>Per-character animated text (drop-in / pop-in), driven by song time.</summary>
    internal class CharAnim
    {
        public TextMeshPro tmp;
        TMP_MeshInfo[] _cache;

        public CharAnim(TextMeshPro t, string text)
        {
            tmp = t; tmp.text = text; tmp.ForceMeshUpdate();
            _cache = tmp.textInfo.CopyMeshInfoVertexData();
        }

        /// mode 0 = drop from above with bounce, 1 = pop in place
        public void Apply(float t, float t0, float stagger, float fadeStart, float fadeEnd, Color hi, int mode)
        {
            var ti = tmp.textInfo;
            float outA = 1f - Mathf.Clamp01((t - fadeStart) / Mathf.Max(0.01f, fadeEnd - fadeStart));
            int vis = 0;
            for (int i = 0; i < ti.characterCount; i++)
            {
                var ci = ti.characterInfo[i];
                if (!ci.isVisible) continue;
                int m = ci.materialReferenceIndex, v = ci.vertexIndex;
                var src = _cache[m].vertices; var dst = ti.meshInfo[m].vertices; var cols = ti.meshInfo[m].colors32;
                float d = t - (t0 + vis * stagger);
                vis++;
                float a = Mathf.Clamp01(d / 0.12f) * outA;
                float y = 0f, sc = 1f;
                if (mode == 0)
                {
                    float e = Mathf.Clamp01(d / 0.38f);
                    float bounce = 1f - Mathf.Abs(Mathf.Cos(e * Mathf.PI * 1.5f)) * (1f - e);
                    y = (1f - bounce) * 2.2f;
                    sc = 1f + 0.35f * Mathf.Exp(-Mathf.Pow((d - 0.38f) / 0.1f, 2f));
                }
                else
                {
                    sc = d < 0f ? 0f : Mathf.Lerp(1.8f, 1f, Mathf.Clamp01(d / 0.25f)) + 0.04f * Mathf.Sin(t * 3f + i);
                }
                y += 0.08f * Mathf.Sin(t * 2.2f + i * 0.5f) * Mathf.Clamp01(d) + (t > fadeStart ? (t - fadeStart) * 1.2f : 0f);
                Vector3 c = (src[v] + src[v + 2]) * 0.5f;
                for (int q = 0; q < 4; q++) dst[v + q] = c + (src[v + q] - c) * sc + new Vector3(0f, y, 0f);
                // colour sweeps from highlight to white
                Color col = Color.Lerp(hi, Color.white, Mathf.Clamp01((d - 0.35f) / 0.6f) * 0.55f);
                col.a = d < 0f ? 0f : a;
                Color32 c32 = col;
                for (int q = 0; q < 4; q++) cols[v + q] = c32;
            }
            tmp.UpdateVertexData(TMP_VertexDataUpdateFlags.Vertices | TMP_VertexDataUpdateFlags.Colors32);
        }
    }

    public partial class Plugin
    {
        CharAnim _introTitle, _introArtist, _outroText;
        HeartFx _rain;

        void InitIntroOutro(TMP_FontAsset font, int layer)
        {
            float s = cfgSize.Value;
            _introTitle = _introArtist = _outroText = null;
            if (_lyrics.intro != null && !string.IsNullOrEmpty(_lyrics.intro.title))
            {
                var tt = MakeText("IntroTitle", s * 1.9f, font); tt.transform.localPosition = new Vector3(0f, 0.5f, 0f);
                _introTitle = new CharAnim(tt, _lyrics.intro.title);
                if (!string.IsNullOrEmpty(_lyrics.intro.artist))
                {
                    var ta = MakeText("IntroArtist", s * 0.7f, font); ta.transform.localPosition = new Vector3(0f, 1.9f, 0f);
                    _introArtist = new CharAnim(ta, _lyrics.intro.artist);
                }
                // heart explosion right when the title has landed
                float land = _lyrics.intro.start + 0.6f + _lyrics.intro.title.Length * 0.09f + 0.3f;
                _lyrics.bursts = (_lyrics.bursts ?? new float[0]).Concat(new[] { land }).ToArray();
            }
            if (_lyrics.outro != null)
            {
                if (!string.IsNullOrEmpty(_lyrics.outro.text) && !HasDustText("wind"))   // dust-formed outro text replaces the plain one
                {
                    var to = MakeText("OutroText", s * 1.3f, font); to.transform.localPosition = new Vector3(0f, 0.6f, 0f);
                    _outroText = new CharAnim(to, _lyrics.outro.text);
                }
                _rain = new HeartFx(_root.transform, layer, 44);
            }
            foreach (var tr in _root.GetComponentsInChildren<Transform>(true)) tr.gameObject.layer = layer;
        }

        void RenderIntroOutro(float t)
        {
            var it = _lyrics.intro;
            if (_introTitle != null)
            {
                bool on = t > it.start - 0.1f && t < it.end + 0.1f;
                bool titleOn = on && !HasDustText("burst");   // dust-formed title replaces the plain one
                if (_introTitle.tmp.gameObject.activeSelf != titleOn) _introTitle.tmp.gameObject.SetActive(titleOn);
                if (_introArtist != null && _introArtist.tmp.gameObject.activeSelf != on) _introArtist.tmp.gameObject.SetActive(on);
                if (on)
                {
                    _introArtist?.Apply(t, it.start, 0.045f, it.end - 0.7f, it.end, Color.white, 1);
                    _introTitle.Apply(t, it.start + 0.6f, 0.09f, it.end - 0.7f, it.end, _hi, 0);
                }
            }
            var ot = _lyrics.outro;
            if (ot == null) return;
            if (_outroText != null)
            {
                bool on = t > ot.start + 1.2f && t < ot.end + 0.1f;
                if (_outroText.tmp.gameObject.activeSelf != on) _outroText.tmp.gameObject.SetActive(on);
                if (on) _outroText.Apply(t, ot.start + 1.5f, 0.07f, ot.end - 1.0f, ot.end, _hi, 1);
            }
            if (_rain == null) return;
            var P = _rain.Particles;
            bool rainOn = t >= ot.start && t <= ot.end;
            const float period = 3.4f;
            for (int i = 0; i < P.Length; i++)
            {
                var img = P[i];
                if (!rainOn) { HeartFx.Show(img, false); continue; }
                float ph = Hash(i, 7) * period;
                float u = t - ot.start + ph;
                float since = t - ot.start;
                if (since < ph * 0.35f) { HeartFx.Show(img, false); continue; }   // stagger the first wave
                u = Mathf.Repeat(u, period);
                HeartFx.Show(img, true);
                float fall = 2.4f + 1.4f * Hash(i, 8);
                float x = (Hash(i, 1) - 0.5f) * 18f + 0.5f * Mathf.Sin(u * 1.7f + i);
                float z = -2f + Hash(i, 2) * 12f;
                float y = 5.0f - u * fall;
                img.rectTransform.localPosition = new Vector3(x, y, z);
                img.rectTransform.localRotation = Quaternion.Euler(0f, 0f, 25f * Mathf.Sin(u * 2.3f + i));
                img.rectTransform.localScale = Vector3.one * (0.7f + 1.1f * Hash(i, 3));
                float a = Mathf.Clamp01(u / 0.3f) * Mathf.Clamp01((period - u) / 0.4f) * (1f - Mathf.Clamp01((t - (ot.end - 1.5f)) / 1.5f));
                Color c = Color.Lerp(_hi, i % 4 == 0 ? Color.white : new Color(1f, 0.3f, 0.6f), Hash(i, 6) * 0.6f);
                c.a = a * 0.9f; img.color = c;
            }
        }
    }
}
