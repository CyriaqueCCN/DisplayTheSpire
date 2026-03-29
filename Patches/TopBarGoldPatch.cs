using System;
using System.Linq;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.sts2.Core.Nodes.TopBar;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Runs;
using DisplayTheSpire.Logging;
using DisplayTheSpire.UI;

namespace DisplayTheSpire.Patches;

[HarmonyPatch]
public static class TopBarGoldPatch
{
    private static IRunState?   _runState;
    private static NTopBarGold? _goldButton;
    private static readonly DtsNativeTip _tip = new();

    // Base prices
    private const float CardCommon    = 50f;
    private const float CardUncommon  = 75f;
    private const float CardRare      = 150f;
    private const float PotionCommon  = 50f;
    private const float PotionUncommon = 75f;
    private const float PotionRare    = 100f;
    private const float RelicCommon   = 200f;
    private const float RelicUncommon = 250f;
    private const float RelicRare     = 300f;
    private const float CardVariance  = 0.05f;
    private const float RelicVariance = 0.15f;

    [HarmonyPatch(typeof(NTopBar), nameof(NTopBar.Initialize))]
    [HarmonyPostfix]
    private static void AfterTopBarInitialize(NTopBar __instance, IRunState runState)
    {
        try
        {
            if (runState.Players.Count == 0) return;
            _runState   = runState;
            _goldButton = __instance.Gold;
        }
        catch (Exception e) { ModLog.Error("TopBarGoldPatch.AfterTopBarInitialize", e); }
    }

    [HarmonyPatch(typeof(NTopBar), nameof(NTopBar._ExitTree))]
    [HarmonyPostfix]
    private static void AfterTopBarExitTree()
    {
        try { _tip.Hide(); _runState = null; _goldButton = null; }
        catch (Exception e) { ModLog.Error("TopBarGoldPatch.AfterTopBarExitTree", e); }
    }

    [HarmonyPatch(typeof(NTopBarGold), "OnFocus")]
    [HarmonyPostfix]
    private static void AfterGoldFocus(NTopBarGold __instance)
    {
        try
        {
            NHoverTipSet.Remove(__instance);
            if (_runState == null || _goldButton == null) return;
            _tip.Show(_goldButton, "Shop Prices", BuildBbcode(), minWidth: 460f);
        }
        catch (Exception e) { ModLog.Error("TopBarGoldPatch.AfterGoldFocus", e); }
    }

    [HarmonyPatch(typeof(NTopBarGold), "OnUnfocus")]
    [HarmonyPostfix]
    private static void AfterGoldUnfocus() { try { _tip.Hide(); } catch { } }

    private static string BuildBbcode()
    {
        var player = _runState!.Players[0];
        int gold   = player.Gold;

        bool hasMem = player.Relics.Any(r => r is MembershipCard);
        bool hasCou = player.Relics.Any(r => r is TheCourier);
        float mult  = (hasMem ? 0.5f : 1f) * (hasCou ? 0.8f : 1f);

        int removal = (int)((75 + 25 * player.ExtraFields.CardShopRemovalsUsed) * mult);

        // Rarity header colors match the game's card frame palette
        const string rarHdr = "#E8C840"; // gold - rare frame
        const string uncHdr = "#3080E8"; // blue - uncommon frame
        const string comHdr = "#C8C8C8"; // light gray - common tier
        const string k = "#7A8FA8";
        const string v = "#FFF6E2";
        string remColor = removal <= gold ? v : k;

        // 2 NBSP each side on every value cell -> consistent column padding
        const string p = "\u00A0\u00A0";

        var sb = new System.Text.StringBuilder();
        sb.Append("\n[table=4]");
        sb.Append(
            $"[cell][/cell]" +
            $"[cell][center][color={comHdr}]{p}Common{p}[/color][/center][/cell]" +
            $"[cell][center][color={uncHdr}]{p}Uncommon{p}[/color][/center][/cell]" +
            $"[cell][center][color={rarHdr}]{p}Rare{p}[/color][/center][/cell]");
        sb.Append(PriceRow($"[color={v}]Cards[/color]",       CardCommon,   CardUncommon,   CardRare,   CardVariance,  mult, gold, p));
        sb.Append(PriceRow($"[color=#E8C840]Potions[/color]", PotionCommon, PotionUncommon, PotionRare, CardVariance,  mult, gold, p));
        sb.Append(PriceRow($"[color=#60C8A8]Relics[/color]",  RelicCommon,  RelicUncommon,  RelicRare,  RelicVariance, mult, gold, p));
        sb.Append("[/table]");
        sb.Append($"\n\n[font_size=11][color={k}]Card Removal:  [/color][color={remColor}]{removal}g[/color][/font_size]");
        if (hasMem) sb.Append($"\n[font_size=11][color=#60C8A8]Membership Card  −50%[/color][/font_size]");
        if (hasCou) sb.Append($"\n[font_size=11][color=#60C8A8]The Courier  −20%[/color][/font_size]");
        return sb.ToString();
    }

    private static string PriceRow(string coloredLabel, float bCom, float bUnc, float bRar,
                                    float variance, float mult, int gold, string p)
    {
        string c  = PriceRange(bCom, variance, mult);
        string u  = PriceRange(bUnc, variance, mult);
        string r  = PriceRange(bRar, variance, mult);
        string cc = AffordColor(bCom, variance, mult, gold);
        string uc = AffordColor(bUnc, variance, mult, gold);
        string rc = AffordColor(bRar, variance, mult, gold);
        return $"[cell][center]{coloredLabel}[/center][/cell]" +
               $"[cell][center][color={cc}]{p}{c}{p}[/color][/center][/cell]" +
               $"[cell][center][color={uc}]{p}{u}{p}[/color][/center][/cell]" +
               $"[cell][center][color={rc}]{p}{r}{p}[/color][/center][/cell]";
    }

    private static string PriceRange(float base_, float variance, float mult)
    {
        int min = (int)(base_ * (1f - variance) * mult);
        int max = (int)(base_ * (1f + variance) * mult);
        return $"{min}\u2013{max}";
    }

    private static string AffordColor(float base_, float variance, float mult, int gold)
    {
        int min = (int)(base_ * (1f - variance) * mult);
        return min <= gold ? "#FFF6E2" : "#7A8FA8";
    }
}
