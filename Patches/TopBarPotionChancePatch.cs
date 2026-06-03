using System;
using System.Linq;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Odds;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using DisplayTheSpire.Logging;
using DisplayTheSpire.UI;

namespace DisplayTheSpire.Patches;

// Potion drop chance widget. Injects a styled panel into NTopBar showing
// the current drop chance and a tooltip with cumulative-drop projections
// for the next 2-5 fights.
[HarmonyPatch]
public static class TopBarPotionChancePatch
{
    private static Control?          _widgetRoot;
    private static Label?            _label;
    private static IRunState?        _runState;
    private static readonly DtsNativeTip _tip = new();

    // PanelW must fit the widest label text ("100%") at FontSizeTitle=32
    // with OutlineSizeLarge=12, plus the 36px potion icon, 10+10 margins,
    // and 4px HBox separation. "100%" measures ~88px in Kreon Bold at
    // size 32; with outline overshoot allow ~104px for the label area:
    //   170 = 36 (icon) + 4 (sep) + 104 (label) + 26 (left/right margin slack)
    // The panel never resizes after init -- every possible value (0%
    // through 100%) renders inside this fixed slot.
    private const float PanelW         = 170f;
    private const float PanelH         = 71f;
    private const float PanelTopOffset = 4f;
    private const float IconPx         = 36f;
    private const int   ContentMarginH = 10;
    private const int   ContentMarginV = 4;

    private const string PotionIconPath = "res://images/atlases/potion_atlas.sprites/block_potion.tres";

    [HarmonyPatch(typeof(NTopBar), nameof(NTopBar.Initialize))]
    [HarmonyPostfix]
    private static void AfterTopBarInitialize(NTopBar __instance, IRunState runState)
    {
        try
        {
            if (runState.Players.Count == 0) return;
            DtsRunData.OnRunStart(runState);
            _runState       = runState;
            var player      = runState.Players[0];
            float initValue = player.PlayerOdds.PotionReward.CurrentValue;

            // ZIndex stays at the default 0 so NTransition's fade overlay
            // covers the widget during the menu-to-game scene swap, the
            // same way native NTopBar children behave. Visible=false
            // until positioning completes below.
            var root = _widgetRoot = new Control
            {
                CustomMinimumSize = new Vector2(PanelW, PanelH),
                Visible           = false,
            };

            var bgTex = TryLoadTexture(DtsTheme.BackdropTexture);
            if (bgTex != null)
                root.AddChild(new NinePatchRect
                {
                    Texture           = bgTex,
                    PatchMarginLeft   = DtsTheme.BackdropPatch,
                    PatchMarginRight  = DtsTheme.BackdropPatch,
                    PatchMarginTop    = DtsTheme.BackdropPatch,
                    PatchMarginBottom = DtsTheme.BackdropPatch,
                    AnchorRight       = 1f,
                    AnchorBottom      = 1f,
                });

            var margin = new MarginContainer { AnchorRight = 1f, AnchorBottom = 1f };
            margin.AddThemeConstantOverride("margin_left",   ContentMarginH);
            margin.AddThemeConstantOverride("margin_right",  ContentMarginH);
            margin.AddThemeConstantOverride("margin_top",    ContentMarginV);
            margin.AddThemeConstantOverride("margin_bottom", ContentMarginV);
            root.AddChild(margin);

            var hbox = new HBoxContainer
            {
                Alignment           = BoxContainer.AlignmentMode.Center,
                SizeFlagsHorizontal = Control.SizeFlags.Fill,
                SizeFlagsVertical   = Control.SizeFlags.Fill,
            };
            hbox.AddThemeConstantOverride("separation", 4);
            margin.AddChild(hbox);

            var iconTex = TryLoadTexture(PotionIconPath);
            if (iconTex != null)
                hbox.AddChild(new TextureRect
                {
                    Texture             = iconTex,
                    ExpandMode          = TextureRect.ExpandModeEnum.IgnoreSize,
                    CustomMinimumSize   = new Vector2(IconPx, IconPx),
                    StretchMode         = TextureRect.StretchModeEnum.KeepAspectCentered,
                    SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
                    SizeFlagsVertical   = Control.SizeFlags.ShrinkCenter,
                });

            var label = new Label
            {
                // Center the percentage text within the label area so
                // shorter values ("40%", "70%") have breathing room from
                // the icon AND the panel's right edge, rather than
                // sitting flush against the icon.
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
                SizeFlagsVertical   = Control.SizeFlags.ShrinkCenter,
                AutowrapMode        = TextServer.AutowrapMode.Off,
            };
            label.AddThemeColorOverride("font_outline_color", DtsTheme.Outline);
            label.AddThemeConstantOverride("outline_size", DtsTheme.OutlineSizeLarge);
            label.AddThemeFontSizeOverride("font_size", DtsTheme.FontSizeTitle);
            hbox.AddChild(label);

            RefreshLabel(label, initValue);
            _label = label;

            root.MouseEntered += () =>
            {
                if (_widgetRoot == null || !GodotObject.IsInstanceValid(_widgetRoot)) return;
                _tip.Show(_widgetRoot, DtsLoc.Tr("tip.potion.title"), BuildBbcode(
                    (_label != null && GodotObject.IsInstanceValid(_label))
                        ? GetCurrentValue()
                        : initValue));
            };
            root.MouseExited += () => _tip.Hide();

            // Inject INTO RightAlignedStuff so the widget participates
            // in the same HBoxContainer layout as the other right-aligned
            // controls.
            //
            // Vanilla RAS sibling order:
            //     SaveIndicator | Padding (36) | TimerContainer |
            //     Map | DeckContainer | PauseButton
            // The only explicit spacer is the 36px Padding between
            // SaveIndicator and TimerContainer (HBox separation = 0).
            // We mirror that pattern by adding a SECOND 36px Padding
            // before the widget, so the widget has symmetric gutters
            // on both sides:
            //     SaveIndicator | Padding(new, 36) | Widget |
            //     Padding(vanilla, 36) | TimerContainer | Map |
            //     DeckContainer | PauseButton
            // The vanilla Padding stays in place; the widget slots
            // between the two pads.
            //
            // ShrinkCenter on SizeFlagsHorizontal opts the widget (and
            // the new spacer) out of HBox extra-space distribution, so
            // they always render at exactly their CustomMinimumSize
            // regardless of cluster total.
            var ras = TopBarHelper.FindControl(__instance, "RightAlignedStuff");
            if (ras != null)
            {
                var leftPad = new Control
                {
                    Name                = "DtsLeftPadding",
                    CustomMinimumSize   = new Vector2(36f, 0f),
                    MouseFilter         = Control.MouseFilterEnum.Ignore,
                    SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
                };

                root.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
                root.SizeFlagsVertical   = Control.SizeFlags.ShrinkCenter;

                ras.AddChild(leftPad);
                ras.AddChild(root);
                // Order after AddChild calls (RAS appends to end):
                //   0 Save, 1 Padding(vanilla), 2 Timer, 3 Map,
                //   4 DeckContainer, 5 Pause, 6 leftPad, 7 widget
                // Move into final order:
                //   0 Save, 1 leftPad, 2 widget, 3 Padding(vanilla),
                //   4 Timer, 5 Map, 6 DeckContainer, 7 Pause
                ras.MoveChild(leftPad, 1);
                ras.MoveChild(root,    2);
            }
            else
            {
                // Defensive fallback: parent under NTopBar at a fixed
                // right-anchor position. Should not happen on supported
                // game versions but keeps the widget on screen if the
                // RAS lookup ever changes.
                __instance.AddChild(root);
                root.AnchorLeft   = root.AnchorRight = 1f;
                root.OffsetRight  = -200f;
                root.OffsetLeft   = root.OffsetRight - PanelW;
                root.OffsetTop    = PanelTopOffset;
                root.OffsetBottom = PanelTopOffset + PanelH;
            }
            root.Visible = true;

            ModLog.Info($"Potion chance widget injected ({(int)Math.Round(initValue * 100)}%)");
        }
        catch (Exception e) { ModLog.Error("AfterTopBarInitialize", e); }
    }

    [HarmonyPatch(typeof(PotionRewardOdds), nameof(PotionRewardOdds.Roll))]
    [HarmonyPostfix]
    private static void AfterPotionRoll(PotionRewardOdds __instance, bool __result)
    {
        try
        {
            if (__result) DtsRunData.IncrementPotionsDropped();
            if (_label == null || !GodotObject.IsInstanceValid(_label)) return;
            RefreshLabel(_label, __instance.CurrentValue);
            if (_tip.IsVisible)
                _tip.UpdateBbcode(BuildBbcode(__instance.CurrentValue));
        }
        catch (Exception e) { ModLog.Error("AfterPotionRoll", e); }
    }

    // Refresh after a reward is taken or a shop item is purchased so the
    // label flips to 100% the moment WhiteBeastStatue (or any future
    // forcing source) is picked up, instead of lagging one combat. The
    // underlying CurrentValue is unchanged by these hooks; RefreshLabel
    // re-runs the force check and chooses the displayed text accordingly.
    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterRewardTaken))]
    [HarmonyPostfix]
    private static void OnAfterRewardTaken() => RefreshFromCurrentValue();

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterItemPurchased))]
    [HarmonyPostfix]
    private static void OnAfterItemPurchased() => RefreshFromCurrentValue();

    private static void RefreshFromCurrentValue()
    {
        try
        {
            if (_label == null || !GodotObject.IsInstanceValid(_label)) return;
            if (_runState == null || _runState.Players.Count == 0) return;
            float cv = _runState.Players[0].PlayerOdds.PotionReward.CurrentValue;
            RefreshLabel(_label, cv);
            if (_tip.IsVisible) _tip.UpdateBbcode(BuildBbcode(cv));
        }
        catch (Exception e) { ModLog.Error("RefreshFromCurrentValue", e); }
    }

    [HarmonyPatch(typeof(NTopBar), nameof(NTopBar._ExitTree))]
    [HarmonyPostfix]
    private static void AfterTopBarExitTree()
    {
        try
        {
            _tip.Hide();
            DtsRunData.OnRunSuspended();
            _widgetRoot  = null;
            _label       = null;
            _runState    = null;
            ModLog.Info("Widget cleaned up");
        }
        catch (Exception e) { ModLog.Error("AfterTopBarExitTree", e); }
    }

    private static string BuildBbcode(float cv)
    {
        // When a relic (or any AbstractModel via Hook) forces a drop on
        // every combat, the per-room and cumulative chances are all 100%.
        // Replace the 4-row drop table with a single credit line crediting
        // the source relic, otherwise the rows are visual noise (every
        // row reads "100%").
        if (IsDropForced(out var sourceName))
        {
            string credit = sourceName != null
                ? DtsLoc.Tr("potion.guaranteed_by", sourceName)
                : DtsLoc.Tr("potion.guaranteed_generic");
            const string topHex = "9940FF";  // top of PotionChanceColor gradient
            return
                $"\n[center][color=#{topHex}]100%[/color][/center]\n" +
                $"\n[center][font_size=11][color=#60C8A8]{credit}[/color][/font_size][/center]" +
                $"\n[center][font_size=11][color=#7A8FA8]{DtsLoc.Tr("potion.dropped_this_run")}   [color=#FFF6E2]{DtsRunData.PotionsDropped}[/color][/color][/font_size][/center]";
        }

        float p2 = CumulativeDropChance(cv, 2);
        float p3 = CumulativeDropChance(cv, 3);
        float p4 = CumulativeDropChance(cv, 4);
        float p5 = CumulativeDropChance(cv, 5);

        // The "next-N-fights" row template lives in eng.json; the game's
        // localized version may put the number anywhere in the sentence.
        // {0} is the highlighted count, {1} is its color hex.
        string nextTemplate = DtsLoc.Tr("potion.next_fights_row");
        string Row(string nHex, int n, string pHex, int pct)
        {
            string left;
            try
            {
                left = string.Format(nextTemplate,
                    $"[color={nHex}]{n}[/color]");
            }
            catch (FormatException) { left = nextTemplate; }
            return $"[center][color=#7A8FA8]{left}[/color]   [color=#{pHex}]{pct}%[/color][/center]";
        }

        return
            "\n" +
            Row("#FFF6E2", 2, ColorHex(PotionChanceColor(p2)), (int)Math.Round(p2 * 100)) + "\n" +
            Row("#FFF6E2", 3, ColorHex(PotionChanceColor(p3)), (int)Math.Round(p3 * 100)) + "\n" +
            Row("#FFF6E2", 4, ColorHex(PotionChanceColor(p4)), (int)Math.Round(p4 * 100)) + "\n" +
            Row("#FFF6E2", 5, ColorHex(PotionChanceColor(p5)), (int)Math.Round(p5 * 100)) + "\n" +
            $"\n[center][font_size=11][color=#7A8FA8]{DtsLoc.Tr("potion.dropped_this_run")}   [color=#FFF6E2]{DtsRunData.PotionsDropped}[/color][/color][/font_size][/center]" +
            $"\n[center][font_size=11][color=#7A8FA8]{DtsLoc.Tr("potion.elite_bonus")}[/color][/font_size][/center]";
    }

    // True when any AbstractModel (relic, modifier, power) forces a potion
    // drop on the next combat. Probes the hook with RoomType.Monster, the
    // canonical "is the next fight guaranteed" question.
    //
    // sourceName receives the localized title of the FIRST relic owned by
    // player 0 that votes true on its own ShouldForcePotionReward override.
    // Falls back to null when the force comes from a non-relic source
    // (modifier, power); the caller picks a generic credit string.
    //
    // Read-only: ShouldForcePotionReward implementations are pure by
    // convention (the same hook fires from the merchant deny path with
    // no side effects), so calling per-hover is safe.
    private static bool IsDropForced(out string? sourceName)
    {
        sourceName = null;
        try
        {
            if (_runState == null || _runState.Players.Count == 0) return false;
            var player = _runState.Players[0];
            bool forced = Hook.ShouldForcePotionReward(_runState, player, RoomType.Monster);
            if (!forced) return false;

            foreach (var relic in player.Relics)
            {
                try
                {
                    if (relic.ShouldForcePotionReward(player, RoomType.Monster))
                    {
                        sourceName = relic.Title.GetFormattedText();
                        break;
                    }
                }
                catch { /* skip relics that throw on the probe */ }
            }
            return true;
        }
        catch (Exception ex) { ModLog.Error("IsDropForced", ex); return false; }
    }

    // Debug: Invoked via reflection by the debug server to force the tooltip on screen
    private static void ForceShowTip()
    {
        if (_widgetRoot == null || !GodotObject.IsInstanceValid(_widgetRoot)) return;
        _tip.Show(_widgetRoot, DtsLoc.Tr("tip.potion.title"), BuildBbcode(GetCurrentValue()));
    }

    private static float _lastKnownValue;

    private static float GetCurrentValue() => _lastKnownValue;

    private static void RefreshLabel(Label label, float v)
    {
        _lastKnownValue = v;
        // When a relic forces every-combat drops, the live CurrentValue
        // decays into negative territory (Roll() always returns true and
        // subtracts 0.1 per fight). The number is then meaningless to the
        // player. Display the effective 100% and use the top gradient
        // color so the widget visually matches the tooltip.
        if (IsDropForced(out _))
        {
            label.Text = "100%";
            label.AddThemeColorOverride("font_color", PotionChanceColor(1f));
            return;
        }
        label.Text = $"{(int)Math.Round(v * 100)}%";
        label.AddThemeColorOverride("font_color", PotionChanceColor(v));
    }

    private static Texture2D? TryLoadTexture(string path)
    {
        try { return ResourceLoader.Load<Texture2D>(path, "", ResourceLoader.CacheMode.Reuse); }
        catch (Exception e) { ModLog.Error("TryLoadTexture", e); return null; }
    }

    // Probability of at least one potion drop across the next n combats.
    // Each prior combat is assumed to have missed, which adds +0.10 to
    // the drop chance for the next roll.
    private static float CumulativeDropChance(float current, int n)
    {
        float probNone = 1f;
        for (int i = 0; i < n; i++)
        {
            float roomChance = Math.Min(1f, current + i * 0.10f);
            probNone *= (1f - roomChance);
            if (probNone <= 0f) break;
        }
        return 1f - probNone;
    }

    private static string ColorHex(Color c) => $"{c.R8:X2}{c.G8:X2}{c.B8:X2}";

    // Color gradient sampled to color the percentage label
    private static readonly (float stop, Color color)[] GradientStops =
    {
        (0.00f, new Color("721010")),
        (0.15f, new Color("CC2020")),
        (0.30f, new Color("D96010")),
        (0.45f, new Color("E8C840")),
        (0.55f, new Color("FFF6E2")),
        (0.70f, new Color("60C8A8")),
        (0.85f, new Color("3080E8")),
        (1.00f, new Color("9940FF")),
    };

    private static Color PotionChanceColor(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        for (int i = 0; i < GradientStops.Length - 1; i++)
        {
            if (t <= GradientStops[i + 1].stop)
            {
                float segLen = GradientStops[i + 1].stop - GradientStops[i].stop;
                float frac   = segLen > 0f ? (t - GradientStops[i].stop) / segLen : 0f;
                return GradientStops[i].color.Lerp(GradientStops[i + 1].color, frac);
            }
        }
        return GradientStops[^1].color;
    }
}
