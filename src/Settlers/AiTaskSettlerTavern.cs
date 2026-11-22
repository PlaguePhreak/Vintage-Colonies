using CreateAColony.Colonies;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace CreateAColony.Settlers;

/// <summary>
/// An evening at the Tavern (ColonyManager.Tavern.cs): during the Tavern's hours a settler may leave what it is doing,
/// walk to the Tavern, sit down on a free seat near it (or the floor) and stay a while. On arrival it has a drink if
/// it can pay for one, else a chat. Eating, sleeping, a church service and running from danger still come first.
/// </summary>
public class AiTaskSettlerTavern : SettlerTaskBase
{
    const long RetryMs = 15_000;
    const float ArriveRange = 2f;
    /// <summary>Close enough to the Tavern to count as there when the way to the chosen spot fails.</summary>
    const float NearEnough = 8f;
    const int SeatRange = 8;

    long nextTryMs;
    int tavernId;
    BlockPos hut = new(0);
    Vec3d goal = new();
    BlockPos? seat;
    bool there, sitting, drank;
    float stayLeft;

    public AiTaskSettlerTavern(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig) : base(entity, taskConfig, aiConfig) { }

    protected override bool CountsForWillpower => false;

    public override bool ShouldExecute()
    {
        if (manager == null || entity.World.ElapsedMilliseconds < nextTryMs) return false;
        nextTryMs = entity.World.ElapsedMilliseconds + RetryMs;
        if (Colony is not { } colony || Citizen is not { } citizen || CalledToArms) return false;
        if (manager.IsSleepTime(colony, citizen, entity.World.Calendar.HourOfDay)) return false;
        if (manager.TavernFor(colony, citizen) is not { } tavern) return false;
        tavernId = tavern.BuildingId;
        hut = tavern.HutPos.Copy();
        seat = SettlerSeats.FindSeat(entity, hut, SeatRange);
        var rand = entity.World.Rand;
        goal = seat != null ? SettlerSeats.SeatSpot(seat)
            : manager.FreeSpotNear(entity, hut.AddCopy(rand.Next(-3, 4), 0, rand.Next(-3, 4)), 3) ?? manager.FreeSpotNear(entity, hut, 5) ?? hut.ToVec3d().Add(0.5, 1, 0.5);
        var cfg = manager.Config;
        stayLeft = cfg.TavernStayMinSeconds + (float)rand.NextDouble() * System.Math.Max(0, cfg.TavernStayMaxSeconds - cfg.TavernStayMinSeconds);
        return true;
    }

    public override void StartExecute()
    {
        base.StartExecute();
        there = false;
        sitting = false;
        drank = false;
        WalkTo(goal, ArriveRange);
    }

    public override bool ContinueExecute(float dt)
    {
        if (manager == null || Colony is not { } colony || Citizen is not { } citizen || CalledToArms) return false;
        if (there)
        {
            stayLeft -= dt;
            return stayLeft > 0 && !manager.IsSleepTime(colony, citizen, entity.World.Calendar.HourOfDay);
        }
        if (Arrived || (PathFailed && entity.Pos.SquareDistanceTo(hut.ToVec3d().Add(0.5, 1, 0.5)) <= NearEnough * NearEnough))
        {
            StopWalking();
            there = true;
            if (Arrived && seat != null && SettlerSeats.SitOn(entity, seat)) sitting = true;
            else if (SettlerSeats.CanSitHere(entity))
            {
                SettlerSeats.SitOnFloor(entity);
                sitting = true;
            }
            var tavern = colony.Buildings.Find(b => b.BuildingId == tavernId);
            if (tavern != null) drank = manager.AtTavern(colony, citizen, tavern);
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

    public override string Describe(Colony colony, CitizenRecord citizen) =>
        Lang.Get(!there ? "createacolony:activity-tavern-walking" : drank ? "createacolony:activity-tavern-drinking" : "createacolony:activity-tavern-chatting");
}
