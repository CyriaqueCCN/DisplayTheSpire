using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Odds;
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
    private static readonly DtsNativeTip _tip = new();

    private const float PanelW         = 130f;
    private const float PanelH         = 71f;
    private const float PanelTopOffset = 4f;
    private const float IconPx         = 36f;
    private const int   ContentMarginH = 10;
    private const int   ContentMarginV = 4;
    private const float GapPx          = 4f;   // gap between widget and RightAlignedStuff

    // Slide-aside animation. NSaveIndicator runs ~3 s of "Game saved"
    // text; the hold matches that minus the two 0.3 s slide phases.
    private const float SlideDuration     = 0.30f;
    private const float SlideHoldDuration = 2.40f;
    private static float  _baseOffsetRight;
    private static float  _baseOffsetLeft;
    private static float  _saveIndicatorWidth;
    private static Tween? _slideTween;

    private const string PotionIconPath = "res://images/atlases/potion_atlas.sprites/block_potion.tres";

    [HarmonyPatch(typeof(NTopBar), nameof(NTopBar.Initialize))]
    [HarmonyPostfix]
    private static void AfterTopBarInitialize(NTopBar __instance, IRunState runState)
    {
        try
        {
            if (runState.Players.Count == 0) return;
            DtsRunData.OnRunStart();
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
                HorizontalAlignment = HorizontalAlignment.Left,
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
                _tip.Show(_widgetRoot, "Potion Drop Chance", BuildBbcode(
                    (_label != null && GodotObject.IsInstanceValid(_label))
                        ? GetCurrentValue()
                        : initValue));
            };
            root.MouseExited += () => _tip.Hide();

            // Inject into NTopBar synchronously. Position uses the raw
            // RightAlignedStuff.OffsetLeft from the .tscn, which is valid
            // before any layout pass. Visible=true in the same frame so
            // the widget fades in alongside NTopBar under NTransition.
            __instance.AddChild(root);

            var ras = TopBarHelper.FindControl(__instance, "RightAlignedStuff");
            if (ras != null)
            {
                float distFromRight = -ras.OffsetLeft + GapPx;
                root.AnchorLeft   = root.AnchorRight = 1f;
                root.OffsetRight  = -distFromRight;
                root.OffsetLeft   = root.OffsetRight - PanelW;
                root.OffsetTop    = PanelTopOffset;
                root.OffsetBottom = PanelTopOffset + PanelH;
            }
            else
            {
                // Fixed right-anchor offset when RightAlignedStuff cannot
                // be located (defensive: should not happen on supported
                // game versions).
                root.AnchorLeft   = root.AnchorRight = 1f;
                root.OffsetRight  = -200f;
                root.OffsetLeft   = root.OffsetRight - PanelW;
                root.OffsetTop    = PanelTopOffset;
                root.OffsetBottom = PanelTopOffset + PanelH;
            }
            _baseOffsetRight = root.OffsetRight;
            _baseOffsetLeft  = root.OffsetLeft;
            root.Visible = true;

            // Defer only the SaveIndicator width measurement: it requires
            // a completed layout pass to resolve.
            Callable.From(new Action(() =>
            {
                try
                {
                    if (!GodotObject.IsInstanceValid(__instance)) return;
                    var rasD = TopBarHelper.FindControl(__instance, "RightAlignedStuff");
                    if (rasD != null)
                        foreach (var child in rasD.GetChildren())
                            if (child is NSaveIndicator si && GodotObject.IsInstanceValid(si))
                            { _saveIndicatorWidth = si.Size.X; break; }
                    ModLog.Info($"SaveIndicator width: {_saveIndicatorWidth:0}px");
                }
                catch (Exception ex) { ModLog.Error("PotionChance deferred measure", ex); }
            })).CallDeferred();

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

    [HarmonyPatch(typeof(NTopBar), nameof(NTopBar._ExitTree))]
    [HarmonyPostfix]
    private static void AfterTopBarExitTree()
    {
        try
        {
            _tip.Hide();
            _slideTween?.Kill();
            _slideTween  = null;
            DtsRunData.OnRunEnd();
            _widgetRoot  = null;
            _label       = null;
            _baseOffsetRight = _baseOffsetLeft = _saveIndicatorWidth = 0f;
            ModLog.Info("Widget cleaned up");
        }
        catch (Exception e) { ModLog.Error("AfterTopBarExitTree", e); }
    }

    // Slides the widget aside when "Game saved" appears so the two
    // elements do not collide on the right side of the bar.
    [HarmonyPatch(typeof(NSaveIndicator), "SavedGame")]
    [HarmonyPostfix]
    private static void AfterGameSaved()
    {
        try
        {
            if (_widgetRoot == null || !GodotObject.IsInstanceValid(_widgetRoot)) return;
            if (_saveIndicatorWidth <= 0f) return;

            _slideTween?.Kill();
            _slideTween = _widgetRoot.CreateTween();

            // Slide left to make room. NSaveIndicator has a 0.5 s lead-in
            // before its own text appears; this duration matches it.
            _slideTween
                .TweenProperty(_widgetRoot, "offset_right",
                    _baseOffsetRight - _saveIndicatorWidth, SlideDuration)
                .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);
            _slideTween.Parallel()
                .TweenProperty(_widgetRoot, "offset_left",
                    _baseOffsetLeft - _saveIndicatorWidth, SlideDuration)
                .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);

            _slideTween.TweenInterval(SlideHoldDuration);

            // Slide back.
            _slideTween
                .TweenProperty(_widgetRoot, "offset_right", _baseOffsetRight, SlideDuration)
                .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);
            _slideTween.Parallel()
                .TweenProperty(_widgetRoot, "offset_left", _baseOffsetLeft, SlideDuration)
                .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);
        }
        catch (Exception e) { ModLog.Error("AfterGameSaved", e); }
    }

    private static string BuildBbcode(float cv)
    {
        float p2 = CumulativeDropChance(cv, 2);
        float p3 = CumulativeDropChance(cv, 3);
        float p4 = CumulativeDropChance(cv, 4);
        float p5 = CumulativeDropChance(cv, 5);

        static string Row(string nHex, int n, string pHex, int pct)
            => $"[center][color=#7A8FA8]Next [color={nHex}]{n}[/color] fights[/color]   [color=#{pHex}]{pct}%[/color][/center]";

        return
            "\n" +
            Row("#FFF6E2", 2, ColorHex(PotionChanceColor(p2)), (int)Math.Round(p2 * 100)) + "\n" +
            Row("#FFF6E2", 3, ColorHex(PotionChanceColor(p3)), (int)Math.Round(p3 * 100)) + "\n" +
            Row("#FFF6E2", 4, ColorHex(PotionChanceColor(p4)), (int)Math.Round(p4 * 100)) + "\n" +
            Row("#FFF6E2", 5, ColorHex(PotionChanceColor(p5)), (int)Math.Round(p5 * 100)) + "\n" +
            $"\n[center][font_size=11][color=#7A8FA8]Dropped this run   [color=#FFF6E2]{DtsRunData.PotionsDropped}[/color][/color][/font_size][/center]" +
            "\n[center][font_size=11][color=#7A8FA8]Elite rooms add [color=#FFF6E2]+12.5%[/color] at roll time[/color][/font_size][/center]";
    }

    // Debug: Invoked via reflection by the debug server to force the tooltip on screen
    private static void ForceShowTip()
    {
        if (_widgetRoot == null || !GodotObject.IsInstanceValid(_widgetRoot)) return;
        _tip.Show(_widgetRoot, "Potion Drop Chance", BuildBbcode(GetCurrentValue()));
    }

    private static float _lastKnownValue;

    private static float GetCurrentValue() => _lastKnownValue;

    private static void RefreshLabel(Label label, float v)
    {
        _lastKnownValue = v;
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
