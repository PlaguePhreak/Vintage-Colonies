using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace CreateAColony.Colonies;

/// <summary>
/// The work area of a Forester's or Farmer's Hut: how far out its worker goes, set in the hut window,
/// and the scanners that search that area a little at a time. Parts of the area no player is near
/// are loaded from the save one column at a time to be looked at; land never visited is skipped, so
/// a big work area never generates new terrain.
/// </summary>
public partial class ColonyManager
{
    const int ScanTickMs = 200;
    /// <summary>Time the scanners may take each tick.</summary>
    const double ScanBudgetMs = 4;
    /// <summary>A finished sweep starts over after this long, to find trees that have grown and bushes that have ripened.</summary>
    const long RescanAfterMs = 180_000;
    /// <summary>A scanner nobody has asked for in this long belongs to a removed hut or a changed setting.</summary>
    const long ScannerForgetMs = 600_000;
    /// <summary>A column load that has not answered by then is given up on.</summary>
    const long LoadTimeoutMs = 30_000;

    readonly Dictionary<(int HutId, string Kind), AreaScanner> scanners = new();
    /// <summary>Answers from the chunk thread: whether a column exists in the save, then that it has loaded.</summary>
    readonly ConcurrentQueue<(AreaScanner Scanner, Vec2i Column, bool Exists, bool Loaded)> loadResults = new();
    bool loadInFlight;
    long loadStartedMs;
    (AreaScanner Scanner, Vec2i Column)? loading;
    int scanTurn;

    /// <summary>How far, in blocks, the worker of a Forester's or Farmer's Hut goes out.</summary>
    public int GatherRadius(Building hut)
    {
        if (hut.WorkRadius > 0) return hut.WorkRadius;
        return hut.Type switch
        {
            BuildingTypes.Forester => Config.ForesterRadius,
            BuildingTypes.Prospector => Config.ProspectorRadius,
            BuildingTypes.Miner => Config.MinerRadius,
            BuildingTypes.Explorer => (int)(Config.ExplorerRadius * ResearchMult(ColonyOfBuilding(hut), "explorerRange")), // Field Notes
            BuildingTypes.Gatherer => Config.GathererRadius,
            BuildingTypes.Hunter => Config.HunterRadius,
            _ => Config.FarmerOrchardRadius
        };
    }

    public static bool IsGatherHut(string type) => GatherHuts.ContainsKey(type);

    /// <summary>True while the search of a hut's work area has not looked everywhere yet.</summary>
    public bool StillSearching(Building hut, string kind) =>
        scanners.TryGetValue((hut.BuildingId, kind), out var scanner) && (scanner.Sweeping || scanner.Unseen.Count > 0);

    /// <summary>The scanner for one kind of thing around a hut, made fresh when the hut's range changed.</summary>
    AreaScanner Scanner(Building hut, string kind, AreaScanner.ColumnScan scan)
    {
        int radius = Math.Max(1, GatherRadius(hut));
        var key = (hut.BuildingId, kind);
        if (!scanners.TryGetValue(key, out var scanner) || scanner.Radius != radius || !scanner.Center.Equals(hut.HutPos))
        {
            scanner = new AreaScanner(hut.HutPos, radius, scan);
            scanners[key] = scanner;
            // A small area is swept at once, so a worker at a new hut does not wait for the first tick.
            if (radius <= 32) while (scanner.Step(sapi.World.BlockAccessor)) { }
        }
        scanner.LastUsedMs = sapi.World.ElapsedMilliseconds;
        return scanner;
    }

    /// <summary>Advances the scanners in turn for a few milliseconds, and loads one unseen column at a time.</summary>
    void OnScanTick(float dt)
    {
        if (scanners.Count == 0) return;
        long now = sapi.World.ElapsedMilliseconds;
        var ba = sapi.World.BlockAccessor;

        foreach (var key in scanners.Where(kv => now - kv.Value.LastUsedMs > ScannerForgetMs).Select(kv => kv.Key).ToList())
        {
            scanners.Remove(key);
        }

        while (loadResults.TryDequeue(out var result))
        {
            if (!result.Exists)
            {
                result.Scanner.Unseen.Remove(result.Column); // never generated: nothing out there yet
                loadInFlight = false;
            }
            else if (!result.Loaded)
            {
                var (scanner, column) = (result.Scanner, result.Column);
                loadStartedMs = now;
                sapi.WorldManager.LoadChunkColumnPriority(column.X, column.Y, new ChunkLoadOptions
                {
                    OnLoaded = () => loadResults.Enqueue((scanner, column, true, true))
                });
            }
            else
            {
                loadInFlight = false;
                if (ba.GetMapChunk(result.Column.X, result.Column.Y) != null) result.Scanner.ScanLoaded(result.Column);
                else result.Scanner.Unseen.Remove(result.Column);
            }
        }
        if (loadInFlight && now - loadStartedMs > LoadTimeoutMs)
        {
            // No answer: skip that column rather than asking for it forever.
            if (loading is { } stuck) stuck.Scanner.Unseen.Remove(stuck.Column);
            loadInFlight = false;
        }

        var list = scanners.Values.ToList();
        if (list.Count == 0) return;
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < list.Count && watch.Elapsed.TotalMilliseconds < ScanBudgetMs; i++)
        {
            var scanner = list[(scanTurn + i) % list.Count];
            while (watch.Elapsed.TotalMilliseconds < ScanBudgetMs && scanner.Step(ba)) { }
            if (!scanner.Sweeping && scanner.SweepDoneMs < 0) scanner.SweepDoneMs = now;
            if (!scanner.Sweeping && scanner.Unseen.Count == 0 && now - scanner.SweepDoneMs > RescanAfterMs) scanner.Restart();
        }
        scanTurn++;

        if (!loadInFlight && list.FirstOrDefault(s => s.Unseen.Count > 0) is { } waiting) LoadUnseen(waiting, waiting.Unseen[0]);
    }

    /// <summary>Loads a column from the save so it can be looked at; the server unloads it again once no one is near.</summary>
    void LoadUnseen(AreaScanner scanner, Vec2i column)
    {
        if (sapi.World.BlockAccessor.GetMapChunk(column.X, column.Y) != null)
        {
            scanner.ScanLoaded(column);
            return;
        }
        loadInFlight = true;
        loading = (scanner, column);
        loadStartedMs = sapi.World.ElapsedMilliseconds;
        // Callbacks may come from the chunk thread; the answers are handled on the next tick.
        sapi.WorldManager.TestMapChunkExists(column.X, column.Y, exists => loadResults.Enqueue((scanner, column, exists, false)));
    }
}
