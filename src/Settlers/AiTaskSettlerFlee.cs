using CreateAColony.Colonies;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace CreateAColony.Settlers;

/// <summary>
/// A settler who is not a guard runs when a creature hurts it: home, or to a nearby guard, whichever
/// ColonyManager.FleeDestination finds safer. Once there it hides until the attacker is dead, far away,
/// or has left it alone for FleeSeconds. Hit again while hiding at home, it runs straight away from the
/// attacker instead; with nowhere to go it runs straight away too.
/// </summary>
public class AiTaskSettlerFlee : SettlerTaskBase
{
    /// <summary>How often the walk to a guard is aimed again at where the guard is now.</summary>
    const long GuardAimMs = 2000;
    const double RunAwayDistance = 16;

    Entity? attacker;
    EntityAgent? guard;
    Vec3d guardAimedAt = new();
    long nextGuardAimMs;
    int seenHits;
    bool runningAway;
    bool hiding;

    /// <summary>Never given up, however often the walk fails (0.200.0).</summary>
    protected override bool CanGiveUp => false;

    public AiTaskSettlerFlee(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig) : base(entity, taskConfig, aiConfig) { }

    public override bool ShouldExecute() =>
        manager != null && Colony != null && Citizen is { } citizen && manager.FleesWhenHurt(citizen)
        && manager.FleeState(entity) is { Hits: > 0 } // a helper that was never hit has nothing to run from (Support allies)
        && !manager.FightsBack(Colony, citizen, entity); // armed and no guard near: AiTaskSettlerDefend

    public override void StartExecute()
    {
        base.StartExecute();
        StartRun();
    }

    /// <summary>Picks home or a guard and runs there.</summary>
    void StartRun()
    {
        if (manager!.FleeState(entity) is not { } state || Colony is not { } colony || Citizen is not { } citizen) return;
        attacker = state.Attacker;
        seenHits = state.Hits;
        hiding = false;
        runningAway = false;
        StopAnimation("shelter");
        var (spot, toGuard) = manager.FleeDestination(colony, citizen, entity, attacker);
        guard = toGuard;
        if (guard != null)
        {
            guardAimedAt = guard.Pos.XYZ;
            nextGuardAimMs = entity.World.ElapsedMilliseconds + GuardAimMs;
        }
        WalkTo(spot, guard != null ? 2.5f : 1.5f);
    }

    /// <summary>Home or the guard did not work out: run straight away from the attacker.</summary>
    void RunAway()
    {
        hiding = false;
        runningAway = true;
        guard = null;
        StopAnimation("shelter");
        if (attacker != null && manager!.RunAwaySpot(entity, attacker, RunAwayDistance) is { } spot) WalkTo(spot, 1.5f);
        else Hide();
    }

    void Hide()
    {
        StopWalking();
        hiding = true;
        // Huddle at home; next to a guard just stand and let it fight.
        if (guard == null) PlayAnimation("shelter");
    }

    public override bool ContinueExecute(float dt)
    {
        var m = manager!;
        if (m.FleeState(entity) is not { } state || Colony is not { } colony || Citizen is not { } citizen || !m.FleesWhenHurt(citizen)) return false;
        if (m.FightsBack(colony, citizen, entity)) return false; // hands over to AiTaskSettlerDefend
        attacker = state.Attacker;
        bool hitAgain = state.Hits != seenHits;
        seenHits = state.Hits;

        if (hiding)
        {
            // Far enough away from it: back to what the settler was doing.
            double safe = m.Config.FleeSafeDistance;
            if (entity.Pos.SquareDistanceTo(attacker.Pos.XYZ) > safe * safe)
            {
                m.StopFleeing(entity);
                return false;
            }
            // Found and hit where it hid: run on. Next to a guard it stays, the guard is fighting for it.
            if (hitAgain && guard == null) RunAway();
            return true;
        }

        if (Arrived)
        {
            Hide();
            return true;
        }
        if (PathFailed)
        {
            if (runningAway) Hide();
            else RunAway();
            return true;
        }

        if (guard != null && entity.World.ElapsedMilliseconds >= nextGuardAimMs)
        {
            nextGuardAimMs = entity.World.ElapsedMilliseconds + GuardAimMs;
            if (!guard.Alive) StartRun();
            else if (guard.Pos.SquareDistanceTo(guardAimedAt) > 3 * 3)
            {
                // The guard is on the move (likely coming to fight): head for where it is now.
                guardAimedAt = guard.Pos.XYZ;
                WalkTo(guard.Pos.XYZ, 2.5f);
            }
        }
        KeepWalking();
        return true;
    }

    public override void FinishExecute(bool cancelled)
    {
        StopAnimation("shelter");
        attacker = null;
        guard = null;
        hiding = false;
        runningAway = false;
        base.FinishExecute(cancelled);
    }

    public override string Describe(Colony colony, CitizenRecord citizen)
    {
        string from = attacker?.GetName() ?? "";
        if (hiding) return Lang.Get("createacolony:activity-flee-hiding", from);
        if (runningAway) return Lang.Get("createacolony:activity-flee-away", from);
        if (guard != null) return Lang.Get("createacolony:activity-flee-guard", from, colony.Citizens.Find(c => c.EntityId == guard.EntityId)?.Name ?? "");
        return Lang.Get("createacolony:activity-flee-home", from);
    }
}
