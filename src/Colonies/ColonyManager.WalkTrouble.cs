using System;
using System.Collections.Generic;
using System.Linq;
using CreateAColony.Settlers;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace CreateAColony.Colonies;

/// <summary>One line of a settler's walking history: what it was doing, where to, and how the walk ended.</summary>
public class WalkNote
{
    public double Hours;
    public string Task = "";
    public string Result = "";
    public BlockPos At = new BlockPos(0);
    public BlockPos? Goal;
    public int Times = 1;
}

/// <summary>
/// Walks that fail (0.200.0, from the overnight test). Before, a task whose walk failed simply tried the same
/// walk again a little later, for ever: the Mayor on the Residence ladder, the courier at Builder's Hut 2, the
/// gatherer at a spot in a lake. Recall and reloading the world sent each one straight back.
///
/// Now every walk that fails for good is counted per goal. After WalkFailsBeforeHelp failures to the same goal the
/// settler is moved a little way towards it (the Willpower hop, or out of the front of the building it is in).
/// When that has been done WalkHelpsBeforeGiveUp times and the walk still fails, the settler gives the goal up
/// for WalkGiveUpDays: walks there end at once, the task does something else, and the settler window, the
/// report and the log say why. A short history of each settler's walks is kept for the report.
/// Kept in memory only.
/// </summary>
public partial class ColonyManager
{
    const int WalkNotesKept = 10;
    /// <summary>Goals this close together (blocks) count as the same goal.</summary>
    const double SameGoalDistance = 3;

    class FailedGoal
    {
        public Vec3d Goal = new();
        public int Fails;
        public int Helps;
        public double LastHours;
        public double GivenUpUntilDays;
    }

    readonly Dictionary<int, List<WalkNote>> walkNotes = new();
    readonly Dictionary<int, List<FailedGoal>> failedGoals = new();

    /// <summary>A settler's last walks, oldest first (for the report and the settler window).</summary>
    public IReadOnlyList<WalkNote> WalkNotes(int citizenId) => walkNotes.TryGetValue(citizenId, out var notes) ? notes : Array.Empty<WalkNote>();

    /// <summary>Writes a line into a settler's walking history; a repeat of the last line only counts up.</summary>
    public void NoteWalk(CitizenRecord citizen, Entity entity, string task, string result, Vec3d? goal)
    {
        if (!walkNotes.TryGetValue(citizen.CitizenId, out var notes)) walkNotes[citizen.CitizenId] = notes = new List<WalkNote>();
        var goalPos = goal?.AsBlockPos;
        var last = notes.Count > 0 ? notes[^1] : null;
        double hours = sapi.World.Calendar.TotalHours;
        if (last != null && last.Task == task && last.Result == result && SamePlace(last.Goal, goalPos))
        {
            last.Times++;
            last.Hours = hours;
            last.At = entity.Pos.AsBlockPos;
            return;
        }
        notes.Add(new WalkNote { Hours = hours, Task = task, Result = result, At = entity.Pos.AsBlockPos, Goal = goalPos });
        if (notes.Count > WalkNotesKept) notes.RemoveAt(0);
    }

    static bool SamePlace(BlockPos? a, BlockPos? b) =>
        a == null || b == null ? a == b : Math.Abs(a.X - b.X) <= SameGoalDistance && Math.Abs(a.Y - b.Y) <= SameGoalDistance && Math.Abs(a.Z - b.Z) <= SameGoalDistance;

    FailedGoal? FailedGoalAt(int citizenId, Vec3d goal, bool add)
    {
        if (!failedGoals.TryGetValue(citizenId, out var list))
        {
            if (!add) return null;
            failedGoals[citizenId] = list = new List<FailedGoal>();
        }
        double hours = sapi.World.Calendar.TotalHours, days = sapi.World.Calendar.TotalDays;
        // Old trouble is forgotten: a goal that has not failed for a while starts from nothing.
        list.RemoveAll(f => f.GivenUpUntilDays < days && hours - f.LastHours > Math.Max(1, Config.WalkFailMemoryHours));
        var found = list.Find(f => f.Goal.SquareDistanceTo(goal) <= SameGoalDistance * SameGoalDistance);
        if (found == null && add)
        {
            found = new FailedGoal { Goal = goal.Clone() };
            list.Add(found);
            if (list.Count > 12) list.RemoveAt(0);
        }
        return found;
    }

    /// <summary>True while the settler has given this goal up (see the class notes). until: the game day it ends.</summary>
    public bool GaveUpOn(CitizenRecord citizen, Vec3d goal, out double until)
    {
        until = 0;
        if (FailedGoalAt(citizen.CitizenId, goal, false) is not { } f || f.GivenUpUntilDays <= sapi.World.Calendar.TotalDays) return false;
        until = f.GivenUpUntilDays;
        return true;
    }

    /// <summary>The goals a settler has given up for now, for the report and the settler window.</summary>
    public IEnumerable<(BlockPos Goal, double UntilDays)> GivenUpGoals(int citizenId)
    {
        double days = sapi.World.Calendar.TotalDays;
        return failedGoals.TryGetValue(citizenId, out var list)
            ? list.Where(f => f.GivenUpUntilDays > days).Select(f => (f.Goal.AsBlockPos, f.GivenUpUntilDays))
            : Enumerable.Empty<(BlockPos, double)>();
    }

    /// <summary>A settler arrived: whatever trouble it had with this goal is over.</summary>
    public void WalkArrived(CitizenRecord citizen, Vec3d goal)
    {
        if (failedGoals.TryGetValue(citizen.CitizenId, out var list)) list.RemoveAll(f => f.Goal.SquareDistanceTo(goal) <= SameGoalDistance * SameGoalDistance);
    }

    /// <summary>Recall, or a new job: the settler starts again with a clean slate.</summary>
    public void ForgetWalkTrouble(int citizenId)
    {
        failedGoals.Remove(citizenId);
        offLimits.Remove(citizenId);
    }

    /// <summary>
    /// A walk failed for good. Counts it; after enough failures to the same goal the settler is helped a little way
    /// towards it, and after enough help the goal is given up for a while (never when mayGiveUp is false: a bed,
    /// food, shelter). Returns what was done, for the settler's walking history.
    /// </summary>
    public string WalkFailed(Colony? colony, CitizenRecord citizen, EntityAgent entity, Vec3d goal, bool mayGiveUp)
    {
        var f = FailedGoalAt(citizen.CitizenId, goal, true)!;
        f.Fails++;
        f.LastHours = sapi.World.Calendar.TotalHours;
        if (f.Fails < Math.Max(1, Config.WalkFailsBeforeHelp)) return "no way";
        f.Fails = 0;
        if (mayGiveUp && f.Helps >= Math.Max(0, Config.WalkHelpsBeforeGiveUp))
        {
            f.Helps = 0;
            f.GivenUpUntilDays = sapi.World.Calendar.TotalDays + Math.Max(0.05, Config.WalkGiveUpDays);
            if (PathDebug) Mod.Logger.Notification("[pathing] {0} at {1} gives up on {2} until day {3:0.0}", citizen.Name, entity.Pos.AsBlockPos, goal.AsBlockPos, f.GivenUpUntilDays);
            return "gave up";
        }
        f.Helps++;
        var will = entity.GetBehavior<EntityBehaviorColonyCitizen>()?.Willpower;
        if (will == null || !Config.Willpower) return "no way";
        will.Aim(goal);
        return Relocate(entity, will, citizen) ? "no way, moved closer" : "no way";
    }

    /// <summary>
    /// True for a spot at one of the settler's own places: its hut, its home, a building it is building. These are
    /// never off limits and a lost spot near them closes nothing (in the test a builder was locked out of his own
    /// build site for three days).
    /// </summary>
    public bool IsOwnPlace(Colony? colony, CitizenRecord citizen, Vec3d spot)
    {
        double r = Config.LostOffLimitsRadius;
        bool Near(BlockPos? p) => p != null && Sq(p.X + 0.5 - spot.X) + Sq(p.Z + 0.5 - spot.Z) <= r * r && Math.Abs(p.Y - spot.Y) <= r;
        if (Near(citizen.WorkplacePos) || Near(citizen.HomeBedPos)) return true;
        if (colony == null) return false;
        if (Near(colony.TownHallPos) || Near(colony.WarehousePos)) return true;
        return colony.Buildings.Any(b => b.BuilderCitizenId == citizen.CitizenId && b.HasWorkOrder && Near(b.HutPos));
    }

    /// <summary>The building whose outline the spot is inside, or null.</summary>
    public Building? BuildingAround(Colony colony, Vec3d spot)
    {
        int x = (int)Math.Floor(spot.X), y = (int)Math.Floor(spot.Y), z = (int)Math.Floor(spot.Z);
        foreach (var b in colony.Buildings)
        {
            if (b.IsStation || b.Level < 1 || Math.Abs(b.HutPos.X - x) > 48 || Math.Abs(b.HutPos.Z - z) > 48) continue;
            var box = Footprint(b);
            if (x >= box.X1 && x <= box.X2 && z >= box.Z1 && z <= box.Z2 && y >= box.Y1 - 1 && y <= box.Y2 + 1) return b;
        }
        return null;
    }

    /// <summary>The side a building's front faces now it is turned (BlockFacing's HorizontalAngleIndex), or null without a door or marker.</summary>
    public int? FrontSide(Building b)
    {
        if (Blueprints.Get(Blueprints.StyleFor(b), b.Type, 1, 0, b.Variant)?.Front is not { } front) return null;
        return GameMath.Mod(front - b.Rotation / 90, 4);
    }

    /// <summary>
    /// A spot to stand on just outside a building's front (the side its door or "Front of build" marker is on),
    /// nearest the middle of that side; without a front, outside the side nearest towards. Null when there is none.
    /// </summary>
    public Vec3d? FrontSpot(Building b, Entity entity, Vec3d? towards = null)
    {
        var box = Footprint(b);
        var sides = new List<BlockFacing>();
        if (FrontSide(b) is { } side) sides.Add(BlockFacing.HORIZONTALS_ANGLEORDER[side]);
        if (towards != null)
        {
            double cx = (box.X1 + box.X2 + 1) / 2.0, cz = (box.Z1 + box.Z2 + 1) / 2.0;
            double dx = towards.X - cx, dz = towards.Z - cz;
            var facing = Math.Abs(dx) > Math.Abs(dz) ? (dx > 0 ? BlockFacing.EAST : BlockFacing.WEST) : (dz > 0 ? BlockFacing.SOUTH : BlockFacing.NORTH);
            if (!sides.Contains(facing)) sides.Add(facing);
        }
        foreach (var face in BlockFacing.HORIZONTALS) if (!sides.Contains(face)) sides.Add(face);
        foreach (var face in sides)
        {
            int x = face == BlockFacing.WEST ? box.X1 - 2 : face == BlockFacing.EAST ? box.X2 + 2 : (box.X1 + box.X2) / 2;
            int z = face == BlockFacing.NORTH ? box.Z1 - 2 : face == BlockFacing.SOUTH ? box.Z2 + 2 : (box.Z1 + box.Z2) / 2;
            var around = new BlockPos(x, b.HutPos.Y, z, entity.Pos.Dimension);
            if (sapi.World.BlockAccessor.GetChunkAtBlockPos(around) == null) continue;
            around.Y = sapi.World.BlockAccessor.GetTerrainMapheightAt(around) + 1;
            if (Math.Abs(around.Y - b.HutPos.Y) > 6) continue;
            for (int r = 0; r <= 3; r++)
            {
                if (FreeSpotNear(entity, around, r) is not { } spot) continue;
                var feet = spot.AsBlockPos;
                // Outside the building, on dry ground.
                if (feet.X >= box.X1 && feet.X <= box.X2 && feet.Z >= box.Z1 && feet.Z <= box.Z2) continue;
                if (sapi.World.BlockAccessor.GetBlock(feet, BlockLayersAccess.Fluid).IsLiquid()) continue;
                return spot;
            }
        }
        return null;
    }
}
