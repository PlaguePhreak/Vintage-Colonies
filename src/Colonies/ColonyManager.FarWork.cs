using System.Collections.Generic;
using System.Linq;
using CreateAColony.Settlers;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace CreateAColony.Colonies;

/// <summary>
/// Keeps foresters, farmers and guards going when they work far from every player, with their hut loaded. The game only
/// runs creatures near players and unloads the land no one is near, which would freeze a forester
/// 300 blocks out. So while such a settler is far from everyone, it stays active and the chunk columns
/// around it stay loaded; once a player comes near or it walks home, they are let go again. One whose
/// land was unloaded anyway (the world was reloaded while it was out) has the land where it was last seen
/// loaded again, so it comes back where it was instead of being respawned at the Town Hall.
/// </summary>
public partial class ColonyManager
{
    const int FarWorkTickMs = 1000;
    /// <summary>Chunk columns kept loaded around a far settler in each direction: 1 keeps a 3 by 3 block of them.</summary>
    const int FarKeepColumns = 1;

    readonly HashSet<Vec2i> keptColumns = new();
    /// <summary>Columns no settler needs any more, unloaded once no player is near them either.</summary>
    readonly HashSet<Vec2i> releasingColumns = new();

    static bool WorksFarOut(string? job) => job is Jobs.Forester or Jobs.Farmer or Jobs.Guard or Jobs.Prospector or Jobs.Miner or Jobs.Quarrier or Jobs.Explorer or Jobs.Gatherer or Jobs.Hunter;

    /// <summary>
    /// True for a settler that keeps going while no player is near. The outdoor jobs always do; with config
    /// AllJobsWorkFarOut (Aaron, 0.202.0, on by default) every grown settler with a job does: builders, couriers, cooks
    /// and crafters too.
    /// </summary>
    bool KeepsWorkingFarOut(Colony colony, CitizenRecord citizen) =>
        WorksFarOut(citizen.JobCode)
        || Config.AllJobsWorkFarOut && !string.IsNullOrEmpty(citizen.JobCode) && !citizen.IsChild && !citizen.Visitor
        || colony.Teardowns.Any(t => t.BuilderCitizenId == citizen.CitizenId);

    void OnFarWorkTick(float dt)
    {
        var players = sapi.World.AllOnlinePlayers.Select(p => p.Entity).Where(e => e != null).ToList();
        var want = new HashSet<Vec2i>();
        int size = GlobalConstants.ChunkSize;
        // With nobody online the world is left to rest, as the game does.
        if (players.Count > 0)
        {
            foreach (var colony in colonies.Values)
            {
                // With every job working, the Town Hall and the Warehouse stay loaded too while the players are away:
                // couriers, builders and crafters all go there.
                if (Config.AllJobsWorkFarOut && colony.Citizens.Any(c => c.Alive && !string.IsNullOrEmpty(c.JobCode) && !c.Visitor))
                {
                    if (FarFromPlayers(players, colony.TownHallPos.ToVec3d(), GlobalConstants.DefaultSimulationRange)) KeepAround(want, colony.TownHallPos.ToVec3d(), size);
                    if (colony.WarehousePos is { } warehouse && FarFromPlayers(players, warehouse.ToVec3d(), GlobalConstants.DefaultSimulationRange)) KeepAround(want, warehouse.ToVec3d(), size);
                }
                foreach (var citizen in colony.Citizens)
                {
                    if (!citizen.Alive || citizen.IsDowned || !KeepsWorkingFarOut(colony, citizen)) continue;
                    // The worker plans every trip from its hut and takes what it gathered there, so a hut no player is
                    // near stays loaded too; without it the worker finds no hut storage and stops.
                    if (citizen.WorkplacePos is { } hutPos && FarFromPlayers(players, hutPos.ToVec3d(), GlobalConstants.DefaultSimulationRange))
                    {
                        KeepAround(want, hutPos.ToVec3d(), size, 0);
                    }
                    if (sapi.World.GetEntityById(citizen.EntityId) is not EntitySettler entity || !entity.Alive)
                    {
                        // Not loaded: its land was unloaded (after the world was reloaded, say). Load the land where
                        // it was last seen so it comes back and carries on, rather than being given up as missing.
                        if (citizen.LastSeenPos is { } last && FarFromPlayers(players, last, GlobalConstants.DefaultSimulationRange)) KeepAround(want, last, size);
                        continue;
                    }
                    bool far = FarFromPlayers(players, entity.Pos.XYZ, entity.SimulationRange);
                    if (entity.AlwaysActive != far) entity.AlwaysActive = far;
                    if (!far) continue;
                    // The game stops running a creature once no player is within its simulation range, and AlwaysActive
                    // alone does not wake one it has already stopped (one loaded with its land, or one a player left
                    // behind quickly), so wake it as the game's own always-active creatures are.
                    if (entity.State != EnumEntityState.Active)
                    {
                        var before = entity.State;
                        entity.State = EnumEntityState.Active;
                        entity.OnStateChanged(before);
                    }
                    KeepAround(want, entity.Pos.XYZ, size);
                }
            }
        }

        foreach (var column in want)
        {
            releasingColumns.Remove(column);
            if (!keptColumns.Add(column)) continue;
            sapi.WorldManager.LoadChunkColumnPriority(column.X, column.Y, new ChunkLoadOptions { KeepLoaded = true });
        }
        foreach (var column in keptColumns.Where(c => !want.Contains(c)).ToList())
        {
            keptColumns.Remove(column);
            releasingColumns.Add(column);
        }

        // A column is only unloaded when no player is near it, so no one sees land vanish.
        long releaseSq = (long)(Config.FarWorkPlayerDistance + 2 * size) * (Config.FarWorkPlayerDistance + 2 * size);
        foreach (var column in releasingColumns.ToList())
        {
            var center = new Vec3d(column.X * size + size / 2, 0, column.Y * size + size / 2);
            if (players.Any(p => HorizontalDistanceSq(p.Pos.XYZ, center) <= releaseSq)) continue;
            sapi.WorldManager.UnloadChunkColumn(column.X, column.Y);
            releasingColumns.Remove(column);
        }
    }

    /// <summary>
    /// True when no player is within FarWorkPlayerDistance, measured as the game measures its simulation range
    /// (straight-line, so a miner deep under a player counts as far), and kept inside that range so a settler
    /// is made always-active before the game would stop it.
    /// </summary>
    bool FarFromPlayers(List<EntityPlayer> players, Vec3d pos, int simulationRange)
    {
        double distance = System.Math.Min(Config.FarWorkPlayerDistance, simulationRange - 16);
        return players.All(p => p.Pos.SquareDistanceTo(pos) > distance * distance);
    }

    static void KeepAround(HashSet<Vec2i> want, Vec3d pos, int size, int columns = FarKeepColumns)
    {
        int cx = (int)pos.X / size, cz = (int)pos.Z / size;
        for (int dx = -columns; dx <= columns; dx++)
        for (int dz = -columns; dz <= columns; dz++)
        {
            want.Add(new Vec2i(cx + dx, cz + dz));
        }
    }

    static double HorizontalDistanceSq(Vec3d a, Vec3d b)
    {
        double dx = a.X - b.X, dz = a.Z - b.Z;
        return dx * dx + dz * dz;
    }
}
