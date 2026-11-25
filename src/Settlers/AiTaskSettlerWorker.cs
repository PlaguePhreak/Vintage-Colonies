using System.Collections.Generic;
using System.Linq;
using CreateAColony.Colonies;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using ItemMatch = System.Func<Vintagestory.API.Common.ItemStack, bool>;

namespace CreateAColony.Settlers;

/// <summary>
/// The working day of a settler who gathers from the land around its hut (forester, farmer). It needs
/// a chest at its hut and its tool; without a tool it asks couriers for one and waits. Then it walks
/// from job to job, carrying what it gathers, and takes it to the hut chest when its inventory is full
/// or there is nothing left to do. It keeps its tool and a stock of seeds; couriers take the rest.
/// </summary>
public abstract class AiTaskSettlerWorker : SettlerTaskBase
{
    const long IdleRetryMs = 5_000;
    protected const long WaitRetryMs = 15_000;
    /// <summary>A spot that could not be reached is left alone for this long.</summary>
    const long UnreachableForgetMs = 300_000;
    /// <summary>If no path gets closer, a worker still works on things this close.</summary>
    const float StretchRange = 5f;

    protected enum Step { FetchTool, FetchSupplies, Deposit, Work }

    protected Step step;
    protected BlockPos target = new BlockPos(0);
    /// <summary>How close the worker must stand to work on the target.</summary>
    protected float workRange = 2.5f;
    protected long nextSearchMs;
    protected int hutId;
    bool walking;
    float workTime;
    string? workAnim;
    ItemMatch supplyMatch = _ => false;
    int supplyWant;
    string? supplyName;
    readonly Dictionary<BlockPos, long> unreachable = new();
    /// <summary>A thing from the hut's Crafting recipes tab being made now (the Work step crafts it), or null for the job's own work.</summary>
    ColonyManager.SawKind? craftKind;
    ColonyManager.SawRecipe? craftRecipe;

    protected AiTaskSettlerWorker(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig) : base(entity, taskConfig, aiConfig) { }

    protected abstract string Job { get; }
    protected abstract string HutType { get; }
    protected abstract EnumTool Tool { get; }
    /// <summary>The seeds this job keeps a stock of in its inventory.</summary>
    protected abstract ItemMatch Stock { get; }
    protected abstract float WorkSeconds { get; }

    /// <summary>Picks the next thing to work on (calling GoWork or FetchSupplies) and sets the hut status. False when there is nothing.</summary>
    protected abstract bool PlanWork(Colony colony, CitizenRecord citizen, Building hut, List<BlockEntityContainer> storage);

    /// <summary>Does the work at the target once the work time is up.</summary>
    protected abstract void DoWork(Colony colony, CitizenRecord citizen, Building hut);

    /// <summary>What the settler is working on, for the settler window.</summary>
    protected abstract string DescribeWork();

    /// <summary>
    /// Work that comes before taking things to the hut storage, such as a cook putting what it cooked on the
    /// kitchen shelves. Calls GoWork and returns true when there is some.
    /// </summary>
    protected virtual bool PlanFirst(Colony colony, CitizenRecord citizen, Building hut) => false;

    /// <summary>
    /// True when the job's own work comes before the Crafting recipes tab: a Tailor mends first unless its
    /// Priorities tab puts something to make ahead of mending.
    /// </summary>
    protected virtual bool WorkBeforeRecipes(Colony colony, Building hut) => false;

    /// <summary>For a hut whose workers each have their own kind of work (a Prospector's Hut): true when this task is that work.</summary>
    protected virtual bool ForRole(int role) => true;

    /// <summary>Other things the worker keeps in its inventory instead of taking them to the hut.</summary>
    protected virtual bool AlsoKeeps(ItemStack stack) => false;

    /// <summary>The tool to hold for the work at hand; the job's own tool unless the job picks per task (diggers: shovel or pickaxe).</summary>
    protected virtual bool WantsInHand(ItemStack? stack) => IsToolStack(stack);

    /// <summary>Where to walk for the current target; the ground beside it unless a job says otherwise.</summary>
    protected virtual Vec3d WalkGoal() => target.ToVec3d().Add(0.5, 0, 0.5);

    /// <summary>False for a job that works by hand and has no tool to fetch (the potter).</summary>
    protected virtual bool NeedsTool => true;

    /// <summary>True when the current work is done with the tool in hand, false when it is done bare-handed.</summary>
    protected virtual bool WorkNeedsTool => true;

    /// <summary>True for the job's tool; most jobs go by tool type, the prospector by the prospecting pick itself.</summary>
    protected virtual bool IsToolStack(ItemStack? stack) => ColonyManager.ToolFits(stack, Tool, 0);

    /// <summary>Asks couriers to bring the job's tool to the hut.</summary>
    /// <summary>The work status shown while the worker has no tool.</summary>
    protected virtual string NeedsToolStatus => "needs-tool-" + Tool.ToString().ToLowerInvariant();

    protected virtual void RequestTool(Colony colony, Building hut) => manager!.EnsureToolRequest(colony, hut, hut, new ColonyManager.ToolNeed(Tool, 0));

    /// <summary>True when the worker can work on the target from where it stands, without walking.</summary>
    protected virtual bool CloseEnough() => entity.Pos.XYZ.DistanceTo(target.ToVec3d().Add(0.5, 0.5, 0.5)) <= workRange + 0.5;

    /// <summary>What a player would play for the current work.</summary>
    protected virtual string WorkAnimation(ItemSlot? hand) =>
        hand?.Itemstack?.Collectible.GetHeldTpHitAnimation(hand, entity) ?? "breakhand";

    public override bool ShouldExecute()
    {
        if (manager == null || entity.World.ElapsedMilliseconds < nextSearchMs) return false;
        nextSearchMs = entity.World.ElapsedMilliseconds + IdleRetryMs;
        return Plan();
    }

    bool Plan()
    {
        var colony = Colony;
        var citizen = Citizen;
        if (colony == null || citizen == null || citizen.JobCode != Job || !ForRole(citizen.WorkRole)) return false;
        if (manager!.IsBrokenDown(citizen)) return false; // too unhappy to work
        craftRecipe = null;

        var hut = manager!.EnsureWorkplace(colony, citizen, HutType);
        if (hut == null) return false;
        hutId = hut.BuildingId;

        var storage = manager.HutStorage(colony, hut).Cast<BlockEntityContainer>().ToList();
        if (storage.Count == 0)
        {
            SetStatus("no-hut-chest");
            return Wait();
        }

        if (PlanFirst(colony, citizen, hut)) return true;

        // Hands full: take the harvest home first.
        if (manager.PackFull(citizen) && HasSurplus(citizen)) return Go(Step.Deposit, storage[0].Pos);
        // Passing the hut: drop off what it does not need (a harvest, things picked up) on the way.
        int near = manager.Config.DepositNearHut;
        if (near > 0 && HasSurplus(citizen) && storage[0].Pos.DistanceSqTo(entity.Pos.X, entity.Pos.Y, entity.Pos.Z) < near * near)
        {
            return Go(Step.Deposit, storage[0].Pos);
        }

        if (NeedsTool && !HasTool(citizen))
        {
            var toolChest = storage.FirstOrDefault(c => c.Inventory.Any(s => IsToolStack(s.Itemstack)))
                // Not in the hut: one in the Town Hall's storage or the warehouse is fetched by the worker (0.201.0).
                ?? manager.SpareToolChest(colony, s => IsToolStack(s));
            if (toolChest != null) return Go(Step.FetchTool, toolChest.Pos);
            if (HasSurplus(citizen)) return Go(Step.Deposit, storage[0].Pos);
            // The colony's starting kit, handed out once per tool.
            if (manager.GiveStarterTool(colony, citizen, Tool, 0)) return Wait();
            RequestTool(colony, hut);
            SetStatus(NeedsToolStatus);
            return Wait();
        }

        if (WorkBeforeRecipes(colony, hut))
        {
            if (PlanWork(colony, citizen, hut, storage)) return true;
            if (PlanRecipe(colony, citizen, hut, storage)) return true;
        }
        else
        {
            if (PlanRecipe(colony, citizen, hut, storage)) return true;
            if (PlanWork(colony, citizen, hut, storage)) return true;
        }

        // Nothing left to do: bring the harvest home and wait for things to grow.
        if (HasSurplus(citizen)) return Go(Step.Deposit, storage[0].Pos);
        return Wait();
    }

    /// <summary>
    /// The hut's Crafting recipes tab (ColonyManager.HutRecipes.cs): when the colony is short of something
    /// set there, the worker crafts it at the hut if it carries what it takes, or fetches that from the hut
    /// storage. If it is only in the warehouse, couriers are asked for it and the job's own work goes on.
    /// </summary>
    bool PlanRecipe(Colony colony, CitizenRecord citizen, Building hut, List<BlockEntityContainer> storage)
    {
        var m = manager!;
        craftKind = null;
        if (!m.HasHutRecipes(hut.Type)) return false;
        var wanted = m.WantedRecipes(colony, hut, citizen);
        if (wanted.Count == 0) return false;

        foreach (var want in wanted)
        {
            var ready = want.Recipes.FirstOrDefault(r => r.Station == null && m.HasRecipeInputs(citizen, r, Hand?.Itemstack));
            if (ready == null) continue;
            craftKind = want;
            craftRecipe = ready;
            workRange = 2.5f;
            SetStatus("recipes-crafting");
            return GoWork(hut.HutPos);
        }

        foreach (var want in wanted)
        {
            int batches = System.Math.Clamp((m.RecipeWant(colony, hut, want.Key) - m.RecipeStock(colony, hut, want, citizen)) / System.Math.Max(1, want.Sample.StackSize) + 1,
                1, m.Config.HutRecipeBatch);
            foreach (var r in want.Recipes)
            {
                if (r.Station != null) continue; // the cook makes these at the station (AiTaskSettlerCook.PlanStation)
                // A recipe a player taught the hut: the worker gets the tool it takes from the hut storage, or asks couriers for one.
                if (r.CustomId != 0 && FetchTaughtTool(colony, citizen, hut, storage, want, r)) return true;
                // The tool must be carried already; the worker never fetches one just for a game recipe.
                if (!m.CarriesTool(citizen, r.Tool, Hand?.Itemstack) || !m.CarriesTool(citizen, r.Tool2, Hand?.Itemstack)) continue;
                foreach (var (part, quantity) in r.Inputs)
                {
                    ItemMatch fits = s => part.SatisfiesAsIngredient(s, false);
                    int missing = quantity * batches - m.PackCount(citizen, fits);
                    if (missing <= 0) continue;
                    var chest = storage.FirstOrDefault(c => c.Inventory.Any(s => !s.Empty && fits(s.Itemstack)));
                    if (chest == null || m.PackFull(citizen)) continue;
                    craftKind = want;
                    SetStatus("recipes-fetching");
                    return FetchSupplies(chest.Pos, fits, missing);
                }
            }
            foreach (var r in want.Recipes)
            {
                if (r.Station != null) continue;
                var codes = r.Inputs.Select(i => (Code: m.CodeInWarehouse(colony, s => i.Part.SatisfiesAsIngredient(s, false)), i.Quantity)).ToList();
                if (codes.Any(c => c.Code == null)) continue;
                foreach (var (code, quantity) in codes) m.EnsureCraftRequest(colony, hut, code!, quantity * batches);
                break;
            }
        }
        return false;
    }

    /// <summary>
    /// The tools a taught recipe takes that the worker does not carry: it walks to the hut storage for one there,
    /// or asks couriers to bring one from the warehouse. True when it walks to get one.
    /// </summary>
    bool FetchTaughtTool(Colony colony, CitizenRecord citizen, Building hut, List<BlockEntityContainer> storage, ColonyManager.SawKind want, ColonyManager.SawRecipe recipe)
    {
        var m = manager!;
        foreach (var tool in new[] { recipe.Tool, recipe.Tool2 })
        {
            if (tool == null || m.CarriesTool(citizen, tool, Hand?.Itemstack)) continue;
            ItemMatch fits = s => tool.SatisfiesAsIngredient(s, false);
            var chest = storage.FirstOrDefault(c => c.Inventory.Any(s => !s.Empty && fits(s.Itemstack)));
            if (chest != null && !m.PackFull(citizen))
            {
                craftKind = want;
                SetStatus("fetching-tools");
                return FetchSupplies(chest.Pos, fits, 1);
            }
            if (m.CodeInWarehouse(colony, fits) is { } code) m.EnsureCraftRequest(colony, hut, code, 1);
        }
        return false;
    }

    /// <summary>
    /// Picks up one of each kind of tool its recipes may take (config HutRecipeTools, by the first part of the
    /// code) that is in the hut storage and that it does not carry yet: a Tailor's needle and awl. True when it
    /// walks to get one.
    /// </summary>
    protected bool FetchRecipeTools(Colony colony, CitizenRecord citizen, Building hut, List<BlockEntityContainer> storage)
    {
        var m = manager!;
        if (m.PackFull(citizen) || m.Config.HutRecipeTools.GetValueOrDefault(hut.Type) is not { } kinds) return false;
        foreach (var kind in kinds)
        {
            ItemMatch fits = s => ColonyManager.FirstPart(s.Collectible.Code) == kind;
            if ((Hand?.Itemstack is { } held && fits(held)) || m.PackCount(citizen, fits) > 0) continue;
            var chest = storage.FirstOrDefault(c => c.Inventory.Any(s => !s.Empty && fits(s.Itemstack)));
            if (chest == null) continue;
            SetStatus("fetching-tools");
            return FetchSupplies(chest.Pos, fits, 1);
        }
        return false;
    }

    /// <summary>True for a tool its recipes may take, which it keeps in its inventory.</summary>
    protected bool IsRecipeTool(ItemStack stack) =>
        manager!.Config.HutRecipeTools.GetValueOrDefault(HutType) is { } kinds && kinds.Contains(ColonyManager.FirstPart(stack.Collectible.Code));

    bool Wait()
    {
        nextSearchMs = entity.World.ElapsedMilliseconds + WaitRetryMs;
        return false;
    }

    protected void SetStatus(string status)
    {
        if (hutId != 0) manager!.WorkStatus[hutId] = status;
        if (Citizen is { } citizen) manager!.WorkerStatus[citizen.CitizenId] = status;
    }

    bool Go(Step next, BlockPos pos)
    {
        step = next;
        target = pos.Copy();
        return true;
    }

    protected bool GoWork(BlockPos pos) => Go(Step.Work, pos);

    /// <summary>Walks to this chest of the hut to put away what the worker does not keep (into the hut storage, coolest first for food).</summary>
    protected bool GoDeposit(BlockPos chest) => Go(Step.Deposit, chest);

    /// <summary>Walks to the chest to take up to want items the work needs.</summary>
    protected bool FetchSupplies(BlockPos chest, ItemMatch match, int want)
    {
        supplyMatch = match;
        supplyWant = want;
        supplyName = SupplyName(chest, match);
        return Go(Step.FetchSupplies, chest);
    }

    /// <summary>What the worker is fetching, for its "Doing right now" line: the first matching item in the chest.</summary>
    string? SupplyName(BlockPos chest, ItemMatch match)
    {
        if (entity.World.BlockAccessor.GetBlockEntity(chest) is not IBlockEntityContainer container) return null;
        foreach (var slot in container.Inventory)
        {
            if (!slot.Empty && match(slot.Itemstack)) return slot.Itemstack.GetName();
        }
        return null;
    }

    /// <summary>True when a spot could not be reached a little while ago, so the worker tries something else first.</summary>
    protected bool IsUnreachable(BlockPos pos)
    {
        if (!unreachable.TryGetValue(pos, out long until)) return false;
        if (entity.World.ElapsedMilliseconds < until) return true;
        unreachable.Remove(pos);
        return false;
    }

    /// <summary>Leaves a spot alone for a while, as if it could not be reached.</summary>
    protected void MarkUnreachable(BlockPos pos) => unreachable[pos.Copy()] = entity.World.ElapsedMilliseconds + UnreachableForgetMs;

    // A prospecting pick counts as a pickaxe to the game, but only jobs that use one (IsToolStack) keep it as their tool.
    bool IsMyTool(ItemStack s) => NeedsTool && (IsToolStack(s) || s.Collectible.Tool == Tool && !ColonyManager.IsProspectingPick(s));

    bool HasTool(CitizenRecord citizen) =>
        IsToolStack(Hand?.Itemstack) || manager!.PackCount(citizen, s => IsToolStack(s)) > 0;

    /// <summary>What the job keeps, and what the thing it is crafting for the Crafting recipes tab takes.</summary>
    bool Keeps(ItemStack s) => AlsoKeeps(s) || craftKind?.IsInput(s) == true
        || craftKind?.Recipes.Any(r => r.CustomId != 0 && (r.Tool?.SatisfiesAsIngredient(s, false) == true || r.Tool2?.SatisfiesAsIngredient(s, false) == true)) == true;

    /// <summary>True when the settler carries anything it does not keep: a harvest, or seeds beyond its stock.</summary>
    bool HasSurplus(CitizenRecord citizen)
    {
        var items = manager!.PackItems(citizen);
        return items.Any(s => !IsMyTool(s) && !Stock(s) && !Keeps(s) && !ColonyManager.IsLadderStack(s))
            || items.Where(Stock).Sum(s => s.StackSize) > manager.SeedReserve(Job);
    }

    public override void StartExecute()
    {
        base.StartExecute();
        BeginStep();
    }

    void BeginStep()
    {
        workTime = 0;
        if (step != Step.Work && Colony is { } colony && Citizen is { } citizen) PutAwayHand(colony, citizen);
        bool crafting = step == Step.Work && craftRecipe != null;
        if (crafting)
        {
            // At the hut block, whatever the job's own work spot would be.
            var spot = target.ToVec3d().Add(0.5, 0, 0.5);
            walking = entity.Pos.XYZ.DistanceTo(target.ToVec3d().Add(0.5, 0.5, 0.5)) > workRange + 0.5;
            if (walking) WalkTo(spot, workRange, () => spot);
            return;
        }
        walking = !(step == Step.Work && CloseEnough());
        if (!walking) return;
        if (step == Step.Work) WalkTo(WalkGoal(), workRange, WalkGoal);
        else WalkTo(target.ToVec3d().Add(0.5, 0, 0.5), 1.5f);
    }

    public override bool ContinueExecute(float dt)
    {
        if (walking)
        {
            if (PathFailed)
            {
                if (step != Step.Work || entity.Pos.XYZ.DistanceTo(target.ToVec3d().Add(0.5, 0.5, 0.5)) > StretchRange)
                {
                    unreachable[target.Copy()] = entity.World.ElapsedMilliseconds + UnreachableForgetMs;
                    SetStatus("unreachable");
                    nextSearchMs = entity.World.ElapsedMilliseconds + IdleRetryMs;
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
        }

        var colony = Colony;
        var citizen = Citizen;
        var hut = colony?.Buildings.Find(b => b.BuildingId == hutId);
        if (colony == null || citizen == null || hut == null) return false;

        switch (step)
        {
            case Step.FetchTool:
                if (!Take(colony, citizen, s => IsToolStack(s), 1)) return Wait();
                manager!.TopUpLadders(colony, citizen, manager.HutStorage(colony, hut).Cast<BlockEntityContainer>());
                manager.TakeSpareWeapon(colony, citizen, manager.HutStorage(colony, hut).Cast<BlockEntityContainer>());
                break;
            case Step.FetchSupplies:
                if (!Take(colony, citizen, supplyMatch, supplyWant)) return Wait();
                break;
            case Step.Deposit:
                if (!Deposit(colony, citizen, hut))
                {
                    SetStatus("hut-chest-full");
                    return Wait();
                }
                // Ladders for cliffs on the way (ColonyManager.Scaffold.cs).
                manager!.TopUpLadders(colony, citizen, manager.HutStorage(colony, hut).Cast<BlockEntityContainer>());
                manager.TakeSpareWeapon(colony, citizen, manager.HutStorage(colony, hut).Cast<BlockEntityContainer>());
                break;
            case Step.Work:
                if (workAnim == null)
                {
                    if (craftRecipe != null) PrepareCraftHand(colony, citizen, craftRecipe);
                    else PrepareHand(colony, citizen);
                    workAnim = PlayShapeAnimation(craftRecipe != null && Hand?.Itemstack == null ? "interactstatic" : WorkAnimation(Hand), "build");
                }
                workTime += dt;
                // A skilled worker gets the same work done in less time (ColonyManager.Skills.cs).
                float seconds = craftRecipe != null ? manager!.Config.HutRecipeSecondsPerCraft : WorkSeconds;
                if (workTime < seconds * manager!.WorkTimeFactor(colony, citizen)) return true;
                if (craftRecipe != null)
                {
                    if (craftRecipe.Tool != null && Hand is { } hand && craftRecipe.Tool.SatisfiesAsIngredient(hand.Itemstack, false)) manager.WearTool(entity, hand);
                    manager.CraftHutRecipe(colony, hut, citizen, craftRecipe);
                }
                else DoWork(colony, citizen, hut);
                manager.GainWorkXp(colony, citizen, Jobs.WorkJob(citizen));
                StopWorkAnimation();
                break;
        }

        // Straight on to the next job; only walk if it is out of reach.
        if (!Plan()) return false;
        BeginStep();
        return true;
    }

    /// <summary>Takes up to want matching items from the chest at the target into the inventory. False if nothing moved.</summary>
    bool Take(Colony colony, CitizenRecord citizen, ItemMatch match, int want)
    {
        if (entity.World.BlockAccessor.GetBlockEntity(target) is not BlockEntityContainer chest) return false;
        int moved = 0;
        while (moved < want)
        {
            var part = manager!.TakeItems(new[] { chest }, match, want - moved);
            if (part == null) break;
            int n = manager.AddToPack(colony, citizen, part);
            moved += n;
            if (part.StackSize > 0)
            {
                manager.PutItems(chest, part); // inventory full: the rest stays in the chest
                break;
            }
        }
        return moved > 0;
    }

    /// <summary>Puts everything but the tool, the seed stock and what the job keeps into the hut storage. False if they are too full to take it all.</summary>
    bool Deposit(Colony colony, CitizenRecord citizen, Building hut)
    {
        var storage = manager!.HutStorage(colony, hut).Cast<BlockEntityContainer>().ToList();
        manager.UnloadPack(colony, citizen, storage, s => !IsMyTool(s) && !Stock(s) && !Keeps(s) && !ColonyManager.IsLadderStack(s));

        int extra = manager.PackCount(citizen, Stock) - manager.SeedReserve(Job);
        if (extra > 0 && manager.TakeFromPack(colony, citizen, Stock, extra) is { } seeds)
        {
            foreach (var chest in storage)
            {
                if (seeds.StackSize <= 0) break;
                manager.PutItems(chest, seeds);
            }
            if (seeds.StackSize > 0) manager.AddToPack(colony, citizen, seeds);
        }
        return !HasSurplus(citizen);
    }

    /// <summary>Holds the tool for work done with it and puts it away for work done by hand.</summary>
    void PrepareHand(Colony colony, CitizenRecord citizen)
    {
        var hand = Hand;
        if (hand == null) return;
        if (!WorkNeedsTool || !NeedsTool)
        {
            PutAwayHand(colony, citizen);
            return;
        }
        if (WantsInHand(hand.Itemstack)) return;
        var tool = manager!.TakeFromPack(colony, citizen, s => WantsInHand(s), 1);
        if (tool == null) return;
        PutAwayHand(colony, citizen);
        hand.Itemstack = tool;
        SyncHand();
    }

    /// <summary>Holds the recipe's tool when it takes one, and nothing otherwise.</summary>
    void PrepareCraftHand(Colony colony, CitizenRecord citizen, ColonyManager.SawRecipe recipe)
    {
        var hand = Hand;
        if (hand == null) return;
        if (recipe.Tool == null)
        {
            PutAwayHand(colony, citizen);
            return;
        }
        if (recipe.Tool.SatisfiesAsIngredient(hand.Itemstack, false)) return;
        var tool = manager!.TakeFromPack(colony, citizen, s => recipe.Tool.SatisfiesAsIngredient(s, false), 1);
        if (tool == null) return;
        PutAwayHand(colony, citizen);
        hand.Itemstack = tool;
        SyncHand();
    }

    /// <summary>True when the tool is in hand for work that needs it.</summary>
    protected bool HoldsTool => IsToolStack(Hand?.Itemstack);

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
        return step switch
        {
            Step.FetchTool => Lang.Get("createacolony:activity-worker-tool", Lang.Get("createacolony:tool-" + Tool.ToString().ToLowerInvariant())),
            Step.FetchSupplies => supplyName != null ? Lang.Get("createacolony:activity-worker-supplies", supplyName) : Lang.Get("createacolony:activity-worker-supplies-any"),
            Step.Deposit => Lang.Get("createacolony:activity-worker-deposit"),
            _ when craftRecipe != null && craftKind != null => Lang.Get("createacolony:activity-worker-craft", craftKind.Sample.GetName()),
            _ => DescribeWork()
        };
    }
}
