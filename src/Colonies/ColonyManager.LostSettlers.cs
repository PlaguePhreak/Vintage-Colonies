using System.Collections.Generic;
using CreateAColony.Settlers;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace CreateAColony.Colonies;

/// <summary>
/// Lost settlers (0.198.0). In Aaron's offline play test four settlers ended up 15 to 25 blocks underground and over
/// 100 blocks from home, marked "unreachable" for days. A settler who cannot find the way home at bedtime, or who has
/// been "unreachable" for a whole game day, is brought back to bed. Then they are reset: what they were doing is
/// dropped, and the land around where they got lost is off limits for a few days, so they do not walk back into it.
/// Kept in memory only.
/// </summary>
public partial class ColonyManager
{
    /// <summary>
    /// Per settler (by citizen id): the spot they have stayed near since when (game days), and whether they were
    /// "unreachable" in that time. A settler who keeps saying "unreachable" while never getting away from one spot far
    /// from home is lost; one that wanders off again is not.
    /// </summary>
    readonly Dictionary<int, (Vec3d At, double Since, bool Unreachable)> lostWatch = new();
    /// <summary>Where each settler got lost and until when (game days) they keep away from it.</summary>
    readonly Dictionary<int, (Vec3d At, double Until)> offLimits = new();

    /// <summary>True when a walk to this spot would take the settler back where they got lost.</summary>
    public bool IsOffLimits(CitizenRecord citizen, Vec3d goal)
    {
        if (!offLimits.TryGetValue(citizen.CitizenId, out var zone)) return false;
        if (sapi.World.Calendar.TotalDays > zone.Until)
        {
            offLimits.Remove(citizen.CitizenId);
            return false;
        }
        double r = Config.LostOffLimitsRadius;
        return zone.At.SquareDistanceTo(goal) < r * r;
    }

    /// <summary>Brings home settlers who have been lost for too long. Called from the colony tick.</summary>
    void TickLostSettlers(Colony colony, double now)
    {
        foreach (var citizen in colony.Citizens)
        {
            if (!citizen.Alive || citizen.IsDowned || citizen.Visitor) continue;
            if (sapi.World.GetEntityById(citizen.EntityId) is not EntitySettler entity || !entity.Alive)
            {
                lostWatch.Remove(citizen.CitizenId);
                continue;
            }
            var here = entity.Pos.XYZ;
            var home = citizen.HomeBedPos?.ToVec3d() ?? colony.TownHallPos.ToVec3d();
            bool unreachable = WorkerStatus.GetValueOrDefault(citizen.CitizenId) == "unreachable";
            double near = Config.LostOffLimitsRadius;
            if (here.SquareDistanceTo(home) < near * near * 4
                || !lostWatch.TryGetValue(citizen.CitizenId, out var watch) || watch.At.SquareDistanceTo(here) > near * near)
            {
                lostWatch[citizen.CitizenId] = (here.Clone(), now, unreachable);
                continue;
            }
            if (unreachable && !watch.Unreachable) lostWatch[citizen.CitizenId] = watch = (watch.At, watch.Since, true);
            if (watch.Unreachable && now - watch.Since >= Config.LostRescueDays) BringHome(colony, citizen, entity);
        }
    }

    /// <summary>
    /// Teleports a lost settler to their bed (or beside the Town Hall without one) and resets them. Returns the spot
    /// they were put on.
    /// </summary>
    public Vec3d BringHome(Colony colony, CitizenRecord citizen, EntitySettler entity, bool stopTasks = true)
    {
        var lostAt = entity.Pos.XYZ.Clone();
        var bed = citizen.HomeBedPos;
        var spot = bed != null ? new Vec3d(bed.X + 0.5, bed.Y + 1, bed.Z + 0.5) : Beside(colony.TownHallPos);
        // Land near where they got lost is off limits for a while; never their own home, though.
        if (bed == null || lostAt.SquareDistanceTo(spot) > Config.LostOffLimitsRadius * Config.LostOffLimitsRadius * 4)
            offLimits[citizen.CitizenId] = (lostAt, sapi.World.Calendar.TotalDays + Config.LostOffLimitsDays);
        if (stopTasks) entity.GetBehavior<EntityBehaviorTaskAI>()?.TaskManager?.StopTasks();
        entity.TeleportToDouble(spot.X, spot.Y, spot.Z);
        lostWatch.Remove(citizen.CitizenId);
        WorkerStatus.Remove(citizen.CitizenId);
        citizen.LastSeenPos = spot.Clone();
        Mod.Logger.Notification("[createacolony] {0} was lost at {1} and was brought home to {2}", citizen.Name, lostAt.AsBlockPos, spot.AsBlockPos);
        LogEvent(colony, "event-lost-home", citizen.Name);
        MarkDirty(colony);
        return spot;
    }
}
