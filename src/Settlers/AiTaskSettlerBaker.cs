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
/// A baker works from the Bakery (ColonyManager.Baking.cs). In order: it takes baked bread off the fire pit, keeps the
/// fire going under dough that bakes, and, while the colony has less bread than BakerBreadKeep, puts dough on the fire,
/// mixes flour into dough at the Bakery, or grinds grain into flour at the quern. Grain, flour and dough come from the
/// Bakery's storage, which couriers fill from the warehouse; firewood too.
/// </summary>
public class AiTaskSettlerBaker : AiTaskSettlerWorker
{
    enum Work { None, Empty, Fuel, Load, Mix, Grind }

    Work work;

    public AiTaskSettlerBaker(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig) : base(entity, taskConfig, aiConfig) { }

    protected override string Job => Jobs.Baker;
    protected override string HutType => BuildingTypes.Bakery;
    protected override EnumTool Tool => EnumTool.Knife; // unused: a baker needs no tool
    protected override bool NeedsTool => false;
    protected override bool WorkNeedsTool => false;
    protected override ItemMatch Stock => ColonyManager.IsDough;
    protected override float WorkSeconds => manager!.Config.BakerSecondsPerStep;

    /// <summary>It carries firewood, dough, flour and grain for its next bake.</summary>
    protected override bool AlsoKeeps(ItemStack stack) => ColonyManager.IsFirewood(stack) || ColonyManager.IsDough(stack)
        || ColonyManager.IsFlour(stack) || ColonyManager.IsGrindableToFlour(stack);

    protected override string WorkAnimation(ItemSlot? hand) => "interactstatic";

    protected override bool PlanWork(Colony colony, CitizenRecord citizen, Building hut, List<BlockEntityContainer> storage)
    {
        var m = manager!;
        work = Work.None;
        var pit = m.CookFirepit(colony, hut);
        if (pit == null)
        {
            SetStatus("baker-no-firepit");
            return false;
        }
        // Baked bread comes out first, before it chars.
        if (!pit.outputSlot.Empty && !m.PackFull(citizen)) return Go(Work.Empty, pit.Pos, "baker-taking-out");
        if (!pit.inputSlot.Empty)
        {
            // Something a player put there is theirs.
            if (!ColonyManager.IsDough(pit.inputSlot.Itemstack))
            {
                SetStatus("baker-firepit-busy");
                return false;
            }
            if (!pit.IsBurning)
            {
                if (!pit.fuelSlot.Empty || m.PackCount(citizen, ColonyManager.IsFirewood) > 0) return Go(Work.Fuel, pit.Pos, "baker-lighting");
                return FetchFirewood(colony, hut, storage);
            }
            SetStatus("baker-baking");
            return false;
        }

        if (m.BreadStock(colony, hut, citizen) >= m.Config.BakerBreadKeep)
        {
            SetStatus("baker-stocked");
            return false;
        }

        // Dough on hand: on the fire with it.
        if (m.PackItems(citizen).Any(s => ColonyManager.IsDough(s) && m.BreadFor(s) != null))
        {
            if (pit.IsBurning || !pit.fuelSlot.Empty || m.PackCount(citizen, ColonyManager.IsFirewood) > 0) return Go(Work.Load, pit.Pos, "baker-loading");
            return FetchFirewood(colony, hut, storage);
        }
        // Flour on hand: mix it into dough at the Bakery.
        if (m.PackItems(citizen).FirstOrDefault(ColonyManager.IsFlour) is { } flour && m.DoughRecipe(flour) is { } recipe
            && m.PackCount(citizen, s => m.SameItem(s, flour)) >= recipe.Flour)
        {
            return Go(Work.Mix, hut.HutPos, "baker-mixing");
        }
        // Grain on hand: grind it at the quern.
        var quern = m.BakeryQuern(hut);
        if (m.PackCount(citizen, ColonyManager.IsGrindableToFlour) > 0 && quern != null) return Go(Work.Grind, quern.Pos, "baker-grinding");

        // Fetch from the Bakery's storage: dough, then flour, then grain.
        if (!m.PackFull(citizen))
        {
            foreach (var (match, want) in new (ItemMatch, int)[]
            {
                (s => ColonyManager.IsDough(s) && m.BreadFor(s) != null, m.Config.CookBatch),
                (ColonyManager.IsFlour, m.Config.BakerDoughBatch * 2),
                (s => quern != null && ColonyManager.IsGrindableToFlour(s), m.Config.BakerGrindBatch)
            })
            {
                var chest = storage.FirstOrDefault(c => c.Inventory.Any(s => !s.Empty && match(s.Itemstack)));
                if (chest == null) continue;
                SetStatus("baker-fetching");
                return FetchSupplies(chest.Pos, match, want);
            }
        }
        // Ask couriers for what the warehouse has.
        foreach (var (match, want) in new (ItemMatch, int)[]
        {
            (ColonyManager.IsDough, m.Config.CookBatch),
            (ColonyManager.IsFlour, m.Config.BakerDoughBatch * 2),
            (s => quern != null && ColonyManager.IsGrindableToFlour(s), m.Config.BakerGrindBatch)
        })
        {
            if (m.CodeInWarehouse(colony, match) is not { } code) continue;
            m.EnsureCraftRequest(colony, hut, code, want);
            SetStatus("baker-waiting");
            return false;
        }
        SetStatus(quern == null ? "baker-no-quern" : "baker-needs-grain");
        return false;
    }

    bool FetchFirewood(Colony colony, Building hut, List<BlockEntityContainer> storage)
    {
        var m = manager!;
        var chest = storage.FirstOrDefault(c => c.Inventory.Any(s => !s.Empty && ColonyManager.IsFirewood(s.Itemstack)));
        if (chest != null && !m.PackFull(citizenOrNull()))
        {
            SetStatus("baker-fetching");
            return FetchSupplies(chest.Pos, ColonyManager.IsFirewood, m.Config.CookFirewood * 2);
        }
        m.EnsureCraftRequest(colony, hut, "game:firewood", m.Config.CookFirewood * 4);
        SetStatus("baker-needs-firewood");
        return false;
    }

    CitizenRecord citizenOrNull() => Citizen!;

    bool Go(Work next, Vintagestory.API.MathTools.BlockPos pos, string status)
    {
        work = next;
        SetStatus(status);
        return GoWork(pos);
    }

    protected override void DoWork(Colony colony, CitizenRecord citizen, Building hut)
    {
        var m = manager!;
        var pit = m.CookFirepit(colony, hut);
        switch (work)
        {
            case Work.Empty when pit != null:
                m.EmptyFirepit(colony, citizen, pit);
                break;
            case Work.Fuel when pit != null:
                m.FuelFirepit(colony, citizen, pit);
                break;
            case Work.Load when pit != null:
                var dough = m.PackItems(citizen).FirstOrDefault(s => ColonyManager.IsDough(s) && m.BreadFor(s) != null);
                if (dough != null && m.BreadFor(dough) is { } kind) m.LoadFirepit(colony, citizen, pit, kind);
                break;
            case Work.Mix:
                m.MixDough(colony, hut, citizen);
                break;
            case Work.Grind:
                m.GrindAtQuern(colony, hut, citizen);
                entity.World.PlaySoundAt(new AssetLocation("game:sounds/block/quern"), target.X + 0.5, target.Y + 0.5, target.Z + 0.5, null, true, 16);
                break;
        }
        work = Work.None;
    }

    protected override string DescribeWork() => work switch
    {
        Work.Grind => Lang.Get("createacolony:activity-baker-grind"),
        Work.Mix => Lang.Get("createacolony:activity-baker-mix"),
        Work.Load or Work.Fuel or Work.Empty => Lang.Get("createacolony:activity-baker-bake"),
        _ => Lang.Get("createacolony:activity-baker-idle")
    };
}
