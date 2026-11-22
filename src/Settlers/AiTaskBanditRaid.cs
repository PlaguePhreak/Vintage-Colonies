using System;
using System.Collections.Generic;
using CreateAColony.Colonies;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace CreateAColony.Settlers;

/// <summary>
/// What an outlaw does on a raid (0.181.0). In order: fights the nearest settler or player close by (a spear,
/// club or knife up close, or a bow from further off while it has arrows); runs back to where the raid gathered
/// once it has stolen something or the raid is over; sets dry grass alight with its torch (once); robs the
/// nearest chest on the colony's land; and otherwise marches on the Town Hall and prowls around it. It never
/// breaks a block.
/// </summary>
public class AiTaskBanditRaid : SettlerTaskBase
{
    const float Reach = 2.4f;
    const float SwingSeconds = 0.6f;
    const float SeeRange = 16f;
    const float ArcherRange = 24f;

    enum Goal { None, Foe, Escape, Chest, Target, Prowl }

    Goal goal;
    Entity? foe;
    BlockEntityContainer? chest;
    readonly HashSet<BlockPos> triedChests = new();
    readonly RangedShooter shooter = new();
    float cooldown;
    float swingLeft;
    string? swingAnim;
    long nextLookMs;
    long nextAimMs;
    long nextChestMs;
    long nextFireMs;
    bool fireDone;
    bool? willLight;

    public AiTaskBanditRaid(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig) : base(entity, taskConfig, aiConfig) { }

    protected override bool CountsForWillpower => false;

    EntityBandit Bandit => (EntityBandit)entity;

    ColonyManager.ActiveRaid? Raid => manager?.RaidOf(entity) is { Attacking: true } raid ? raid : null;

    public override bool ShouldExecute() => entity is EntityBandit && Raid != null;

    public override void StartExecute()
    {
        base.StartExecute();
        goal = Goal.None;
        nextLookMs = 0;
    }

    public override bool ContinueExecute(float dt)
    {
        if (Raid is not { } raid) return false;
        var m = manager!;
        long now = entity.World.ElapsedMilliseconds;
        cooldown -= dt;
        shooter.Tick(entity, dt);
        if (swingAnim != null && (swingLeft -= dt) <= 0)
        {
            StopAnimation(swingAnim);
            swingAnim = null;
        }

        bool leaving = raid.Fleeing || Bandit.HasLoot;
        if (now >= nextLookMs)
        {
            nextLookMs = now + 1000;
            var near = FindFoe(leaving ? 3f : Bandit.Archer && Bandit.ArrowsLeft > 0 ? ArcherRange : SeeRange);
            if (near != null) foe = near;
            else if (foe != null && (!foe.Alive || foe.Pos.SquareDistanceTo(entity.Pos.XYZ) > (SeeRange + 8) * (SeeRange + 8))) foe = null;
        }

        if (foe is { Alive: true }) return Fight(m, foe, dt, now);
        shooter.Cancel(entity, Hand, SyncHand);

        if (leaving)
        {
            if (goal != Goal.Escape)
            {
                goal = Goal.Escape;
                WalkTo(raid.Gather, 3);
            }
            if (Arrived || PathFailed)
            {
                m.BanditEscaped(Bandit);
                return false;
            }
            KeepWalking();
            return true;
        }

        if (TryFire(m, now)) return true;
        if (Chest(m, now)) return true;

        // March on the Town Hall, then prowl about it.
        var target = m.RaidTarget(raid);
        if (goal is not (Goal.Target or Goal.Prowl) || Arrived || PathFailed)
        {
            bool there = entity.Pos.SquareDistanceTo(target) < 12 * 12;
            if (there)
            {
                var rand = entity.World.Rand;
                goal = Goal.Prowl;
                WalkTo(target.AddCopy(rand.Next(-12, 13), 0, rand.Next(-12, 13)), 2);
            }
            else
            {
                goal = Goal.Target;
                WalkTo(target, 6);
            }
        }
        KeepWalking();
        return true;
    }

    /// <summary>The nearest living settler, or player in survival, within range.</summary>
    Entity? FindFoe(float range) => entity.World.GetNearestEntity(entity.Pos.XYZ, range, range / 2, e =>
        e.Alive && (e is EntitySettler || e is EntityPlayer { Player.WorldData.CurrentGameMode: EnumGameMode.Survival }));

    bool Fight(ColonyManager m, Entity target, float dt, long now)
    {
        double distance = Math.Sqrt(entity.Pos.SquareDistanceTo(target.Pos.XYZ));
        var hand = Hand;
        if (Bandit.Archer && Bandit.ArrowsLeft > 0 && hand?.Itemstack != null && distance > 4 && distance <= ArcherRange)
        {
            if (goal != Goal.None)
            {
                StopWalking();
                goal = Goal.None;
            }
            shooter.Update(m, entity, hand, target, dt, SyncHand, () => Shoot(m, hand, target), () => m.Config.RangedSecondsPerShot * 1.3f);
            return true;
        }
        shooter.Cancel(entity, hand, SyncHand);

        if (distance <= Reach)
        {
            if (goal != Goal.None)
            {
                StopWalking();
                goal = Goal.None;
            }
            entity.Pos.Yaw = (float)Math.Atan2(target.Pos.X - entity.Pos.X, target.Pos.Z - entity.Pos.Z);
            if (cooldown <= 0) Strike(m, target);
            return true;
        }
        if (goal != Goal.Foe || now >= nextAimMs)
        {
            goal = Goal.Foe;
            nextAimMs = now + 1000;
            WalkTo(target.Pos.XYZ, Reach - 0.4f);
        }
        if (PathFailed) foe = null;
        KeepWalking();
        return true;
    }

    bool Shoot(ColonyManager m, ItemSlot hand, Entity target)
    {
        if (m.BanditArrow() is not { } arrow) return false;
        if (!m.LaunchProjectile(entity, hand, arrow, target, m.Config.BanditDamageFactor, m.Config.RangedSpreadDegrees * 1.5)) return false;
        Bandit.ArrowsLeft--;
        return true;
    }

    void Strike(ColonyManager m, Entity target)
    {
        var hand = Hand;
        var weapon = hand?.Itemstack is { } held && !ColonyManager.IsRangedWeapon(held) ? held : null;
        float damage = (weapon != null ? ColonyManager.WeaponDamage(weapon) : m.Config.GuardFistDamage) * m.Config.BanditDamageFactor;
        target.ReceiveDamage(new DamageSource
        {
            Source = EnumDamageSource.Entity,
            SourceEntity = entity,
            CauseEntity = entity,
            Type = weapon != null ? EnumDamageType.PiercingAttack : EnumDamageType.BluntAttack,
            DamageTier = weapon?.Collectible.ToolTier ?? 0
        }, damage);
        if (swingAnim != null) StopAnimation(swingAnim);
        swingAnim = PlayShapeAnimation(weapon != null && hand != null ? weapon.Collectible.GetHeldTpHitAnimation(hand, entity) ?? "breakhand" : "breakhand", "idle");
        swingLeft = SwingSeconds;
        cooldown = 1.6f;
    }

    /// <summary>A bandit with a torch tries now and then to light the grass about it, until it has lit one fire.</summary>
    bool TryFire(ColonyManager m, long now)
    {
        if (!Bandit.CarriesTorch || fireDone || now < nextFireMs) return false;
        willLight ??= entity.World.Rand.NextDouble() < m.Config.BanditFireChance;
        if (willLight != true)
        {
            fireDone = true;
            return false;
        }
        nextFireMs = now + 3000;
        if (!m.SetFireNear(Bandit, 4)) return false;
        fireDone = true;
        return false; // carries on with whatever it was doing
    }

    /// <summary>A bandit with no bow walks to the nearest chest with something in it and takes a stack.</summary>
    bool Chest(ColonyManager m, long now)
    {
        if (Bandit.Archer) return false;
        if (chest == null)
        {
            if (now < nextChestMs) return false;
            nextChestMs = now + 5000;
            chest = m.ChestToRob(Bandit, 12, triedChests);
            if (chest == null) return false;
            goal = Goal.Chest;
            WalkTo(chest.Pos.ToVec3d().Add(0.5, 0, 0.5), 1.6f);
        }
        if (chest.Pos == null || entity.World.BlockAccessor.GetBlockEntity(chest.Pos) != chest)
        {
            chest = null;
            return false;
        }
        if (entity.Pos.SquareDistanceTo(chest.Pos.ToVec3d().Add(0.5, 0.5, 0.5)) <= 2.6 * 2.6)
        {
            triedChests.Add(chest.Pos.Copy());
            StopWalking();
            m.StealFrom(Bandit, chest);
            chest = null;
            goal = Goal.None;
            return true;
        }
        if (PathFailed || goal != Goal.Chest)
        {
            if (PathFailed) triedChests.Add(chest.Pos.Copy());
            chest = null;
            return false;
        }
        KeepWalking();
        return true;
    }

    public override void FinishExecute(bool cancelled)
    {
        StopWalking();
        shooter.Stop(entity, Hand, SyncHand);
        if (swingAnim != null) StopAnimation(swingAnim);
        swingAnim = null;
        base.FinishExecute(cancelled);
    }
}
