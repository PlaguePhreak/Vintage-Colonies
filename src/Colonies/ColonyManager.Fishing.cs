using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using ItemMatch = System.Func<Vintagestory.API.Common.ItemStack, bool>;

namespace CreateAColony.Colonies;

/// <summary>
/// The Fisher (0.184.0). Its fisher stands on the bank of water near the hut with the game's fishing pole and catches
/// the fish the game's own fishing gives there: the fish that live in that water's climate, eating the bait it uses
/// (bait from the hut storage; without bait only the fish that bite on nothing). Ponds under 100 blocks of water have no
/// fish, as in the game, and every catch counts towards the game's fish depletion, so a spot fished out rests.
/// </summary>
public partial class ColonyManager
{
    /// <summary>A place to fish: the water block the line goes into, where to stand, and how big the water is.</summary>
    public sealed record FishingSpot(BlockPos Water, BlockPos Stand, int PondSize);

    readonly Dictionary<int, (long At, List<FishingSpot> Spots)> fishingSpots = new();
    List<EntityProperties>? fishTypes;

    public static bool IsFishingPole(ItemStack? s) => s?.Collectible is ItemFishingPole;
    public static bool IsFishBait(ItemStack s) => s.Collectible.Attributes?["isFishBait"].AsBool(false) == true;

    /// <summary>The game's fish: creatures of the fish class that drop something.</summary>
    List<EntityProperties> FishTypes() => fishTypes ??= sapi.World.EntityTypes
        .Where(t => t?.Class != null && t.Class.Contains("Fish") && t.Drops != null && t.Drops.Length > 0 && t.Drops[0]?.ResolvedItemstack != null)
        .ToList();

    /// <summary>The spots to fish from near the hut, nearest first, found again every few minutes.</summary>
    public List<FishingSpot> FishingSpots(Building hut)
    {
        long now = sapi.World.ElapsedMilliseconds;
        if (fishingSpots.TryGetValue(hut.BuildingId, out var known) && now - known.At < 300_000) return known.Spots;
        var spots = new List<FishingSpot>();
        var ba = sapi.World.BlockAccessor;
        int r = GatherRadius(hut);
        var h = hut.HutPos;
        var seen = new HashSet<BlockPos>();
        var columns = new List<(int Dx, int Dz)>();
        for (int dx = -r; dx <= r; dx += 2)
        for (int dz = -r; dz <= r; dz += 2)
            if (dx * dx + dz * dz <= r * r) columns.Add((dx, dz));
        foreach (var (dx, dz) in columns.OrderBy(c => c.Dx * c.Dx + c.Dz * c.Dz))
        {
            if (spots.Count >= 6) break;
            var top = new BlockPos(h.X + dx, h.Y + 4, h.Z + dz, h.dimension);
            for (int i = 0; i < 12; i++, top.Down())
            {
                var fluid = ba.GetBlock(top, BlockLayersAccess.Fluid);
                if (fluid.Id == 0)
                {
                    if (ba.GetBlock(top).SideSolid[BlockFacing.UP.Index]) break;
                    continue;
                }
                if (fluid.LiquidCode != "water" || seen.Contains(top)) break;
                // The water's surface: open air above it.
                if (ba.GetBlock(top.UpCopy(), BlockLayersAccess.Fluid).Id != 0) continue;
                var stand = BankBeside(ba, top);
                if (stand == null) break;
                int size = PondSize(ba, top, seen);
                if (size >= Config.FisherMinPond) spots.Add(new FishingSpot(top.Copy(), stand, size));
                break;
            }
        }
        fishingSpots[hut.BuildingId] = (now, spots);
        return spots;
    }

    /// <summary>A bank next to the water (solid ground with room to stand), searched up to 3 blocks out; null for none.</summary>
    static BlockPos? BankBeside(IBlockAccessor ba, BlockPos water)
    {
        for (int d = 1; d <= 3; d++)
        {
            foreach (var face in BlockFacing.HORIZONTALS)
            {
                var ground = water.AddCopy(face.Normali.X * d, 0, face.Normali.Z * d);
                for (int up = 0; up <= 2; up++, ground.Up())
                {
                    var block = ba.GetBlock(ground);
                    if (ba.GetBlock(ground, BlockLayersAccess.Fluid).Id != 0) break;
                    if (!block.SideSolid[BlockFacing.UP.Index]) continue;
                    var feet = ground.UpCopy();
                    if (ba.GetBlock(feet).CollisionBoxes?.Length > 0 || ba.GetBlock(feet.UpCopy()).CollisionBoxes?.Length > 0) break;
                    if (ba.GetBlock(feet, BlockLayersAccess.Fluid).Id != 0) break;
                    return feet;
                }
            }
        }
        return null;
    }

    /// <summary>How many water blocks this water reaches, counted up to 1200 (the game's fish need 100).</summary>
    static int PondSize(IBlockAccessor ba, BlockPos start, HashSet<BlockPos> seen)
    {
        var queue = new Queue<BlockPos>();
        var mine = new HashSet<BlockPos> { start.Copy() };
        queue.Enqueue(start.Copy());
        while (queue.Count > 0 && mine.Count < 1200)
        {
            var p = queue.Dequeue();
            foreach (var face in BlockFacing.ALLFACES)
            {
                var n = p.AddCopy(face);
                if (mine.Contains(n) || Math.Abs(n.Y - start.Y) > 8) continue;
                if (ba.GetBlock(n, BlockLayersAccess.Fluid).LiquidCode != "water") continue;
                mine.Add(n);
                queue.Enqueue(n);
            }
        }
        // Columns of this water are not looked at again for another spot.
        foreach (var p in mine) if (p.Y == start.Y) seen.Add(p);
        return mine.Count;
    }

    /// <summary>
    /// The fish that bite at this water with this bait: the game's fish whose climate fits and that eat the bait (the
    /// "nobait" fish without any). Empty when nothing lives there.
    /// </summary>
    List<EntityProperties> FishHere(BlockPos water, ItemStack? bait)
    {
        var ba = sapi.World.BlockAccessor;
        var climatePos = new BlockPos(water.X, (int)(sapi.World.SeaLevel * 1.09), water.Z, water.dimension);
        var climate = ba.GetClimateAt(climatePos, EnumGetClimateMode.WorldGenValues);
        if (climate == null) return new List<EntityProperties>();
        string tag = bait?.Collectible.Attributes?["baitTag"].AsString() ?? "nobait";
        var inside = ba.GetBlock(water, BlockLayersAccess.Fluid);
        var list = new List<EntityProperties>();
        foreach (var type in FishTypes())
        {
            var spawn = type.Server?.SpawnConditions;
            var cond = (ClimateSpawnCondition?)spawn?.Climate ?? (ClimateSpawnCondition?)spawn?.Runtime ?? spawn?.Worldgen;
            if (cond != null && !cond.MatchesClimate(climate)) continue;
            BaseSpawnConditions? where = (BaseSpawnConditions?)spawn?.Runtime ?? spawn?.Worldgen;
            if (where != null && !where.CanSpawnInside(inside)) continue;
            var tags = type.Attributes?["baitTags"].AsArray<string>();
            if (tags != null && tags.Length > 0 && !tags.Contains(tag)) continue;
            list.Add(type);
        }
        return list;
    }

    /// <summary>
    /// One cast: maybe a fish, as the game's fishing gives it (a juvenile more often where fish are few), using up one
    /// bait when something bites. Returns what was caught, or null.
    /// </summary>
    public ItemStack? CastLine(Colony colony, Building hut, CitizenRecord fisher, FishingSpot spot)
    {
        var bait = PackItems(fisher).FirstOrDefault(IsFishBait);
        var fish = FishHere(spot.Water, bait);
        if (fish.Count == 0) return null;
        var depletion = sapi.ModLoader.GetModSystem<ModSystemFishDepletion>();
        float taken = depletion?.GetHarvestAmount(spot.Water) ?? 0;
        float left = 1f - GameMath.Clamp(taken / (ModSystemFishDepletion.MaxHarvestablePerLocation * 0.8f) - 0.2f, 0f, 1f);
        float abundance = GameMath.Clamp(spot.PondSize / 1200f, 0.1f, 1f) * left;
        double chance = Config.FisherCatchChance * (bait != null ? 1 : Config.FisherNoBaitFactor) * (0.5 + abundance);
        if (left <= 0 || sapi.World.Rand.NextDouble() > chance) return null;

        var type = fish[sapi.World.Rand.Next(fish.Count)];
        var drop = type.Drops[0].ResolvedItemstack.Collectible;
        string age = sapi.World.Rand.NextDouble() > abundance ? "juvenile" : "adult";
        var item = sapi.World.GetItem(drop.CodeWithVariant("age", age)) ?? drop as Item;
        ItemStack caught = item != null ? new ItemStack(item) : type.Drops[0].ResolvedItemstack.Clone();
        if (bait != null) TakeFromPack(colony, fisher, IsFishBait, 1);
        depletion?.AddHarvest(spot.Water, 1);
        StoreHarvest(colony, hut, fisher, new[] { caught }, spot.Stand);
        AddStat(hut, "fish");
        return caught;
    }

    /// <summary>At a Fisher's Hut the pole and the bait stay; the fish go to the warehouse.</summary>
    ItemMatch FisherHutJunk() => s => !IsFishingPole(s) && !IsFishBait(s);

    public List<string> FisherInfo(Colony colony, Building hut)
    {
        var spots = FishingSpots(hut);
        var lines = new List<string> { spots.Count == 0 ? Lang.Get("createacolony:fisher-no-water", GatherRadius(hut)) : Lang.Get("createacolony:fisher-spots", spots.Count) };
        if (FishTypes().Count == 0) lines.Add(Lang.Get("createacolony:fisher-no-fish"));
        if (!HutStorage(colony, hut).Any(c => c.Inventory.Any(s => !s.Empty && IsFishBait(s.Itemstack)))) lines.Add(Lang.Get("createacolony:fisher-no-bait"));
        return lines;
    }
}
