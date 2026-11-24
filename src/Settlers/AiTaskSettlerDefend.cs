using System;
using System.Linq;
using CreateAColony.Colonies;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;

namespace CreateAColony.Settlers;

/// <summary>
/// A settler who is not a guard, carries a weapon and has no guard near fights back against the creature
/// that hurt it, as Aaron asked, instead of running (ColonyManager.FightsBack). It only fights that one
/// creature and never gives chase: once the attacker is dead or backs off past FightBackChaseDistance it
/// goes back to what it was doing. Badly hurt, or with a guard coming, it runs instead (AiTaskSettlerFlee).
/// </summary>
public class AiTaskSettlerDefend : SettlerTaskBase
{
    const float Reach = 2.4f;
    const float SwingSeconds = 0.6f;
    /// <summary>How often the walk to the attacker is aimed again at where it is now.</summary>
    const long AimMs = 1000;

    Entity? attacker;
    float cooldown;
    float swingLeft;
    string? swingAnim;
    long nextAimMs;
    readonly RangedShooter shooter = new();
    /// <summary>Whether the last look found a clear shot, looked again twice a second.</summary>
    bool clearShot;
    long clearLookMs;

    public AiTaskSettlerDefend(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig) : base(entity, taskConfig, aiConfig) { }

    public override bool ShouldExecute() =>
        manager != null && Colony is { } colony && Citizen is { } citizen && manager.FleeingFrom(entity) is { } from
        && manager.FightsBack(colony, citizen, entity, Helping) && InRange(from);

    /// <summary>Came to help another settler (Support allies) and has not been hit itself.</summary>
    bool Helping => manager?.FleeState(entity) is { Hits: 0 };

    /// <summary>Close enough to fight; a helper comes from further away, up to SupportAllyRadius beyond.</summary>
    bool InRange(Entity from)
    {
        double chase = manager!.Config.FightBackChaseDistance;
        if (Helping) chase += manager.Config.SupportAllyRadius;
        return entity.Pos.SquareDistanceTo(from.Pos.XYZ) <= chase * chase;
    }

    public override void StartExecute()
    {
        base.StartExecute();
        attacker = manager!.FleeingFrom(entity);
        cooldown = 0;
        nextAimMs = 0;
        if (Colony is { } colony && Citizen is { } citizen) HoldBestWeapon(colony, citizen);
    }

    public override bool ContinueExecute(float dt)
    {
        var m = manager!;
        if (Colony is not { } colony || Citizen is not { } citizen || !citizen.Alive) return false;
        attacker = m.FleeingFrom(entity);
        // Dead, gone or left alone long enough: done.
        if (attacker == null) return false;
        // Badly hurt or a guard is close: run instead (the flee task takes over).
        if (!m.FightsBack(colony, citizen, entity, Helping))
        {
            // A helper that can no longer fight just goes back to work; one that was hit runs (the flee task).
            if (Helping) m.StopFleeing(entity);
            return false;
        }
        // It backed off: let it go rather than hunt it down.
        if (!InRange(attacker))
        {
            m.StopFleeing(entity);
            return false;
        }

        cooldown -= dt;
        shooter.Tick(entity, dt);
        if (swingAnim != null && (swingLeft -= dt) <= 0)
        {
            StopAnimation(swingAnim);
            swingAnim = null;
        }
        m.SetShieldUp(citizen, entity, true);

        // Only a bow or sling to hand (its last choice): shoot from where it stands while it can, else close in.
        if (Hand is { Itemstack: { } held } bow && ColonyManager.IsRangedWeapon(held) && m.AmmoCount(citizen, held) > 0)
        {
            double distance = entity.Pos.DistanceTo(attacker.Pos.XYZ);
            if (entity.World.ElapsedMilliseconds >= clearLookMs)
            {
                clearLookMs = entity.World.ElapsedMilliseconds + 500;
                clearShot = m.ClearShot(entity, attacker);
            }
            if (distance <= m.RangeOf(held) && distance > 1.5 && clearShot)
            {
                StopWalking();
                if (shooter.Update(m, colony, citizen, entity, bow, attacker, dt, SyncHand)) m.GainWorkXp(colony, citizen, Jobs.Guard, 1);
                return true;
            }
            shooter.Cancel(entity, bow, SyncHand);
            if (entity.World.ElapsedMilliseconds >= nextAimMs || PathFailed || Arrived)
            {
                nextAimMs = entity.World.ElapsedMilliseconds + AimMs;
                WalkTo(attacker.Pos.XYZ, 6f);
            }
            KeepWalking();
            return true;
        }

        if (entity.Pos.DistanceTo(attacker.Pos.XYZ) <= Reach)
        {
            StopWalking();
            entity.Pos.Yaw = (float)Math.Atan2(attacker.Pos.X - entity.Pos.X, attacker.Pos.Z - entity.Pos.Z);
            if (cooldown <= 0) Strike(colony, citizen, attacker);
            return true;
        }
        if (entity.World.ElapsedMilliseconds >= nextAimMs || PathFailed || Arrived)
        {
            nextAimMs = entity.World.ElapsedMilliseconds + AimMs;
            WalkTo(attacker.Pos.XYZ, Reach - 0.4f);
        }
        KeepWalking();
        return true;
    }

    /// <summary>A guard's blow (AiTaskSettlerGuard.Strike), with the weapon in hand.</summary>
    void Strike(Colony colony, CitizenRecord citizen, Entity target)
    {
        var m = manager!;
        var hand = Hand;
        var weapon = hand?.Itemstack is { } held && ColonyManager.IsWeapon(held) ? held : null;
        float damage = weapon != null ? ColonyManager.WeaponDamage(weapon) : m.Config.GuardFistDamage;
        damage *= m.DamageFactor(colony, citizen); // Fighting and Strength skills
        damage += m.TraitAdd(citizen, "damageAdd"); // Veteran
        manager!.SafeHit(target, new DamageSource
        {
            Source = EnumDamageSource.Entity,
            SourceEntity = entity,
            CauseEntity = entity,
            Type = weapon != null ? EnumDamageType.SlashingAttack : EnumDamageType.BluntAttack,
            DamageTier = weapon?.Collectible.ToolTier ?? 0
        }, damage);
        if (!target.Alive)
        {
            m.CountKill(colony, citizen); // towards Veteran
            m.LogEvent(colony, "event-fought-off", citizen.Name, target.GetName());
        }
        m.GainWorkXp(colony, citizen, Jobs.Guard, target.Alive ? 1 : 5); // Fighting XP
        if (weapon != null && hand != null)
        {
            m.WearTool(entity, hand);
            SyncHand();
        }

        if (swingAnim != null) StopAnimation(swingAnim);
        swingAnim = PlayShapeAnimation(hand?.Itemstack?.Collectible.GetHeldTpHitAnimation(hand, entity) ?? "breakhand", "breakhand");
        swingLeft = SwingSeconds;
        cooldown = m.Config.GuardSecondsPerHit * m.WorkTimeFactor(colony, citizen) / m.SwingFactor(colony, citizen);
    }

    /// <summary>
    /// Holds the weapon it fights with (ColonyManager.CivilianWeapon, Aaron 0.178.0): a spear first, then its
    /// hardest-hitting other weapon, and a bow or sling only when it has nothing else. Puts its tool away.
    /// </summary>
    void HoldBestWeapon(Colony colony, CitizenRecord citizen)
    {
        var hand = Hand;
        if (hand == null) return;
        var best = manager!.CivilianWeapon(citizen, hand.Itemstack);
        if (best == null || hand.Itemstack?.Collectible.Code.Equals(best.Collectible.Code) == true) return;
        var weapon = manager.TakeFromPack(colony, citizen, s => s.Collectible.Code.Equals(best.Collectible.Code), 1);
        if (weapon == null) return;
        PutAwayHand(colony, citizen);
        hand.Itemstack = weapon;
        SyncHand();
    }

    public override void FinishExecute(bool cancelled)
    {
        if (swingAnim != null) StopAnimation(swingAnim);
        swingAnim = null;
        shooter.Stop(entity, Hand, SyncHand);
        if (Citizen is { } fighter) manager?.SetShieldUp(fighter, entity, false);
        // Back to work: the job's task takes its tool out again.
        if (Hand?.Itemstack is { } held && (ColonyManager.IsWeapon(held) || ColonyManager.IsRangedWeapon(held)) && Colony is { } colony && Citizen is { } citizen) PutAwayHand(colony, citizen);
        attacker = null;
        base.FinishExecute(cancelled);
    }

    public override string Describe(Colony colony, CitizenRecord citizen) =>
        Lang.Get("createacolony:activity-defend", attacker?.GetName() ?? "");
}
