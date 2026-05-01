using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using DisplayTheSpire.Logging;

namespace DisplayTheSpire.UI;

// Two-by-two grid event overview:
//   top-left:    current act (act-specific events)
//   top-right:   shared events (all acts)
//   bottom row:  the other two act slots in chronological order
//
// Per cell colour:
//   gold (#E8C840)      unseen, reachable
//   muted (#7A8FA8)     already seen this run
//   amber (#B89A58)     future act, or prereqs not met
//   dim (#445566)       missed in a past act
//
// Past-act cells show only the variant the player actually went through.
// Future-act cells merge the predetermined act with the slot's default
// act so neither is revealed as the upcoming choice. New variants
// (e.g. 2B, 3B) appear automatically through GetDefaultList.
//
// No event has an ascension-gated IsAllowed check yet, so the per-act lists
// are ascension-independent.
internal static class EventOverviewModal
{
    internal static DtsModal Show(Control host, IRunState runState)
    {
        try
        {
            // VisitedEventIds is not exposed on IRunState, so a cast to
            // RunState is required. The cast is safe because the
            // NTopBar.Initialize guard ensures Players.Count > 0, which
            // implies runState is a real RunState rather than NullRunState.
            var rs      = (RunState)runState;
            var visited = rs.VisitedEventIds;   // live IReadOnlySet from the run

            int curIdx     = runState.CurrentActIndex;  // 0-based
            var defaultList = ActModel.GetDefaultList(); // Overgrowth, Hive, Glory

            // Indices of the two non-current act slots, in chronological order.
            var otherSlots = Enumerable.Range(0, runState.Acts.Count)
                .Where(i => i != curIdx)
                .OrderBy(i => i)
                .ToList();

            // Size the panel to roughly two thirds of the logical canvas.
            // GetVisibleRect().Size returns the content-scale canvas
            // (e.g. 1920x1080) regardless of physical resolution or
            // fullscreen state, matching the pattern used by HoverTip,
            // NCardPlay, NEndTurnButton and other game systems.
            var screen   = host.GetViewport().GetVisibleRect().Size;
            float panelW = screen.X * 2f / 3f;
            float panelH = screen.Y * 2f / 3f;

            var modal   = new DtsModal("Event Overview", panelW, panelH);
            var overlay = modal.OverlayLayer;   // backdrop, used to host tooltips

            var rows = new VBoxContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.Fill,
                SizeFlagsVertical   = Control.SizeFlags.ExpandFill,
            };
            rows.AddThemeConstantOverride("separation", 8);
            modal.Content.AddChild(rows);

            // Top row: current act + shared.
            string curActName = runState.Act.Title.GetFormattedText();
            rows.AddChild(BuildRow(
                BuildSection(
                    $"Current Act: {curActName}",
                    SortByTitle(runState.Act.AllEvents),
                    visited,
                    isFuture: false, isPast: false,
                    rs, overlay),
                BuildSection(
                    "Shared Events",
                    SortByTitle(ModelDb.AllSharedEvents),
                    visited,
                    isFuture: false, isPast: false,
                    rs, overlay)
            ));

            rows.AddChild(HRule());

            // Bottom row: the other two act slots.
            var bottomSections = otherSlots.Select(slotIdx =>
            {
                ActModel actual   = runState.Acts[slotIdx];
                bool     isFuture = slotIdx > curIdx;

                List<EventModel> events;
                if (isFuture)
                {
                    // Future act: the player does not yet know which
                    // variant they will get. Merge the predetermined act
                    // with the slot default so neither variant is
                    // revealed as the chosen one. When the two are
                    // identical the union deduplicates to a single set.
                    ActModel primary = slotIdx < defaultList.Count
                        ? defaultList[slotIdx]
                        : actual;
                    var merged = actual.AllEvents
                        .Concat(primary.AllEvents)
                        .GroupBy(e => e.Id)
                        .Select(g => g.First());
                    events = SortByTitle(merged);
                }
                else
                {
                    // Past act: only the variant actually played is
                    // shown.
                    events = SortByTitle(actual.AllEvents);
                }

                bool isPast = slotIdx < curIdx;
                return BuildSection(ActTitle(actual, slotIdx, curIdx), events, visited, isFuture, isPast, rs, overlay);
            }).ToList();

            rows.AddChild(BuildRow(bottomSections[0], bottomSections[1]));

            // Modal close frees the backdrop and every tooltip parented
            // to OverlayLayer. EventTooltip caches the last-shown tip in
            // a static field for cross-row dismissal; that ref would
            // outlive the modal and only get cleared on the next hover.
            // Reset clears it now so no stale ref ever lingers.
            modal.Closed += EventTooltip.Reset;

            modal.Show(host);
            return modal;
        }
        catch (Exception ex)
        {
            ModLog.Error("EventOverviewModal.Show", ex);
            var fallback = new DtsModal("Event Overview", 400f, 120f);
            fallback.Show(host);
            return fallback;
        }
    }

    // Alphabetical sort by event title. LocString.GetFormattedText runs
    // SmartFormat (parses the template each call); a naive
    // OrderBy(e => e.Title.GetFormattedText()) invokes it O(N log N)
    // times during the merge sort. Resolving the title once into a tuple
    // and sorting on the cached string drops it to one call per event.
    private static List<EventModel> SortByTitle(IEnumerable<EventModel> events)
    {
        var keyed = new List<(string Key, EventModel Event)>();
        foreach (var e in events)
        {
            string key;
            try   { key = e.Title.GetFormattedText() ?? ""; }
            catch { key = ""; }
            keyed.Add((key, e));
        }
        keyed.Sort(static (a, b) =>
            string.Compare(a.Key, b.Key, StringComparison.CurrentCultureIgnoreCase));
        var result = new List<EventModel>(keyed.Count);
        foreach (var (_, e) in keyed) result.Add(e);
        return result;
    }

    // "Act N: name" once the act has been or is being played; "Act N"
    // alone for future slots so the chosen variant is not leaked.
    private static string ActTitle(ActModel act, int slotIdx, int curIdx)
    {
        int num = slotIdx + 1;
        return slotIdx <= curIdx
            ? $"Act {num}: {act.Title.GetFormattedText()}"
            : $"Act {num}";
    }

    // Two equal-width columns separated by a 1px vertical rule.
    private static HBoxContainer BuildRow(VBoxContainer left, VBoxContainer right)
    {
        var row = new HBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.Fill,
            SizeFlagsVertical   = Control.SizeFlags.ExpandFill,
        };
        row.AddThemeConstantOverride("separation", 16);
        row.AddChild(left);
        row.AddChild(VRule());
        row.AddChild(right);
        return row;
    }

    // One cell: header (with optional [seen / total] count) -> separator
    // -> scrollable list. events must be pre-sorted and deduplicated by
    // the caller. visited is the live IReadOnlySet from
    // RunState.VisitedEventIds. rs and overlay are forwarded to
    // EventTooltip.Attach for each event row.
    private static VBoxContainer BuildSection(
        string title,
        List<EventModel> events,
        IReadOnlySet<ModelId> visited,
        bool isFuture,
        bool isPast,
        RunState rs,
        Control overlay)
    {
        // Skip the count for future slots: it would leak the merged-pool size.
        int seenCount = isFuture ? 0 : events.Count(e => visited.Contains(e.Id));

        var col = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical   = Control.SizeFlags.ExpandFill,
        };
        col.AddThemeConstantOverride("separation", 4);

        // Header.
        var header = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.Fill };

        var titleLabel = new Label
        {
            Text                = title,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            AutowrapMode        = TextServer.AutowrapMode.Off,
        };
        titleLabel.AddThemeFontSizeOverride("font_size", 14);
        titleLabel.AddThemeColorOverride("font_color",         DtsTheme.White);
        titleLabel.AddThemeColorOverride("font_outline_color", DtsTheme.Outline);
        titleLabel.AddThemeConstantOverride("outline_size",    DtsTheme.OutlineSizeSmall);
        header.AddChild(titleLabel);

        // [seen / total] counter, shown only for past and current acts.
        // total is the full pool count (AllEvents merged across variants
        // where applicable).
        if (!isFuture)
        {
            var countLabel = new Label
            {
                Text                = $"[{seenCount} / {events.Count}]",
                SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd,
                AutowrapMode        = TextServer.AutowrapMode.Off,
                VerticalAlignment   = VerticalAlignment.Bottom,
            };
            countLabel.AddThemeFontSizeOverride("font_size", 12);
            countLabel.AddThemeColorOverride("font_color", DtsTheme.KeyLabel);
            header.AddChild(countLabel);
        }

        col.AddChild(header);
        col.AddChild(HRule());

        // Scrollable event list
        var scroll = new ScrollContainer
        {
            SizeFlagsVertical   = Control.SizeFlags.ExpandFill,
            SizeFlagsHorizontal = Control.SizeFlags.Fill,
        };

        var list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.Fill };
        list.AddThemeConstantOverride("separation", 2);

        foreach (var ev in events)
        {
            bool seen = !isFuture && visited.Contains(ev.Id);
            var lbl = new Label
            {
                Text                = ev.Title.GetFormattedText(),
                AutowrapMode        = TextServer.AutowrapMode.Off,
                SizeFlagsHorizontal = Control.SizeFlags.Fill,
            };
            lbl.AddThemeFontSizeOverride("font_size", 13);
            // Four states:
            //   future or gated -> amber (#B89A58)
            //   seen            -> grey  (#7A8FA8)
            //   missed          -> dim   (#445566)
            //   reachable       -> gold  (#E8C840)
            Color labelColor = isFuture           ? DtsTheme.FutureEvent
                             : seen               ? DtsTheme.KeyLabel
                             : isPast             ? DtsTheme.MissedEvent
                             : !IsAllowed(ev, rs) ? DtsTheme.FutureEvent
                             :                      DtsTheme.EliteYellow;
            lbl.AddThemeColorOverride("font_color", labelColor);
            list.AddChild(lbl);

            // Hover tooltip: description, options, prerequisites.
            EventTooltip.Attach(lbl, ev, rs, overlay,
                seen:     seen,
                isFuture: isFuture,
                isPast:   isPast);
        }

        scroll.AddChild(list);
        col.AddChild(scroll);
        return col;
    }

    // Safe wrapper around EventModel.IsAllowed. Returns true on any
    // exception so a thrown check never accidentally hides a reachable
    // event.
    private static bool IsAllowed(EventModel ev, RunState rs)
    {
        try   { return ev.IsAllowed(rs); }
        catch { return true; }
    }

    private static ColorRect HRule() => new ColorRect
    {
        Color               = DtsTheme.SeparatorLine,
        CustomMinimumSize   = new Vector2(0, 1),
        SizeFlagsHorizontal = Control.SizeFlags.Fill,
        MouseFilter         = Control.MouseFilterEnum.Ignore,
    };

    private static ColorRect VRule() => new ColorRect
    {
        Color             = DtsTheme.SeparatorLine,
        CustomMinimumSize = new Vector2(1, 0),
        SizeFlagsVertical = Control.SizeFlags.Fill,
        MouseFilter       = Control.MouseFilterEnum.Ignore,
    };
}
