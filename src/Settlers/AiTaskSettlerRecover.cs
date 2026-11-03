using CreateAColony.Colonies;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace CreateAColony.Settlers;

/// <summary>
/// A badly hurt settler (below config RecoverBelowHealth) goes home and lies in its bed until it is well again
/// (RecoverUntilHealth). On lying down it uses its first aid, or a bandage or poultice from a chest at home.
/// In bed it heals RestHealPerHour faster, and the Healer comes to treat it (ColonyManager.Healing.cs).
/// Running from an attacker comes first; a very hungry settler gets up to eat and comes back. Guards pull back to
/// their tower instead (AiTaskSettlerGuard).
/// </summary>
public class AiTaskSettlerRecover : SettlerTaskBase
{
    const long CheckMs = 2_000;
    long nextCheckMs;
    BlockPos? bedPos;
    bool lyingDown;
    Vec3d? spot;
    float? spotYaw;
    double lastHours;

    public AiTaskSettlerRecover(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig) : base(entity, taskConfig, aiConfig) { }

    public override bool ShouldExecute()
    {
        if (manager == null || entity.World.ElapsedMilliseconds < nextCheckMs) return false;
        nextCheckMs = entity.World.ElapsedMilliseconds + CheckMs;
        return Colony != null && Citizen is { } citizen && manager.NeedsRest(citizen, entity) && !Interrupted(citizen);
    }

    /// <summary>Running from an attacker, called to fight, or very hungry: those come first.</summary>
    bool Interrupted(CitizenRecord citizen) =>
        manager!.FleeingFrom(entity) != null || CalledToArms
        || (citizen.Saturation >= 0 && citizen.Saturation < manager.Config.SaturationMax * 0.1f);

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
        var colony = Colony;
        var citizen = Citizen;
        if (colony == null || citizen == null || citizen.IsDowned || Interrupted(citizen) || manager!.RestedEnough(entity)) return false;

        if (!lyingDown && (Arrived || PathFailed))
        {
            StopWalking();
            if (bedPos != null && (Arrived || NearBed(bedPos)))
            {
                (spot, spotYaw) = manager.BedSpot(colony, citizen, bedPos);
                entity.TeleportToDouble(spot.X, spot.Y, spot.Z);
            }
            PlayAnimation("sleep");
            lyingDown = true;
            lastHours = entity.World.Calendar.TotalHours;
            manager.StartRest(colony, citizen, entity, bedPos ?? entity.Pos.AsBlockPos);
        }

        if (!lyingDown)
        {
            KeepWalking();
            return true;
        }
        double now = entity.World.Calendar.TotalHours;
        manager.Rest(colony, entity, System.Math.Clamp(now - lastHours, 0, 1));
        lastHours = now;
        HoldStill();
        return true;
    }

    /// <summary>Keeps the settler in its place in bed, as the sleep task does.</summary>
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
        if (lyingDown && bedPos != null && spot != null) entity.TeleportToDouble(bedPos.X + 0.5, bedPos.Y + 1, bedPos.Z + 0.5);
        lyingDown = false;
        if (Citizen is { } citizen) manager?.EndRest(citizen);
        base.FinishExecute(cancelled);
    }

    protected override bool CountsForWillpower => false;

    public override string Describe(Colony colony, CitizenRecord citizen) => Lang.Get("createacolony:activity-recovering");
}
