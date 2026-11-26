using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using ItemMatch = System.Func<Vintagestory.API.Common.ItemStack, bool>;

namespace CreateAColony.Colonies;

/// <summary>
/// Berry bush cuttings (Aaron, 0.151.0). A gatherer that carries a knife takes a cutting from a berry bush the way a
/// player does (sneak + right-click with a knife): one per bush a year, without harming the bush. It keeps config
/// GathererCuttingsKeep of each kind in the hut and warehouse, so players can plant new bushes.
/// </summary>
public partial class ColonyManager
{
    /// <summary>The cutting a berry bush gives (its "cuttingBlockCode"), or null when it gives none.</summary>
    Block? CuttingBlock(BlockEntity? be)
    {
        if (be?.GetBehavior<BEBehaviorFruitingBush>() == null) return null;
        string? code = be.Block.Attributes?["cuttingBlockCode"].AsString();
        return string.IsNullOrEmpty(code) ? null : sapi.World.GetBlock(AssetLocation.Create(code, be.Block.Code.Domain));
    }

    /// <summary>True when a cutting can be taken from this bush now: none was taken in the last year (as the game counts it).</summary>
    bool CanTakeCutting(BlockEntity? be)
    {
        if (!Config.GathererTakesCuttings || CuttingBlock(be) == null || be!.GetBehavior<BEBehaviorFruitingBush>() is not { } bush) return false;
        double years = (sapi.World.Calendar.TotalDays - bush.BState.LastCuttingTakenTotalDays) / sapi.World.Calendar.DaysPerYear;
        return years >= (YearsBetweenCuttingsField?.GetValue(bush) as double? ?? 1.0);
    }

    static readonly System.Reflection.FieldInfo? YearsBetweenCuttingsField =
        typeof(BEBehaviorFruitingBush).GetField("yearsBetweenCuttings", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);

    /// <summary>True when the colony has config GathererCuttingsKeep of this bush's cutting already (warehouse and hut). Checked every 30 seconds.</summary>
    bool CuttingsStocked(Colony colony, Building hut, Block cutting)
    {
        long now = sapi.World.ElapsedMilliseconds;
        string key = "cutting-" + cutting.Code;
        if (herbStock.TryGetValue((hut.BuildingId, key), out var cached) && now - cached.Ms < 30_000) return cached.Stocked;
        ItemMatch same = s => s.Collectible.Code == cutting.Code;
        bool stocked = CountItems(WarehouseContainers(colony), same) + CountItems(HutStorage(colony, hut), same) >= Config.GathererCuttingsKeep;
        herbStock[(hut.BuildingId, key)] = (now, stocked);
        return stocked;
    }

    /// <summary>Takes a cutting from the bush into the gatherer's inventory, as BEBehaviorFruitingBush.SetHarvested does for a player.</summary>
    bool TakeCutting(Colony colony, Building hut, CitizenRecord worker, BlockPos pos)
    {
        var be = sapi.World.BlockAccessor.GetBlockEntity(pos);
        if (!CanTakeCutting(be) || CuttingBlock(be) is not { } cutting || be!.GetBehavior<BEBehaviorFruitingBush>() is not { } bush) return false;
        var stack = new ItemStack(cutting);
        stack.Attributes.SetString("traits", string.Join(",", bush.BState.Traits ?? Array.Empty<string>()));
        bush.BState.LastCuttingTakenTotalDays = sapi.World.Calendar.TotalDays;
        be.MarkDirty(true);
        herbStock.Remove((hut.BuildingId, "cutting-" + cutting.Code));
        StoreHarvest(colony, hut, worker, new[] { stack }, pos);
        return true;
    }

    HashSet<AssetLocation>? cuttingCodes;

    /// <summary>True for a berry bush cutting (any block a bush names as its "cuttingBlockCode").</summary>
    public bool IsCutting(ItemStack? stack)
    {
        if (stack?.Block == null) return false;
        if (cuttingCodes == null)
        {
            cuttingCodes = new HashSet<AssetLocation>();
            foreach (var block in sapi.World.Blocks)
            {
                if (block?.Code == null || block.Attributes?["cuttingBlockCode"].AsString() is not { Length: > 0 } code) continue;
                cuttingCodes.Add(AssetLocation.Create(code, block.Code.Domain));
            }
        }
        return cuttingCodes.Contains(stack.Block.Code);
    }

    /// <summary>True while the Gatherer's Hut's berry garden has room for another bush.</summary>
    public bool GardenHasRoom(Building hut) => Config.BerryGardenSize > 0 && hut.StatTotals.GetValueOrDefault("planted") < Config.BerryGardenSize;

    /// <summary>
    /// A spot near the Gatherer's Hut to plant a cutting: soil with open air (or grass) above, clear of buildings and
    /// a block away from other bushes. Null when there is none.
    /// </summary>
    public BlockPos? GardenSpot(Colony colony, Building hut)
    {
        var ba = sapi.World.BlockAccessor;
        int r = Math.Max(5, Config.BerryGardenRadius);
        BlockPos? best = null;
        double bestDist = double.MaxValue;
        for (int dx = -r; dx <= r; dx += 2)
        for (int dz = -r; dz <= r; dz += 2)
        {
            double d = dx * dx + dz * dz;
            if (d > r * r || d < 9 || d >= bestDist) continue;
            var ground = new BlockPos(hut.HutPos.X + dx, hut.HutPos.Y, hut.HutPos.Z + dz, hut.HutPos.dimension);
            if (ba.GetChunkAtBlockPos(ground) == null) continue;
            ground.Y = ba.GetTerrainMapheightAt(ground);
            if (Math.Abs(ground.Y - hut.HutPos.Y) > 6) continue;
            var soil = ba.GetBlock(ground);
            if (soil.Code == null || !(soil.Code.Path.StartsWith("soil-") || soil.Code.Path.StartsWith("forestfloor"))) continue;
            var above = ground.UpCopy();
            var on = ba.GetBlock(above);
            if (on.Id != 0 && on.Replaceable < 6000) continue;
            if (ba.GetBlock(above, BlockLayersAccess.Fluid).Id != 0) continue;
            if (NearBuilding(colony, above) || ZoneAt(colony, above) != null || !CanWorkAt(colony, above)) continue;
            bool crowded = false;
            for (int ox = -1; ox <= 1 && !crowded; ox++)
            for (int oz = -1; oz <= 1 && !crowded; oz++)
            {
                if (ox == 0 && oz == 0) continue;
                var near = ba.GetBlock(above.AddCopy(ox, 0, oz));
                crowded = near.Id != 0 && near.Replaceable < 6000;
            }
            if (crowded) continue;
            best = above;
            bestDist = d;
        }
        return best;
    }

    /// <summary>Plants a cutting from the gatherer's pack at the spot. False when it had none or the spot is taken.</summary>
    public bool PlantCutting(Colony colony, Building hut, CitizenRecord worker, BlockPos spot)
    {
        var ba = sapi.World.BlockAccessor;
        var on = ba.GetBlock(spot);
        if (on.Id != 0 && on.Replaceable < 6000) return false;
        if (TakeFromPack(colony, worker, IsCutting, 1) is not { Block: { } cutting } stack) return false;
        ba.SetBlock(cutting.Id, spot, stack);
        ba.MarkBlockDirty(spot);
        AddStat(hut, "planted");
        MarkDirty(colony);
        return true;
    }
}
