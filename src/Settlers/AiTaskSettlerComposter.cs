using System.Collections.Generic;
using System.Linq;
using CreateAColony.Colonies;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;
using ItemMatch = System.Func<Vintagestory.API.Common.ItemStack, bool>;

namespace CreateAColony.Settlers;

/// <summary>
/// A composter works from the Composter's Hut (ColonyManager.Composting.cs). In order: it takes fertilizer it carries to
/// a Farmer's Hut short of it, puts rot and dry grass on the compost heap, turns poor soil into compost when the heap is
/// ready, grinds bones into bone meal at the quern, and goes round the warehouse and the huts for rot and bones.
/// </summary>
public class AiTaskSettlerComposter : AiTaskSettlerWorker
{
    enum Work { None, Deliver, Feed, Compost, Grind }

    Work work;

    public AiTaskSettlerComposter(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig) : base(entity, taskConfig, aiConfig) { }

    protected override string Job => Jobs.Composter;
    protected override string HutType => BuildingTypes.Composter;
    protected override EnumTool Tool => EnumTool.Shovel; // unused: a composter needs no tool
    protected override bool NeedsTool => false;
    protected override bool WorkNeedsTool => false;
    protected override ItemMatch Stock => _ => false;
    protected override float WorkSeconds => manager!.Config.ComposterSecondsPerTask;

    /// <summary>It carries rot, bones, dry grass, poor soil and fertilizer until each goes where it belongs.</summary>
    protected override bool AlsoKeeps(ItemStack stack) => ColonyManager.IsRot(stack) || ColonyManager.IsGrindableToFertilizer(stack)
        || ColonyManager.IsDryGrass(stack) || ColonyManager.IsPoorSoil(stack) || ColonyManager.IsFertilizer(stack);

    protected override string WorkAnimation(ItemSlot? hand) => "interactstatic";

    protected override bool PlanWork(Colony colony, CitizenRecord citizen, Building hut, List<BlockEntityContainer> storage)
    {
        var m = manager!;
        work = Work.None;
        workRange = 2.5f;
        var own = storage.ToHashSet();

        // Fertilizer to the farms (rot the heap still wants stays).
        bool spareRot = m.PackCount(citizen, ColonyManager.IsRot) > System.Math.Max(0, m.Config.CompostHeapRot - hut.CompostRot);
        if ((spareRot || m.PackItems(citizen).Any(s => ColonyManager.IsFertilizer(s) && !ColonyManager.IsRot(s))) && m.FarmWantingFertilizer(colony, hut) is { } farm)
        {
            return Go(Work.Deliver, farm.Pos, "composter-delivering");
        }
        // Rot and dry grass onto the heap.
        if ((m.PackCount(citizen, ColonyManager.IsRot) > 0 && m.HeapWants(hut, true)) || (m.PackCount(citizen, ColonyManager.IsDryGrass) > 0 && m.HeapWants(hut, false)))
        {
            return Go(Work.Feed, hut.HutPos, "composter-feeding");
        }
        // A ready heap turns poor soil into compost.
        if (m.HeapReady(hut))
        {
            if (m.PackCount(citizen, ColonyManager.IsPoorSoil) > 0) return Go(Work.Compost, hut.HutPos, "composter-composting");
            var soil = storage.FirstOrDefault(c => c.Inventory.Any(s => !s.Empty && ColonyManager.IsPoorSoil(s.Itemstack)));
            if (soil != null && !m.PackFull(citizen))
            {
                SetStatus("composter-fetching");
                return FetchSupplies(soil.Pos, ColonyManager.IsPoorSoil, 4);
            }
            if (m.CodeInWarehouse(colony, ColonyManager.IsPoorSoil) is { } code) m.EnsureCraftRequest(colony, hut, code, 4);
        }
        // Bones into bone meal.
        var quern = m.ComposterQuern(hut);
        if (quern != null && m.PackCount(citizen, ColonyManager.IsGrindableToFertilizer) > 0) return Go(Work.Grind, quern.Pos, "composter-grinding");

        if (m.PackFull(citizen))
        {
            SetStatus("composter-pack-full");
            return false;
        }
        // Dry grass for the heap from the hut storage (couriers bring it).
        if (m.HeapWants(hut, false))
        {
            var grass = storage.FirstOrDefault(c => c.Inventory.Any(s => !s.Empty && ColonyManager.IsDryGrass(s.Itemstack)));
            if (grass != null)
            {
                SetStatus("composter-fetching");
                return FetchSupplies(grass.Pos, ColonyManager.IsDryGrass, m.Config.CompostHeapGrass - hut.CompostGrass);
            }
            if (m.CodeInWarehouse(colony, ColonyManager.IsDryGrass) is { } code) m.EnsureCraftRequest(colony, hut, code, m.Config.CompostGrassPerSoil * 2);
        }
        // Round the colony for rot, then bones (bones only with a quern to grind them).
        ItemMatch pickup = s => ColonyManager.IsRot(s) || (quern != null && ColonyManager.IsBone(s) && ColonyManager.IsGrindableToFertilizer(s));
        var chest = m.ComposterPickup(colony, hut, pickup, c => own.Contains(c) || IsUnreachable(c.Pos));
        if (chest != null)
        {
            SetStatus("composter-collecting");
            return FetchSupplies(chest.Pos, pickup, 64);
        }
        SetStatus(m.HeapWants(hut, true) ? "composter-no-rot" : "composter-waiting");
        return false;
    }

    bool Go(Work next, Vintagestory.API.MathTools.BlockPos at, string status)
    {
        work = next;
        SetStatus(status);
        return GoWork(at);
    }

    protected override void DoWork(Colony colony, CitizenRecord citizen, Building hut)
    {
        var m = manager!;
        switch (work)
        {
            case Work.Deliver:
                if (entity.World.BlockAccessor.GetBlockEntity(target) is BlockEntityContainer chest && m.DeliverFertilizer(colony, citizen, chest, hut) == 0) MarkUnreachable(target);
                break;
            case Work.Feed:
                m.FeedHeap(colony, hut, citizen);
                break;
            case Work.Compost:
                m.MakeCompost(colony, hut, citizen);
                break;
            case Work.Grind:
                m.GrindFertilizer(colony, hut, citizen);
                entity.World.PlaySoundAt(new AssetLocation("game:sounds/block/quern"), target.X + 0.5, target.Y + 0.5, target.Z + 0.5, null, true, 16);
                break;
        }
        work = Work.None;
    }

    protected override string DescribeWork() => work switch
    {
        Work.Deliver => Lang.Get("createacolony:activity-composter-deliver"),
        Work.Feed or Work.Compost => Lang.Get("createacolony:activity-composter-heap"),
        Work.Grind => Lang.Get("createacolony:activity-composter-grind"),
        _ => Lang.Get("createacolony:activity-composter-idle")
    };
}
