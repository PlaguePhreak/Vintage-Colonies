using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.GameContent;
using ItemMatch = System.Func<Vintagestory.API.Common.ItemStack, bool>;

namespace CreateAColony.Colonies;

/// <summary>
/// The sawyer of a Sawmill saws logs into planks and makes other wooden things at the hut, with a saw. What
/// it can make comes from the game's crafting recipes: every recipe that takes only wood (logs, planks,
/// firewood, sticks, support beams and the like, config SawmillWoodParts) and, if it takes a tool, a saw.
/// The sawyer crafts at the hut block as a player would at the crafting grid, one recipe per stretch of
/// work. The Sawmill tab says how many of each thing to keep.
/// </summary>
public partial class ColonyManager
{
    /// <summary>One crafting recipe the sawyer can use: the wood it takes, how much of each, and what it makes.</summary>
    public sealed class SawRecipe
    {
        public required List<(CraftingRecipeIngredient Part, int Quantity)> Inputs;
        /// <summary>The saw the recipe wears down, or null for a recipe without a tool.</summary>
        public CraftingRecipeIngredient? Tool;
        /// <summary>A second tool some recipes take (a Tailor's knife and awl with Tailor's Delight), or null.</summary>
        public CraftingRecipeIngredient? Tool2;
        public required ItemStack Output;
        /// <summary>A recipe a player taught the hut (ColonyManager.CustomRecipes.cs): its CustomRecipe.Id; 0 for the game's recipes.</summary>
        public int CustomId;
        /// <summary>What it is made from, for its row, where the parts' own names would not say it; null for the usual text.</summary>
        public string? Text;
        /// <summary>A food mod station the recipe is made at (ColonyManager.FoodStations.cs), or null for the hut.</summary>
        public string? Station;
        /// <summary>Water portions a station recipe takes from the cook's bucket.</summary>
        public int Water;

        public int InputCount => Inputs.Sum(i => i.Quantity);
        public bool IsInput(ItemStack s) => Inputs.Any(i => i.Part.SatisfiesAsIngredient(s, false));
    }

    /// <summary>One thing the sawyer can make, with every recipe that makes it, fewest inputs first.</summary>
    public sealed class SawKind
    {
        public required string Key;
        public required ItemStack Sample;
        public required List<SawRecipe> Recipes;

        public bool IsMine(ItemStack s) => s.Collectible.Code.ToString() == Key;
        public bool IsInput(ItemStack s) => Recipes.Any(r => r.IsInput(s));
    }

    List<SawKind>? sawKinds;

    /// <summary>The first part of a code (plank in plank-oak), which says what kind of thing it is.</summary>
    internal static string FirstPart(AssetLocation? code)
    {
        if (code == null) return "";
        int dash = code.Path.IndexOf('-');
        return dash < 0 ? code.Path : code.Path[..dash];
    }

    bool IsWoodPart(CraftingRecipeIngredient part) => part.Code?.Domain == "game" && Config.SawmillWoodParts.Contains(FirstPart(part.Code));

    /// <summary>Everything the game's crafting recipes make from wood alone, with a saw or no tool.</summary>
    public List<SawKind> SawKinds() => sawKinds ??= ScanGridRecipes(IsWoodPart,
        tool => Config.SawmillToolParts.Contains(FirstPart(tool.Code)), _ => true, Config.SawmillMaxInputs);

    /// <summary>
    /// The game's crafting recipes whose inputs and output pass the tests, grouped by what they make, with
    /// at most one tool. Used by the Sawmill and the huts' Crafting recipes tab.
    /// </summary>
    List<SawKind> ScanGridRecipes(System.Func<CraftingRecipeIngredient, bool> inputOk, System.Func<CraftingRecipeIngredient, bool> toolOk,
        System.Func<ItemStack, bool> outputOk, int maxInputs, string[]? traits = null, int maxTools = 1)
    {
        var kinds = new Dictionary<string, SawKind>();
        foreach (var recipe in sapi.World.GridRecipes)
        {
            var output = recipe?.Output?.ResolvedItemStack;
            if (recipe == null || output == null || !recipe.Enabled || recipe.ResolvedIngredients == null) continue;
            // Recipes only some player classes can make are left out, unless this worker counts as that class.
            if (recipe.RequiresTrait != null && (traits == null || !(traits.Contains(recipe.RequiresTrait) || traits.Contains("*")))) continue;
            if (output.Collectible.Code.Domain == "createacolony" || !outputOk(output)) continue;
            var parts = recipe.ResolvedIngredients.Where(i => i != null).Select(i => i!).ToList();
            if (parts.Count == 0) continue;
            var tools = parts.Where(i => i.IsTool || !i.Consume).ToList();
            var inputs = parts.Where(i => !(i.IsTool || !i.Consume)).ToList();
            if (inputs.Count == 0 || tools.Count > maxTools) continue;
            if (!tools.All(toolOk)) continue;
            if (!inputs.All(inputOk)) continue;
            // Repairs and the like give back what they take.
            if (inputs.Any(i => i.SatisfiesAsIngredient(output, false))) continue;

            var grouped = inputs.GroupBy(i => i.Type + ":" + i.Code)
                .Select(g => (Part: g.First(), Quantity: g.Sum(i => Math.Max(1, i.Quantity))))
                .ToList();
            var saw = new SawRecipe { Inputs = grouped, Tool = tools.FirstOrDefault(), Tool2 = tools.ElementAtOrDefault(1), Output = output.Clone() };
            if (saw.InputCount > maxInputs) continue;
            string key = output.Collectible.Code.ToString();
            if (!kinds.TryGetValue(key, out var kind))
            {
                var sample = output.Clone();
                sample.StackSize = Math.Max(1, output.StackSize);
                kinds[key] = kind = new SawKind { Key = key, Sample = sample, Recipes = new() };
            }
            kind.Recipes.Add(saw);
        }
        foreach (var kind in kinds.Values) kind.Recipes.Sort((a, b) => a.InputCount.CompareTo(b.InputCount));
        return kinds.Values.OrderBy(k => FirstPart(k.Sample.Collectible.Code)).ThenBy(k => k.Key).ToList();
    }

    /// <summary>How many of a thing are ready: at the hut, in the warehouse and with the sawyer.</summary>
    public int SawStock(Colony colony, Building hut, SawKind kind, CitizenRecord? sawyer)
    {
        ItemMatch mine = kind.IsMine;
        int count = CountItems(WarehouseContainers(colony), mine) + CountItems(HutStorage(colony, hut), mine);
        if (sawyer != null) count += PackCount(sawyer, mine);
        return count;
    }

    /// <summary>The things there are too few of, most wanted first.</summary>
    public List<SawKind> WantedSawing(Colony colony, Building hut, CitizenRecord? sawyer) => ByPriority(hut, SawKinds()
        .Select(k => (Kind: k, Want: CraftWant(colony, hut, k.Key)))
        .Where(k => k.Want > 0)
        .Select(k => (k.Kind, k.Kind.Key, Short: k.Want - SawStock(colony, hut, k.Kind, sawyer)))
        .Where(k => k.Short > 0))
        .ToList();

    /// <summary>
    /// The rows of the Sawmill tab: everything kept in stock, and everything that can be made from the wood
    /// the colony has (in the warehouse, the hut storage or with the sawyer).
    /// </summary>
    public List<SawKind> SawRows(Colony colony, Building hut, CitizenRecord? sawyer)
    {
        var stacks = WarehouseContainers(colony).Cast<BlockEntityContainer>().Concat(HutStorage(colony, hut))
            .SelectMany(c => c.Inventory).Where(s => !s.Empty).Select(s => s.Itemstack!).ToList();
        if (sawyer != null) stacks.AddRange(PackItems(sawyer));
        // One stack of each kind is enough to know what the colony has.
        stacks = stacks.GroupBy(s => s.Collectible.Code).Select(g => g.First()).ToList();
        return SawKinds().Where(k => CraftKeep(hut, k.Key) > 0
            || k.Recipes.Any(r => r.Inputs.All(i => stacks.Any(s => i.Part.SatisfiesAsIngredient(s, false))))).ToList();
    }

    /// <summary>True when the sawyer carries all the wood the recipe takes.</summary>
    public bool HasSawInputs(CitizenRecord sawyer, SawRecipe recipe) =>
        recipe.Inputs.All(i => PackCount(sawyer, s => i.Part.SatisfiesAsIngredient(s, false)) >= i.Quantity);

    /// <summary>
    /// Makes the thing once: takes the wood from the sawyer's inventory and puts what it made there (or in
    /// the hut storage). False if some wood is missing.
    /// </summary>
    public bool SawCraft(Colony colony, Building hut, CitizenRecord sawyer, SawRecipe recipe)
    {
        if (!SawCraftInputs(colony, sawyer, recipe)) return false;
        var made = recipe.Output.Clone();
        int count = made.StackSize;
        StoreHarvest(colony, hut, sawyer, new[] { made }, hut.HutPos);
        AddStat(hut, "sawn", count);
        return true;
    }

    /// <summary>Takes what the recipe uses from the worker's inventory. False, taking nothing, if some is missing.</summary>
    bool SawCraftInputs(Colony colony, CitizenRecord worker, SawRecipe recipe)
    {
        if (!HasSawInputs(worker, recipe)) return false;
        foreach (var (part, quantity) in recipe.Inputs)
        {
            int left = quantity;
            // A part can match several kinds (plank-*): take from each until there is enough.
            while (left > 0 && TakeFromPack(colony, worker, s => part.SatisfiesAsIngredient(s, false), left) is { } taken) left -= taken.StackSize;
        }
        return true;
    }

    /// <summary>
    /// At a Sawmill the wood for what it keeps in stock and its saws stay; what was made goes to the
    /// warehouse, unless it is wood for something else it keeps.
    /// </summary>
    ItemMatch SawmillHutJunk(Colony colony, Building hut)
    {
        var inputs = SawKinds().Where(k => CraftWant(colony, hut, k.Key) > 0).SelectMany(k => k.Recipes).SelectMany(r => r.Inputs).Select(i => i.Part).ToList();
        return s => s.Collectible.Tool != EnumTool.Saw && !inputs.Any(p => p.SatisfiesAsIngredient(s, false));
    }

    /// <summary>A line about what the Sawmill keeps, for the hut window.</summary>
    public string SawmillInfo(Building hut)
    {
        int kept = SawKinds().Count(k => CraftKeep(hut, k.Key) > 0);
        return Lang.Get("createacolony:sawmill-keeping", kept, SawKinds().Count);
    }
}
