using System;
using System.Text;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;          // AscensionHelper
using MegaCrit.Sts2.Core.Nodes.HoverTips;  // NHoverTipSet
using MegaCrit.Sts2.Core.Runs;             // IRunState
using DisplayTheSpire.Logging;
using DisplayTheSpire.UI;

// NTopBar is in MegaCrit.Sts2.Core.Nodes.TopBar 
// NTopBarPortraitTip is in MegaCrit.sts2.Core.Nodes.TopBar
// Both are referenced via fully-qualified names in typeof() to avoid namespace collision.

namespace DisplayTheSpire.Patches;

/// <summary>
/// When Ascension > 0, replaces the native portrait hover tip with an enriched
/// version that shows both the name and description of each active ascension level.
/// </summary>
[HarmonyPatch]
public static class TopBarAscensionPatch
{
    private static IRunState?                               _runState;
    private static MegaCrit.sts2.Core.Nodes.TopBar.NTopBarPortraitTip? _portrait;
    private static readonly DtsNativeTip                    _tip = new();

    [HarmonyPatch(typeof(MegaCrit.Sts2.Core.Nodes.CommonUi.NTopBar),
                  nameof(MegaCrit.Sts2.Core.Nodes.CommonUi.NTopBar.Initialize))]
    [HarmonyPostfix]
    private static void AfterTopBarInitialize(IRunState runState)
    {
        try
        {
            if (runState.Players.Count == 0) return;
            _runState = runState;
        }
        catch (Exception e) { ModLog.Error("TopBarAscensionPatch.AfterTopBarInitialize", e); }
    }

    [HarmonyPatch(typeof(MegaCrit.sts2.Core.Nodes.TopBar.NTopBarPortraitTip), "Initialize")]
    [HarmonyPostfix]
    private static void AfterPortraitInit(MegaCrit.sts2.Core.Nodes.TopBar.NTopBarPortraitTip __instance)
    {
        _portrait = __instance;
    }

    [HarmonyPatch(typeof(MegaCrit.Sts2.Core.Nodes.CommonUi.NTopBar),
                  nameof(MegaCrit.Sts2.Core.Nodes.CommonUi.NTopBar._ExitTree))]
    [HarmonyPostfix]
    private static void AfterTopBarExitTree()
    {
        try { _tip.Hide(); _runState = null; _portrait = null; }
        catch (Exception e) { ModLog.Error("TopBarAscensionPatch.AfterTopBarExitTree", e); }
    }

    [HarmonyPatch(typeof(MegaCrit.sts2.Core.Nodes.TopBar.NTopBarPortraitTip), "OnHovered")]
    [HarmonyPostfix]
    private static void AfterPortraitHovered(MegaCrit.sts2.Core.Nodes.TopBar.NTopBarPortraitTip __instance)
    {
        try
        {
            if (_runState == null || _runState.AscensionLevel == 0) return;
            NHoverTipSet.Remove(__instance);
            var anchor = (Godot.Control?)_portrait ?? __instance;
            _tip.Show(anchor, BuildTitle(_runState.AscensionLevel), BuildBbcode(_runState.AscensionLevel));
        }
        catch (Exception e) { ModLog.Error("TopBarAscensionPatch.AfterPortraitHovered", e); }
    }

    [HarmonyPatch(typeof(MegaCrit.sts2.Core.Nodes.TopBar.NTopBarPortraitTip), "OnUnhovered")]
    [HarmonyPostfix]
    private static void AfterPortraitUnhovered() { try { _tip.Hide(); } catch { } }

    private static string BuildTitle(int level) => $"Ascension {level}";

    private static string BuildBbcode(int maxLevel)
    {
        var sb = new StringBuilder();
        for (int i = 1; i <= maxLevel; i++)
        {
            string title = AscensionHelper.GetTitle(i).GetFormattedText();
            string desc  = AscensionHelper.GetDescription(i).GetFormattedText();
            if (i > 1) sb.Append('\n');
            sb.Append($"[font_size=17][color=#E8C840][b]{i}. {title}[/b][/color][/font_size]\n");
            sb.Append($"[font_size=13][color=#7A8FA8]{desc}[/color][/font_size]");
        }
        return sb.ToString();
    }
}
