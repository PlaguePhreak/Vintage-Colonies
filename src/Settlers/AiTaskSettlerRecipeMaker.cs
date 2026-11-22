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
/// A worker whose whole job is its hut's Crafting recipes tab (0.185.0): the Stonemason cuts stone, the Fletcher makes
/// arrows and bows. What the hut can make is in config HutRecipeOutputs / HutRecipeInputParts / HutRecipeTools; a new hut
/// starts with config HutRecipeStartKeep, and builders' and guard towers' requests are made here when the warehouse is
/// short (ColonyManager.Orders.cs). The work itself is the base worker's recipe crafting. It picks up its tools from
/// the hut storage and asks couriers for one it lacks.
/// </summary>
public abstract class AiTaskSettlerRecipeMaker : AiTaskSettlerWorker
{
    protected AiTaskSettlerRecipeMaker(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig) : base(entity, taskConfig, aiConfig) { }

    protected override ItemMatch Stock => _ => false;
    protected override float WorkSeconds => manager!.Config.HutRecipeSecondsPerCraft;
    protected override bool NeedsTool => false;
    protected override bool WorkNeedsTool => false;

    /// <summary>It carries every tool its recipes may take.</summary>
    protected override bool AlsoKeeps(ItemStack stack) => IsRecipeTool(stack);

    protected override string WorkAnimation(ItemSlot? hand) => "interactstatic";

    protected override bool PlanFirst(Colony colony, CitizenRecord citizen, Building hut) =>
        FetchRecipeTools(colony, citizen, hut, manager!.HutStorage(colony, hut).Cast<BlockEntityContainer>().ToList());

    /// <summary>All its work is on the Crafting recipes tab; this asks for missing tools and says why it is idle.</summary>
    protected override bool PlanWork(Colony colony, CitizenRecord citizen, Building hut, List<BlockEntityContainer> storage)
    {
        var m = manager!;
        var wanted = m.WantedRecipes(colony, hut, citizen);
        if (hut.RecipeKeep.Count == 0 && wanted.Count == 0)
        {
            SetStatus("maker-nothing-set");
            return false;
        }
        if (wanted.Count == 0)
        {
            SetStatus("maker-stocked");
            return false;
        }
        // A tool none of its recipes can do without: ask couriers for one from the warehouse.
        foreach (var want in wanted)
        {
            if (want.Recipes.Any(r => m.CarriesTool(citizen, r.Tool, Hand?.Itemstack) && m.CarriesTool(citizen, r.Tool2, Hand?.Itemstack))) continue;
            var tool = want.Recipes.Select(r => r.Tool ?? r.Tool2).FirstOrDefault(t => t != null);
            if (tool == null) continue;
            if (m.CodeInWarehouse(colony, s => tool.SatisfiesAsIngredient(s, false)) is { } code)
            {
                m.EnsureCraftRequest(colony, hut, code, 1);
                SetStatus("maker-waiting-tool");
            }
            else SetStatus("maker-needs-tool");
            return false;
        }
        SetStatus("maker-needs-materials");
        return false;
    }

    protected override void DoWork(Colony colony, CitizenRecord citizen, Building hut) { }
}

/// <summary>The Stonemason: cuts stone into bricks, slabs, stairs, walls and polished stone at the Stonemason's Hut.</summary>
public class AiTaskSettlerStonemason : AiTaskSettlerRecipeMaker
{
    public AiTaskSettlerStonemason(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig) : base(entity, taskConfig, aiConfig) { }

    protected override string Job => Jobs.Stonemason;
    protected override string HutType => BuildingTypes.Stonemason;
    protected override EnumTool Tool => EnumTool.Chisel;

    protected override string DescribeWork() => Lang.Get("createacolony:activity-stonemason-idle");
}

/// <summary>The Fletcher: makes arrows and bows at the Fletcher's Hut, for the guards.</summary>
public class AiTaskSettlerFletcher : AiTaskSettlerRecipeMaker
{
    public AiTaskSettlerFletcher(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig) : base(entity, taskConfig, aiConfig) { }

    protected override string Job => Jobs.Fletcher;
    protected override string HutType => BuildingTypes.Fletcher;
    protected override EnumTool Tool => EnumTool.Knife;

    protected override string DescribeWork() => Lang.Get("createacolony:activity-fletcher-idle");
}
