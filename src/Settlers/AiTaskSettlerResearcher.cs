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
/// A researcher works at the Archive. When a research node is running there and its items have been brought,
/// it studies at the hut and each piece of work adds Research Points to the node (ColonyManager.Research.cs).
/// The Archive's researchers share its running nodes out between them. With nothing to study it says why.
/// With a chair, stool or bench beside a table near the Archive (0.180.0, ResearchersSit), it sits there and writes.
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

    /// <summary>The seat it writes at, or null when it studies standing at the hut.</summary>
    BlockPos? seat;
    bool seated;

    protected override string WorkAnimation(ItemSlot? hand)
    {
        if (seat != null && !seated) seated = SettlerSeats.SitOn(entity, seat);
        return "interactstatic";
    }

    protected override Vec3d WalkGoal() => seat != null ? SettlerSeats.SeatSpot(seat) : base.WalkGoal();

    protected override bool PlanWork(Colony colony, CitizenRecord citizen, Building hut, List<BlockEntityContainer> storage)
    {
        var progress = manager!.ResearchFor(colony, hut, citizen, out string status);
        SetStatus(status);
        if (progress == null)
        {
            GetUp();
            return false;
        }
        var desk = manager.Config.ResearchersSit ? seat ?? FindDesk(hut) : null;
        if (desk != null)
        {
            seat = desk;
            workRange = 1.2f;
            return GoWork(desk);
        }
        GetUp();
        return GoWork(hut.HutPos);
    }

    /// <summary>A free seat with a table right beside it, near the Archive's hut block.</summary>
    BlockPos? FindDesk(Building hut)
    {
        var ba = entity.World.BlockAccessor;
        bool BesideTable(BlockPos p)
        {
            foreach (var face in BlockFacing.HORIZONTALS)
            {
                for (int dy = 0; dy <= 1; dy++)
                {
                    var b = ba.GetBlock(p.AddCopy(face).Up(dy));
                    if (b.Code != null && b.Code.Path.StartsWith("table")) return true;
                }
            }
            return false;
        }
        return SettlerSeats.FindSeat(entity, hut.HutPos, System.Math.Max(1, manager!.Config.ArchiveRoomRadius), BesideTable);
    }

    void GetUp()
    {
        if (seated) SettlerSeats.StandUp(entity);
        seated = false;
        seat = null;
    }

    public override void FinishExecute(bool cancelled)
    {
        GetUp();
        base.FinishExecute(cancelled);
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
