using System;
using System.Collections.Generic;
using UnityEngine;

namespace WWDS.Game.Wwds.Mods.Ui;

/// <summary>
/// Draws keyboard key symbols for rebound keys: a light key cap with its label in a 5x7 pixel font,
/// sized like the game sprite it replaces. Several labels side by side (both players' shoulder keys)
/// or as a cross (four direction keys) are supported. Sprites are cached.
/// </summary>
public static class KeyCaps
{
    private static readonly Dictionary<char, int[]> Font = BuildFont();
    private static readonly Dictionary<string, Sprite> Cache = new();
    private static readonly Color Border = new(0.17f, 0.17f, 0.17f, 1f);
    private static readonly Color Face = new(0.93f, 0.93f, 0.93f, 1f);
    private static readonly Color Shade = new(0.78f, 0.78f, 0.78f, 1f);
    private static readonly Color Ink = new(0.1f, 0.1f, 0.1f, 1f);

    /// <summary>One key cap per label, side by side, in a sprite of the given size.</summary>
    public static Sprite Row(Vector2 size, params string[] labels) => Get("row", size, labels, (tex, w, h) =>
    {
        var cap = Math.Min(w / labels.Length, h);
        var x0 = (w - (cap * labels.Length)) / 2;
        for (var i = 0; i < labels.Length; i++) DrawCap(tex, x0 + (i * cap), (h - cap) / 2, cap, labels[i]);
    });

    /// <summary>Four direction keys as on a keyboard: up on top, left, down, right below.</summary>
    public static Sprite Cross(Vector2 size, string up, string left, string down, string right) => Get("cross", size, new[] { up, left, down, right }, (tex, w, h) =>
    {
        var cap = Math.Min(w / 3, h / 2);
        var x0 = (w - (3 * cap)) / 2;
        var y0 = (h - (2 * cap)) / 2;
        DrawCap(tex, x0 + cap, y0 + cap, cap, up);
        DrawCap(tex, x0, y0, cap, left);
        DrawCap(tex, x0 + cap, y0, cap, down);
        DrawCap(tex, x0 + (2 * cap), y0, cap, right);
    });

    private static Sprite Get(string kind, Vector2 size, string[] labels, Action<Texture2D, int, int> draw)
    {
        var w = Mathf.Clamp(Mathf.RoundToInt(size.x), 16, 256);
        var h = Mathf.Clamp(Mathf.RoundToInt(size.y), 16, 256);
        var key = kind + w + "x" + h + ":" + string.Join("|", labels);
        if (Cache.TryGetValue(key, out var sprite) && sprite != null) return sprite;
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        var clear = new Color[w * h];
        tex.SetPixels(clear);
        draw(tex, w, h);
        tex.Apply();
        sprite = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f);
        sprite.name = "WWDS_Key_" + key;
        UnityEngine.Object.DontDestroyOnLoad(tex);
        Cache[key] = sprite;
        return sprite;
    }

    /// <summary>A key cap of <paramref name="size"/> pixels at (x, y) (bottom left) with a label.</summary>
    private static void DrawCap(Texture2D tex, int x, int y, int size, string label)
    {
        var pad = Math.Max(1, size / 16);
        var border = Math.Max(2, size / 20);
        var radius = size / 5;
        var x1 = x + size - pad;
        var y1 = y + size - pad;
        for (var py = y + pad; py < y1; py++)
        {
            for (var px = x + pad; px < x1; px++)
            {
                var d = Outside(px, py, x + pad, y + pad, x1, y1, radius);
                if (d > 0) continue;
                var inner = Outside(px, py, x + pad + border, y + pad + border, x1 - border, y1 - border, Math.Max(0, radius - border));
                var shade = py < y + pad + border + (size / 10);
                tex.SetPixel(px, py, inner > 0 ? Border : shade ? Shade : Face);
            }
        }

        DrawText(tex, x + (size / 2), y + (size / 2) + (size / 30), size - (2 * (pad + border)) - (size / 6), label);
    }

    /// <summary>Distance outside a rounded rectangle (0 or less = inside).</summary>
    private static float Outside(int px, int py, int x0, int y0, int x1, int y1, int r)
    {
        var cx = Mathf.Clamp(px, x0 + r, x1 - 1 - r);
        var cy = Mathf.Clamp(py, y0 + r, y1 - 1 - r);
        if (px < x0 || py < y0 || px >= x1 || py >= y1) return 1;
        var dx = px - cx;
        var dy = py - cy;
        return Mathf.Sqrt((dx * dx) + (dy * dy)) - r;
    }

    private static void DrawText(Texture2D tex, int cx, int cy, int room, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var units = (text.Length * 6) - 1;
        var scale = Math.Max(1, Math.Min(room / units, (room * 7 / 10) / 7));
        var x = cx - (units * scale / 2);
        var y = cy - (7 * scale / 2);
        foreach (var c in text.ToUpperInvariant())
        {
            var rows = Font.TryGetValue(c, out var r) ? r : Font['?'];
            for (var row = 0; row < 7; row++)
            {
                for (var col = 0; col < 5; col++)
                {
                    if ((rows[row] & (1 << (4 - col))) == 0) continue;
                    for (var sy = 0; sy < scale; sy++)
                    {
                        for (var sx = 0; sx < scale; sx++) tex.SetPixel(x + (col * scale) + sx, y + ((6 - row) * scale) + sy, Ink);
                    }
                }
            }

            x += 6 * scale;
        }
    }

    private static Dictionary<char, int[]> BuildFont()
    {
        // Rows top to bottom, 5 bits each (left pixel = highest bit).
        const string glyphs =
            "A01110100011000111111100011000110001B11110100011000111110100011000111110C01110100011000010000100001000101110" +
            "D11110100011000110001100011000111110E11111100001000011110100001000011111F11111100001000011110100001000010000" +
            "G01110100011000010111100011000101111H10001100011000111111100011000110001I01110001000010000100001000010001110" +
            "J00111000100001000010000101001001100K10001100101010011000101001001010001L10000100001000010000100001000011111" +
            "M10001110111010110101100011000110001N10001100011100110101100111000110001O01110100011000110001100011000101110" +
            "P11110100011000111110100001000010000Q01110100011000110001101011001001101R11110100011000111110101001001010001" +
            "S01111100001000001110000010000111110T11111001000010000100001000010000100U10001100011000110001100011000101110" +
            "V10001100011000110001100010101000100W10001100011000110101101011010101010X10001100010101000100010101000110001" +
            "Y10001100010101000100001000010000100Z11111000010001000100010001000011111" +
            "001110100011001110101110011000101110100100011000010000100001000010001110201110100010000100010001000100011111" +
            "311111000100010000010000011000101110400010001100101010010111110001000010511111100001111000001000011000101110" +
            "600110010001000011110100011000101110711111000010001000100010000100001000801110100011000101110100011000101110" +
            "901110100011000101111000010001001100^00100011101010100100001000010000100v00100001000010000100101010111000100" +
            "<00000001000100011111010000010000000>00000001000001011111000100010000000+00000001000010011111001000010000000" +
            "-00000000000000011111000000000000000*00000101010111011111011101010100000/00001000100001000100010000100010000" +
            ".00000000000000000000000000110001100,00000000000000000000011000010001000?01110100010000100010001000000000100";
        var font = new Dictionary<char, int[]>();
        for (var i = 0; i + 35 < glyphs.Length + 1; i += 36)
        {
            var rows = new int[7];
            for (var r = 0; r < 7; r++) rows[r] = Convert.ToInt32(glyphs.Substring(i + 1 + (r * 5), 5), 2);
            font[glyphs[i]] = rows;
        }

        return font;
    }
}
