using System;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using DisplayTheSpire.Logging;

namespace DisplayTheSpire.UI;

/// <summary>
/// Wraps the game's native <c>hover_tip.tscn</c> scene so the tooltips are
/// visually identical to built-in game tooltips (same panel, border, font).
/// The tip is added to NGame.Instance.HoverTipsContainer and positioned via
/// GlobalPosition as NHoverTipSet does internally.
/// </summary>
internal sealed class DtsNativeTip
{
    private static PackedScene? _scene;
    private Control? _instance;

    private static PackedScene? GetScene()
    {
        if (_scene != null) return _scene;
        try { _scene = ResourceLoader.Load<PackedScene>("res://scenes/ui/hover_tip.tscn"); }
        catch (Exception e) { ModLog.Error("DtsNativeTip: hover_tip.tscn load failed", e); }
        return _scene;
    }

    /// <summary>Height of the rendered tip in screen pixels (0 if not visible)</summary>
    public float Height => (_instance != null && GodotObject.IsInstanceValid(_instance))
        ? _instance.Size.Y : 0f;

    /// <summary>
    /// Shows a native tooltip positioned just below <paramref name="anchor"/>,
    /// horizontally centered on it and clamped to the viewport.
    /// </summary>
    /// <param name="anchor">The button/control to position below.</param>
    /// <param name="title">Header text. Empty or null hides the title bar entirely.</param>
    /// <param name="bbcode">Body content in Godot 4 BBcode. Empty or null hides the body.</param>
    /// <param name="minWidth">
    ///   If &gt; 0, forces the tip to be at least this wide (px).
    ///   Also propagates to the inner RTL so wide BBcode tables render correctly.
    /// </param>
    /// <param name="yOffset">Extra vertical offset below the anchor (stacked tooltips).</param>
    public void Show(Control anchor, string title, string bbcode, float minWidth = 0f, float yOffset = 0f)
    {
        Hide();
        try
        {
            var scene     = GetScene();
            var container = NGame.Instance?.HoverTipsContainer;
            if (scene == null || container == null) return;

            _instance = scene.Instantiate<Control>(PackedScene.GenEditState.Disabled);
            if (minWidth > 0f)
                _instance.CustomMinimumSize = new Vector2(minWidth, 0f);
            // Prevent the HoverTipsContainer from stretching the tooltip vertically.
            _instance.SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;
            container.AddChild(_instance);

            // Hide the icon slot (not used by our tooltips)
            if (_instance.HasNode("%Icon"))
                _instance.GetNode<Control>("%Icon").Visible = false;

            // Title - ExpandFill so HorizontalAlignment.Center fills the full tooltip width.
            var titleNode                 = _instance.GetNode<Label>("%Title");
            titleNode.Text                = title ?? "";
            titleNode.Visible             = !string.IsNullOrEmpty(title);
            titleNode.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            titleNode.HorizontalAlignment = HorizontalAlignment.Center;

            // Description body (BBcode)
            var descNode           = _instance.GetNode<RichTextLabel>("%Description");
            descNode.BbcodeEnabled = true;
            // Disable autowrap: table cells must never split words mid-cell.
            // Explicit \n in BBcode still creates new lines; only intra-cell wrapping is suppressed.
            descNode.AutowrapMode  = TextServer.AutowrapMode.Off;
            if (string.IsNullOrEmpty(bbcode))
            {
                // No body: hide the RTL AND zero its minimum size so the VBoxContainer
                // doesn't reserve any vertical space for it (hidden nodes still occupy
                // their minimum size in Godot containers).
                descNode.Visible           = false;
                descNode.CustomMinimumSize = Vector2.Zero;
            }
            else
            {
                descNode.Visible = true;
                if (minWidth > 0f)
                {
                    // Propagate width to the inner RTL so BBcode table columns span the
                    // full tooltip width instead of only fitting their content.
                    descNode.CustomMinimumSize   = new Vector2(minWidth - 28f, 0f);
                    descNode.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                }
            }
            descNode.Text = bbcode;

            _instance.ResetSize();

            // Center the tip below the anchor and clamp to viewport bounds.
            float tipW = _instance.Size.X > 1f ? _instance.Size.X : 360f;
            float x    = anchor.GlobalPosition.X + anchor.Size.X / 2f - tipW / 2f;
            try
            {
                var vr = anchor.GetViewportRect();
                x = Math.Clamp(x, 4f, vr.Size.X - tipW - 4f);
            }
            catch { /* viewport not ready - use unclamped x */ }
            _instance.GlobalPosition = new Vector2(x, anchor.GlobalPosition.Y + anchor.Size.Y + 8f + yOffset);
        }
        catch (Exception e) { ModLog.Error("DtsNativeTip.Show", e); }
    }

    /// <summary>Updates the BBcode body in the existing tip</summary>
    public void UpdateBbcode(string bbcode)
    {
        if (_instance == null || !GodotObject.IsInstanceValid(_instance)) return;
        try { _instance.GetNode<RichTextLabel>("%Description").Text = bbcode; }
        catch (Exception e) { ModLog.Error("DtsNativeTip.UpdateBbcode", e); }
    }

    public void Hide()
    {
        if (_instance != null && GodotObject.IsInstanceValid(_instance))
            _instance.QueueFree();
        _instance = null;
    }

    public bool IsVisible => _instance != null && GodotObject.IsInstanceValid(_instance);
}
