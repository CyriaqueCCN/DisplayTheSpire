using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using DisplayTheSpire.Logging;

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
            ModLog.Info("Initialized");
        }
        catch (Exception e)
        {
            ModLog.Error("Harmony PatchAll failed", e);
        }
    }
}
