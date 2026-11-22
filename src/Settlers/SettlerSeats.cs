using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace CreateAColony.Settlers;

/// <summary>
/// Sitting down, as a player does: on a chair, stool or bench nearby when there is one, else on the floor.
/// Settlers sit while they eat and while they take a break (AiTaskSettlerEat, AiTaskSettlerIdle).
/// A seat is any block whose name says chair, stool, bench and the like (other mods' furniture too), with a
/// seat low enough to sit on and room above it. One settler sits on a seat at a time.
/// </summary>
public static class SettlerSeats
{
    static readonly string[] SeatWords = { "chair", "stool", "bench", "seat", "throne", "pew", "sofa", "couch" };
    /// <summary>Workbenches and the like are not for sitting on.</summary>
    static readonly string[] NotSeatWords = { "work", "table", "anvil", "saw", "craft", "tanning", "drafting", "carpent", "loom", "toolrack" };
    const double MinSeatHeight = 0.2;
    const double MaxSeatHeight = 0.75;

    /// <summary>Seats in use, and the settler on each.</summary>
    static readonly Dictionary<BlockPos, long> taken = new();

    public static bool IsSeat(Block block)
    {
        string path = block.Code?.Path ?? "";
        return SeatWords.Any(path.Contains) && !NotSeatWords.Any(path.Contains);
    }

    /// <summary>How high the seat is above the block's bottom, or -1 when it is no good to sit on.</summary>
    static double SeatHeight(IBlockAccessor ba, BlockPos pos, Block block)
    {
        var boxes = block.GetCollisionBoxes(ba, pos);
        if (boxes == null || boxes.Length == 0) return -1;
        double top = boxes.Max(b => b.Y2);
        return top >= MinSeatHeight && top <= MaxSeatHeight ? top : -1;
    }

    static bool Free(IBlockAccessor ba, BlockPos pos)
    {
        var boxes = ba.GetBlock(pos).GetCollisionBoxes(ba, pos);
        return boxes == null || boxes.Length == 0;
    }

    static bool TakenByOther(EntityAgent entity, BlockPos pos)
    {
        if (!taken.TryGetValue(pos, out long id) || id == entity.EntityId) return false;
        if (entity.World.GetEntityById(id) is { Alive: true } other && other.Pos.SquareDistanceTo(pos.X + 0.5, pos.Y + 0.5, pos.Z + 0.5) < 4) return true;
        taken.Remove(pos);
        return false;
    }

    /// <summary>The nearest free seat within range of a spot, or null.</summary>
    public static BlockPos? FindSeat(EntityAgent entity, BlockPos around, int range, System.Func<BlockPos, bool>? also = null)
    {
        var ba = entity.World.BlockAccessor;
        BlockPos? best = null;
        double bestDist = double.MaxValue;
        var pos = new BlockPos(around.dimension);
        for (int dx = -range; dx <= range; dx++)
        for (int dy = -2; dy <= 2; dy++)
        for (int dz = -range; dz <= range; dz++)
        {
            pos.Set(around.X + dx, around.Y + dy, around.Z + dz);
            var block = ba.GetBlock(pos);
            if (block.Id == 0 || !IsSeat(block) || SeatHeight(ba, pos, block) < 0) continue;
            if (!Free(ba, pos.UpCopy()) || !Free(ba, pos.UpCopy(2)) || TakenByOther(entity, pos)) continue;
            if (also != null && !also(pos)) continue;
            double dist = pos.DistanceSqTo(entity.Pos.X, entity.Pos.Y, entity.Pos.Z);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = pos.Copy();
            }
        }
        return best;
    }

    /// <summary>Where to walk to, to sit on a seat.</summary>
    public static Vec3d SeatSpot(BlockPos seat) => new(seat.X + 0.5, seat.Y, seat.Z + 0.5);

    /// <summary>True when the settler can sit down where it stands: on the ground, not in water or on a ladder.</summary>
    public static bool CanSitHere(EntityAgent entity) =>
        entity.OnGround && !entity.Swimming && !entity.FeetInLiquid && !entity.Controls.IsClimbing;

    /// <summary>
    /// Sits the settler on the seat, facing the way the seat faces when its block says so. False when the
    /// seat is gone or taken; the settler can sit on the floor instead.
    /// </summary>
    public static bool SitOn(EntityAgent entity, BlockPos seat)
    {
        var ba = entity.World.BlockAccessor;
        var block = ba.GetBlock(seat);
        double height = IsSeat(block) ? SeatHeight(ba, seat, block) : -1;
        if (height < 0 || TakenByOther(entity, seat)) return false;
        taken[seat.Copy()] = entity.EntityId;

        if (Facing(block) is { } facing)
        {
            float yaw = (float)Math.Atan2(facing.Normalf.X, facing.Normalf.Z);
            entity.ServerPos.Yaw = entity.Pos.Yaw = yaw;
            entity.BodyYaw = entity.BodyYawServer = yaw;
        }
        entity.TeleportToDouble(seat.X + 0.5, seat.Y + height, seat.Z + 0.5);
        entity.AnimManager.StartAnimation(SettlerAnimations.SitSeat);
        return true;
    }

    /// <summary>Sits the settler down on the floor where it stands.</summary>
    public static void SitOnFloor(EntityAgent entity) => entity.AnimManager.StartAnimation(SettlerAnimations.SitFloor);

    /// <summary>Gets the settler up again, and frees its seat.</summary>
    public static void StandUp(EntityAgent entity)
    {
        entity.AnimManager.StopAnimation(SettlerAnimations.SitSeat);
        entity.AnimManager.StopAnimation(SettlerAnimations.SitFloor);
        foreach (var pos in taken.Where(t => t.Value == entity.EntityId).Select(t => t.Key).ToList()) taken.Remove(pos);
    }

    /// <summary>The way a seat faces, from its block's orientation, if it has one.</summary>
    static BlockFacing? Facing(Block block)
    {
        foreach (string key in new[] { "side", "horizontalorientation", "orientation", "rot", "facing" })
        {
            if (block.Variant.TryGetValue(key, out var code) && BlockFacing.FromCode(code) is { IsHorizontal: true } facing) return facing;
        }
        return null;
    }
}
