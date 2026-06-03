using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using DisplayTheSpire.Logging;

namespace DisplayTheSpire.Patches;

// Prevents the "Game Saved" text from rendering past its allocated rect.
//
// Vanilla save_indicator.tscn ships the inner RichTextLabel with:
//   AutoSizeEnabled = false
//   autowrap_mode   = 0 (Off)
//   fit_content     = true
//   normal_font_size = 32
// `fit_content = true` only grows the rect vertically. `autowrap_mode = 0`
// lets text extend horizontally past the 200x80 slot, so longer localized
// strings like "Partie sauvegardee" (French) or "Spiel gespeichert"
// (German) overflow into adjacent controls.
//
// Fix: switch AutowrapMode to WordSmart. Long text wraps onto two lines
// inside the original 200x80 slot at the original font size 32. English
// "Game Saved" still fits on one line. No other geometry or theme
// override is touched (the v0.7.15 shrink that dropped the slot to 120
// and the font to 18 is reverted here).
[HarmonyPatch]
public static class SaveIndicatorWrapPatch
{
    [HarmonyPatch(typeof(NSaveIndicator), "_Ready")]
    [HarmonyPostfix]
    private static void AfterSaveIndicatorReady(NSaveIndicator __instance)
    {
        try
        {
            if (__instance.GetNodeOrNull<RichTextLabel>("Label") is { } lbl)
            {
                lbl.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            }
        }
        catch (Exception e) { ModLog.Error("SaveIndicatorWrapPatch._Ready", e); }
    }
}
