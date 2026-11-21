using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.GameContent;
using ItemMatch = System.Func<Vintagestory.API.Common.ItemStack, bool>;

namespace CreateAColony.Colonies;

/// <summary>
/// Alloys (0.169.0): the smelter mixes nuggets of two or three metals in one crucible, as a player does, in the
/// shares the game's alloy recipes ask for (tin bronze is 88-92% copper and 8-12% tin). The game itself then
/// melts the mix into the alloy, and the smelter pours it into ingot molds like any other metal. Every alloy the
/// game (or another mod) knows shows on the Smelting tab, once the game has nuggets for each of its metals.
/// </summary>
public partial class ColonyManager
{
    /// <summary>One metal in an alloy: its ingot, its share of the alloy, and the ores that melt into it.</summary>
    public sealed class AlloyPart
    {
        public required AssetLocation Metal;
        public required float Min;
        public required float Max;
        public readonly Dictionary<AssetLocation, int> UnitsPer = new();

        public float Mid => (Min + Max) / 2f;
        public bool IsOre(ItemStack s) => UnitsPer.ContainsKey(s.Collectible.Code);
    }

    /// <summary>The game's alloys whose every metal has ores that melt into it in a crucible.</summary>
    List<SmeltKind> AlloyKinds(Dictionary<string, SmeltKind> metals)
    {
        var found = new List<SmeltKind>();
        List<AlloyRecipe>? alloys;
        try
        {
            alloys = sapi.GetMetalAlloys();
        }
        catch (Exception)
        {
            return found;
        }
        foreach (var alloy in alloys ?? new List<AlloyRecipe>())
        {
            var output = alloy?.Output?.ResolvedItemstack;
            if (alloy == null || !alloy.Enabled || output == null || alloy.Ingredients == null || alloy.Ingredients.Length < 2) continue;
            string key = output.Collectible.Code.ToString();
            if (metals.ContainsKey(key) || found.Any(k => k.Key == key)) continue;
            var sample = output.Clone();
            sample.StackSize = 1;
            var kind = new SmeltKind { Key = key, Sample = sample, Alloy = alloy };
            bool ok = true;
            foreach (var ingredient in alloy.Ingredients)
            {
                var ingot = ingredient?.ResolvedItemstack;
                if (ingot == null || !metals.TryGetValue(ingot.Collectible.Code.ToString(), out var metal))
                {
                    ok = false;
                    break;
                }
                var part = new AlloyPart { Metal = ingot.Collectible.Code, Min = ingredient!.MinRatio, Max = ingredient.MaxRatio };
                foreach (var (ore, units) in metal.UnitsPer)
                {
                    part.UnitsPer[ore] = units;
                    kind.UnitsPer[ore] = units;
                    kind.MeltingPoint[ore] = metal.MeltingPoint[ore];
                }
                kind.Parts.Add(part);
            }
            if (ok && kind.Parts.Count >= 2) found.Add(kind);
        }
        return found;
    }

    /// <summary>
    /// What to put in the crucible for an alloy: how many of each ore, for as many whole ingots as the ore on hand,
    /// the crucible's slots and the wanted amount allow, with each metal's share inside what the game asks for.
    /// Null when not even one ingot can be mixed.
    /// </summary>
    public List<(AssetLocation Code, int Pieces)>? AlloyPlan(SmeltKind kind, IEnumerable<ItemStack> have, int ingots, int slots, int slotSize)
    {
        if (kind.Alloy == null || kind.Parts.Count == 0 || ingots <= 0) return null;
        var stock = have.Where(kind.IsOre).GroupBy(s => s.Collectible.Code).ToDictionary(g => g.Key, g => g.Sum(s => s.StackSize));
        // The metal with the biggest share takes what is left, so the units add up to whole ingots.
        int main = kind.Parts.Select((p, i) => (p, i)).OrderByDescending(x => x.p.Mid).First().i;
        for (int n = ingots; n >= 1; n--)
        {
            int total = 100 * n;
            var plan = new List<(AssetLocation, int)>();
            int used = 0;
            bool ok = true;
            for (int i = 0; i < kind.Parts.Count && ok; i++)
            {
                if (i == main) continue;
                var part = kind.Parts[i];
                var fill = FillShare(part, stock, total, (int)Math.Ceiling(part.Min * total), (int)Math.Floor(part.Max * total), (int)Math.Round(part.Mid * total));
                if (fill == null)
                {
                    ok = false;
                    break;
                }
                plan.AddRange(fill.Value.Pieces);
                used += fill.Value.Units;
            }
            if (!ok) continue;
            var mainPart = kind.Parts[main];
            int rest = total - used;
            if (rest < mainPart.Min * total - 0.5f || rest > mainPart.Max * total + 0.5f) continue;
            var mainFill = FillShare(mainPart, stock, total, rest, rest, rest);
            if (mainFill == null) continue;
            plan.AddRange(mainFill.Value.Pieces);

            // Each kind of ore takes its own crucible slots.
            int need = plan.Sum(p => (int)Math.Ceiling(p.Item2 / (double)Math.Max(1, Math.Min(slotSize, MaxStack(p.Item1)))));
            if (need > slots) continue;
            var stacks = plan.Select(p => sapi.World.GetItem(p.Item1) is { } item ? new ItemStack(item, p.Item2)
                : sapi.World.GetBlock(p.Item1) is { } block ? new ItemStack(block, p.Item2) : null).ToArray();
            if (stacks.Any(s => s == null) || !kind.Alloy.Matches(stacks!)) continue;
            return plan;
        }
        return null;
    }

    /// <summary>
    /// Ore for one metal of an alloy, between the fewest and most units its share allows, as near the middle as the
    /// ore on hand can make it exactly. Null when the ore on hand cannot make any amount in that range.
    /// </summary>
    static (List<(AssetLocation, int)> Pieces, int Units)? FillShare(AlloyPart part, Dictionary<AssetLocation, int> stock, int total, int min, int max, int aim)
    {
        if (max < min) return null;
        for (int step = 0; step <= Math.Max(aim - min, max - aim); step++)
        {
            foreach (int units in step == 0 ? new[] { aim } : new[] { aim - step, aim + step })
            {
                if (units < min || units > max || units <= 0) continue;
                int left = units;
                var pieces = new List<(AssetLocation, int)>();
                foreach (var (code, count) in stock.Where(e => part.UnitsPer.ContainsKey(e.Key)).OrderByDescending(e => part.UnitsPer[e.Key]).ThenByDescending(e => e.Value))
                {
                    int each = part.UnitsPer[code];
                    int take = Math.Min(count, left / each);
                    if (take <= 0) continue;
                    pieces.Add((code, take));
                    left -= take * each;
                    if (left == 0) break;
                }
                if (left == 0) return (pieces, units);
            }
        }
        return null;
    }

    int MaxStack(AssetLocation code) => Math.Max(1, (sapi.World.GetItem(code) as CollectibleObject ?? sapi.World.GetBlock(code))?.MaxStackSize ?? 64);

    /// <summary>The crucible slots of a fire pit, and how many pieces each takes, for a crucible.</summary>
    public static (int Slots, int Size) CrucibleRoom(BlockEntityFirepit pit, ItemStack? crucible)
    {
        int size = crucible?.ItemAttributes?["maxContainerSlotStackSize"].AsInt(999) ?? 999;
        return (Math.Max(1, pit.otherCookingSlots?.Length ?? 4), Math.Max(1, size));
    }

    /// <summary>
    /// The ore for an alloy that the smelter should get next, for this many ingots: each metal it has too little of,
    /// with the ore it is short of. Used to fetch ore from the hut storage or ask couriers for it.
    /// </summary>
    public List<(AlloyPart Part, int Units)> AlloyShort(SmeltKind kind, IEnumerable<ItemStack> have, int ingots)
    {
        var list = new List<(AlloyPart, int)>();
        var stacks = have.ToList();
        foreach (var part in kind.Parts)
        {
            int want = (int)Math.Ceiling(part.Mid * 100 * ingots);
            int got = stacks.Where(part.IsOre).Sum(s => s.StackSize * part.UnitsPer[s.Collectible.Code]);
            if (got < want) list.Add((part, want - got));
        }
        return list;
    }

    /// <summary>
    /// Fills an empty crucible with the ores for an alloy and puts it on the fire pit with fuel, then lights it.
    /// False (and nothing used) when the ore on hand cannot be mixed into the alloy.
    /// </summary>
    public bool LoadAlloy(Colony colony, CitizenRecord smelter, BlockEntityFirepit pit, SmeltKind kind, int ingots)
    {
        if (!pit.inputSlot.Empty || kind.Alloy == null) return false;
        var crucibleSample = PackItems(smelter).FirstOrDefault(IsEmptyCrucible);
        if (crucibleSample == null) return false;
        var (slotCount, slotSize) = CrucibleRoom(pit, crucibleSample);
        var plan = AlloyPlan(kind, PackItems(smelter), ingots, slotCount, slotSize);
        if (plan == null) return false;

        var crucible = TakeFromPack(colony, smelter, IsEmptyCrucible, 1);
        if (crucible == null) return false;
        pit.inputSlot.Itemstack = crucible;
        pit.inputSlot.MarkDirty(); // the fire pit opens the crucible's slots
        var slots = pit.otherCookingSlots;
        foreach (var (code, pieces) in plan)
        {
            int left = pieces;
            foreach (var slot in slots)
            {
                if (left <= 0) break;
                if (!slot.Empty) continue;
                int room = Math.Min(left, Math.Max(1, Math.Min(slotSize, slot.MaxSlotStackSize)));
                if (TakeFromPack(colony, smelter, s => s.Collectible.Code.Equals(code), room) is not { } part) break;
                room = Math.Min(room, Math.Max(1, part.Collectible.MaxStackSize));
                if (part.StackSize > room)
                {
                    var extra = part.Clone();
                    extra.StackSize = part.StackSize - room;
                    part.StackSize = room;
                    AddToPack(colony, smelter, extra);
                }
                slot.Itemstack = part;
                slot.MarkDirty();
                left -= part.StackSize;
            }
        }

        var stacks = slots.Where(s => !s.Empty).Select(s => s.Itemstack!).ToArray();
        if (!CrucibleCanSmelt(pit) || !kind.Alloy.Matches(stacks))
        {
            UnloadCrucible(colony, smelter, pit);
            return false;
        }
        FuelSmelterFire(colony, smelter, pit, MeltTemperature(kind, stacks));
        return true;
    }

    /// <summary>What the crucible on the fire pit (still unmelted) will make, and how many units of it; null when nothing.</summary>
    public (string Code, int Units)? CrucibleMakes(BlockEntityFirepit pit)
    {
        if (pit.inputSlot.Itemstack?.Block is not BlockSmeltingContainer bsc) return null;
        var stacks = pit.otherCookingSlots.Where(s => !s.Empty).Select(s => s.Itemstack!).ToArray();
        if (stacks.Length == 0) return null;
        try
        {
            if (bsc.GetMatchingAlloy(sapi.World, stacks) is { } alloy && alloy.Output?.ResolvedItemstack is { } output)
            {
                return (output.Collectible.Code.ToString(), (int)Math.Round(alloy.GetTotalOutputQuantity(stacks) * 100));
            }
        }
        catch (Exception)
        {
            // A broken alloy recipe from another mod: count it as plain ore below.
        }
        string? code = null;
        int units = 0;
        foreach (var s in stacks)
        {
            var props = s.Collectible.CombustibleProps;
            var ingot = props?.SmeltedStack?.ResolvedItemstack;
            if (ingot == null) return null;
            string c = ingot.Collectible.Code.ToString();
            if (code != null && code != c) return null;
            code = c;
            units += (int)Math.Round(100.0 * props!.SmeltedStack.StackSize * s.StackSize / Math.Max(1, props.SmeltedRatio));
        }
        return code == null ? null : (code, units);
    }

    /// <summary>Ore for one metal of an alloy, as a match.</summary>
    public static ItemMatch AlloyOre(AlloyPart part) => part.IsOre;
}
