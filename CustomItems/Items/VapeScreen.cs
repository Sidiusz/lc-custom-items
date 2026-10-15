using UnityEngine;

namespace CustomItems.Items
{
    /// <summary>
    /// Пиксельный экран вейпа: текстура рисуется в коде и ставится на объект Screen модели
    /// (цвет и свечение HDRP/Lit). Та же картинка, что в Tools/Vape/screen.py (держите одинаковыми).
    /// Перерисовывается, только когда показанное изменилось.
    /// </summary>
    public class VapeScreen
    {
        public enum Style { Pixl, Box, Pod }

        // Шрифт 3x5: по строке сверху вниз, младший бит справа
        private static readonly string Glyphs = "0123456789%W";
        private static readonly int[][] Font =
        {
            new[] { 7, 5, 5, 5, 7 }, new[] { 2, 6, 2, 2, 7 }, new[] { 7, 1, 7, 4, 7 }, new[] { 7, 1, 7, 1, 7 },
            new[] { 5, 5, 7, 1, 1 }, new[] { 7, 4, 7, 1, 7 }, new[] { 7, 4, 7, 5, 7 }, new[] { 7, 1, 2, 2, 2 },
            new[] { 7, 5, 7, 5, 7 }, new[] { 7, 5, 7, 1, 7 }, new[] { 5, 1, 2, 4, 5 }, new[] { 5, 5, 5, 7, 5 },
        };

        // Семь сегментов: a верх, b верх-право, c низ-право, d низ, e низ-лево, f верх-лево, g середина
        private static readonly string[] Segments =
            { "abcdef", "bc", "abdeg", "abcdg", "bcfg", "acdfg", "acdefg", "abc", "abcdefg", "abcdfg" };

        private static readonly Color32 White = new Color32(235, 240, 255, 255);
        private static readonly Color32 Dim = new Color32(22, 23, 28, 255);
        private static readonly Color32 Black = new Color32(4, 4, 6, 255);
        private static readonly Color32 Red = new Color32(255, 70, 60, 255);

        private readonly Style style;
        private readonly int w, h;
        private readonly Color32[] px;
        private readonly Texture2D tex;
        private readonly Material material;
        private readonly float glow;
        private string shown;

        public VapeScreen(Style style, Renderer renderer, float glow)
        {
            this.style = style;
            this.glow = glow;
            (w, h) = style switch { Style.Box => (48, 24), Style.Pod => (24, 64), _ => (32, 24) };
            px = new Color32[w * h];
            tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = "VapeScreen",
            };
            material = renderer.material; // свой экземпляр у каждого вейпа
            material.SetTexture("_BaseColorMap", tex);
            material.SetTexture("_MainTex", tex);
            material.SetTexture("_EmissiveColorMap", tex);
            material.EnableKeyword("_EMISSIVE_COLOR_MAP");
            material.SetColor("_BaseColor", Color.white);
            if (material.HasProperty("_Smoothness"))
                material.SetFloat("_Smoothness", 0.85f);
        }

        /// <summary>charge и heat 0..1. Выключенный экран чёрный и не светится.</summary>
        public void Draw(bool on, float charge, float heat, int puffs, bool inhaling)
        {
            var pct = Mathf.Clamp(Mathf.RoundToInt(charge * 100f), 0, 100);
            var heatPx = Mathf.RoundToInt(Mathf.Clamp01(heat) * 32f);
            var key = on ? $"{pct}/{heatPx}/{puffs}/{inhaling}" : "off";
            if (key == shown)
                return;
            shown = key;

            for (var i = 0; i < px.Length; i++)
                px[i] = Black;
            if (on)
                Paint(Mathf.Clamp01(charge), pct, Mathf.Clamp01(heat), puffs, inhaling);
            tex.SetPixels32(px);
            tex.Apply(false);
            material.SetColor("_EmissiveColor", Color.white * (on ? glow : 0f));
        }

        private void Paint(float charge, int pct, float heat, int puffs, bool inhaling)
        {
            var c = pct <= 10 ? Red : White;
            switch (style)
            {
                case Style.Pixl:
                {
                    var shownPct = Mathf.Min(99, pct);
                    Seven(3, 3, shownPct / 10, c);
                    Seven(15, 3, shownPct % 10, c);
                    BatteryVertical(27, 3, charge, c);
                    Rect(28, 13, 2, 3, inhaling ? new Color32(255, 60, 60, 255) : Dim);
                    Rect(3, 21, 26, 2, Dim);
                    Rect(3, 21, Mathf.RoundToInt(26 * heat), 2, HeatColor(heat));
                    break;
                }
                case Style.Box:
                {
                    // Крупные проценты по центру, под ними полоса нагрева
                    var txt = pct + "%";
                    Text((w - (txt.Length * 12 - 3)) / 2, 2, txt, c, 3);
                    Rect(3, 20, w - 6, 2, Dim);
                    Rect(3, 20, Mathf.RoundToInt((w - 6) * heat), 2, HeatColor(heat));
                    break;
                }
                default:
                {
                    var txt = pct.ToString();
                    Text(2, 3, txt, c, 1);
                    var x = 2 + txt.Length * 4;
                    var bolt = new Color32(255, 210, 60, 255);
                    foreach (var (dx, dy) in new[] { (2, 0), (1, 1), (0, 2), (1, 2), (2, 2), (1, 3), (0, 4) })
                        Set(x + dx, 3 + dy, bolt);
                    var watts = (20 + Mathf.RoundToInt(20 * heat)).ToString();
                    Text(2, 12, watts, White, 2);
                    Text(2 + watts.Length * 8, 17, "W", White, 1);
                    // Огонь снизу вверх, высота растёт с нагревом
                    const int top = 26, bottom = 57;
                    var level = 0.25f + 0.75f * heat;
                    for (var y = top; y <= bottom; y++)
                    {
                        var k = (bottom - y) / (float)(bottom - top);
                        if (k > level)
                            continue;
                        var f = k / Mathf.Max(level, 1e-3f);
                        var dark = Mathf.Max(0f, (f - 0.7f) / 0.3f);
                        var m = 1f - 0.8f * dark;
                        var col = new Color32((byte)(255 * m), (byte)((230 - 200 * f) * m), (byte)((60 - 60 * f) * m), 255);
                        for (var xx = 1; xx < w - 1; xx++)
                            Set(xx, y, col);
                    }
                    Text(2, h - 6, (puffs % 100000).ToString("00000"), new Color32(150, 155, 170, 255), 1);
                    break;
                }
            }
        }

        private static Color32 HeatColor(float k)
        {
            k = Mathf.Clamp01(k);
            return k < 0.5f
                ? new Color32((byte)(60 + 390 * k), 220, 60, 255)
                : new Color32(255, (byte)(220 - 380 * (k - 0.5f)), 40, 255);
        }

        // Координаты как в screen.py: начало сверху слева. В текстуре Unity строка 0 — нижняя.
        private void Set(int x, int y, Color32 c)
        {
            if (x >= 0 && x < w && y >= 0 && y < h)
                px[(h - 1 - y) * w + x] = c;
        }

        private void Rect(int x, int y, int rw, int rh, Color32 c)
        {
            for (var yy = y; yy < y + rh; yy++)
                for (var xx = x; xx < x + rw; xx++)
                    Set(xx, yy, c);
        }

        private void Text(int x, int y, string s, Color32 c, int scale)
        {
            foreach (var ch in s)
            {
                var g = Glyphs.IndexOf(ch);
                if (g >= 0)
                {
                    for (var gy = 0; gy < 5; gy++)
                        for (var gx = 0; gx < 3; gx++)
                            if ((Font[g][gy] & (4 >> gx)) != 0)
                                Rect(x + gx * scale, y + gy * scale, scale, scale, c);
                }
                x += 4 * scale;
            }
        }

        private void Seven(int x, int y, int digit, Color32 c, int sw = 10, int sh = 16, int t = 2)
        {
            var on = Segments[digit];
            var mid = y + (sh - t) / 2;
            void Seg(char name, int sx, int sy, int rw, int rh) => Rect(sx, sy, rw, rh, on.IndexOf(name) >= 0 ? c : Black);
            Seg('a', x + t, y, sw - 2 * t, t);
            Seg('d', x + t, y + sh - t, sw - 2 * t, t);
            Seg('g', x + t, mid, sw - 2 * t, t);
            Seg('f', x, y + t, t, mid - y - t);
            Seg('b', x + sw - t, y + t, t, mid - y - t);
            Seg('e', x, mid + t, t, y + sh - t - mid - t);
            Seg('c', x + sw - t, mid + t, t, y + sh - t - mid - t);
        }

        private void BatteryVertical(int x, int y, float charge, Color32 c)
        {
            Rect(x + 1, y, 2, 1, c);
            Rect(x, y + 1, 4, 6, c);
            Rect(x + 1, y + 2, 2, 4, Black);
            var fill = Mathf.RoundToInt(4 * charge);
            Rect(x + 1, y + 6 - fill, 2, fill, c);
        }
    }
}
