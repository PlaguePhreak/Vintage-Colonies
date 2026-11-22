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
/// A Tavern Keeper works at the Tavern (ColonyManager.Tavern.cs). While the Tavern has less than TavernStockLitres of
/// drink it brings jugs and buckets of drink from the warehouse, or takes empty ones (from the Tavern, else the
/// warehouse) to a finished barrel near the Cook's Hut, fills them and brings them back. The rest of the time it
/// minds the bar: settlers only get drinks while a keeper works there. Before the economy starts it has nothing to do.
/// </summary>
public class AiTaskSettlerTavernKeeper : AiTaskSettlerWorker
{
    /// <summary>The barrel it is filling jugs at, or null while it minds the bar.</summary>
    BlockPos? fillAt;

    public AiTaskSettlerTavernKeeper(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig) : base(entity, taskConfig, aiConfig) { }

    protected override string Job => Jobs.TavernKeeper;
    protected override string HutType => BuildingTypes.Tavern;
    protected override EnumTool Tool => EnumTool.Knife;
    protected override bool NeedsTool => false;
    protected override bool WorkNeedsTool => false;
    protected override ItemMatch Stock => _ => false;
    protected override float WorkSeconds => fillAt != null ? 4f : 10f;
    protected override string WorkAnimation(ItemSlot? hand) => "interactstatic";

    /// <summary>Empty jugs and buckets are kept for filling; drinks go into the Tavern's storage.</summary>
    protected override bool AlsoKeeps(ItemStack stack) => ColonyManager.IsEmptyDrinkContainer(stack);

    protected override bool PlanWork(Colony colony, CitizenRecord citizen, Building hut, List<BlockEntityContainer> storage)
    {
        var m = manager!;
        fillAt = null;
        bool needsJugs = false;
        if (!m.EconomyOpen(colony))
        {
            SetStatus("tavern-closed");
            return false;
        }
        var pack = m.PackItems(citizen);
        // Carrying drink: into the Tavern with it.
        if (pack.Any(m.HoldsDrink)) return GoDeposit(storage[0].Pos);

        if (m.TavernLitres(colony, hut) < m.Config.TavernStockLitres && !m.PackFull(citizen))
        {
            // Jugs and buckets of drink in the warehouse.
            if (m.WarehouseDrinkChest(colony, IsUnreachable) is { } drinks)
            {
                SetStatus("tavern-fetching");
                return FetchSupplies(drinks.Pos, m.HoldsDrink, 4);
            }
            // A finished brew near the Cook's Hut: fill empty jugs there.
            if (m.FinishedDrinkBarrel(colony, IsUnreachable) is { } barrel)
            {
                if (pack.Any(ColonyManager.IsEmptyDrinkContainer))
                {
                    fillAt = barrel.Pos.Copy();
                    workRange = 2.5f;
                    SetStatus("tavern-filling");
                    return GoWork(barrel.Pos);
                }
                if (m.EmptyContainerChest(colony, hut, IsUnreachable) is { } empties)
                {
                    SetStatus("tavern-fetching-jugs");
                    return FetchSupplies(empties.Pos, ColonyManager.IsEmptyDrinkContainer, 4);
                }
                needsJugs = true;
            }
        }

        // Minding the bar.
        SetStatus(needsJugs ? "tavern-needs-jugs" : "tavern-serving");
        workRange = 2.5f;
        return GoWork(hut.HutPos);
    }

    protected override void DoWork(Colony colony, CitizenRecord citizen, Building hut)
    {
        if (fillAt == null) return;
        if (entity.World.BlockAccessor.GetBlockEntity(fillAt) is BlockEntityBarrel barrel)
        {
            int n = manager!.FillFromBarrel(colony, citizen, barrel);
            if (n > 0) manager.AddStat(hut, "jugs-filled", n);
        }
        fillAt = null;
    }

    protected override string DescribeWork() => Lang.Get(fillAt != null ? "createacolony:activity-tavern-filling" : "createacolony:activity-tavern-keeper");
}
