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
/// A gatherer works from a Gatherer's Hut: it forages around the hut by hand, picking ripe berries and fruit,
/// edible mushrooms, and loose stones, flints and sticks, and brings them to the hut storage. When the colony
/// is waiting for dry grass it cuts tall grass first. Wherever it works it puts the plants it sees on the
/// colony's map. With nothing to pick it walks out now and then just to look around.
/// </summary>
public class AiTaskSettlerGatherer : AiTaskSettlerWorker
{
    /// <summary>True while walking out only to look around, with nothing to pick there.</summary>
    bool looking;
    /// <summary>True while going to cut tall grass because the colony needs dry grass.</summary>
    bool cutting;
    /// <summary>Where it last looked around on the move, and when (ms), so it marks what it passes.</summary>
    Vec3d? lastLookPos;
    long lastLookMs;

    public AiTaskSettlerGatherer(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig) : base(entity, taskConfig, aiConfig) { }

    protected override string Job => Jobs.Gatherer;
    protected override string HutType => BuildingTypes.Gatherer;
    protected override EnumTool Tool => EnumTool.Knife;
    protected override ItemMatch Stock => _ => false;
    protected override float WorkSeconds => looking ? manager!.Config.ExplorerSecondsPerLook : manager!.Config.GathererSecondsPerPick;
    protected override bool NeedsTool => false;
    protected override bool WorkNeedsTool => false;

    /// <summary>
    /// It carries a knife if its hut has one, for recipes that take it (config HutRecipeTools), and while food is
    /// low the roots and firewood it is taking to the fire.
    /// </summary>
    protected override bool AlsoKeeps(ItemStack stack) => IsRecipeTool(stack)
        || (roasting && (manager!.IsCookableRoot(stack) || ColonyManager.IsFirewood(stack)));

    /// <summary>A knife in its hand or pack: for cutting grass and taking bush cuttings.</summary>
    bool HasKnife(CitizenRecord citizen) => ColonyManager.IsKnife(Hand?.Itemstack) || manager!.PackItems(citizen).Any(ColonyManager.IsKnife);

    /// <summary>True while the colony's food is low and there are roots to roast.</summary>
    bool roasting;
    /// <summary>What it does at the fire pit it walks to: put roots on, or take roasted ones off.</summary>
    enum FireWork { None, Cook, Collect }
    FireWork fireWork;

    /// <summary>
    /// Roasting roots (ColonyManager.Roots.cs): roasted roots on the fire pit by the hut are taken off first. While
    /// food is low, roots it carries or the hut holds go on the fire, with firewood from the hut or the warehouse.
    /// </summary>
    bool PlanRoast(Colony colony, CitizenRecord citizen, Building hut, List<BlockEntityContainer> storage)
    {
        var m = manager!;
        fireWork = FireWork.None;
        roasting = false;
        if (!m.Config.GathererDigsRoots || m.HomeFirepit(colony, hut) is not { } pit || IsUnreachable(pit.Pos)) return false;
        workRange = 2.5f;
        if (ColonyManager.FireHasFood(pit) && pit.inputSlot.Empty)
        {
            fireWork = FireWork.Collect;
            SetStatus("roots-collect");
            return GoWork(pit.Pos);
        }
        if (!m.FoodLow(colony, entity) || !pit.inputSlot.Empty) return false;
        bool carries = m.PackCount(citizen, m.IsCookableRoot) > 0;
        var rootChest = carries ? null : storage.FirstOrDefault(c => c.Inventory.Any(s => !s.Empty && m.IsCookableRoot(s.Itemstack)));
        if (!carries && rootChest == null) return false;
        roasting = true;
        if (!m.CanFuel(colony, hut, citizen, pit))
        {
            SetStatus("roots-no-firewood");
            return false;
        }
        if (rootChest != null && !m.PackFull(citizen))
        {
            SetStatus("roots-fetch");
            return FetchSupplies(rootChest.Pos, m.IsCookableRoot, m.Config.CookBatch);
        }
        if (!carries) return false;
        if (!pit.IsBurning && pit.fuelSlot.Empty && m.PackCount(citizen, ColonyManager.IsFirewood) == 0
            && m.HomeSupply(colony, hut, ColonyManager.IsFirewood) is { } wood)
        {
            SetStatus("roots-fetch");
            return FetchSupplies(wood.Pos, ColonyManager.IsFirewood, m.Config.CookFirewood);
        }
        fireWork = FireWork.Cook;
        SetStatus("roots-roast");
        return GoWork(pit.Pos);
    }

    protected override bool PlanFirst(Colony colony, CitizenRecord citizen, Building hut) =>
        FetchRecipeTools(colony, citizen, hut, manager!.HutStorage(colony, hut).Cast<BlockEntityContainer>().ToList());

    protected override bool PlanWork(Colony colony, CitizenRecord citizen, Building hut, List<BlockEntityContainer> storage)
    {
        bool lookOnly = hut.GatherMode == GatherModes.Look;
        if (!lookOnly && PlanRoast(colony, citizen, hut, storage))
        {
            looking = cutting = false;
            return true;
        }
        // Home before dark (0.200.0): as bedtime nears it only goes as far out as it can still walk back from, and
        // when it is farther out than that already it turns for home. Near the hut it works until bedtime.
        double reach = System.Math.Max(24, manager!.HomeReach(colony, citizen));
        if (HutDistance(hut, entity.Pos.AsBlockPos) > reach && storage.Count > 0)
        {
            looking = cutting = false;
            SetStatus("heading-home");
            return GoDeposit(storage[0].Pos);
        }
        bool Skip(BlockPos p) => IsUnreachable(p) || HutDistance(hut, p) > reach;
        int rank = int.MaxValue;
        bool knife = HasKnife(citizen);
        var spot = lookOnly ? null : manager!.NextForage(colony, hut, entity.Pos.XYZ, Skip, knife, out rank, entity);
        // Dry grass the colony is waiting for is one of its requests: it goes where requests are on the collecting order.
        bool grassFirst = spot == null || ColonyManager.GatherOrder(hut).IndexOf(ColonyManager.GatherRequests) <= rank;
        if (!lookOnly && grassFirst && manager!.GrassShort(colony) > 0 && !knife)
        {
            // Cutting grass takes a knife: ask for one, and pick other things meanwhile.
            manager.EnsureToolRequest(colony, hut, hut, new ColonyManager.ToolNeed(EnumTool.Knife, 0));
            if (spot == null)
            {
                SetStatus("needs-tool-knife");
                return false;
            }
        }
        else if (!lookOnly && grassFirst && manager!.GrassShort(colony) > 0 && manager.NextGrass(colony, hut, entity.Pos.XYZ, Skip) is { } grass)
        {
            looking = false;
            cutting = true;
            workRange = 2.5f;
            SetStatus("cutting-grass");
            return GoWork(grass);
        }
        cutting = false;
        if (spot != null)
        {
            looking = false;
            workRange = 2.5f;
            SetStatus("gathering");
            return GoWork(spot);
        }
        spot = manager!.ForageWalk(colony, hut, Skip);
        if (spot != null)
        {
            looking = true;
            workRange = 3f;
            SetStatus("foraging-look");
            return GoWork(spot);
        }
        SetStatus("gathered-all");
        return false;
    }

    static double HutDistance(Building hut, BlockPos p)
    {
        double dx = p.X - hut.HutPos.X, dz = p.Z - hut.HutPos.Z;
        return System.Math.Sqrt(dx * dx + dz * dz);
    }

    protected override void DoWork(Colony colony, CitizenRecord citizen, Building hut)
    {
        var m = manager!;
        if (fireWork != FireWork.None)
        {
            var work = fireWork;
            fireWork = FireWork.None;
            if (entity.World.BlockAccessor.GetBlockEntity(target) is not BlockEntityFirepit pit) return;
            if (work == FireWork.Collect && ColonyManager.FireHasFood(pit)) m.EmptyFirepit(colony, citizen, pit);
            else if (work == FireWork.Cook && pit.inputSlot.Empty
                && m.PackItems(citizen).FirstOrDefault(m.IsCookableRoot) is { } root && m.KitchenKindOf(root) is { } kind)
            {
                m.LoadFirepit(colony, citizen, pit, kind);
            }
            return;
        }
        if (cutting)
        {
            if (!manager!.CutGrass(colony, hut, citizen, target)) MarkUnreachable(target);
        }
        else if (!looking && !manager!.Gather(colony, hut, citizen, target)) MarkUnreachable(target);
        manager!.LookAround(colony, hut, target, LookRadius(hut));
        manager.NoteForageAround(colony, hut, target, System.Math.Max(4, manager.Config.GathererLookRadius));
    }

    /// <summary>
    /// On the move it keeps looking around: every few seconds, once it has gone a few blocks, it marks what it
    /// sees on the map, so a walk out to look (or to pick something) marks everything along the way.
    /// </summary>
    public override bool ContinueExecute(float dt)
    {
        long now = entity.World.ElapsedMilliseconds;
        if (now - lastLookMs >= LookEveryMs && (lastLookPos == null || lastLookPos.SquareDistanceTo(entity.Pos.XYZ) >= LookEveryBlocks * LookEveryBlocks))
        {
            lastLookMs = now;
            lastLookPos = entity.Pos.XYZ;
            if (Colony is { } colony && colony.Buildings.Find(b => b.BuildingId == hutId) is { } hut)
            {
                manager!.LookAround(colony, hut, entity.Pos.AsBlockPos, LookRadius(hut));
            }
        }
        return base.ContinueExecute(dt);
    }

    const long LookEveryMs = 3000;
    const int LookEveryBlocks = 5;

    /// <summary>Set to only look around, it looks farther.</summary>
    int LookRadius(Building hut) => System.Math.Max(2, manager!.Config.GathererLookRadius) * (hut.GatherMode == GatherModes.Look ? 2 : 1);

    protected override string DescribeWork() => Lang.Get(cutting ? "createacolony:activity-gatherer-grass"
        : looking ? "createacolony:activity-gatherer-look" : "createacolony:activity-gatherer-pick");
}
