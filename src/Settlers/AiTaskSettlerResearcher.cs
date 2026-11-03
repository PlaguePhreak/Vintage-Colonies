using System.Collections.Generic;
using CreateAColony.Colonies;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;
using ItemMatch = System.Func<Vintagestory.API.Common.ItemStack, bool>;

namespace CreateAColony.Settlers;

/// <summary>
/// A researcher works at the Archive. When a research node is running there and its items have been brought,
/// it studies at the hut and each piece of work adds Research Points to the node (ColonyManager.Research.cs).
/// The Archive's researchers share its running nodes out between them. With nothing to study it says why.
/// </summary>
public class AiTaskSettlerResearcher : AiTaskSettlerWorker
{
    public AiTaskSettlerResearcher(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig) : base(entity, taskConfig, aiConfig) { }

    protected override string Job => Jobs.Researcher;
    protected override string HutType => BuildingTypes.Archive;
    // No tool: research is done by hand. Knife is only a placeholder (see NeedsTool).
    protected override EnumTool Tool => EnumTool.Knife;
    protected override bool NeedsTool => false;
    protected override bool WorkNeedsTool => false;
    protected override ItemMatch Stock => _ => false;
    protected override float WorkSeconds => manager!.Config.ResearchSecondsPerWork;

    protected override string WorkAnimation(ItemSlot? hand) => "interactstatic";

    protected override bool PlanWork(Colony colony, CitizenRecord citizen, Building hut, List<BlockEntityContainer> storage)
    {
        var progress = manager!.ResearchFor(colony, hut, citizen, out string status);
        SetStatus(status);
        return progress != null && GoWork(hut.HutPos);
    }

    protected override void DoWork(Colony colony, CitizenRecord citizen, Building hut) => manager!.AddResearchWork(colony, hut, citizen);

    protected override string DescribeWork()
    {
        var m = manager;
        if (m == null || Colony is not { } colony || Citizen is not { } citizen || citizen.WorkplacePos == null || m.GetBuilding(colony, citizen.WorkplacePos) is not { } hut)
            return Lang.Get("createacolony:activity-researcher-idle");
        var progress = m.ResearchFor(colony, hut, citizen, out _, false);
        return progress == null ? Lang.Get("createacolony:activity-researcher-idle")
            : Lang.Get("createacolony:activity-researcher", Lang.Get("createacolony:research-" + progress.Code));
    }
}
