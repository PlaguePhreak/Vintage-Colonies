using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Util;
using ItemMatch = System.Func<Vintagestory.API.Common.ItemStack, bool>;

namespace CreateAColony.Colonies;

/// <summary>
/// The Crafting recipes tab, on every hut with a worker: the player sets how many of a thing to keep, such as
/// firewood or sticks at a Forester's Hut, and the hut's worker crafts it at the hut block between its other
/// work, as a player would at the crafting grid. What a hut can make is set by config HutRecipeOutputs and
/// HutRecipeTools and found in the game's grid recipes, plus the recipes players taught it
/// (ColonyManager.CustomRecipes.cs). The crafting itself is in AiTaskSettlerWorker (PlanRecipe).
/// </summary>
public partial class ColonyManager
{
    readonly Dictionary<string, List<SawKind>> hutRecipeKinds = new();
    readonly Dictionary<string, string> partNames = new();

    /// <summary>
    /// True for a hut with a Crafting recipes tab: every hut whose worker works at the hut (AiTaskSettlerWorker).
    /// Builders, guards and priests have their own work.
    /// </summary>
    public bool HasHutRecipes(string type) => JobOf(type) != null
        && type is not (BuildingTypes.Builder or BuildingTypes.GuardTower or BuildingTypes.Barracks or BuildingTypes.Church or BuildingTypes.Archive);

    /// <summary>
    /// Everything a hut of this type can craft from the game's grid recipes (and other mods' with config
    /// HutRecipeDomains), without the recipes players taught it; HutRecipeKinds(colony, type) has those too.
    /// </summary>
    public List<SawKind> HutRecipeKinds(string type)
    {
        if (hutRecipeKinds.TryGetValue(type, out var known)) return known;
        var kinds = new List<SawKind>();
        if (HasHutRecipes(type) && Config.HutRecipeOutputs.TryGetValue(type, out var outputs))
        {
            var domains = Config.HutRecipeDomains.GetValueOrDefault(type) ?? new[] { "game" };
            var traits = Config.HutRecipeTraits.GetValueOrDefault(type);
            var tools = ToolSamples(Config.HutRecipeTools.GetValueOrDefault(type) ?? System.Array.Empty<string>());
            kinds = ScanGridRecipes(_ => true,
                tool => tools.Any(s => tool.SatisfiesAsIngredient(s, false)),
                output => domains.Contains(output.Collectible.Code.Domain) && outputs.Any(o => OutputMatches(o, output.Collectible.Code)) && JewelleryFits(type, output),
                type == BuildingTypes.Armourer ? System.Math.Max(Config.HutRecipeMaxInputs, Config.ArmourerMaxInputs) : Config.HutRecipeMaxInputs,
                traits, type is BuildingTypes.Tailor or BuildingTypes.Jeweller ? 2 : 1);
        }
        return hutRecipeKinds[type] = kinds;
    }

    /// <summary>
    /// An entry of config HutRecipeOutputs: the first part of the code (firewood), or with a *, a pattern for the
    /// whole path (armor-*-linen).
    /// </summary>
    static bool OutputMatches(string entry, AssetLocation code) =>
        entry.Contains('*') ? WildcardUtil.Match(entry, code.Path) : FirstPart(code) == entry;

    /// <summary>The Jeweller makes only jewellery, and the Tailor every other piece of clothing; the Armourer only metal armor.</summary>
    bool JewelleryFits(string type, ItemStack output) => type switch
    {
        BuildingTypes.Jeweller => IsJewellery(output),
        BuildingTypes.Tailor => !IsJewellery(output),
        BuildingTypes.Armourer => IsArmor(output) && !IsClothArmor(output),
        _ => true
    };

    /// <summary>One of each item whose code starts with one of these (knife in knife-flint), to test recipe tools against.</summary>
    List<ItemStack> ToolSamples(string[] firstParts)
    {
        var samples = new List<ItemStack>();
        if (firstParts.Length == 0) return samples;
        foreach (var item in sapi.World.Items)
        {
            if (item?.Code == null || !firstParts.Contains(FirstPart(item.Code))) continue;
            samples.Add(new ItemStack(item));
        }
        return samples;
    }

    public int RecipeKeep(Building hut, string key) => hut.RecipeKeep.GetValueOrDefault(key);

    public void SetRecipeKeep(Colony colony, Building hut, string key, int keep)
    {
        if (keep > 0) hut.RecipeKeep[key] = keep;
        else hut.RecipeKeep.Remove(key);
        MarkDirty(colony);
    }

    /// <summary>How many of a thing the colony has: in the warehouse, at the hut and with the worker.</summary>
    public int RecipeStock(Colony colony, Building hut, SawKind kind, CitizenRecord? worker)
    {
        ItemMatch mine = kind.IsMine;
        int count = CountItems(WarehouseContainers(colony), mine) + CountItems(HutStorage(colony, hut), mine);
        if (worker != null) count += PackCount(worker, mine);
        return count;
    }

    /// <summary>The things a hut has too few of, most wanted first.</summary>
    public List<SawKind> WantedRecipes(Colony colony, Building hut, CitizenRecord? worker)
    {
        if (hut.RecipeKeep.Count == 0 && !HasOrders(colony, hut)) return new List<SawKind>();
        return ByPriority(hut, HutRecipeKinds(colony, hut.Type)
            .Select(k => (Kind: k, Want: RecipeWant(colony, hut, k.Key)))
            .Where(k => k.Want > 0)
            .Select(k => (k.Kind, k.Kind.Key, Short: k.Want - RecipeStock(colony, hut, k.Kind, worker)))
            .Where(k => k.Short > 0))
            .ToList();
    }

    /// <summary>True when the worker carries everything the recipe takes, and its tool if it needs one.</summary>
    public bool HasRecipeInputs(CitizenRecord worker, SawRecipe recipe, ItemStack? inHand) =>
        HasSawInputs(worker, recipe) && CarriesTool(worker, recipe.Tool, inHand) && CarriesTool(worker, recipe.Tool2, inHand);

    /// <summary>True when the recipe takes no such tool, or the worker holds or carries one.</summary>
    public bool CarriesTool(CitizenRecord worker, CraftingRecipeIngredient? tool, ItemStack? inHand) =>
        tool == null || tool.SatisfiesAsIngredient(inHand, false) || PackCount(worker, s => tool.SatisfiesAsIngredient(s, false)) > 0;

    /// <summary>Crafts the recipe once from the worker's inventory; what it made goes to its inventory or the hut storage.</summary>
    public bool CraftHutRecipe(Colony colony, Building hut, CitizenRecord worker, SawRecipe recipe)
    {
        if (!SawCraftInputs(colony, worker, recipe)) return false;
        var made = recipe.Output.Clone();
        int count = made.StackSize;
        // New clothes are as good as new, as when a player makes them.
        if (made.Collectible.GetCollectibleInterface<IWearable>() != null) made.Attributes.SetFloat("condition", 1);
        StoreHarvest(colony, hut, worker, new[] { made }, hut.HutPos);
        AddStat(hut, "crafted", count);
        return true;
    }

    /// <summary>The things a hut keeps for recipes it is short of, so couriers leave them at the hut.</summary>
    ItemMatch HutRecipeInputs(Colony colony, Building hut)
    {
        if (hut.RecipeKeep.Count == 0 && !HasOrders(colony, hut)) return _ => false;
        var recipes = WantedRecipes(colony, hut, WorkerAt(colony, hut, JobOf(hut.Type) ?? "")).SelectMany(k => k.Recipes).ToList();
        // The tools of taught recipes stay too, since the worker fetches those from the hut storage.
        var parts = recipes.SelectMany(r => r.Inputs).Select(i => i.Part)
            .Concat(recipes.Where(r => r.CustomId != 0).SelectMany(r => new[] { r.Tool, r.Tool2 }).OfType<CraftingRecipeIngredient>()).ToList();
        return s => parts.Any(p => p.SatisfiesAsIngredient(s, false));
    }

    /// <summary>What a recipe is made from, for its row: "1 Log, 2 Sticks".</summary>
    public string RecipeInputsText(SawRecipe recipe) => recipe.Text ??
        string.Join(", ", recipe.Inputs.Select(i => i.Quantity + " " + PartName(i.Part)))
        + (recipe.Tool != null ? " + " + PartName(recipe.Tool) : "")
        + (recipe.Tool2 != null ? " + " + PartName(recipe.Tool2) : "");

    /// <summary>The name of a recipe ingredient; for one like log-*, the name of the first thing that fits.</summary>
    string PartName(CraftingRecipeIngredient part)
    {
        if (part.ResolvedItemStack != null && !part.IsWildCard) return part.ResolvedItemStack.GetName();
        // Parts matched by tag (any knife) share a code, so each is its own entry.
        string key = part.Type + ":" + part.Code + (part.Tags.IsEmpty ? "" : ":" + part.Tags.GetHashCode());
        if (partNames.TryGetValue(key, out var name)) return name;
        name = FirstPart(part.Code);
        IEnumerable<CollectibleObject> all = part.Type == EnumItemClass.Block ? sapi.World.Blocks : sapi.World.Items;
        foreach (var c in all)
        {
            if (c?.Code == null || !WildcardUtil.Match(part.Code, c.Code)) continue;
            var stack = new ItemStack(c);
            if (!part.SatisfiesAsIngredient(stack, false)) continue;
            name = stack.GetName();
            break;
        }
        return partNames[key] = name;
    }

    /// <summary>A line about what the hut crafts, for the hut window.</summary>
    public string HutRecipesInfo(Colony colony, Building hut) =>
        Lang.Get("createacolony:recipes-keeping", HutRecipeKinds(colony, hut.Type).Count(k => RecipeKeep(hut, k.Key) > 0));
}
