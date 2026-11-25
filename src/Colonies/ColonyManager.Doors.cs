using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using CreateAColony.Settlers;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.Essentials;
using Vintagestory.GameContent;

namespace CreateAColony.Colonies;

/// <summary>
/// Settlers go through doors and fence gates. The game's pathfinder treats a closed door as a wall, so
/// settlers walked around buildings or gave up. While it plans a path for a settler it now counts a door
/// or gate that can be opened by hand as open; the settler opens it when it gets there and closes it
/// again once nobody is in the way.
/// </summary>
public partial class ColonyManager
{
    const int DoorTickMs = 500;
    /// <summary>A door a settler opened stays open at least this long, so the settler gets into it.</summary>
    const long DoorOpenMinMs = 600;
    /// <summary>A door is shut again at the latest after this long, even if the settler that opened it turned back.</summary>
    const long DoorOpenMaxMs = 8000;
    /// <summary>The settler is through once it is this far (blocks, level) from the door's middle.</summary>
    const double DoorPassedDistance = 1.4;

    /// <summary>Doors and gates settlers opened, by position, with when and by whom.</summary>
    readonly Dictionary<BlockPos, (long OpenedMs, long ByEntityId)> doorsOpened = new();
    Harmony? harmony;

    void StartDoors()
    {
        harmony = new Harmony("createacolony.doors");
        PathBlocks.Init(sapi.World, Config);
        StepCheck.Init(Config);
        // These hook into the game's pathfinder by name. If a game update renames what they hook, that one
        // helper is left off (with a warning) and the rest of the mod keeps working.
        TryPatch("door pathing", () => DoorPathing.Patch(harmony));
        TryPatch("Pathblock discounts", () => PathBlocks.Patch(harmony));
        TryPatch("step checks", () => StepCheck.Patch(harmony));
        sapi.Event.RegisterGameTickListener(OnDoorTick, DoorTickMs);
    }

    void TryPatch(string what, Action patch)
    {
        try { patch(); }
        catch (Exception e) { Mod.Logger.Warning("Could not hook {0} into the pathfinder, so it is off: {1}", what, e.Message); }
    }

    public override void Dispose()
    {
        harmony?.UnpatchAll("createacolony.doors");
        harmony = null;
        StopFires();
        StopCropWatch();
        base.Dispose();
    }

    /// <summary>Opens the closed doors and gates just ahead of a walking settler.</summary>
    public void OpenDoorsAhead(Entity settler)
    {
        DoorPathing.Register(settler);
        var ba = sapi.World.BlockAccessor;
        var pos = settler.Pos;
        var motion = pos.Motion;
        bool moving = motion.X * motion.X + motion.Z * motion.Z > 1e-6;
        const double reach = 0.8;
        var box = settler.CollisionBox;
        int x1 = (int)Math.Floor(pos.X + box.X1 - reach), x2 = (int)Math.Floor(pos.X + box.X2 + reach);
        int z1 = (int)Math.Floor(pos.Z + box.Z1 - reach), z2 = (int)Math.Floor(pos.Z + box.Z2 + reach);
        int y = (int)Math.Floor(pos.Y + 0.1);
        var cell = new BlockPos(pos.Dimension);
        for (int x = x1; x <= x2; x++)
        for (int z = z1; z <= z2; z++)
        {
            // Only doors the settler is walking towards, not ones it passes alongside.
            if (moving && (x + 0.5 - pos.X) * motion.X + (z + 0.5 - pos.Z) * motion.Z < 0) continue;
            for (int dy = 0; dy <= 1; dy++)
            {
                cell.Set(x, y + dy, z);
                if (DoorPathing.ClosedDoorAt(ba, cell) is not { } door) continue;
                if (DoorPathing.SetOpen(sapi.World, door, true)) doorsOpened[door.Copy()] = (sapi.World.ElapsedMilliseconds, settler.EntityId);
                else if (PathDebug && doorsLogged.Add(door.Copy()))
                {
                    Mod.Logger.Notification("[doors] {0} could not open the door at {1}: {2}", Name(settler), door, DoorPathing.LastError ?? "it did not change");
                }
            }
        }
    }

    /// <summary>Doors a settler could not open, so the log names each one once.</summary>
    readonly HashSet<BlockPos> doorsLogged = new();

    /// <summary>
    /// A settler that keeps bumping into something right by a door or gate is set down on the far side of it
    /// (0.200.0). In the overnight test settlers stood in the Residence's inner doorway for days, bumping, and
    /// two went hungry behind it. Whatever is wrong with that door, a settler that has walked up to a doorway
    /// gets through it. False when there is no door within a step, or no spot to stand on beyond it.
    /// </summary>
    public bool HelpThroughDoor(EntityAgent settler, Vec3d goal)
    {
        var ba = sapi.World.BlockAccessor;
        var pos = settler.Pos;
        int y = (int)Math.Floor(pos.Y + 0.1);
        int sx = (int)Math.Floor(pos.X), sz = (int)Math.Floor(pos.Z);
        var cell = new BlockPos(pos.Dimension);
        Vec3d? best = null;
        BlockPos? through = null;
        double bestDist = double.MaxValue;
        for (int x = sx - 1; x <= sx + 1; x++)
        for (int z = sz - 1; z <= sz + 1; z++)
        for (int dy = 0; dy <= 1; dy++)
        {
            cell.Set(x, y + dy, z);
            if (DoorPathing.DoorAt(ba, cell) is not { } door) continue;
            double cx = door.X + 0.5, cz = door.Z + 0.5;
            double fromX = pos.X - cx, fromZ = pos.Z - cz;
            bool inDoorway = Math.Abs(fromX) < 0.5 && Math.Abs(fromZ) < 0.5;
            if (!inDoorway && fromX * fromX + fromZ * fromZ > 1.6 * 1.6) continue;
            foreach (var face in BlockFacing.HORIZONTALS)
            {
                // Beyond the door: the side the settler is not on (any side, for one standing in the doorway).
                if (!inDoorway && face.Normali.X * fromX + face.Normali.Z * fromZ > -0.3) continue;
                foreach (int step in new[] { 0, -1, 1 })
                {
                    var feet = new BlockPos(door.X + face.Normali.X, door.Y + step, door.Z + face.Normali.Z, pos.Dimension);
                    if (!CanStandAt(settler, feet)) continue;
                    var spot = new Vec3d(feet.X + 0.5, feet.Y, feet.Z + 0.5);
                    double dist = spot.SquareDistanceTo(goal);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        best = spot;
                        through = door.Copy();
                    }
                    break;
                }
            }
        }
        if (best == null || through == null) return false;
        // Only a step that leads on: not back to where it came from.
        if (best.SquareDistanceTo(goal) >= pos.XYZ.SquareDistanceTo(goal) && pos.XYZ.SquareDistanceTo(best) < 0.5) return false;
        if (PathDebug) Mod.Logger.Notification("[doors] {0} kept bumping at the door at {1} and was set down beyond it, at {2}", Name(settler), through, best.AsBlockPos);
        settler.Pos.Motion.Set(0, 0, 0);
        settler.TeleportToDouble(best.X, best.Y, best.Z);
        return true;
    }

    /// <summary>
    /// Shuts each door or gate a settler opened as soon as that settler has walked through it (or away from it),
    /// once no settler or player stands in the doorway. Doors players left open are not touched.
    /// </summary>
    void OnDoorTick(float dt)
    {
        if (doorsOpened.Count == 0) return;
        long now = sapi.World.ElapsedMilliseconds;
        foreach (var (door, (openedAt, byId)) in doorsOpened.ToList())
        {
            if (now - openedAt < DoorOpenMinMs) continue;
            if (now - openedAt < DoorOpenMaxMs && !SettlerPassed(door, byId)) continue;
            if (sapi.World.BlockAccessor.GetChunkAtBlockPos(door) == null)
            {
                doorsOpened.Remove(door);
                continue;
            }
            if (DoorBlocked(door)) continue;
            DoorPathing.SetOpen(sapi.World, door, false);
            doorsOpened.Remove(door);
        }
    }

    /// <summary>True once the settler that opened the door is through it: a step past it, or gone.</summary>
    bool SettlerPassed(BlockPos door, long entityId)
    {
        if (sapi.World.GetEntityById(entityId) is not { Alive: true } settler) return true;
        double dx = settler.Pos.X - (door.X + 0.5), dz = settler.Pos.Z - (door.Z + 0.5);
        return dx * dx + dz * dz >= DoorPassedDistance * DoorPassedDistance;
    }

    /// <summary>
    /// True while a settler or player is in the doorway or within a step of it, so closing would hit it or shut the door
    /// in the face of a settler about to walk through. (Only right next to it, the door used to close on settlers
    /// that had slowed down to turn into it, and they walked into the closed door.)
    /// </summary>
    bool DoorBlocked(BlockPos door)
    {
        const double margin = 0.9;
        var area = new Cuboidd(door.X - margin, door.Y, door.Z - margin, door.X + 1 + margin, door.Y + 2, door.Z + 1 + margin);
        // Only people count: a door is shut in a monster's face rather than left open for it.
        return sapi.World.GetEntitiesAround(door.ToVec3d().Add(0.5, 0.5, 0.5), 3, 3, e => e is EntitySettler or EntityPlayer && e.Alive)
            .Any(e => e.CollisionBox.ToDouble().Translate(e.Pos.X, e.Pos.Y, e.Pos.Z).Intersects(area));
    }
}

/// <summary>
/// The game's pathfinder sees doors through their collision boxes. While it plans for a settler, these
/// patches leave out the boxes of closed doors and gates that open by hand, so the path goes through them.
/// The pathfinder runs on its own thread, so "planning for a settler" is kept per thread.
/// </summary>
public static class DoorPathing
{
    static readonly ConditionalWeakTable<Cuboidf, object> settlerBoxes = new();
    static readonly object marker = new();
    [ThreadStatic] static bool planning;

    /// <summary>True on the pathfinder's thread while it plans a path for a settler.</summary>
    public static bool Planning => planning;
    static MethodInfo? openOldDoor;

    /// <summary>The pathfinder is given the walker's own collision box, which is how a settler's search is told apart.</summary>
    public static void Register(Entity settler) => settlerBoxes.AddOrUpdate(settler.CollisionBox, marker);

    public static void Patch(Harmony harmony)
    {
        openOldDoor = AccessTools.Method(typeof(BlockBaseDoor), "Open");
        harmony.Patch(AccessTools.Method(typeof(AStar), nameof(AStar.FindPathOrEscapePath)),
            prefix: new HarmonyMethod(typeof(DoorPathing), nameof(PlanStart)),
            finalizer: new HarmonyMethod(typeof(DoorPathing), nameof(PlanEnd)));
        harmony.Patch(AccessTools.Method(typeof(BlockBehaviorDoor), nameof(BlockBehaviorDoor.GetCollisionBoxes)),
            postfix: new HarmonyMethod(typeof(DoorPathing), nameof(DoorBoxes)));
        harmony.Patch(AccessTools.Method(typeof(BlockBehaviorDoor), nameof(BlockBehaviorDoor.MBGetCollisionBoxes)),
            postfix: new HarmonyMethod(typeof(DoorPathing), nameof(DoorPartBoxes)));
        // Fence gates and old-style doors are blocks of their own, with the game's plain collision boxes.
        harmony.Patch(AccessTools.Method(typeof(Block), nameof(Block.GetCollisionBoxes)),
            prefix: new HarmonyMethod(typeof(DoorPathing), nameof(GateBoxes)));
    }

    static void PlanStart(Cuboidf entityCollBox) => planning = entityCollBox != null && settlerBoxes.TryGetValue(entityCollBox, out _);

    static void PlanEnd() => planning = false;

    static void DoorBoxes(BlockBehaviorDoor __instance, IBlockAccessor blockAccessor, BlockPos pos, ref Cuboidf[] __result)
    {
        if (planning && __instance.handopenable && blockAccessor.GetBlockEntity(pos)?.GetBehavior<BEBehaviorDoor>() is { Opened: false }) __result = null!;
    }

    static void DoorPartBoxes(BlockBehaviorDoor __instance, IBlockAccessor blockAccessor, BlockPos pos, Vec3i offset, ref Cuboidf[] __result)
    {
        if (!planning || !__instance.handopenable) return;
        if (blockAccessor.GetBlockEntity(pos.AddCopy(offset.X, offset.Y, offset.Z))?.GetBehavior<BEBehaviorDoor>() is { Opened: false }) __result = null!;
    }

    static bool GateBoxes(Block __instance, ref Cuboidf[] __result)
    {
        if (!planning || __instance is not BlockBaseDoor door || door.IsOpened()) return true;
        __result = null!;
        return false;
    }

    /// <summary>Why the last door or gate could not be opened or shut (for the walking log), or null.</summary>
    public static string? LastError;

    /// <summary>The position of a door or gate at pos, open or closed (a door's lower block), or null.</summary>
    public static BlockPos? DoorAt(IBlockAccessor ba, BlockPos pos)
    {
        var block = ba.GetBlock(pos);
        var at = pos;
        if (block is BlockMultiblock part)
        {
            at = pos.AddCopy(part.OffsetInv.X, part.OffsetInv.Y, part.OffsetInv.Z);
            block = ba.GetBlock(at);
        }
        if (block is BlockBaseDoor) return at.Copy();
        return block.GetBehavior<BlockBehaviorDoor>() != null && ba.GetBlockEntity(at)?.GetBehavior<BEBehaviorDoor>() != null ? at.Copy() : null;
    }

    /// <summary>The position of a closed door or gate a settler can open at pos (a door's lower block), or null.</summary>
    public static BlockPos? ClosedDoorAt(IBlockAccessor ba, BlockPos pos)
    {
        var block = ba.GetBlock(pos);
        var at = pos;
        if (block is BlockMultiblock part)
        {
            at = pos.AddCopy(part.OffsetInv.X, part.OffsetInv.Y, part.OffsetInv.Z);
            block = ba.GetBlock(at);
        }
        if (block is BlockBaseDoor old) return old.IsOpened() ? null : at;
        if (block.GetBehavior<BlockBehaviorDoor>() is not { handopenable: true }) return null;
        return ba.GetBlockEntity(at)?.GetBehavior<BEBehaviorDoor>() is { Opened: false } ? at : null;
    }

    /// <summary>Opens or closes a door or gate. False if there is none there, or it already was that way.</summary>
    public static bool SetOpen(IWorldAccessor world, BlockPos pos, bool open)
    {
        var block = world.BlockAccessor.GetBlock(pos);
        LastError = null;
        try
        {
            if (block is BlockBaseDoor old)
            {
                if (old.IsOpened() == open || openOldDoor == null) return false;
                openOldDoor.Invoke(old, new object?[] { world, null, pos });
                return true;
            }
            var door = world.BlockAccessor.GetBlockEntity(pos)?.GetBehavior<BEBehaviorDoor>();
            if (door == null || door.Opened == open) return false;
            door.ToggleDoorState(null, open);
            return true;
        }
        catch (Exception e)
        {
            // A door that can break when used looks for the player who used it; a settler is not one. Leave it shut.
            LastError = (e.InnerException ?? e).Message;
            return false;
        }
    }
}
