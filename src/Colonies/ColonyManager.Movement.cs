using System.Collections.Generic;
using System;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace CreateAColony.Colonies;

/// <summary>
/// Keeping settlers out of walls: builders do not place a block where someone stands, and a settler
/// that ends up inside a block (a player built over it) is moved to the nearest free spot.
/// </summary>
public partial class ColonyManager
{
    /// <summary>A living creature whose body would end up inside this block if it were placed at pos, or null.</summary>
    public Entity? EntityInTheWay(Block block, BlockPos pos)
    {
        var boxes = block.CollisionBoxes;
        if (boxes == null || boxes.Length == 0) return null;
        var center = pos.ToVec3d().Add(0.5, 0.5, 0.5);
        foreach (var e in sapi.World.GetEntitiesAround(center, 2, 3, e => e is EntityAgent && e.Alive))
        {
            var body = BodyAt(e, e.Pos.X, e.Pos.Y, e.Pos.Z);
            if (boxes.Any(b => b.ToDouble().Translate(pos.X, pos.Y, pos.Z).Intersects(body))) return e;
        }
        return null;
    }

    /// <summary>True when the entity's body is inside the block space at pos.</summary>
    public bool Overlaps(Entity entity, BlockPos pos) =>
        BodyAt(entity, entity.Pos.X, entity.Pos.Y, entity.Pos.Z).Intersects(new Cuboidd(pos.X, pos.Y, pos.Z, pos.X + 1, pos.Y + 1, pos.Z + 1));

    /// <summary>True when the entity fits in the block space at p with floor under it.</summary>
    public bool CanStandAt(Entity entity, BlockPos p)
    {
        var ba = sapi.World.BlockAccessor;
        if (sapi.World.CollisionTester.IsColliding(ba, entity.CollisionBox, new Vec3d(p.X + 0.5, p.Y, p.Z + 0.5), false)) return false;
        var floor = ba.GetBlock(p.DownCopy()).GetCollisionBoxes(ba, p.DownCopy());
        return floor != null && floor.Length > 0;
    }

    /// <summary>
    /// A free, dry spot near a position that no other settler stands on (0.200.0), nearest the position first:
    /// settlers with nothing to do stood inside each other on one block. Null when there is none.
    /// </summary>
    public Vec3d? OwnSpotNear(Entity entity, BlockPos around, int radius)
    {
        var p = new BlockPos(around.dimension);
        int[] heights = { 0, 1, -1 };
        for (int r = 0; r <= radius; r++)
        {
            for (int dx = -r; dx <= r; dx++)
            for (int dz = -r; dz <= r; dz++)
            {
                if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r) continue;
                foreach (int dy in heights)
                {
                    p.Set(around.X + dx, around.Y + dy, around.Z + dz);
                    if (!IsDryStand(entity, p)) continue;
                    var spot = new Vec3d(p.X + 0.5, p.Y, p.Z + 0.5);
                    if (sapi.World.GetEntitiesAround(spot, 0.75f, 1.5f, e => e is Settlers.EntitySettler && e.Alive && e.EntityId != entity.EntityId).Length > 0) break;
                    return spot;
                }
            }
        }
        return null;
    }

    readonly Dictionary<BlockPos, (bool Dry, long UntilMs)> dryStands = new();

    /// <summary>
    /// True when the entity can stand on dry ground within reach of a spot (0.200.0): the gatherer leaves reeds in
    /// the middle of a lake alone. Remembered for five minutes per spot.
    /// </summary>
    public bool DryStandNear(Entity entity, BlockPos pos, int reach = 2)
    {
        long now = sapi.World.ElapsedMilliseconds;
        if (dryStands.TryGetValue(pos, out var known) && known.UntilMs > now) return known.Dry;
        if (dryStands.Count > 4000) dryStands.Clear();
        bool dry = false;
        var p = new BlockPos(pos.dimension);
        for (int dx = -reach; dx <= reach && !dry; dx++)
        for (int dz = -reach; dz <= reach && !dry; dz++)
        for (int dy = -2; dy <= 2 && !dry; dy++)
        {
            p.Set(pos.X + dx, pos.Y + dy, pos.Z + dz);
            dry = IsDryStand(entity, p);
        }
        dryStands[pos.Copy()] = (dry, now + 300_000);
        return dry;
    }

    /// <summary>A spot the entity can stand on with no water at its feet.</summary>
    public bool IsDryStand(Entity entity, BlockPos p) =>
        !sapi.World.BlockAccessor.GetBlock(p, BlockLayersAccess.Fluid).IsLiquid() && CanStandAt(entity, p);

    /// <summary>
    /// The dry standing spot for a settler in the water (0.200.0): the nearest, or of the near ones the one closest
    /// to where it was going. Null when there is none within the radius.
    /// </summary>
    public Vec3d? ShoreNear(Entity entity, Vec3d? goal, int radius)
    {
        var here = entity.Pos.AsBlockPos;
        var p = new BlockPos(here.dimension);
        Vec3d? best = null;
        double bestScore = double.MaxValue;
        int stopAt = radius;
        for (int r = 1; r <= stopAt; r++)
        {
            for (int dx = -r; dx <= r; dx++)
            for (int dz = -r; dz <= r; dz++)
            {
                if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r) continue;
                for (int dy = -1; dy <= 3; dy++)
                {
                    p.Set(here.X + dx, here.Y + dy, here.Z + dz);
                    if (!IsDryStand(entity, p)) continue;
                    var spot = new Vec3d(p.X + 0.5, p.Y, p.Z + 0.5);
                    double score = goal == null ? spot.SquareDistanceTo(entity.Pos.XYZ) : spot.SquareDistanceTo(goal);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        best = spot;
                    }
                    break;
                }
            }
            // A few rings past the first shore found, so the side it was heading for can win.
            if (best != null && stopAt == radius) stopAt = Math.Min(radius, r + 6);
        }
        return best;
    }

    /// <summary>
    /// How far from home a settler may be right now and still walk back before bedtime, in blocks (0.200.0): the
    /// time left until it sleeps, less HomeMarginHours, at HomeWalkBlocksPerSecond.
    /// </summary>
    public double HomeReach(Colony colony, CitizenRecord citizen)
    {
        var calendar = sapi.World.Calendar;
        float hour = calendar.HourOfDay;
        if (IsSleepTime(colony, citizen, hour)) return 0;
        float start = (Config.SleepStartHour + TraitAdd(citizen, "sleepStart") + 24) % 24;
        double hoursLeft = (start - hour + 24) % 24 - Config.HomeMarginHours;
        double secondsPerHour = 3600.0 / Math.Max(1, calendar.SpeedOfTime * calendar.CalendarSpeedMul);
        return Math.Max(0, hoursLeft) * secondsPerHour * Config.HomeWalkBlocksPerSecond;
    }

    /// <summary>
    /// The nearest spot around a position where the entity can stand: its body fits, there is floor under
    /// it, and it does not overlap the block at avoid. Null if there is none within the radius.
    /// </summary>
    public Vec3d? FreeSpotNear(Entity entity, BlockPos around, int radius, BlockPos? avoid = null)
    {
        var avoidBox = avoid == null ? null : new Cuboidd(avoid.X, avoid.Y, avoid.Z, avoid.X + 1, avoid.Y + 1, avoid.Z + 1);
        int[] heights = { 0, 1, -1, 2 };
        for (int r = 0; r <= radius; r++)
        {
            Vec3d? best = null;
            double bestDist = double.MaxValue;
            for (int dx = -r; dx <= r; dx++)
            for (int dz = -r; dz <= r; dz++)
            {
                if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r) continue; // only the ring at this distance
                foreach (int dy in heights)
                {
                    var p = around.AddCopy(dx, dy, dz);
                    var spot = new Vec3d(p.X + 0.5, p.Y, p.Z + 0.5);
                    if (!CanStandAt(entity, p)) continue;
                    if (avoidBox != null && BodyAt(entity, spot.X, spot.Y, spot.Z).Intersects(avoidBox)) continue;
                    double dist = spot.SquareDistanceTo(entity.Pos.XYZ);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        best = spot;
                    }
                    break; // the lowest free height in this column will do
                }
            }
            if (best != null) return best;
        }
        return null;
    }

    /// <summary>The entity's collision box at a position, a little smaller so touching a wall is not "inside" it.</summary>
    static Cuboidd BodyAt(Entity e, double x, double y, double z)
    {
        var box = e.CollisionBox.ToDouble().Translate(x, y, z);
        const double inset = 0.05;
        return new Cuboidd(box.X1 + inset, box.Y1 + inset, box.Z1 + inset, box.X2 - inset, box.Y2 - inset, box.Z2 - inset);
    }
}
