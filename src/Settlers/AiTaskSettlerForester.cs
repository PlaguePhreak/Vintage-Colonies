using System.Collections.Generic;
using CreateAColony.Colonies;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using ItemMatch = System.Func<Vintagestory.API.Common.ItemStack, bool>;

namespace CreateAColony.Settlers;

/// <summary>
/// A forester works from a Forester's Hut: it fells the trees around the hut, nearest first, with an
/// axe, and plants a tree seed or sapling where each one stood, so the forest grows back. It clears wild
/// bushes near the hut too, which give sticks.
/// </summary>
public class AiTaskSettlerForester : AiTaskSettlerWorker
{
    public AiTaskSettlerForester(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig) : base(entity, taskConfig, aiConfig) { }

    protected override string Job => Jobs.Forester;
    protected override string HutType => BuildingTypes.Forester;
    protected override EnumTool Tool => EnumTool.Axe;
    protected override ItemMatch Stock => ColonyManager.IsTreeSeed;
    protected override float WorkSeconds => clearingBush ? manager!.Config.ForesterSecondsPerBush : manager!.Config.ForesterSecondsPerTree;

    /// <summary>True while the work is a bush, not a tree.</summary>
    bool clearingBush;

    protected override bool PlanWork(Colony colony, CitizenRecord citizen, Building hut, List<BlockEntityContainer> storage)
    {
        var tree = manager!.FindTree(colony, hut, IsUnreachable);
        var bush = manager.FindBush(colony, hut, IsUnreachable);
        // Whichever is nearer the hut, so the bushes around it get cleared as the forest is worked.
        if (bush != null && (tree == null || bush.DistanceSqTo(hut.HutPos.X, hut.HutPos.Y, hut.HutPos.Z) < tree.DistanceSqTo(hut.HutPos.X, hut.HutPos.Y, hut.HutPos.Z)))
        {
            clearingBush = true;
            SetStatus("clearing-bushes");
            return GoWork(bush);
        }
        if (tree == null && !manager.StillSearching(hut, "trees") && manager.ForesterGoFurther(hut))
        {
            // The trees near the hut are gone: it looks further out (note 114).
            SetStatus("forester-further");
            return false;
        }
        if (tree == null)
        {
            SetStatus(manager.StillSearching(hut, "trees") || manager.StillSearching(hut, "bushes") ? "searching" : "no-trees");
            return false;
        }
        clearingBush = false;
        SetStatus("felling");
        return GoWork(tree);
    }

    protected override void DoWork(Colony colony, CitizenRecord citizen, Building hut)
    {
        var axe = Hand;
        if (axe == null || !HoldsTool) return;
        if (clearingBush)
        {
            manager!.ClearBush(colony, hut, citizen, entity, target, axe);
            SyncHand();
            return;
        }
        string? wood = manager!.FellTree(colony, hut, citizen, entity, target, axe);
        SyncHand(); // an axe that broke on the last log must vanish from the model too
        if (wood != null || entity.World.BlockAccessor.GetBlock(target).Id == 0) manager.Replant(colony, citizen, target, wood);
    }

    protected override string DescribeWork() => Lang.Get(clearingBush ? "createacolony:activity-forester-bush" : "createacolony:activity-forester-fell");
}
