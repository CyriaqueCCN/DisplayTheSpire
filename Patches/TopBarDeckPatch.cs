using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.TopBar;
using MegaCrit.Sts2.Core.Odds;
using MegaCrit.Sts2.Core.Runs;
using DisplayTheSpire.Logging;
using DisplayTheSpire.UI;

namespace DisplayTheSpire.Patches;

[HarmonyPatch]
public static class TopBarDeckPatch
{
    private static IRunState?         _runState;
    private static NTopBarDeckButton? _deckButton;
    private static readonly DtsNativeTip _tip = new();

    [HarmonyPatch(typeof(NTopBar), nameof(NTopBar.Initialize))]
    [HarmonyPostfix]
    private static void AfterTopBarInitialize(NTopBar __instance, IRunState runState)
    {
        try
        {
            if (runState.Players.Count == 0) return;
            _runState   = runState;
            _deckButton = __instance.Deck;
        }
        catch (Exception e) { ModLog.Error("TopBarDeckPatch.AfterTopBarInitialize", e); }
    }

    [HarmonyPatch(typeof(NTopBar), nameof(NTopBar._ExitTree))]
    [HarmonyPostfix]
    private static void AfterTopBarExitTree()
    {
        try { _tip.Hide(); _runState = null; _deckButton = null; }
        catch (Exception e) { ModLog.Error("TopBarDeckPatch.AfterTopBarExitTree", e); }
    }

    [HarmonyPatch(typeof(NTopBarDeckButton), "OnFocus")]
    [HarmonyPostfix]
    private static void AfterDeckFocus(NTopBarDeckButton __instance)
    {
        try
        {
            NHoverTipSet.Remove(__instance);
            if (_runState == null || _deckButton == null) return;
            _tip.Show(_deckButton, "Card Reward Odds  (D)", BuildBbcode(), minWidth: 360f);
        }
        catch (Exception e) { ModLog.Error("TopBarDeckPatch.AfterDeckFocus", e); }
    }

    [HarmonyPatch(typeof(NTopBarDeckButton), "OnUnfocus")]
    [HarmonyPostfix]
    private static void AfterDeckUnfocus() { try { _tip.Hide(); } catch { } }

    // CardRarityOdds.RegularRareOdds and EliteRareOdds are static properties that already
    // call AscensionHelper.GetValueIfAscension(AscensionLevel.Scarcity, ...) internally,
    // so Ascension 7 (Scarcity) is accounted for automatically.
    // The pity accumulation is reflected in CurrentValue (= offset), so the displayed odds
    // already include pity
    private static string BuildBbcode()
    {
        var player = _runState!.Players[0];
        float offset = player.PlayerOdds.CardRarity.CurrentValue;

        float regRare    = Math.Max(0f, CardRarityOdds.RegularRareOdds + offset);
        float eliRare    = Math.Max(0f, CardRarityOdds.EliteRareOdds   + offset);
        const float regUncommon = 0.37f;
        const float eliUncommon = 0.40f;
        float regCommon  = Math.Max(0f, 1f - regRare - regUncommon);
        float eliCommon  = Math.Max(0f, 1f - eliRare - eliUncommon);

        const string rareHex = "#E8C840"; // gold - matches game rare card frame
        const string ucHex   = "#3080E8"; // blue - matches game uncommon card frame
        const string cmHex   = "#C8C8C8"; // light gray - common tier
        const string hdrHex  = "#7A8FA8"; // muted header

        const string p = "\u00A0\u00A0\u00A0";
        return
            "\n[table=3]" +
            $"[cell][/cell][cell][center][color={hdrHex}]{p}Normal{p}[/color][/center][/cell][cell][center][color={hdrHex}]{p}Elite{p}[/color][/center][/cell]" +
            TwoCol($"[color={rareHex}]Rare[/color]",   regRare,     eliRare,     rareHex, p) +
            TwoCol($"[color={ucHex}]Uncommon[/color]", regUncommon, eliUncommon, ucHex,   p) +
            TwoCol($"[color={cmHex}]Common[/color]",   regCommon,   eliCommon,   cmHex,   p) +
            "[/table]";
    }

    private static string TwoCol(string label, float norm, float elite, string valHex, string p)
        => $"[cell][center]{label}[/center][/cell]" +
           $"[cell][center][color={valHex}]{p}{norm * 100:F1}%{p}[/color][/center][/cell]" +
           $"[cell][center][color={valHex}]{p}{elite * 100:F1}%{p}[/color][/center][/cell]";
}
