using System;
using System.Reflection;
using MegaCrit.Sts2.Core.Logging;

namespace DisplayTheSpire.Logging;

internal static class ModLog
{
    private static readonly string Version = ResolveVersion();
    private static readonly string Prefix  = $"[{DtsConst.ModName} v{Version}]";

    public static void Info(string message)  => Log.Info($"{Prefix} {message}");
    public static void Warn(string message)  => Log.Warn($"{Prefix} {message}");
    public static void Error(string message) => Log.Error($"{Prefix} {message}");
    public static void Error(string message, Exception ex) => Log.Error($"{Prefix} {message}: {ex}");
    public static void Debug(string message) => Log.Debug($"{Prefix} {message}");

    private static string ResolveVersion()
    {
        try
        {
            var asm = typeof(ModLog).Assembly;
            var infoAttr = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            if (infoAttr != null) return infoAttr.InformationalVersion;
            var ver = asm.GetName().Version;
            if (ver != null) return ver.ToString(3);
        }
        catch { }
        return "?.?.?";
    }
}
