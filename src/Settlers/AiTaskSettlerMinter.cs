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
/// A minter works from the Royal Mint, which only exists with the Coinage mod (0.177.0). It strikes copies of the
/// colony's Coinage coin with a hammer from ingots of its metal. The coins stay in the Mint, part of the colony's
/// treasury, where the bailiff pays wages from (ColonyManager.Economy.cs). Without Coinage there is nothing to
/// strike; the colony tick frees any minter for other work.
/// </summary>
public class AiTaskSettlerMinter : AiTaskSettlerWorker
{
    public AiTaskSettlerMinter(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig) : base(entity, taskConfig, aiConfig) { }

    protected override string Job => Jobs.Minter;
    protected override string HutType => BuildingTypes.Mint;
    protected override EnumTool Tool => EnumTool.Hammer;
    protected override ItemMatch Stock => _ => false;
    protected override float WorkSeconds => manager!.Config.HutRecipeSecondsPerCraft;

    protected override bool AlsoKeeps(ItemStack stack) => IsRecipeTool(stack);

    protected override bool PlanFirst(Colony colony, CitizenRecord citizen, Building hut) =>
        FetchRecipeTools(colony, citizen, hut, manager!.HutStorage(colony, hut).Cast<BlockEntityContainer>().ToList());

    /// <summary>The colony's own coin (Coinage) comes first: copies of it struck from ingots of its metal.</summary>
    protected override bool WorkBeforeRecipes(Colony colony, Building hut) => manager!.HasOwnCoin(colony);

    /// <summary>
    /// With Coinage: strike copies of the colony's coin while the Mint holds fewer than MintStartKeep. Without it
    /// there is nothing to strike, and this only says why it is idle.
    /// </summary>
    protected override bool PlanWork(Colony colony, CitizenRecord citizen, Building hut, List<BlockEntityContainer> storage)
    {
        var m = manager!;
        if (m.HasOwnCoin(colony))
        {
            if (m.CountItems(storage, m.CurrencyMatch(colony)) >= m.Config.MintStartKeep)
            {
                SetStatus("mint-stocked");
                return false;
            }
            var metal = m.CoinMetal(colony);
            var chest = storage.FirstOrDefault(c => c.Inventory.Any(s => !s.Empty && s.Itemstack.Collectible.Code.Equals(metal)));
            if (chest == null)
            {
                m.EnsureCraftRequest(colony, hut, metal.ToString(), 4);
                SetStatus("mint-needs-metal");
                return false;
            }
            SetStatus("mint-striking");
            return GoWork(chest.Pos);
        }
        SetStatus("mint-needs-coinage");
        return false;
    }

    protected override void DoWork(Colony colony, CitizenRecord citizen, Building hut)
    {
        if (manager!.HasOwnCoin(colony)) manager.StrikeOwnCoins(colony, hut);
    }

    protected override string DescribeWork() => Lang.Get("createacolony:activity-minter-idle");
}
