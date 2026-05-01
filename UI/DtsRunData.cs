using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Godot;
using MegaCrit.Sts2.Core.Saves;
using DisplayTheSpire.Logging;

namespace DisplayTheSpire.UI;

// Persists per-run counters that need to survive save / quit / resume.
// The game does not expose a mod extension point on current_run.save, so
// a companion JSON file at user://display_the_spire/run_data.json is
// written every time the run is saved and reloaded on resume.
//
// Lifecycle: each NTopBar.Initialize postfix that needs persistent
// counters calls OnRunStart (the second call is a no-op). The matching
// _ExitTree postfix calls OnRunEnd.
//
// Threading: increments come from Harmony postfixes on game hooks and
// always run on the main thread. Flush may run off-thread because
// RunSaveManager.SaveRun is async and the Saved event fires on the
// continuation. Cross-thread reads go through Volatile.Read; the file
// write itself is serialized by _flushLock so two queued saves cannot
// fight over the same file handle.
internal static class DtsRunData
{
    // Backing fields are mutated only via Interlocked / Volatile so the
    // off-thread Flush stays race-free. A naive PotionsDropped++ is a
    // non-atomic read-modify-write across threads; patches must use the
    // Increment* helpers below.
    private static int _potionsDropped;
    private static int _cardsThisRun;
    private static int _turnsThisRun;

    public static int PotionsDropped => Volatile.Read(ref _potionsDropped);
    public static int CardsThisRun   => Volatile.Read(ref _cardsThisRun);
    public static int TurnsThisRun   => Volatile.Read(ref _turnsThisRun);

    public static void IncrementPotionsDropped() => Interlocked.Increment(ref _potionsDropped);
    public static void IncrementCardsThisRun()   => Interlocked.Increment(ref _cardsThisRun);
    public static void IncrementTurnsThisRun()   => Interlocked.Increment(ref _turnsThisRun);

    private static bool   _runActive;
    private static string _filePath = "";   // resolved on first OnRunStart, main thread only
    // Serializes Flush(). Without the lock, two SaveManager.Saved invocations
    // queued on the thread pool can both reach File.WriteAllText and the
    // second one throws on the exclusive open.
    private static readonly object _flushLock = new();

    // Idempotent. Safe to call from each NTopBar.Initialize postfix; only
    // the first call per run does work. Always runs on the main thread,
    // so it is safe to touch Godot APIs and subscribe to events here.
    public static void OnRunStart()
    {
        if (_runActive) return;
        _runActive = true;

        // Resolve the path on the main thread so Flush (which can run on
        // an async-save continuation) never has to call OS.GetUserDataDir.
        _filePath = Path.Combine(OS.GetUserDataDir(), DtsConst.ModId, "run_data.json");

        // Volatile writes publish the zero values to any concurrent reader.
        // A late Flush continuation from a previous run could in theory
        // still be in flight when a new one starts.
        Volatile.Write(ref _potionsDropped, 0);
        Volatile.Write(ref _cardsThisRun,   0);
        Volatile.Write(ref _turnsThisRun,   0);

        // HasRunSave is true when the game is resuming an existing run.
        // For a new run the save file does not yet exist.
        if (SaveManager.Instance.HasRunSave)
            TryRestore();

        // Unsub-then-sub keeps OnRunStart safe to call from any caller
        // pattern. _runActive guards the second call today, but if a
        // future patch reaches this from a different thread or via a
        // re-entrant path the pattern still avoids a duplicate handler.
        try
        {
            SaveManager.Instance.Saved -= Flush;
            SaveManager.Instance.Saved += Flush;
        }
        catch (Exception e) { ModLog.Warn($"DtsRunData: could not subscribe to Saved - {e.Message}"); }
    }

    // Detaches the save hook. Does not delete the companion file: it has
    // to outlive the run so a later resume can read it.
    public static void OnRunEnd()
    {
        if (!_runActive) return;
        _runActive = false;
        try { SaveManager.Instance.Saved -= Flush; }
        catch (Exception e) { ModLog.Warn($"DtsRunData: could not unsubscribe from Saved - {e.Message}"); }
    }

    private static void TryRestore()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var saved = JsonSerializer.Deserialize<SavedState>(File.ReadAllText(_filePath));
            if (saved == null) return;
            // Volatile writes so any subsequent off-thread Flush sees the
            // restored values.
            Volatile.Write(ref _potionsDropped, saved.PotionsDropped);
            Volatile.Write(ref _cardsThisRun,   saved.CardsThisRun);
            Volatile.Write(ref _turnsThisRun,   saved.TurnsThisRun);
            ModLog.Info($"DtsRunData: restored {PotionsDropped} potions, {CardsThisRun} cards, {TurnsThisRun} turns");
        }
        catch (Exception e) { ModLog.Warn($"DtsRunData: restore failed - {e.Message}"); }
    }

    // SaveManager.Saved fires on the async continuation, so this can run
    // off the main thread. All reads go through Volatile.Read and the
    // file write is serialized by _flushLock.
    private static void Flush()
    {
        // Snapshot outside the lock to keep it held for the minimum time.
        // Volatile.Read provides the memory barrier so the snapshot
        // reflects the latest main-thread increments.
        int p = Volatile.Read(ref _potionsDropped);
        int c = Volatile.Read(ref _cardsThisRun);
        int t = Volatile.Read(ref _turnsThisRun);

        lock (_flushLock)
        {
            try
            {
                string path = _filePath;
                if (string.IsNullOrEmpty(path)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(new SavedState
                {
                    PotionsDropped = p,
                    CardsThisRun   = c,
                    TurnsThisRun   = t,
                }));
            }
            catch (Exception e) { ModLog.Warn($"DtsRunData: flush failed - {e.Message}"); }
        }
    }

    // On-disk shape for the companion file.
    private sealed class SavedState
    {
        [JsonPropertyName("potions_dropped")] public int PotionsDropped { get; init; }
        [JsonPropertyName("cards_this_run")]  public int CardsThisRun   { get; init; }
        [JsonPropertyName("turns_this_run")]  public int TurnsThisRun   { get; init; }
    }
}
