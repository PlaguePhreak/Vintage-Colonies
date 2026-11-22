using System;
using System.Collections.Generic;
using System.Linq;
using CreateAColony.Settlers;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;

namespace CreateAColony.Colonies;

/// <summary>
/// Guards: settlers with the Guard job. A guard with a place at a Guard Tower lives there and does what
/// the tower is set to (stand guard, patrol, or follow a player), keeps watch at night and sleeps by day.
/// Guards without a tower escort the workers of huts set to "Escort by guard", in hut order, or stay by
/// the Town Hall. Every guard fights with the best weapon it can find, wears the best armor and carries the best shield, and comes
/// running when a monster hurts a settler inside the colony.
/// </summary>
public partial class ColonyManager
{
    static readonly HashSet<EnumTool> Weapons = new()
    {
        EnumTool.Sword, EnumTool.Spear, EnumTool.Club, EnumTool.Mace, EnumTool.Warhammer, EnumTool.Poleaxe,
        EnumTool.Halberd, EnumTool.Pike, EnumTool.Polearm, EnumTool.Knife
    };

    public static bool IsWeapon(ItemStack stack) => stack.Collectible.Tool is { } tool && Weapons.Contains(tool);

    /// <summary>How hard a weapon hits; the better of two weapons is the one that hits harder.</summary>
    public static float WeaponDamage(ItemStack stack) => stack.Collectible.GetAttackPower(stack);

    /// <summary>Guard places a Barracks has for each level.</summary>
    public const int BarracksGuardsPerLevel = 2;

    /// <summary>
    /// Buildings guards are stationed at: Guard Towers and Barracks. Both work the same way (Guard, Patrol or
    /// Follow, the Guard Scepter, the Hostiles and Minimum stock tabs); a Barracks holds more guards and
    /// watches and patrols farther. "Tower" in the guard code means either.
    /// </summary>
    public static bool IsGuardPost(string type) => type is BuildingTypes.GuardTower or BuildingTypes.Barracks;

    /// <summary>Places for guards: one for each Guard Tower level, two for each Barracks level, and as many as for level 1 while only the hut block stands.</summary>
    public static int GuardSlots(Building tower) => Math.Max(1, tower.Level) * (tower.Type == BuildingTypes.Barracks ? BarracksGuardsPerLevel : 1);

    /// <summary>How far around its guard spot a post's guards fight monsters on the Guard task.</summary>
    public float GuardRadius(Building tower) => tower.Type == BuildingTypes.Barracks ? Config.BarracksRadius : Config.GuardTowerRadius;

    /// <summary>
    /// The guard's tower or Barracks, giving it a place at one with room if it has none (towers first). Null
    /// when every one is full or there is none.
    /// </summary>
    public Building? EnsureGuardTower(Colony colony, CitizenRecord guard)
    {
        if (guard.JobCode != Jobs.Guard) return null;
        if (TowerOf(colony, guard) is { } post) return post;
        return EnsureWorkplace(colony, guard, BuildingTypes.GuardTower) ?? EnsureWorkplace(colony, guard, BuildingTypes.Barracks);
    }

    /// <summary>The guard's tower or Barracks if it has one, without assigning.</summary>
    public Building? TowerOf(Colony colony, CitizenRecord guard) =>
        guard.WorkplacePos != null && GetBuilding(colony, guard.WorkplacePos) is { } tower && IsGuardPost(tower.Type) ? tower : null;

    /// <summary>Workers whose hut asks for an escort, each paired with a guard that has no tower, in order.</summary>
    List<(CitizenRecord Guard, CitizenRecord Worker, Building Hut)> Escorts(Colony colony)
    {
        var guards = colony.Citizens.Where(c => c.Alive && !c.IsDowned && c.JobCode == Jobs.Guard && TowerOf(colony, c) == null).OrderBy(c => c.CitizenId).ToList();
        var pairs = new List<(CitizenRecord, CitizenRecord, Building)>();
        foreach (var hut in colony.Buildings.Where(b => b.Escort && GatherHuts.ContainsKey(b.Type)).OrderBy(b => b.BuildingId))
        {
            if (pairs.Count >= guards.Count) break;
            if (WorkerAt(colony, hut, GatherHuts[hut.Type].Job) is { } worker) pairs.Add((guards[pairs.Count], worker, hut));
        }
        return pairs;
    }

    /// <summary>The worker this guard escorts, or null when it guards the Town Hall or works from a tower.</summary>
    public CitizenRecord? EscortedBy(Colony colony, CitizenRecord guard) =>
        Escorts(colony).Find(p => p.Guard == guard).Worker;

    /// <summary>The guard escorting this hut's worker, or null.</summary>
    public CitizenRecord? GuardOf(Colony colony, Building hut) =>
        Escorts(colony).Find(p => p.Hut == hut).Guard;

    public int GuardCount(Colony colony) => colony.Citizens.Count(c => c.Alive && c.JobCode == Jobs.Guard);

    /// <summary>A living creature guards fight: one of the GuardTargets in the config.</summary>
    public bool IsThreat(Entity e) =>
        e.Alive && e is EntityAgent && e is not EntityPlayer && e is not EntitySettler
        && (e is EntityBandit || Config.GuardTargets.Any(pattern => WildcardUtil.Match(pattern, e.Code.Path)) || RaidOf(e) != null);

    /// <summary>A threat this tower's guards fight: not one switched off on its Hostiles tab.</summary>
    public bool IsThreatFor(Building? tower, Entity e) => IsThreat(e) && (tower == null || !tower.HostilesOff.Contains(e.Code.Path));

    /// <summary>The nearest threat within the radius of a spot, or null.</summary>
    public Entity? FindThreat(Vec3d around, float radius, Building? tower = null) =>
        sapi.World.GetNearestEntity(around, radius, radius / 2, e => IsThreatFor(tower, e));

    public Entity? FindThreat(Vec3d around) => FindThreat(around, Config.GuardThreatRadius);

    List<string>? hostileTypes;

    /// <summary>Every creature type guards fight, by entity code path, for the Hostiles tab.</summary>
    public List<string> HostileTypes() => hostileTypes ??= sapi.World.EntityTypes
        .Where(t => t.Code != null && Config.GuardTargets.Any(p => WildcardUtil.Match(p, t.Code.Path)))
        .Select(t => t.Code.Path).Distinct().OrderBy(c => c).ToList();

    /// <summary>A container holding a weapon: the guard's tower storage first, then the warehouse.</summary>
    public BlockPos? WeaponChest(Colony colony, Building? tower) => GearContainers(colony, tower)
        .FirstOrDefault(c => c.Inventory.Any(s => !s.Empty && IsWeapon(s.Itemstack)))?.Pos;

    public BlockPos? WarehouseWeapon(Colony colony) => WeaponChest(colony, null);

    /// <summary>Where guards look for weapons and armor: their tower's storage, then the warehouse.</summary>
    public IEnumerable<Vintagestory.GameContent.BlockEntityContainer> GearContainers(Colony colony, Building? tower) =>
        (tower == null ? Enumerable.Empty<Vintagestory.GameContent.BlockEntityContainer>() : HutStorage(colony, tower))
        .Concat(WarehouseContainers(colony));

    /// <summary>At a Guard Tower couriers take away everything but weapons, armor, shields and what the Minimum stock tab keeps.</summary>
    System.Func<ItemStack, bool> GuardTowerJunk(Building tower) => s => !IsWeapon(s) && !IsArmor(s) && !IsShield(s) && !IsRangedWeapon(s) && !IsAmmo(s) && !IsMinStock(tower, s);

    // ---- Where guards go ------------------------------------------------------------------

    /// <summary>Where a tower's guards stand on the Guard task: the spot set with the Guard Scepter, or beside the tower.</summary>
    public Vec3d GuardHome(Building tower) => (tower.GuardSpot ?? tower.HutPos).ToVec3d().Add(0.5, tower.GuardSpot != null ? 0 : 1, 0.5);

    /// <summary>
    /// The points a tower's guards walk on the Patrol task: the ones set with the Guard Scepter, or (on
    /// Automatic) the tower and every hut within patrol range of it, in a ring around the tower.
    /// </summary>
    public List<Vec3d> PatrolRoute(Colony colony, Building tower)
    {
        if (tower.PatrolManual && tower.PatrolPoints.Count > 0) return tower.PatrolPoints.Select(p => p.ToVec3d().Add(0.5, 0, 0.5)).ToList();
        var t = tower.HutPos;
        int range = tower.Type == BuildingTypes.Barracks ? Config.BarracksPatrolRange : Config.GuardPatrolRange;
        var route = colony.Buildings
            .Where(b => b != tower && Math.Abs(b.HutPos.X - t.X) <= range && Math.Abs(b.HutPos.Z - t.Z) <= range)
            .OrderBy(b => Math.Atan2(b.HutPos.Z - t.Z, b.HutPos.X - t.X))
            .Select(b => b.HutPos.ToVec3d().Add(0.5, 1, 0.5))
            .ToList();
        route.Insert(0, t.ToVec3d().Add(0.5, 1, 0.5));
        return route;
    }

    /// <summary>The player a tower's guards follow on the Follow task, if they are online and in this world.</summary>
    public EntityPlayer? FollowedPlayer(Building tower) =>
        tower.FollowPlayerUid.Length > 0 && sapi.World.PlayerByUid(tower.FollowPlayerUid)?.Entity is { Alive: true } player ? player : null;

    // ---- Night watch ----------------------------------------------------------------------

    /// <summary>True when this settler should be asleep: at night, or by day for a guard on night watch.</summary>
    public bool IsSleepTime(Colony colony, CitizenRecord citizen, float hour)
    {
        if (OnNightWatch(colony, citizen)) return InHours(hour, Config.GuardSleepStartHour, Config.GuardSleepEndHour);
        // Early Riser, Night Owl and Heavy Sleeper shift the hours.
        float start = (Config.SleepStartHour + TraitAdd(citizen, "sleepStart") + 24) % 24;
        float end = (Config.SleepEndHour + TraitAdd(citizen, "sleepEnd") + 24) % 24;
        return InHours(hour, start, end);
    }

    static bool InHours(float hour, float start, float end) => start > end ? hour >= start || hour < end : hour >= start && hour < end;

    /// <summary>A guard with a tower keeps watch at night and sleeps by day.</summary>
    public bool OnNightWatch(Colony colony, CitizenRecord citizen) =>
        Config.GuardNightWatch && citizen.JobCode == Jobs.Guard && TowerOf(colony, citizen) != null;

    /// <summary>Tower guards stay out in temporal storms to fight.</summary>
    public bool StaysOutInStorms(Colony colony, CitizenRecord citizen) =>
        Config.GuardsFightInStorms && citizen.JobCode == Jobs.Guard && TowerOf(colony, citizen) != null;

    // ---- Alarms and calls to arms ---------------------------------------------------------

    /// <summary>Monsters that hurt a settler inside the colony, by colony: entity id -> when the alarm runs out (ms).</summary>
    readonly Dictionary<string, Dictionary<long, long>> alarms = new();
    /// <summary>Guards falling back to their tower right now, by entity id. Not saved.</summary>
    public readonly HashSet<long> GuardsRetreating = new();
    readonly Dictionary<long, (long Until, bool Called)> guardCalledCache = new();

    /// <summary>A monster hurt a settler: if it happened in or near the colony, or near one of its guards, guards are called.</summary>
    void RaiseAlarm(Colony colony, Entity attacker, Vec3d where)
    {
        // Anywhere on the colony's land (every building's square), or a little past its edge.
        int margin = Config.GuardAlarmMargin;
        if (!ClaimAreas(colony).Any(a => where.X >= a.X1 - margin && where.X <= a.X2 + margin && where.Z >= a.Z1 - margin && where.Z <= a.Z2 + margin)
            && !GuardNear(colony, where, Config.FleeGuardRadius)) return;
        if (!alarms.TryGetValue(colony.ColonyId, out var list)) alarms[colony.ColonyId] = list = new();
        list[attacker.EntityId] = sapi.World.ElapsedMilliseconds + (long)(Config.GuardAlarmSeconds * 1000);
    }

    /// <summary>True when one of the colony's guards, up and about, is within the radius of a spot (out with a worker, say).</summary>
    bool GuardNear(Colony colony, Vec3d where, double radius) => colony.Citizens.Any(c =>
        c.Alive && !c.IsDowned && c.JobCode == Jobs.Guard
        && sapi.World.GetEntityById(c.EntityId) is EntityAgent { Alive: true } e && e.Pos.SquareDistanceTo(where) <= radius * radius);

    /// <summary>Living monsters the colony's alarm is out for.</summary>
    IEnumerable<Entity> AlarmThreats(Colony colony)
    {
        if (!alarms.TryGetValue(colony.ColonyId, out var list)) yield break;
        long now = sapi.World.ElapsedMilliseconds;
        foreach (var (id, until) in list.ToList())
        {
            var e = sapi.World.GetEntityById(id);
            if (until < now || e == null || !e.Alive)
            {
                list.Remove(id);
                continue;
            }
            yield return e;
        }
    }

    /// <summary>
    /// A monster from the alarm this guard should go after: one it is the nearest free guard to. Guards
    /// following a player, retreating, or switched off for that monster are not called.
    /// </summary>
    public Entity? AlarmFor(Colony colony, EntityAgent guardEntity, Building? tower)
    {
        if (tower?.GuardTask == GuardTasks.Follow || GuardsRetreating.Contains(guardEntity.EntityId) || RallyFor(colony, tower, guardEntity) != null) return null;
        foreach (var threat in AlarmThreats(colony))
        {
            if (!IsThreatFor(tower, threat)) continue;
            double mine = guardEntity.Pos.SquareDistanceTo(threat.Pos.XYZ);
            bool nearest = true;
            foreach (var other in colony.Citizens)
            {
                if (!other.Alive || other.IsDowned || other.JobCode != Jobs.Guard || other.EntityId == guardEntity.EntityId) continue;
                if (sapi.World.GetEntityById(other.EntityId) is not EntityAgent { Alive: true } e || GuardsRetreating.Contains(e.EntityId)) continue;
                var otherTower = TowerOf(colony, other);
                if (otherTower?.GuardTask == GuardTasks.Follow || !IsThreatFor(otherTower, threat) || RallyFor(colony, otherTower, e) != null) continue;
                if (e.Pos.SquareDistanceTo(threat.Pos.XYZ) < mine) { nearest = false; break; }
            }
            if (nearest) return threat;
        }
        return null;
    }

    /// <summary>
    /// True when a guard should leave its meal, bed or shelter: a monster is close, or the alarm calls it.
    /// Worked out at most once a second per guard.
    /// </summary>
    public bool GuardCalled(EntityAgent entity, Colony? colony, CitizenRecord? citizen)
    {
        if (colony == null || citizen == null || citizen.JobCode != Jobs.Guard || GuardsRetreating.Contains(entity.EntityId)) return false;
        long now = sapi.World.ElapsedMilliseconds;
        if (guardCalledCache.TryGetValue(entity.EntityId, out var cached) && cached.Until > now) return cached.Called;
        var tower = TowerOf(colony, citizen);
        bool called = FindThreat(entity.Pos.XYZ, Config.GuardThreatRadius, tower) != null || AlarmFor(colony, entity, tower) != null;
        guardCalledCache[entity.EntityId] = (now + 1000, called);
        return called;
    }

    /// <summary>
    /// Whether a guard gets up from (or does not go to) bed: only for a monster close by on its own level, or an
    /// alarm from a settler being hurt. A monster further off, or in a cave below, does not keep it up all night.
    /// </summary>
    public bool GuardWoken(EntityAgent entity, Colony? colony, CitizenRecord? citizen)
    {
        if (colony == null || citizen == null || citizen.JobCode != Jobs.Guard || GuardsRetreating.Contains(entity.EntityId)) return false;
        long now = sapi.World.ElapsedMilliseconds;
        if (guardWokenCache.TryGetValue(entity.EntityId, out var cached) && cached.Until > now) return cached.Woken;
        var tower = TowerOf(colony, citizen);
        float radius = Math.Max(1, Config.GuardWakeRadius);
        bool woken = sapi.World.GetNearestEntity(entity.Pos.XYZ, radius, 3, e => IsThreatFor(tower, e)) != null
            || AlarmFor(colony, entity, tower) != null;
        guardWokenCache[entity.EntityId] = (now + 1000, woken);
        return woken;
    }

    readonly Dictionary<long, (long Until, bool Woken)> guardWokenCache = new();

    /// <summary>Called for every blow a settler takes, before it lands: armor softens it, monsters raise the alarm, towers count it.</summary>
    public float OnSettlerHurt(EntitySettler settler, DamageSource source, float damage)
    {
        var colony = settler.ColonyId == null ? null : GetById(settler.ColonyId);
        var citizen = colony?.Citizens.Find(c => c.CitizenId == settler.CitizenId);
        if (colony == null || citizen == null) return damage;

        var attacker = source.CauseEntity ?? source.SourceEntity;
        if (attacker != null && IsThreat(attacker)) RaiseAlarm(colony, attacker, settler.Pos.XYZ);
        if (IsAttackerToFlee(attacker) && (FleesWhenHurt(citizen) || FightsBack(colony, citizen, settler))) StartFleeing(colony, citizen, settler, attacker!);
        if (IsAttackerToFlee(attacker) && citizen.JobCode != Jobs.Guard) CallAllies(colony, citizen, settler, attacker!);
        if (IsAttackerToFlee(attacker) && damage > 0) FeelAttacked(colony, citizen, settler, attacker!);

        damage = ShieldBlock(colony, citizen, settler, source, damage);
        damage = ArmorAbsorb(colony, citizen, settler, source, damage);
        if (citizen.JobCode == Jobs.Guard && TowerOf(colony, citizen) is { } tower && damage > 0)
        {
            AddStat(tower, "damage-taken", (int)Math.Ceiling(damage));
        }
        return damage;
    }

    // ---- Tower window ---------------------------------------------------------------------

    /// <summary>Lines for a Guard Tower window: who is stationed there, what they do, and what each is up to.</summary>
    string GuardTowerInfo(Colony colony, Building tower)
    {
        var lines = new List<string>();
        var guards = WorkersAt(colony, tower);
        lines.Add(Lang.Get("createacolony:tower-guards", guards.Count, GuardSlots(tower)));
        string task = tower.GuardTask switch
        {
            GuardTasks.Patrol => Lang.Get(tower.PatrolManual && tower.PatrolPoints.Count > 0 ? "createacolony:tower-doing-patrol-manual" : "createacolony:tower-doing-patrol-auto",
                tower.PatrolPoints.Count),
            GuardTasks.Follow => Lang.Get("createacolony:tower-doing-follow", sapi.World.PlayerByUid(tower.FollowPlayerUid)?.PlayerName ?? "?"),
            _ => Lang.Get(tower.GuardSpot != null ? "createacolony:tower-doing-guard-spot" : "createacolony:tower-doing-guard")
        };
        lines.Add(task);
        if (guards.Count == 0) lines.Add(Lang.Get("createacolony:tower-no-guards"));
        lines.Add("");
        lines.Add(Lang.Get("createacolony:hint-" + tower.Type));
        return string.Join("<br>", lines);
    }
}
