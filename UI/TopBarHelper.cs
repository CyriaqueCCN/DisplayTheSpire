using System;
using Godot;
using DisplayTheSpire.Logging;

namespace DisplayTheSpire.UI;

/// <summary>
/// Scene-tree utilities for NTopBar patches.
/// </summary>
internal static class TopBarHelper
{
    /// <summary>
    /// Finds the first direct or recursive child of <paramref name="root"/> with the
    /// given <paramref name="name"/> and returns it as a <see cref="Control"/>.
    /// Returns <c>null</c> if not found or if the node is not a Control.
    /// </summary>
    public static Control? FindControl(Node root, string name)
    {
        try
        {
            var node = root.FindChild(name, recursive: true, owned: false);
            if (node == null || !GodotObject.IsInstanceValid(node)) return null;
            return node as Control;
        }
        catch (Exception e) { ModLog.Error($"TopBarHelper.FindControl({name})", e); return null; }
    }
}
