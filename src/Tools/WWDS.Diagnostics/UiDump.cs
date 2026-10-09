using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace WWDS.Diagnostics;

/// <summary>Runtime inventory of the UI: canvases, event system, fonts, sprites, audio clips, input setup.</summary>
internal static class UiDump
{
    public static void Write(ProbeLog log, string state)
    {
        var canvases = Object.FindObjectsOfType<Canvas>()
            .Where(c => c.isRootCanvas)
            .Select(c =>
            {
                var scaler = c.GetComponent<CanvasScaler>();
                return (object?)(c.name + " mode=" + c.renderMode + " order=" + c.sortingOrder + " active=" + c.gameObject.activeInHierarchy
                    + (scaler != null ? " scaler=" + scaler.uiScaleMode + " ref=" + scaler.referenceResolution : string.Empty)
                    + " raycaster=" + (c.GetComponent<GraphicRaycaster>() != null));
            }).ToList();

        var es = EventSystem.current;
        var modules = Object.FindObjectsOfType<BaseInputModule>().Select(m => (object?)(m.GetType().FullName + " on " + m.name + " enabled=" + m.enabled)).ToList();

        var fonts = Resources.FindObjectsOfTypeAll<TMPro.TMP_FontAsset>().Select(f => (object?)f.name).Distinct().OrderBy(n => n).ToList();
        var materials = Resources.FindObjectsOfTypeAll<TMPro.TextMeshProUGUI>()
            .Where(t => t.gameObject.activeInHierarchy && t.font != null)
            .GroupBy(t => t.font.name + " | " + (t.fontSharedMaterial != null ? t.fontSharedMaterial.name : "-") + " | size " + Mathf.RoundToInt(t.fontSize) + " | color " + ColorUtility.ToHtmlStringRGBA(t.color))
            .OrderByDescending(g => g.Count()).Take(25)
            .Select(g => (object?)(g.Count() + "x " + g.Key + " e.g. '" + Short(g.First().text) + "' at " + Path(g.First().transform)))
            .ToList();

        var sprites = Object.FindObjectsOfType<Image>()
            .Where(i => i.sprite != null && i.gameObject.activeInHierarchy)
            .GroupBy(i => i.sprite.name + " type=" + i.type + " color " + ColorUtility.ToHtmlStringRGBA(i.color))
            .OrderByDescending(g => g.Count()).Take(40)
            .Select(g => (object?)(g.Count() + "x " + g.Key + " at " + Path(g.First().transform)))
            .ToList();

        var allSprites = Resources.FindObjectsOfTypeAll<Sprite>().Select(s => s.name)
            .Where(n => n.IndexOf("button", System.StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("frame", System.StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("panel", System.StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("bg", System.StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("ui_", System.StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("box", System.StringComparison.OrdinalIgnoreCase) >= 0)
            .Distinct().OrderBy(n => n).Take(120).Select(n => (object?)n).ToList();

        var clips = Resources.FindObjectsOfTypeAll<AudioClip>().Select(c => c.name)
            .Where(n => n.IndexOf("ui", System.StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("click", System.StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("select", System.StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("highlight", System.StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("button", System.StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("menu", System.StringComparison.OrdinalIgnoreCase) >= 0)
            .Distinct().OrderBy(n => n).Take(60).Select(n => (object?)n).ToList();

        var selectables = Object.FindObjectsOfType<Selectable>().Where(s => s.gameObject.activeInHierarchy)
            .Take(40).Select(s => (object?)(s.GetType().Name + " " + Path(s.transform) + " interactable=" + s.interactable)).ToList();

        log.Event("dump.ui", "state", state, "screen", Screen.width + "x" + Screen.height + " fullscreen=" + Screen.fullScreen,
            "cursorVisible", Cursor.visible, "cursorLock", Cursor.lockState.ToString(),
            "eventSystem", es == null ? "none" : es.name + " selected=" + (es.currentSelectedGameObject != null ? es.currentSelectedGameObject.name : "-"),
            "inputModules", modules, "canvases", canvases, "fonts", fonts, "textStyles", materials, "imageSprites", sprites,
            "uiSprites", allSprites, "uiClips", clips, "selectables", selectables);
    }

    private static string Short(string s) => string.IsNullOrEmpty(s) ? string.Empty : (s.Length > 30 ? s.Substring(0, 30) : s).Replace('\n', ' ');

    private static string Path(Transform t)
    {
        var parts = new List<string>();
        for (var x = t; x != null && parts.Count < 6; x = x.parent) parts.Insert(0, x.name);
        return string.Join("/", parts.ToArray());
    }
}
