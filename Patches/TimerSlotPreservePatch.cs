using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.TopBar;
using DisplayTheSpire.Logging;

namespace DisplayTheSpire.Patches;

// Reserves the run-timer's HBoxContainer slot even when the timer is
// hidden, so the SaveIndicator and the potion-chance widget never
// shift horizontally as the player enters/leaves the map screen or
// toggles the ShowRunTimer preference.
//
// Vanilla NRunTimer.ToggleTimer sets `base.Visible = on`. An invisible
// HBoxContainer child collapses to a 0-width slot in Godot, so RAS
// reflows whenever the timer flips. Swap that for a Modulate.A swap:
// the child stays Visible (so the slot keeps reserving the
// TimerIcon (40) + sep (4) + TimerLabel (120) = 164 px width), but
// renders fully transparent when the timer would otherwise be off.
[HarmonyPatch]
public static class TimerSlotPreservePatch
{
    [HarmonyPatch(typeof(NRunTimer), "ToggleTimer")]
    [HarmonyPrefix]
    private static bool BeforeTimerToggle(NRunTimer __instance, bool on)
    {
        try
        {
            // Children inherit modulate, so the icon and label both
            // fade with the parent. The slot itself is laid out from
            // CustomMinimumSize / explicit child sizes -- unaffected
            // by modulate.
            var c = __instance.Modulate;
            __instance.Modulate = new Color(c.R, c.G, c.B, on ? 1f : 0f);
            // Force Visible back on if any other code path turned it
            // off before the patch fired.
            if (!__instance.Visible) __instance.Visible = true;
        }
        catch (Exception e) { ModLog.Error("TimerSlotPreservePatch", e); }
        // Skip the original ToggleTimer body.
        return false;
    }
}
