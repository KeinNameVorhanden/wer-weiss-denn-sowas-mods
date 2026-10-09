using System;
using System.Collections.Generic;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.Audio;

namespace WWDS.Game.Wwds.Mods.Ui;

/// <summary>
/// Looks up the game's own fonts, sprites and UI sounds by name so mod
/// menus look like the game. Everything has a flat fallback: a missing asset never breaks a menu.
/// </summary>
public sealed class GameStyle
{
    public static readonly Color White = Color.white;
    public static readonly Color Accent = new(1f, 0.706f, 0.114f);
    public static readonly Color Muted = new(0.698f, 0.698f, 0.698f);
    public static readonly Color PanelFallback = new(0.04f, 0.07f, 0.16f, 0.96f);
    public static readonly Color RowFallback = new(1f, 1f, 1f, 0.06f);
    public static readonly Color RowFocusFallback = new(1f, 0.706f, 0.114f, 0.35f);
    public static readonly Color Dim = new(0f, 0f, 0f, 0.6f);

    private readonly Dictionary<string, Sprite> _sprites = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AudioClip> _clips = new(StringComparer.Ordinal);

    public TMP_FontAsset? BodyFont { get; private set; }

    public TMP_FontAsset? TitleFont { get; private set; }

    public AudioMixerGroup? UiMixer { get; private set; }

    /// <summary>Reloads the caches (call after a scene load; show assets exist only during a show).</summary>
    public void Refresh()
    {
        foreach (var font in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
        {
            if (font == null) continue;
            if (font.name == "THESANS-SEMIBOLD SDF") BodyFont = font;
            else if (font.name == "TheSans_B7_Bold SDF") TitleFont = font;
            else if (BodyFont == null) BodyFont = font;
        }

        TitleFont ??= BodyFont;
        foreach (var sprite in Resources.FindObjectsOfTypeAll<Sprite>())
        {
            if (sprite != null && !_sprites.ContainsKey(sprite.name)) _sprites[sprite.name] = sprite;
        }

        foreach (var clip in Resources.FindObjectsOfTypeAll<AudioClip>())
        {
            if (clip != null && (clip.name.StartsWith("sfx_", StringComparison.Ordinal) || clip.name == "Pop")) _clips[clip.name] = clip;
        }

        if (UiMixer == null && SSoundManager.Instance != null)
        {
            var field = typeof(SSoundManager).GetField("UIAudioSource", BindingFlags.Instance | BindingFlags.NonPublic);
            UiMixer = (field?.GetValue(SSoundManager.Instance) as AudioSource)?.outputAudioMixerGroup;
        }
    }

    public Sprite? Sprite(string name)
    {
        if (_sprites.TryGetValue(name, out var s) && s != null) return s;
        _sprites.Remove(name);
        return null;
    }

    public AudioClip? Clip(string name) => _clips.TryGetValue(name, out var c) && c != null ? c : null;

    /// <summary>Controller glyph of the game for a face (keyboard or pad style), or null.</summary>
    public Sprite? Glyph(ButtonFaces face, bool keyboard)
    {
        try
        {
            return SDataStore.Instance?.GetSpriteFaceForButton(keyboard ? ButtonStyles.KeyboardOne : ButtonStyles.XBoxOne, face, false);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
