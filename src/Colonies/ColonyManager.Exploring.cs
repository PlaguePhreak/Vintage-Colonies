using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace CreateAColony.Colonies;

/// <summary>
/// The Explorer's and Gatherer's trades. The explorer walks out over the land around its hut, one square at a
/// time, nearest first, and marks what it sees on the colony's map: ore lying on the ground, clay, traders,
/// animals, danger and landmarks. The gatherer forages near its hut (ripe berries and fruit, edible mushrooms,
/// loose stones, flints, sticks, ore bits and seashells, reed tops, and clay and peat on the surface) and marks
/// the plants it passes. Markers: ColonyManager.Markers.cs.
/// </summary>
public partial class ColonyManager
{
    // ---- Explorer ----

    /// <summary>The square of land (ExplorerStep wide) a spot is in, as one number for Building.Explored.</summary>
    public long ExploreCell(int x, int z)
    {
        int step = Math.Max(8, Config.ExplorerStep);
        return ((long)(int)Math.Floor(x / (double)step) << 32) | (uint)(int)Math.Floor(z / (double)step);
    }

    /// <summary>
    /// The middle of the nearest square of land the explorer has not looked over yet, within its hut's range, or
    /// null when it has seen it all. Squares it could not reach (skip) count as seen. Once everything is seen it
    /// starts over after ExplorerRevisitDays.
    /// </summary>
    public BlockPos? NextExploreSpot(Colony colony, Building hut, Vec3d from, System.Func<BlockPos, bool> skip)
    {
        var ba = sapi.World.BlockAccessor;
        int radius = Math.Max(1, GatherRadius(hut)), step = Math.Max(8, Config.ExplorerStep);
        var seen = new HashSet<long>(hut.Explored);
        double now = sapi.World.Calendar.TotalDays;
        if (hut.ExploreStartedTotalDays <= 0) hut.ExploreStartedTotalDays = now;

        int c0x = (int)Math.Floor((hut.HutPos.X - radius) / (double)step), c1x = (int)Math.Floor((hut.HutPos.X + radius) / (double)step);
        int c0z = (int)Math.Floor((hut.HutPos.Z - radius) / (double)step), c1z = (int)Math.Floor((hut.HutPos.Z + radius) / (double)step);
        BlockPos? best = null;
        double bestDist = double.MaxValue;
        var rand = sapi.World.Rand;
        for (int cx = c0x; cx <= c1x; cx++)
        for (int cz = c0z; cz <= c1z; cz++)
        {
            int x = cx * step + step / 2, z = cz * step + step / 2;
            double hx = x - hut.HutPos.X, hz = z - hut.HutPos.Z;
            if (hx * hx + hz * hz > (double)radius * radius) continue;
            if (seen.Contains(ExploreCell(x, z))) continue;
            // Mostly the nearest square to where the explorer stands, with a little wander in it.
            double dx = x - from.X, dz = z - from.Z;
            double dist = dx * dx + dz * dz + rand.NextDouble() * step * step;
            if (dist >= bestDist) continue;
            var pos = new BlockPos(x, hut.HutPos.Y + 1, z);
            if (ba.GetChunkAtBlockPos(pos) != null)
            {
                pos.Y = ba.GetTerrainMapheightAt(pos) + 1;
                // A lake or the sea is looked over from its shore, from the squares around it.
                if (ba.GetBlock(pos.DownCopy(), BlockLayersAccess.Fluid).Id != 0 || !CanWorkAt(colony, pos))
                {
                    hut.Explored.Add(ExploreCell(x, z));
                    seen.Add(ExploreCell(x, z));
                    continue;
                }
            }
            if (skip(pos))
            {
                hut.Explored.Add(ExploreCell(x, z));
                seen.Add(ExploreCell(x, z));
                continue;
            }
            best = pos;
            bestDist = dist;
        }

        if (best == null && hut.Explored.Count > 0 && now - hut.ExploreStartedTotalDays >= Config.ExplorerRevisitDays)
        {
            // Traders and animals move on: go round again.
            hut.Explored.Clear();
            hut.ExploreStartedTotalDays = now;
            MarkDirty(colony);
        }
        return best;
    }

    /// <summary>The explorer looks around where it stands, marks what it sees, and counts the square as seen.</summary>
    public void Explore(Colony colony, Building hut, BlockPos spot)
    {
        LookAround(colony, hut, spot, Math.Max(4, Config.ExplorerLookRadius));
        long cell = ExploreCell(spot.X, spot.Z);
        if (!hut.Explored.Contains(cell)) hut.Explored.Add(cell);
        AddStat(hut, "explored");
        MarkDirty(colony);
    }

    /// <summary>About how much of the explorer's range it has looked over, from 0 to 100.</summary>
    public int ExploredPercent(Building hut)
    {
        int radius = Math.Max(1, GatherRadius(hut)), step = Math.Max(8, Config.ExplorerStep);
        var seen = new HashSet<long>(hut.Explored);
        int all = 0, done = 0;
        int c0x = (int)Math.Floor((hut.HutPos.X - radius) / (double)step), c1x = (int)Math.Floor((hut.HutPos.X + radius) / (double)step);
        int c0z = (int)Math.Floor((hut.HutPos.Z - radius) / (double)step), c1z = (int)Math.Floor((hut.HutPos.Z + radius) / (double)step);
        for (int cx = c0x; cx <= c1x; cx++)
        for (int cz = c0z; cz <= c1z; cz++)
        {
            int x = cx * step + step / 2, z = cz * step + step / 2;
            double hx = x - hut.HutPos.X, hz = z - hut.HutPos.Z;
            if (hx * hx + hz * hz > (double)radius * radius) continue;
            all++;
            if (seen.Contains(ExploreCell(x, z))) done++;
        }
        return all == 0 ? 100 : done * 100 / all;
    }

    /// <summary>The Explorer's Hut window line: how much it has seen and how many markers it put down.</summary>
    string ExplorerInfo(Colony colony, Building hut) =>
        Lang.Get("createacolony:explorer-progress", ExploredPercent(hut), MarkerCount(colony, hut));

    // ---- Gatherer ----

    /// <summary>When each Gatherer's Hut's gatherer last went for a look around, so it does not wander all the time.</summary>
    readonly Dictionary<int, long> lastForageWalk = new();

    /// <summary>What a gatherer could take from a block: "fruit", "mushroom", "crop", "loose", or null for nothing.</summary>
    string? Forage(Colony colony, BlockPos pos)
    {
        var ba = sapi.World.BlockAccessor;
        var be = ba.GetBlockEntity(pos);
        if (IsFruitBearer(be)) return IsRipeFruit(be) ? "fruit" : CanTakeCutting(be) && CanWorkAt(colony, pos) && !NearBuilding(colony, pos) ? "cutting" : null;
        var block = ba.GetBlock(pos);
        string path = block.Code?.Path ?? "";
        if (path.StartsWith("mushroom-")) return IsPoisonousMushroom(block) ? null : "mushroom";
        // A wild crop (not on farmland) only once it is fully grown.
        if (IsWildCrop(block, pos)) return IsFullyGrown((BlockCrop)block) && CanWorkAt(colony, pos) && !NearBuilding(colony, pos) ? "crop" : null;
        string? wild = WildKind(block);
        if (wild == null && block is not BlockReeds) return null;
        // On open land or the colony's own, but not in or beside a building, so things placed on purpose stay.
        if (!CanWorkAt(colony, pos) || NearBuilding(colony, pos)) return null;
        // Food is low: the whole reed is dug up for its root (ColonyManager.Roots.cs).
        if (block is BlockReeds && DigsRoots(colony, pos)) return "root";
        if (wild == null) return null;
        // Seashells under water are out of reach.
        if (block is BlockSeashell && ba.GetBlock(pos, BlockLayersAccess.Fluid).Id != 0) return null;
        // Clay and peat only where the world made the ground, with open air above: one layer is taken, never a pit.
        if (wild == "dig" && (pos.Y != ba.GetTerrainMapheightAt(pos) || !OpenAbove(pos))) return null;
        return wild;
    }

    /// <summary>
    /// What else lies about that a gatherer can take, by how it is taken: "loose" (stones, flint, sticks, ore bits
    /// and seashells on the ground), "reed" (cattail, papyrus and tule tops, cut the way a knife does, so the plant
    /// stays), "dig" (clay and peat on the surface) or "herb" (horsetail and other plants for healing). Null for anything else.
    /// </summary>
    string? WildKind(Block block)
    {
        string path = block.Code?.Path ?? "";
        if (Config.GathererPicksUpStones && (path.StartsWith("loosestones-") || path.StartsWith("looseflints-") || path.StartsWith("loosestick")
            || path.StartsWith("looseores-") || block is BlockSeashell)) return "loose";
        if (Config.GathererCutsReeds && block is BlockReeds && block.Variant["state"] == "normal") return "reed";
        if (Config.GathererDigsClay && (path.StartsWith("rawclay-") || path.StartsWith("peat-"))) return "dig";
        // Horsetail and other plants for bandages and poultices (ColonyManager.Healing.cs).
        if (IsHerb(block)) return "herb";
        return null;
    }

    /// <summary>True when nothing solid and no water is on top of the block, only air or plants.</summary>
    bool OpenAbove(BlockPos pos)
    {
        var ba = sapi.World.BlockAccessor;
        var up = pos.UpCopy();
        if (ba.GetBlock(up, BlockLayersAccess.Fluid).Id != 0) return false;
        var above = ba.GetBlock(up);
        return above.Id == 0 || above.Replaceable >= 5000;
    }

    /// <summary>A crop growing in the wild: not on farmland, so no one's field.</summary>
    bool IsWildCrop(Block block, BlockPos pos) => block is BlockCrop { CropProps: not null }
        && sapi.World.BlockAccessor.GetBlockEntity(pos.DownCopy()) is not BlockEntityFarmland;

    static bool IsFullyGrown(BlockCrop crop) => crop.CurrentCropStage >= crop.CropProps.GrowthStages;

    /// <summary>Each colony's building footprints, two blocks wider all round, and when they were worked out.</summary>
    readonly Dictionary<string, (long Ms, List<Cuboidi> Boxes)> buildingBoxes = new();

    /// <summary>True when a spot is in a colony building or within two blocks of one.</summary>
    bool NearBuilding(Colony colony, BlockPos pos)
    {
        long now = sapi.World.ElapsedMilliseconds;
        if (!buildingBoxes.TryGetValue(colony.ColonyId, out var cached) || now - cached.Ms > 30_000)
        {
            var boxes = colony.Buildings.Select(b =>
            {
                var box = Footprint(b);
                return new Cuboidi(box.X1 - 2, box.Y1 - 2, box.Z1 - 2, box.X2 + 3, box.Y2 + 3, box.Z2 + 3);
            }).ToList();
            buildingBoxes[colony.ColonyId] = cached = (now, boxes);
        }
        return cached.Boxes.Any(b => b.Contains(pos.X, pos.Y, pos.Z));
    }

    /// <summary>Notes every berry bush, fruit tree branch, mushroom, loose stone, reed and surface clay in a chunk column.</summary>
    void ScanForage(int chunkX, int chunkZ, AreaScanner scanner)
    {
        var ba = sapi.World.BlockAccessor;
        int size = GlobalConstants.ChunkSize;
        for (int cy = 0; cy < ba.MapSizeY / size; cy++)
        {
            var chunk = ba.GetChunk(chunkX, cy, chunkZ);
            if (chunk?.BlockEntities == null) continue;
            foreach (var be in chunk.BlockEntities.Values)
            {
                if (IsFruitBearer(be) && scanner.InRange(be.Pos.X, be.Pos.Z)) scanner.Found[be.Pos.Copy()] = IsRipeFruit(be);
            }
        }
        var pos = new BlockPos(0);
        for (int x = 0; x < size; x++)
        for (int z = 0; z < size; z++)
        {
            int wx = chunkX * size + x, wz = chunkZ * size + z;
            if (!scanner.InRange(wx, wz)) continue;
            pos.Set(wx, 0, wz);
            int top = ba.GetTerrainMapheightAt(pos);
            // The height map is the ground as the world was made: dug or raised ground is a block or two off.
            for (int y = top - 2; y <= top + 2; y++)
            {
                pos.Y = y;
                var found = ba.GetBlock(pos);
                string path = found.Code?.Path ?? "";
                if (IsWildCrop(found, pos))
                {
                    scanner.Found[pos.Copy()] = IsFullyGrown((BlockCrop)found);
                    continue;
                }
                if (path.StartsWith("mushroom-") || WildKind(found) != null)
                {
                    scanner.Found[pos.Copy()] = true;
                }
            }
        }
    }

    /// <summary>
    /// Notes what a gatherer can take around the spot it stands at, so it goes for what it just saw instead of
    /// waiting for the next sweep of its area (Aaron saw gatherers walk past things, still "looking").
    /// </summary>
    public void NoteForageAround(Colony colony, Building hut, BlockPos center, int radius)
    {
        var scanner = Scanner(hut, "forage", ScanForage);
        var ba = sapi.World.BlockAccessor;
        var pos = new BlockPos(0);
        for (int dx = -radius; dx <= radius; dx++)
        for (int dz = -radius; dz <= radius; dz++)
        {
            if (dx * dx + dz * dz > radius * radius) continue;
            pos.Set(center.X + dx, center.Y, center.Z + dz);
            if (!scanner.InRange(pos.X, pos.Z) || ba.GetChunkAtBlockPos(pos) == null) continue;
            int top = ba.GetTerrainMapheightAt(pos);
            for (int y = Math.Min(top, center.Y) - 2; y <= Math.Max(top, center.Y) + 2; y++)
            {
                pos.Y = y;
                if (scanner.Found.ContainsKey(pos)) continue;
                if (IsFruitBearer(ba.GetBlockEntity(pos))) scanner.Found[pos.Copy()] = IsRipeFruit(ba.GetBlockEntity(pos));
                else if (Forage(colony, pos) != null) scanner.Found[pos.Copy()] = true;
            }
        }
    }

    /// <summary>
    /// The thing the gatherer takes next: the first kind on the hut's collecting order (GatherOrder) there is
    /// any of, and of those the nearest. Null when there is nothing; rank is its place on the order.
    /// </summary>
    public BlockPos? NextForage(Colony colony, Building hut, Vec3d from, System.Func<BlockPos, bool> skip, bool hasKnife, out int rank, Vintagestory.API.Common.Entities.Entity? walker = null)
    {
        var ba = sapi.World.BlockAccessor;
        var scanner = Scanner(hut, "forage", ScanForage);
        var order = GatherOrder(hut);
        BlockPos? best = null;
        double bestDist = double.MaxValue;
        int bestRank = int.MaxValue;
        foreach (var (pos, ready) in scanner.Found.ToList())
        {
            // Out of loaded land, what it is cannot be told: after everything else.
            int spotRank = order.Count;
            if (ba.GetChunkAtBlockPos(pos) != null)
            {
                string? kind = Forage(colony, pos);
                if (kind == null && !IsFruitBearer(ba.GetBlockEntity(pos)) && !IsWildCrop(ba.GetBlock(pos), pos))
                {
                    scanner.Found.Remove(pos); // picked, broken, or never worth it
                    continue;
                }
                scanner.Found[pos] = kind != null;
                if (kind == null || !MayCollect(hut, ForageKey(pos))) continue;
                if (kind == "herb" && HerbStocked(colony, hut, ba.GetBlock(pos))) continue;
                // Cuttings take a knife, and only as many as the colony keeps.
                if (kind == "cutting" && (!hasKnife || CuttingBlock(ba.GetBlockEntity(pos)) is not { } cutting || CuttingsStocked(colony, hut, cutting))) continue;
                spotRank = order.IndexOf(GatherCategory(colony, ba.GetBlock(pos), kind));
            }
            else if (!ready) continue;
            if (spotRank > bestRank || skip(pos) || !CanWorkAt(colony, pos)) continue;
            double dist = pos.DistanceSqTo(from.X, from.Y, from.Z);
            if (spotRank == bestRank && dist >= bestDist) continue;
            // Nothing to stand on beside it but water (0.200.0): reeds out in a lake are left alone.
            if (walker != null && ba.GetChunkAtBlockPos(pos) != null && !DryStandNear(walker, pos)) continue;
            best = pos;
            bestDist = dist;
            bestRank = spotRank;
        }
        rank = bestRank;
        return best;
    }

    /// <summary>
    /// A spot in the gatherer's range to walk to and look around, when there is nothing to pick; at most every
    /// two minutes, so it rests at the hut in between. Null when it is not time yet.
    /// </summary>
    public BlockPos? ForageWalk(Colony colony, Building hut, System.Func<BlockPos, bool> skip)
    {
        if (hut.GatherMode == GatherModes.Gather) return null;
        long now = sapi.World.ElapsedMilliseconds;
        // Set to only look around, it goes off again soon after.
        long wait = hut.GatherMode == GatherModes.Look ? 20_000 : 120_000;
        if (lastForageWalk.TryGetValue(hut.BuildingId, out long last) && now - last < wait) return null;
        lastForageWalk[hut.BuildingId] = now;
        var ba = sapi.World.BlockAccessor;
        var rand = sapi.World.Rand;
        int radius = Math.Max(4, GatherRadius(hut));
        for (int tries = 0; tries < 12; tries++)
        {
            double angle = rand.NextDouble() * Math.PI * 2, dist = radius * Math.Sqrt(rand.NextDouble());
            var pos = new BlockPos((int)(hut.HutPos.X + Math.Cos(angle) * dist), hut.HutPos.Y, (int)(hut.HutPos.Z + Math.Sin(angle) * dist));
            if (ba.GetChunkAtBlockPos(pos) == null) continue;
            pos.Y = ba.GetTerrainMapheightAt(pos) + 1;
            // Dry ground only: the terrain height is the lake bed, so the spot above it can be water (0.200.0).
            if (ba.GetBlock(pos, BlockLayersAccess.Fluid).IsLiquid() || ba.GetBlock(pos.DownCopy(), BlockLayersAccess.Fluid).Id != 0) continue;
            if (skip(pos) || !CanWorkAt(colony, pos)) continue;
            return pos;
        }
        return null;
    }

    /// <summary>Sets what a Gatherer's Hut's gatherer does (GatherModes).</summary>
    public string? SetGatherMode(Colony colony, Building hut, string mode)
    {
        if (hut.Type != BuildingTypes.Gatherer || !GatherModes.All.Contains(mode)) return "createacolony:bad-building";
        hut.GatherMode = mode;
        MarkDirty(colony);
        return null;
    }

    /// <summary>Picks what is at the spot into the gatherer's inventory. False when there was nothing to take.</summary>
    public bool Gather(Colony colony, Building hut, CitizenRecord worker, BlockPos pos)
    {
        string? kind = Forage(colony, pos);
        if (kind == null || !MayCollect(hut, ForageKey(pos))) return false;
        if (kind == "herb" && HerbStocked(colony, hut, sapi.World.BlockAccessor.GetBlock(pos))) return false;
        if (kind == "fruit")
        {
            if (!PickFruit(colony, hut, worker, pos)) return false;
        }
        else if (kind == "cutting")
        {
            if (!TakeCutting(colony, hut, worker, pos)) return false;
        }
        else
        {
            var block = sapi.World.BlockAccessor.GetBlock(pos);
            // Reeds drop their tops whatever the hand holds, as the game's own breaking does.
            var drops = kind == "reed" ? ToolDrops(block, null) : kind == "root" ? RootDrops(block, pos) : DropsOf(block, pos);
            if (Map.MarkerSubjects.OreKey(block) is { } ore) NoteOreHint(colony, hut, pos, ore);
            if (kind == "reed") CutReeds(block, pos);
            else BreakQuietly(pos);
            if (kind == "crop") ForgetPickedCrops(colony, pos);
            StoreHarvest(colony, hut, worker, drops, pos);
        }
        AddStat(hut, "gathered");
        MarkDirty(colony);
        return true;
    }

    /// <summary>Takes the tops off reeds and leaves the cut plant, as the game does when a player uses a knife.</summary>
    void CutReeds(Block block, BlockPos pos)
    {
        var ba = sapi.World.BlockAccessor;
        var cut = sapi.World.GetBlock(block.CodeWithVariants(new[] { "habitat", "state" }, new[] { "land", "harvested" }));
        if (cut == null || cut.Id == 0) BreakQuietly(pos);
        else
        {
            ba.SetBlock(cut.BlockId, pos);
            ba.MarkBlockDirty(pos);
        }
    }

    /// <summary>
    /// Ore bits on the ground (native copper in claystone) mean a deposit is likely right below: the spot is
    /// marked "Underground copper deposit" on the colony's map, and the prospectors go and take a reading there
    /// first, so the miners can dig it if it is good. One hint per ore within OreHintSpacing blocks.
    /// </summary>
    void NoteOreHint(Colony colony, Building hut, BlockPos pos, string ore)
    {
        double near = Math.Max(1, Config.OreHintSpacing);
        if (colony.OreHints.Any(h => h.Ore == ore && h.Pos.HorDistanceSqTo(pos.X, pos.Z) < near * near)) return;
        if (ReadNear(colony, pos.X, pos.Z, HintReadRange)) return;
        colony.OreHints.Add(new OreHint { Pos = pos.Copy(), Ore = ore, TotalDays = sapi.World.Calendar.TotalDays });
        string kind = OreMarkerKind(ore);
        string metal = kind == "ore-any" ? OreName(ore) : Lang.Get("createacolony:marker-" + kind);
        AddMarker(colony, hut, kind, pos.ToVec3d().Add(0.5, 0.5, 0.5), Lang.Get("createacolony:marker-ore-hint", metal));
        AddStat(hut, "ore-hints");
    }

    /// <summary>The Gatherer's Hut window line: how many markers it put down.</summary>
    string GathererInfo(Colony colony, Building hut)
    {
        int ready = scanners.TryGetValue((hut.BuildingId, "forage"), out var scanner) ? scanner.Found.Count(f => f.Value) : 0;
        return Lang.Get("createacolony:gatherer-progress", MarkerCount(colony, hut), ready);
    }
}
