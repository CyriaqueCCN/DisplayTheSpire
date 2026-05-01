using System;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using DisplayTheSpire.Logging;

namespace DisplayTheSpire.UI;

// Wraps the game's native hover_tip.tscn so mod tooltips share the panel,
// border and font of vanilla tooltips. Instances live under
// NGame.Instance.HoverTipsContainer and are positioned via GlobalPosition,
// matching how NHoverTipSet places its own tips.
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

    // Rendered tip height in screen pixels, or 0 when the tip is not on screen.
    public float Height => (_instance != null && GodotObject.IsInstanceValid(_instance))
        ? _instance.Size.Y : 0f;

    // Builds and shows a tooltip directly under anchor, horizontally centered
    // on it and clamped to the viewport. minWidth (when > 0) forces the tip
    // and the inner RichTextLabel to a minimum width so wide BBcode tables
    // span the full panel. yOffset stacks tooltips vertically.
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
            // Stop the HoverTipsContainer from stretching the tip vertically.
            _instance.SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;
            container.AddChild(_instance);

            // The icon slot is unused.
            if (_instance.HasNode("%Icon"))
                _instance.GetNode<Control>("%Icon").Visible = false;

            // Title fills the full tip width so HorizontalAlignment.Center
            // resolves against the panel rather than the label content.
            var titleNode                 = _instance.GetNode<Label>("%Title");
            titleNode.Text                = title ?? "";
            titleNode.Visible             = !string.IsNullOrEmpty(title);
            titleNode.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            titleNode.HorizontalAlignment = HorizontalAlignment.Center;

            // BBcode body. AutowrapMode.Off keeps table cells from breaking
            // mid-word; explicit \n still produces line breaks.
            var descNode           = _instance.GetNode<RichTextLabel>("%Description");
            descNode.BbcodeEnabled = true;
            descNode.AutowrapMode  = TextServer.AutowrapMode.Off;
            if (string.IsNullOrEmpty(bbcode))
            {
                // Hide the body and zero its minimum size: hidden nodes still
                // reserve their min size inside Godot containers.
                descNode.Visible           = false;
                descNode.CustomMinimumSize = Vector2.Zero;
            }
            else
            {
                descNode.Visible = true;
                if (minWidth > 0f)
                {
                    // Push the width down to the inner RTL so table columns
                    // span the full panel instead of shrinking to content.
                    descNode.CustomMinimumSize   = new Vector2(minWidth - 28f, 0f);
                    descNode.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                }
            }
            descNode.Text = bbcode;

            _instance.ResetSize();

            // Center under the anchor and clamp to viewport bounds.
            float tipW = _instance.Size.X > 1f ? _instance.Size.X : 360f;
            float x    = anchor.GlobalPosition.X + anchor.Size.X / 2f - tipW / 2f;
            try
            {
                var vr = anchor.GetViewportRect();
                x = Math.Clamp(x, 4f, vr.Size.X - tipW - 4f);
            }
            catch { /* viewport not ready: leave x unclamped */ }
            _instance.GlobalPosition = new Vector2(x, anchor.GlobalPosition.Y + anchor.Size.Y + 8f + yOffset);
        }
        catch (Exception e) { ModLog.Error("DtsNativeTip.Show", e); }
    }

    // Replaces the body BBcode without rebuilding the tip.
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
