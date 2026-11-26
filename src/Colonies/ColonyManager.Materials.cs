using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace CreateAColony.Colonies;

/// <summary>
/// What a builder spends on each block of a blueprint, and what counts as built. Most blocks cost the
/// block itself. A fire pit is made the way a player makes one: dry grass on the ground, then four
/// firewood added one at a time, and daub as a wattle fence with raw daub put on it coat by coat. When the
/// colony allows it, any wood or stone type (or daub colour) will do in place of the one the blueprint asks
/// for, and the builder places the block in the type it brought.
/// </summary>
public partial class ColonyManager
{
    /// <summary>One thing to pay: an item code (a pattern with * when any type will do), a sample for its picture, and how many.</summary>
    public readonly record struct MaterialCost(string Code, ItemStack Sample, int Count);

    /// <summary>Variant groups a builder may swap: oak for pine, granite for andesite.</summary>
    static readonly string[] SwappableVariants = { "wood", "rock" };
    /// <summary>A fire pit's stage once it is finished (cold, lit or burnt out); 1 to 4 are under construction.</summary>
    const int FirepitReady = 5;
    const int FirepitFirewood = 4;
    /// <summary>The variant of a fence that says which of its sides join a neighbour.</summary>
    const string FenceJoins = "type";

    readonly Dictionary<(int BlockId, bool AnyType), MaterialCost?> pickCostCache = new();

    static string? VariantOf(CollectibleObject c, string key) =>
        c.Variant != null && c.Variant.TryGetValue(key, out var value) ? value : null;

    // ---- What counts as built -------------------------------------------------------------

    /// <summary>
    /// True when the block standing there will do for the blueprint's block: a finished fire pit (cold,
    /// lit or burnt out all count), a fence however it is joined to its neighbours, or the same block in
    /// another wood or stone when the colony allows it.
    /// </summary>
    public bool Fulfils(Colony colony, Block current, int targetId, BlockPos pos)
    {
        if (current.Id == 0 || targetId == 0) return false;
        var target = sapi.World.GetBlock(targetId);
        if (target == null) return false;
        target = VariedDaub(target, pos);
        // A Stone Bake Oven block is the same block whether its fire is lit or the oven is hot (ColonyManager.StoneOven.cs).
        if (IsStoneOvenBlock(target) || IsStoneOvenBlock(current)) return OvenRestingBlock(current).Id == OvenRestingBlock(target).Id
            || (colony.AnyWoodOrStone && SameButStone(OvenRestingBlock(current), OvenRestingBlock(target)));
        if (current is BlockFirepit have && target is BlockFirepit want) return have.Stage >= Math.Min(want.Stage, FirepitReady);
        // Daub is built up coat by coat (ColonyManager.WattleDaub.cs): a smoother coat, or another colour when any will do, counts.
        bool daubFits = DaubFulfils(colony, current, target, out bool isDaub);
        if (isDaub) return daubFits;
        // A fence's code says which sides it joins ("type": n, ew, nesw, empty...), and the game changes it
        // as neighbours are built. The blueprint's joins are from wherever it was saved, so they never count.
        if (current is BlockFence && target is BlockFence && VariantOf(current, FenceJoins) is { } joins
            && sapi.World.GetBlock(target.CodeWithVariant(FenceJoins, joins)) is { } joined)
        {
            if (joined.Id == current.Id) return true;
            target = joined;
        }
        // A gate or door is the same one open or shut, and with or without snow on it: settlers open gates as they
        // walk through, so a gate saved closed must not be torn down and put back each time it is opened.
        if (current is BlockBaseDoor && target is BlockBaseDoor && SameIgnoring(current, target, "state", "cover")) return true;
        // A torch is a torch, lit or burned out.
        if (IsTorch(target) && IsTorch(current) && LitTorch(current.Code).Equals(LitTorch(target.Code))) return true;
        // A torch put up where the colony had no lantern stays, lit or not.
        if (Config.LanternTorchFallback && IsLantern(target) && IsTorch(current)) return true;
        // Ground in a blueprint is any soil: grass comes and goes, and how fertile it is depends on the spot. Natural
        // ground of another kind (rock, gravel, sand) will do too, so builders do not dig out stone to put dirt there.
        if (IsSoil(target) && (IsSoil(current) || IsFarmland(current) || IsNaturalGround(current))) return true;
        // Farmland is farmland however wet or fertile it is (Aaron, 0.97.0): it dries out and wears down by itself.
        if (IsFarmland(target) && IsFarmland(current)) return true;
        // A VS Roofing roof block turns bare or filled by itself as its materials load; either one is the roof.
        if (target.Code.Domain == RoofingDomain && VariantOf(current, "type") is { } roofState
            && target.CodeWithVariant("type", roofState).Equals(current.Code)) return true;
        return colony.AnyWoodOrStone && SameButStone(current, target);
    }

    /// <summary>True when the two blocks differ only in their wood or stone.</summary>
    static bool SameButStone(Block current, Block target)
    {
        foreach (string v in SwappableVariants)
        {
            string? theirs = VariantOf(current, v);
            if (theirs != null && VariantOf(target, v) != null && target.CodeWithVariant(v, theirs).Equals(current.Code)) return true;
        }
        return false;
    }

    /// <summary>True when the block is a half-built step towards the blueprint's block, so it must not be torn down.</summary>
    public bool InProgress(Colony colony, Block current, int targetId, BlockPos pos) =>
        IsSoil(current) && sapi.World.GetBlock(targetId) is { } field && IsFarmland(field)
        || current is BlockFirepit pit && !IsStoneOvenBlock(pit) && pit.Stage < FirepitReady && sapi.World.GetBlock(targetId) is BlockFirepit want && !IsStoneOvenBlock(want)
        || DaubInProgress(colony, current, VariedDaub(sapi.World.GetBlock(targetId), pos));

    /// <summary>True when the two blocks' codes differ at most in the given variant groups.</summary>
    bool SameIgnoring(Block current, Block target, params string[] groups)
    {
        var keep = groups.Where(g => current.Variant.ContainsKey(g) && target.Variant.ContainsKey(g)).ToArray();
        if (keep.Length == 0) return current.Code.Equals(target.Code);
        var values = keep.Select(g => current.Variant[g]).ToArray();
        return target.CodeWithVariants(keep, values).Equals(current.Code);
    }

    /// <summary>True when a builder leaves this block standing for the step.</summary>
    public bool KeepsBlock(Colony colony, Block current, int targetId, BlockPos pos) =>
        current.Id == targetId || Fulfils(colony, current, targetId, pos) || InProgress(colony, current, targetId, pos);

    /// <summary>Rock, gravel, sand, clay and other natural ground blocks.</summary>
    static bool IsNaturalGround(Block block) => block.SideSolid.All && block.BlockMaterial is EnumBlockMaterial.Soil
        or EnumBlockMaterial.Gravel or EnumBlockMaterial.Sand or EnumBlockMaterial.Stone;

    /// <summary>A vanilla torch (lit, extinct or burned out), on the ground or a wall.</summary>
    public static bool IsTorch(Block block) => block.Code?.Domain == "game" && block.Code.Path.StartsWith("torch-");

    /// <summary>The lit version of a torch's code: torches burn out by themselves, so blueprints ask for lit ones.</summary>
    public static AssetLocation LitTorch(AssetLocation code) =>
        new(code.Domain, code.Path.Replace("-burnedout-", "-lit-").Replace("-extinct-", "-lit-"));

    /// <summary>Plain soil blocks (soil-medium-normal and the like), not farmland, packed dirt or rammed earth.</summary>
    static bool IsSoil(Block block) => block.Code?.Domain == "game" && block.Code.Path.StartsWith("soil-");

    /// <summary>Tilled farmland, wet or dry, of any fertility.</summary>
    static bool IsFarmland(Block block) => block.Code?.Domain == "game" && block.Code.Path.StartsWith("farmland-");

    /// <summary>The code to ask for when any wood or stone will do: the item's own code with its wood or rock left open.</summary>
    static string OpenType(CollectibleObject collectible)
    {
        foreach (string v in SwappableVariants)
        {
            if (VariantOf(collectible, v) != null) return collectible.CodeWithVariant(v, "*").ToString();
        }
        return collectible.Code.ToString();
    }

    /// <summary>Costs with their wood or stone left open, so a roof's planks are asked for with the rest of the planks.</summary>
    static IReadOnlyList<MaterialCost> OpenTypes(IReadOnlyList<MaterialCost> costs) =>
        costs.Select(c => c.Code.Contains('*') || c.Code.Contains('|') || c.Code.Contains('{') ? c : c with { Code = OpenType(c.Sample.Collectible) }).ToList();

    // ---- What to place and what it costs --------------------------------------------------

    /// <summary>
    /// The next block to place on the way to the step's block, and everything placing it costs (empty:
    /// nothing). Most blocks cost one item; a roof block from VS Roofing costs its frame and materials, and a
    /// furniture block or banner pole its item in the blueprint's wood and colours (plus the pole's banner).
    /// </summary>
    public (Block Place, IReadOnlyList<MaterialCost> Costs) NextPlacement(Colony colony, BuildStep step, BlockPos pos)
    {
        var target = sapi.World.GetBlock(step.BlockId);
        // An oven's heat source goes down with its fire out and its other blocks cold, as they come from crafting.
        target = OvenRestingBlock(target);
        if (target is BlockFirepit && !IsStoneOvenBlock(target))
        {
            if (sapi.World.BlockAccessor.GetBlock(pos) is BlockFirepit pit && !IsStoneOvenBlock(pit) && pit.Stage < FirepitReady)
            {
                var next = sapi.World.GetBlock(pit.CodeWithParts(pit.NextStageCodePart)) ?? target;
                return (next, Costs(ItemCost("game:firewood", 1)));
            }
            var start = sapi.World.GetBlock(new AssetLocation("game:firepit-construct1"));
            if (start != null) return (start, Costs(ItemCost("game:drygrass", 1)));
        }
        // Farmland: soil first, then the builder tills it with a hoe (ColonyManager.Tilling.cs).
        if (IsFarmland(target))
        {
            var here = sapi.World.BlockAccessor.GetBlock(pos);
            if (IsSoil(here)) return (TilledFrom(here) ?? target, Array.Empty<MaterialCost>());
            var soil = sapi.World.GetBlock(new AssetLocation("game", "soil-" + target.LastCodePart(0) + "-none"))
                ?? sapi.World.GetBlock(new AssetLocation("game:soil-medium-none"));
            if (soil != null) return (soil, Costs(PickCost(colony, soil, pos)));
        }
        if (DaubPlacement(colony, VariedDaub(target, pos), pos) is { } daub) return daub;
        // No lantern in the colony: a torch goes up instead (ColonyManager.Torches.cs).
        if (TorchForLantern(colony, target, pos) is { } torch) return (torch, Costs(TorchCost(torch)));
        if (Config.BuildersNeedMaterials && PlacedItemCosts(step, target, pos, colony.AnyWoodOrStone) is { } placed) return (target, placed);
        // A roof's planks and logs, like every other block's, may be any wood when the colony allows it. A chiseled
        // block keeps its own materials: its shape is copied as saved, so other wood or stone would not show.
        if (Config.BuildersNeedMaterials && SavedCost(step, target) is { } saved)
            return (target, colony.AnyWoodOrStone && target is not BlockMicroBlock ? OpenTypes(saved) : saved);
        // A torch (0.202.0): one that is lit or has only gone out will do, so the unlit torches huts make count. The
        // builder lights it as it puts it up.
        if (IsTorch(target) && !target.Code.Path.Contains("-burnedout-")) return (target, Costs(TorchCost(target)));
        return (target, Costs(PickCost(colony, target, pos)));
    }

    static IReadOnlyList<MaterialCost> Costs(MaterialCost? cost) =>
        cost is { } one ? new[] { one } : Array.Empty<MaterialCost>();

    /// <summary>Everything still to pay for one step, for the "Items needed" list and courier requests.</summary>
    public IEnumerable<MaterialCost> RemainingCost(Colony colony, BuildStep step, BlockPos pos)
    {
        var target = sapi.World.GetBlock(step.BlockId);
        if (target is BlockFirepit && !IsStoneOvenBlock(target) && ItemCost("game:firewood", 1) is { } firewood)
        {
            var current = sapi.World.BlockAccessor.GetBlock(pos);
            int stage = current is BlockFirepit pit && !IsStoneOvenBlock(pit) ? pit.Stage : 0;
            int wood = stage == 0 ? FirepitFirewood : FirepitReady - stage;
            if (stage == 0 && ItemCost("game:drygrass", 1) is { } grass) yield return grass;
            if (wood > 0) yield return firewood with { Count = wood };
            yield break;
        }
        if (DaubRemaining(colony, VariedDaub(target, pos), pos) is { } daub)
        {
            foreach (var cost in daub) yield return cost;
            yield break;
        }
        foreach (var cost in NextPlacement(colony, step, pos).Costs) yield return cost;
    }

    /// <summary>The block actually placed: the blueprint's, or the same block in the wood or stone the builder brought.</summary>
    public Block SwapToMaterial(Colony colony, Block place, ItemStack? used)
    {
        // Any soil will do, so the soil goes down as the kind the builder brought (and farmland tilled from it is as fertile).
        if (IsSoil(place) && used?.Block is { } broughtSoil && IsSoil(broughtSoil)) return broughtSoil;
        if (!colony.AnyWoodOrStone || used == null) return place;
        if (SwapDaubColor(place, used) is { } recoloured) return recoloured;
        foreach (string v in SwappableVariants)
        {
            string? brought = VariantOf(used.Collectible, v);
            string? wanted = VariantOf(place, v);
            if (brought == null || wanted == null || brought == wanted) continue;
            if (sapi.World.GetBlock(place.CodeWithVariant(v, brought)) is { } swapped) return swapped;
        }
        return place;
    }

    /// <summary>A finished fire pit gets its firewood as fuel, as when a player builds one.</summary>
    public void FinishPlacement(Block placed, BlockPos pos)
    {
        if (placed is not BlockFirepit { Stage: FirepitReady } || IsStoneOvenBlock(placed)) return;
        if (sapi.World.BlockAccessor.GetBlockEntity(pos) is not BlockEntityFirepit be || !be.Inventory[0].Empty) return;
        var firewood = sapi.World.GetItem(new AssetLocation("game:firewood"));
        if (firewood == null) return;
        be.Inventory[0].Itemstack = new ItemStack(firewood, FirepitFirewood);
        be.Inventory[0].MarkDirty();
        be.MarkDirty(true);
    }

    MaterialCost? ItemCost(string code, int count)
    {
        if (!Config.BuildersNeedMaterials) return null;
        var item = sapi.World.GetItem(new AssetLocation(code));
        return item == null ? null : new MaterialCost(code, new ItemStack(item), count);
    }

    /// <summary>The block's own item, as a player would pick it, with its wood or stone left open when any will do.</summary>
    MaterialCost? PickCost(Colony colony, Block block, BlockPos pos)
    {
        if (!Config.BuildersNeedMaterials || block.Id == 0) return null;
        var key = (block.Id, colony.AnyWoodOrStone);
        if (pickCostCache.TryGetValue(key, out var cached)) return cached;

        // Soil with grass on it cannot be picked up as such; any soil will do to fill a hole in the ground.
        if (IsSoil(block))
        {
            var bare = sapi.World.GetBlock(new AssetLocation("game:soil-medium-none")) ?? block;
            var soil = new MaterialCost("game:soil-*", new ItemStack(bare), 1);
            pickCostCache[key] = soil;
            return soil;
        }

        ItemStack? stack = OvenItemStack(block);
        try
        {
            stack ??= block.OnPickBlock(sapi.World, pos);
        }
        catch (Exception)
        {
            stack = null;
        }
        stack ??= new ItemStack(block);
        stack.StackSize = 1;
        // Planks laid sideways pick as themselves (planks-oak-ns), but players only carry upright ones: ask for what it drops.
        if (stack.Block is { } picked && DroppedBlock(picked) is { } carried) stack = new ItemStack(carried);

        string code = stack.Collectible.Code.ToString();
        if (colony.AnyWoodOrStone) code = OpenType(stack.Collectible);
        var cost = new MaterialCost(code, stack, 1);
        pickCostCache[key] = cost;
        return cost;
    }
}
