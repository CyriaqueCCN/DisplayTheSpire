using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;   // CardPlay
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
            DtsRunData.OnRunStart(runState);
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
            DtsRunData.OnRunSuspended();
            _runState = null; _topBar = null; _pauseButton = null;
            _cardsThisTurn = _cardsThisCombat = _turnsThisCombat = 0;
        }
        catch (Exception e) { ModLog.Error("TopBarSettingsPatch.AfterTopBarExitTree", e); }
    }

    // Hook.AfterCardPlayed is the single universal chokepoint for every
    // card play: CardModel's play loop fires it once per play, covering
    // manual plays, auto-plays (CardPlay.IsAutoPlay), multi-plays (once
    // per repeat), and any effect/relic/power that plays a card. Counting
    // here therefore counts every card the local player plays, by any
    // means.
    //
    // In multiplayer this hook also fires on our client for cards played
    // by OTHER players (the combat is synchronized), so we attribute by
    // owner: only the local player's plays are counted. A partner who
    // wants their own counts installs the mod on their side. The check is
    // fail-open -- if ownership or the local id can't be determined it
    // counts, so single-player (one local player) never loses a card.
    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardPlayed))]
    [HarmonyPostfix]
    private static void OnAfterCardPlayed(CardPlay cardPlay)
    {
        try
        {
            if (_topBar == null) return;
            if (!IsLocalPlay(cardPlay)) return;
            _cardsThisTurn++;
            _cardsThisCombat++;
            DtsRunData.IncrementCardsThisRun();
        }
        catch (Exception e) { ModLog.Error("TopBarSettingsPatch.OnAfterCardPlayed", e); }
    }

    // True when the played card belongs to the local player (or when
    // ownership cannot be determined -- fail-open so single-player keeps
    // counting every card). cardPlay.Card.Owner is the playing Player;
    // RunManager.NetService.NetId is the local player's id (the same id
    // the game uses for "me", LocalContext.NetId).
    private static bool IsLocalPlay(CardPlay cardPlay)
    {
        try
        {
            var owner = cardPlay.Card?.Owner;
            if (owner == null) return true;   // unknown owner -> count
            return owner.NetId == RunManager.Instance.NetService.NetId;
        }
        catch { return true; }                // can't determine -> count
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

    [HarmonyPatch(typeof(Hook), nameof(Hook.BeforeCombatStart))]
    [HarmonyPostfix]
    private static void OnBeforeCombatStart()
    {
        try
        {
            if (_topBar == null) return;
            // Per-combat stats reset only when the next fight actually
            // begins -- not at combat end -- so they persist through the
            // reward screen and the player can still read last-combat
            // numbers while picking rewards.
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
            string actName = _runState?.Act.Title.GetFormattedText() ?? DtsLoc.Tr("settings.run_label");
            _tip.Show(_pauseButton, DtsLoc.Tr("tip.settings.title"), BuildBbcode(turn), minWidth: 460f);
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
        // turn header inserts a NBSP between word and number so it does
        // not wrap on a narrow viewport.
        const string p = "\u00A0\u00A0";

        // Replace any plain space inside the localized turn header with
        // a NBSP so the column header does not wrap on a narrow viewport.
        string turnHdr = DtsLoc.Tr("settings.turn_n", currentTurn).Replace(' ', '\u00A0');

        string header =
            $"[cell][/cell]" +
            $"[cell][center][color={k}]{p}{turnHdr}{p}[/color][/center][/cell]" +
            $"[cell][center][color={k}]{p}{DtsLoc.Tr("settings.combat")}{p}[/color][/center][/cell]" +
            $"[cell][center][color={k}]{p}{DtsLoc.Tr("settings.run")}{p}[/color][/center][/cell]";

        return
            "\n[table=4]" +
            header +
            DataRow($"[color={k}]{DtsLoc.Tr("settings.cards_played")}[/color]", _cardsThisTurn.ToString(), _cardsThisCombat.ToString(), DtsRunData.CardsThisRun.ToString(), v, p) +
            DataRow($"[color={k}]{DtsLoc.Tr("settings.avg_per_turn")}[/color]",  "-", $"{avgCombat:F1}", $"{avgRun:F1}", v, p) +
            "[/table]";
    }

    private static string DataRow(string label, string t, string c, string r, string valColor, string p)
        => $"[cell][center]{label}[/center][/cell]" +
           $"[cell][center][color={valColor}]{p}{t}{p}[/color][/center][/cell]" +
           $"[cell][center][color={valColor}]{p}{c}{p}[/color][/center][/cell]" +
           $"[cell][center][color={valColor}]{p}{r}{p}[/color][/center][/cell]";
}
