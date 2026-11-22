using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.GameContent;
using ItemMatch = System.Func<Vintagestory.API.Common.ItemStack, bool>;

namespace CreateAColony.Colonies;

/// <summary>
/// The cook of a Cook's Hut also cooks pot meals the way a player does: it puts an empty cooking pot in
/// the fire pit, fills the pot's slots with ingredients that make one of the game's meal recipes (stew,
/// porridge and so on, and the recipes food mods add), and lights the fire. The cooked pot goes into
/// the hut's storage, where settlers eat from it a serving at a time. The Kitchen tab says how many
/// servings of each meal to keep. The cook leaves recipes that need water or another liquid to players.
/// </summary>
public partial class ColonyManager
{
    /// <summary>The ingredients for one pot: each slot's item and how many of it, and the servings it makes.</summary>
    public sealed class MealPlan
    {
        public required CookingRecipe Recipe;
        public required List<(ItemStack Sample, int Count)> Slots;
        public int Servings;

        public string Key => MealKey(Recipe);
        /// <summary>Water portions the pot takes (0 for none).</summary>
        public int Water => Slots.Where(x => IsWater(x.Sample)).Sum(x => x.Count);
        public bool Uses(ItemStack s) => Slots.Any(x => x.Sample.Collectible.Code.Equals(s.Collectible.Code));
    }

    public static string MealKey(CookingRecipe recipe) => "meal:" + recipe.Code;

    List<CookingRecipe>? mealRecipes;

    /// <summary>The game's recipes for food cooked in a pot (not dyes, glue and the like).</summary>
    public List<CookingRecipe> MealRecipes()
    {
        if (mealRecipes != null) return mealRecipes;
        var all = sapi.GetCookingRecipes();
        if (all == null || all.Count == 0) return new List<CookingRecipe>();
        return mealRecipes = all
            .Where(r => r.Enabled && r.IsFood && r.CooksInto == null && r.Code != null && r.Ingredients?.Length > 0)
            .GroupBy(r => r.Code)
            .Select(g => g.First())
            .OrderBy(r => r.Code)
            .ToList();
    }

    public CookingRecipe? MealRecipe(string key) => MealRecipes().FirstOrDefault(r => MealKey(r) == key);

    /// <summary>An empty, clean cooking pot.</summary>
    public static bool IsCleanPot(ItemStack s) =>
        s.Block is BlockCookingContainer pot && !(pot.Attributes?["isDirtyPot"].AsBool(false) ?? false);

    static bool IsLiquid(ItemStack s) => BlockLiquidContainerBase.GetContainableProps(s) != null;

    /// <summary>How many servings of a meal to keep in stock.</summary>
    public int MealKeep(Building hut, CookingRecipe recipe) =>
        hut.KitchenChosen ? hut.KitchenKeep.GetValueOrDefault(MealKey(recipe)) : Config.KitchenAutoMealKeep;

    /// <summary>How many servings of a meal are ready: at the hut, in the warehouse, with the cook and in the fire pit.</summary>
    public int MealStock(Colony colony, Building hut, CookingRecipe recipe, CitizenRecord? cook) =>
        MealStock(colony, hut, recipe, cook, CookFirepit(colony, hut));

    int MealStock(Colony colony, Building hut, CookingRecipe recipe, CitizenRecord? cook, BlockEntityFirepit? pit)
    {
        float servings = 0;
        var slots = WarehouseContainers(colony).Cast<BlockEntityContainer>().Concat(KitchenStores(colony, hut)).SelectMany(c => c.Inventory).Where(s => !s.Empty).Select(s => s.Itemstack!);
        if (cook != null) slots = slots.Concat(PackItems(cook));
        if (pit != null && !pit.outputSlot.Empty) slots = slots.Append(pit.outputSlot.Itemstack!);
        foreach (var stack in slots) servings += ServingsOf(stack, recipe);
        // A pot on the fire counts too, so the cook does not start a second one of the same meal.
        if (pit?.inputSlot.Itemstack?.Block is BlockCookingContainer pot)
        {
            var cooking = pot.GetMatchingCookingRecipe(sapi.World, pot.GetCookingStacks(pit.Inventory as ISlotProvider ?? EmptySlots), out int q);
            if (cooking?.Code == recipe.Code && q > 0) servings += q;
        }
        return (int)servings;
    }

    static readonly ISlotProvider EmptySlots = new NoSlots();

    sealed class NoSlots : ISlotProvider
    {
        public ItemSlot[] Slots => Array.Empty<ItemSlot>();
    }

    float ServingsOf(ItemStack stack, CookingRecipe recipe)
    {
        if (stack.Block is not IBlockMealContainer meal || meal.GetRecipeCode(sapi.World, stack) != recipe.Code) return 0;
        return meal.GetQuantityServings(sapi.World, stack) * stack.StackSize;
    }

    /// <summary>
    /// The meal the cook should make next: the most wanted meal there are ingredients for, counting the
    /// cook's inventory, the hut storage and the warehouse. Null if no meal is wanted or none can be made.
    /// </summary>
    public MealPlan? NextMeal(Colony colony, Building hut, CitizenRecord cook)
    {
        var pit = CookFirepit(colony, hut);
        // Only the meals this cook knows at their level; see ColonyManager.Cooking.cs.
        var wanted = MealRecipes()
            .Where(r => KnowsMeal(colony, cook, r))
            .Select(r => (Recipe: r, Short: MealKeep(hut, r) - MealStock(colony, hut, r, cook, pit)))
            .Where(w => w.Short > 0)
            .OrderBy(w => hut.Priorities.Count == 0 ? 0 : PriorityIndex(hut.Priorities, MealKey(w.Recipe)))
            .ThenByDescending(w => w.Short)
            .ToList();
        if (wanted.Count == 0) return null;

        var pot = FindCleanPot(colony, hut, cook) ?? DefaultPot();
        if (pot?.Block is not BlockCookingContainer potBlock) return null;
        int slots = pot.ItemAttributes?["cookingContainerSlots"].AsInt(4) ?? 4;
        int maxSlotStack = pot.ItemAttributes?["maxContainerSlotStackSize"].AsInt(999) ?? 999;
        float slotLitres = pot.ItemAttributes?["cookingSlotCapacityLitres"].AsFloat(6f) ?? 6f;

        var larder = Larder(colony, hut, cook);
        // Water for soups and the like: the cook fetches it in a bucket (ColonyManager.Serving.cs).
        if (WaterAvailable(colony, hut, cook) && WaterSample() is { } water) larder[water.Collectible.Code] = (water, 1_000_000);
        // Getting ready for winter: food that keeps is left for the winter while there is enough to eat now.
        if (PrepsForWinter(hut) && sapi.World.GetEntityById(cook.EntityId) is EntityAgent cookEntity && !FoodLow(colony, cookEntity))
        {
            foreach (var code in larder.Where(a => SavedForWinter(a.Value.Sample)).Select(a => a.Key).ToList()) larder.Remove(code);
        }
        foreach (var (recipe, shortBy) in wanted)
        {
            int servings = Math.Clamp(shortBy, 1, potBlock.MaxServingSize);
            if (PlanMeal(recipe, larder, slots, servings, maxSlotStack, slotLitres) is { } plan) return plan;
        }
        return null;
    }

    /// <summary>Everything the cook could cook with, by item code: in its inventory, the hut storage and the warehouse.</summary>
    Dictionary<AssetLocation, (ItemStack Sample, int Count)> Larder(Colony colony, Building hut, CitizenRecord? cook)
    {
        var larder = new Dictionary<AssetLocation, (ItemStack Sample, int Count)>();
        var stacks = (cook == null ? new List<ItemStack>() : PackItems(cook))
            .Concat(KitchenStores(colony, hut).Concat(WarehouseContainers(colony)).SelectMany(c => c.Inventory).Where(s => !s.Empty).Select(s => s.Itemstack!));
        foreach (var stack in stacks)
        {
            if (stack.Collectible.Code.Path == "rot" || IsLiquid(stack) || stack.Block is IBlockMealContainer or BlockCookingContainer) continue;
            var code = stack.Collectible.Code;
            larder[code] = larder.TryGetValue(code, out var have) ? (have.Sample, have.Count + stack.StackSize) : (stack, stack.StackSize);
        }
        return larder;
    }

    /// <summary>
    /// Picks ingredients for a recipe: the most servings (up to maxServings) the larder has enough for.
    /// Each slot of the pot holds one ingredient, as many of it as the servings need. Different items are
    /// picked where there is a choice, and spare slots get extra ingredients the recipe allows, as a player
    /// would for a richer meal. The game's own recipe check has the last word.
    /// </summary>
    MealPlan? PlanMeal(CookingRecipe recipe, Dictionary<AssetLocation, (ItemStack Sample, int Count)> larder, int slots, int maxServings, int maxSlotStack, float slotLitres = 6f)
    {
        // A liquid's portion is in litres (as the game counts it in CookingRecipe.Matches); its slot holds slotLitres.
        int PortionOf(CookingRecipeIngredient i, ItemStack sample)
        {
            int size = i.GetMatchingStack(sample)?.StackSize ?? 0;
            if (size <= 0 || BlockLiquidContainerBase.GetContainableProps(sample) is not { } liquid) return size;
            return (int)(size * liquid.ItemsPerLitre * i.PortionSizeLitres);
        }
        int SlotMax(ItemStack sample) => BlockLiquidContainerBase.GetContainableProps(sample) is { } liquid ? (int)(slotLitres * liquid.ItemsPerLitre) : maxSlotStack;
        var options = recipe.Ingredients!.ToDictionary(i => i, i => larder
            .Select(a => (Code: a.Key, a.Value.Sample, Portion: PortionOf(i, a.Value.Sample)))
            .Where(c => c.Portion > 0)
            .ToList());
        if (recipe.Ingredients!.Sum(i => i.MinQuantity) > slots) return null;

        for (int servings = maxServings; servings >= 1; servings--)
        {
            var left = larder.ToDictionary(a => a.Key, a => a.Value.Count);
            var chosen = new List<(AssetLocation Code, ItemStack Sample, int Count)>();
            bool ok = true;
            foreach (var ingredient in recipe.Ingredients!)
            {
                for (int k = 0; k < ingredient.MinQuantity && ok; k++) ok = Pick(ingredient, servings, distinct: false);
                if (!ok) break;
            }
            if (!ok) continue;

            var required = chosen.ToList();
            foreach (var ingredient in recipe.Ingredients!)
            {
                int used = chosen.Count(c => ingredient.GetMatchingStack(c.Sample) != null);
                while (chosen.Count < slots && used < ingredient.MaxQuantity && Pick(ingredient, servings, distinct: true)) used++;
            }
            if (Fits(chosen, servings) is { } rich) return rich;
            if (chosen.Count > required.Count && Fits(required, servings) is { } plain) return plain;

            bool Pick(CookingRecipeIngredient ingredient, int n, bool distinct)
            {
                var best = options[ingredient]
                    .Where(c => c.Portion * n <= SlotMax(c.Sample) && left[c.Code] >= c.Portion * n)
                    .Where(c => !distinct || !chosen.Any(x => x.Code.Equals(c.Code)))
                    .OrderBy(c => chosen.Count(x => x.Code.Equals(c.Code)))
                    .ThenByDescending(c => left[c.Code] / c.Portion)
                    .Select(c => ((AssetLocation, ItemStack, int)?)(c.Code, c.Sample, c.Portion * n))
                    .FirstOrDefault();
                if (best is not { } pick) return false;
                left[pick.Item1] -= pick.Item3;
                chosen.Add(pick);
                return true;
            }
        }
        return null;

        MealPlan? Fits(List<(AssetLocation Code, ItemStack Sample, int Count)> pick, int servings)
        {
            if (pick.Count == 0) return null;
            var stacks = pick.Select(p =>
            {
                var s = p.Sample.Clone();
                s.StackSize = p.Count;
                return s;
            }).ToArray();
            int q = 0;
            if (!recipe.Matches(stacks, ref q) || q != servings) return null;
            return new MealPlan { Recipe = recipe, Slots = pick.Select(p => (p.Sample, p.Count)).ToList(), Servings = servings };
        }
    }

    /// <summary>A clean cooking pot the cook can use: one it carries, one in the hut storage or one in the warehouse.</summary>
    ItemStack? FindCleanPot(Colony colony, Building hut, CitizenRecord cook) =>
        PackItems(cook).FirstOrDefault(IsCleanPot)
        ?? KitchenStores(colony, hut).Concat(WarehouseContainers(colony)).SelectMany(c => c.Inventory)
            .FirstOrDefault(s => !s.Empty && IsCleanPot(s.Itemstack))?.Itemstack;

    ItemStack? defaultPot;

    /// <summary>Any cooking pot the game has, to know how many slots and servings a pot takes before the cook has one.</summary>
    ItemStack? DefaultPot()
    {
        if (defaultPot != null) return defaultPot;
        var block = sapi.World.Blocks.FirstOrDefault(b => b?.Code != null && IsCleanPot(new ItemStack(b)));
        return defaultPot = block == null ? null : new ItemStack(block);
    }

    /// <summary>
    /// Puts the cooking pot in the fire pit, fills it with the meal's ingredients and lights the fire.
    /// If the game does not take it as a meal after all, everything goes back into the cook's inventory.
    /// </summary>
    public void LoadMeal(Colony colony, CitizenRecord cook, BlockEntityFirepit pit, MealPlan plan)
    {
        if (!pit.inputSlot.Empty || pit.Inventory is not ISlotProvider cookingSlots) return;
        var pot = TakeFromPack(colony, cook, IsCleanPot, 1);
        if (pot?.Block is not BlockCookingContainer potBlock) return;
        pit.inputSlot.Itemstack = pot;
        pit.inputSlot.MarkDirty(); // the fire pit opens the pot's slots

        var slots = pit.otherCookingSlots;
        for (int i = 0; i < plan.Slots.Count && i < slots.Length; i++)
        {
            var code = plan.Slots[i].Sample.Collectible.Code;
            int need = plan.Slots[i].Count;
            if (IsWater(plan.Slots[i].Sample))
            {
                PourWater(colony, cook, slots[i], need);
                continue;
            }
            while (need > 0 && TakeFromPack(colony, cook, s => s.Collectible.Code.Equals(code), need) is { } part)
            {
                var source = new DummySlot(part);
                need -= source.TryPutInto(sapi.World, slots[i], part.StackSize);
                if (!source.Empty)
                {
                    AddToPack(colony, cook, source.Itemstack);
                    break;
                }
            }
            slots[i].MarkDirty();
        }

        if (!potBlock.CanSmelt(sapi.World, cookingSlots, pot, null))
        {
            // Take the ingredients out before the pot: a fire pit drops whatever is in a pot taken off it.
            foreach (var slot in slots)
            {
                if (slot.Empty) continue;
                AddToPack(colony, cook, slot.Itemstack);
                slot.Itemstack = null;
                slot.MarkDirty();
            }
            AddToPack(colony, cook, pot);
            pit.inputSlot.Itemstack = null;
            pit.inputSlot.MarkDirty();
            pit.MarkDirty(true);
            return;
        }
        FuelFirepit(colony, cook, pit);
    }

    /// <summary>True when the pot in the fire pit holds something the game will cook, so the cook may light the fire under it.</summary>
    public bool PotWillCook(BlockEntityFirepit pit) =>
        pit.inputSlot.Itemstack?.Block is BlockCookingContainer pot && pit.Inventory is ISlotProvider slots
        && pot.CanSmelt(sapi.World, slots, pit.inputSlot.Itemstack, pit.outputSlot.Itemstack);

    /// <summary>A meal's name for the Kitchen tab, from the recipe's first ingredients.</summary>
    public string MealName(CookingRecipe recipe)
    {
        string fallback = Lang.Get("createacolony:kitchen-meal", recipe.Code);
        try
        {
            var stacks = recipe.Ingredients!
                .Where(i => i.MinQuantity > 0 || recipe.Ingredients!.All(x => x.MinQuantity == 0))
                .SelectMany(i => Enumerable.Repeat(i.ValidStacks?.FirstOrDefault()?.ResolvedItemstack, Math.Max(1, i.MinQuantity)))
                .Where(s => s != null)
                .Select(s => s!)
                .ToArray();
            if (stacks.Length == 0) return fallback;
            string name = recipe.GetOutputName(sapi.World, stacks);
            return string.IsNullOrWhiteSpace(name) || name == Lang.Get("unknown") ? fallback : name;
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    /// <summary>A cooking pot (or failing that, an ingredient) to show as the meal's icon on the Kitchen tab.</summary>
    public ItemStack? MealIcon(CookingRecipe recipe)
    {
        if (DefaultPot() is { } pot) return pot;
        return recipe.Ingredients?.SelectMany(i => i.ValidStacks ?? Array.Empty<CookingRecipeStack>()).FirstOrDefault()?.ResolvedItemstack;
    }
}
