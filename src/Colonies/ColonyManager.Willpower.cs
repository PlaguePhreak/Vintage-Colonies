using System;
using System.Collections.Generic;
using System.Linq;
using CreateAColony.Settlers;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;

namespace CreateAColony.Colonies;

/// <summary>
/// The other half of Willpower (see Settlers/Willpower.cs): checking whether a settler is boxed in, and
/// moving a settler whose Willpower is full to a safe spot nearby, with a puff of sparks and a sound.
/// </summary>
public partial class ColonyManager
{
    /// <summary>A trapped check gives up after looking at this many standing spots.</summary>
    const int TrappedSearchLimit = 80;
    /// <summary>Getting this many blocks away (sideways) from where it stands means the settler is not boxed in.</summary>
    const int TrappedEscapeDistance = 4;
    /// <summary>The spot a relocation picks is checked for being boxed in too; this many of the best are tried.</summary>
    const int RelocationCandidatesChecked = 6;
    const string RelocateSound = "game:sounds/effect/translocate-breakdown";

    long relocationSecond;
    int relocationsThisSecond;

    /// <summary>Switched by /colony pathdebug; starts as config PathDebug.</summary>
    public bool PathDebug { get; set; }

    /// <summary>Whether the body fits at a spot (feet at its y) and there is something to stand on under it.</summary>
    bool CanStandAt(Entity entity, BlockPos feet, IBlockAccessor ba)
    {
        var spot = new Vec3d(feet.X + 0.5, feet.Y, feet.Z + 0.5);
        if (sapi.World.CollisionTester.IsColliding(ba, entity.CollisionBox, spot, false)) return false;
        var below = feet.DownCopy();
        var floor = ba.GetBlock(below).GetCollisionBoxes(ba, below);
        return floor != null && floor.Length > 0;
    }

    /// <summary>
    /// True when the settler cannot walk TrappedEscapeDistance blocks away from where it stands: walls all
    /// around, a pit it cannot step out of. Closed doors count as open, settlers open them. It walks the
    /// spots around the settler like the pathfinder does (step up one block, drop down up to MaxDropHeight), but
    /// stops after TrappedSearchLimit spots, so a large closed room also counts as open.
    /// </summary>
    public bool IsTrapped(Entity entity) => IsTrapped(entity, entity.Pos.AsBlockPos);

    /// <summary>IsTrapped for a spot the entity is not standing on yet.</summary>
    bool IsTrapped(Entity entity, BlockPos start)
    {
        var ba = sapi.World.BlockAccessor;
        var seen = new HashSet<BlockPos> { start };
        var queue = new Queue<BlockPos>();
        queue.Enqueue(start);
        // Up one block, or down as far as a settler's path may drop (config MaxDropHeight).
        var heights = new[] { 0, 1 }.Concat(Enumerable.Range(1, Math.Max(1, Config.MaxDropHeight)).Select(d => -d)).ToArray();
        while (queue.Count > 0 && seen.Count < TrappedSearchLimit)
        {
            var at = queue.Dequeue();
            foreach (var face in BlockFacing.HORIZONTALS)
            {
                foreach (int dy in heights)
                {
                    var next = at.AddCopy(face.Normali.X, dy, face.Normali.Z);
                    if (seen.Contains(next)) continue;
                    if (ba.GetChunkAtBlockPos(next) == null) continue;
                    bool door = DoorPathing.ClosedDoorAt(ba, next) != null;
                    if (!door && !CanStandAt(entity, next, ba)) continue;
                    // Stepping up needs head room above where it stands now.
                    if (dy == 1 && sapi.World.CollisionTester.IsColliding(ba, entity.CollisionBox, new Vec3d(at.X + 0.5, at.Y + 1, at.Z + 0.5), false)) continue;
                    if (Math.Max(Math.Abs(next.X - start.X), Math.Abs(next.Z - start.Z)) >= TrappedEscapeDistance) return false;
                    seen.Add(next);
                    queue.Enqueue(next);
                    break;
                }
            }
        }
        return queue.Count == 0;
    }

    /// <summary>
    /// Moves a settler whose Willpower is full to a safe spot nearby, preferring spots towards where it was
    /// going and on or by Pathblocks. False when there is no spot (it tries again a second later) or the
    /// server already moved WillpowerMaxRelocationsPerSecond settlers this second.
    /// </summary>
    public bool Relocate(EntityAgent entity, Willpower willpower, CitizenRecord? citizen)
    {
        string? job = citizen?.JobCode;
        long second = sapi.World.ElapsedMilliseconds / 1000;
        if (second != relocationSecond)
        {
            relocationSecond = second;
            relocationsThisSecond = 0;
        }
        if (relocationsThisSecond >= Math.Max(1, Config.WillpowerMaxRelocationsPerSecond)) return false;

        // Now and then a settler leaps without looking. Guards never look out for monsters: they go where the fight is.
        bool reckless = sapi.World.Rand.NextDouble() < Config.WillpowerRecklessChance + (citizen == null ? 0 : TraitAdd(citizen, "reckless")); // Reckless trait
        bool mindMonsters = !reckless && job != Jobs.Guard;
        var goal = willpower.Goal ?? willpower.LastGoal;
        var from = entity.Pos.XYZ;
        // Inside a building and heading out of it: out by the front (0.200.0). Settlers who could not walk out of
        // the Residence stood in it and went hungry.
        Vec3d? spot = null;
        if (goal != null && entity is EntitySettler { ColonyId: { } colonyId } && GetById(colonyId) is { } colony && BuildingAround(colony, from) is { } inside)
        {
            var box = Footprint(inside);
            bool goalInside = goal.X >= box.X1 && goal.X <= box.X2 + 1 && goal.Z >= box.Z1 && goal.Z <= box.Z2 + 1;
            if (!goalInside) spot = FrontSpot(inside, entity, goal);
        }
        spot ??= SafeSpotNear(entity, goal, Math.Max(2, Config.WillpowerRadius), reckless, mindMonsters);
        if (spot == null)
        {
            if (PathDebug) Mod.Logger.Notification("[willpower] {0} at {1} is stuck but found no safe spot within {2} blocks", Name(entity), from.AsBlockPos, Config.WillpowerRadius);
            willpower.TryAgainLater();
            return false;
        }
        relocationsThisSecond++;

        RelocateEffect(from);
        entity.TeleportToDouble(spot.X, spot.Y, spot.Z);
        RelocateEffect(spot);
        if (PathDebug)
        {
            Mod.Logger.Notification("[willpower] {0} relocated from {1} to {2} (reason {3}{4}{5}, goal {6})", Name(entity), from.AsBlockPos, spot.AsBlockPos,
                willpower.Reason, willpower.Trapped ? ", trapped" : "", reckless ? ", reckless" : "", goal?.AsBlockPos.ToString() ?? "none");
        }
        willpower.Relocated();
        return true;
    }

    string Name(Entity entity) =>
        entity is EntitySettler s ? FindCitizen(s.ColonyId, s.CitizenId)?.Name ?? $"settler {entity.EntityId}" : entity.Code.ToShortString();

    /// <summary>
    /// The best spot within radius of the settler to relocate to, or null. A spot must fit the body and have
    /// floor under it, be at least two blocks from where the settler is, and (unless reckless) not be in or next
    /// to lava or fire, in deep water, or (when mindMonsters) near a monster from GuardTargets. Among those,
    /// the one nearest the goal wins, with a bonus for Pathblocks underfoot and a little chance mixed in.
    /// </summary>
    Vec3d? SafeSpotNear(Entity entity, Vec3d? goal, int radius, bool reckless, bool mindMonsters)
    {
        var ba = sapi.World.BlockAccessor;
        var here = entity.Pos.AsBlockPos;
        var rand = sapi.World.Rand;
        var monsters = mindMonsters
            ? sapi.World.GetEntitiesAround(entity.Pos.XYZ, radius + 8, radius + 8, e => e.Alive && IsGuardTarget(e)).Select(e => e.Pos.XYZ).ToList()
            : new List<Vec3d>();
        var candidates = new List<(Vec3d Spot, BlockPos Feet, double Score)>();
        var feet = new BlockPos(here.dimension);
        int up = Math.Max(3, radius / 2), down = Math.Max(4, radius / 2 + 2);
        // A settler on the surface stays on the surface: a goal set too low (land that was not loaded when it
        // was planned) drew prospectors into caves under it, out of sight, until they hopped back up.
        bool onSurface = here.Y >= ba.GetTerrainMapheightAt(here);

        for (int dx = -radius; dx <= radius; dx++)
        for (int dz = -radius; dz <= radius; dz++)
        {
            if (dx * dx + dz * dz > radius * radius) continue;
            if (ba.GetChunkAtBlockPos(here.X + dx, here.Y, here.Z + dz) == null) continue;
            for (int dy = up; dy >= -down; dy--)
            {
                feet.Set(here.X + dx, here.Y + dy, here.Z + dz);
                if (feet.Y < 1) break;
                if (Math.Abs(dx) <= 1 && Math.Abs(dz) <= 1 && Math.Abs(dy) <= 1) continue; // not where it is stuck
                if (!CanStandAt(entity, feet, ba)) continue;
                if (onSurface && feet.Y < ba.GetTerrainMapheightAt(feet)) continue;
                if (!reckless && Dangerous(ba, feet)) continue;
                // Never into water (0.200.0): hops towards a goal in a lake carried the gatherer further into it.
                if (ba.GetBlock(feet, BlockLayersAccess.Fluid).IsLiquid()) continue;
                var spot = new Vec3d(feet.X + 0.5, feet.Y, feet.Z + 0.5);
                if (monsters.Any(m => m.SquareDistanceTo(spot) < 8 * 8)) continue;

                // Nearest to the goal, or with no goal, a few blocks away from where it got stuck.
                double score = goal != null ? Math.Sqrt(Sq(spot.X - goal.X) + 2 * Sq(spot.Y - goal.Y) + Sq(spot.Z - goal.Z)) : Math.Abs(spot.DistanceTo(entity.Pos.XYZ) - 4);
                if (PathBlocks.IsPath(ba.GetBlock(feet.DownCopy())) || PathBlocks.IsPath(ba.GetBlock(feet))) score -= 3;
                else if (NextToPath(ba, feet)) score -= 1.5;
                score += rand.NextDouble() * 1.5;
                candidates.Add((spot, feet.Copy(), score));
            }
        }

        // The best few are checked for being boxed in, so the settler does not land in another hole.
        foreach (var c in candidates.OrderBy(c => c.Score).Take(RelocationCandidatesChecked))
        {
            if (!IsTrapped(entity, c.Feet)) return c.Spot;
        }
        return candidates.Count > 0 ? candidates.MinBy(c => c.Score).Spot : null;
    }

    static double Sq(double v) => v * v;

    bool NextToPath(IBlockAccessor ba, BlockPos feet)
    {
        foreach (var face in BlockFacing.HORIZONTALS)
        {
            var side = feet.AddCopy(face);
            if (PathBlocks.IsPath(ba.GetBlock(side)) || PathBlocks.IsPath(ba.GetBlock(side.DownCopy()))) return true;
        }
        return false;
    }

    /// <summary>Lava or fire in or right around the spot, or water over the settler's head.</summary>
    static bool Dangerous(IBlockAccessor ba, BlockPos feet)
    {
        for (int dx = -1; dx <= 1; dx++)
        for (int dy = -1; dy <= 1; dy++)
        for (int dz = -1; dz <= 1; dz++)
        {
            var p = feet.AddCopy(dx, dy, dz);
            if (Burns(ba.GetBlock(p, BlockLayersAccess.Fluid)) || Burns(ba.GetBlock(p, BlockLayersAccess.Solid))) return true;
        }
        // Deep water: water at the feet and at the head.
        return ba.GetBlock(feet, BlockLayersAccess.Fluid).IsLiquid() && ba.GetBlock(feet.UpCopy(), BlockLayersAccess.Fluid).IsLiquid();
    }

    static bool Burns(Block block) =>
        block.Id != 0 && (block.LiquidCode == "lava" || block.Code.Path.StartsWith("lava") || block.Code.Path.StartsWith("fire") || block.Code.Path.StartsWith("ember"));

    bool IsGuardTarget(Entity e) => Config.GuardTargets.Any(p => WildcardUtil.Match(new AssetLocation(p.Contains(':') ? p : "game:" + p), e.Code));

    /// <summary>A puff of purple sparks and the translocator crackle, where a settler leaves and where it lands.</summary>
    void RelocateEffect(Vec3d at)
    {
        var props = new SimpleParticleProperties(
            12, 18, ColorUtil.ToRgba(200, 160, 90, 230),
            at.AddCopy(-0.4, 0.1, -0.4), at.AddCopy(0.4, 1.8, 0.4),
            new Vec3f(-0.3f, 0.2f, -0.3f), new Vec3f(0.3f, 0.9f, 0.3f),
            1f, -0.05f, 0.15f, 0.35f, EnumParticleModel.Quad)
        {
            VertexFlags = 128,
            SizeEvolve = EvolvingNatFloat.create(EnumTransformFunction.LINEAR, -0.2f)
        };
        sapi.World.SpawnParticles(props);
        sapi.World.PlaySoundAt(new AssetLocation(RelocateSound), at.X, at.Y + 1, at.Z, null, true, 16, 0.4f);
    }

    /// <summary>With PathDebug on: sparks over a stuck settler, few and blue at first, more and red as Willpower fills.</summary>
    public void ShowWillpower(Entity entity, Willpower willpower)
    {
        if (!PathDebug || willpower.Value <= 0) return;
        double f = willpower.Fraction;
        int color = ColorUtil.ToRgba(230, (int)(60 + 190 * f), 60, (int)(250 - 190 * f));
        var top = entity.Pos.XYZ.Add(0, entity.CollisionBox.Y2 + 0.3, 0);
        var props = new SimpleParticleProperties(
            1 + (float)(f * 6), 2 + (float)(f * 8), color,
            top.AddCopy(-0.25, 0, -0.25), top.AddCopy(0.25, 0.2, 0.25),
            new Vec3f(-0.05f, 0.1f, -0.05f), new Vec3f(0.05f, 0.3f, 0.05f),
            0.8f, 0f, 0.2f, 0.3f, EnumParticleModel.Quad)
        {
            VertexFlags = 255
        };
        sapi.World.SpawnParticles(props);
    }
}
