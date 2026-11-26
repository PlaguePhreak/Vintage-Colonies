using System.Linq;
using CreateAColony.Settlers;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;

namespace CreateAColony.Colonies;

/// <summary>
/// Test settings (0.196.0, Town Hall Settings > Town): "Settlers can't be hurt" and "No enemies", for leaving a test
/// colony running. No enemies is world-wide while any colony has it on: hostile creatures (Config.GuardTargets, and
/// raiders) are not spawned, any already loaded are removed, and raids stop.
/// </summary>
public partial class ColonyManager
{
    bool testSafetyStarted;

    /// <summary>True while any colony has No enemies on.</summary>
    public bool NoEnemiesOn => colonies.Values.Any(c => c.NoEnemies);

    /// <summary>The settler's colony has "Settlers can't be hurt" on.</summary>
    public bool SettlerUnhurt(EntitySettler settler) => settler.ColonyId is { } id && GetById(id) is { SettlersUnhurt: true };

    void StartTestSafety()
    {
        if (testSafetyStarted) return;
        testSafetyStarted = true;
        sapi.Event.OnTrySpawnEntity += OnTrySpawnEnemy;
        sapi.Event.OnEntitySpawn += OnEnemyAppears;
        sapi.Event.OnEntityLoaded += OnEnemyAppears;
        sapi.Event.RegisterGameTickListener(_ => { if (NoEnemiesOn) ClearEnemies(); }, 10000, 5000);
    }

    bool IsEnemyCode(AssetLocation? code) => code != null && Config.GuardTargets.Any(p => WildcardUtil.Match(p, code.Path));

    bool IsEnemy(Entity e) => e.Alive && e is not EntityPlayer && e is not EntitySettler
        && (e is EntityBandit || IsEnemyCode(e.Code) || RaidOf(e) != null);

    bool OnTrySpawnEnemy(IBlockAccessor blockAccessor, ref EntityProperties properties, Vec3d spawnPosition, long herdId) =>
        !(NoEnemiesOn && IsEnemyCode(properties?.Code));

    void OnEnemyAppears(Entity entity)
    {
        if (!NoEnemiesOn || !IsEnemy(entity)) return;
        sapi.Event.EnqueueMainThreadTask(() => { if (entity.Alive) entity.Die(EnumDespawnReason.Removed); }, "createacolony-noenemies");
    }

    /// <summary>Removes every loaded hostile creature.</summary>
    void ClearEnemies()
    {
        foreach (var e in sapi.World.LoadedEntities.Values.ToList())
        {
            if (IsEnemy(e)) e.Die(EnumDespawnReason.Removed);
        }
    }

    /// <summary>Ends a raid quietly: its raiders go, it counts as neither won nor lost, and the next one is planned afresh.</summary>
    void CallOffRaid(Colony colony, ActiveRaid raid)
    {
        foreach (long id in raid.Raiders.ToList())
        {
            if (sapi.World.GetEntityById(id) is { Alive: true } e) e.Die(EnumDespawnReason.Removed);
        }
        raids.Remove(colony.ColonyId);
        colony.NextRaidDay = 0;
        MarkDirty(colony);
    }

    /// <summary>The Town Hall's test switches: Text "unhurt|1", "enemies|1" or "report|1", 0 for off.</summary>
    public string? SetTestSafety(Colony colony, string? text)
    {
        var parts = (text ?? "").Split('|');
        if (parts.Length != 2) return "createacolony:bad-request";
        bool on = parts[1] == "1";
        switch (parts[0])
        {
            case "unhurt": colony.SettlersUnhurt = on; break;
            case "enemies":
                colony.NoEnemies = on;
                if (on) ClearEnemies();
                break;
            case "report": colony.AutoReport = on; break;
            default: return "createacolony:bad-request";
        }
        return null;
    }
}
