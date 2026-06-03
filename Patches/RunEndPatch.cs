using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Runs;
using DisplayTheSpire.Logging;
using DisplayTheSpire.UI;

namespace DisplayTheSpire.Patches;

// Stamps the per-run stat record with an end time and outcome when a run
// terminates. RunManager.OnEnded(bool isVictory) is the single terminal
// path for victory, death, and abandon (Abandon kills all players, which
// routes through the game-over flow into OnEnded), so one postfix covers
// every ending.
//
// Read-only with respect to gameplay: it only records metadata on the
// mod's own JSON store. The original OnEnded return value is untouched.
[HarmonyPatch]
public static class RunEndPatch
{
    [HarmonyPatch(typeof(RunManager), nameof(RunManager.OnEnded))]
    [HarmonyPostfix]
    private static void AfterRunEnded(bool isVictory)
    {
        try { DtsRunData.OnRunEnded(isVictory); }
        catch (Exception e) { ModLog.Error("RunEndPatch.AfterRunEnded", e); }
    }
}
