namespace DisplayTheSpire;

/// <summary>
/// Mod-wide string constants
/// A rename here propagates everywhere automatically.
/// </summary>
internal static class DtsConst
{
    /// <summary>
    /// Mod name - used in log prefixes and any user-visible strings.
    /// </summary>
    public const string ModName = "DisplayTheSpire";

    /// <summary>
    /// Unique mod identifier. Must match the <c>id</c> field in mod_manifest.json.
    /// Used as: user:// subdirectory name for save data.
    /// </summary>
    public const string ModId = "display_the_spire";

    /// <summary>
    /// Harmony instance identifier
    /// <c>author.ModName</c> format prevents collisions with patches from other mods.
    /// </summary>
    public const string HarmonyId = "syca.DisplayTheSpire";
}
