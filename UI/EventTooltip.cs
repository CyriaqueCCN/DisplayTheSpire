using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Godot;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using DisplayTheSpire.Logging;

namespace DisplayTheSpire.UI;

// Hover tooltip for event names in the Event Overview Modal. Each tip is
// parented to the modal's full-screen OverlayLayer.
//
// Options resolve through reflection on GenerateInitialOptions when safe;
// side-effecting events (the one Custom-layout event plus the Default-
// layout events on the deny-list below) fall through to a LocTable read
// instead. Game color tags are converted to Godot
// [color=#HEX], animation tags are stripped, [font_size] is clamped to
// roughly 50 to 150 percent of the base, and [img] is removed.
internal static class EventTooltip
{
    private const float Width   = 360f;
    private const float OffsetX = 12f;

    private const int FontDesc     = 13;
    private const int FontOptTitle = 13;
    private const int FontOptDesc  = 12;
    private const int FontHeader   = 11;
    private const int FontBadge    = 11;
    private const int FontStatus   = 11;

    // Toggle for "[TT] EVENT_ID layout=..." traces emitted from Build.
    // Off by default: every modal hover would log a line, swamping the
    // session log. Flip to true while investigating a tooltip-shape bug
    // to see which event and LayoutType combo took which path.
    private const bool LogHovers = false;

    private static readonly string[] LeaveKeywords =
        ["LEAVE", "EXIT", "ABSTAIN", "GIVE_UP", "FLEE", "FIND_AN_EXIT"];

    private static readonly Color DangerColor = new Color("E05050");
    private static readonly Color LeaveColor  = DtsTheme.KeyLabel;
    // Locked options reuse the muted gold the status line uses for
    // "prerequisites not met". The two cases are thematically related
    // ("you don't qualify"), and the colour is distinct enough from the
    // slate-blue used for description text that a locked row never
    // reads as a sub-note of the option above it.
    private static readonly Color LockedColor = DtsTheme.FutureEvent;

    // Per-event variable overrides. Some events have CanonicalVars whose
    // ToString() returns empty until the event's own CalculateVars runs,
    // but invoking CalculateVars has gameplay-mutating side effects (e.g.
    // RelicTrader.NewRelics pulls from the relic pool via
    // RelicFactory.PullNextRelicFromFront, removing relics from the
    // run-long drop pool).
    // For those events the placeholders render as semantic descriptions
    // of the trade structure rather than ellipses. RelicTrader's
    // NewRelics uses RelicFactory.PullNextRelicFromFront(player), which
    // rolls rarity via RelicFactory.RollRarity over the player's
    // PlayerRng.Rewards stream:
    //     num <  0.50         -> Common   (50%)
    //     0.50 <= num < 0.83  -> Uncommon (33%)
    //     num >= 0.83         -> Rare     (17%)
    // Each of the three offered relics is an independent roll. The
    // PullFromFront fallthrough to a higher rarity when a deque is
    // empty is a rare late-run edge case and is ignored here.
    // RelicTrader's six StringVars default to "" pre-CalculateVars.
    // Render them as the localized "your tradable relic" /
    // "random new relic + rarity table" placeholders so the option
    // shape reads correctly without invoking CalculateVars (which
    // would pull from SharedRelicGrabBag).
    private static IReadOnlyDictionary<string, string> BuildRelicTraderOverrides()
    {
        string owned = DtsLoc.Tr("event.relic_trader.owned_var");
        string novel = DtsLoc.Tr("event.relic_trader.new_var");
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TopRelicOwned"]    = owned,
            ["TopRelicNew"]      = novel,
            ["MiddleRelicOwned"] = owned,
            ["MiddleRelicNew"]   = novel,
            ["BottomRelicOwned"] = owned,
            ["BottomRelicNew"]   = novel,
        };
    }

    private static IReadOnlyDictionary<string, string>? GetTemplateOverrides(string eventId) =>
        eventId switch
        {
            "RELIC_TRADER" => BuildRelicTraderOverrides(),
            _              => null,
        };

    // One tooltip is shared across every label attached by this modal.
    // When the user scans the list quickly, each label.MouseEntered
    // calls CloseActive() to kill the previous tip immediately;
    // otherwise the 350 ms close timer on the previous label leaves two
    // tooltips on screen at the same time.
    private static Control? _activeTip;

    private static void CloseActive()
    {
        if (_activeTip != null && GodotObject.IsInstanceValid(_activeTip))
            _activeTip.QueueFree();
        _activeTip = null;
    }

    // Drops any reference this class holds to a previously-built tooltip.
    // Call from the owning modal's Closed handler. The tooltip itself
    // dies with the modal backdrop, but the static _activeTip would
    // otherwise keep pointing at the freed Control until the next
    // hover. The ref is harmless thanks to IsInstanceValid in
    // CloseActive, but resetting keeps the static state honest.
    public static void Reset() => _activeTip = null;

    public static void Attach(
        Label      label,
        EventModel eventModel,
        RunState   runState,
        Control    tooltipHost,
        bool       seen     = false,
        bool       isFuture = false,
        bool       isPast   = false)
    {
        Control? tip     = null;
        bool     overLbl = false;
        bool     overTip = false;

        // Close only when the mouse is over neither the label nor the tip.
        void TryClose()
        {
            if (overLbl || overTip) return;
            if (tip != null && GodotObject.IsInstanceValid(tip))
            {
                // MouseEntered can miss in edge cases where the tip is
                // repositioned under a stationary mouse during the
                // layout pass. Cross-check the live mouse position
                // against the live rect before tearing the tip down.
                var mp = tip.GetGlobalMousePosition();
                if (tip.GetGlobalRect().HasPoint(mp))
                {
                    overTip = true;
                    return;
                }
                tip.QueueFree();
                if (_activeTip == tip) _activeTip = null;
            }
            // tip may already be invalid here. Either way, drop the ref.
            tip = null;
        }

        label.MouseFilter = Control.MouseFilterEnum.Pass;

        label.MouseEntered += () =>
        {
            try
            {
                overLbl = true;
                overTip = false;
                // Dismiss any tooltip still on screen, whether ours
                // (waiting on a close timer) or a sibling label's that
                // a fast mouse scan would otherwise leave overlapping.
                CloseActive();
                tip = null;
                if (!GodotObject.IsInstanceValid(tooltipHost)) return;

                var capturedTip = Build(eventModel, runState, seen, isFuture, isPast);
                tip = capturedTip;
                _activeTip = capturedTip;

                // MouseFilter=Stop lets the panel fire MouseEntered /
                // MouseExited so hovering the tip can keep it open, and
                // ensures scroll-wheel events reach the inner
                // ScrollContainer.
                capturedTip.MouseFilter  = Control.MouseFilterEnum.Stop;
                capturedTip.MouseEntered += () => { overTip = true; };
                capturedTip.MouseExited  += () => { overTip = false; TryClose(); };

                // Modulate.A=0 keeps the control fully laid out, text-
                // shaped and hit-testable while invisible. Visible=false
                // would disable hit-testing during the shaping window
                // and miss MouseEntered when the user glides quickly
                // from the label into the tip.
                capturedTip.Modulate = new Color(1f, 1f, 1f, 0f);
                tooltipHost.AddChild(capturedTip);
                capturedTip.ResetSize();

                // Position immediately so the hit rect is correct from
                // the first frame. Without this, AddChild leaves the
                // tip at (0,0) and any mouse event during shaping would
                // target the wrong rect and either miss the tip or
                // fire a spurious MouseEntered from the top-left.
                PositionTip(capturedTip, label, tooltipHost);

                // RTL FitContent resolves minimum height after the
                // render pipeline shapes the text. 100 ms fires well
                // after that pass.
                //
                // Lifetime note: SceneTree.CreateTimer returns a
                // SceneTreeTimer owned by the scene tree, not by
                // capturedTip. If the modal closes before the timer
                // fires, the timer still runs; the IsInstanceValid
                // guard inside the callback is what makes that safe.
                // A node-attached Timer would auto-cancel on free but
                // adding a child mid-AddChild has caused layout
                // glitches.
                var layoutTimer = capturedTip.GetTree().CreateTimer(0.1, false);
                layoutTimer.Timeout += () =>
                {
                    if (!GodotObject.IsInstanceValid(capturedTip)) return;

                    capturedTip.ResetSize();

                    var screen   = tooltipHost.GetViewport().GetVisibleRect().Size;
                    // 0.60 keeps tall tips clear of the screen edge.
                    // 22 px = ContentMarginTop (10) + ContentMarginBottom (10) + 2 spare.
                    float maxH   = screen.Y * 0.60f;
                    float innerH = maxH - 22f;

                    if (capturedTip.Size.Y > maxH
                        && capturedTip is PanelContainer panel2
                        && panel2.GetChildCount() > 0
                        && panel2.GetChild(0) is VBoxContainer vbox2)
                    {
                        panel2.RemoveChild(vbox2);
                        // ShrinkBegin so the vbox sizes to its own
                        // content rather than to the ScrollContainer
                        // height; otherwise there is nothing to scroll.
                        vbox2.SizeFlagsVertical   = Control.SizeFlags.ShrinkBegin;
                        vbox2.SizeFlagsHorizontal = Control.SizeFlags.Fill;

                        var scroll = new ScrollContainer
                        {
                            CustomMinimumSize   = new Vector2(0, innerH),
                            SizeFlagsHorizontal = Control.SizeFlags.Fill,
                            MouseFilter         = Control.MouseFilterEnum.Stop,
                        };
                        scroll.AddChild(vbox2);
                        panel2.AddChild(scroll);
                        capturedTip.ResetSize();
                    }

                    PositionTip(capturedTip, label, tooltipHost);
                    capturedTip.Modulate = new Color(1f, 1f, 1f, 1f);

                    // Even with hit-testing live during the alpha-zero
                    // window, MouseEntered can be missed if the rect
                    // shifts under a stationary mouse during the final
                    // PositionTip. Sync overTip with the live position
                    // at reveal.
                    var mousePos = capturedTip.GetGlobalMousePosition();
                    if (capturedTip.GetGlobalRect().HasPoint(mousePos))
                        overTip = true;
                };
            }
            catch (Exception ex) { ModLog.Error("EventTooltip.Enter", ex); }
        };

        label.MouseExited += () =>
        {
            overLbl = false;
            // Label and tip are separated by an OffsetX-pixel gap. A
            // synchronous rect check would close the tip while the
            // mouse is still in the gap. A short timer gives the
            // pointer time to cross: if tip.MouseEntered fires before
            // the timer expires, overTip is true and TryClose keeps
            // the tip alive.
            var current = tip;
            if (current == null || !GodotObject.IsInstanceValid(current))
            {
                TryClose();
                return;
            }
            // 0.35 s is enough for the pointer to cross the 12 px gap
            // on typical monitor refresh cadence.
            //
            // Lifetime note: SceneTreeTimer is tree-owned, so it fires
            // independent of `current`'s lifetime. The IsInstanceValid
            // check inside TryClose covers the modal-closed-mid-timer
            // case.
            var closeTimer = current.GetTree().CreateTimer(0.35, false);
            closeTimer.Timeout += TryClose;
        };
    }

    private static Control Build(EventModel ev, RunState rs, bool seen, bool isFuture, bool isPast)
    {
        // Diagnostic: one line per build, grep-friendly. Off by default.
        // CS0162 is suppressed because the const evaluates to false in
        // shipping builds; the warning IS the dead-code state we want.
#pragma warning disable CS0162
        if (LogHovers)
        {
            try { ModLog.Info($"[TT] {ev.Id.Entry} layout={ev.LayoutType}"); } catch { }
        }
#pragma warning restore CS0162

        var panel = new PanelContainer
        {
            CustomMinimumSize = new Vector2(Width, 0),
            ZIndex            = DtsTheme.ZModalPanel + 2,
            MouseFilter       = Control.MouseFilterEnum.Ignore,
        };
        panel.AddThemeStyleboxOverride("panel", MakePanelStyle());

        var vbox = new VBoxContainer
        {
            MouseFilter       = Control.MouseFilterEnum.Ignore,
            // ShrinkBegin stops PanelContainer from stretching the vbox
            // to fill the panel height; otherwise extra panel height
            // shows as empty space at the bottom.
            SizeFlagsVertical = Control.SizeFlags.ShrinkBegin,
        };
        vbox.AddThemeConstantOverride("separation", 8);
        panel.AddChild(vbox);

        // Type badge. Only Combat and Ancient are meaningful; Default
        // and Custom are not informative to a player.
        if (ev.LayoutType != EventLayoutType.Default
         && ev.LayoutType != EventLayoutType.Custom)
            vbox.AddChild(MakeTypeBadge(ev.LayoutType));

        AddDescription(vbox, ev);

        // Options. The reflection path on GenerateInitialOptions returns
        // EventOption objects with IsLocked / WillKillPlayer metadata.
        // Side-effecting events -- FakeMerchant (the only Custom-layout
        // event) plus the Default-layout events on the deny-list
        // (RelicTrader, RanwidTheElder, Neow, ...) -- implement
        // GenerateInitialOptions with gameplay-mutating side effects, so
        // for those the LocTable path is used instead. That path only
        // reads localization data and never touches the model.
        //
        // Templated placeholders that the event's own CalculateVars
        // would resolve are ellipsized by ResolveOrEllipsize; CalculateVars
        // is never called for the same side-effect reason. The player
        // sees the shape of the option ("Trade ... for ...") without
        // pre-rolled RNG outcomes being spoiled.
        var reflOpts = TryGetReflectionOptions(ev);
        if (reflOpts is { Count: > 0 })
        {
            vbox.AddChild(MakeSeparator());
            vbox.AddChild(MakeReflectionOptionsSection(reflOpts, ev));
        }
        else
        {
            var locOpts = GetLocTableOptions(ev, "INITIAL");
            if (locOpts.Count > 0)
            {
                vbox.AddChild(MakeSeparator());
                vbox.AddChild(MakeLocOptionsSection(DtsLoc.Tr("event.options_header"), locOpts));
            }
        }

        // Additional pages (non-INITIAL outcome screens). LocTable only,
        // so unresolved placeholders are ellipsized rather than rolled.
        foreach (var page in GetAdditionalPages(ev))
        {
            vbox.AddChild(MakeSeparator());
            vbox.AddChild(MakePageSection(page));
        }

        // Status line, always shown.
        vbox.AddChild(MakeSeparator());
        vbox.AddChild(MakeStatusLine(ev, rs, seen, isFuture, isPast));

        return panel;
    }

    private static void AddDescription(VBoxContainer vbox, EventModel ev)
    {
        try
        {
            var overrides = GetTemplateOverrides(ev.Id.Entry);
            // Standard LocString path.
            string text = "";
            try
            {
                var descStr = ev.InitialDescription;
                ev.DynamicVars.AddTo(descStr);
                text = ResolveOrEllipsize(descStr, overrides);
            }
            catch { }

            if (!string.IsNullOrWhiteSpace(text) && !IsPlaceholder(text))
            {
                vbox.AddChild(MakeRtl(text, FontDesc, Width - 32f));
                return;
            }

            // Fallback for events with no LocString content. Custom-
            // layout events keep their description in the UI scene
            // rather than in the events table.
            string? fallback = GetHardcodedDescription(ev.Id.Entry);
            if (fallback != null)
                vbox.AddChild(MakeRtl(fallback, FontDesc, Width - 32f));
        }
        catch (Exception ex) { ModLog.Error("EventTooltip.AddDescription", ex); }
    }

    // True when GetFormattedText returned its "key not found" sentinel
    // or when the LocTable carries stub text for a Custom-layout event
    // (the real content lives in a bespoke UI scene; the events table
    // stores "Placeholder" as filler that the standard event UI never
    // displays).
    private static bool IsPlaceholder(string s)
    {
        string t = s.Trim();
        return t is "???" or "??" or "?"
            || t.StartsWith("Placeholder", StringComparison.OrdinalIgnoreCase);
    }

    // Hardcoded descriptions for events whose standard LocString path
    // yields no usable text: FakeMerchant (Custom layout, text lives in
    // its bespoke UI scene, InitialDescription returns "???"/"Placeholder")
    // and the two trader events (RelicTrader, RanwidTheElder) whose
    // descriptions are template-only and skipped to avoid CalculateVars.
    // FakeMerchant's hidden Foul Potion interaction is intentionally not
    // described to keep the Easter-egg discovery mechanic intact.
    private static string? GetHardcodedDescription(string eventId)
    {
        string key = eventId switch
        {
            "FAKE_MERCHANT"    => "event.desc.fake_merchant",
            "RELIC_TRADER"     => "event.desc.relic_trader",
            "RANWID_THE_ELDER" => "event.desc.ranwid",
            _                  => null!,
        };
        if (key == null) return null;
        string s = DtsLoc.Tr(key);
        // DtsLoc.Tr returns "[key]" for missing entries; treat that as
        // absent so the standard LocString path is tried first.
        return s.StartsWith("[") && s.EndsWith("]") ? null : s;
    }

    // Default-layout event classes whose GenerateInitialOptions has
    // gameplay-mutating side effects. Calling them on hover would
    // advance per-event RNG, pull from the relic pool, or write cached
    // fields that the running event reads later, silently changing
    // the outcome the player gets when they actually pick the event.
    //
    // Specific offences by class name (verified against sts2.dll v0.103.3):
    //   ColorfulPhilosophers - Rng.NextInt + list.RemoveAt
    //   Darv                 - Rng.NextItem + UnstableShuffle + NextBool
    //                          (also reached via the AncientEventModel branch)
    //   Neow                 - NextItem + NextBool branches + UnstableShuffle.Take(2)
    //   Nonupeipe            - list.UnstableShuffle(Rng)
    //   Orobas               - NextItem + NextFloat gate + 3x NextItem from pools
    //   Pael                 - 3x NextItem from OptionPool1/2/3
    //   RanwidTheElder       - Rng.NextItem over Owner.Potions / tradable relics
    //   RelicTrader          - OwnedRelics StableShuffle(Rng) + NewRelics
    //                          RelicFactory.PullNextRelicFromFront x3 (pool pull)
    //   StoneOfAllTime       - mutates this.DrinkAndLiftPotion via NextItem
    //   Tanx                 - UnstableShuffle(Rng).Take(3)
    //   Tezcatara            - 3x NextItem across pools
    //   Vakuu                - 3x UnstableShuffle(Rng)
    //   WelcomeToWongos      - RelicFactory.PullNextRelicFromFront (pool corruption)
    //                          plus writes this.FeaturedItem
    //
    // RelicTrader and RanwidTheElder are Default layout (NOT Custom -- only
    // FakeMerchant overrides LayoutType => Custom). They must be on this
    // deny-list explicitly; the Custom check in TryGetReflectionOptions
    // does not cover them.
    //
    // For these, the LocTable path is used instead. If a future game
    // patch adds new side-effecting events, extend this set.
    private static readonly HashSet<string> _sideEffectingEventTypes =
        new(StringComparer.Ordinal)
        {
            "ColorfulPhilosophers",
            "Darv",
            "Neow",
            "Nonupeipe",
            "Orobas",
            "Pael",
            "RanwidTheElder",
            "RelicTrader",
            "StoneOfAllTime",
            "Tanx",
            "Tezcatara",
            "Vakuu",
            "WelcomeToWongos",
        };

    private static IReadOnlyList<EventOption>? TryGetReflectionOptions(EventModel ev)
    {
        // Never invoke GenerateInitialOptions on a Custom-layout event.
        // FakeMerchant is the only Custom-layout event: it owns its full
        // UI in a bespoke scene, so a hardcoded description is used and
        // GenerateInitialOptions is never reflected into.
        if (ev.LayoutType == EventLayoutType.Custom) return null;

        // Default-layout deny-list: see _sideEffectingEventTypes above.
        // This is what actually skips the side-effecting Default events
        // such as RelicTrader (pulls 3 relics from the pool) and
        // RanwidTheElder (advances the event Rng) -- both are Default
        // layout, so the Custom check above does NOT catch them.
        //
        // Defense in depth: even without this list the overview is safe,
        // because it shows canonical (immutable) ModelDb instances whose
        // GenerateInitialOptions throws (AssertMutable, or a null
        // base.Owner / base.Rng deref) before any RNG or pool mutation,
        // and the reflection call below is wrapped in try/catch. The
        // deny-list exists to skip that work cleanly and avoid logging an
        // exception on every hover.
        //
        // Match by GetType().Name (no namespace, no generic suffix; none
        // of the listed classes are generic) to avoid drift if events
        // get re-namespaced.
        if (_sideEffectingEventTypes.Contains(ev.GetType().Name)) return null;

        try
        {
            if (ev is AncientEventModel ancient)
            {
                // AllPossibleOptions is safe per-hover for every Ancient
                // event: subclasses implement it as a pure Concat of
                // relic-option helpers (no Rng, no pool pulls).
                var list = ancient.AllPossibleOptions?.ToList();
                return list?.Count > 0 ? list : null;
            }
            var method = ev.GetType().GetMethod(
                "GenerateInitialOptions",
                BindingFlags.NonPublic | BindingFlags.Instance);
            return method?.Invoke(ev, null) as IReadOnlyList<EventOption>;
        }
        catch { return null; }
    }

    private static Control MakeReflectionOptionsSection(IReadOnlyList<EventOption> options, EventModel ev)
    {
        var vbox = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        vbox.AddThemeConstantOverride("separation", 5);
        vbox.AddChild(MakeLabel(DtsLoc.Tr("event.options_header"), FontHeader, DtsTheme.KeyLabel));

        foreach (var opt in options)
        {
            if (opt.IsProceed) continue;
            try
            {
                // Locked rows render at the same level as the unlocked
                // sibling. The muted-gold LockedColor and smaller font
                // already convey "alternative outcome you don't qualify
                // for"; indenting would misread as a child note.
                vbox.AddChild(MakeReflectionOptionRow(opt, ev));
            }
            catch { /* skip malformed option */ }
        }
        return vbox;
    }

    private static Control MakeReflectionOptionRow(EventOption opt, EventModel ev)
    {
        var overrides = GetTemplateOverrides(ev.Id.Entry);
        bool isLocked = opt.IsLocked;
        bool isLeave  = IsLeaveKey(opt.TextKey);
        bool isDanger = opt.WillKillPlayer != null;
        // Locked beats leave or danger. A locked row is mutually
        // exclusive with the unlocked sibling above, so the visual
        // treatment should be distinctive without reading as a
        // subordinate note.
        Color titleColor = isLocked ? LockedColor
                         : isDanger ? DangerColor
                         : isLeave  ? LeaveColor
                         :            DtsTheme.Cream;
        int titleFontSize = isLocked ? FontOptDesc : FontOptTitle;

        var row = new VBoxContainer
        {
            MouseFilter         = Control.MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = Control.SizeFlags.Fill,
        };
        row.AddThemeConstantOverride("separation", 2);

        try
        {
            if (opt.Title == null) return row;
            ev.DynamicVars.AddTo(opt.Title);
            string titleText = ResolveOrEllipsize(opt.Title, overrides);
            if (string.IsNullOrWhiteSpace(titleText) || IsPlaceholder(titleText)) return row;
            var lbl = new Label
            {
                Text                = StripBbcode(titleText),
                AutowrapMode        = TextServer.AutowrapMode.Word,
                SizeFlagsHorizontal = Control.SizeFlags.Fill,
                MouseFilter         = Control.MouseFilterEnum.Ignore,
            };
            lbl.AddThemeFontSizeOverride("font_size", titleFontSize);
            lbl.AddThemeColorOverride("font_color", titleColor);
            row.AddChild(lbl);
        }
        catch { /* title unavailable */ }

        try
        {
            if (opt.Description == null) return row;
            ev.DynamicVars.AddTo(opt.Description);
            string descText = ResolveOrEllipsize(opt.Description, overrides);
            if (!string.IsNullOrWhiteSpace(descText))
            {
                var descRow = MakeIndentRow();
                descRow.AddChild(MakeRtl(descText, FontOptDesc, Width - 44f, DtsTheme.KeyLabel));
                row.AddChild(descRow);
            }
        }
        catch { /* description unavailable */ }

        return row;
    }

    private record LocOpt(string Title, string? Desc, bool IsLeave, bool IsLocked);

    private static List<LocOpt> GetLocTableOptions(EventModel ev, string pageId)
    {
        var result = new List<LocOpt>();
        try
        {
            const string tbl  = "events";
            var table         = LocManager.Instance.GetTable(tbl);
            var optPrefix     = $"{ev.Id.Entry}.pages.{pageId}.options.";
            var overrides     = GetTemplateOverrides(ev.Id.Entry);

            var optNames = table.Keys
                .Where(k => k.StartsWith(optPrefix) && k.EndsWith(".title"))
                .Select(k => k[optPrefix.Length..^".title".Length])
                .Distinct()
                // Alphabetical so POTION_LOCKED lands right after
                // POTION (because '_' sorts after end-of-string), so
                // locked variants sit under their unlocked siblings.
                .OrderBy(n => n)
                .ToList();

            foreach (var name in optNames)
            {
                string fullKey = optPrefix + name;
                string title   = "";
                string? desc   = null;

                try
                {
                    var ts = new LocString(tbl, fullKey + ".title");
                    ev.DynamicVars.AddTo(ts);
                    title = ResolveOrEllipsize(ts, overrides);
                }
                catch { continue; }

                try
                {
                    var ds = LocString.GetIfExists(tbl, fullKey + ".description");
                    if (ds != null) { ev.DynamicVars.AddTo(ds); desc = ResolveOrEllipsize(ds, overrides); }
                }
                catch { }

                // Naming convention in the events LocTable: locked
                // variants use `_LOCKED` suffixed keys (POTION /
                // POTION_LOCKED). The unlocked title shows when the
                // precondition is met; the locked title explains why
                // the option is greyed out. Both render, but the
                // locked variant is sub-styled (smaller, grey).
                bool isLocked = name.EndsWith("_LOCKED",
                    StringComparison.OrdinalIgnoreCase);

                if (!string.IsNullOrWhiteSpace(title) && !IsPlaceholder(title))
                    result.Add(new LocOpt(title,
                        string.IsNullOrWhiteSpace(desc) ? null : desc,
                        IsLeaveKey(fullKey),
                        isLocked));
            }
        }
        catch (Exception ex) { ModLog.Error("EventTooltip.GetLocTableOptions", ex); }
        return result;
    }

    private static Control MakeLocOptionsSection(string header, List<LocOpt> opts)
    {
        var vbox = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        vbox.AddThemeConstantOverride("separation", 5);
        vbox.AddChild(MakeLabel(header, FontHeader, DtsTheme.KeyLabel));

        // Locked rows render at the same level as their unlocked
        // sibling. The muted-gold LockedColor and smaller font already
        // convey "alternative state you don't qualify for"; indenting
        // would misread as a child note of the option above.
        foreach (var opt in opts) vbox.AddChild(MakeLocOptionRow(opt));
        return vbox;
    }

    private static Control MakeLocOptionRow(LocOpt opt)
    {
        // Locked beats leave colouring: a locked row is mutually
        // exclusive with its unlocked sibling, so the same muted gold
        // used for unmet prerequisites is consistent.
        Color titleColor = opt.IsLocked ? LockedColor
                         : opt.IsLeave  ? LeaveColor
                         :                DtsTheme.Cream;
        int titleFontSize = opt.IsLocked ? FontOptDesc : FontOptTitle;

        var row = new VBoxContainer
        {
            MouseFilter         = Control.MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = Control.SizeFlags.Fill,
        };
        row.AddThemeConstantOverride("separation", 2);

        var lbl = new Label
        {
            Text                = StripBbcode(opt.Title),
            AutowrapMode        = TextServer.AutowrapMode.Word,
            SizeFlagsHorizontal = Control.SizeFlags.Fill,
            MouseFilter         = Control.MouseFilterEnum.Ignore,
        };
        lbl.AddThemeFontSizeOverride("font_size", titleFontSize);
        lbl.AddThemeColorOverride("font_color", titleColor);
        row.AddChild(lbl);

        if (!string.IsNullOrWhiteSpace(opt.Desc))
        {
            var descRow = MakeIndentRow();
            descRow.AddChild(MakeRtl(opt.Desc, FontOptDesc, Width - 44f, DtsTheme.KeyLabel));
            row.AddChild(descRow);
        }

        return row;
    }

    private record PageInfo(string PageId, string Desc, List<LocOpt> Options);

    private static List<PageInfo> GetAdditionalPages(EventModel ev)
    {
        var result = new List<PageInfo>();
        try
        {
            const string tbl  = "events";
            var table         = LocManager.Instance.GetTable(tbl);
            var prefix        = ev.Id.Entry + ".pages.";

            var evKeys    = table.Keys.Where(k => k.StartsWith(prefix)).ToList();
            var overrides = GetTemplateOverrides(ev.Id.Entry);

            var pageIds = evKeys
                .Where(k => k.EndsWith(".description"))
                .Select(k =>
                {
                    var rest = k[prefix.Length..];
                    var dot  = rest.IndexOf('.');
                    return dot >= 0 ? rest[..dot] : null;
                })
                .Where(p => p != null && p != "INITIAL")
                .Distinct()
                .OrderBy(p => p)
                .ToList();

            foreach (var pageId in pageIds)
            {
                string desc = "";
                try
                {
                    var ds = new LocString(tbl, prefix + pageId + ".description");
                    ev.DynamicVars.AddTo(ds);
                    desc = ResolveOrEllipsize(ds, overrides);
                }
                catch { }

                var opts = GetLocTableOptions(ev, pageId!);
                result.Add(new PageInfo(pageId!, desc, opts));
            }
        }
        catch (Exception ex) { ModLog.Error("EventTooltip.GetAdditionalPages", ex); }
        return result;
    }

    private static Control MakePageSection(PageInfo page)
    {
        var vbox = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        vbox.AddThemeConstantOverride("separation", 5);
        // Page IDs in events.json are keys (GOLD, POTION, RELIC,
        // GRAB_POTIONS, etc.). They are not localized in the game's
        // data, so we wrap them with a localized "Outcome" prefix
        // and feed the underscore-flattened key as the format arg.
        // English keeps the bracket-only form; Chinese (and any
        // future locale) prepends a translated header so the bracket
        // content is recognisable as an outcome marker.
        vbox.AddChild(MakeLabel(
            DtsLoc.Tr("event.outcome_section", page.PageId.Replace('_', ' ')),
            FontHeader, DtsTheme.KeyLabel));
        if (!string.IsNullOrWhiteSpace(page.Desc))
            vbox.AddChild(MakeRtl(page.Desc, FontDesc, Width - 32f));
        if (page.Options.Count > 0)
            vbox.AddChild(MakeLocOptionsSection(DtsLoc.Tr("event.options_header"), page.Options));
        return vbox;
    }

    private static Control MakeStatusLine(EventModel ev, RunState rs,
        bool seen, bool isFuture, bool isPast)
    {
        string text;
        Color  color;

        if (seen)
        {
            text  = DtsLoc.Tr("event.status.seen");
            color = DtsTheme.KeyLabel;
        }
        else if (isPast)
        {
            text  = DtsLoc.Tr("event.status.missed");
            color = DangerColor;
        }
        else if (isFuture)
        {
            text  = DtsLoc.Tr("event.status.may_encounter");
            color = DtsTheme.EliteYellow;
        }
        else
        {
            bool allowed = true;
            try { allowed = ev.IsAllowed(rs); } catch { }
            text  = allowed
                ? DtsLoc.Tr("event.status.may_encounter")
                : DtsLoc.Tr("event.status.locked");
            color = allowed ? DtsTheme.EliteYellow : DtsTheme.FutureEvent;
        }

        var lbl = new Label
        {
            Text                = text,
            AutowrapMode        = TextServer.AutowrapMode.Word,
            SizeFlagsHorizontal = Control.SizeFlags.Fill,
            MouseFilter         = Control.MouseFilterEnum.Ignore,
        };
        lbl.AddThemeFontSizeOverride("font_size", FontStatus);
        lbl.AddThemeColorOverride("font_color", color);
        return lbl;
    }

    private static Control MakeTypeBadge(EventLayoutType layout) => layout switch
    {
        EventLayoutType.Combat  => MakeLabel(DtsLoc.Tr("event.badge.combat"),  FontBadge, DangerColor),
        EventLayoutType.Ancient => MakeLabel(DtsLoc.Tr("event.badge.ancient"), FontBadge, DtsTheme.EliteYellow),
        _                       => MakeLabel(layout.ToString().ToUpperInvariant(), FontBadge, DtsTheme.KeyLabel),
    };

    private static void PositionTip(Control tip, Label label, Control host)
    {
        tip.ResetSize();

        var labelRect = label.GetGlobalRect();
        var hostRect  = host.GetGlobalRect();
        var screen    = host.GetViewport().GetVisibleRect().Size;

        float x = labelRect.End.X + OffsetX - hostRect.Position.X;
        float y = labelRect.Position.Y       - hostRect.Position.Y;

        if (x + Width > screen.X - 8f)
            x = labelRect.Position.X - OffsetX - Width - hostRect.Position.X;

        // PositionTip only runs from the layout timer, after RTL shaping
        // and the 60% screen-height cap, so tip.Size.Y is real and
        // reasonable. Math.Max guards against the theoretical case of
        // Size.Y still exceeding screen.Y (Math.Clamp would throw with
        // min > max).
        float maxY = screen.Y - tip.Size.Y - 4f;
        y = Math.Clamp(y, 4f, Math.Max(4f, maxY));

        tip.Position = new Vector2(x, y);
    }

    private static readonly Dictionary<string, string> _colorMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            { "gold",   "#EFC851" },
            { "red",    "#FF5555" },
            { "green",  "#7FFF00" },
            { "blue",   "#87CEEB" },
            { "aqua",   "#2AEBBE" },
            { "orange", "#FFA518" },
            { "pink",   "#FF78A0" },
            { "purple", "#EE82EE" },
        };

    private static readonly Regex _colorOpenRx  = new(
        @"\[(gold|red|green|blue|aqua|orange|pink|purple)\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex _colorCloseRx = new(
        @"\[/(gold|red|green|blue|aqua|orange|pink|purple)\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex _imgRx = new(
        @"\[img[^\]]*\].*?\[/img\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex _fontSizeRx = new(
        @"\[font_size=(\d+)\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex _stripTagRx = new(
        @"\[/?(?:jitter|sine|wave|shake|tornado|rainbow|pulse|ghost|flicker|twitch|wiggle|bounce" +
        @"|fade_in|fly_in|thinky_dots|ancient_banner)[^\]]*\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex _anyTagRx = new(
        @"\[[^\]]*\]", RegexOptions.Compiled);
    // Unresolved SmartFormat placeholder: {VarName}. When a LocString's
    // template references DynamicVars that only the event's own
    // CalculateVars would populate, SmartFormat throws. The fallback is
    // to keep the raw template and replace these with ellipses, so the
    // player sees the option's shape ("Insert ... Potion") without the
    // mod calling CalculateVars and spoiling the pre-rolled outcome.
    private static readonly Regex _unresolvedVarRx = new(
        @"\{[A-Za-z_][A-Za-z0-9_]*\}", RegexOptions.Compiled);

    private static string ResolveOrEllipsize(LocString ls,
        IReadOnlyDictionary<string, string>? overrides = null)
    {
        string raw = "";
        try { raw = ls.GetRawText() ?? ""; } catch { }
        if (string.IsNullOrEmpty(raw)) return "";

        // Two SmartFormat failure modes to dodge:
        //
        //  1. Variable missing entirely - SmartFormat throws and writes
        //     "[ERROR] Localization formatting error" to the game log
        //     on every hover. The try/catch around it hides the throw
        //     but the log noise piles up.
        //
        //  2. Variable present but ToString() empty - canonical case is
        //     RelicTrader's six StringVars (TopRelicOwned, etc.), which
        //     default to "". SmartFormat happily renders them as empty,
        //     producing "Trade  for " (double-space gap).
        //
        // Strategy: pre-scan the {VarName} placeholders. If every one
        // resolves to a non-empty string and there are no overrides,
        // run SmartFormat (it preserves rich formatting like {Gold:N0},
        // plurals, conditionals). Otherwise do a manual replace,
        // consulting overrides first, then DynamicVars, then ellipsis.
        bool hasOverrides = overrides != null && overrides.Count > 0;

        if (!hasOverrides)
        {
            var matches = _unresolvedVarRx.Matches(raw);
            bool needsManual = false;
            foreach (Match m in matches)
            {
                string name = m.Value.Trim('{', '}');
                if (!ls.Variables.TryGetValue(name, out var v) || v == null)
                { needsManual = true; break; }
                string s;
                try { s = v.ToString() ?? ""; } catch { s = ""; }
                if (string.IsNullOrWhiteSpace(s)) { needsManual = true; break; }
            }

            if (!needsManual)
            {
                try { return ls.GetFormattedText(); }
                catch { /* fall through to manual */ }
            }
        }

        return _unresolvedVarRx.Replace(raw, m =>
        {
            string name = m.Value.Trim('{', '}');
            // Override first: lets event-specific resolvers inject
            // semantic placeholders (e.g. "one of your tradable
            // relics") that read better than ellipses for templated
            // empty StringVars.
            if (overrides != null && overrides.TryGetValue(name, out var sub))
                return sub;
            if (!ls.Variables.TryGetValue(name, out var v) || v == null) return "\u2026";
            string s;
            try { s = v.ToString() ?? ""; } catch { s = ""; }
            return string.IsNullOrWhiteSpace(s) ? "\u2026" : s;
        });
    }

    private static string ConvertBbcode(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = _imgRx.Replace(text, "");
        text = _colorOpenRx.Replace(text, m =>
            _colorMap.TryGetValue(m.Groups[1].Value, out var hex) ? $"[color={hex}]" : m.Value);
        text = _colorCloseRx.Replace(text, "[/color]");
        int minSz = Math.Max(6, FontDesc / 2);
        int maxSz = FontDesc + FontDesc / 2;
        text = _fontSizeRx.Replace(text, m =>
            int.TryParse(m.Groups[1].Value, out int sz)
                ? $"[font_size={Math.Clamp(sz, minSz, maxSz)}]"
                : m.Value);
        text = _stripTagRx.Replace(text, "");
        return text;
    }

    private static string StripBbcode(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return _anyTagRx.Replace(ConvertBbcode(text), "");
    }

    private static RichTextLabel MakeRtl(string text, int fontSize, float minWidth,
        Color? color = null)
    {
        var rtl = new RichTextLabel
        {
            BbcodeEnabled       = true,
            FitContent          = true,
            ScrollActive        = false,
            AutowrapMode        = TextServer.AutowrapMode.Word,
            CustomMinimumSize   = new Vector2(minWidth, 0),
            SizeFlagsHorizontal = Control.SizeFlags.Fill,
            MouseFilter         = Control.MouseFilterEnum.Ignore,
            Text                = ConvertBbcode(text),
        };
        rtl.AddThemeFontSizeOverride("normal_font_size", fontSize);
        rtl.AddThemeColorOverride("default_color", color ?? DtsTheme.Cream);
        return rtl;
    }

    private static HBoxContainer MakeIndentRow()
    {
        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 0);
        row.AddChild(new Control
        {
            CustomMinimumSize = new Vector2(10f, 0),
            MouseFilter       = Control.MouseFilterEnum.Ignore,
        });
        return row;
    }

    private static Label MakeLabel(string text, int fontSize, Color color)
    {
        var lbl = new Label
        {
            Text         = text,
            AutowrapMode = TextServer.AutowrapMode.Off,
            MouseFilter  = Control.MouseFilterEnum.Ignore,
        };
        lbl.AddThemeFontSizeOverride("font_size", fontSize);
        lbl.AddThemeColorOverride("font_color", color);
        return lbl;
    }

    private static ColorRect MakeSeparator() => new ColorRect
    {
        Color               = DtsTheme.SeparatorLine,
        CustomMinimumSize   = new Vector2(0, 1),
        SizeFlagsHorizontal = Control.SizeFlags.Fill,
        MouseFilter         = Control.MouseFilterEnum.Ignore,
    };

    private static StyleBoxFlat MakePanelStyle() => new StyleBoxFlat
    {
        BgColor                 = new Color(0.06f, 0.11f, 0.17f, 0.97f),
        CornerRadiusTopLeft     = DtsTheme.CornerRadius,
        CornerRadiusTopRight    = DtsTheme.CornerRadius,
        CornerRadiusBottomLeft  = DtsTheme.CornerRadius,
        CornerRadiusBottomRight = DtsTheme.CornerRadius,
        BorderWidthLeft         = 1, BorderWidthRight  = 1,
        BorderWidthTop          = 1, BorderWidthBottom = 1,
        BorderColor             = DtsTheme.Border,
        ContentMarginLeft       = 12f, ContentMarginRight  = 12f,
        ContentMarginTop        = 10f, ContentMarginBottom = 10f,
    };

    private static bool IsLeaveKey(string textKey)
    {
        int    dot = textKey.LastIndexOf('.');
        string seg = dot >= 0 ? textKey[(dot + 1)..] : textKey;
        return LeaveKeywords.Any(kw =>
            seg.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0);
    }
}
