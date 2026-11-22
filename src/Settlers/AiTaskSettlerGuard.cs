using System;
using System.Collections.Generic;
using System.Linq;
using CreateAColony.Colonies;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace CreateAColony.Settlers;

/// <summary>
/// A guard's day. With a place at a Guard Tower it does what the tower says: stands guard at its spot,
/// walks its patrol, or follows a player as a bodyguard. Without one it follows the worker it escorts,
/// or stays by the Town Hall. Either way it fights the monsters that come near, answers the colony's
/// alarm when a monster hurts a settler, falls back to its tower when badly hurt (if the tower says to),
/// and swaps in better weapons, armor and shields from its tower and the warehouse when it has a quiet moment.
/// </summary>
public class AiTaskSettlerGuard : SettlerTaskBase
{
    const float ThinkSeconds = 0.5f;
    const float Reach = 2.4f;
    /// <summary>An escorting guard keeps within this distance of its worker.</summary>
    const float FollowDistance = 4f;
    /// <summary>A guard at the Town Hall wanders no further than this from it.</summary>
    const float PostDistance = 10f;
    /// <summary>A guard on the Guard task goes back once it is further than this from its spot.</summary>
    const float HoldDistance = 3f;
    const float SwingSeconds = 0.6f;

    enum Mode { Idle, Fight, Follow, Post, FetchGear, Hold, Patrol, Bodyguard, Retreat, Rally, Collect }

    /// <summary>The bow or sling in a fight (0.178.0); shooting happens only while shootReady.</summary>
    readonly RangedShooter shooter = new();
    bool shootingRanged;
    bool shootReady;
    bool steppingBack;
    Entity? looseAmmo;
    readonly HashSet<long> unreachableAmmo = new();
    float ammoLookIn;

    Mode mode;
    Entity? threat;
    bool answeringAlarm;
    Entity? protectee;
    BlockPos? gearChest;
    Vec3d? walkingTo;
    float thinkIn;
    float cooldown;
    float swingLeft;
    string? swingAnim;
    int patrolIndex;
    float patrolPause;
    bool retreating;
    int towerId;

    public AiTaskSettlerGuard(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig) : base(entity, taskConfig, aiConfig) { }

    public override bool ShouldExecute() => manager != null && Citizen is { JobCode: Jobs.Guard } c && !manager.IsBrokenDown(c) && Colony != null;

    public override void StartExecute()
    {
        base.StartExecute();
        thinkIn = 0;
        walkingTo = null;
        mode = Mode.Idle;
    }

    public override bool CanContinueExecute() => true;

    public override bool ContinueExecute(float dt)
    {
        var colony = Colony;
        var citizen = Citizen;
        if (colony == null || citizen == null || citizen.JobCode != Jobs.Guard || !citizen.Alive || manager!.IsBrokenDown(citizen)) return false;

        cooldown -= dt;
        patrolPause -= dt;
        if (swingAnim != null && (swingLeft -= dt) <= 0)
        {
            StopAnimation(swingAnim);
            swingAnim = null;
        }
        if ((thinkIn -= dt) <= 0)
        {
            thinkIn = ThinkSeconds;
            Think(colony, citizen);
        }

        shooter.Tick(entity, dt);
        if (walkingTo != null)
        {
            if (Arrived || PathFailed)
            {
                bool failed = PathFailed;
                StopWalking();
                walkingTo = null;
                if (mode == Mode.FetchGear) TakeGear(colony, citizen);
                if (mode == Mode.Patrol) ReachedPatrolPoint(colony);
                if (mode == Mode.Collect) ReachedAmmo(colony, citizen, failed);
            }
            else
            {
                KeepWalking();
            }
        }

        if (mode == Mode.Fight && threat != null && shootingRanged)
        {
            // Fighting XP for each shot; a kill counts in ColonyManager.OnShotKill.
            if (!shootReady || Hand is not { } bow) shooter.Cancel(entity, Hand, SyncHand);
            else if (shooter.Update(manager!, colony, citizen, entity, bow, threat, dt, SyncHand)) manager!.GainWorkXp(colony, citizen, Jobs.Guard, 1);
        }
        else
        {
            shooter.Cancel(entity, Hand, SyncHand);
            if (mode == Mode.Fight && threat != null && !steppingBack && cooldown <= 0 && entity.Pos.DistanceTo(threat.Pos.XYZ) <= Reach) Strike(colony, citizen, threat);
        }
        return true;
    }

    /// <summary>Picks what to do: retreat, fight, fetch gear, or the tower's task (or escort and Town Hall watch without a tower).</summary>
    void Think(Colony colony, CitizenRecord citizen)
    {
        var m = manager!;
        var tower = m.EnsureGuardTower(colony, citizen);
        towerId = tower?.BuildingId ?? 0;
        shootingRanged = shootReady = steppingBack = false;
        // A ranged guard's bow or sling, if it has something to shoot (Colony militia tab).
        var ranged = citizen.GuardRanged ? m.ReadyRanged(citizen, Hand?.Itemstack) : null;

        if (tower != null && Retreats(tower))
        {
            HoldBestWeapon(colony, citizen);
            mode = Mode.Retreat;
            threat = null;
            Walk(tower.HutPos.ToVec3d().Add(0.5, 1, 0.5), 2f);
            return;
        }

        // Who or what the guard looks after, and how far around it monsters are fought. A raised Rally Banner
        // that calls this guard's post comes first.
        var rallier = m.RallyFor(colony, tower, entity);
        EntityPlayer? player = rallier ?? (tower?.GuardTask == GuardTasks.Follow ? m.FollowedPlayer(tower) : null);
        // A tower on Patrol mine sends its guard with the miner of the mine that took it on its Guards tab.
        CitizenRecord? escorted = rallier != null ? null : tower == null ? m.EscortedBy(colony, citizen)
            : tower.GuardTask == GuardTasks.PatrolMine ? m.MinerGuardedBy(colony, citizen) : null;
        protectee = player ?? (escorted == null ? null : entity.World.GetEntityById(escorted.EntityId));
        Vec3d center;
        float radius = m.Config.GuardThreatRadius;
        float farther = m.TraitAdd(citizen, "guardRange"); // Sharp-eyed
        if (protectee != null) center = protectee.Pos.XYZ;
        else if (tower == null) center = colony.TownHallPos.ToVec3d().Add(0.5, 0, 0.5);
        else if (tower.GuardTask == GuardTasks.Patrol) center = entity.Pos.XYZ;
        else
        {
            center = m.GuardHome(tower);
            radius = m.GuardRadius(tower);
        }
        radius = Math.Max(1, radius + farther);

        var near = m.FindThreat(center, radius, tower) ?? m.FindThreat(entity.Pos.XYZ, Math.Max(1, m.Config.GuardThreatRadius + farther), tower);
        var alarm = near == null ? m.AlarmFor(colony, entity, tower) : null;
        threat = near ?? alarm;
        // The shield goes up in a fight, and down again after (so a light can show in the dark).
        m.SetShieldUp(citizen, entity, threat != null);
        if (threat != null)
        {
            if (alarm != null && !answeringAlarm && tower != null) m.AddStat(tower, "alarms");
            answeringAlarm = alarm != null;
            mode = Mode.Fight;
            double distance = entity.Pos.DistanceTo(threat.Pos.XYZ);
            if (ranged != null && distance > m.Config.RangedCloseRange)
            {
                // Shoot from where it stands if it can; walk closer when out of reach or something is in the way.
                Hold(colony, citizen, ranged);
                shootingRanged = true;
                shootReady = distance <= m.RangeOf(ranged) && m.ClearShot(entity, threat);
                if (shootReady)
                {
                    StopWalking();
                    walkingTo = null;
                }
                else Walk(threat.Pos.XYZ, Math.Min(m.RangeOf(ranged) / 2, 8f));
                return;
            }
            bool melee = HoldBestWeapon(colony, citizen);
            if (ranged != null && !melee)
            {
                // Too close for a bow and no blade to draw: step back to shoot again.
                steppingBack = true;
                var away = entity.Pos.XYZ - threat.Pos.XYZ;
                away.Y = 0;
                if (away.Length() < 0.1) away.Set(1, 0, 0);
                Walk(entity.Pos.XYZ + away.Normalize() * 5, 1f);
                return;
            }
            if (distance > Reach) Walk(threat.Pos.XYZ, Reach - 0.6f);
            return;
        }
        answeringAlarm = false;
        // No fight: a ranged guard keeps its bow or sling in hand, the others their best weapon.
        var bow = citizen.GuardRanged ? m.BestRanged(citizen, Hand?.Itemstack) : null;
        if (bow != null) Hold(colony, citizen, bow);
        else HoldBestWeapon(colony, citizen);

        // A quiet moment: pick up arrows from the fight, then a better weapon, armor or shield. An escort stays with its worker instead.
        if (escorted == null && player == null)
        {
            if (bow != null && FindLooseAmmo(colony))
            {
                mode = Mode.Collect;
                Walk(looseAmmo!.Pos.XYZ, 1.2f);
                return;
            }
            var chest = m.GearChest(colony, tower, citizen, Hand?.Itemstack);
            if (chest != null)
            {
                mode = Mode.FetchGear;
                gearChest = chest;
                Walk(chest.ToVec3d().Add(0.5, 0, 0.5), 1.5f);
                return;
            }
            if (bow != null && m.AmmoCount(citizen, bow) < m.Config.GuardAmmoLow) m.EnsureAmmoRequest(colony, tower, bow);
        }

        if (rallier != null && tower != null)
        {
            Bodyguard(rallier, tower, teleport: false);
            mode = Mode.Rally;
            return;
        }

        if (tower == null)
        {
            if (protectee != null)
            {
                mode = Mode.Follow;
                if (entity.Pos.DistanceTo(center) > FollowDistance) Walk(center, FollowDistance - 1.5f);
                return;
            }
            mode = Mode.Post;
            if (entity.Pos.DistanceTo(center) > PostDistance) Walk(center, PostDistance / 2);
            return;
        }

        switch (tower.GuardTask)
        {
            case GuardTasks.Follow when player != null:
                Bodyguard(player, tower);
                break;
            case GuardTasks.Patrol:
                Patrol(colony, tower);
                break;
            case GuardTasks.PatrolMine when protectee != null:
                mode = Mode.Follow;
                if (entity.Pos.DistanceTo(center) > FollowDistance) Walk(center, FollowDistance - 1.5f);
                if (protectee is EntityAgent && m.MineGuardedBy(colony, citizen) is { } mine && entity.Pos.DistanceTo(center) < FollowDistance) PatrolStat(m, mine);
                break;
            default:
                mode = Mode.Hold;
                var home = m.GuardHome(tower);
                if (entity.Pos.DistanceTo(home) > HoldDistance) Walk(home, 1.5f);
                break;
        }
    }

    /// <summary>True while the guard is too hurt to fight and should be at its tower; counts each retreat.</summary>
    bool Retreats(Building tower)
    {
        var m = manager!;
        var health = entity.WatchedAttributes.GetTreeAttribute("health");
        float max = health?.GetFloat("maxhealth") ?? 0;
        float share = max <= 0 ? 1 : health!.GetFloat("currenthealth") / max;
        // Brave guards hold on longer, Cowardly and Shaken ones run sooner (trait retreatHealth).
        float nerve = Citizen is { } c ? m.TraitAdd(c, "retreatHealth") : 0f;
        float retreatAt = Math.Clamp(m.Config.GuardRetreatHealth + nerve, 0.05f, 0.95f);
        float returnAt = Math.Max(retreatAt, m.Config.GuardReturnHealth + nerve);
        bool now = tower.Retreat && (retreating ? share < returnAt : share < retreatAt);
        if (now && !retreating) m.AddStat(tower, "retreats");
        retreating = now;
        if (now) m.GuardsRetreating.Add(entity.EntityId);
        else m.GuardsRetreating.Remove(entity.EntityId);
        return now;
    }

    /// <summary>Keeps near the player, loose or tight; a player far away (a long ride, a teleport) gets the guard brought to them.</summary>
    void Bodyguard(EntityPlayer player, Building tower, bool teleport = true)
    {
        var m = manager!;
        mode = Mode.Bodyguard;
        float keep = tower.FollowTight ? m.Config.GuardFollowTight : m.Config.GuardFollowLoose;
        double distance = entity.Pos.DistanceTo(player.Pos.XYZ);
        // Rallied guards run to the player; only a player in another dimension (a mine, say) gets them brought along.
        if (teleport && distance > m.Config.GuardFollowTeleport || entity.Pos.Dimension != player.Pos.Dimension)
        {
            var spot = m.FreeSpotNear(entity, player.Pos.AsBlockPos, 3);
            if (spot != null)
            {
                StopWalking();
                walkingTo = null;
                entity.TeleportToDouble(spot.X, spot.Y, spot.Z);
            }
            return;
        }
        if (distance > keep) Walk(player.Pos.XYZ, System.Math.Max(1.5f, keep - 1.5f));
    }

    /// <summary>Walks the patrol route point by point, looking around a few seconds at each.</summary>
    void Patrol(Colony colony, Building tower)
    {
        mode = Mode.Patrol;
        if (walkingTo != null || patrolPause > 0) return;
        var route = manager!.PatrolRoute(colony, tower);
        if (route.Count == 0) return;
        patrolIndex %= route.Count;
        Walk(route[patrolIndex], 2f);
    }

    double minePatrolCounted;

    /// <summary>Counts each in-game hour a guard spends by its miner, for the mine's Statistics tab.</summary>
    void PatrolStat(ColonyManager m, Building mine)
    {
        double hours = entity.World.Calendar.TotalHours;
        if (hours - minePatrolCounted < 1) return;
        if (minePatrolCounted > 0) m.AddStat(mine, "guard-hours");
        minePatrolCounted = hours;
    }

    void ReachedPatrolPoint(Colony colony)
    {
        patrolIndex++;
        patrolPause = manager!.Config.GuardPatrolPauseSeconds;
        if (colony.Buildings.Find(b => b.BuildingId == towerId) is { } tower) manager.AddStat(tower, "patrol-points");
    }

    /// <summary>Walks somewhere, planning a new path only when the goal has moved since the last one.</summary>
    void Walk(Vec3d to, float range)
    {
        if (walkingTo != null && walkingTo.SquareDistanceTo(to) < 9) return;
        walkingTo = to.Clone();
        WalkTo(to, range);
    }

    void Strike(Colony colony, CitizenRecord citizen, Entity target)
    {
        StopWalking();
        walkingTo = null;
        entity.Pos.Yaw = (float)System.Math.Atan2(target.Pos.X - entity.Pos.X, target.Pos.Z - entity.Pos.Z);

        var hand = Hand;
        var weapon = hand?.Itemstack is { } held && ColonyManager.IsWeapon(held) ? held : null;
        float damage = weapon != null ? ColonyManager.WeaponDamage(weapon) : manager!.Config.GuardFistDamage;
        damage *= manager!.DamageFactor(colony, citizen); // Fighting and Strength skills
        damage += manager.TraitAdd(citizen, "damageAdd"); // Veteran
        target.ReceiveDamage(new DamageSource
        {
            Source = EnumDamageSource.Entity,
            SourceEntity = entity,
            CauseEntity = entity,
            Type = weapon != null ? EnumDamageType.SlashingAttack : EnumDamageType.BluntAttack,
            DamageTier = weapon?.Collectible.ToolTier ?? 0
        }, damage);
        if (!target.Alive && colony.Buildings.Find(b => b.BuildingId == towerId) is { } tower) manager!.AddStat(tower, "kill:" + target.Code.Path);
        if (!target.Alive) manager!.CountKill(colony, citizen); // towards Veteran
        // Fighting XP for the hit, four hits' worth more for the kill.
        manager!.GainWorkXp(colony, citizen, Jobs.Guard, target.Alive ? 1 : 5);
        if (weapon != null && hand != null)
        {
            manager!.WearTool(entity, hand);
            SyncHand();
        }

        if (swingAnim != null) StopAnimation(swingAnim);
        swingAnim = PlayShapeAnimation(hand?.Itemstack?.Collectible.GetHeldTpHitAnimation(hand, entity) ?? "breakhand", "breakhand");
        swingLeft = SwingSeconds;
        cooldown = manager!.Config.GuardSecondsPerHit * manager.WorkTimeFactor(colony, citizen) / manager.SwingFactor(colony, citizen); // Fighting skill and traits swing faster
    }

    /// <summary>Holds the hardest-hitting melee weapon it carries. True when one is in hand after.</summary>
    bool HoldBestWeapon(Colony colony, CitizenRecord citizen)
    {
        var hand = Hand;
        if (hand == null) return false;
        var best = manager!.CarriedItems(citizen).Where(ColonyManager.IsWeapon).OrderByDescending(ColonyManager.WeaponDamage).FirstOrDefault();
        bool holding = hand.Itemstack is { } held && ColonyManager.IsWeapon(held);
        if (best == null) return holding;
        if (holding && ColonyManager.WeaponDamage(hand.Itemstack!) >= ColonyManager.WeaponDamage(best)) return true;
        Hold(colony, citizen, best);
        return hand.Itemstack is { } now && ColonyManager.IsWeapon(now);
    }

    /// <summary>Takes this weapon (or one just like it) out of its inventory into its hand, putting away what it held.</summary>
    void Hold(Colony colony, CitizenRecord citizen, ItemStack want)
    {
        var hand = Hand;
        if (hand == null || hand.Itemstack?.Collectible.Code.Equals(want.Collectible.Code) == true) return;
        var weapon = manager!.TakeFromPack(colony, citizen, s => s.Collectible.Code.Equals(want.Collectible.Code), 1);
        if (weapon == null) return;
        shooter.Cancel(entity, hand, SyncHand);
        PutAwayHand(colony, citizen);
        if (!hand.Empty)
        {
            manager.AddToPack(colony, citizen, weapon);
            return;
        }
        hand.Itemstack = weapon;
        SyncHand();
    }

    /// <summary>Looks (every few seconds) for an arrow or stone the colony shot lying near the guard. True when it found one.</summary>
    bool FindLooseAmmo(Colony colony)
    {
        if (looseAmmo is { Alive: true } && !unreachableAmmo.Contains(looseAmmo.EntityId)) return true;
        looseAmmo = null;
        if ((ammoLookIn -= ThinkSeconds) > 0) return false;
        ammoLookIn = 3f;
        looseAmmo = manager!.LooseAmmoNear(colony, entity, manager.Config.ArrowPickupRadius, unreachableAmmo);
        return looseAmmo != null;
    }

    /// <summary>At the arrow: picks it up if it is within reach, else leaves it be from now on.</summary>
    void ReachedAmmo(Colony colony, CitizenRecord citizen, bool failed)
    {
        mode = Mode.Idle;
        var loose = looseAmmo;
        looseAmmo = null;
        if (loose == null || !loose.Alive) return;
        double dx = loose.Pos.X - entity.Pos.X, dz = loose.Pos.Z - entity.Pos.Z, dy = loose.Pos.Y - entity.Pos.Y;
        if (dx * dx + dz * dz <= 2.5 * 2.5 && dy > -1.5 && dy < 3 && manager!.PickUpAmmo(colony, citizen, entity, loose))
        {
            ammoLookIn = 0; // look for the next one straight away
            return;
        }
        if (failed || loose.Alive) unreachableAmmo.Add(loose.EntityId);
    }

    /// <summary>At the chest it walked to: swaps in a better weapon, armor and shield, leaving the old ones there.</summary>
    void TakeGear(Colony colony, CitizenRecord citizen)
    {
        mode = Mode.Idle;
        if (gearChest == null || entity.World.BlockAccessor.GetBlockEntity(gearChest) is not BlockEntityContainer chest) return;
        var m = manager!;
        if (Hand is { } hand)
        {
            // Only a weapon belongs in a guard's hand.
            if (hand.Itemstack is { } held && !ColonyManager.IsWeapon(held) && !ColonyManager.IsRangedWeapon(held)) PutAwayHand(colony, citizen);
            // Only a weapon belongs in a guard's hand: a bow or sling counts for a ranged guard.
            if (m.TakeBetterWeapon(colony, citizen, chest, hand)) SyncHand();
            m.TakeRangedGear(colony, citizen, chest, hand);
            SyncHand();
        }
        if (m.Config.GuardsWearArmor) m.PutOnArmorFrom(colony, citizen, chest);
        m.TakeBetterShield(colony, citizen, chest);
    }

    public override void FinishExecute(bool cancelled)
    {
        if (Citizen is { } citizen) manager?.SetShieldUp(citizen, entity, false);
        shooter.Stop(entity, Hand, SyncHand);
        if (swingAnim != null) StopAnimation(swingAnim);
        swingAnim = null;
        walkingTo = null;
        base.FinishExecute(cancelled);
    }

    public override string Describe(Colony colony, CitizenRecord citizen)
    {
        var m = manager!;
        return mode switch
        {
            Mode.Fight when shootingRanged => Lang.Get("createacolony:activity-guard-shoot", threat?.GetName() ?? ""),
            Mode.Fight => Lang.Get(answeringAlarm ? "createacolony:activity-guard-alarm" : "createacolony:activity-guard-fight", threat?.GetName() ?? ""),
            Mode.Rally => Lang.Get("createacolony:activity-guard-rally", protectee?.GetName() ?? ""),
            Mode.Collect => Lang.Get("createacolony:activity-guard-arrows"),
            Mode.Follow => Lang.Get("createacolony:activity-guard-escort", (m.EscortedBy(colony, citizen) ?? m.MinerGuardedBy(colony, citizen))?.Name ?? ""),
            Mode.FetchGear => Lang.Get("createacolony:activity-guard-gear"),
            Mode.Hold => Lang.Get("createacolony:activity-guard-hold"),
            Mode.Patrol => Lang.Get("createacolony:activity-guard-patrol"),
            Mode.Bodyguard => Lang.Get("createacolony:activity-guard-bodyguard", protectee?.GetName() ?? ""),
            Mode.Retreat => Lang.Get("createacolony:activity-guard-retreat"),
            _ => Lang.Get("createacolony:activity-guard-post")
        };
    }
}
