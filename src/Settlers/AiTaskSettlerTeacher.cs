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
/// A teacher works at the School in lesson hours (ColonyManager.School.cs). It stands at the hut block, or sits at
/// a seat beside a table near it, and each piece of work keeps the School open for the children and gives it
/// teaching XP. Outside lesson hours it has nothing to do there and says so.
/// </summary>
public class AiTaskSettlerTeacher : AiTaskSettlerWorker
{
    public AiTaskSettlerTeacher(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig) : base(entity, taskConfig, aiConfig) { }

    protected override string Job => Jobs.Teacher;
    protected override string HutType => BuildingTypes.School;
    // No tool: teaching is done by talking. Knife is only a placeholder (see NeedsTool).
    protected override EnumTool Tool => EnumTool.Knife;
    protected override bool NeedsTool => false;
    protected override bool WorkNeedsTool => false;
    protected override ItemMatch Stock => _ => false;
    protected override float WorkSeconds => manager!.Config.TeacherSecondsPerWork;

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
        string status = manager!.TeacherStatus(colony, hut);
        SetStatus(status);
        if (!manager.LessonTime())
        {
            GetUp();
            return false;
        }
        var desk = seat ?? FindDesk(hut);
        if (desk != null)
        {
            seat = desk;
            workRange = 1.2f;
            return GoWork(desk);
        }
        GetUp();
        workRange = 2.5f;
        return GoWork(hut.HutPos);
    }

    /// <summary>A free seat with a table right beside it, near the School's hut block.</summary>
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
        return SettlerSeats.FindSeat(entity, hut.HutPos, System.Math.Max(1, manager!.Config.SchoolRoomRadius), BesideTable);
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

    protected override void DoWork(Colony colony, CitizenRecord citizen, Building hut) => manager!.TeacherWork(colony, hut, citizen);

    protected override string DescribeWork()
    {
        var m = manager;
        if (m == null || Colony is not { } colony || Citizen?.WorkplacePos is not { } at || m.GetBuilding(colony, at) is not { } hut || !m.LessonTime())
            return Lang.Get("createacolony:activity-teacher-idle");
        var skill = m.LessonSkill(colony, hut, (int)entity.World.Calendar.TotalDays);
        return skill == null ? Lang.Get("createacolony:activity-teacher-idle")
            : Lang.Get("createacolony:activity-teacher", Lang.Get("createacolony:skill-" + skill));
    }
}
