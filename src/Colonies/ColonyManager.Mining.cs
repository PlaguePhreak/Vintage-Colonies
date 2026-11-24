using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace CreateAColony.Colonies;

/// <summary>
/// The miner's trade, and the digging it shares with the quarrier. As in MineColonies, a miner works a
/// shaft beside its hut: a spiral staircase round a pillar, going down a level at a time, with a tunnel
/// out from each side of every level, and it digs out any ore it sees on the way. When "Go to prospected
/// ore" is on, it first goes to good ore the colony's prospectors found: there it digs a staircase down
/// from the surface to the ore's blocks and digs them out, and when they are dug out it comes back to
/// its shaft.
/// </summary>
public partial class ColonyManager
{
    public enum MineWork { Travel, Dig, Fill, Search, Build }

    /// <summary>What a miner does next: walk to Stand, then (for Dig) dig out Block from there, or (for Build) put Step's block there.</summary>
    public readonly record struct MineJob(MineWork Kind, BlockPos Block, BlockPos Stand, bool Ore, BuildStep? Step = null);

    /// <summary>The ore blocks found around each trip's site, so the ground is searched once per site.</summary>
    readonly Dictionary<MineTrip, (BlockPos Site, string Ore, List<BlockPos> Blocks)> oreBlocks = new();
    /// <summary>Ore blocks a miner could not get to (water next to the way, someone else's land, too hard for its pick), with when to try again.</summary>
    readonly Dictionary<MineTrip, Dictionary<BlockPos, long>> blockedOre = new();
    const long BlockedOreForgetMs = 600_000;

    static readonly BlockFacing[] Horizontals = BlockFacing.HORIZONTALS;

    /// <summary>A block of ore, such as ore-poor-nativecopper-granite.</summary>
    public static bool IsOre(Block block) => block.Code?.Path.StartsWith("ore-") == true;

    /// <summary>A block of the ore with this deposit code, the code the prospecting pick reports.</summary>
    public static bool IsOreOf(Block block, string ore)
    {
        if (!IsOre(block) || ore.Length == 0) return false;
        string path = block.Code.Path;
        return block.Variant["type"] == ore || path.Contains("-" + ore + "-") || path.EndsWith("-" + ore);
    }

    /// <summary>Air, or something a settler walks through (grass, snow, plants, a torch a miner put down).</summary>
    bool IsClear(BlockPos pos)
    {
        var block = sapi.World.BlockAccessor.GetBlock(pos);
        return block.Id == 0 || block.Replaceable >= 6000 || IsTorch(block);
    }

    /// <summary>True when water or lava is in or next to the block, so digging it would flood the way.</summary>
    bool NearLiquid(BlockPos pos)
    {
        var ba = sapi.World.BlockAccessor;
        if (ba.GetBlock(pos, BlockLayersAccess.Fluid).Id != 0) return true;
        foreach (var face in BlockFacing.ALLFACES)
        {
            if (ba.GetBlock(pos.AddCopy(face), BlockLayersAccess.Fluid).Id != 0) return true;
        }
        return false;
    }

    /// <summary>The boxes of the colony's buildings, which miners and quarriers never dig into.</summary>
    List<(BlockPos Min, BlockPos Max)> DigProtected(Colony colony) =>
        colony.Buildings.Select(b => BuildingBox(b)).ToList();

    /// <summary>
    /// What a hut's worker may not dig at its own dig site. Usually every building's box. When the hut's
    /// blueprint marks the site with a waypoint, the site is inside the hut's own box, so that box is left
    /// open and only the blocks its blueprint builds (Keep) are spared.
    /// </summary>
    (List<(BlockPos Min, BlockPos Max)> Boxes, HashSet<BlockPos> Keep) DigLimits(Colony colony, Building hut, bool marked)
    {
        var keep = new HashSet<BlockPos>();
        if (!marked) return (DigProtected(colony), keep);
        var boxes = colony.Buildings.Where(b => b != hut).Select(b => BuildingBox(b)).ToList();
        if (Blueprints.Get(hut, Math.Max(1, hut.Level)) is { } blueprint)
        {
            foreach (var step in blueprint.Steps) if (step.BlockId != 0) keep.Add(StepPos(hut, step));
        }
        keep.Add(hut.HutPos.Copy());
        return (boxes, keep);
    }

    /// <summary>
    /// Where a waypoint block of this kind ("mine" or "quarry") sits in the hut's blueprint, in the world:
    /// the current level's blueprint first, then the others. Null when no blueprint has one.
    /// </summary>
    BlockPos? WaypointOf(Building hut, string kind)
    {
        int now = Math.Max(1, hut.Level);
        foreach (int level in Enumerable.Range(1, Math.Max(now, Blueprints.MaxLevel(hut))).OrderBy(l => l == now ? 0 : 1))
        {
            if (Blueprints.Get(hut, level)?.Waypoints is { } marks && marks.TryGetValue(kind, out var at))
            {
                return hut.HutPos.AddCopy(at.Dx + hut.XOffset, at.Dy + hut.YOffset, at.Dz + hut.ZOffset);
            }
        }
        return null;
    }

    static bool Inside(BlockPos pos, List<(BlockPos Min, BlockPos Max)> boxes) =>
        boxes.Any(b => pos.X >= b.Min.X && pos.X <= b.Max.X && pos.Y >= b.Min.Y && pos.Y <= b.Max.Y && pos.Z >= b.Min.Z && pos.Z <= b.Max.Z);

    /// <summary>
    /// True when a settler may dig this block out: something solid, not the unbreakable mantle, nothing
    /// with contents (chests, ovens), not part of a colony building, not in someone else's land, and no
    /// water or lava next to it.
    /// </summary>
    public bool CanDig(Colony colony, BlockPos pos, List<(BlockPos Min, BlockPos Max)> boxes)
    {
        var ba = sapi.World.BlockAccessor;
        var block = ba.GetBlock(pos);
        if (block.Id == 0 || block.Code?.Path == "mantle" || block.Resistance > 100 || IsTorch(block)) return false;
        if (ba.GetBlockEntity(pos) != null) return false;
        if (pos.Y <= 1 || Inside(pos, boxes) || !CanWorkAt(colony, pos)) return false;
        return !NearLiquid(pos);
    }

    /// <summary>
    /// Digs a block out with the tool in hand, as a player would: what it drops goes into the worker's
    /// inventory (or the hut's storage), and the tool wears down by one use. False if nothing was there.
    /// </summary>
    public bool DigBlock(Colony colony, Building hut, CitizenRecord worker, EntityAgent entity, BlockPos pos, ItemSlot? hand)
    {
        var block = sapi.World.BlockAccessor.GetBlock(pos);
        if (block.Id == 0) return false;
        bool ore = IsOre(block);
        // Rock with open space on all sides comes away whole, as when a player relieves it.
        bool relieved = IsRelievedRock(pos);
        var drops = relieved ? new[] { new ItemStack(block) } : DropsOf(block, pos);
        BreakQuietly(pos);
        if (hand?.Itemstack != null) WearTool(entity, hand);
        // What the hut is set not to collect is left where it was dug.
        if (MayDig(hut, block)) StoreHarvest(colony, hut, worker, drops, pos);
        else foreach (var drop in drops) sapi.World.SpawnItemEntity(drop, pos.ToVec3d().Add(0.5, 0.5, 0.5));
        AddStat(hut, relieved ? "rocks-relieved" : ore ? "ore-mined" : "blocks-dug");
        return true;
    }

    /// <summary>Seconds to dig a block: ore takes half as long again as rock.</summary>
    public float DigSeconds(BlockPos pos, float perBlock) => IsOre(sapi.World.BlockAccessor.GetBlock(pos)) ? perBlock * 1.5f : perBlock;

    // ---- Filling finished mines in -----------------------------------------------------------

    /// <summary>A block a miner fills a finished mine with: soil, or failing that gravel, sand or stone it dug.</summary>
    public static bool IsFillStack(ItemStack? stack) => stack?.Block is { } block && block.SideSolid.All && block.EntityClass == null
        && block.BlockMaterial is EnumBlockMaterial.Soil or EnumBlockMaterial.Gravel or EnumBlockMaterial.Sand or EnumBlockMaterial.Stone
        && !IsOre(block);

    /// <summary>Soil first: the miner covers its mines with dirt when it has any.</summary>
    public static bool IsSoilStack(ItemStack? stack) => IsFillStack(stack) && stack!.Block!.BlockMaterial == EnumBlockMaterial.Soil;

    /// <summary>
    /// The open spaces of a finished mine to fill: everything dug out below the surface and joined to the
    /// entrance, down MinerFillDepth blocks (the staircase and whatever opens off it that high up), lowest
    /// first. A cave the mine broke into is filled only within that box, and buildings never.
    /// </summary>
    public List<BlockPos> FillCells(Colony colony, BlockPos entrance)
    {
        var ba = sapi.World.BlockAccessor;
        var boxes = DigProtected(colony);
        int depth = Math.Max(1, Config.MinerFillDepth), reach = depth * 2 + 2;
        bool Open(BlockPos p) => ba.GetChunkAtBlockPos(p) != null && IsClear(p) && ba.GetBlock(p, BlockLayersAccess.Fluid).Id == 0 && !Inside(p, boxes);

        // From the first steps down beside the entrance; never along the surface, so other holes nearby are left alone.
        var seen = new HashSet<BlockPos>();
        var open = new Queue<BlockPos>();
        for (int dx = -1; dx <= 1; dx++)
        for (int dz = -1; dz <= 1; dz++)
        {
            var start = entrance.AddCopy(dx, -1, dz);
            if (Open(start) && seen.Add(start)) open.Enqueue(start);
        }
        while (open.Count > 0 && seen.Count < 2000)
        {
            var at = open.Dequeue();
            foreach (var face in BlockFacing.ALLFACES)
            {
                var next = at.AddCopy(face);
                if (next.Y >= entrance.Y || next.Y < entrance.Y - depth) continue;
                if (Math.Abs(next.X - entrance.X) > reach || Math.Abs(next.Z - entrance.Z) > reach) continue;
                if (seen.Contains(next) || !Open(next)) continue;
                seen.Add(next);
                open.Enqueue(next);
            }
        }
        return seen.OrderBy(p => p.Y).ThenBy(p => Horizontal(p, entrance)).ToList();
    }

    /// <summary>
    /// The miner's next job filling in a finished mine: walk to its entrance, then put a block in the
    /// lowest open space. Status "mine-filling". Null when no mine is waiting, or no fill is at hand (then
    /// the status says so and other work goes on).
    /// </summary>
    public MineJob? NextFillJob(Colony colony, MineTrip trip, System.Func<BlockPos, bool> skip, out string status)
    {
        status = "";
        var ba = sapi.World.BlockAccessor;
        while (trip.Seals.Count > 0)
        {
            var entrance = trip.Seals[0];
            if (ba.GetChunkAtBlockPos(entrance) == null)
            {
                status = "mine-filling";
                return new MineJob(MineWork.Travel, entrance, entrance, false);
            }
            var cell = FillCells(colony, entrance).FirstOrDefault(p => !skip(p) && CanWorkAt(colony, p));
            if (cell == null)
            {
                trip.Seals.RemoveAt(0);
                MarkDirty(colony);
                continue;
            }
            status = "mine-filling";
            return new MineJob(MineWork.Fill, cell, entrance.Copy(), false);
        }
        return null;
    }

    /// <summary>Asks couriers for soil to fill finished mines with, once at a time.</summary>
    public void EnsureFillRequest(Colony colony, Building hut)
    {
        var chest = HutStorage(colony, hut).FirstOrDefault();
        if (chest == null) return;
        const string code = "game:soil-*";
        string tag = RequestTag(hut);
        if (!colony.Requests.Any(r => r.RequestedBy == tag && r.ItemCode == code)) AddRequest(colony, code, 32, chest.Pos, tag);
    }

    /// <summary>Puts a fill block from the worker's inventory into an open space of a finished mine. False when it has none.</summary>
    public bool FillBlock(Colony colony, CitizenRecord worker, BlockPos pos)
    {
        var ba = sapi.World.BlockAccessor;
        if (!IsClear(pos)) return true;
        var stack = TakeFromPack(colony, worker, IsSoilStack, 1) ?? TakeFromPack(colony, worker, IsFillStack, 1);
        if (stack?.Block is not { } block) return false;
        ba.SetBlock(block.Id, pos);
        ba.TriggerNeighbourBlockUpdate(pos);
        if (block.Sounds?.Place != null) sapi.World.PlaySoundAt(block.Sounds.Place, pos, 0);
        return true;
    }

    // ---- Torches -------------------------------------------------------------------------------

    /// <summary>A torch a miner can put down: lit or gone out, not burned out.</summary>
    public static bool IsTorchStack(ItemStack? stack) => stack?.Block is { } block && IsTorch(block) && !block.Code.Path.Contains("-burnedout-");

    /// <summary>Asks couriers to bring torches to the Miner's Hut, once at a time.</summary>
    public void EnsureTorchRequest(Colony colony, Building hut)
    {
        var chest = HutStorage(colony, hut).FirstOrDefault();
        if (chest == null) return;
        const string code = "game:torch-*";
        string tag = RequestTag(hut);
        if (!colony.Requests.Any(r => r.RequestedBy == tag && r.ItemCode == code)) AddRequest(colony, code, Math.Max(1, Config.MinerTorchesCarried), chest.Pos, tag);
    }

    /// <summary>
    /// Puts a torch from the worker's inventory down where it stands when it is too dark there (light
    /// below MinerTorchLight): on the floor, lit, as a player would. The torch burns out in time like any
    /// other. True if one went down.
    /// </summary>
    public bool PlaceTorchIfDark(Colony colony, CitizenRecord worker, BlockPos at)
    {
        if (Config.MinerTorchLight <= 0) return false;
        var ba = sapi.World.BlockAccessor;
        if (ba.GetChunkAtBlockPos(at) == null || ba.GetLightLevel(at, EnumLightLevelType.MaxLight) >= Config.MinerTorchLight) return false;
        if (ba.GetBlock(at).Id != 0 || ba.GetBlock(at, BlockLayersAccess.Fluid).Id != 0) return false;
        if (!ba.GetBlock(at.DownCopy()).SideSolid[BlockFacing.UP.Index]) return false;
        if (!CanWorkAt(colony, at) || PackCount(worker, IsTorchStack) == 0) return false;

        var torch = TakeFromPack(colony, worker, IsTorchStack, 1);
        if (torch?.Block is not { } held) return false;
        var code = LitTorch(held.Variant.ContainsKey("orientation") ? held.CodeWithVariant("orientation", "up") : held.Code);
        if (sapi.World.GetBlock(code) is not { } lit)
        {
            AddToPack(colony, worker, torch);
            return false;
        }
        var stack = new ItemStack(lit);
        ba.SetBlock(lit.Id, at, stack);
        ba.TriggerNeighbourBlockUpdate(at);
        if (lit.Sounds?.Place != null) sapi.World.PlaySoundAt(lit.Sounds.Place, at, 0);
        return true;
    }

    // ---- Choosing where to mine -----------------------------------------------------------

    /// <summary>Forgets the miner's site and tunnel, so it picks a new reading. The staircases it dug there get filled back in.</summary>
    public void ClearMine(Colony colony, Building hut, MineTrip trip)
    {
        var site = trip.Site;
        foreach (var entrance in trip.Entrances)
        {
            if (!trip.Seals.Any(s => s.Equals(entrance))) trip.Seals.Add(entrance);
        }
        trip.Entrances.Clear();
        trip.Site = null;
        trip.SiteOre = "";
        trip.Entrance = null;
        trip.End = null;
        trip.Target = null;
        oreBlocks.Remove(trip);
        blockedOre.Remove(trip);
        if (trip == hut.Trip && nodeFinds.TryGetValue(hut.BuildingId, out var finds) && site != null) finds.Forget(site, Config.MinerOreSearchRadius + NodeSearchRadius());
        MarkDirty(colony);
    }

    /// <summary>Forgets the trips of the hut's miner and of its prospectors on Mine ore, as when the ore to dig changes.</summary>
    public void ClearMines(Colony colony, Building hut)
    {
        ClearMine(colony, hut, hut.Trip);
        foreach (var worker in WorkersAt(colony, hut)) ClearMine(colony, hut, worker.Trip);
    }

    /// <summary>Every trip to prospected ore in the colony: each Miner's Hut's, and each prospector's on Mine ore.</summary>
    IEnumerable<MineTrip> AllTrips(Colony colony) =>
        colony.Buildings.Where(b => b.Type == BuildingTypes.Miner).Select(b => b.Trip)
            .Concat(colony.Citizens.Where(c => c.Alive && c.JobCode == Jobs.Prospector && c.WorkRole == ProspectorRoles.Mine).Select(c => c.Trip));

    /// <summary>The reading the Miner's Hut is working, or null (and the mine forgotten) when it no longer fits.</summary>
    OreSite? CurrentMineSite(Colony colony, Building hut, MineTrip trip)
    {
        if (trip.Site == null) return null;
        var site = colony.OreSites.Find(s => s.Pos.Equals(trip.Site));
        if (site != null && !site.MinedOut.Contains(trip.SiteOre) && (hut.MineOre.Length == 0 || hut.MineOre == trip.SiteOre)
            && MayCollect(hut, "ore-" + trip.SiteOre)) return site;
        ClearMine(colony, hut, trip);
        return null;
    }

    /// <summary>
    /// The strongest reading in the miner's range of the ore it is set to dig (any ore when none is set),
    /// left alone by the colony's other miners, or null.
    /// </summary>
    OreSite? ChooseMineSite(Colony colony, Building hut, MineTrip trip)
    {
        int radius = GatherRadius(hut);
        // A trip away from the shaft is only worth it for good ore, unless an ore was chosen.
        double least = hut.MineOre.Length > 0 ? Config.MinerMinFactor : Math.Max(Config.MinerMinFactor, Config.MinerTripMinFactor);
        var taken = AllTrips(colony).Where(t => t != trip && t.Site != null).Select(t => t.Site!).ToList();
        OreSite? bestSite = null;
        string bestOre = "";
        double bestScore = double.MinValue;
        foreach (var site in colony.OreSites)
        {
            double dx = site.Pos.X - hut.HutPos.X, dz = site.Pos.Z - hut.HutPos.Z;
            double distance = Math.Sqrt(dx * dx + dz * dz);
            if (distance > radius || taken.Any(t => t.Equals(site.Pos))) continue;
            var ores = site.Ores.ToList();
            if (site.HintOre.Length > 0 && !ores.Any(o => o.Code == site.HintOre)) ores.Add(new OreFind { Code = site.HintOre });
            foreach (var ore in ores)
            {
                bool found = ore.Code == site.HintOre;
                if ((ore.Factor < least && !found) || site.MinedOut.Contains(ore.Code)) continue;
                if (hut.MineOre.Length > 0 && ore.Code != hut.MineOre) continue;
                if (!MayCollect(hut, "ore-" + ore.Code)) continue;
                // Ore a gatherer found lying here comes first, then a stronger reading; between equal ones, the nearer.
                double score = (found ? 1 : 0) + ore.Factor - distance / 10000;
                if (score <= bestScore) continue;
                (bestSite, bestOre, bestScore) = (site, ore.Code, score);
            }
        }
        // Ore a gatherer found that no prospector has read yet: the miner goes and digs for it all the same.
        foreach (var hint in colony.OreHints)
        {
            double dx = hint.Pos.X - hut.HutPos.X, dz = hint.Pos.Z - hut.HutPos.Z;
            double distance = Math.Sqrt(dx * dx + dz * dz);
            if (distance > radius || taken.Any(t => t.Equals(hint.Pos))) continue;
            if (hut.MineOre.Length > 0 && hint.Ore != hut.MineOre) continue;
            if (!MayCollect(hut, "ore-" + hint.Ore)) continue;
            double score = 1 - distance / 10000;
            if (score <= bestScore) continue;
            bestSite = new OreSite { Pos = hint.Pos.Copy(), TotalDays = hint.TotalDays, HintOre = hint.Ore };
            (bestOre, bestScore) = (hint.Ore, score);
        }
        if (bestSite == null) return null;
        if (bestSite.Unread && !colony.OreSites.Contains(bestSite))
        {
            colony.OreSites.Add(bestSite);
            colony.OreHints.RemoveAll(h => h.Pos.Equals(bestSite.Pos));
        }
        trip.Site = bestSite.Pos.Copy();
        trip.SiteOre = bestOre;
        trip.Entrance = trip.End = trip.Target = null;
        MarkDirty(colony);
        return bestSite;
    }

    /// <summary>
    /// Every block of the ore within MinerOreSearchRadius of the site, from the surface down. Null while
    /// some of that ground is not loaded, so it is not taken for dug out.
    /// </summary>
    List<BlockPos>? OreBlocksAt(MineTrip trip, OreSite site, string ore)
    {
        if (oreBlocks.TryGetValue(trip, out var cached) && cached.Site.Equals(site.Pos) && cached.Ore == ore) return cached.Blocks;

        var ba = sapi.World.BlockAccessor;
        int r = Math.Max(1, Config.MinerOreSearchRadius);
        var found = new List<BlockPos>();
        var pos = new BlockPos(0);
        for (int x = site.Pos.X - r; x <= site.Pos.X + r; x++)
        for (int z = site.Pos.Z - r; z <= site.Pos.Z + r; z++)
        {
            pos.Set(x, site.Pos.Y, z);
            if (ba.GetChunkAtBlockPos(pos) == null) return null;
            int top = ba.GetTerrainMapheightAt(pos);
            for (int y = top; y > 1; y--)
            {
                pos.Y = y;
                var block = ba.GetBlock(pos);
                if (block.Code?.Path == "mantle") break;
                if (IsOreOf(block, ore)) found.Add(pos.Copy());
            }
        }
        oreBlocks[trip] = (site.Pos.Copy(), ore, found);
        return found;
    }

    bool IsBlockedOre(MineTrip trip, BlockPos pos)
    {
        if (!blockedOre.TryGetValue(trip, out var blocked) || !blocked.TryGetValue(pos, out long until)) return false;
        if (sapi.World.ElapsedMilliseconds < until) return true;
        blocked.Remove(pos);
        return false;
    }

    /// <summary>Leaves an ore block alone for a while: its way is flooded, in someone else's land, or too hard for the miner's pick.</summary>
    public void BlockOre(MineTrip trip, BlockPos pos)
    {
        if (!blockedOre.TryGetValue(trip, out var blocked)) blockedOre[trip] = blocked = new();
        blocked[pos.Copy()] = sapi.World.ElapsedMilliseconds + BlockedOreForgetMs;
    }

    /// <summary>Steps along the ground and up or down, the longest way first; a staircase can climb one block per step.</summary>
    static int Horizontal(BlockPos a, BlockPos b) => Math.Abs(a.X - b.X) + Math.Abs(a.Z - b.Z);

    /// <summary>
    /// The next ore block to tunnel to. From the end of the tunnel: the nearest one a staircase can reach
    /// (at least one step across for every step up or down). Without a tunnel: the shallowest, nearest the site.
    /// </summary>
    BlockPos? NextOreBlock(Colony colony, MineTrip trip, List<BlockPos> blocks, string ore, OreSite site, System.Func<BlockPos, bool> skip)
    {
        var ba = sapi.World.BlockAccessor;
        blocks.RemoveAll(p => !IsOreOf(ba.GetBlock(p), ore));
        var open = blocks.Where(p => !skip(p) && !IsBlockedOre(trip, p) && CanWorkAt(colony, p)).ToList();
        if (open.Count == 0) return null;
        if (trip.End is { } end)
        {
            var reachable = open.Where(p => Horizontal(end, p) >= Math.Abs(end.Y - p.Y) + 1)
                .OrderBy(p => Horizontal(end, p) + Math.Abs(end.Y - p.Y)).FirstOrDefault();
            if (reachable != null) return reachable;
            // Nothing a staircase from here reaches: start a new one from the surface.
            trip.End = null;
        }
        return open.OrderByDescending(p => p.Y).ThenBy(p => Horizontal(site.Pos, p)).First();
    }

    /// <summary>
    /// Where a staircase down to the ore block starts: a spot on the surface far enough away to the side
    /// (one step across for every block down) in the direction nearest the hut. Null if there is none.
    /// </summary>
    BlockPos? PlanEntrance(Colony colony, Building hut, BlockPos ore, List<(BlockPos Min, BlockPos Max)> boxes)
    {
        var ba = sapi.World.BlockAccessor;
        BlockPos? best = null;
        double bestDistance = double.MaxValue;
        foreach (var face in Horizontals)
        {
            var spot = new BlockPos(0);
            int distance = 1;
            bool ok = false;
            for (int tries = 0; tries < 6; tries++)
            {
                spot.Set(ore.X + face.Normali.X * distance, ore.Y, ore.Z + face.Normali.Z * distance);
                if (ba.GetChunkAtBlockPos(spot) == null) break;
                spot.Y = ba.GetTerrainMapheightAt(spot) + 1;
                int need = spot.Y - ore.Y + 1;
                if (distance >= need) { ok = true; break; }
                distance = need;
            }
            if (!ok || spot.Y <= ore.Y) continue;
            if (ba.GetBlock(spot, BlockLayersAccess.Fluid).Id != 0 || ba.GetBlock(spot.DownCopy(), BlockLayersAccess.Fluid).Id != 0) continue;
            if (Inside(spot, boxes) || !CanWorkAt(colony, spot)) continue;
            double d = spot.DistanceTo(hut.HutPos);
            if (d >= bestDistance) continue;
            (best, bestDistance) = (spot.Copy(), d);
        }
        return best;
    }

    /// <summary>
    /// The tunnel from a standing spot to the ore block, as the spots to stand on along the way, each with
    /// the blocks to dig out (top first) to get there. One step across per step, and one up or down while
    /// the height differs, ending beside the ore at its height.
    /// </summary>
    public static List<(BlockPos Feet, List<BlockPos> Digs)> MineRoute(BlockPos from, BlockPos ore)
    {
        var route = new List<(BlockPos, List<BlockPos>)>();
        var at = from.Copy();
        // The starting spot itself must be open (two high).
        route.Add((at.Copy(), new List<BlockPos> { at.UpCopy(), at.Copy() }));
        for (int guard = 0; guard < 512; guard++)
        {
            int dx = ore.X - at.X, dz = ore.Z - at.Z, dy = ore.Y - at.Y;
            if (Math.Abs(dx) + Math.Abs(dz) <= 1 && dy == 0) break;
            if (Math.Abs(dx) + Math.Abs(dz) <= 1) break; // cannot get level from here; the caller checks the reach
            var next = at.Copy();
            if (Math.Abs(dx) >= Math.Abs(dz)) next.X += Math.Sign(dx);
            else next.Z += Math.Sign(dz);
            int vertical = Math.Sign(dy);
            next.Y += vertical;
            var digs = new List<BlockPos>();
            if (vertical < 0) digs.Add(next.UpCopy(2));
            if (vertical > 0) digs.Add(at.UpCopy(2));
            digs.Add(next.UpCopy());
            digs.Add(next.Copy());
            route.Add((next.Copy(), digs));
            at = next;
        }
        return route;
    }

    /// <summary>
    /// An ore block within the miner's reach of where it stands with open air beside it, or null. Blocks in
    /// keep (the floors of the shaft and its tunnels) are left alone even when they are ore.
    /// </summary>
    BlockPos? OreInReach(Colony colony, MineTrip trip, BlockPos end, System.Func<Block, bool> isOre, System.Func<BlockPos, bool> skip,
        List<(BlockPos Min, BlockPos Max)> boxes, ISet<BlockPos>? keep = null)
    {
        var ba = sapi.World.BlockAccessor;
        int r = Math.Max(1, Config.MinerReach);
        BlockPos? best = null;
        double bestDistance = double.MaxValue;
        var eye = end.UpCopy();
        for (int x = -r; x <= r; x++)
        for (int y = -r; y <= r + 1; y++)
        for (int z = -r; z <= r; z++)
        {
            var pos = end.AddCopy(x, y, z);
            if (!isOre(ba.GetBlock(pos)) || skip(pos) || IsBlockedOre(trip, pos) || keep?.Contains(pos) == true) continue;
            double d = pos.DistanceTo(eye);
            if (d > r + 0.5 || d >= bestDistance) continue;
            if (!BlockFacing.ALLFACES.Any(f => IsClear(pos.AddCopy(f)))) continue;
            if (!CanDig(colony, pos, boxes)) continue;
            (best, bestDistance) = (pos, d);
        }
        return best;
    }

    /// <summary>
    /// The miner's next job, and its status for the hut window. With "Go to prospected ore" on, good ore
    /// the prospectors found comes first; otherwise, or when there is none it can get to, the shaft. At is
    /// where the miner stands. Null when there is nothing to dig.
    /// </summary>
    public MineJob? NextMineJob(Colony colony, Building hut, CitizenRecord miner, BlockPos at, System.Func<BlockPos, bool> skip, bool canFill, bool canSearch, out string status)
    {
        if (canFill && NextFillJob(colony, hut.Trip, skip, out status) is { } fill) return fill;
        if (NextNodeJob(colony, hut, skip, out status) is { } node) return node;
        MineJob? job;
        // A mineshaft made of pieces (ColonyManager.Mineshaft.cs) comes first; prospected ore when it is done or waiting.
        if (NextMineshaftJob(colony, hut, miner, at, skip, out status) is { } shaft) job = shaft;
        else if (status.Length > 0)
        {
            string waiting = status;
            job = hut.MineProspected ? NextSiteJob(colony, hut, hut.Trip, skip, out status) : null;
            if (job == null) status = waiting;
        }
        else job = hut.MineProspected && NextSiteJob(colony, hut, hut.Trip, skip, out status) is { } trip ? trip : NextShaftJob(colony, hut, at, skip, out status);
        // Somewhere new in the mine: a node search there first, for ore close by.
        if (canSearch && job is { Kind: MineWork.Dig } dig && NeedsNodeSearch(hut, dig.Stand))
        {
            status = "mine-node-search";
            return new MineJob(MineWork.Search, dig.Stand.Copy(), dig.Stand.Copy(), false);
        }
        return job;
    }

    /// <summary>
    /// The miner's next job at prospected ore: walk to the site, dig the next block of the tunnel, or dig
    /// out ore. Null when there is no site worth a trip, or none it can get to right now.
    /// </summary>
    public MineJob? NextSiteJob(Colony colony, Building hut, MineTrip trip, System.Func<BlockPos, bool> skip, out string status)
    {
        var ba = sapi.World.BlockAccessor;
        var boxes = DigProtected(colony);
        for (int tries = 0; tries < 6; tries++)
        {
            var site = CurrentMineSite(colony, hut, trip) ?? ChooseMineSite(colony, hut, trip);
            if (site == null)
            {
                status = colony.OreSites.Count == 0 ? "mine-no-readings" : hut.MineOre.Length > 0 ? "mine-no-site-ore" : "mine-no-site";
                return null;
            }
            string ore = trip.SiteOre;

            // The ground must be loaded to look for the ore: walk to the site first.
            var blocks = OreBlocksAt(trip, site, ore);
            if (blocks == null)
            {
                status = "mine-travelling";
                var stand = trip.Entrance ?? site.Pos.UpCopy();
                return new MineJob(MineWork.Travel, stand, stand, false);
            }

            // Ore around the end of the tunnel comes out first.
            if (trip.End != null && OreInReach(colony, trip, trip.End, b => IsOreOf(b, ore) && MayDig(hut, b), skip, boxes) is { } near)
            {
                status = "mine-ore";
                return new MineJob(MineWork.Dig, near, trip.End.Copy(), true);
            }

            if (trip.Target == null || !IsOreOf(ba.GetBlock(trip.Target), ore) || skip(trip.Target) || IsBlockedOre(trip, trip.Target))
            {
                trip.Target = NextOreBlock(colony, trip, blocks, ore, site, skip);
                MarkDirty(colony);
                if (trip.Target == null)
                {
                    if (blocks.Count > 0)
                    {
                        // Ore is left, but none the miner can get to right now.
                        status = "mine-blocked";
                        return null;
                    }
                    site.MinedOut.Add(ore);
                    LogEvent(colony, "event-mine-done", OreName(ore), DirectionFrom(hut.HutPos, site.Pos));
                    ClearMine(colony, hut, trip);
                    continue;
                }
            }

            if (trip.End == null)
            {
                var entrance = PlanEntrance(colony, hut, trip.Target, boxes);
                if (entrance == null)
                {
                    BlockOre(trip, trip.Target);
                    trip.Target = null;
                    continue;
                }
                trip.Entrance = entrance;
                trip.Entrances.Add(entrance.Copy());
                trip.End = entrance.Copy();
                MarkDirty(colony);
            }

            // Follow the tunnel to the ore; the first block still in the way is the next to dig.
            var target = trip.Target;
            var standAt = trip.End.Copy();
            bool blocked = false;
            foreach (var (feet, digs) in MineRoute(trip.End, target))
            {
                foreach (var dig in digs)
                {
                    if (IsClear(dig)) continue;
                    if (dig.Equals(target)) break;
                    if (skip(dig) || !CanDig(colony, dig, boxes))
                    {
                        blocked = true;
                        break;
                    }
                    status = "mine-tunnelling";
                    return new MineJob(MineWork.Dig, dig, standAt, IsOre(ba.GetBlock(dig)));
                }
                if (blocked) break;
                standAt = feet;
            }
            if (blocked)
            {
                BlockOre(trip, target);
                trip.Target = null;
                continue;
            }

            // The tunnel reaches the ore: dig it out from the tunnel's new end.
            trip.End = standAt;
            MarkDirty(colony);
            if (!CanDig(colony, target, boxes))
            {
                BlockOre(trip, target);
                trip.Target = null;
                continue;
            }
            status = "mine-ore";
            return new MineJob(MineWork.Dig, target, standAt, true);
        }
        status = "mine-blocked";
        return null;
    }

    // ---- The shaft beside the hut -----------------------------------------------------------

    /// <summary>The cells round the shaft's pillar, clockwise from the north-west corner; odd ones are the middles of the sides.</summary>
    static readonly (int X, int Z)[] ShaftRing = { (-1, -1), (0, -1), (1, -1), (1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0) };

    /// <summary>
    /// One spot of the shaft or of a tunnel off it: where a settler stands once it is dug out (Feet), how
    /// many blocks high it is dug from there, and where the miner stands to dig it. Tunnel is the shaft
    /// step the tunnel leads off (-1 for the shaft itself), and Level the shaft's level, from 0 at the top.
    /// </summary>
    public readonly record struct ShaftSpot(BlockPos Feet, BlockPos Stand, int Height, int Tunnel, int Level);

    /// <summary>
    /// The whole mine for a shaft centred on centre (the height of the ground there): a spiral staircase
    /// round a pillar, one step down for each cell round it, so every turn goes 8 blocks deeper. The first
    /// turn is the way in; every turn below it is a level, with a tunnel out from the middle of each side.
    /// Steps are dug three high so a settler can walk down them, tunnels two high. In digging order: each
    /// level's tunnels are dug out before the shaft goes deeper.
    /// </summary>
    public static List<ShaftSpot> ShaftPlan(BlockPos centre, int levels, int tunnelLength)
    {
        var plan = new List<ShaftSpot>();
        var (fx, fz) = ShaftRing[0];
        // The first step is dug from just outside the shaft.
        BlockPos previous = new(centre.X + fx * 2, centre.Y, centre.Z + fz * 2);
        for (int k = 0; k < 8 * (levels + 1); k++)
        {
            var (dx, dz) = ShaftRing[k % 8];
            var feet = new BlockPos(centre.X + dx, centre.Y - k, centre.Z + dz);
            if (feet.Y <= 3) break;
            plan.Add(new ShaftSpot(feet, previous, 3, -1, k / 8));
            previous = feet;
            if (k < 8 || k % 2 == 0) continue;
            // The middle of a side: a tunnel straight out from it, away from the pillar.
            var stand = feet;
            for (int t = 1; t <= tunnelLength; t++)
            {
                var cell = new BlockPos(feet.X + dx * t, feet.Y, feet.Z + dz * t);
                plan.Add(new ShaftSpot(cell, stand, 2, k, k / 8));
                stand = cell;
            }
        }
        return plan;
    }

    /// <summary>How many levels the Miner's Hut's shaft may have at its hut level.</summary>
    int ShaftLevels(Building hut) => Math.Max(1, hut.Level) * Math.Max(1, Config.MinerLevelsPerHutLevel);

    /// <summary>
    /// A spot beside a hut for something dug into the ground, such as the Miner's Hut's shaft or the
    /// Quarry's pit: the middle of one of the hut's sides, far enough out that a square half blocks round
    /// it leaves a gap of two to the hut. The side facing away from the Town Hall is tried first. The
    /// square must be loaded, in the colony's land, clear of other buildings and not under water. The
    /// spot is at the height a settler stands on the ground there, or null when no side will do.
    /// </summary>
    BlockPos? SpotBeside(Colony colony, Building hut, int half)
    {
        var excluded = badShaftSpots.GetValueOrDefault(hut.BuildingId);
        var ba = sapi.World.BlockAccessor;
        // The hut at its highest level, so what is dug stays clear of it when it is upgraded.
        var hutBox = Footprint(hut);
        var others = colony.Buildings.Where(b => b != hut).Select(Footprint).ToList();
        int gap = half + 3, midX = (hutBox.X1 + hutBox.X2) / 2, midZ = (hutBox.Z1 + hutBox.Z2) / 2;
        var sides = new[]
        {
            (X: midX, Z: hutBox.Z2 + gap), (X: hutBox.X2 + gap, Z: midZ), (X: midX, Z: hutBox.Z1 - gap), (X: hutBox.X1 - gap, Z: midZ)
        }.OrderByDescending(c => (double)(c.X - colony.TownHallPos.X) * (c.X - colony.TownHallPos.X) + (double)(c.Z - colony.TownHallPos.Z) * (c.Z - colony.TownHallPos.Z));
        foreach (var (cx, cz) in sides)
        {
            if (excluded != null && excluded.Contains((cx, cz))) continue;
            bool ok = true;
            var pos = new BlockPos(0);
            for (int x = cx - half; x <= cx + half && ok; x++)
            for (int z = cz - half; z <= cz + half && ok; z++)
            {
                pos.Set(x, hut.HutPos.Y, z);
                if (ba.GetChunkAtBlockPos(pos) == null) { ok = false; break; }
                pos.Y = ba.GetTerrainMapheightAt(pos);
                if (ba.GetBlock(pos, BlockLayersAccess.Fluid).Id != 0 || ba.GetBlock(pos.UpCopy(), BlockLayersAccess.Fluid).Id != 0) ok = false;
                else if (!CanWorkAt(colony, pos)) ok = false;
                else if (others.Any(o => x >= o.X1 - 2 && x <= o.X2 + 2 && z >= o.Z1 - 2 && z <= o.Z2 + 2)) ok = false;
            }
            if (!ok) continue;
            pos.Set(cx, hut.HutPos.Y, cz);
            return new BlockPos(cx, ba.GetTerrainMapheightAt(pos) + 1, cz);
        }
        return null;
    }

    // ---- A shaft that stays blocked (0.198.0) ----------------------------------------------
    // Aaron's miner said "mine shaft blocked" for 19 game days and never dug. The block in the way is now kept for the
    // status and the /colony report, and a shaft spot the miner chose himself is moved to another side of the hut
    // once it has been blocked for a while. A spot from the blueprint's Mine waypoint stays where it is.

    /// <summary>What blocks each Miner's Hut's shaft (by building id): where, which block, and since when (game hours).</summary>
    public readonly Dictionary<int, (BlockPos Pos, string Block, double SinceHours)> ShaftBlockedAt = new();
    /// <summary>Shaft spots (x, z) given up per Miner's Hut, so the next one is on another side.</summary>
    readonly Dictionary<int, HashSet<(int X, int Z)>> badShaftSpots = new();

    void ShaftBlocked(Colony colony, Building hut, BlockPos centre, BlockPos dig, bool marked)
    {
        double hours = sapi.World.Calendar.TotalHours;
        string code = sapi.World.BlockAccessor.GetBlock(dig).Code?.ToString() ?? "?";
        if (!ShaftBlockedAt.TryGetValue(hut.BuildingId, out var was) || !was.Pos.Equals(dig))
        {
            ShaftBlockedAt[hut.BuildingId] = (dig.Copy(), code, hours);
            return;
        }
        if (marked || hours - was.SinceHours < Config.MinerShaftMoveHours) return;
        if (!badShaftSpots.TryGetValue(hut.BuildingId, out var bad)) badShaftSpots[hut.BuildingId] = bad = new();
        bad.Add((centre.X, centre.Z));
        var old = hut.ShaftPos;
        hut.ShaftPos = SpotBeside(colony, hut, 1);
        if (hut.ShaftPos == null)
        {
            // No other side will do: keep the old spot and wait for the way to be cleared.
            hut.ShaftPos = old;
            ShaftBlockedAt[hut.BuildingId] = (dig.Copy(), code, hours);
            return;
        }
        ShaftBlockedAt.Remove(hut.BuildingId);
        Mod.Logger.Notification("[createacolony] Miner's shaft at {0} was blocked by {1} at {2}; moved to {3}", old, code, dig, hut.ShaftPos);
        MarkDirty(colony);
    }

    /// <summary>
    /// The middle of the Miner's Hut's shaft: the Mine waypoint in its blueprint, or else a spot beside the
    /// hut chosen the first time it is needed; null when there is no room beside the hut.
    /// </summary>
    BlockPos? ShaftFor(Colony colony, Building hut, out bool marked, int half = 1)
    {
        var mark = WaypointOf(hut, "mine");
        marked = mark != null;
        if (mark != null)
        {
            if (!mark.Equals(hut.ShaftPos)) { hut.ShaftPos = mark; MarkDirty(colony); }
            return mark;
        }
        if (hut.ShaftPos != null) return hut.ShaftPos;
        hut.ShaftPos = SpotBeside(colony, hut, half);
        if (hut.ShaftPos != null) MarkDirty(colony);
        return hut.ShaftPos;
    }

    /// <summary>
    /// The miner's next job in its shaft: ore it can see from where it stands first, then the next block
    /// of the shaft or of a level's tunnel. A tunnel that runs into water, a building or someone else's
    /// land just stops there; the shaft itself waits until the way is clear.
    /// </summary>
    MineJob? NextShaftJob(Colony colony, Building hut, BlockPos at, System.Func<BlockPos, bool> skip, out string status)
    {
        if (ShaftFor(colony, hut, out bool marked) is not { } centre)
        {
            status = "mine-no-shaft-spot";
            return null;
        }
        var ba = sapi.World.BlockAccessor;
        if (ba.GetChunkAtBlockPos(centre) == null)
        {
            status = "mine-travelling";
            return new MineJob(MineWork.Travel, centre, centre, false);
        }
        var (boxes, keep) = DigLimits(colony, hut, marked);
        var plan = ShaftPlan(centre, ShaftLevels(hut), Math.Max(1, Config.MinerTunnelLength));
        var floors = new HashSet<BlockPos>(plan.Select(p => p.Feet.DownCopy()));
        floors.UnionWith(keep);

        // Ore in sight of the spot in the mine where the miner stands comes out first.
        if (plan.Any(p => p.Feet.Equals(at)) && OreInReach(colony, hut.Trip, at, b => IsOre(b) && MayDig(hut, b), skip, boxes, floors) is { } ore)
        {
            status = "mine-ore";
            return new MineJob(MineWork.Dig, ore, at.Copy(), true);
        }

        int stoppedTunnel = int.MinValue;
        foreach (var spot in plan)
        {
            if (spot.Tunnel >= 0 && spot.Tunnel == stoppedTunnel) continue;
            for (int h = spot.Height - 1; h >= 0; h--)
            {
                var dig = spot.Feet.UpCopy(h);
                if (IsClear(dig)) continue;
                if (skip(dig) || keep.Contains(dig) || !CanDig(colony, dig, boxes))
                {
                    if (spot.Tunnel < 0)
                    {
                        status = "mine-shaft-blocked";
                        ShaftBlocked(colony, hut, centre, dig, marked);
                        return null;
                    }
                    stoppedTunnel = spot.Tunnel;
                    break;
                }
                status = spot.Tunnel >= 0 ? "mine-shaft-tunnel" : "mine-shaft";
                ShaftBlockedAt.Remove(hut.BuildingId);
                return new MineJob(MineWork.Dig, dig, spot.Stand.Copy(), IsOre(ba.GetBlock(dig)));
            }
        }
        status = "mine-shaft-done";
        return null;
    }

    /// <summary>How far the shaft has got: its depth below the ground, and the deepest level reached.</summary>
    (int Depth, int Level) ShaftProgress(Building hut)
    {
        if (hut.ShaftPos is not { } centre || sapi.World.BlockAccessor.GetChunkAtBlockPos(centre) == null) return (0, 0);
        int depth = 0, level = 0;
        foreach (var spot in ShaftPlan(centre, ShaftLevels(hut), 0))
        {
            if (!IsClear(spot.Feet) || !IsClear(spot.Feet.UpCopy())) break;
            depth = centre.Y - spot.Feet.Y;
            level = spot.Level;
        }
        return (depth, level);
    }

    /// <summary>The Miner's Hut window lines: the shaft, whether the miner goes to prospected ore, and where it digs now.</summary>
    string MineInfo(Colony colony, Building hut)
    {
        string line;
        if (MineshaftInfo(colony, hut) is { } pieces) line = pieces;
        else if (hut.ShaftPos is { } shaft)
        {
            var (depth, level) = ShaftProgress(hut);
            line = Lang.Get("createacolony:mine-shaft", depth, level, ShaftLevels(hut), shaft.X, shaft.Y, shaft.Z);
        }
        else line = Lang.Get("createacolony:mine-shaft-none");
        if (!hut.MineProspected) return line + "<br>" + Lang.Get("createacolony:mine-trips-off");

        string choice = hut.MineOre.Length == 0 ? Lang.Get("createacolony:mine-any-ore") : OreName(hut.MineOre);
        line += "<br>" + Lang.Get("createacolony:mine-digs-for", choice);
        if (TripInfo(colony, hut, hut.Trip) is { } trip) line += "<br>" + trip;
        return line;
    }

    /// <summary>Where a trip to prospected ore digs now, and how deep its staircase goes; null when it has no site.</summary>
    public string? TripInfo(Colony colony, Building hut, MineTrip trip)
    {
        if (trip.Site == null || trip.SiteOre.Length == 0) return null;
        var site = colony.OreSites.Find(s => s.Pos.Equals(trip.Site));
        var factor = site?.Ores.Find(o => o.Code == trip.SiteOre)?.Factor ?? 0;
        string line = site?.HintOre == trip.SiteOre
            ? Lang.Get("createacolony:mine-site-found", OreName(trip.SiteOre), DirectionFrom(hut.HutPos, trip.Site))
            : Lang.Get("createacolony:mine-site", OreName(trip.SiteOre), DensityName(factor), DirectionFrom(hut.HutPos, trip.Site));
        if (trip.Entrance != null && trip.End != null)
        {
            line += "<br>" + Lang.Get("createacolony:mine-depth", Math.Max(0, trip.Entrance.Y - trip.End.Y), trip.Entrance.X, trip.Entrance.Y, trip.Entrance.Z);
        }
        return line;
    }

    // ---- Node search with the prospecting pick -----------------------------------------------

    /// <summary>
    /// What a miner's node searches found: where it searched, the ore blocks it found (each with the spot
    /// it searched from), and the side tunnel it is digging to one of them (its target and how far it got).
    /// </summary>
    sealed class NodeFinds
    {
        public readonly List<BlockPos> Searched = new();
        public readonly List<(BlockPos Ore, BlockPos From)> Ore = new();
        public BlockPos? Target, End;

        /// <summary>Drops what was searched and found around a finished site, within range of it.</summary>
        public void Forget(BlockPos site, int range)
        {
            bool Near(BlockPos p) => Math.Abs(p.X - site.X) <= range && Math.Abs(p.Z - site.Z) <= range;
            Searched.RemoveAll(Near);
            Ore.RemoveAll(f => Near(f.From));
            Target = End = null;
        }
    }

    /// <summary>Each Miner's Hut's node finds. Kept in memory only: after a restart the miner just searches again.</summary>
    readonly Dictionary<int, NodeFinds> nodeFinds = new();

    NodeFinds FindsOf(Building hut)
    {
        if (!nodeFinds.TryGetValue(hut.BuildingId, out var finds)) nodeFinds[hut.BuildingId] = finds = new NodeFinds();
        return finds;
    }

    /// <summary>
    /// How far a node search reaches: MinerNodeSearchRadius, or when that is 0 the world's own setting for
    /// the prospecting pick's node search mode, or 8 when the world has none.
    /// </summary>
    public int NodeSearchRadius()
    {
        if (Config.MinerNodeSearchRadius > 0) return Config.MinerNodeSearchRadius;
        var value = sapi.World.Config?["propickNodeSearchRadius"]?.GetValue()?.ToString();
        return int.TryParse(value, out int radius) && radius > 0 ? Math.Min(radius, 32) : 8;
    }

    /// <summary>True when the miner has not yet searched near this spot (within half a search's reach).</summary>
    bool NeedsNodeSearch(Building hut, BlockPos stand)
    {
        double half = NodeSearchRadius() / 2.0;
        return !FindsOf(hut).Searched.Any(p => p.DistanceTo(stand) <= half);
    }

    /// <summary>
    /// A node search, as the prospecting pick's node search mode does for a player: every ore block within
    /// reach of where the miner stands is noted, to be dug out. Wears the pick down. The number of new
    /// ore blocks found.
    /// </summary>
    public int NodeSearch(Colony colony, Building hut, EntityAgent entity, BlockPos at, ItemSlot pick)
    {
        var finds = FindsOf(hut);
        finds.Searched.Add(at.Copy()); // even without a pick in hand, so the miner does not try here again and again
        if (!IsProspectingPick(pick.Itemstack)) return 0;
        var ba = sapi.World.BlockAccessor;
        int r = NodeSearchRadius(), found = 0;
        var known = new HashSet<BlockPos>(finds.Ore.Select(f => f.Ore));
        var pos = new BlockPos(0);
        for (int x = -r; x <= r; x++)
        for (int y = -r; y <= r; y++)
        for (int z = -r; z <= r; z++)
        {
            pos.Set(at.X + x, at.Y + y, at.Z + z);
            if (pos.Y <= 1 || !IsOre(ba.GetBlock(pos)) || known.Contains(pos)) continue;
            finds.Ore.Add((pos.Copy(), at.Copy()));
            found++;
        }
        for (int i = 0; i < Math.Max(0, Config.MinerNodeSearchWear); i++) WearTool(entity, pick);
        AddStat(hut, "node-searches");
        MarkDirty(colony);
        return found;
    }

    /// <summary>
    /// The miner's next job digging out ore its node searches found: the next block of a side tunnel to it,
    /// or the ore itself. A side tunnel starts from where the search was made (or from the end of the last
    /// one, when the next ore is in reach of it) and never digs away the floor of a way through the mine
    /// or the shaft's pillar. Ore it cannot get to is let go. Null when there is none to dig.
    /// </summary>
    MineJob? NextNodeJob(Colony colony, Building hut, System.Func<BlockPos, bool> skip, out string status)
    {
        status = "";
        if (!nodeFinds.TryGetValue(hut.BuildingId, out var finds) || finds.Ore.Count == 0) return null;
        var ba = sapi.World.BlockAccessor;
        finds.Ore.RemoveAll(f => !IsOre(ba.GetBlock(f.Ore)));
        if (finds.Ore.Count == 0)
        {
            finds.Target = finds.End = null;
            return null;
        }

        var (boxes, keep) = DigLimits(colony, hut, WaypointOf(hut, "mine") != null);
        var guarded = new HashSet<BlockPos>(keep);
        if (MineshaftGuard(colony, hut) is { } pieces) guarded.UnionWith(pieces);
        else if (hut.ShaftPos is { } centre)
        {
            var plan = ShaftPlan(centre, ShaftLevels(hut), Math.Max(1, Config.MinerTunnelLength));
            guarded.UnionWith(plan.Select(p => p.Feet.DownCopy()));
            int bottom = plan.Count > 0 ? plan.Min(p => p.Feet.Y) : centre.Y;
            for (int y = bottom - 1; y <= centre.Y; y++) guarded.Add(new BlockPos(centre.X, y, centre.Z));
        }
        // The floor of an open way someone walks on (two clear blocks above it) that the tunnel itself did not open.
        bool Guarded(BlockPos dig, ISet<BlockPos> tunnel) => guarded.Contains(dig)
            || !tunnel.Contains(dig.UpCopy()) && IsClear(dig.UpCopy()) && IsClear(dig.UpCopy(2));
        bool Reaches(BlockPos from, BlockPos ore) => Horizontal(from, ore) >= Math.Abs(from.Y - ore.Y) + 1;

        for (int tries = 0; tries < 8 && finds.Ore.Count > 0; tries++)
        {
            var current = finds.Target;
            if (current == null || !finds.Ore.Any(f => f.Ore.Equals(current)) || skip(current) || IsBlockedOre(hut.Trip, current))
            {
                finds.Target = null;
                var open = finds.Ore.Where(f => !skip(f.Ore) && !IsBlockedOre(hut.Trip, f.Ore)).ToList();
                if (open.Count == 0) return null;
                if (finds.End is { } end && open.Where(f => Reaches(end, f.Ore)).OrderBy(f => end.DistanceTo(f.Ore)).FirstOrDefault() is { Ore: not null } next)
                {
                    finds.Target = next.Ore;
                }
                else
                {
                    finds.End = null;
                    // Ore no side tunnel from its search spot can reach (straight above or below it) is let go.
                    finds.Ore.RemoveAll(f => !Reaches(f.From, f.Ore));
                    var first = finds.Ore.Where(f => !skip(f.Ore) && !IsBlockedOre(hut.Trip, f.Ore)).OrderBy(f => f.From.DistanceTo(f.Ore)).FirstOrDefault();
                    if (first.Ore == null) return null;
                    finds.Target = first.Ore;
                    finds.End = first.From.Copy();
                }
            }
            var target = finds.Target!;
            var from = finds.End ?? finds.Ore.First(f => f.Ore.Equals(target)).From;

            var route = MineRoute(from, target);
            var tunnel = new HashSet<BlockPos>(route.SelectMany(r => r.Digs));
            var standAt = from.Copy();
            bool blocked = Guarded(target, tunnel) || !CanDig(colony, target, boxes);
            foreach (var (feet, digs) in route)
            {
                if (blocked) break;
                foreach (var dig in digs)
                {
                    if (IsClear(dig)) continue;
                    if (dig.Equals(target)) break;
                    if (skip(dig) || Guarded(dig, tunnel) || !CanDig(colony, dig, boxes))
                    {
                        blocked = true;
                        break;
                    }
                    status = "mine-node-tunnel";
                    return new MineJob(MineWork.Dig, dig, standAt, IsOre(ba.GetBlock(dig)));
                }
                if (!blocked) standAt = feet;
            }
            if (blocked)
            {
                finds.Ore.RemoveAll(f => f.Ore.Equals(target));
                finds.Target = finds.End = null;
                continue;
            }
            finds.End = standAt;
            status = "mine-node-ore";
            return new MineJob(MineWork.Dig, target, standAt, true);
        }
        return null;
    }
}
