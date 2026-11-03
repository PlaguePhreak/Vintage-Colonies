using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace CreateAColony.Colonies;

/// <summary>
/// The rest of a farmer's day beyond sowing and harvesting: watering dry crops from a watering can
/// (refilled at the nearest water), spreading fertilizer on worn-out soil, and picking berries from
/// berry bushes and fruit from fruit trees around the Farmer's Hut.
/// </summary>
public partial class ColonyManager
{
    /// <summary>Seconds of watering each dry farmland block gets; it soaks the block and its neighbours a little.</summary>
    const float WateringSeconds = 1.5f;
    /// <summary>How far below and above the hut a farmer looks for bushes, fruit trees and water.</summary>
    const int OrchardDepthBelow = 4;
    const int OrchardHeightAbove = 10;

    /// <summary>The fruit tree part's "already picked this season" flag, which the game keeps to itself.</summary>
    static readonly FieldInfo? FruitHarvestedField = typeof(BlockEntityFruitTreePart).GetField("harvested", BindingFlags.Instance | BindingFlags.NonPublic);

    public static bool IsWateringCan(ItemStack stack) => stack.Block is BlockWateringCan;

    public static bool IsFertilizer(ItemStack stack) => stack.Collectible.Attributes?["fertilizerProps"].Exists == true;

    // ---- Fertilizer -----------------------------------------------------------------------

    /// <summary>Spreads one fertilizer from the worker's inventory when the nutrient the crop needs runs low.</summary>
    void Fertilize(Colony colony, CitizenRecord worker, BlockEntityFarmland farmland, EnumSoilNutrient needs)
    {
        if (farmland.Nutrients[(int)needs] >= Config.FarmerFertilizeBelow) return;
        var fertilizer = TakeFromPack(colony, worker, IsFertilizer, 1);
        var props = fertilizer?.Collectible.Attributes["fertilizerProps"].AsObject<FertilizerProps>();
        if (props == null) return;

        // A player's fertilizer releases slowly; the farmer's works at once, up to a full soil.
        farmland.Nutrients[0] = Math.Min(100, farmland.Nutrients[0] + props.N);
        farmland.Nutrients[1] = Math.Min(100, farmland.Nutrients[1] + props.P);
        farmland.Nutrients[2] = Math.Min(100, farmland.Nutrients[2] + props.K);
        farmland.MarkDirty(true);
        sapi.World.PlaySoundAt(new AssetLocation("sounds/block/dirt"), farmland.Pos, 0.25);
    }

    // ---- Water ----------------------------------------------------------------------------

    /// <summary>How many seconds of water the worker's watering cans hold, and whether it has a can at all.</summary>
    public (bool HasCan, float Seconds) WaterCarried(CitizenRecord worker)
    {
        var cans = PackItems(worker).Where(IsWateringCan).ToList();
        return (cans.Count > 0, cans.Sum(c => ((BlockWateringCan)c.Block).GetRemainingWateringSeconds(c)));
    }

    /// <summary>Waters dry farmland from the watering can in the worker's hand, or else one in its inventory.</summary>
    public bool Water(Colony colony, CitizenRecord worker, BlockPos ground, ItemSlot? hand = null)
    {
        if (sapi.World.BlockAccessor.GetBlockEntity(ground) is not BlockEntityFarmland farmland) return false;
        bool inHand = hand?.Itemstack is { } held && IsWateringCan(held) && ((BlockWateringCan)held.Block).GetRemainingWateringSeconds(held) > 0;
        var items = inHand ? new List<ItemStack>() : PackItems(worker);
        var can = inHand ? hand!.Itemstack : items.FirstOrDefault(s => IsWateringCan(s) && ((BlockWateringCan)s.Block).GetRemainingWateringSeconds(s) > 0);
        if (can == null) return false;
        var block = (BlockWateringCan)can.Block;
        float left = block.GetRemainingWateringSeconds(can);
        // Pour until the soil is soaked (100%), as a player would: the game adds half the moisture range per second.
        float needed = Math.Max(WateringSeconds, (1 - farmland.MoistureLevel) * 2 + 0.1f);
        float pour = Math.Min(left, needed);
        // Irrigation Systems: the water goes further, so the can empties slower.
        block.SetRemainingWateringSeconds(can, Math.Max(0, left - pour / Math.Max(0.1f, ResearchMult(colony, "wateringCan"))));
        if (inHand) hand!.MarkDirty();
        else SavePack(colony, worker, items);

        farmland.WaterFarmland(pour);
        sapi.World.PlaySoundAt(new AssetLocation("sounds/effect/watering"), ground, 0);
        return true;
    }

    /// <summary>Fills every watering can the worker carries from the water at pos.</summary>
    public void RefillCans(Colony colony, CitizenRecord worker, BlockPos water)
    {
        var items = PackItems(worker);
        foreach (var can in items.Where(IsWateringCan))
        {
            var block = (BlockWateringCan)can.Block;
            block.SetRemainingWateringSeconds(can, block.CapacitySeconds);
        }
        SavePack(colony, worker, items);
        sapi.World.PlaySoundAt(new AssetLocation("sounds/block/water"), water, 0.35);
    }

    /// <summary>The water block nearest the hut with open air above it, where a farmer can fill a can.</summary>
    public BlockPos? FindWater(Colony colony, Building hut) => FindWaterNear(colony, hut.HutPos, Math.Max(1, Config.FarmerOrchardRadius));

    /// <summary>The surface water block nearest a spot, within radius blocks across, where a settler can fill a can.</summary>
    public BlockPos? FindWaterNear(Colony colony, BlockPos center, int radius)
    {
        var ba = sapi.World.BlockAccessor;
        var pos = center.Copy();
        BlockPos? best = null;
        double bestDist = double.MaxValue;
        for (int dx = -radius; dx <= radius; dx++)
        for (int dz = -radius; dz <= radius; dz++)
        for (int dy = -OrchardDepthBelow; dy <= OrchardHeightAbove; dy++)
        {
            pos.Set(center.X + dx, center.Y + dy, center.Z + dz);
            if (ba.GetBlock(pos, BlockLayersAccess.Fluid).LiquidCode != "water") continue;
            if (ba.GetBlock(pos.UpCopy(), BlockLayersAccess.Fluid).LiquidCode == "water") continue; // only the surface
            double dist = pos.DistanceSqTo(center.X, center.Y, center.Z);
            if (dist >= bestDist || !CanWorkAt(colony, pos)) continue;
            best = pos.Copy();
            bestDist = dist;
        }
        return best;
    }

    // ---- Berries and fruit ----------------------------------------------------------------

    /// <summary>
    /// Ripe berry bushes and fruit-laden fruit tree branches within the hut's range, unless the hut is
    /// set not to collect them. Bushes in land no player is near count as they were when last looked at.
    /// </summary>
    public IEnumerable<BlockPos> RipeFruitNear(Colony colony, Building hut)
    {
        if (hut.NoBerries) yield break;
        var ba = sapi.World.BlockAccessor;
        var scanner = Scanner(hut, "fruit", ScanFruit);
        foreach (var pos in scanner.Found.Keys.ToList())
        {
            bool ripe;
            if (ba.GetChunkAtBlockPos(pos) == null)
            {
                ripe = scanner.Found[pos];
            }
            else
            {
                var be = ba.GetBlockEntity(pos);
                if (!IsFruitBearer(be))
                {
                    scanner.Found.Remove(pos);
                    continue;
                }
                ripe = IsRipeFruit(be);
                scanner.Found[pos] = ripe;
                if (ripe && !MayCollect(hut, FruitKey(be!))) continue;
            }
            if (ripe && CanWorkAt(colony, pos)) yield return pos;
        }
    }

    /// <summary>Notes every berry bush and fruit tree branch in a chunk column, and whether it is ripe.</summary>
    void ScanFruit(int chunkX, int chunkZ, AreaScanner scanner)
    {
        var ba = sapi.World.BlockAccessor;
        for (int cy = 0; cy < ba.MapSizeY / GlobalConstants.ChunkSize; cy++)
        {
            var chunk = ba.GetChunk(chunkX, cy, chunkZ);
            if (chunk?.BlockEntities == null) continue;
            foreach (var be in chunk.BlockEntities.Values)
            {
                if (IsFruitBearer(be) && scanner.InRange(be.Pos.X, be.Pos.Z)) scanner.Found[be.Pos.Copy()] = IsRipeFruit(be);
            }
        }
    }

    static bool IsFruitBearer(BlockEntity? be) => be switch
    {
        BlockEntityFruitTreePart part => part.PartType != EnumTreePartType.Stem,
        null => false,
        _ => be.GetBehavior<BEBehaviorFruitingBush>() != null
    };

    static bool IsRipeFruit(BlockEntity? be) => be switch
    {
        BlockEntityFruitTreePart part => FruitHarvestedField != null && part.FoliageState == EnumFoliageState.Ripe && part.PartType != EnumTreePartType.Stem,
        null => false,
        _ => be.GetBehavior<BEBehaviorFruitingBush>()?.BState.Growthstate == EnumFruitingBushGrowthState.Ripe
    };

    /// <summary>Picks a ripe berry bush or fruit tree branch the way a player does; the fruit goes into the worker's inventory.</summary>
    public bool PickFruit(Colony colony, Building hut, CitizenRecord worker, BlockPos pos)
    {
        var be = sapi.World.BlockAccessor.GetBlockEntity(pos);
        if (!IsRipeFruit(be) || !MayCollect(hut, FruitKey(be!))) return false;
        var drops = new List<ItemStack>();

        if (be is BlockEntityFruitTreePart part)
        {
            part.FoliageState = EnumFoliageState.Plain;
            FruitHarvestedField!.SetValue(part, true);
            part.MarkDirty(true);
            var branchCode = AssetLocation.Create(part.Block.Attributes["branchBlock"].AsString(), part.Block.Code.Domain);
            if (sapi.World.GetBlock(branchCode) is BlockFruitTreeBranch branch && part.TreeType != null
                && branch.TypeProps.TryGetValue(part.TreeType, out var props))
            {
                foreach (var drop in props.FruitStacks)
                {
                    var stack = drop.GetNextItemStack(1);
                    if (stack != null) drops.Add(stack);
                    if (drop.LastDrop) break;
                }
            }
            if (part.harvestingSound != null) sapi.World.PlaySoundAt(part.harvestingSound, pos, 0);
        }
        else if (be!.GetBehavior<BEBehaviorFruitingBush>() is { } bush)
        {
            drops.AddRange(bush.GetRipeDrops(null) ?? Array.Empty<ItemStack>());
            bush.BState.Growthstate = EnumFruitingBushGrowthState.Mature;
            bush.BState.TransitionHoursLeft = bush.GetHoursForNextStage();
            be.MarkDirty(true);
            sapi.World.PlaySoundAt(new AssetLocation("sounds/block/plant"), pos, 0);
        }

        StoreHarvest(colony, hut, worker, drops, pos);
        return true;
    }
}
