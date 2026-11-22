using CreateAColony.Colonies;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace CreateAColony.Settlers;

/// <summary>
/// A child's day (ColonyManager.School.cs). In lesson hours a child with a place at a School walks there and sits
/// in class, on a seat near the hut block or on the floor; after LessonSeconds the day's lesson counts. After
/// lessons it follows a parent at a safe job and watches them work; after WatchSeconds close by that counts too.
/// Fleeing, shelter, sleep and meals come first.
/// </summary>
public class AiTaskSettlerChildDay : SettlerTaskBase
{
    const long RetryMs = 10_000;
    const float NearEnough = 10f;
    const float WatchRange = 5f;

    enum Mode { School, Follow }

    long nextTryMs;
    Mode mode;
    int schoolId;
    BlockPos hutPos = new(0);
    BlockPos? seat;
    Vec3d goal = new();
    bool there, sitting;
    float sat;
    int parentId;
    long parentEntityId;
    string parentName = "";

    public AiTaskSettlerChildDay(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig) : base(entity, taskConfig, aiConfig) { }

    protected override bool CountsForWillpower => false;

    public override bool ShouldExecute()
    {
        if (manager == null || entity.World.ElapsedMilliseconds < nextTryMs) return false;
        nextTryMs = entity.World.ElapsedMilliseconds + RetryMs;
        if (Colony is not { } colony || Citizen is not { IsChild: true } child || child.IsDowned) return false;
        if (manager.LessonTime() && manager.SchoolFor(colony, child) is { } school)
        {
            mode = Mode.School;
            schoolId = school.BuildingId;
            hutPos = school.HutPos.Copy();
            seat = SettlerSeats.FindSeat(entity, hutPos, System.Math.Max(1, manager.Config.SchoolRoomRadius));
            goal = seat != null ? SettlerSeats.SeatSpot(seat)
                : manager.FreeSpotNear(entity, hutPos.AddCopy(entity.World.Rand.Next(-3, 4), 0, entity.World.Rand.Next(-3, 4)), 3)
                  ?? hutPos.ToVec3d().Add(0.5, 1, 0.5);
            return true;
        }
        if (manager.AfterSchoolTime() && child.LastWatchDay != (int)entity.World.Calendar.TotalDays && manager.ParentToFollow(colony, child) is { } parent)
        {
            mode = Mode.Follow;
            parentId = parent.CitizenId;
            parentEntityId = parent.EntityId;
            parentName = parent.Name;
            return true;
        }
        return false;
    }

    public override void StartExecute()
    {
        base.StartExecute();
        there = false;
        sitting = false;
        sat = 0;
        if (mode == Mode.School) WalkTo(goal, seat != null ? 0.6f : 1.5f);
        else if (ParentEntity() is { } p) WalkTo(p.Pos.XYZ, WatchRange - 2, () => ParentEntity()?.Pos.XYZ ?? p.Pos.XYZ);
    }

    Entity? ParentEntity() => entity.World.GetEntityById(parentEntityId) is { Alive: true } e ? e : null;

    public override bool ContinueExecute(float dt)
    {
        if (manager == null || Colony is not { } colony || Citizen is not { IsChild: true } child) return false;
        return mode == Mode.School ? InClass(colony, child, dt) : Following(colony, child, dt);
    }

    bool InClass(Colony colony, CitizenRecord child, float dt)
    {
        if (!manager!.LessonTime() || colony.Buildings.Find(b => b.BuildingId == schoolId) is not { } school) return false;
        if (there)
        {
            if (!sitting && SettlerSeats.CanSitHere(entity))
            {
                sitting = seat != null && SettlerSeats.SitOn(entity, seat);
                if (!sitting)
                {
                    SettlerSeats.SitOnFloor(entity);
                    sitting = true;
                }
            }
            if (seat == null) entity.Pos.Yaw = (float)System.Math.Atan2(hutPos.X + 0.5 - entity.Pos.X, hutPos.Z + 0.5 - entity.Pos.Z);
            sat += dt;
            if (sat >= manager.Config.LessonSeconds) manager.AttendLesson(colony, child, school);
            return true;
        }
        if (Arrived || (PathFailed && entity.Pos.SquareDistanceTo(hutPos.ToVec3d().Add(0.5, 1, 0.5)) <= NearEnough * NearEnough))
        {
            StopWalking();
            there = true;
            return true;
        }
        if (PathFailed)
        {
            StopWalking();
            return false;
        }
        KeepWalking();
        return true;
    }

    bool Following(Colony colony, CitizenRecord child, float dt)
    {
        if (!manager!.AfterSchoolTime() || ParentEntity() is not { } p) return false;
        if (manager.FindCitizen(colony.ColonyId, parentId) is not { } parent || parent.IsDowned || manager.RaidOn(colony) != null) return false;
        if (entity.Pos.SquareDistanceTo(p.Pos.XYZ) <= WatchRange * WatchRange)
        {
            if (!there)
            {
                StopWalking();
                there = true;
            }
            entity.Pos.Yaw = (float)System.Math.Atan2(p.Pos.X - entity.Pos.X, p.Pos.Z - entity.Pos.Z);
            sat += dt;
            if (sat >= manager.Config.WatchSeconds)
            {
                manager.WatchParent(colony, child, parent);
                return false;
            }
            return true;
        }
        if (there)
        {
            // The parent walked off: after them.
            there = false;
            WalkTo(p.Pos.XYZ, WatchRange - 2, () => ParentEntity()?.Pos.XYZ ?? p.Pos.XYZ);
            return true;
        }
        if (PathFailed)
        {
            StopWalking();
            return false;
        }
        KeepWalking();
        return true;
    }

    public override void FinishExecute(bool cancelled)
    {
        if (sitting) SettlerSeats.StandUp(entity);
        sitting = false;
        base.FinishExecute(cancelled);
    }

    public override string Describe(Colony colony, CitizenRecord citizen) => mode == Mode.School
        ? Lang.Get(there ? "createacolony:activity-child-class" : "createacolony:activity-child-school")
        : Lang.Get(there ? "createacolony:activity-child-watching" : "createacolony:activity-child-following", parentName);
}
