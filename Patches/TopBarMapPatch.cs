using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.TopBar;
using MegaCrit.Sts2.Core.Runs;
using DisplayTheSpire.Logging;
using DisplayTheSpire.UI;

namespace DisplayTheSpire.Patches;

[HarmonyPatch]
public static class TopBarMapPatch
{
    private static IRunState?        _runState;
    private static NTopBarMapButton? _mapButton;
    private static readonly DtsNativeTip _tip = new();

    [HarmonyPatch(typeof(NTopBar), nameof(NTopBar.Initialize))]
    [HarmonyPostfix]
    private static void AfterTopBarInitialize(NTopBar __instance, IRunState runState)
    {
        try
        {
            if (runState.Players.Count == 0) return;
            _runState  = runState;
            _mapButton = __instance.Map;
        }
        catch (Exception e) { ModLog.Error("TopBarMapPatch.AfterTopBarInitialize", e); }
    }

    [HarmonyPatch(typeof(NTopBar), nameof(NTopBar._ExitTree))]
    [HarmonyPostfix]
    private static void AfterTopBarExitTree()
    {
        try { _tip.Hide(); _runState = null; _mapButton = null; }
        catch (Exception e) { ModLog.Error("TopBarMapPatch.AfterTopBarExitTree", e); }
    }

    [HarmonyPatch(typeof(NTopBarMapButton), "OnFocus")]
    [HarmonyPostfix]
    private static void AfterMapFocus(NTopBarMapButton __instance)
    {
        try
        {
            NHoverTipSet.Remove(__instance);
            if (_runState == null || _mapButton == null) return;
            _tip.Show(_mapButton, "Next Unknown Node  (M)", BuildBbcode());
        }
        catch (Exception e) { ModLog.Error("TopBarMapPatch.AfterMapFocus", e); }
    }

    [HarmonyPatch(typeof(NTopBarMapButton), "OnUnfocus")]
    [HarmonyPostfix]
    private static void AfterMapUnfocus() { try { _tip.Hide(); } catch { } }

    private static string BuildBbcode()
    {
        var odds  = _runState!.Odds.UnknownMapPoint;
        int ev  = Pct(Math.Max(0f, odds.EventOdds));
        int mon = Pct(Math.Max(0f, odds.MonsterOdds));
        int tre = Pct(Math.Max(0f, odds.TreasureOdds));
        int shp = Pct(Math.Max(0f, odds.ShopOdds));

        // Each row is a centered line - the whole block is centered within the tooltip.
        // The tooltip width is driven by the title; centering each row makes the content
        // symmetrically placed regardless of label/value width differences.
        return
            "\n" +
            Row("[color=#8888CC]Event[/color]",    ev,  "#8888CC")   + "\n" +
            Row("[color=#CC4040]Monster[/color]",  mon, "#FFF6E2")   + "\n" +
            Row("[color=#E8C840]Treasure[/color]", tre, "#E8C840")   + "\n" +
            Row("[color=#60C8A8]Shop[/color]",     shp, "#60C8A8");
    }

    private static string Row(string label, int pct, string valColor)
        => $"[center]{label}   [color={valColor}]{pct}%[/color][/center]";

    private static int Pct(float v) => (int)Math.Round(v * 100);
}
