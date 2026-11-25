using System;
using CreateAColony.Colonies;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace CreateAColony.Settlers;

/// <summary>Why a settler counts as stuck right now, for the settler window, /colony stuck and the log.</summary>
public enum StuckReason
{
    None,
    /// <summary>The pathfinder found no way to the goal.</summary>
    NoPath,
    /// <summary>A path was found but the settler has got no closer for StuckNoProgressSeconds.</summary>
    NoProgress
}

/// <summary>
/// One settler's Willpower: how long it has been stuck, and the decision to relocate it.
///
/// The walking code (SettlerTaskBase) tells it where the settler is heading, when a path search fails and when
/// the walk ends. Once a second EntityBehaviorColonyCitizen calls Tick, which works out whether the settler
/// is stuck and adds or takes away Willpower:
///
///   stuck:     Willpower += seconds * (trapped ? WillpowerTrappedRate : 1)
///   moving on: Willpower -= seconds * WillpowerDecayPerSecond
///   not walking anywhere: Willpower -= seconds * WillpowerIdleDecayPerSecond
///   arrived:   Willpower = 0
///
/// When Willpower reaches the settler's threshold (WillpowerSeconds, rolled up to WillpowerJitter above or
/// below it each time), ColonyManager.Relocate moves it to a safe spot nearby and Willpower starts again at 0.
/// Willpower is kept between tasks, so a settler that tries, gives up and tries again keeps building it.
/// </summary>
public class Willpower
{
    /// <summary>Getting this much closer to the goal counts as progress.</summary>
    const double ProgressDistance = 1.0;
    /// <summary>Moving this far from where it last made progress also counts, for goals that move (a guard's target).</summary>
    const double MoveDistance = 3.0;
    /// <summary>How often a stuck settler checks whether it is boxed in; the check walks the blocks around it.</summary>
    const long TrappedCheckMs = 5000;

    readonly Entity entity;

    public double Value { get; private set; }
    public double Threshold { get; private set; }
    public StuckReason Reason { get; private set; }
    public bool Trapped { get; private set; }
    /// <summary>Goes up by one each time the settler is relocated, so a walk in progress knows to plan again.</summary>
    public int Relocations { get; private set; }

    /// <summary>The goal of the walk in progress, or null while the settler is not walking anywhere.</summary>
    public Vec3d? Goal { get; private set; }
    /// <summary>The last goal the settler walked towards, kept after the walk ends; relocations head towards it.</summary>
    public Vec3d? LastGoal { get; private set; }
    float goalRange;
    bool noPath;
    double bestDistance;
    Vec3d progressPos = new();
    long progressMs;
    long nextTrappedCheckMs;

    public Willpower(Entity entity)
    {
        this.entity = entity;
    }

    /// <summary>Multiplies the threshold: a Patient settler waits longer before leaping (set from the trait each tick).</summary>
    public double WaitFactor = 1;

    /// <summary>Fraction of the threshold reached, 0 to 1.</summary>
    public double Fraction => Threshold <= 0 ? 0 : Math.Min(1, Value / Threshold);

    /// <summary>The settler starts or carries on walking to a goal.</summary>
    public void Heading(Vec3d goal, float range)
    {
        long now = entity.World.ElapsedMilliseconds;
        if (Goal == null)
        {
            // A fresh walk gets a full StuckNoProgressSeconds before it can count as stuck.
            progressMs = now;
            progressPos = entity.Pos.XYZ;
            noPath = false;
        }
        if (Goal == null || Goal.SquareDistanceTo(goal) > 4)
        {
            // Another goal: progress is measured from here. The clock is not reset, so a settler that keeps
            // switching between goals it cannot reach still builds Willpower.
            bestDistance = entity.Pos.DistanceTo(goal);
        }
        Goal = goal.Clone();
        LastGoal = Goal;
        goalRange = range;
    }

    /// <summary>A walk that already ended in failure: a relocation heads towards its goal.</summary>
    public void Aim(Vec3d goal) => LastGoal = goal.Clone();

    /// <summary>A path search to the current goal failed.</summary>
    public void NoPathFound() => noPath = true;

    /// <summary>The walk ended: arrived, given up or the task was stopped. Willpower stays until it decays.</summary>
    public void Stopped()
    {
        if (Goal != null && IsAt(Goal, goalRange)) Arrived();
        Goal = null;
        noPath = false;
        Reason = StuckReason.None;
    }

    void Arrived()
    {
        Value = 0;
        Threshold = 0;
        Goal = null;
        noPath = false;
        Reason = StuckReason.None;
        Trapped = false;
    }

    bool IsAt(Vec3d goal, float range) => entity.Pos.SquareDistanceTo(goal) <= (range + 1) * (range + 1);

    /// <summary>
    /// Updates Willpower for the seconds since the last call. True when it is full and the settler should be
    /// relocated now.
    /// </summary>
    public bool Tick(double seconds, ColonyManager manager)
    {
        var config = manager.Config;
        long now = entity.World.ElapsedMilliseconds;
        if (Goal == null)
        {
            Reason = StuckReason.None;
            Trapped = false;
            Value = Math.Max(0, Value - seconds * config.WillpowerIdleDecayPerSecond);
            return false;
        }
        if (IsAt(Goal, goalRange))
        {
            Arrived();
            return false;
        }

        double distance = entity.Pos.DistanceTo(Goal);
        bool progressed = false;
        if (distance < bestDistance - ProgressDistance || entity.Pos.SquareDistanceTo(progressPos) > MoveDistance * MoveDistance)
        {
            bestDistance = Math.Min(bestDistance, distance);
            progressPos = entity.Pos.XYZ;
            progressMs = now;
            noPath = false;
            progressed = true;
        }

        Reason = noPath ? StuckReason.NoPath
            : now - progressMs > config.StuckNoProgressSeconds * 1000 ? StuckReason.NoProgress
            : StuckReason.None;

        if (Reason == StuckReason.None)
        {
            Trapped = false;
            if (progressed || Value > 0) Value = Math.Max(0, Value - seconds * config.WillpowerDecayPerSecond);
            return false;
        }

        if (now >= nextTrappedCheckMs)
        {
            nextTrappedCheckMs = now + TrappedCheckMs;
            Trapped = manager.IsTrapped(entity);
        }
        if (Value <= 0 || Threshold <= 0)
        {
            double jitter = Math.Clamp(config.WillpowerJitter, 0, 0.9);
            Threshold = Math.Max(1, config.WillpowerSeconds) * WaitFactor * (1 + (entity.World.Rand.NextDouble() * 2 - 1) * jitter);
        }
        Value += seconds * (Trapped ? Math.Max(1, config.WillpowerTrappedRate) : 1);
        return Value >= Threshold;
    }

    /// <summary>No safe spot was found: wait a few seconds before looking again, rather than every second.</summary>
    public void TryAgainLater() => Value = Math.Min(Value, Threshold * 0.75);

    /// <summary>The settler was moved: start again from zero and measure progress from the new spot.</summary>
    public void Relocated()
    {
        Value = 0;
        Threshold = 0;
        noPath = false;
        Trapped = false;
        Reason = StuckReason.None;
        progressMs = entity.World.ElapsedMilliseconds;
        progressPos = entity.Pos.XYZ;
        if (Goal != null) bestDistance = entity.Pos.DistanceTo(Goal);
        Relocations++;
    }
}
