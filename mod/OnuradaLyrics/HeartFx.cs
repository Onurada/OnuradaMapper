using UnityEngine;
using UnityEngine.UI;

namespace OnuradaLyrics
{
    /// <summary>Hearts drawn from a procedurally generated texture (the game's fonts have no heart glyph).</summary>
    internal class HeartFx
    {
        public readonly RawImage Big;
        public readonly RawImage[] Particles;
        readonly GameObject _go;
        static Texture2D _tex;

        public HeartFx(Transform parent, int layer, int particleCount)
        {
            if (_tex == null) _tex = MakeHeartTexture(128);
            _go = new GameObject("OnuradaHearts");
            _go.layer = layer;
            _go.transform.SetParent(parent, false);
            var canvas = _go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 5;
            ((RectTransform)_go.transform).sizeDelta = new Vector2(1f, 1f);
            var mat = new Material(Canvas.GetDefaultCanvasMaterial());
            if (mat.HasProperty("_ColorMask")) mat.SetFloat("_ColorMask", 14f);
            Big = MakeImage("Big", 1.7f, mat, layer);
            Particles = new RawImage[particleCount];
            for (int i = 0; i < particleCount; i++) Particles[i] = MakeImage("P" + i, 0.42f, mat, layer);
        }

        RawImage MakeImage(string n, float size, Material mat, int layer)
        {
            var go = new GameObject(n);
            go.layer = layer;
            go.transform.SetParent(_go.transform, false);
            var img = go.AddComponent<RawImage>();
            img.texture = _tex; img.material = mat; img.raycastTarget = false;
            img.rectTransform.sizeDelta = new Vector2(size, size);
            img.color = new Color(1, 1, 1, 0);
            go.SetActive(false);
            return img;
        }

        public static void Show(RawImage img, bool on)
        {
            if (img.gameObject.activeSelf != on) img.gameObject.SetActive(on);
        }

        /// <summary>White heart with a soft glow, tinted by RawImage.color.</summary>
        static Texture2D MakeHeartTexture(int n)
        {
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear };
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    // supersample the implicit heart curve (x^2 + y^2 - 1)^3 - x^2 y^3 <= 0
                    float cov = 0f;
                    for (int sy = 0; sy < 3; sy++)
                        for (int sx = 0; sx < 3; sx++)
                        {
                            float u = ((x + (sx + 0.5f) / 3f) / n - 0.5f) * 2.9f;
                            float v = ((y + (sy + 0.5f) / 3f) / n - 0.47f) * 2.9f;
                            float a = u * u + v * v - 1f;
                            if (a * a * a - u * u * v * v * v <= 0f) cov += 1f / 9f;
                        }
                    // glow: distance-ish falloff from the shape
                    float gu = ((x + 0.5f) / n - 0.5f) * 2.9f, gv = ((y + 0.5f) / n - 0.47f) * 2.9f;
                    float ga = gu * gu + gv * gv - 1f;
                    float f = ga * ga * ga - gu * gu * gv * gv * gv;
                    float glow = Mathf.Clamp01(1f - f * 3.5f) * 0.45f;
                    float alpha = Mathf.Max(cov, glow * (1f - cov));
                    // light highlight on the upper-left lobe
                    float hl = Mathf.Clamp01(1f - Mathf.Sqrt((gu + 0.45f) * (gu + 0.45f) + (gv - 0.45f) * (gv - 0.45f)) / 0.35f) * cov;
                    byte c = (byte)Mathf.Clamp(215 + 40 * hl, 0, 255);
                    px[y * n + x] = new Color32(c, c, c, (byte)(alpha * 255));
                }
            tex.SetPixels32(px);
            tex.Apply(true, true);
            return tex;
        }

        public void Destroy() { if (_go != null) Object.Destroy(_go); }
    }
}
