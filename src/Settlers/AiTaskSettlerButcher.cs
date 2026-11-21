using System.Collections.Generic;
using System.Linq;
using CreateAColony.Colonies;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using ItemMatch = System.Func<Vintagestory.API.Common.ItemStack, bool>;

namespace CreateAColony.Settlers;

/// <summary>
/// A butcher works from a Butcher's Hut (ColonyManager.Butchery.cs). It cuts up the carcasses hunters bring to the
/// hut with its knife, at the hut block (or at a Butchering mod hook or table there). When none are waiting it
/// fetches carcasses left at a Hunter's Hut or in the warehouse, and picks up dead animals lying on the colony's
/// land or culled by herders, within its work area. The meat, hides, fat and bones go in the hut storage, where
/// couriers pick them up.
/// </summary>
public class AiTaskSettlerButcher : AiTaskSettlerWorker
{
    enum Work { None, Cut, PickUp }

    Work work;
    long animalId;
    bool atStation;

    public AiTaskSettlerButcher(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig) : base(entity, taskConfig, aiConfig) { }

    protected override string Job => Jobs.Butcher;
    protected override string HutType => BuildingTypes.Butcher;
    protected override EnumTool Tool => EnumTool.Knife;
    protected override ItemMatch Stock => _ => false;
    protected override bool AlsoKeeps(ItemStack stack) => ColonyManager.IsCarcass(stack);
    protected override bool WorkNeedsTool => work == Work.Cut;

    ItemStack? CarriedCarcass(CitizenRecord citizen) => manager!.PackItems(citizen).FirstOrDefault(ColonyManager.IsCarcass);

    protected override float WorkSeconds => work == Work.PickUp
        ? manager!.Config.CarcassPickUpSeconds
        : Citizen is { } me ? manager!.ButcherSeconds(CarriedCarcass(me)) : 10f;

    protected override bool PlanWork(Colony colony, CitizenRecord citizen, Building hut, List<BlockEntityContainer> storage)
    {
        var m = manager!;
        work = Work.None;
        int load = System.Math.Max(1, m.Config.CarcassLoad);
        int carried = m.PackCount(citizen, ColonyManager.IsCarcass);

        // A carcass in hand: cut it up at the hut (or a Butchering mod hook or table there).
        if (carried > 0 && HoldsOrCarriesKnife(citizen))
        {
            var station = m.ButcheringStation(colony, hut);
            atStation = station != null;
            work = Work.Cut;
            workRange = 2.2f;
            SetStatus("butcher-cutting");
            return GoWork(station ?? hut.HutPos);
        }
        if (m.PackFull(citizen)) return false;

        // Carcasses waiting in the hut storage.
        if (storage.FirstOrDefault(c => c.Inventory.Any(s => !s.Empty && ColonyManager.IsCarcass(s.Itemstack))) is { } chest)
        {
            SetStatus("butcher-fetching");
            return FetchSupplies(chest.Pos, ColonyManager.IsCarcass, 1);
        }

        // Dead animals lying around the colony, or culled by its herders.
        if (carried < load && m.CarcassForButcher(colony, hut, entity, IsUnreachable) is { } animal)
        {
            work = Work.PickUp;
            animalId = animal.EntityId;
            m.ClaimCarcass(animal, entity);
            workRange = 2f;
            SetStatus("butcher-picking-up");
            return GoWork(animal.Pos.AsBlockPos);
        }

        // Carcasses left at a Hunter's Hut or in the warehouse.
        if (m.CarcassChest(colony, hut, entity.Pos.XYZ) is { } away && !IsUnreachable(away.Pos))
        {
            SetStatus("butcher-fetching");
            return FetchSupplies(away.Pos, ColonyManager.IsCarcass, load);
        }

        SetStatus("butcher-nothing");
        return false;
    }

    bool HoldsOrCarriesKnife(CitizenRecord citizen) => ColonyManager.IsKnife(Hand?.Itemstack) || manager!.PackCount(citizen, s => ColonyManager.IsKnife(s)) > 0;

    protected override Vec3d WalkGoal() => work == Work.PickUp && entity.World.GetEntityById(animalId) is { } e ? e.Pos.XYZ : base.WalkGoal();

    protected override bool CloseEnough() => work == Work.PickUp && entity.World.GetEntityById(animalId) is { } e
        ? entity.Pos.DistanceTo(e.Pos.XYZ) <= workRange + 0.5
        : base.CloseEnough();

    protected override string WorkAnimation(ItemSlot? hand) => work == Work.Cut ? "knifecut" : "interactstatic";

    protected override void DoWork(Colony colony, CitizenRecord citizen, Building hut)
    {
        var m = manager!;
        if (work == Work.PickUp)
        {
            if (entity.World.GetEntityById(animalId) is not { } animal) return;
            if (m.PickUpCarcass(colony, entity, animal) is { } whole)
            {
                if (m.AddToPack(colony, citizen, whole, false) == 0) entity.World.SpawnItemEntity(whole, entity.Pos.XYZ.Add(0, 0.5, 0));
            }
            return;
        }
        if (work != Work.Cut || Hand is not { } hand || !ColonyManager.IsKnife(hand.Itemstack)) return;
        if (m.TakeFromPack(colony, citizen, ColonyManager.IsCarcass, 1) is not { } carcass) return;

        var cuts = m.CutUp(colony, citizen, carcass, atStation);
        m.CollectBlood(colony, hut, carcass);
        m.StoreCuts(colony, hut, cuts);
        for (int i = 0; i < System.Math.Max(1, m.Config.ButcherKnifeWear); i++) m.WearTool(entity, hand);
        SyncHand();
        entity.World.PlaySoundAt(new AssetLocation("sounds/player/scrape"), entity, null, true, 16);
        string animalCode = carcass.Attributes.GetString(ColonyManager.CarcassAnimalAttr) ?? "unknown";
        m.AddStat(hut, "butchered:" + new AssetLocation(animalCode).Path);
    }

    protected override string DescribeWork() => Lang.Get(work switch
    {
        Work.PickUp => "createacolony:activity-butcher-pick-up",
        Work.Cut => "createacolony:activity-butcher-cut",
        _ => "createacolony:activity-butcher-idle"
    });
}
