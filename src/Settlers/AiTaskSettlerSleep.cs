using CreateAColony.Colonies;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace CreateAColony.Settlers;

/// <summary>
/// At night settlers walk to their bed and lie on it until morning. A settler without a free bed
/// sleeps on the floor next to the Town Hall, which is the player's cue to build more beds.
/// </summary>
public class AiTaskSettlerSleep : SettlerTaskBase
{
    BlockPos? bedPos;
    bool lyingDown;
    /// <summary>Where the settler lies on its bed and which way it faces, held while it sleeps.</summary>
    Vec3d? spot;
    float? spotYaw;

    /// <summary>Never given up, however often the walk fails (0.200.0).</summary>
    protected override bool CanGiveUp => false;

    /// <summary>A settler who cannot get to bed is carried there by this task itself.</summary>
    protected override bool HelpedWhenStuck => false;

    public AiTaskSettlerSleep(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig) : base(entity, taskConfig, aiConfig) { }

    /// <summary>Night for most settlers; for a guard on night watch, the day hours it sleeps.</summary>
    bool IsSleepTime()
    {
        var colony = Colony;
        var citizen = Citizen;
        return colony != null && citizen != null && manager!.IsSleepTime(colony, citizen, entity.World.Calendar.HourOfDay);
    }

    public override bool ShouldExecute()
    {
        return manager != null && IsSleepTime() && !WokenByThreat;
    }

    public override void StartExecute()
    {
        base.StartExecute();
        lyingDown = false;
        spot = null;
        spotYaw = null;
        var colony = Colony!;
        bedPos = manager!.EnsureBed(colony, Citizen!);
        BlockPos target = bedPos ?? colony.TownHallPos;
        WalkTo(target.ToVec3d().Add(0.5, 0, 0.5), bedPos != null ? 1.5f : 2.5f);
    }

    public override bool ContinueExecute(float dt)
    {
        if (!IsSleepTime() || WokenByThreat) return false;

        if (!lyingDown && (Arrived || PathFailed))
        {
            StopWalking();
            // No way home at bedtime: brought home to bed and reset, so a lost settler is back by morning (0.198.0).
            // However near the bed is (0.200.0): between 16 and 24 blocks from it a settler used to sleep on the ground.
            if (bedPos != null && !Arrived && !NearBed(bedPos) && entity is EntitySettler lost && Citizen is { } who)
            {
                manager!.BringHome(Colony!, who, lost, stopTasks: false);
            }
            if (bedPos != null && (Arrived || NearBed(bedPos)))
            {
                // Lie along the bed as a player does (on its own half of a furniture double bed).
                (spot, spotYaw) = manager!.BedSpot(Colony!, Citizen!, bedPos);
                entity.TeleportToDouble(spot.X, spot.Y, spot.Z);
            }
            PlayAnimation("sleep");
            lyingDown = true;
        }

        if (!lyingDown) KeepWalking();
        else HoldStill();
        return true;
    }

    /// <summary>Keeps a sleeper in its place and facing along the bed, so a bedmate or passer-by does not push it askew.</summary>
    void HoldStill()
    {
        var pos = entity.ServerPos;
        pos.Motion.Set(0, 0, 0);
        if (spot != null && pos.SquareDistanceTo(spot.X, spot.Y, spot.Z) > 0.01) pos.SetPos(spot);
        if (spotYaw is float yaw)
        {
            pos.Yaw = yaw;
            pos.HeadYaw = yaw;
            entity.BodyYaw = yaw;
        }
    }

    public override void FinishExecute(bool cancelled)
    {
        StopAnimation("sleep");
        if (lyingDown && bedPos != null && spot != null)
        {
            // Step off the bed so the settler does not start the day standing on it.
            entity.TeleportToDouble(bedPos.X + 0.5, bedPos.Y + 1, bedPos.Z + 0.5);
        }
        lyingDown = false;
        base.FinishExecute(cancelled);
    }

    public override string Describe(Colony colony, CitizenRecord citizen) => Vintagestory.API.Config.Lang.Get("createacolony:activity-sleeping");
}
