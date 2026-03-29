using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using MegaCrit.Sts2.Core.Saves;
using DisplayTheSpire.Logging;

namespace DisplayTheSpire.UI;

/// <summary>
/// Persists run-level counters that must survive save / quit / resume.
/// <para>
/// The game provides no mod extension point in <c>current_run.save</c>.
/// We write a companion JSON file to
/// <c>user://display_the_spire/run_data.json</c> every time the game saves
/// the run, and restore it on resume.
/// </para>
/// <para>
/// Call <see cref="OnRunStart"/> from every <c>NTopBar.Initialize</c> postfix
/// that needs persistent counters (guarded - only the first call per run does
/// anything). Call <see cref="OnRunEnd"/> from the matching <c>_ExitTree</c>
/// postfix.
/// </para>
/// </summary>
internal static class DtsRunData
{
    // Live counters, written by patches on the main thread

    public static int PotionsDropped { get; set; }
    public static int CardsThisRun   { get; set; }
    public static int TurnsThisRun   { get; set; }

    // Internal state

    private static bool   _runActive;
    private static string _filePath = "";   // set on first OnRunStart (main thread)

    // Public lifecycle

    /// <summary>
    /// Call once (or idempotently) from each <c>NTopBar.Initialize</c> postfix.
    /// Always runs on the main thread - safe to call Godot and subscribe events.
    /// </summary>
    public static void OnRunStart()
    {
        if (_runActive) return; // guard: only the first patch call per run executes
        _runActive = true;

        // Resolve path on the main thread so Flush() (which may run off-thread
        // after an async save continuation) never calls OS.GetUserDataDir().
        _filePath = Path.Combine(OS.GetUserDataDir(), DtsConst.ModId, "run_data.json");

        PotionsDropped = 0;
        CardsThisRun   = 0;
        TurnsThisRun   = 0;

        // HasRunSave is true when the game is resuming an existing save.
        // For a brand-new run, current_run.save doesn't exist yet -> false.
        if (SaveManager.Instance.HasRunSave)
            TryRestore();

        try { SaveManager.Instance.Saved += Flush; }
        catch (Exception e) { ModLog.Warn($"DtsRunData: could not subscribe to Saved - {e.Message}"); }
    }

    /// <summary>
    /// Call from <c>NTopBar._ExitTree</c> postfix.
    /// Unsubscribes the save hook. Does NOT delete the companion file -
    /// it persists so a subsequent resume can read it.
    /// </summary>
    public static void OnRunEnd()
    {
        if (!_runActive) return;
        _runActive = false;
        try { SaveManager.Instance.Saved -= Flush; }
        catch (Exception e) { ModLog.Warn($"DtsRunData: could not unsubscribe from Saved - {e.Message}"); }
    }

    // private helpers

    private static void TryRestore()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var saved = JsonSerializer.Deserialize<SavedState>(File.ReadAllText(_filePath));
            if (saved == null) return;
            PotionsDropped = saved.PotionsDropped;
            CardsThisRun   = saved.CardsThisRun;
            TurnsThisRun   = saved.TurnsThisRun;
            ModLog.Info($"DtsRunData: restored {PotionsDropped} potions, {CardsThisRun} cards, {TurnsThisRun} turns");
        }
        catch (Exception e) { ModLog.Warn($"DtsRunData: restore failed - {e.Message}"); }
    }

    // Called by SaveManager.Instance.Saved - may fire off the main thread
    // (RunSaveManager.SaveRun is async; the Saved invoke is in a continuation).
    // All accesses here are either cached strings or simple int reads
    private static void Flush()
    {
        try
        {
            string path = _filePath;
            if (string.IsNullOrEmpty(path)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new SavedState
            {
                PotionsDropped = PotionsDropped,
                CardsThisRun   = CardsThisRun,
                TurnsThisRun   = TurnsThisRun,
            }));
        }
        catch (Exception e) { ModLog.Warn($"DtsRunData: flush failed - {e.Message}"); }
    }

    // Serialization type (private, disk-only)

    private sealed class SavedState
    {
        [JsonPropertyName("potions_dropped")] public int PotionsDropped { get; init; }
        [JsonPropertyName("cards_this_run")]  public int CardsThisRun   { get; init; }
        [JsonPropertyName("turns_this_run")]  public int TurnsThisRun   { get; init; }
    }
}
