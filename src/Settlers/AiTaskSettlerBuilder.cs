using System.Collections.Generic;
using System.Linq;
using CreateAColony.Colonies;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace CreateAColony.Settlers;

/// <summary>
/// A builder works from a Builder's Hut. It takes the oldest work order in the colony and goes
/// through the blueprint one block at a time: clearing what is in the way, then placing blocks.
/// Materials come from the chests at its own hut; whatever is missing it asks couriers to bring from
/// the warehouse, and waits. Blocks it clears go into the same chests.
/// </summary>
public class AiTaskSettlerBuilder : SettlerTaskBase
{
    const long IdleRetryMs = 5_000;
    const long WaitRetryMs = 15_000;
    /// <summary>How close a builder must stand to place or clear a block.</summary>
    const float WorkRange = 4.5f;
    /// <summary>How far below a block a builder looks for the ground to stand on while working on it.</summary>
    const int GroundSearchDepth = 12;

    enum Step { Fetch, Stash, FetchTool, Work, StepAside }

    Step step;
    BlockPos target = new BlockPos(0);
    BuildStep buildStep;
    int orderId;
    /// <summary>The trader's house being taken down (TeardownOrder.Id), 0 while on a building's work order.</summary>
    int teardownId;
    /// <summary>Blocks of a trader's house the builder could not reach; tried again once the rest is down.</summary>
    readonly HashSet<BlockPos> unreachable = new();
    ColonyManager.ToolNeed neededTool;
    /// <summary>Taking a building down, the last block is its hut block, which goes back to the player.</summary>
    bool takingHutBlock;

    bool walking;
    float workTime;
    /// <summary>Set by Work when the builder stands where its next block goes: the spot to move to.</summary>
    Vec3d? stepAsideTo;
    /// <summary>While stepping aside: the block the builder must get its body out of.</summary>
    BlockPos? clearOf;
    /// <summary>The animation playing while working on the current block.</summary>
    string? workAnim;
    long nextSearchMs;

    public AiTaskSettlerBuilder(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig) : base(entity, taskConfig, aiConfig) { }

    public override bool ShouldExecute()
    {
        if (manager == null || entity.World.ElapsedMilliseconds < nextSearchMs) return false;
        nextSearchMs = entity.World.ElapsedMilliseconds + IdleRetryMs;
        return Plan();
    }

    /// <summary>Decides the next thing to do and sets step and target. False when there is nothing to do now.</summary>
    bool Plan()
    {
        var colony = Colony;
        var citizen = Citizen;
        if (colony == null || citizen == null || citizen.JobCode != Jobs.Builder) return false;
        if (manager!.IsBrokenDown(citizen)) return false; // too unhappy to work

        var hut = manager!.EnsureBuilderHut(colony, citizen);
        if (hut == null)
        {
            // Only this builder's own orders: the others are another builder's, or wait for one (0.201.0).
            foreach (var b in colony.Buildings.Where(b => b.HasWorkOrder && b.BuilderCitizenId == citizen.CitizenId)) manager.BuildStatus[b.BuildingId] = "no-builder-hut";
            return false;
        }

        bool carrying = !manager.PackEmpty(citizen);
        var (order, teardown) = manager.NextBuilderJob(colony, citizen);
        teardownId = 0;
        if (order == null)
        {
            orderId = 0;
            if (teardown != null) return PlanTeardown(colony, citizen, hut, teardown) || carrying && StashAt(colony, hut);
            return carrying && StashAt(colony, hut);
        }
        orderId = order.BuildingId;
        takingHutBlock = false;
        if (order.Removing) return PlanRemoval(colony, citizen, hut, order);

        var blueprint = manager.Blueprints.Get(order, order.TargetLevel);
        if (blueprint == null)
        {
            manager.BuildStatus[order.BuildingId] = "no-blueprint";
            return false;
        }
        if (!manager.FindNextStep(colony, order, blueprint, out buildStep, out var pos))
        {
            manager.CompleteWorkOrder(colony, order);
            return carrying && StashAt(colony, hut);
        }
        // The whole list is asked for at the start and kept up to date (0.201.0), not only once the builder runs out:
        // couriers and the Mayor see what the build needs while the site is still being cleared.
        long nowMs = entity.World.ElapsedMilliseconds;
        if (askedOrderId != order.BuildingId || nowMs >= nextAskMs)
        {
            askedOrderId = order.BuildingId;
            nextAskMs = nowMs + AskEveryMs;
            manager.EnsureMaterialRequests(colony, order, hut, citizen, blueprint);
        }
        slowWork = false;

        // The right tool first: a builder digs with a shovel and fells with an axe, like a player.
        if (ToolFirst(colony, citizen, hut, order, pos) is { } toolPlan) return toolPlan;

        // Everything the next block costs must be in hand; a roof block takes its frame and materials together.
        var costs = manager.NextPlacement(colony, buildStep, pos).Costs;
        var missing = costs.Where(c => manager.PackCount(citizen, ColonyManager.Matcher(c.Code)) < c.Count).ToList();
        if (missing.Count == 0)
        {
            manager.BuildStatus[order.BuildingId] = buildStep.BlockId == 0 ? "clearing" : "building";
            return Go(Step.Work, pos);
        }

        var fits = missing.Select(c => ColonyManager.Matcher(c.Code)).ToList();
        var source = manager.HutStorage(colony, hut).FirstOrDefault(c => c.Inventory.Any(s => !s.Empty && fits.Any(m => m(s.Itemstack))));
        if (source != null)
        {
            manager.BuildStatus[order.BuildingId] = "building";
            return Go(Step.Fetch, source.Pos);
        }

        // This block waits for something not brought yet: put in another one it has the materials for meanwhile.
        var hutStorage = manager.HutStorage(colony, hut).Cast<BlockEntityContainer>().ToList();
        if (manager.StepWithMaterials(colony, order, blueprint, citizen, hutStorage) is { } other)
        {
            buildStep = other.Step;
            manager.BuildStatus[order.BuildingId] = "building";
            var otherCosts = manager.NextPlacement(colony, other.Step, other.Pos).Costs;
            if (otherCosts.All(c => manager.PackCount(citizen, ColonyManager.Matcher(c.Code)) >= c.Count)) return Go(Step.Work, other.Pos);
            var otherFits = otherCosts.Select(c => ColonyManager.Matcher(c.Code)).ToList();
            if (hutStorage.FirstOrDefault(c => c.Inventory.Any(s => !s.Empty && otherFits.Any(m => m(s.Itemstack)))) is { } chest) return Go(Step.Fetch, chest.Pos);
        }

        // Nothing to build with: ask couriers and wait.
        bool hasStorage = manager.EnsureMaterialRequests(colony, order, hut, citizen, blueprint);
        manager.BuildStatus[order.BuildingId] = hasStorage ? "waiting-materials" : "no-hut-chest";
        nextSearchMs = entity.World.ElapsedMilliseconds + WaitRetryMs;
        return false;
    }

    /// <summary>
    /// When the block at pos needs a tool the builder lacks: goes for one in the hut's storage, or asks couriers
    /// for one and waits. Null when the builder has what it needs.
    /// </summary>
    bool? ToolFirst(Colony colony, CitizenRecord citizen, Building hut, Building order, BlockPos pos)
    {
        if (manager!.ToolNeededAt(colony, buildStep, pos) is not { } toolNeed || HasTool(citizen, toolNeed)) return null;
        neededTool = toolNeed;
        var toolChest = manager.HutStorage(colony, hut)
            .FirstOrDefault(c => c.Inventory.Any(s => ColonyManager.ToolFits(s.Itemstack, toolNeed.Type, toolNeed.Tier)))
            // Not in the hut: one in the Town Hall's storage or the warehouse is fetched by the builder (0.201.0).
            ?? manager.SpareToolChest(colony, s => ColonyManager.ToolFits(s, toolNeed.Type, toolNeed.Tier));
        if (toolChest != null) return Go(Step.FetchTool, toolChest.Pos);
        // The colony's starting kit: a flint axe, shovel and hoe, handed out once.
        if (manager.GiveStarterTool(colony, citizen, toolNeed.Type, toolNeed.Tier)) return null;
        // No pickaxe in the colony and nobody to make one: stone is cleared by hand, slowly.
        if (toolNeed.Type == EnumTool.Pickaxe && manager.ClearsStoneByHand(colony))
        {
            slowWork = true;
            return null;
        }

        bool canAsk = manager.EnsureToolRequest(colony, order, hut, toolNeed);
        manager.BuildStatus[order.BuildingId] = canAsk ? "needs-tool-" + toolNeed.Type.ToString().ToLowerInvariant() : "no-hut-chest";
        nextSearchMs = entity.World.ElapsedMilliseconds + WaitRetryMs;
        return false;
    }

    /// <summary>
    /// Remove hut: the next block of the building to take down, from the top, as a step that clears it; when
    /// none is left, the hut block itself.
    /// </summary>
    bool PlanRemoval(Colony colony, CitizenRecord citizen, Building hut, Building order)
    {
        var spot = manager!.NextRemovalSpot(colony, order);
        takingHutBlock = spot == null;
        var pos = spot ?? order.HutPos;
        buildStep = new BuildStep(pos.X - order.HutPos.X - order.XOffset, pos.Y - order.HutPos.Y - order.YOffset, pos.Z - order.HutPos.Z - order.ZOffset, 0);
        if (!takingHutBlock && ToolFirst(colony, citizen, hut, order, pos) is { } toolPlan) return toolPlan;
        manager.BuildStatus[order.BuildingId] = "removing";
        return Go(Step.Work, pos);
    }

    /// <summary>
    /// A trader's old house: the next block from the top. Its land is loaded first when nobody is near it; when no
    /// block is left the order is done. Blocks out of reach are left for last.
    /// </summary>
    bool PlanTeardown(Colony colony, CitizenRecord citizen, Building hut, TeardownOrder order)
    {
        teardownId = order.Id;
        takingHutBlock = false;
        var (spot, loaded) = manager!.NextTeardownSpot(colony, order, unreachable);
        if (!loaded)
        {
            manager.BuildStatus[-teardownId] = "loading";
            nextSearchMs = entity.World.ElapsedMilliseconds + WaitRetryMs;
            return false;
        }
        if (spot == null)
        {
            if (unreachable.Count > 0)
            {
                // Only blocks it could not reach are left: try them again in a while.
                unreachable.Clear();
                manager.BuildStatus[-teardownId] = "unreachable";
                nextSearchMs = entity.World.ElapsedMilliseconds + WaitRetryMs;
                return false;
            }
            manager.FinishTeardown(colony, order);
            teardownId = 0;
            return false;
        }
        buildStep = new BuildStep(0, 0, 0, 0);
        if (ToolFirst(colony, citizen, hut, hut, spot) is { } toolPlan)
        {
            manager.BuildStatus[-teardownId] = manager.BuildStatus.GetValueOrDefault(hut.BuildingId, "removing");
            return toolPlan;
        }
        manager.BuildStatus[-teardownId] = "removing";
        return Go(Step.Work, spot);
    }

    /// <summary>The order whose materials were last asked for, and when to bring the requests up to date again.</summary>
    int askedOrderId;
    long nextAskMs;
    const long AskEveryMs = 60_000;
    /// <summary>Clearing stone by hand, without a pickaxe: slower.</summary>
    bool slowWork;

    bool StashAt(Colony colony, Building hut)
    {
        var chest = manager!.HutStorage(colony, hut).FirstOrDefault();
        return chest != null && Go(Step.Stash, chest.Pos);
    }

    bool Go(Step next, BlockPos pos)
    {
        step = next;
        target = pos.Copy();
        return true;
    }

    public override void StartExecute()
    {
        base.StartExecute();
        stepAsideTo = null;
        clearOf = null;
        BeginStep();
    }

    void BeginStep()
    {
        workTime = 0;
        if (step != Step.Work && Colony is { } colony && Citizen is { } citizen) PutAwayHand(colony, citizen);
        var center = target.ToVec3d().Add(0.5, 0.5, 0.5);
        walking = !(step == Step.Work && entity.Pos.XYZ.DistanceTo(center) <= WorkRange);
        if (!walking) return;
        if (step == Step.StepAside && stepAsideTo != null)
        {
            WalkTo(stepAsideTo, 0.1f);
            return;
        }
        if (step == Step.Work)
        {
            WalkTo(WorkSpot(), 1f);
            return;
        }
        WalkTo(target.ToVec3d().Add(0.5, 0, 0.5), 1.5f);
    }

    /// <summary>
    /// Where to stand to work on the target: on the ground below it, beside it. A block high up (a roof,
    /// the top of a wall) is worked on from the ground underneath, within the builder's reach.
    /// </summary>
    Vec3d WorkSpot()
    {
        var ba = entity.World.BlockAccessor;
        var ground = target.Copy();
        for (int i = 0; i < GroundSearchDepth && ground.Y > 1; i++)
        {
            var below = ground.DownCopy();
            var boxes = ba.GetBlock(below).GetCollisionBoxes(ba, below);
            if (boxes != null && boxes.Length > 0) break;
            ground = below;
        }
        return manager!.FreeSpotNear(entity, ground, 3, target) ?? target.ToVec3d().Add(0.5, 0, 0.5);
    }

    bool InReach() => entity.Pos.XYZ.DistanceTo(target.ToVec3d().Add(0.5, 0.5, 0.5))
        <= manager!.Config.BuilderReach * (Colony?.Buildings.Find(b => b.BuildingId == orderId) is { } o && manager.LongReachStep(o, target) ? 2 : 1);

    void MarkUnreachable()
    {
        var o = Colony?.Buildings.Find(b => b.BuildingId == orderId);
        if (o != null) manager!.BuildStatus[o.BuildingId] = "unreachable";
        if (o != null && teardownId == 0 && step == Step.Work && !o.Removing)
        {
            // A block it cannot get to counts double: set aside after two goes, the rest of the build carries on.
            manager!.StepFailed(o, target, 2, "reach");
            nextSearchMs = entity.World.ElapsedMilliseconds + IdleRetryMs;
            return;
        }
        if (teardownId != 0)
        {
            unreachable.Add(target.Copy());
            // Other blocks of the house may be in reach: look again soon.
            nextSearchMs = entity.World.ElapsedMilliseconds + IdleRetryMs;
            return;
        }
        nextSearchMs = entity.World.ElapsedMilliseconds + WaitRetryMs;
    }

    public override bool ContinueExecute(float dt)
    {
        if (walking)
        {
            if (PathFailed)
            {
                // A failed step aside falls through: the StepAside step shuffles the builder over itself.
                // If no path gets closer, a builder still works on blocks within reach, so it never gets stuck on its own walls.
                if (step != Step.StepAside && (step != Step.Work || !InReach()))
                {
                    MarkUnreachable();
                    return false;
                }
            }
            else if (!Arrived)
            {
                KeepWalking();
                return true;
            }
            StopWalking();
            walking = false;
            // Standing on the ground below a block can still leave it out of reach (too high up).
            if (step == Step.Work && !InReach())
            {
                MarkUnreachable();
                return false;
            }
        }

        var colony = Colony;
        var citizen = Citizen;
        if (colony == null || citizen == null) return false;

        switch (step)
        {
            case Step.Fetch:
                Fetch(colony, citizen);
                break;
            case Step.FetchTool:
                FetchTool(colony, citizen);
                break;
            case Step.Stash:
                Stash(colony, citizen);
                if (!manager!.PackEmpty(citizen)) return false;
                break;
            case Step.StepAside:
                // The pathfinder counts "close enough" as arrived, which can leave the builder half inside
                // the block it wants to place. Shuffle the last bit over to the free spot.
                if (clearOf != null && stepAsideTo != null && manager!.Overlaps(entity, clearOf)
                    && entity.Pos.XYZ.DistanceTo(stepAsideTo) < 2.5)
                {
                    entity.TeleportToDouble(stepAsideTo.X, stepAsideTo.Y, stepAsideTo.Z);
                }
                stepAsideTo = null;
                clearOf = null;
                break;
            case Step.Work:
                if (workAnim == null)
                {
                    PrepareHand(colony, citizen);
                    workAnim = PlayShapeAnimation(WorkAnimationCode(colony), "build");
                }
                workTime += dt;
                if (workTime < manager!.Config.BuilderSecondsPerBlock * manager.WorkTimeFactor(colony, citizen) * (slowWork ? System.Math.Max(1f, manager.Config.NoPickaxeSlowdown) : 1f)) return true;
                int before = entity.World.BlockAccessor.GetBlock(target).Id;
                Work(colony, citizen);
                StopWorkAnimation();
                CountTry(colony, before);
                if (stepAsideTo != null)
                {
                    clearOf = target.Copy();
                    Go(Step.StepAside, stepAsideTo.AsBlockPos);
                    BeginStep();
                    return true;
                }
                break;
        }

        // Straight on to the next block; only walk if it is out of reach.
        if (!Plan()) return false;
        BeginStep();
        return true;
    }

    /// <summary>
    /// Loads up at the hut: puts back what the order no longer needs, then takes the material for the
    /// next block and as much of the rest of the order as the inventory holds, so one trip goes a long way.
    /// </summary>
    void Fetch(Colony colony, CitizenRecord citizen)
    {
        var order = colony.Buildings.Find(b => b.BuildingId == orderId);
        var hut = manager!.EnsureBuilderHut(colony, citizen);
        if (order == null || hut == null) return;
        var blueprint = manager.Blueprints.Get(order, order.TargetLevel);
        var costs = manager.NextPlacement(colony, buildStep, manager.StepPos(order, buildStep)).Costs;
        if (blueprint == null || costs.Count == 0) return;

        var storage = manager.HutStorage(colony, hut).Cast<BlockEntityContainer>().ToList();
        var needs = manager.MaterialsNeeded(colony, order, blueprint);
        var matchers = needs.Keys.Select(ColonyManager.Matcher).ToList();
        manager.UnloadPack(colony, citizen, storage, s => !matchers.Any(m => m(s)) && !ColonyManager.IsBuilderTool(s));

        var nowMatchers = costs.Select(c => ColonyManager.Matcher(c.Code)).ToList();
        foreach (var cost in costs)
        {
            var current = ColonyManager.Matcher(cost.Code);
            int currentNeed = needs.TryGetValue(cost.Code, out var n) ? n.Count : cost.Count;
            if (Load(colony, citizen, storage, current, currentNeed) == 0 && manager.PackCount(citizen, current) < cost.Count)
            {
                // Full of other materials: make room for what the next block needs right now.
                manager.UnloadPack(colony, citizen, storage, s => !nowMatchers.Any(m => m(s)) && !ColonyManager.IsBuilderTool(s));
                Load(colony, citizen, storage, current, currentNeed);
            }
        }
        foreach (var (code, (_, count)) in needs)
        {
            if (costs.Any(c => c.Code == code)) continue;
            Load(colony, citizen, storage, ColonyManager.Matcher(code), count);
        }
    }

    /// <summary>Takes items from the chests into the inventory until it holds want of them or is full. Returns how many moved.</summary>
    int Load(Colony colony, CitizenRecord citizen, List<BlockEntityContainer> storage, System.Func<ItemStack, bool> match, int want)
    {
        int moved = 0;
        int missing = want - manager!.PackCount(citizen, match);
        while (missing > 0)
        {
            var part = manager.TakeItems(storage, match, missing);
            if (part == null) break;
            int n = manager.AddToPack(colony, citizen, part);
            moved += n;
            missing -= n;
            if (part.StackSize > 0)
            {
                foreach (var chest in storage)
                {
                    if (part.StackSize <= 0) break;
                    manager.PutItems(chest, part);
                }
                break;
            }
        }
        return moved;
    }

    bool HasTool(CitizenRecord citizen, ColonyManager.ToolNeed need) =>
        ColonyManager.ToolFits(Hand?.Itemstack, need.Type, need.Tier)
        || manager!.PackCount(citizen, s => ColonyManager.ToolFits(s, need.Type, need.Tier)) > 0;

    /// <summary>Takes a tool that fits the job from the hut chest into the inventory.</summary>
    void FetchTool(Colony colony, CitizenRecord citizen)
    {
        if (entity.World.BlockAccessor.GetBlockEntity(target) is not BlockEntityContainer chest) return;
        var tool = manager!.TakeItems(new[] { chest }, s => ColonyManager.ToolFits(s, neededTool.Type, neededTool.Tier), 1);
        if (tool == null) return;
        manager.AddToPack(colony, citizen, tool);
        if (tool.StackSize > 0) manager.PutItems(chest, tool); // inventory full: the tool waits in the chest
    }

    /// <summary>Holds the right tool while clearing a block, and puts tools away otherwise, like a player switching hotbar slots.</summary>
    void PrepareHand(Colony colony, CitizenRecord citizen)
    {
        var hand = Hand;
        if (hand == null) return;
        var current = entity.World.BlockAccessor.GetBlock(target);
        bool clearing = current.Id != 0 && !manager!.KeepsBlock(colony, current, buildStep.BlockId, target);
        var need = clearing || manager!.Tilling(buildStep, target) ? manager!.ToolNeededAt(colony, buildStep, target) : null;
        if (need == null)
        {
            PutAwayHand(colony, citizen);
            return;
        }
        if (ColonyManager.ToolFits(hand.Itemstack, need.Value.Type, need.Value.Tier)) return;
        var tool = manager!.TakeFromPack(colony, citizen, s => ColonyManager.ToolFits(s, need.Value.Type, need.Value.Tier), 1);
        if (tool == null) return;
        PutAwayHand(colony, citizen);
        hand.Itemstack = tool;
        SyncHand();
    }

    void Stash(Colony colony, CitizenRecord citizen)
    {
        var hut = manager!.EnsureBuilderHut(colony, citizen);
        if (hut == null) return;
        // Hut chests full: wait instead of walking back and forth.
        if (!manager.UnloadPack(colony, citizen, manager.HutStorage(colony, hut))) nextSearchMs = entity.World.ElapsedMilliseconds + WaitRetryMs;
    }

    /// <summary>How many times in a row the builder found itself where its block goes; at 3 it builds elsewhere first.</summary>
    int inOwnWay;

    void Work(Colony colony, CitizenRecord citizen)
    {
        if (teardownId != 0)
        {
            TakeDownTraderBlock(colony, citizen);
            return;
        }
        var order = colony.Buildings.Find(b => b.BuildingId == orderId);
        var hut = manager!.EnsureBuilderHut(colony, citizen);
        if (order == null || hut == null) return;
        if (order.Removing)
        {
            TakeDown(colony, citizen, order, hut);
            return;
        }
        if (manager.IsStepDone(colony, buildStep, target)) return;

        var ba = entity.World.BlockAccessor;
        var current = ba.GetBlock(target);
        if (current.Id != 0 && !manager.KeepsBlock(colony, current, buildStep.BlockId, target))
        {
            var toolNeed = manager.ToolNeededAt(colony, buildStep, target);
            var hand = Hand;
            if (toolNeed != null && !ColonyManager.ToolFits(hand?.Itemstack, toolNeed.Value.Type, toolNeed.Value.Tier)) return;
            manager.BreakForBuild(colony, hut, target);
            manager.GainWorkXp(colony, citizen, Jobs.Builder);
            // Every block cleared wears the tool down; a broken tool sends the builder back for another.
            if (toolNeed != null && hand?.Itemstack != null)
            {
                hand.Itemstack.Collectible.DamageItem(entity.World, entity, hand);
                SyncHand(); // a tool that just broke must vanish from the model too
            }
        }
        if (buildStep.BlockId == 0) return;

        // Soil where farmland goes: till it with the hoe in hand, as a player does.
        if (manager.Tilling(buildStep, target))
        {
            var hoe = Hand;
            if (!ColonyManager.ToolFits(hoe?.Itemstack, EnumTool.Hoe, 0) && manager.Config.BuildersNeedTools) return;
            if (!manager.TillSoil(target)) return;
            if (hoe?.Itemstack != null && ColonyManager.ToolFits(hoe.Itemstack, EnumTool.Hoe, 0))
            {
                hoe.Itemstack.Collectible.DamageItem(entity.World, entity, hoe);
                SyncHand();
            }
            manager.GainWorkXp(colony, citizen, Jobs.Builder);
            if (manager.EnsureBuilderHut(colony, citizen) is { } tilledFor) manager.AddStat(tilledFor, "blocks-placed");
            return;
        }

        var (place, costs) = manager.NextPlacement(colony, buildStep, target);
        var paid = new List<ItemStack>();
        foreach (var cost in costs)
        {
            var taken = manager.TakeFromPack(colony, citizen, ColonyManager.Matcher(cost.Code), cost.Count);
            if (taken != null) paid.Add(taken);
            if (taken == null || taken.StackSize < cost.Count)
            {
                foreach (var stack in paid) manager.AddToPack(colony, citizen, stack);
                return;
            }
        }
        var used = paid.FirstOrDefault();
        place = manager.SwapToMaterial(colony, place, used);
        // A Master Mason now and then makes a block out of nothing: the materials go back in the pack.
        if (manager.TraitAdd(citizen, "freeBlock") is var free && free > 0 && entity.World.Rand.NextDouble() < free)
        {
            foreach (var stack in paid) manager.AddToPack(colony, citizen, stack.Clone());
        }
        // Standing where the block goes (Aaron saw a builder stuck on a wall top, over and over walking to a spot it
        // could not reach and back): hop straight to the nearest free spot beside it, as a player steps aside.
        if (manager.EntityInTheWay(place, target) is { EntityId: var selfId } && selfId == entity.EntityId
            && manager.FreeSpotNear(entity, target, 3, target) is { } hop)
        {
            StopWalking();
            entity.TeleportToDouble(hop.X, hop.Y, hop.Z);
        }
        if (manager.EntityInTheWay(place, target) is { } inTheWay)
        {
            foreach (var stack in paid) manager.AddToPack(colony, citizen, stack);
            if (inTheWay.EntityId == entity.EntityId && ++inOwnWay < 3 && manager.FreeSpotNear(entity, target, 3, target) is { } spot)
            {
                // Standing where the block goes: step out of the way first.
                stepAsideTo = spot;
            }
            else
            {
                // Someone else is standing there: build elsewhere and come back to this block later.
                order.Cursor++;
                manager.MarkDirty(colony);
            }
            return;
        }
        inOwnWay = 0;
        var placedFrom = manager.PlacementStacks(buildStep, place, target, paid);
        manager.PlaceForBuild(place.Id, target, placedFrom?.FirstOrDefault());
        manager.FinishPlacement(place, target);
        if (place.Id == buildStep.BlockId) manager.RestoreSavedDetails(buildStep, target, placedFrom);
        manager.GainWorkXp(colony, citizen, Jobs.Builder);
        if (manager.EnsureBuilderHut(colony, citizen) is { } builderHut) manager.AddStat(builderHut, "blocks-placed");
    }

    /// <summary>Takes one block of a building being removed down into the hut's storage, or at the end the hut block itself.</summary>
    void TakeDown(Colony colony, CitizenRecord citizen, Building order, Building hut)
    {
        if (takingHutBlock)
        {
            if (target.Equals(order.HutPos)) manager!.TakeHutBlock(colony, order, hut);
            return;
        }
        if (entity.World.BlockAccessor.GetBlock(target).Id == 0) return;
        var toolNeed = manager!.ToolNeededAt(colony, buildStep, target);
        var hand = Hand;
        if (toolNeed != null && !ColonyManager.ToolFits(hand?.Itemstack, toolNeed.Value.Type, toolNeed.Value.Tier)) return;
        manager.BreakForBuild(colony, hut, target);
        manager.GainWorkXp(colony, citizen, Jobs.Builder);
        manager.AddStat(hut, "blocks-removed");
        if (toolNeed != null && hand?.Itemstack != null)
        {
            hand.Itemstack.Collectible.DamageItem(entity.World, entity, hand);
            SyncHand();
        }
    }

    /// <summary>Takes one block of a trader's old house down: clutter is destroyed, the rest goes to the hut.</summary>
    void TakeDownTraderBlock(Colony colony, CitizenRecord citizen)
    {
        var order = colony.Teardowns.Find(t => t.Id == teardownId);
        var hut = manager!.EnsureBuilderHut(colony, citizen);
        if (order == null || hut == null) return;
        var toolNeed = manager.ToolNeededAt(colony, buildStep, target);
        var hand = Hand;
        if (toolNeed != null && !ColonyManager.ToolFits(hand?.Itemstack, toolNeed.Value.Type, toolNeed.Value.Tier)) return;
        manager.TakeDownTraderBlock(colony, order, hut, target);
        manager.GainWorkXp(colony, citizen, Jobs.Builder);
        manager.AddStat(hut, "blocks-removed");
        if (toolNeed != null && hand?.Itemstack != null)
        {
            hand.Itemstack.Collectible.DamageItem(entity.World, entity, hand);
            SyncHand();
        }
    }

    /// <summary>What a player would play: the held tool's swing when clearing a block, the place motion when building.</summary>
    string WorkAnimationCode(Colony colony)
    {
        var current = entity.World.BlockAccessor.GetBlock(target);
        bool clearing = current.Id != 0 && !manager!.KeepsBlock(colony, current, buildStep.BlockId, target);
        if (!clearing) return SettlerAnimations.PlayerAnimations.Contains("placeblock") ? "placeblock" : "interactstatic";
        var hand = Hand;
        return hand?.Itemstack?.Collectible.GetHeldTpHitAnimation(hand, entity) ?? "breakhand";
    }

    /// <summary>After working at a block of a work order: forgets its failures if it is done, else counts one.</summary>
    void CountTry(Colony colony, int blockBefore)
    {
        if (teardownId != 0 || stepAsideTo != null) return;
        var order = colony.Buildings.Find(b => b.BuildingId == orderId);
        if (order == null || order.Removing || !order.HasWorkOrder) return;
        // Done, or changed on the way there (a fire pit gets its firewood next, wattle its daub): no failure.
        if (manager!.IsStepDone(colony, buildStep, target, order) || entity.World.BlockAccessor.GetBlock(target).Id != blockBefore) manager.StepDone(order, target);
        else manager.StepFailed(order, target);
    }

    void StopWorkAnimation()
    {
        if (workAnim != null) StopAnimation(workAnim);
        workAnim = null;
    }

    public override void FinishExecute(bool cancelled)
    {
        StopWorkAnimation();
        var colony = Colony;
        var citizen = Citizen;
        if (colony != null && citizen != null) PutAwayHand(colony, citizen);
        base.FinishExecute(cancelled);
    }

    public override string Describe(Colony colony, CitizenRecord citizen)
    {
        if (teardownId != 0 && colony.Teardowns.Find(t => t.Id == teardownId) is { } teardown)
        {
            return step == Step.Work ? Lang.Get("createacolony:activity-builder-remove", ColonyManager.TeardownName(teardown))
                : Lang.Get("createacolony:activity-builder-work", ColonyManager.TeardownName(teardown));
        }
        var order = colony.Buildings.Find(b => b.BuildingId == orderId);
        string building = order == null ? "" : order.Removing || order.Repairing ? ColonyManager.BuildingName(order) : Lang.Get("createacolony:building-" + order.Type) + " " + order.TargetLevel;
        string key = order?.Removing == true && step == Step.Work ? "activity-builder-remove" : step switch
        {
            Step.Fetch => "activity-builder-fetch",
            Step.FetchTool => "activity-builder-tool",
            Step.Stash => "activity-builder-stash",
            Step.StepAside => "activity-builder-aside",
            _ => "activity-builder-work"
        };
        return Lang.Get("createacolony:" + key, building);
    }
}
