using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using MegaCrit.Sts2.Core.Multiplayer.Game;   // NetGameType + IsMultiplayer()
using MegaCrit.Sts2.Core.Runs;               // RunManager, IRunState
using MegaCrit.Sts2.Core.Saves;              // SaveManager
using DisplayTheSpire.Logging;

namespace DisplayTheSpire.UI;

// Per-run stat counters (potions dropped, cards played, turns), persisted
// to user://display_the_spire/run_data.json so they survive save / quit /
// resume.
//
// Each run gets its OWN record, keyed by the run's identity so no two runs
// ever share counters:
//
//   key = "<profileId>:<sp|mp>:<startTime>"
//
// StartTime is the game's own unique run id -- RunState carries no GUID, and
// the game itself names run-history files "{StartTime}.run" per profile
// (RunHistorySaveManager) and refuses to keep two saves with the same
// StartTime. We read it from RunManager._startTime (set in InitializeShared
// from either DateTimeOffset.UtcNow for a new run or save.StartTime on
// resume), so it is stable across save/quit/resume. The profile id
// (SaveManager.CurrentProfileId) and the single/multiplayer flag
// (RunManager.NetService.Type.IsMultiplayer()) scope the key so different
// profiles and SP-vs-MP runs never bleed into each other.
//
// If StartTime cannot be read (e.g. a future game build renames the field),
// the key degrades to "<profile>:<mode>:s=<seed>:<gameMode>:a<asc>" -- still
// unique for every random-seed run; only repeated custom-seed runs could
// then collide, which is the rare worst case.
//
// Past runs are kept (for future advanced-stats display) up to MaxRuns. At
// launch the store is pruned to the newest MaxRuns by recency so the file
// cannot grow without bound.
//
// Threading: increments fire from Harmony postfixes on the main thread.
// Flush runs from SaveManager.Saved, whose async continuation may land off
// the main thread. All shared state is guarded by _lock; the file write is
// additionally serialized by _fileLock.
internal static class DtsRunData
{
    private const int MaxRuns = 1000;

    private static readonly object _lock     = new();
    private static readonly object _fileLock = new();

    private static RunStore   _store      = new();
    private static RunRecord? _current;
    private static string     _filePath   = "";
    private static bool       _loaded;
    private static bool       _savedHooked;

    // ----- current-run read accessors (0 when no run is active) -----
    public static int PotionsDropped { get { lock (_lock) { return _current?.PotionsDropped ?? 0; } } }
    public static int CardsThisRun   { get { lock (_lock) { return _current?.CardsPlayed    ?? 0; } } }
    public static int TurnsThisRun   { get { lock (_lock) { return _current?.Turns          ?? 0; } } }

    // ----- increments (no-op when no run is active, so nothing can bleed
    // into a default record) -----
    public static void IncrementPotionsDropped() { lock (_lock) { if (_current != null) { _current.PotionsDropped++; _current.LastSeen = Now(); } } }
    public static void IncrementCardsThisRun()   { lock (_lock) { if (_current != null) { _current.CardsPlayed++;    _current.LastSeen = Now(); } } }
    public static void IncrementTurnsThisRun()   { lock (_lock) { if (_current != null) { _current.Turns++;          _current.LastSeen = Now(); } } }

    // Called once from ModInit.Init at launch. Resolves the file path, loads
    // the store, and prunes it to MaxRuns. Idempotent.
    public static void Initialize()
    {
        bool pruned;
        lock (_lock)
        {
            if (_loaded) return;
            try { _filePath = Path.Combine(OS.GetUserDataDir(), DtsConst.ModId, "run_data.json"); }
            catch (Exception e) { ModLog.Error("DtsRunData.Initialize path", e); }
            Load();
            pruned = Prune();
            _loaded = true;
            ModLog.Info($"DtsRunData: {_store.Runs.Count} run(s) on disk");
        }
        if (pruned) SaveToDisk();
    }

    // Resolve / look up the record for the run being entered. A genuinely
    // new run gets a fresh zeroed record; a resumed run finds its existing
    // record by key and keeps its counters. Safe to call from each
    // NTopBar.Initialize postfix (idempotent for the same run).
    public static void OnRunStart(IRunState runState)
    {
        try
        {
            Initialize();
            long now = Now();
            lock (_lock)
            {
                string key = BuildKey(runState, out var meta);
                if (!_store.Runs.TryGetValue(key, out var rec))
                {
                    rec = new RunRecord
                    {
                        Profile   = meta.Profile,
                        Mode      = meta.Mode,
                        Seed      = meta.Seed,
                        GameMode  = meta.GameMode,
                        Ascension = meta.Ascension,
                        StartTime = meta.StartTime,
                        FirstSeen = now,
                        LastSeen  = now,
                    };
                    _store.Runs[key] = rec;
                    ModLog.Info($"DtsRunData: new run '{key}' (seed={meta.Seed}, {meta.Mode}, profile {meta.Profile})");
                }
                else
                {
                    rec.LastSeen = now;
                    ModLog.Info($"DtsRunData: resumed run '{key}' (potions {rec.PotionsDropped}, cards {rec.CardsPlayed}, turns {rec.Turns})");
                }
                _current = rec;
            }
            HookSaved();
            SaveToDisk();
        }
        catch (Exception e) { ModLog.Error("DtsRunData.OnRunStart", e); }
    }

    // The run UI is leaving the tree (quit to menu, or run teardown). The
    // run may resume later, so the record is kept; we just detach the save
    // hook, stamp last_seen, and drop the in-memory current ref so stray
    // increments cannot land on it.
    public static void OnRunSuspended()
    {
        try
        {
            UnhookSaved();
            lock (_lock)
            {
                if (_current != null) _current.LastSeen = Now();
            }
            SaveToDisk();
            lock (_lock) { _current = null; }
        }
        catch (Exception e) { ModLog.Error("DtsRunData.OnRunSuspended", e); }
    }

    // The run actually ended (victory / death / abandon). Stamp the end
    // time and outcome on the record; it stays in the store as past-run
    // history. Fires from the RunManager.OnEnded postfix while the run UI
    // is still up, so _current is still valid.
    public static void OnRunEnded(bool isVictory)
    {
        try
        {
            lock (_lock)
            {
                if (_current != null)
                {
                    _current.Ended   = true;
                    _current.Won     = isVictory;
                    _current.EndTime = Now();
                    _current.LastSeen = _current.EndTime;
                }
            }
            SaveToDisk();
        }
        catch (Exception e) { ModLog.Error("DtsRunData.OnRunEnded", e); }
    }

    // ---------------------------------------------------------------- key

    private struct RunMeta
    {
        public int    Profile;
        public string Mode;
        public string Seed;
        public string GameMode;
        public int    Ascension;
        public long   StartTime;
    }

    private static string BuildKey(IRunState rs, out RunMeta meta)
    {
        int    profile = TryProfile();
        string mode    = TryMode();
        string seed    = "";
        string gm      = "";
        int    asc     = 0;
        try { seed = rs.Rng.StringSeed ?? ""; } catch { }
        try { gm   = rs.GameMode.ToString(); } catch { }
        try { asc  = rs.AscensionLevel; }       catch { }
        long start = TryStartTime();

        meta = new RunMeta
        {
            Profile = profile, Mode = mode, Seed = seed,
            GameMode = gm, Ascension = asc, StartTime = start,
        };

        return start > 0
            ? $"{profile}:{mode}:{start}"
            : $"{profile}:{mode}:s={seed}:{gm}:a{asc}";
    }

    private static int TryProfile()
    {
        try { return SaveManager.Instance.CurrentProfileId; }
        catch { return -1; }
    }

    private static string TryMode()
    {
        try { return RunManager.Instance.NetService.Type.IsMultiplayer() ? "mp" : "sp"; }
        catch { return "sp"; }
    }

    // The game keeps the run's StartTime in RunManager._startTime (set in
    // InitializeShared for both new and resumed runs). No public accessor
    // exists, so read it reflectively. A null/zero result drives the
    // seed-based fallback key in BuildKey.
    private static long TryStartTime()
    {
        try
        {
            var field = typeof(RunManager).GetField("_startTime",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (field?.GetValue(RunManager.Instance) is long l && l > 0) return l;
        }
        catch (Exception e) { ModLog.Warn($"DtsRunData: could not read run StartTime - {e.Message}"); }
        return 0;
    }

    // ------------------------------------------------------------- persist

    private static void HookSaved()
    {
        try
        {
            if (_savedHooked) return;
            SaveManager.Instance.Saved += Flush;
            _savedHooked = true;
        }
        catch (Exception e) { ModLog.Warn($"DtsRunData: could not subscribe to Saved - {e.Message}"); }
    }

    private static void UnhookSaved()
    {
        try
        {
            if (!_savedHooked) return;
            SaveManager.Instance.Saved -= Flush;
            _savedHooked = false;
        }
        catch (Exception e) { ModLog.Warn($"DtsRunData: could not unsubscribe from Saved - {e.Message}"); }
    }

    private static void Flush() => SaveToDisk();

    private static void SaveToDisk()
    {
        string path;
        string json;
        // Serialize a consistent snapshot under _lock (the store is small:
        // <= MaxRuns records of a dozen scalar fields).
        lock (_lock)
        {
            path = _filePath;
            if (string.IsNullOrEmpty(path)) return;
            try { json = JsonSerializer.Serialize(_store); }
            catch (Exception e) { ModLog.Warn($"DtsRunData: serialize failed - {e.Message}"); return; }
        }
        // Serialize concurrent writers (a main-thread save and an off-thread
        // Saved continuation can race for the same file handle).
        lock (_fileLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, json);
            }
            catch (Exception e) { ModLog.Warn($"DtsRunData: write failed - {e.Message}"); }
        }
    }

    // Call under _lock. Replaces _store from disk; tolerates a missing file
    // and the legacy v1 single-record schema (which carried no run identity
    // and is simply discarded -- its counters cannot be attributed to a run).
    private static void Load()
    {
        try
        {
            if (string.IsNullOrEmpty(_filePath) || !File.Exists(_filePath))
            {
                _store = new RunStore();
                return;
            }
            var parsed = JsonSerializer.Deserialize<RunStore>(File.ReadAllText(_filePath));
            if (parsed?.Runs is { } runs)
            {
                _store = parsed;
            }
            else
            {
                _store = new RunStore();
                ModLog.Info("DtsRunData: no v2 records found (fresh or legacy file) - starting clean");
            }
        }
        catch (Exception e)
        {
            ModLog.Warn($"DtsRunData: load failed - {e.Message}");
            _store = new RunStore();
        }
    }

    // Call under _lock. Keeps the newest MaxRuns by recency. Returns true if
    // anything was dropped.
    private static bool Prune()
    {
        if (_store.Runs.Count <= MaxRuns) return false;
        var keep = _store.Runs
            .OrderByDescending(kv => Recency(kv.Value))
            .Take(MaxRuns)
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        int removed = _store.Runs.Count - keep.Count;
        _store.Runs = keep;
        ModLog.Info($"DtsRunData: pruned {removed} old run(s), kept newest {MaxRuns}");
        return removed > 0;
    }

    private static long Recency(RunRecord r) =>
        Math.Max(Math.Max(r.EndTime, r.LastSeen), Math.Max(r.StartTime, r.FirstSeen));

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    // ----------------------------------------------------------- on-disk

    private sealed class RunStore
    {
        [JsonPropertyName("version")] public int Version { get; set; } = 2;
        [JsonPropertyName("runs")]    public Dictionary<string, RunRecord> Runs { get; set; } = new();
    }

    private sealed class RunRecord
    {
        [JsonPropertyName("profile")]         public int    Profile        { get; set; }
        [JsonPropertyName("mode")]            public string Mode           { get; set; } = "sp";
        [JsonPropertyName("seed")]            public string Seed           { get; set; } = "";
        [JsonPropertyName("game_mode")]       public string GameMode       { get; set; } = "";
        [JsonPropertyName("ascension")]       public int    Ascension      { get; set; }
        [JsonPropertyName("start_time")]      public long   StartTime      { get; set; }
        [JsonPropertyName("first_seen")]      public long   FirstSeen      { get; set; }
        [JsonPropertyName("last_seen")]       public long   LastSeen       { get; set; }
        [JsonPropertyName("end_time")]        public long   EndTime        { get; set; }
        [JsonPropertyName("ended")]           public bool   Ended          { get; set; }
        [JsonPropertyName("won")]             public bool   Won            { get; set; }
        [JsonPropertyName("potions_dropped")] public int    PotionsDropped { get; set; }
        [JsonPropertyName("cards_played")]    public int    CardsPlayed    { get; set; }
        [JsonPropertyName("turns")]           public int    Turns          { get; set; }
    }
}
