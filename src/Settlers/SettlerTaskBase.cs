using System.Collections.Generic;
using CreateAColony.Colonies;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace CreateAColony.Settlers;

/// <summary>
/// Shared plumbing for settler AI tasks: finding the settler's colony record and walking to a target
/// with the vanilla async pathfinder, giving up after a few failed attempts instead of re-pathing forever.
/// </summary>
public abstract class SettlerTaskBase : AiTaskBase
{
    const int MaxPathAttempts = 3;
    /// <summary>How many spots one path search looks at on the first try; later tries look at three times as many.</summary>
    const int SearchDepth = 2000;
    const long RetryDelayMs = 4000;
    /// <summary>A walking settler that has not moved this far in NoProgressMs plans a new path.</summary>
    const double ProgressDistance = 0.5;
    const long NoProgressMs = 4000;
    /// <summary>
    /// Times a walk plans again on the spot after bumping into something, before it counts as a failed try.
    /// The game's walker gives up on a path as soon as the settler rubs against a wall corner, a door frame or
    /// another settler for a moment; that used to count as "no path" and soon made settlers give up on their work.
    /// </summary>
    const int MaxStuckReplans = 4;
    /// <summary>
    /// How close the settler gets to each waypoint of a path before heading for the next one. The game's walker
    /// uses the walk's arrival range for every waypoint and looks up to three waypoints ahead, so with a range
    /// of 1.5 to 2.5 blocks settlers cut corners and walked into wall edges and blocks at head height that the
    /// path went around. Arriving at the goal itself is checked here with the walk's own range.
    /// </summary>
    const float WaypointRange = 0.75f;

    protected readonly float moveSpeed;
    protected readonly ColonyManager? manager;

    /// <summary>Further than this, a settler walks in stretches: the pathfinder only plans so far ahead.</summary>
    const double LegLength = 28;
    /// <summary>
    /// Stretches tried in turn when one cannot be walked: (length, turn in degrees from straight at the goal).
    /// Shorter and turned ones get around water, cliffs and out of holes.
    /// </summary>
    static readonly (double Length, double Turn)[] Legs =
    {
        (28, 0), (28, 40), (28, -40), (16, 0), (16, 60), (16, -60), (28, 90), (28, -90), (10, 120), (10, -120), (10, 180)
    };

    Vec3d? finalGoal;
    float finalRange;
    System.Func<Vec3d>? refreshGoal;
    Vec3d? goal;
    float goalRange;
    bool onLeg;
    bool legDone;
    int legTry;
    bool arrived;
    bool failed;
    int attempts;
    int stuckReplans;
    long retryAtMs;
    Vec3d lastProgressPos = new();
    long lastProgressMs;

    enum LadderPhase { None, ToStart, Climbing }
    LadderPhase ladderPhase;
    LadderClimb? ladder;
    /// <summary>Ladders that did not work out on this walk, so the next try picks another.</summary>
    readonly List<BlockPos> triedLadders = new();
    /// <summary>Falls off ladders on this walk; the first fall is retried on the same ladder.</summary>
    int ladderFalls;
    int seenRelocations;
    Willpower? willpower;

    /// <summary>The settler's Willpower (see Willpower.cs), told where the walk is going and how it goes.</summary>
    Willpower? Will => CountsForWillpower ? willpower ??= entity.GetBehavior<EntityBehaviorColonyCitizen>()?.Willpower : null;

    /// <summary>False for walks that do not matter (an idle stroll): failing them never builds Willpower.</summary>
    protected virtual bool CountsForWillpower => true;

    protected SettlerTaskBase(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig) : base(entity, taskConfig, aiConfig)
    {
        moveSpeed = taskConfig["movespeed"].AsFloat(0.02f);
        if (entity.Api.Side == EnumAppSide.Server) manager = entity.Api.ModLoader.GetModSystem<ColonyManager>();
    }

    protected Colony? Colony => entity is EntitySettler s && s.ColonyId != null ? manager?.GetById(s.ColonyId) : null;

    protected CitizenRecord? Citizen => entity is EntitySettler s ? manager?.FindCitizen(s.ColonyId, s.CitizenId) : null;

    /// <summary>A guard with a monster close by or an alarm to answer leaves what it is doing (eating, sleeping, sheltering).</summary>
    protected bool CalledToArms => manager != null && manager.GuardCalled(entity, Colony, Citizen);

    /// <summary>For a guard going to or in bed: only a monster close by or an alarm gets it up (ColonyManager.GuardWoken).</summary>
    protected bool WokenByThreat => manager != null && manager.GuardWoken(entity, Colony, Citizen);

    /// <summary>
    /// Starts walking to a position. Check Arrived / PathFailed from ContinueExecute and call KeepWalking.
    /// A far goal is walked in stretches; refresh, if given, works the goal out again for the last
    /// stretch, once the land there is loaded.
    /// </summary>
    protected void WalkTo(Vec3d target, float range, System.Func<Vec3d>? refresh = null)
    {
        finalGoal = target;
        finalRange = range;
        refreshGoal = refresh;
        arrived = false;
        failureNoted = false;
        arrivalNoted = false;
        WalkRefusal = null;
        // Back where this settler got lost before (0.198.0), or a goal it has given up for now (0.200.0): no walk
        // there, it counts as no path.
        if (manager != null && Citizen is { } lost)
        {
            if (manager.IsOffLimits(Colony, lost, target)) WalkRefusal = "off limits";
            else if (CanGiveUp && manager.GaveUpOn(lost, target, out _)) WalkRefusal = "given up";
            if (WalkRefusal != null)
            {
                failed = true;
                failureNoted = true;
                onLeg = false;
                attempts = MaxPathAttempts;
                legDone = false;
                pathTraverser.Stop();
                manager.NoteWalk(lost, entity, TaskName, WalkRefusal, target);
                if (manager.PathDebug) manager.Mod.Logger.Notification("[pathing] {0} at {1}: walk to {2} refused ({3}, {4})", lost.Name, entity.Pos.AsBlockPos, target.AsBlockPos, WalkRefusal, TaskName);
                return;
            }
        }
        legTry = 0;
        EndLadder();
        triedLadders.Clear();
        ladderFalls = 0;
        doorHelps = 0;
        if (Will is { } will)
        {
            will.Heading(target, range);
            seenRelocations = will.Relocations;
        }
        // StopWalking ends the walk animation, so every new leg starts it again; otherwise settlers glide.
        if (animMeta != null) entity.AnimManager.StartAnimation(animMeta);
        StartLeg();
    }

    /// <summary>Plans the next stretch: straight to the goal when it is near, else a point on the way there.</summary>
    void StartLeg()
    {
        attempts = 0;
        stuckReplans = 0;
        failed = false;
        legDone = false;
        var here = entity.Pos.XYZ;
        double dx = finalGoal!.X - here.X, dz = finalGoal.Z - here.Z;
        onLeg = System.Math.Sqrt(dx * dx + dz * dz) > LegLength + 4;
        if (!onLeg)
        {
            if (refreshGoal != null)
            {
                finalGoal = refreshGoal();
                Will?.Heading(finalGoal, finalRange);
            }
            goal = finalGoal;
            goalRange = finalRange;
            StartPath();
            return;
        }

        // The first stretch, from this one on, that ends somewhere the settler can stand.
        for (; legTry < Legs.Length; legTry++)
        {
            var (length, turn) = Legs[legTry];
            double angle = System.Math.Atan2(dz, dx) + turn * GameMath.DEG2RAD;
            if (StandingSpot(here.X + System.Math.Cos(angle) * length, here.Z + System.Math.Sin(angle) * length) is not { } spot) continue;
            goal = spot;
            goalRange = 2;
            StartPath();
            return;
        }
        failed = true;
        attempts = MaxPathAttempts;
        Will?.NoPathFound();
    }

    /// <summary>A free spot with floor under it on the surface near a point, or null where the land is not loaded or there is none.</summary>
    Vec3d? StandingSpot(double x, double z)
    {
        if (manager == null) return null;
        var ba = entity.World.BlockAccessor;
        var column = new BlockPos((int)System.Math.Floor(x), 0, (int)System.Math.Floor(z));
        if (ba.GetChunkAtBlockPos(column.X, 1, column.Z) == null) return null;
        column.Y = ba.GetTerrainMapheightAt(column) + 1;
        return manager.FreeSpotNear(entity, column, 2);
    }

    protected bool Arrived
    {
        get
        {
            bool there = arrived || (finalGoal != null && entity.Pos.SquareDistanceTo(finalGoal) <= (finalRange + 0.6f) * (finalRange + 0.6f));
            if (there) NoteArrival();
            return there;
        }
    }

    /// <summary>
    /// For a settler whose walk to its bed failed: true when the bed is close enough (BedFindWayRange) to get into
    /// it anyway. A bed up a ladder or behind a door the pathfinder misjudges is still the settler's bed.
    /// </summary>
    protected bool NearBed(BlockPos bed)
    {
        float range = manager?.Config.BedFindWayRange ?? 0;
        return range > 0 && entity.Pos.SquareDistanceTo(bed.X + 0.5, bed.Y + 0.5, bed.Z + 0.5) <= range * range;
    }

    /// <summary>True as soon as one path search or walk has failed, before the retries.</summary>
    protected bool PathFailedOnce => failed;

    /// <summary>True once the target could not be reached after several attempts.</summary>
    protected bool PathFailed
    {
        get
        {
            bool done = failed && attempts >= MaxPathAttempts && (!onLeg || legTry >= Legs.Length);
            if (done) NoteFailure();
            return done;
        }
    }

    /// <summary>Why the last WalkTo was not even started ("off limits", "given up"), or null. For statuses.</summary>
    protected string? WalkRefusal { get; private set; }

    /// <summary>False for walks a settler must never give up: to bed, to food, to safety.</summary>
    protected virtual bool CanGiveUp => true;

    /// <summary>False when the task has its own answer to a walk that fails (the sleep task carries the settler to bed).</summary>
    protected virtual bool HelpedWhenStuck => true;

    /// <summary>This task's name in the walking history: "Courier", "Sleep".</summary>
    string TaskName => GetType().Name.Replace("AiTaskSettler", "");

    bool failureNoted;
    bool arrivalNoted;

    /// <summary>The first time a walk is seen to have failed for good: counted, helped or given up (ColonyManager.WalkTrouble.cs).</summary>
    void NoteFailure()
    {
        if (failureNoted || manager == null || finalGoal == null) return;
        failureNoted = true;
        if (!CountsForWillpower || Citizen is not { } citizen) return;
        string result = HelpedWhenStuck ? manager.WalkFailed(Colony, citizen, entity, finalGoal, CanGiveUp) : "no way";
        manager.NoteWalk(citizen, entity, TaskName, result, finalGoal);
    }

    void NoteArrival()
    {
        if (arrivalNoted || manager == null || finalGoal == null) return;
        arrivalNoted = true;
        if (!CountsForWillpower || Citizen is not { } citizen) return;
        manager.WalkArrived(citizen, finalGoal);
        manager.NoteWalk(citizen, entity, TaskName, "arrived", finalGoal);
    }

    /// <summary>The settler's step height as its entity file sets it, before any stair boost.</summary>
    float? baseStepHeight;
    /// <summary>How much higher a settler may step after bumping into something on the lower half of a stair or a slab.</summary>
    const float HalfBlockStepBoost = 0.5f;
    /// <summary>How long the boost lasts: stepping up rises a little each tick, so it must outlast the climb.</summary>
    const long StepBoostMs = 2000;
    long stepBoostUntilMs;

    /// <summary>
    /// The pathfinder counts a stair as a whole block, so it plans a step from a stair onto the block a level
    /// above it. A settler standing on the stair's lower half is then half a block short and walks into the
    /// wall. When it bumps into something there, it may step half a block higher for a moment, long enough
    /// to rise all the way (the boost must not end as soon as its feet leave the stair's lower half).
    /// </summary>
    void StepUpFromStairs()
    {
        if (entity.GetBehavior<EntityBehaviorControlledPhysics>() is not { } physics) return;
        baseStepHeight ??= physics.StepHeight;
        long now = entity.World.ElapsedMilliseconds;
        double feet = entity.Pos.Y - System.Math.Floor(entity.Pos.Y);
        if (entity.OnGround && entity.CollidedHorizontally && feet > 0.3 && feet < 0.9) stepBoostUntilMs = now + StepBoostMs;
        physics.StepHeight = now < stepBoostUntilMs ? baseStepHeight.Value + HalfBlockStepBoost : baseStepHeight.Value;
    }

    /// <summary>Retries a stuck path after a short pause. Call every ContinueExecute while walking.</summary>
    protected void KeepWalking()
    {
        if (goal == null || arrived) return;
        StepUpFromStairs();
        // Another task finishing, or a work animation ending, can stop the shared walk animation while this
        // task is still walking, and the settler glides. Start it again whenever it is moving without it.
        // Swimming and climbing have their own animations (SettlerBody); the walk comes back once out of the water.
        bool ownWay = entity is EntitySettler { Body.MovesOwnWay: true };
        if (animMeta != null && !ownWay && ladderPhase == LadderPhase.None && pathTraverser.Active && !entity.AnimManager.IsAnimationActive(animMeta.Code))
        {
            entity.AnimManager.StartAnimation(animMeta);
        }
        if (Will is { } will && will.Relocations != seenRelocations)
        {
            // Willpower moved the settler: plan the walk again from the new spot.
            seenRelocations = will.Relocations;
            EndLadder();
            legTry = 0;
            StartLeg();
            return;
        }
        if (ladderPhase == LadderPhase.Climbing)
        {
            if (!ladder!.Tick(entity, moveSpeed, out bool climbFailed)) return;
            if (climbFailed && (!ladder.Fell || ++ladderFalls > 1)) triedLadders.Add(ladder.Column);
            if (manager?.PathDebug == true && (climbFailed || ladder.Carried))
            {
                manager.Mod.Logger.Notification("[pathing] settler {0} at {1}: the climb at {2} {3}", entity.EntityId, entity.Pos.AsBlockPos, ladder.Column,
                    ladder.Carried ? "got nowhere, set down at its other end" : ladder.Fell ? "ended in a fall" : "failed");
            }
            EndLadder();
            legTry = 0;
            StartLeg();
            return;
        }
        if (legDone)
        {
            legTry = 0;
            StartLeg();
            return;
        }
        long now = entity.World.ElapsedMilliseconds;
        // Within the walk's range of the goal (or of a stretch's end): arrived, even though the walker only
        // counts a waypoint as reached much closer than that.
        if (!failed && ladderPhase == LadderPhase.None && entity.Pos.SquareDistanceTo(goal) <= goalRange * goalRange)
        {
            pathTraverser.Stop();
            OnArrived();
            return;
        }
        if (failed)
        {
            if (onLeg)
            {
                // This stretch cannot be walked: try a shorter one or one turned to the side, right away.
                if (legTry < Legs.Length)
                {
                    legTry++;
                    StartLeg();
                }
                // No stretch can be walked: a cliff in the way. Climb it by ladder, or put ladders up.
                else if (ladderPhase == LadderPhase.None && now >= retryAtMs)
                {
                    retryAtMs = now + RetryDelayMs;
                    if (TryLadder()) legTry = 0;
                }
            }
            else if (attempts < MaxPathAttempts && now >= retryAtMs)
            {
                StartPath();
            }
            return;
        }
        // A block placed in the way after the path was planned leaves the settler walking into it: plan again.
        if (entity.Pos.SquareDistanceTo(lastProgressPos) > ProgressDistance * ProgressDistance)
        {
            lastProgressPos = entity.Pos.XYZ;
            lastProgressMs = now;
        }
        else if (now - lastProgressMs > NoProgressMs)
        {
            pathTraverser.Stop();
            stuckReplans = System.Math.Max(stuckReplans, 1);
            if (ThroughDoor()) StartPath();
            else if (attempts < MaxPathAttempts) StartPath();
            else failed = true;
        }
    }

    void StartPath()
    {
        attempts++;
        failed = false;
        lastProgressPos = entity.Pos.XYZ;
        lastProgressMs = entity.World.ElapsedMilliseconds;
        // A search still running would make the traverser ignore this one without a word.
        pathTraverser.Stop();
        // NavigateTo_Async already called OnPathFailed itself when it returns false: do not count that twice.
        // A second and third try search further (0.200.0): the long way round a fenced yard ran out of search and
        // counted as "no path" (Builder's Hut 2 in the overnight test).
        int depth = attempts <= 1 ? SearchDepth : SearchDepth * 3;
        pathTraverser.NavigateTo_Async(goal!, moveSpeed, System.Math.Min(goalRange, WaypointRange), OnArrived, OnStuck, OnPathFailed, depth, 1);
    }

    void OnArrived()
    {
        if (ladderPhase == LadderPhase.ToStart) StartClimbing();
        else if (onLeg) legDone = true;
        else arrived = true;
    }

    /// <summary>The walker stopped because the settler is not getting anywhere along the path it was given.</summary>
    void OnStuck()
    {
        pathTraverser.Stop();
        if (ThroughDoor())
        {
            attempts = System.Math.Max(0, attempts - 1);
            StartPath();
            return;
        }
        if (ladderPhase != LadderPhase.ToStart && stuckReplans < MaxStuckReplans)
        {
            // Bumped into something on the way: plan again from where it stands, straight away, without
            // using up one of the tries. Each search also picks a new line through the blocks.
            stuckReplans++;
            if (manager?.PathDebug == true) manager.Mod.Logger.Notification("[pathing] settler {0} bumped into something at {1}, planning again ({2})", entity.EntityId, entity.Pos.AsBlockPos, stuckReplans);
            attempts--;
            StartPath();
            return;
        }
        OnPathFailed();
    }

    /// <summary>Times this walk set the settler down beyond a door it kept bumping at (ColonyManager.HelpThroughDoor).</summary>
    int doorHelps;
    const int MaxDoorHelps = 3;

    /// <summary>After a second bump by a door: get the settler through it. False when no door is in the way.</summary>
    bool ThroughDoor()
    {
        if (manager == null || goal == null || ladderPhase != LadderPhase.None || stuckReplans < 1 || doorHelps >= MaxDoorHelps) return false;
        if (!manager.HelpThroughDoor(entity, goal)) return false;
        doorHelps++;
        stuckReplans = 0;
        return true;
    }

    void OnPathFailed()
    {
        // With no path the traverser still starts walking straight at the goal, into whatever is in
        // the way; stop that, it is how settlers ended up pressed into walls.
        pathTraverser.Stop();
        if (ladderPhase == LadderPhase.ToStart)
        {
            // Could not get to the ladder: try the walk again without it.
            triedLadders.Add(ladder!.Column);
            EndLadder();
            legTry = 0;
            StartLeg();
            return;
        }
        if (!onLeg && TryLadder()) return;
        failed = true;
        retryAtMs = entity.World.ElapsedMilliseconds + RetryDelayMs * (attempts - 1);
        if (!onLeg) Will?.NoPathFound();
    }

    /// <summary>
    /// No path to a goal well above or below: look for a ladder that leads there and walk to it. The game's
    /// pathfinder does not climb ladders, so the climb itself is steered by LadderClimb.
    /// </summary>
    bool TryLadder()
    {
        if (manager == null || !manager.Config.LadderClimbing || goal == null) return false;
        var climb = LadderClimb.Find(manager, entity, goal, System.Math.Max(2, manager.Config.LadderSearchRadius), triedLadders);
        // No ladder there: put one up against the cliff from the ladders the settler carries.
        if (climb == null && manager.Config.ScaffoldClimbing && Colony is { } colony && Citizen is { } citizen)
        {
            int have = manager.PackCount(citizen, ColonyManager.IsLadderStack);
            climb = manager.FindScaffoldSpot(colony, entity, goal, 4, have, triedLadders);
        }
        if (climb == null) return false;
        ladder = climb;
        if (manager.PathDebug) manager.Mod.Logger.Notification("[pathing] settler {0} at {1} climbs the ladder at {2} towards {3}", entity.EntityId, entity.Pos.AsBlockPos, climb.Column, goal.AsBlockPos);
        if (entity.Pos.SquareDistanceTo(climb.Start) < 0.8 * 0.8)
        {
            StartClimbing();
            return true;
        }
        // Until the climb, the walk goes to the ladder; StartLeg sets the real goal again afterwards.
        ladderPhase = LadderPhase.ToStart;
        goal = climb.Start;
        goalRange = 0.5f;
        attempts = 0;
        StartPath();
        return true;
    }

    void StartClimbing()
    {
        pathTraverser.Stop();
        if (ladder!.ToPlace != null && (Colony is not { } colony || Citizen is not { } citizen || !manager!.PlaceScaffold(colony, citizen, ladder)))
        {
            // The ladders could not go up after all: walk on without them.
            triedLadders.Add(ladder.Column);
            EndLadder();
            failed = true;
            retryAtMs = entity.World.ElapsedMilliseconds + RetryDelayMs;
            return;
        }
        ladderPhase = LadderPhase.Climbing;
        if (entity is EntitySettler { Body: { } body }) body.OnLadder = true;
        ladder!.Begin(entity);
        failed = false;
    }

    void EndLadder()
    {
        if (ladderPhase == LadderPhase.Climbing)
        {
            entity.Controls.WalkVector.Set(0, 0, 0);
            if (entity is EntitySettler { Body: { } body }) body.OnLadder = false;
        }
        ladderPhase = LadderPhase.None;
        ladder = null;
    }

    /// <summary>
    /// While a path search runs the task waits. When it is not walking it always carries on: the game's check
    /// alone is also false for a settler that has never walked since it was loaded, so a job that started
    /// without walking (a builder already standing at its building after the world loaded) never ran.
    /// </summary>
    public override bool CanContinueExecute() => goal == null || pathTraverser.Ready;

    public override void FinishExecute(bool cancelled)
    {
        pathTraverser.Stop();
        EndLadder();
        Will?.Stopped();
        goal = null;
        finalGoal = null;
        base.FinishExecute(cancelled);
    }

    /// <summary>Stops moving and stops the walk animation set by the task's "animation" config.</summary>
    protected void StopWalking()
    {
        pathTraverser.Stop();
        EndLadder();
        Will?.Stopped();
        goal = null;
        if (animMeta != null) entity.AnimManager.StopAnimation(animMeta.Code);
    }

    protected void PlayAnimation(string code)
    {
        if (!entity.AnimManager.IsAnimationActive(code)) entity.AnimManager.StartAnimation(code);
    }

    protected void StopAnimation(string code) => entity.AnimManager.StopAnimation(code);

    /// <summary>The settler's right hand; what is in it shows in the game and is the tool it works with.</summary>
    protected ItemSlot? Hand => entity.RightHandItemSlot;

    /// <summary>
    /// Pushes the hand slot to players' screens. The settler's inventory reaches clients through its
    /// watched attributes, which are only rewritten when stored, so an emptied hand would otherwise
    /// keep showing the old tool.
    /// </summary>
    protected void SyncHand()
    {
        Hand?.MarkDirty();
        entity.GetBehavior<EntityBehaviorVillagerInv>()?.storeInv();
    }

    protected void PutAwayHand(Colony colony, CitizenRecord citizen)
    {
        var hand = Hand;
        if (hand == null || hand.Empty) return;
        var stack = hand.Itemstack;
        manager!.AddToPack(colony, citizen, stack);
        // Inventory full: keep holding it rather than dropping a tool on the ground.
        hand.Itemstack = stack.StackSize > 0 ? stack : null;
        SyncHand();
    }

    /// <summary>What the settler is doing right now, in words, for the settler window.</summary>
    public virtual string Describe(Colony colony, CitizenRecord citizen) => Lang.Get("createacolony:activity-busy");

    /// <summary>
    /// Plays an animation by its shape code, such as a player tool animation copied in by
    /// SettlerAnimations, falling back to an animation from the entity config when the shape lacks it.
    /// </summary>
    protected string PlayShapeAnimation(string code, string fallback)
    {
        // The player's "smithing" swing (the prospecting pick's and the hammer's) went with prospectors
        // vanishing while taking a reading (Aaron, 0.75.4), so settlers swing those like a pickaxe.
        if (code == "smithing") code = "pickaxe";
        if (!SettlerAnimations.PlayerAnimations.Contains(code))
        {
            PlayAnimation(fallback);
            return fallback;
        }
        if (!entity.AnimManager.IsAnimationActive(code))
        {
            entity.AnimManager.StartAnimation(new AnimationMetaData
            {
                Code = code,
                Animation = code,
                Weight = 5,
                BlendMode = EnumAnimationBlendMode.Average
            }.Init());
        }
        return code;
    }
}
