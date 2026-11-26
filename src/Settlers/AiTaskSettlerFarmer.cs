using System.Collections.Generic;
using System.Linq;
using CreateAColony.Colonies;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using ItemMatch = System.Func<Vintagestory.API.Common.ItemStack, bool>;

namespace CreateAColony.Settlers;

/// <summary>
/// A farmer works from a Farmer's Hut. It looks after the fields marked out with Field markers and the
/// berry bushes and fruit trees around the hut, most urgent first: harvest ripe crops (and sow again),
/// pick ripe berries and fruit, pull weeds, water dry crops, sow bare farmland, till the rest. Seeds come
/// from the harvest, the hut, or the warehouse via couriers; a watering can and fertilizer from the hut.
/// Only seeds that will live until ripe in the weather ahead are sown, and none in winter (ColonyManager.Seasons.cs).
/// Before anything else it goes after animals eating its crops (ColonyManager.CropPests.cs): it leaves wild ones to
/// the colony's hunter when there is one, otherwise it kills them itself, and it chases livestock off.
/// </summary>
public class AiTaskSettlerFarmer : AiTaskSettlerWorker
{
    const float FieldReach = 2f;
    /// <summary>Fruit hangs high, so a farmer picks from a little further away.</summary>
    const float FruitReach = 4.5f;
    const int FertilizerStock = 16;
    /// <summary>Berries within this many blocks of the hut are picked as part of the daily round.</summary>
    const float NearFruit = 32;

    FieldWork work;
    FieldRecord? field;
    /// <summary>Soil better than this grade is kept for laying on the fields; -1 when no bare spot needs it.</summary>
    int keepSoilAbove = -1;
    long pestId;
    long chaseStartMs;
    readonly Dictionary<long, long> letGo = new();

    public AiTaskSettlerFarmer(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig) : base(entity, taskConfig, aiConfig)
    {
        workRange = FieldReach;
    }

    protected override string Job => Jobs.Farmer;
    protected override string HutType => BuildingTypes.Farmer;
    protected override EnumTool Tool => EnumTool.Hoe;
    protected override ItemMatch Stock => ColonyManager.IsCropSeed;
    protected override float WorkSeconds => work == FieldWork.Chase
        ? manager!.Config.HunterSecondsPerHit / (Colony is { } c && Citizen is { } me ? manager.SwingFactor(c, me) : 1f)
        : manager!.Config.FarmerSecondsPerBlock;
    /// <summary>The hoe for tilling, and to hit a crop eater with.</summary>
    protected override bool WorkNeedsTool => work is FieldWork.Till or FieldWork.Chase or FieldWork.Water or FieldWork.Refill;
    /// <summary>The watering can while watering or filling it, as a player holds it; the hoe otherwise.</summary>
    protected override bool WantsInHand(ItemStack? stack) => work switch
    {
        FieldWork.Water => stack != null && ColonyManager.IsWateringCan(stack) && ((BlockWateringCan)stack.Block).GetRemainingWateringSeconds(stack) > 0,
        FieldWork.Refill => stack != null && ColonyManager.IsWateringCan(stack),
        _ => base.WantsInHand(stack)
    };
    protected override bool AlsoKeeps(ItemStack stack) => ColonyManager.IsWateringCan(stack) || ColonyManager.IsFertilizer(stack)
        || (keepSoilAbove >= 0 && manager!.SoilRank(stack) > keepSoilAbove);

    protected override string WorkAnimation(ItemSlot? hand) => work switch
    {
        FieldWork.Chase => hand?.Itemstack?.Collectible.GetHeldTpHitAnimation(hand, entity) ?? "breakhand",
        FieldWork.Till => hand?.Itemstack?.Collectible.GetHeldTpUseAnimation(hand, entity) ?? "interactstatic",
        // The game's own watering animation for the can in hand.
        FieldWork.Water or FieldWork.Refill when hand?.Itemstack is { } can && ColonyManager.IsWateringCan(can) => can.Collectible.GetHeldTpUseAnimation(hand, entity) ?? "interactstatic",
        FieldWork.Sow or FieldWork.Water or FieldWork.Refill or FieldWork.Upgrade => SettlerAnimations.PlayerAnimations.Contains("placeblock") ? "placeblock" : "interactstatic",
        _ => "breakhand"
    };

    /// <summary>Fruit up in a tree is picked from the ground below it.</summary>
    protected override Vec3d WalkGoal()
    {
        // A crop eater moves: walk to where it is now.
        if (work == FieldWork.Chase) return entity.World.GetEntityById(pestId)?.Pos.XYZ ?? base.WalkGoal();
        if (work != FieldWork.Pick) return base.WalkGoal();
        var ba = entity.World.BlockAccessor;
        var pos = target.Copy();
        for (int i = 0; i < 8 && ba.GetBlock(pos.DownCopy()).CollisionBoxes == null; i++) pos.Down();
        return pos.ToVec3d().Add(0.5, 0, 0.5);
    }

    protected override bool CloseEnough() => work == FieldWork.Chase && entity.World.GetEntityById(pestId) is { } e
        ? entity.Pos.DistanceTo(e.Pos.XYZ) <= workRange + 0.5
        : base.CloseEnough();

    bool LetGo(long id)
    {
        if (!letGo.TryGetValue(id, out long until)) return false;
        if (entity.World.ElapsedMilliseconds < until) return true;
        letGo.Remove(id);
        return false;
    }

    /// <summary>Goes after an animal eating the crops, if there is one this farmer should deal with.</summary>
    bool PlanChase(Colony colony, CitizenRecord citizen, Building hut)
    {
        var m = manager!;
        long now = entity.World.ElapsedMilliseconds;
        var pest = pestId != 0 ? entity.World.GetEntityById(pestId) : null;
        if (pest == null || !m.FarmerShouldChase(colony, citizen, hut, pest) || LetGo(pestId))
        {
            pest = m.CropPestForFarmer(colony, citizen, hut, entity.Pos.XYZ, LetGo);
            if (pest == null)
            {
                pestId = 0;
                return false;
            }
            pestId = pest.EntityId;
            chaseStartMs = now;
        }
        if (now - chaseStartMs > m.Config.HunterChaseSeconds * 1000)
        {
            // It keeps running: let it be for a while.
            letGo[pestId] = now + 120_000;
            pestId = 0;
            return false;
        }
        work = FieldWork.Chase;
        field = null;
        workRange = 2.2f;
        SetStatus("chasing-pest");
        return GoWork(pest.Pos.AsBlockPos);
    }

    protected override bool PlanWork(Colony colony, CitizenRecord citizen, Building hut, List<BlockEntityContainer> storage)
    {
        var m = manager!;
        if (PlanChase(colony, citizen, hut)) return true;

        // Pick up a watering can and fertilizer from the hut once; the farmer keeps them.
        if (!m.PackItems(citizen).Any(ColonyManager.IsWateringCan) && Hand?.Itemstack is not { Block: BlockWateringCan } && Supplies(storage, ColonyManager.IsWateringCan) is { } canChest)
        {
            return FetchSupplies(canChest, ColonyManager.IsWateringCan, 1);
        }
        if (m.PackCount(citizen, ColonyManager.IsFertilizer) == 0 && Supplies(storage, ColonyManager.IsFertilizer) is { } fertChest)
        {
            return FetchSupplies(fertChest, ColonyManager.IsFertilizer, FertilizerStock);
        }

        var fields = m.FieldsFor(colony, citizen, hut);
        var (hasCan, water) = m.FireWater(citizen, Hand);
        var here = entity.Pos.XYZ;
        (FieldWork Work, FieldRecord? Field, BlockPos Pos)? best = null;
        FieldRecord? shortOfSeeds = null;
        BlockPos? shortAt = null;
        string? noSowing = null;
        bool thirsty = false;
        // Better soil carried, and the best in the hut storage, for swapping out poor soil.
        bool upgrades = m.Config.FarmerUpgradesSoil;
        int carriedSoil = upgrades ? m.BestSoilRank(m.PackItems(citizen)) : -1;
        int storedSoil = upgrades ? m.BestSoilRank(storage.SelectMany(c => c.Inventory).Where(s => !s.Empty).Select(s => s.Itemstack)) : -1;
        int worstSoil = int.MaxValue, toFetch = 0, poorSpots = 0;

        void Consider(FieldWork w, FieldRecord? f, BlockPos pos)
        {
            if (best is { } b && (b.Work < w || (b.Work == w && b.Pos.DistanceSqTo(here.X, here.Y, here.Z) <= pos.DistanceSqTo(here.X, here.Y, here.Z)))) return;
            best = (w, f, pos);
        }

        foreach (var f in fields)
        {
            // Only seeds that would live until ripe here count; in winter, or when it is too cold or hot for every crop, none.
            var spot = m.FieldGround(f).FirstOrDefault() ?? f.Pos;
            string? why = m.NoSowingReason(spot, m.SeedMatcher(f));
            bool hasSeed = why == null && m.PackCount(citizen, m.SowableNow(spot, m.SeedMatcher(f))) > 0;
            foreach (var ground in m.FieldGround(f))
            {
                var w = m.WorkAt(colony, ground);
                if (w == FieldWork.None || IsUnreachable(ground)) continue;
                // Bare ground of a poor grade: lay better soil first, if there is any (even in winter).
                if (upgrades && w is FieldWork.Sow or FieldWork.Till && m.GroundSoilRank(ground) is var rank and >= 0)
                {
                    worstSoil = System.Math.Min(worstSoil, rank);
                    if (rank < carriedSoil)
                    {
                        Consider(FieldWork.Upgrade, f, ground);
                        continue;
                    }
                    if (rank < storedSoil) toFetch++;
                    if (rank < ColonyManager.TopSoilRank) poorSpots++;
                }
                if (w == FieldWork.Sow && why != null)
                {
                    noSowing ??= why;
                    continue;
                }
                if (w == FieldWork.Sow && !hasSeed)
                {
                    shortOfSeeds ??= f;
                    shortAt ??= ground;
                    continue;
                }
                if (w == FieldWork.Water)
                {
                    if (!hasCan) continue;
                    if (water <= 0)
                    {
                        thirsty = true;
                        continue;
                    }
                }
                Consider(w, f, ground);
            }
        }
        // Berries close to the hut come first; far ones wait until the fields have nothing to do.
        var farFruit = new List<BlockPos>();
        foreach (var fruit in m.RipeFruitNear(colony, hut))
        {
            if (IsUnreachable(fruit)) continue;
            if (fruit.DistanceTo(hut.HutPos) <= NearFruit) Consider(FieldWork.Pick, null, fruit);
            else farFruit.Add(fruit);
        }

        keepSoilAbove = worstSoil == int.MaxValue ? -1 : worstSoil;
        // Better soil in the hut for poor spots: pick it up before sowing. Only in the warehouse: ask couriers.
        if (toFetch > 0 && (best == null || best.Value.Work > FieldWork.Upgrade) && !m.PackFull(citizen))
        {
            ItemMatch better = s => m.SoilRank(s) == storedSoil;
            if (Supplies(storage, better) is { } soilChest) return FetchSupplies(soilChest, better, System.Math.Min(toFetch, 16));
        }
        if (upgrades && poorSpots > 0 && storedSoil <= worstSoil && carriedSoil <= worstSoil) m.EnsureSoilRequest(colony, hut, worstSoil, poorSpots);

        // Out of seeds for bare farmland: pick some up at the hut, or ask couriers to bring them.
        if (shortOfSeeds != null)
        {
            var match = m.SowableNow(shortAt!, m.SeedMatcher(shortOfSeeds));
            if (Supplies(storage, match) is { } chest) return FetchSupplies(chest, match, m.SeedReserve(Job));
            m.EnsureSeedRequest(colony, hut, shortOfSeeds, shortAt);
        }

        // The watering can is empty and crops are dry: fill it at the nearest water first.
        bool noWater = false;
        if (thirsty && (best == null || best.Value.Work > FieldWork.Water))
        {
            if (m.FindWater(colony, hut) is { } waterPos && !IsUnreachable(waterPos)) best = (FieldWork.Refill, null, waterPos);
            // Dry crops and nowhere to fill the can: say so (0.202.0).
            else noWater = true;
        }

        if (best == null && farFruit.Count > 0)
        {
            best = (FieldWork.Pick, null, farFruit.OrderBy(p => p.DistanceSqTo(here.X, here.Y, here.Z)).First());
        }

        if (best == null)
        {
            SetStatus(fields.Count == 0 ? "no-fields" : noWater ? "farmer-no-water" : shortOfSeeds != null ? "needs-seeds" : noSowing != null ? "no-sowing-" + noSowing : "growing");
            return false;
        }
        work = best.Value.Work;
        field = best.Value.Field;
        workRange = work is FieldWork.Pick or FieldWork.Refill ? FruitReach : FieldReach;
        SetStatus(work == FieldWork.Pick ? "picking" : noWater ? "farmer-no-water" : "farming");
        return GoWork(best.Value.Pos);
    }

    static BlockPos? Supplies(List<BlockEntityContainer> storage, ItemMatch match) =>
        storage.FirstOrDefault(c => c.Inventory.Any(s => !s.Empty && match(s.Itemstack)))?.Pos;

    protected override void DoWork(Colony colony, CitizenRecord citizen, Building hut)
    {
        var m = manager!;
        switch (work)
        {
            case FieldWork.Chase:
                if (entity.World.GetEntityById(pestId) is not { } pest || !pest.Alive) return;
                // Too far: it ran off while the farmer swung. Plan walks after it again.
                if (entity.Pos.DistanceTo(pest.Pos.XYZ) > workRange + 1) return;
                entity.Pos.Yaw = (float)System.Math.Atan2(pest.Pos.X - entity.Pos.X, pest.Pos.Z - entity.Pos.Z);
                m.FarmerStrike(colony, citizen, hut, entity, pest);
                SyncHand(); // a hoe that just broke must vanish from the model too
                return;
            case FieldWork.Pick:
                m.PickFruit(colony, hut, citizen, target);
                return;
            case FieldWork.Upgrade:
                if (field == null || !colony.Fields.Contains(field) || m.WorkAt(colony, target) is not (FieldWork.Sow or FieldWork.Till)) return;
                m.UpgradeSoil(colony, hut, citizen, target);
                return;
            case FieldWork.Refill:
                m.RefillCansAndHand(colony, citizen, Hand, target);
                return;
        }

        if (field == null || !colony.Fields.Contains(field)) return;
        if (m.WorkAt(colony, target) != work) return; // someone got here first
        switch (work)
        {
            case FieldWork.Harvest:
                string? crop = m.Harvest(colony, hut, citizen, target);
                // Carrying better soil: lay it before sowing again.
                if (m.Config.FarmerUpgradesSoil) m.UpgradeSoil(colony, hut, citizen, target);
                m.Sow(colony, citizen, entity, target, field, crop);
                break;
            case FieldWork.Weed:
                m.Weed(colony, hut, citizen, target);
                break;
            case FieldWork.Water:
                m.Water(colony, citizen, target, Hand);
                break;
            case FieldWork.Sow:
                m.Sow(colony, citizen, entity, target, field, null);
                break;
            case FieldWork.Till:
                var hoe = Hand;
                if (hoe == null || !HoldsTool) return;
                m.Till(colony, hut, citizen, entity, target, hoe);
                SyncHand(); // a hoe that just broke must vanish from the model too
                break;
        }
    }

    protected override string DescribeWork() => Lang.Get("createacolony:activity-farmer-" + work.ToString().ToLowerInvariant());
}
