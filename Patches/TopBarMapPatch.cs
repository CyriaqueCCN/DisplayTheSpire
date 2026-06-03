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
    private static NTopBar?          _topBar;
    private static DtsModal?         _modal;
    private static readonly DtsNativeTip _tip = new();

    [HarmonyPatch(typeof(NTopBar), nameof(NTopBar.Initialize))]
    [HarmonyPostfix]
    private static void AfterTopBarInitialize(NTopBar __instance, IRunState runState)
    {
        try
        {
            if (runState.Players.Count == 0) return;
            _runState  = runState;
            _topBar    = __instance;
            _mapButton = __instance.Map;
            _mapButton.GuiInput += OnMapGuiInput;
        }
        catch (Exception e) { ModLog.Error("TopBarMapPatch.AfterTopBarInitialize", e); }
    }

    [HarmonyPatch(typeof(NTopBar), nameof(NTopBar._ExitTree))]
    [HarmonyPostfix]
    private static void AfterTopBarExitTree()
    {
        try
        {
            _tip.Hide();
            if (_mapButton != null && GodotObject.IsInstanceValid(_mapButton))
                _mapButton.GuiInput -= OnMapGuiInput;
            _modal?.Close();
            _runState  = null;
            _mapButton = null;
            _topBar    = null;
            _modal     = null;
        }
        catch (Exception e) { ModLog.Error("TopBarMapPatch.AfterTopBarExitTree", e); }
    }

    // Right-click on the map button opens the event overview modal
    private static void OnMapGuiInput(InputEvent @event)
    {
        try
        {
            if (@event is not InputEventMouseButton mb || !mb.Pressed
                || mb.ButtonIndex != MouseButton.Right) return;
            if (_runState == null || _topBar == null
                || !GodotObject.IsInstanceValid(_topBar)) return;

            _mapButton?.AcceptEvent();
            _modal?.Close();
            _modal = EventOverviewModal.Show(_topBar, _runState);
            _modal.Closed += () => _modal = null;
        }
        catch (Exception e) { ModLog.Error("TopBarMapPatch.OnMapGuiInput", e); }
    }

    [HarmonyPatch(typeof(NTopBarMapButton), "OnFocus")]
    [HarmonyPostfix]
    private static void AfterMapFocus(NTopBarMapButton __instance)
    {
        try
        {
            NHoverTipSet.Remove(__instance);
            if (_runState == null || _mapButton == null) return;
            _tip.Show(_mapButton, DtsLoc.Tr("tip.map.title"), BuildBbcode());
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

        // Each row is centered. Tooltip width is set by the title, so
        // centering each row keeps content symmetrical regardless of
        // label-vs-value length.
        return
            "\n" +
            Row($"[color=#8888CC]{DtsLoc.Tr("map.event")}[/color]",    ev,  "#8888CC") + "\n" +
            Row($"[color=#CC4040]{DtsLoc.Tr("map.monster")}[/color]",  mon, "#FFF6E2") + "\n" +
            Row($"[color=#E8C840]{DtsLoc.Tr("map.treasure")}[/color]", tre, "#E8C840") + "\n" +
            Row($"[color=#60C8A8]{DtsLoc.Tr("map.shop")}[/color]",     shp, "#60C8A8");
    }

    private static string Row(string label, int pct, string valColor)
        => $"[center]{label}   [color={valColor}]{pct}%[/color][/center]";

    private static int Pct(float v) => (int)Math.Round(v * 100);
}
