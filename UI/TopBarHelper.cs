using System;
using Godot;
using DisplayTheSpire.Logging;

namespace DisplayTheSpire.UI;

internal static class TopBarHelper
{
    // Recursive child lookup that returns null when the node is missing
    // or is not a Control. Wraps Node.FindChild to swallow Godot exceptions
    // raised during scene-tree teardown.
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
