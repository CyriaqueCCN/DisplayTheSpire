using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using DisplayTheSpire.Logging;
using DisplayTheSpire.UI;

namespace DisplayTheSpire;

[ModInitializer(nameof(Init))]
public static class ModInit
{
    private static Harmony? _harmony;

    public static void Init()
    {
        _harmony = new Harmony(DtsConst.HarmonyId);
        try
        {
            _harmony.PatchAll(typeof(ModInit).Assembly);
            DtsLoc.Initialize();
            DtsRunData.Initialize();
            ModLog.Info("Initialized");
        }
        catch (Exception e)
        {
            ModLog.Error("Harmony PatchAll failed", e);
        }
    }

    // The game has no mod-shutdown hook, so this is never invoked in
    // normal operation. Kept for hot-reload harnesses and debug tooling.
    // UnpatchAll scoped by HarmonyId only removes patches owned by this
    // mod, so other mods patching the same targets are not affected.
    public static void Shutdown()
    {
        try
        {
            _harmony?.UnpatchAll(DtsConst.HarmonyId);
            _harmony = null;
            ModLog.Info("Shutdown - Harmony patches removed");
        }
        catch (Exception e)
        {
            ModLog.Error("Harmony UnpatchAll failed", e);
        }
    }
}
