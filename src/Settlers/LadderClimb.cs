using System;
using System.Collections.Generic;
using CreateAColony.Colonies;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace CreateAColony.Settlers;

/// <summary>
/// Climbing ladders. The game's pathfinder only walks: it steps up one block and drops down a few, and never
/// plans a way up or down a ladder. So when a settler finds no path to a goal well above or below it,
/// SettlerTaskBase looks for a ladder nearby with Find, walks to its foot (or top) with the pathfinder, and
/// then this class steers the climb itself: it pushes the settler against the ladder and up (or down) it,
/// which the game's physics turns into climbing, then steps off at the other end.
/// Any block marked "climbable" counts as a ladder, so modded ladders and scaffolding that are climbable work too.
/// </summary>
public class LadderClimb
{
    /// <summary>A climb that has not moved the settler half a block in this long is given up.</summary>
    const long NoProgressMs = 3000;
    /// <summary>Ladders shorter than this are left to the pathfinder, which steps up one block on its own.</summary>
    const int MinHeight = 2;

    /// <summary>Where the settler stands before the climb (found with the pathfinder).</summary>
    public Vec3d Start = null!;
    /// <summary>Where it steps off at the other end.</summary>
    public Vec3d Exit = null!;
    /// <summary>The ladder column the settler climbs in.</summary>
    public BlockPos Column = null!;
    public bool Up;
    /// <summary>Ladders the settler puts up itself before climbing (see ColonyManager.Scaffold.cs), or null for a ladder already there.</summary>
    public List<BlockPos>? ToPlace;
    /// <summary>The side of the ladders the wall is on.</summary>
    public BlockFacing? WallSide;
    /// <summary>Set when the settler fell off the ladder on the way up, so the climb can be tried again.</summary>
    public bool Fell;

    /// <summary>A climb down that has not dropped at all in this long is set over the ladder's hole (0.200.0).</summary>
    const long NudgeMs = 1200;

    bool steppingOff;
    bool nudged;
    double lastY, highestY;
    long lastProgressMs;
    long beganMs;

    /// <summary>
    /// Set when the climb did not work but both ends are sound, so the settler was set down at the far end
    /// (0.200.0). In the overnight test the Mayor stood on top of the Residence's ladder for days: the push
    /// towards the ladder never got her off its top edge, and the climb was simply tried again every 40 seconds.
    /// </summary>
    public bool Carried;

    /// <summary>
    /// The ladder within radius that best gets the settler towards goal's height, with a spot to stand at each
    /// end, or null. Only for goals at least two blocks higher or lower.
    /// </summary>
    public static LadderClimb? Find(ColonyManager manager, EntityAgent entity, Vec3d goal, int radius, ICollection<BlockPos> skip)
    {
        var ba = entity.World.BlockAccessor;
        var here = entity.Pos.AsBlockPos;
        int rise = (int)Math.Floor(goal.Y) - here.Y;
        if (Math.Abs(rise) < MinHeight) return null;
        bool up = rise > 0;
        int low = Math.Min(here.Y, (int)Math.Floor(goal.Y)) - 2, high = Math.Max(here.Y, (int)Math.Floor(goal.Y)) + 2;
        high = Math.Min(high, low + 40);

        LadderClimb? best = null;
        double bestScore = double.MaxValue;
        var pos = new BlockPos(here.dimension);
        for (int dx = -radius; dx <= radius; dx++)
        for (int dz = -radius; dz <= radius; dz++)
        {
            if (dx * dx + dz * dz > radius * radius) continue;
            int x = here.X + dx, z = here.Z + dz;
            if (ba.GetChunkAtBlockPos(x, here.Y, z) == null) continue;
            // Each run of climbable blocks in this column.
            int y = low;
            while (y <= high)
            {
                pos.Set(x, y, z);
                if (!IsLadder(ba, pos)) { y++; continue; }
                int bottom = y;
                while (y <= high + 20 && IsLadder(ba, pos.Set(x, y + 1, z))) y++;
                int top = y;
                y++;
                if (top - bottom + 1 < MinHeight) continue;
                // It has to start near the settler's feet and lead the right way.
                int startY = up ? bottom : top + 1;
                int endY = up ? top + 1 : bottom;
                if (Math.Abs(startY - here.Y) > 1 || (up ? endY <= here.Y + 1 : endY >= here.Y - 1)) continue;

                var column = new BlockPos(x, bottom, z, here.dimension);
                var start = manager.FreeSpotNear(entity, new BlockPos(x, startY, z, here.dimension), 1);
                // Going up, step off onto the block the ladder hangs on, so the settler never steps out into the air.
                var exit = (up ? OntoWall(manager, entity, x, top, z) : null)
                    ?? manager.FreeSpotNear(entity, new BlockPos(x, endY, z, here.dimension), 1);
                if (start == null || exit == null || Math.Abs(exit.Y - endY) > 1.5) continue;
                double gained = Math.Min(Math.Abs(endY - here.Y), Math.Abs(rise));
                double score = start.DistanceTo(entity.Pos.XYZ) + Horizontal(exit, goal) + 2 * Math.Abs(goal.Y - exit.Y) - gained;
                if (skip.Contains(column) || score >= bestScore) continue;
                bestScore = score;
                best = new LadderClimb { Start = start, Exit = exit, Column = column, Up = up };
            }
        }
        return best;
    }

    /// <summary>
    /// The spot on top of the block the ladder at the top of a column hangs on, or null if the settler cannot
    /// stand there. The ladder's "side" variant names the wall side (as ColonyManager.PlaceScaffold sets it);
    /// for ladders without it, any solid block beside the top ladder.
    /// </summary>
    static Vec3d? OntoWall(ColonyManager manager, EntityAgent entity, int x, int top, int z)
    {
        var ba = entity.World.BlockAccessor;
        int dim = entity.Pos.Dimension;
        var ladder = ba.GetBlock(new BlockPos(x, top, z, dim), BlockLayersAccess.Solid);
        var sides = new List<BlockFacing>();
        if (BlockFacing.FromCode(ladder.Variant["side"]) is { IsHorizontal: true } side) sides.Add(side);
        foreach (var face in BlockFacing.HORIZONTALS) if (!sides.Contains(face)) sides.Add(face);
        foreach (var face in sides)
        {
            var wall = new BlockPos(x + face.Normali.X, top, z + face.Normali.Z, dim);
            if (!ba.GetBlock(wall).SideSolid[face.Opposite.Index]) continue;
            var spot = wall.UpCopy();
            if (manager.CanStandAt(entity, spot)) return new Vec3d(spot.X + 0.5, spot.Y, spot.Z + 0.5);
        }
        return null;
    }

    static double Horizontal(Vec3d a, Vec3d b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    static bool IsLadder(IBlockAccessor ba, BlockPos pos)
    {
        var block = ba.GetBlock(pos, BlockLayersAccess.Solid);
        return block.Id != 0 && (block.Climbable || block.IsClimbable(pos));
    }

    public void Begin(EntityAgent entity)
    {
        steppingOff = false;
        nudged = false;
        Carried = false;
        lastY = highestY = entity.Pos.Y;
        Fell = false;
        lastProgressMs = beganMs = entity.World.ElapsedMilliseconds;
        entity.Pos.Yaw = (float)Math.Atan2(Column.X + 0.5 - entity.Pos.X, Column.Z + 0.5 - entity.Pos.Z);
    }

    /// <summary>Steers one step of the climb. Returns true when done, false while climbing; sets failed when it got nowhere.</summary>
    public bool Tick(EntityAgent entity, float speed, out bool failed)
    {
        failed = false;
        var pos = entity.Pos;
        var controls = entity.Controls;
        long now = entity.World.ElapsedMilliseconds;
        if (Math.Abs(pos.Y - lastY) > 0.5 || steppingOff)
        {
            if (Math.Abs(pos.Y - lastY) > 0.5) lastY = pos.Y;
            if (!steppingOff) lastProgressMs = now;
        }
        if (now - lastProgressMs > NoProgressMs)
        {
            controls.WalkVector.Set(0, 0, 0);
            failed = !CarryToExit(entity);
            return true;
        }
        // Going down and still on the ladder's top edge: stand over the hole, so the ladder takes hold.
        if (!Up && !steppingOff && !nudged && now - beganMs > NudgeMs && Math.Abs(pos.Y - highestY) < 0.2)
        {
            nudged = true;
            entity.TeleportToDouble(Column.X + 0.5, pos.Y, Column.Z + 0.5);
        }

        // Fell off on the way up: stop, so the walk is planned again from where it landed.
        highestY = Math.Max(highestY, pos.Y);
        if (Up && highestY - pos.Y > 1.5)
        {
            controls.WalkVector.Set(0, 0, 0);
            Fell = failed = true;
            return true;
        }

        bool atHeight = Up ? pos.Y >= Exit.Y - 0.05 : pos.Y <= Exit.Y + 0.3 && entity.OnGround;
        if (atHeight && !steppingOff)
        {
            steppingOff = true;
            lastProgressMs = now;
        }

        // Towards the ladder while climbing; towards the exit spot when stepping off.
        double tx = steppingOff ? Exit.X : Column.X + 0.5, tz = steppingOff ? Exit.Z : Column.Z + 0.5;
        double dx = tx - pos.X, dz = tz - pos.Z;
        double dist = Math.Sqrt(dx * dx + dz * dz);
        // Done only once it stands on the exit block, not while it still hangs at the ladder's top.
        if (steppingOff && dist < 0.35 && Math.Abs(pos.Y - Exit.Y) < (Up ? 0.3 : 0.8) && (!Up || entity.OnGround))
        {
            controls.WalkVector.Set(0, 0, 0);
            return true;
        }
        if (steppingOff && now - lastProgressMs > NoProgressMs)
        {
            failed = !CarryToExit(entity);
            return true;
        }
        // Never slower than half speed: easing off close to the ladder left a settler creeping on its top edge.
        double sideways = dist < 0.05 ? 0 : Math.Max(0.5, Math.Min(1, dist)) * speed / dist;
        double vertical = steppingOff ? (Up ? speed * 0.5 : 0) : (Up ? speed : -speed);
        controls.WalkVector.Set(dx * sideways, vertical, dz * sideways);
        if (dist > 0.05) pos.Yaw = (float)Math.Atan2(dx, dz);
        return false;
    }

    /// <summary>
    /// The climb got nowhere: when the far end is still a spot to stand on and no more than a short ladder
    /// away, the settler is set down there, as if it had climbed. False when the far end is no good.
    /// </summary>
    bool CarryToExit(EntityAgent entity)
    {
        if (Fell || ToPlace != null) return false;
        if (Math.Abs(Exit.Y - entity.Pos.Y) > 24 || Horizontal(Exit, entity.Pos.XYZ) > 4) return false;
        var feet = new BlockPos((int)Math.Floor(Exit.X), (int)Math.Floor(Exit.Y + 0.01), (int)Math.Floor(Exit.Z), entity.Pos.Dimension);
        var ba = entity.World.BlockAccessor;
        if (entity.World.CollisionTester.IsColliding(ba, entity.CollisionBox, new Vec3d(Exit.X, Exit.Y + 0.01, Exit.Z), false)) return false;
        var below = feet.DownCopy();
        var floor = ba.GetBlock(below).GetCollisionBoxes(ba, below);
        if (floor == null || floor.Length == 0) return false;
        entity.Controls.WalkVector.Set(0, 0, 0);
        entity.Pos.Motion.Set(0, 0, 0);
        entity.TeleportToDouble(Exit.X, Exit.Y + 0.01, Exit.Z);
        Carried = true;
        return true;
    }
}
