using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.TopBar;
using MegaCrit.Sts2.Core.Runs;
using DisplayTheSpire.Logging;
using DisplayTheSpire.UI;

namespace DisplayTheSpire.Patches;

[HarmonyPatch]
public static class TopBarSettingsPatch
{
    private static int _cardsThisTurn;
    private static int _cardsThisCombat;
    private static int _turnsThisCombat;

    private static IRunState?          _runState;
    private static NTopBar?            _topBar;
    private static NTopBarPauseButton? _pauseButton;
    private static readonly DtsNativeTip _tip    = new();
    private static readonly DtsNativeTip _actTip = new();

    [HarmonyPatch(typeof(NTopBar), nameof(NTopBar.Initialize))]
    [HarmonyPostfix]
    private static void AfterTopBarInitialize(NTopBar __instance, IRunState runState)
    {
        try
        {
            if (runState.Players.Count == 0) return;
            DtsRunData.OnRunStart();
            _runState    = runState;
            _topBar      = __instance;
            _pauseButton = __instance.Pause;
        }
        catch (Exception e) { ModLog.Error("TopBarSettingsPatch.AfterTopBarInitialize", e); }
    }

    [HarmonyPatch(typeof(NTopBar), nameof(NTopBar._ExitTree))]
    [HarmonyPostfix]
    private static void AfterTopBarExitTree()
    {
        try
        {
            _tip.Hide(); _actTip.Hide();
            DtsRunData.OnRunEnd();
            _runState = null; _topBar = null; _pauseButton = null;
            _cardsThisTurn = _cardsThisCombat = _turnsThisCombat = 0;
        }
        catch (Exception e) { ModLog.Error("TopBarSettingsPatch.AfterTopBarExitTree", e); }
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardPlayed))]
    [HarmonyPostfix]
    private static void OnAfterCardPlayed()
    {
        try
        {
            if (_topBar == null) return;
            _cardsThisTurn++;
            _cardsThisCombat++;
            DtsRunData.IncrementCardsThisRun();
        }
        catch (Exception e) { ModLog.Error("TopBarSettingsPatch.OnAfterCardPlayed", e); }
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterTurnEnd))]
    [HarmonyPostfix]
    private static void OnAfterTurnEnd(CombatSide side)
    {
        try
        {
            if (_topBar == null || side != CombatSide.Player) return;
            _turnsThisCombat++;
            DtsRunData.IncrementTurnsThisRun();
            _cardsThisTurn = 0;
        }
        catch (Exception e) { ModLog.Error("TopBarSettingsPatch.OnAfterTurnEnd", e); }
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterCombatEnd))]
    [HarmonyPostfix]
    private static void OnAfterCombatEnd()
    {
        // Stats persist through the reward screen so the player can still
        // read last-combat numbers while picking rewards. The reset runs
        // in OnBeforeCombatStart when the next fight actually begins.
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.BeforeCombatStart))]
    [HarmonyPostfix]
    private static void OnBeforeCombatStart()
    {
        try
        {
            if (_topBar == null) return;
            _cardsThisTurn = _cardsThisCombat = _turnsThisCombat = 0;
        }
        catch (Exception e) { ModLog.Error("TopBarSettingsPatch.OnBeforeCombatStart", e); }
    }

    [HarmonyPatch(typeof(NTopBarPauseButton), "OnFocus")]
    [HarmonyPostfix]
    private static void AfterPauseFocus(NTopBarPauseButton __instance)
    {
        try
        {
            NHoverTipSet.Remove(__instance);
            if (_pauseButton == null) return;
            int turn = _turnsThisCombat + 1;
            string actName = _runState?.Act.Title.GetFormattedText() ?? "Run";
            _tip.Show(_pauseButton, "Settings  (Esc)", BuildBbcode(turn), minWidth: 460f);
            // Standalone act-name tip stacked below the stats tip. Putting
            // the name in the body lets the inner RTL (ExpandFill +
            // CustomMinimumSize) center it across the full 460 px width.
            // An empty title hides the title bar so the panel is a single
            // compact line.
            string actBbcode = $"[center][font_size=20][b][color=#E8C840]{actName}[/color][/b][/font_size][/center]";
            _actTip.Show(_pauseButton, "", actBbcode, minWidth: 460f, yOffset: _tip.Height + 8f);
        }
        catch (Exception e) { ModLog.Error("TopBarSettingsPatch.AfterPauseFocus", e); }
    }

    [HarmonyPatch(typeof(NTopBarPauseButton), "OnUnfocus")]
    [HarmonyPostfix]
    private static void AfterPauseUnfocus() { try { _tip.Hide(); _actTip.Hide(); } catch { } }

    private static string BuildBbcode(int currentTurn)
    {
        float avgCombat = currentTurn > 0 ? (float)_cardsThisCombat / currentTurn : 0f;
        float avgRun    = (float)DtsRunData.CardsThisRun / Math.Max(1, DtsRunData.TurnsThisRun);
        const string k = "#7A8FA8", v = "#FFF6E2";
        // Two NBSPs on each side of every value cell force a stable
        // column width and prevent neighbouring cells from merging. The
        // NBSP between "Turn" and the number stops the header from
        // wrapping on a narrow viewport.
        const string p = "\u00A0\u00A0";

        string header =
            $"[cell][/cell]" +
            $"[cell][center][color={k}]{p}Turn\u00A0{currentTurn}{p}[/color][/center][/cell]" +
            $"[cell][center][color={k}]{p}Combat{p}[/color][/center][/cell]" +
            $"[cell][center][color={k}]{p}Run{p}[/color][/center][/cell]";

        return
            "\n[table=4]" +
            header +
            DataRow($"[color={k}]Cards played[/color]", _cardsThisTurn.ToString(), _cardsThisCombat.ToString(), DtsRunData.CardsThisRun.ToString(), v, p) +
            DataRow($"[color={k}]Avg per turn[/color]",  "-", $"{avgCombat:F1}", $"{avgRun:F1}", v, p) +
            "[/table]";
    }

    private static string DataRow(string label, string t, string c, string r, string valColor, string p)
        => $"[cell][center]{label}[/center][/cell]" +
           $"[cell][center][color={valColor}]{p}{t}{p}[/color][/center][/cell]" +
           $"[cell][center][color={valColor}]{p}{c}{p}[/color][/center][/cell]" +
           $"[cell][center][color={valColor}]{p}{r}{p}[/color][/center][/cell]";
}
