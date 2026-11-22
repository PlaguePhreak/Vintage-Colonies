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
/// A Market Trader works from the Market (ColonyManager.Market.cs). It keeps the stall stocked from the warehouse
/// with the kinds on the For sale tab, up to MarketStockEach of each, never taking more than half of what the
/// warehouse has, so the basics stay free. The rest of the time it minds the stall: settlers only buy while a trader
/// works there. Before the economy starts (Town Hall level 2) it has nothing to do.
/// </summary>
public class AiTaskSettlerMarketTrader : AiTaskSettlerWorker
{
    public AiTaskSettlerMarketTrader(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig) : base(entity, taskConfig, aiConfig) { }

    protected override string Job => Jobs.MarketTrader;
    protected override string HutType => BuildingTypes.Market;
    protected override EnumTool Tool => EnumTool.Knife;
    protected override bool NeedsTool => false;
    protected override bool WorkNeedsTool => false;
    protected override ItemMatch Stock => _ => false;
    protected override float WorkSeconds => 10f;
    /// <summary>Minding the stall is standing about and serving, not hitting anything.</summary>
    protected override string WorkAnimation(ItemSlot? hand) => "interactstatic";

    protected override bool PlanWork(Colony colony, CitizenRecord citizen, Building hut, List<BlockEntityContainer> storage)
    {
        var m = manager!;
        if (!m.EconomyOpen(colony))
        {
            SetStatus("market-closed");
            return false;
        }

        // Carrying goods for the stall: put them out first.
        if (m.PackItems(citizen).Any(s => m.MarketKind(s) is { } k && m.MarketSells(colony, k))) return GoDeposit(storage[0].Pos);

        if (!m.PackFull(citizen))
        {
            var warehouse = m.WarehouseContainers(colony).ToList();
            foreach (var kind in ColonyManager.MarketKinds)
            {
                if (!m.MarketSells(colony, kind)) continue;
                ItemMatch match = s => m.MarketKind(s) == kind;
                int want = m.Config.MarketStockEach - m.CountItems(storage, match);
                if (want <= 0) continue;
                int take = System.Math.Min(want, m.CountItems(warehouse, match) / 2);
                if (take <= 0) continue;
                var chest = warehouse.FirstOrDefault(c => !IsUnreachable(c.Pos) && c.Inventory.Any(s => !s.Empty && match(s.Itemstack)));
                if (chest == null) continue;
                SetStatus("market-stocking");
                return FetchSupplies(chest.Pos, match, take);
            }
        }

        // Minding the stall.
        SetStatus("market-selling");
        workRange = 2.5f;
        return GoWork(hut.HutPos);
    }

    protected override void DoWork(Colony colony, CitizenRecord citizen, Building hut) { }

    protected override string DescribeWork() => Lang.Get("createacolony:activity-market-trader");
}
