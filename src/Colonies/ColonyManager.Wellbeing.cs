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
/// Three more parts of happiness (Aaron, 2026-11-21): safety, health and crowding.
///
/// Safety: settlers feel unsafe while monsters (GuardTargets) are near the Town Hall, and for a while after.
/// Being hurt by a creature, seeing a neighbour hurt, or a settler killed by a monster all scare them. Enough
/// guards for the colony's size make them feel safe; a big colony with no guard at all worries them. When
/// a guard or other settler kills a monster, everyone feels a bit safer.
///
/// Health: a hurt settler is unhappy, a badly hurt one more so. Being treated by the Healer cheers them up.
///
/// Crowding: a Residence with too little room for the people sleeping in it makes them unhappy. A home
/// shared only with family feels like their own.
/// Raids are not in the mod yet; when they come, they can add to the danger here.
/// </summary>
public partial class ColonyManager
{
    /// <summary>Until when (total in-game hours) each colony feels in danger. Not saved: a reload is a fresh start.</summary>
    readonly Dictionary<string, double> dangerUntilHours = new();

    /// <summary>When each colony last told neighbours about an attack (ms), so a fight does not run the check every blow.</summary>
    readonly Dictionary<string, long> lastWitnessMs = new();

    /// <summary>Crowding at home by citizen id, worked out at most every 30 s: the thought code and its points.</summary>
    readonly Dictionary<string, (long Ms, Dictionary<int, (string Code, int Mood)> ByCitizen)> crowdingCache = new();

    // ---- Safety -------------------------------------------------------------------------------

    /// <summary>True while monsters are, or lately were, near the colony.</summary>
    public bool InDanger(Colony colony) =>
        dangerUntilHours.TryGetValue(colony.ColonyId, out double until) && until > sapi.World.Calendar.TotalHours;

    void MarkDanger(Colony colony)
    {
        if (!Config.Happiness) return;
        dangerUntilHours[colony.ColonyId] = sapi.World.Calendar.TotalHours + Math.Max(0, Config.DangerHours);
    }

    /// <summary>Part of the happiness tick: is a monster near the Town Hall right now?</summary>
    void CheckDanger(Colony colony)
    {
        float radius = Config.DangerRadius;
        if (radius <= 0) return;
        var at = colony.TownHallPos.ToVec3d().Add(0.5, 0, 0.5);
        if (sapi.World.GetNearestEntity(at, radius, Math.Max(8, radius / 3), IsThreat) != null) MarkDanger(colony);
    }

    /// <summary>True when the colony has enough guards: one for every SettlersPerGuard grown-ups, and at least one.</summary>
    public bool WellGuarded(Colony colony, out int guards, out int adults)
    {
        guards = colony.Citizens.Count(c => c.Alive && !c.IsDowned && c.JobCode == Jobs.Guard);
        adults = colony.Citizens.Count(c => c.Alive && !c.IsChild && !c.Visitor);
        int per = Math.Max(1, Config.SettlersPerGuard);
        return guards > 0 && guards * per >= adults;
    }

    /// <summary>The standing safety thoughts: monsters about, guarded or not.</summary>
    void SafetyThoughts(Colony colony, Action<string, int> standing)
    {
        bool guarded = WellGuarded(colony, out int guards, out int adults);
        if (InDanger(colony)) standing("danger", guarded ? -2 : -6);
        if (guarded) standing("guarded", 3);
        else if (guards == 0 && Config.UnguardedFrom > 0 && adults >= Config.UnguardedFrom) standing("unguarded", -3);
    }

    /// <summary>A creature hurt a settler: it is scared, settlers close by are uneasy, and a monster puts the colony in danger.</summary>
    void FeelAttacked(Colony colony, CitizenRecord hurt, EntitySettler settler, Entity attacker)
    {
        if (!Config.Happiness) return;
        AddThought(hurt, "attacked");
        if (!IsThreat(attacker)) return;
        MarkDanger(colony);
        long now = sapi.World.ElapsedMilliseconds;
        if (lastWitnessMs.TryGetValue(colony.ColonyId, out long last) && now - last < 5000) return;
        lastWitnessMs[colony.ColonyId] = now;
        float range = Math.Max(0, Config.WitnessRadius);
        foreach (var c in colony.Citizens)
        {
            if (c == hurt || !HasMood(c) || c.IsDowned) continue;
            if (sapi.World.GetEntityById(c.EntityId) is not { Alive: true } body) continue;
            if (body.Pos.XYZ.SquareDistanceTo(settler.Pos.XYZ) <= range * range) AddThought(c, "sawattack");
        }
        MarkDirty(colony);
    }

    /// <summary>A settler was killed by a monster: the whole colony is afraid.</summary>
    public void FearMonsterKill(Colony colony, CitizenRecord dead, DamageSource? source)
    {
        var killer = source?.CauseEntity ?? source?.SourceEntity;
        if (killer == null || !IsMonsterCode(killer)) return;
        foreach (var c in colony.Citizens)
        {
            if (c != dead && HasMood(c)) AddThought(c, "monsterkill");
        }
        MarkDanger(colony);
        MarkDirty(colony);
    }

    /// <summary>A monster by its code alone (it may be dead already).</summary>
    bool IsMonsterCode(Entity e) =>
        e is EntityAgent && e is not EntityPlayer && e is not EntitySettler && e.Code != null
        && Config.GuardTargets.Any(pattern => WildcardUtil.Match(pattern, e.Code.Path));

    /// <summary>Any creature died: when a settler killed a monster, its colony feels safer.</summary>
    void OnMonsterDeath(Entity entity, DamageSource? source)
    {
        if (!Config.Happiness || source == null || !IsMonsterCode(entity)) return;
        if ((source.CauseEntity ?? source.SourceEntity) is not EntitySettler { ColonyId: { } id }) return;
        if (GetById(id) is not { } colony) return;
        foreach (var c in colony.Citizens)
        {
            if (HasMood(c)) AddThought(c, "monsterslain");
        }
        MarkDirty(colony);
    }

    // ---- Health -------------------------------------------------------------------------------

    /// <summary>The standing health thoughts: hurt, or badly hurt. Only while the settler is loaded.</summary>
    void HealthThoughts(CitizenRecord c, Action<string, int> standing)
    {
        if (c.IsDowned || sapi.World.GetEntityById(c.EntityId) is not EntityAgent { Alive: true } body) return;
        float share = HealthShare(body);
        if (share < Config.BadlyHurtMoodBelow) standing("badlyhurt", -12);
        else if (share < Config.HurtMoodBelow) standing("hurt", -5);
    }

    // ---- Crowding -----------------------------------------------------------------------------

    /// <summary>
    /// Room in a Residence: its floor (inside the banner margin) times its storeys, a storey for every 5 blocks
    /// of height.
    /// </summary>
    int HomeSpace(Building home)
    {
        var (min, max) = BuildingBox(home);
        int dx = Math.Max(1, max.X - min.X - 1), dz = Math.Max(1, max.Z - min.Z - 1);
        int floors = Math.Max(1, (max.Y - min.Y + 1) / 5);
        return dx * dz * floors;
    }

    /// <summary>Crowding for everyone with a bed in a Residence: crowded, very crowded, or a home of their own (family only).</summary>
    Dictionary<int, (string Code, int Mood)> Crowding(Colony colony)
    {
        long now = sapi.World.ElapsedMilliseconds;
        if (crowdingCache.TryGetValue(colony.ColonyId, out var cached) && now - cached.Ms < 30_000) return cached.ByCitizen;
        var result = new Dictionary<int, (string, int)>();
        var boxes = ResidenceBoxes(colony);
        var byHome = colony.Citizens
            .Where(c => c.Alive && !c.Visitor && c.HomeBedPos != null)
            .Select(c => (c, home: ResidenceAt(colony, c.HomeBedPos!, boxes)))
            .Where(p => p.home != null && p.home.Type != BuildingTypes.BoardingHouse)
            .GroupBy(p => p.home!, p => p.c);
        float need = Math.Max(1, Config.HomeSpacePerSettler);
        foreach (var group in byHome)
        {
            var people = group.ToList();
            float each = HomeSpace(group.Key) / (float)people.Count;
            bool family = people.All(a => people.All(b => a == b || SameHousehold(colony, a, b)));
            (string, int) thought = each < need / 2 ? ("verycrowded", -8)
                : each < need ? ("crowded", -4)
                : family ? ("ownhome", 2)
                : ("", 0);
            if (thought.Item2 == 0) continue;
            foreach (var p in people) result[p.CitizenId] = thought;
        }
        crowdingCache[colony.ColonyId] = (now, result);
        return result;
    }

    /// <summary>Partners, parents and children, and brothers and sisters: people happy to share a house.</summary>
    bool SameHousehold(Colony colony, CitizenRecord a, CitizenRecord b) =>
        IsFamily(colony, a, b) || (a.ParentIds.Count > 0 && a.ParentIds.Intersect(b.ParentIds).Any());

    void CrowdingThoughts(Colony colony, CitizenRecord c, Action<string, int> standing)
    {
        if (Crowding(colony).TryGetValue(c.CitizenId, out var t)) standing(t.Code, t.Mood);
    }
}
