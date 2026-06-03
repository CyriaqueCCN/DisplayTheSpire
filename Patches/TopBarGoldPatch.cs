using System;
using System.Linq;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.sts2.Core.Nodes.TopBar;
using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Hooks;
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

    // Base shop prices.
    //
    // Cards (MerchantCardEntry.GetCost) switch on CardRarity to 50/75/150.
    // Colorless cards are multiplied by 1.15 (rounded). CalcCost applies
    // a per-shop variance of Rng.NextFloat(0.95f, 1.05f), and on-sale
    // entries are halved after that. The Colorless surcharge and the
    // sale discount are not surfaced here because both are per-shop
    // random and the tooltip is pre-shop guidance, not a live quote.
    //
    // Potions (MerchantPotionEntry.GetCost) switch on PotionRarity to
    // 50/75/100 with the same 0.95-1.05 variance band. The constant
    // below is reused; if a future game patch diverges them, split this
    // into a separate PotionVariance.
    //
    // Relics (MerchantRelicEntry) read RelicModel.MerchantCost. Defaults
    // are Common=175, Uncommon=225, Rare=275, Shop=200; subclasses can
    // override. Variance is wider here at 0.85-1.15.
    private const float CardCommon    = 50f;
    private const float CardUncommon  = 75f;
    private const float CardRare      = 150f;
    private const float PotionCommon  = 50f;
    private const float PotionUncommon = 75f;
    private const float PotionRare    = 100f;
    private const float RelicCommon   = 175f;
    private const float RelicUncommon = 225f;
    private const float RelicRare     = 275f;
    private const float CardVariance  = 0.05f;   // cards and potions
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
            _tip.Show(_goldButton, DtsLoc.Tr("tip.gold.title"), BuildBbcode(), minWidth: 460f);
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

        // Card removal price. The game scales it with Inflation:
        //     BaseCost      = GetValueIfAscension(Inflation, 100, 75)
        //     PriceIncrease = GetValueIfAscension(Inflation, 50, 25)
        //     _cost         = BaseCost + PriceIncrease * CardShopRemovalsUsed
        bool inflation = _runState!.AscensionLevel >= (int)AscensionLevel.Inflation;
        int  baseCost  = inflation ? 100 : 75;
        int  priceStep = inflation ?  50 : 25;
        int removal = (int)((baseCost + priceStep * player.ExtraFields.CardShopRemovalsUsed) * mult);

        // Run modifiers can veto card removal entirely (e.g. the Hoarder
        // daily modifier overrides ShouldAllowMerchantCardRemoval to
        // false). The merchant gates the removal button on the same
        // hook, so showing a price when removal is denied would mislead.
        // ShouldAllowMerchantCardRemoval iterates every hook listener
        // and short-circuits on the first false, so any future modifier,
        // relic or power that vetoes removal is covered automatically.
        bool removalAllowed = Hook.ShouldAllowMerchantCardRemoval(_runState!, player);

        // Rarity header colors match the game's card frame palette
        const string rarHdr = "#E8C840"; // rare frame
        const string uncHdr = "#3080E8"; // uncommon frame
        const string comHdr = "#C8C8C8"; // common tier
        const string k = "#7A8FA8";
        const string v = "#FFF6E2";
        string remColor = removal <= gold ? v : k;

        // Two NBSPs on each side of every value cell give consistent column padding
        const string p = "\u00A0\u00A0";

        var sb = new System.Text.StringBuilder();
        sb.Append("\n[table=4]");
        sb.Append(
            $"[cell][/cell]" +
            $"[cell][center][color={comHdr}]{p}{DtsLoc.Tr("rarity.common")}{p}[/color][/center][/cell]" +
            $"[cell][center][color={uncHdr}]{p}{DtsLoc.Tr("rarity.uncommon")}{p}[/color][/center][/cell]" +
            $"[cell][center][color={rarHdr}]{p}{DtsLoc.Tr("rarity.rare")}{p}[/color][/center][/cell]");
        sb.Append(PriceRow($"[color={v}]{DtsLoc.Tr("gold.row.cards")}[/color]",            CardCommon,   CardUncommon,   CardRare,   CardVariance,  mult, gold, p));
        sb.Append(PriceRow($"[color=#E8C840]{DtsLoc.Tr("gold.row.potions")}[/color]",      PotionCommon, PotionUncommon, PotionRare, CardVariance,  mult, gold, p));
        sb.Append(PriceRow($"[color=#60C8A8]{DtsLoc.Tr("gold.row.relics")}[/color]",       RelicCommon,  RelicUncommon,  RelicRare,  RelicVariance, mult, gold, p));
        sb.Append("[/table]");
        if (removalAllowed)
            sb.Append($"\n\n[font_size=11][color={k}]{DtsLoc.Tr("gold.removal_label")}  [/color][color={remColor}]{DtsLoc.Tr("gold.amount_g", removal)}[/color][/font_size]");
        else
            sb.Append($"\n\n[font_size=11][color={k}]{DtsLoc.Tr("gold.removal_blocked")}[/color][/font_size]");
        if (hasMem) sb.Append($"\n[font_size=11][color=#60C8A8]{DtsLoc.Tr("gold.membership_card")}[/color][/font_size]");
        if (hasCou) sb.Append($"\n[font_size=11][color=#60C8A8]{DtsLoc.Tr("gold.the_courier")}[/color][/font_size]");
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
        return $"{min}-{max}";
    }

    private static string AffordColor(float base_, float variance, float mult, int gold)
    {
        int min = (int)(base_ * (1f - variance) * mult);
        return min <= gold ? "#FFF6E2" : "#7A8FA8";
    }
}
