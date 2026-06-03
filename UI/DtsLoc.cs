using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Modding;
using DisplayTheSpire.Logging;

namespace DisplayTheSpire.UI;

// Mod-side localization. Reads flat key-value JSON files from
// <gamedir>/mods/display_the_spire/locales/<lang>.json, picks the file
// matching LocManager.Instance.Language, falls back to eng.json on
// missing keys, and reloads when the player switches language in the
// game's settings.
//
// Adding a new language is a drop-in: copy eng.json next to the other
// locale files, rename to one of the game's three-letter codes (eng,
// zhs, deu, esp, fra, ita, jpn, kor, pol, ptb, rus, spa, tha, tur),
// and translate the values. No recompile required.
//
// Tr never throws. Missing English fallback returns "[key]" so the
// missing entry is visible in-game rather than silently empty.
internal static class DtsLoc
{
    private static Dictionary<string, string> _current = new(StringComparer.Ordinal);
    private static Dictionary<string, string> _english = new(StringComparer.Ordinal);
    private static string _activeLang = "eng";
    private static string? _localesDir;
    private static bool _subscribed;

    internal static string CurrentLanguage => _activeLang;

    // Called once from ModInit.Init after Harmony PatchAll. Resolves
    // the mod install dir via ModManager so the locales path follows
    // the mod wherever it sits (Steam Workshop or local mods folder).
    internal static void Initialize()
    {
        try
        {
            _localesDir = ResolveLocalesDir();
            if (_localesDir == null)
            {
                ModLog.Error("DtsLoc.Initialize: locales dir not found", new InvalidOperationException("no path"));
                return;
            }

            // Load English first as the permanent fallback table.
            _english = LoadLocale("eng") ?? new Dictionary<string, string>(StringComparer.Ordinal);

            string lang = LocManager.Instance?.Language ?? "eng";
            ApplyLanguage(lang);

            if (!_subscribed && LocManager.Instance != null)
            {
                LocManager.Instance.SubscribeToLocaleChange(OnLocaleChanged);
                _subscribed = true;
            }

            ModLog.Info($"DtsLoc loaded {_current.Count} keys for '{_activeLang}' (fallback {_english.Count} keys)");
        }
        catch (Exception ex) { ModLog.Error("DtsLoc.Initialize", ex); }
    }

    // Look up the mod entry by id, then read .path. ModManager.Mods
    // is the public list populated during ModManager.Initialize. The
    // path field equals the directory containing mod_manifest.json.
    private static string? ResolveLocalesDir()
    {
        try
        {
            var mod = ModManager.Mods?.FirstOrDefault(m => m.manifest?.id == DtsConst.ModId);
            if (mod == null || string.IsNullOrEmpty(mod.path)) return null;
            string dir = Path.Combine(mod.path, "locales");
            return Directory.Exists(dir) ? dir : null;
        }
        catch (Exception ex) { ModLog.Error("DtsLoc.ResolveLocalesDir", ex); return null; }
    }

    private static Dictionary<string, string>? LoadLocale(string lang)
    {
        if (_localesDir == null) return null;
        string path = Path.Combine(_localesDir, lang + ".json");
        if (!File.Exists(path)) return null;
        try
        {
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                   ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            ModLog.Error($"DtsLoc.LoadLocale '{lang}'", ex);
            return null;
        }
    }

    private static void ApplyLanguage(string lang)
    {
        if (lang == "eng")
        {
            _current = _english;
            _activeLang = "eng";
            return;
        }
        var loaded = LoadLocale(lang);
        if (loaded != null)
        {
            _current = loaded;
            _activeLang = lang;
        }
        else
        {
            // No file for this language. Stay on English so callers
            // still get readable strings rather than [key] markers.
            _current = _english;
            _activeLang = "eng";
            ModLog.Info($"DtsLoc: no locale file for '{lang}', using English");
        }
    }

    private static void OnLocaleChanged()
    {
        try
        {
            string lang = LocManager.Instance?.Language ?? "eng";
            if (lang == _activeLang) return;
            ApplyLanguage(lang);
            ModLog.Info($"DtsLoc switched to '{_activeLang}'");
        }
        catch (Exception ex) { ModLog.Error("DtsLoc.OnLocaleChanged", ex); }
    }

    // Resolve a key. Order: current language, English fallback, then
    // a bracketed marker so the missing key is obvious in the UI.
    internal static string Tr(string key)
    {
        if (string.IsNullOrEmpty(key)) return "";
        if (_current.TryGetValue(key, out var v) && v != null) return v;
        if (_english.TryGetValue(key, out var e) && e != null) return e;
        return "[" + key + "]";
    }

    // string.Format wrapper. Catches format exceptions so a malformed
    // translation never crashes the tooltip; returns the raw template
    // in that case.
    internal static string Tr(string key, params object[] args)
    {
        string template = Tr(key);
        if (args == null || args.Length == 0) return template;
        try { return string.Format(template, args); }
        catch (FormatException ex)
        {
            ModLog.Error($"DtsLoc.Tr format key='{key}' template='{template}'", ex);
            return template;
        }
    }
}
