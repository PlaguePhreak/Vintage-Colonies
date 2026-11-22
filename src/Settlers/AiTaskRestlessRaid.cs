using System;
using CreateAColony.Colonies;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;

namespace CreateAColony.Settlers;

/// <summary>
/// What one of the Restless does on an undead raid (0.187.0). It goes for the nearest settler or player it can
/// see, claws and bites it (a bite can make a settler ill, ColonyManager.RestlessBite), and otherwise shambles on
/// the Town Hall and wanders about it. It never steals, never lights fires, never runs and never breaks a block.
/// At sunrise the raid sends it off (ColonyManager.Undead), and it comes back the next night.
/// </summary>
public class AiTaskRestlessRaid : SettlerTaskBase
{
    const float Reach = 2.2f;
    const float SwingSeconds = 0.7f;
    const float SeeRange = 14f;

    enum Goal { None, Foe, Target, Prowl }

    Goal goal;
    Entity? foe;
    float cooldown;
    float swingLeft;
    string? swingAnim;
    long nextLookMs;
    long nextAimMs;

    public AiTaskRestlessRaid(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig) : base(entity, taskConfig, aiConfig) { }

    protected override bool CountsForWillpower => false;

    ColonyManager.ActiveRaid? Raid => manager?.RaidOf(entity) is { Attacking: true } raid ? raid : null;

    public override bool ShouldExecute() => entity is EntityRestless && Raid != null;

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
        if (swingAnim != null && (swingLeft -= dt) <= 0)
        {
            StopAnimation(swingAnim);
            swingAnim = null;
        }

        if (now >= nextLookMs)
        {
            nextLookMs = now + 1000;
            var near = FindFoe(SeeRange);
            if (near != null) foe = near;
            else if (foe != null && (!foe.Alive || foe.Pos.SquareDistanceTo(entity.Pos.XYZ) > (SeeRange + 6) * (SeeRange + 6))) foe = null;
        }
        if (foe is { Alive: true }) return Fight(m, foe, now);

        // Shamble on the Town Hall, then wander about it.
        var target = m.RaidTarget(raid);
        if (goal is not (Goal.Target or Goal.Prowl) || Arrived || PathFailed)
        {
            if (entity.Pos.SquareDistanceTo(target) < 12 * 12)
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

    bool Fight(ColonyManager m, Entity target, long now)
    {
        double distance = Math.Sqrt(entity.Pos.SquareDistanceTo(target.Pos.XYZ));
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

    void Strike(ColonyManager m, Entity target)
    {
        bool hit = target.ReceiveDamage(new DamageSource
        {
            Source = EnumDamageSource.Entity,
            SourceEntity = entity,
            CauseEntity = entity,
            Type = EnumDamageType.PiercingAttack,
            DamageTier = 0
        }, m.Config.RestlessDamage);
        if (hit) m.RestlessBite(entity, target);
        if (swingAnim != null) StopAnimation(swingAnim);
        swingAnim = PlayShapeAnimation("breakhand", "idle");
        swingLeft = SwingSeconds;
        cooldown = 2f;
    }

    public override void FinishExecute(bool cancelled)
    {
        StopWalking();
        if (swingAnim != null) StopAnimation(swingAnim);
        swingAnim = null;
        base.FinishExecute(cancelled);
    }
}
