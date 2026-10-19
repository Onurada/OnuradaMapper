using System;
using System.IO;
using System.Threading;
using UnityEngine;
using UnityEngine.UI;

namespace OnuradaLyrics
{
    [Serializable]
    public class VideoInfo { public string file; public float start; public float end; public float fps = 24f; public string mode = "pingpong"; public float x, y, z = 22f, height = 7f; }

    /// <summary>Plays a pre-compressed (BC3/DXT5) RGBA frame sequence on a world-space quad, synced to song time.</summary>
    internal class VideoBillboard
    {
        readonly VideoInfo _v;
        byte[][] _frames;
        int _w, _h;
        volatile bool _ready;
        Texture2D _tex;
        RawImage _img;
        GameObject _go;
        int _shown = -1;

        public VideoBillboard(VideoInfo v, string path, Transform parentRoot, int layer)
        {
            _v = v;
            var t = new Thread(() =>
            {
                try
                {
                    using (var br = new BinaryReader(File.OpenRead(path)))
                    {
                        if (new string(br.ReadChars(4)) != "OBC3") throw new Exception("bad header");
                        _w = br.ReadInt32(); _h = br.ReadInt32(); int n = br.ReadInt32(); br.ReadInt32();
                        var fr = new byte[n][];
                        for (int i = 0; i < n; i++) fr[i] = br.ReadBytes(_w * _h);
                        _frames = fr;
                    }
                    _ready = true;
                }
                catch (Exception e) { Plugin.Instance?.Log("Video load failed: " + e.Message); }
            }) { IsBackground = true };
            t.Start();

            _go = new GameObject("OnuradaVideo");
            _go.transform.SetParent(parentRoot, false);
            _go.layer = layer;
            var canvas = _go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var rt = _go.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(100f, 100f);
            rt.pivot = new Vector2(0.5f, 0f);
            var imgGo = new GameObject("Img");
            imgGo.layer = layer;
            imgGo.transform.SetParent(_go.transform, false);
            _img = imgGo.AddComponent<RawImage>();
            var mat = new Material(Canvas.GetDefaultCanvasMaterial());
            if (mat.HasProperty("_ColorMask")) mat.SetFloat("_ColorMask", 14f);   // RGB only: don't feed Beat Saber's alpha-driven bloom
            _img.material = mat;
            _img.raycastTarget = false;
            _img.color = new Color(1f, 1f, 1f, 0f);
            _go.SetActive(false);
        }

        static float Ease(float x) { x = Mathf.Clamp01(x); return 1f - (1f - x) * (1f - x) * (1f - x); }

        public void Update(float songTime, Transform worldRoot)
        {
            float local = songTime - _v.start;
            bool on = local >= -0.1f && songTime <= _v.end + 0.6f;
            if (!on || !_ready) { if (_go.activeSelf) _go.SetActive(false); return; }
            if (_tex == null)
            {
                _tex = new Texture2D(_w, _h, TextureFormat.DXT5, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                _img.texture = _tex;
                var rt = _img.rectTransform;
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f);
                rt.pivot = new Vector2(0.5f, 0f);
                rt.sizeDelta = new Vector2(_w, _h);
            }
            if (!_go.activeSelf) _go.SetActive(true);
            int n = _frames.Length;
            int idx = Mathf.Max(0, (int)(local * _v.fps));
            if (_v.mode == "pingpong" && n > 1) { int p = 2 * n - 2; idx %= p; if (idx >= n) idx = p - idx; }
            else idx %= n;
            if (idx != _shown)
            {
                _tex.LoadRawTextureData(_frames[idx]);
                _tex.Apply(false, false);
                _shown = idx;
            }
            // appear: pop + fade in, breathe, fade out at the end
            float inA = Ease((local + 0.1f) / 0.5f);
            float outA = 1f - Mathf.Clamp01((songTime - _v.end) / 0.6f);
            float a = inA * outA;
            float scale = (_v.height / _h) * (0.85f + 0.15f * inA) * (1f + 0.012f * Mathf.Sin(songTime * 2.4f));
            _go.transform.position = worldRoot.position * 0f + new Vector3(_v.x, _v.y + 0.12f * Mathf.Sin(songTime * 1.3f), _v.z);
            _go.transform.localScale = Vector3.one * scale;
            _img.color = new Color(1f, 1f, 1f, a);
        }

        public void Destroy()
        {
            if (_go != null) UnityEngine.Object.Destroy(_go);
            if (_tex != null) UnityEngine.Object.Destroy(_tex);
        }
    }
}
