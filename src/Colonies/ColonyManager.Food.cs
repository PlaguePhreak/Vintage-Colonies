using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace CreateAColony.Colonies;

/// <summary>
/// What settlers eat. Besides plain food (bread, berries, cheese) they eat one serving of a meal from a
/// bowl, cooking pot or crock, and one slice of a pie, using the game's own meal nutrition. Food mods
/// such as Expanded Foods and A Culinary Artillery change that same nutrition code, so their foods and
/// meals count properly without this mod knowing about them. Settlers remember the food groups of their
/// last few meals and prefer food from groups they have had least of lately, and food that will spoil
/// soon over food that keeps (ColonyManager.Freshness.cs).
/// </summary>
public partial class ColonyManager
{
    /// <summary>How many food groups a settler remembers; each meal adds the groups it had.</summary>
    const int RecentFoodMemory = 8;
    /// <summary>A container this many blocks further away counts as half as good.</summary>
    const float FoodDistanceHalving = 24f;

    /// <summary>One thing a settler could eat right now, and how much it would give.</summary>
    public sealed class FoodChoice
    {
        public required BlockEntityContainer Container;
        public required ItemSlot Slot;
        /// <summary>Saturation and health per food group for the part the settler would eat.</summary>
        public required FoodNutritionProperties[] Props;
        public float Satiety => Props.Sum(p => p.Satiety);
        public float Health => Props.Sum(p => p.Health);
        public float Score;
        /// <summary>Set when the food is a meal placed in the world rather than an item in a slot.</summary>
        public IBlockEntityMealContainer? Placed;
    }

    /// <summary>Raw ingredients count for this much when a very hungry settler weighs them against ready food.</summary>
    const float RawIngredientScoreMul = 0.25f;
    /// <summary>Per item code: whether it is a raw ingredient (IsRawIngredient), as looking through the meal recipes is slow.</summary>
    readonly Dictionary<string, bool> rawIngredients = new();

    /// <summary>
    /// Raw food that goes further cooked: raw meat and fish (for the fire pit), and grain, vegetables and eggs a
    /// pot meal can use. Settlers leave these for the cooks unless they are very hungry (EatRawBelowFraction).
    /// Fruit, cheese and anything cooked are fine to eat as they are.
    /// </summary>
    public bool IsRawIngredient(ItemStack stack)
    {
        if (stack.Block is IBlockMealContainer or BlockPie || IsCookedFood(stack)) return false;
        string code = stack.Collectible.Code.ToString();
        if (rawIngredients.TryGetValue(code, out bool known)) return known;
        var group = stack.Collectible.NutritionProps?.FoodCategory;
        bool raw = IsGrillable(stack)
            || (group is EnumFoodCategory.Grain or EnumFoodCategory.Vegetable or EnumFoodCategory.Protein && IsMealIngredient(stack));
        rawIngredients[code] = raw;
        return raw;
    }

    /// <summary>True when the settler is hungry enough to eat raw ingredients.</summary>
    public bool EatsRaw(CitizenRecord citizen) => citizen.Saturation < Config.SaturationMax * Config.EatRawBelowFraction;

    /// <summary>
    /// The best food in the colony for this settler, weighing variety against distance. Containers skip says yes to are left out.
    /// Raw ingredients only when the settler is very hungry, and then ready food still comes first.
    /// </summary>
    public FoodChoice? FindFood(Colony colony, CitizenRecord citizen, EntityAgent eater, System.Func<BlockPos, bool>? skip = null)
    {
        FoodChoice? best = null;
        float bestValue = 0;
        bool allowRaw = EatsRaw(citizen);
        // Meals at the Market cost gears (ColonyManager.Market.cs): only for settlers who can pay, who like them better.
        var market = colony.Buildings.Any(b => b.Type == BuildingTypes.Market) ? MarketChests(colony) : null;
        bool canBuy = market != null && CanBuy(colony, citizen, "meal");
        bool wantedMeal = false;
        foreach (var container in FindBlockEntities<BlockEntityContainer>(colony))
        {
            if (skip != null && skip(container.Pos)) continue;
            bool atMarket = market != null && market.Contains(container.Pos);
            var choice = atMarket ? MarketMealIn(container, citizen, eater) : FindFoodIn(container, citizen, eater, allowRaw);
            if (choice == null) continue;
            if (atMarket && !canBuy)
            {
                wantedMeal = true;
                continue;
            }
            float dist = (float)Math.Sqrt(container.Pos.DistanceSqTo(eater.Pos.X, eater.Pos.Y, eater.Pos.Z));
            float value = choice.Score * (atMarket ? MarketMealPull : 1) / (1 + dist / FoodDistanceHalving);
            if (value <= bestValue) continue;
            best = choice;
            bestValue = value;
        }
        if (wantedMeal && MarketOpen(colony) && MarketSells(colony, "meal") && HasMood(citizen) && Earners(colony).Contains(citizen)) CantAfford(citizen);
        return best;
    }

    /// <summary>A settler who can pay weighs a Market meal this much better than free food the same distance away.</summary>
    const float MarketMealPull = 2f;

    /// <summary>The best meal for sale in a Market chest, or null; only what the Market sells as a meal.</summary>
    FoodChoice? MarketMealIn(BlockEntityContainer container, CitizenRecord citizen, EntityAgent eater)
    {
        FoodChoice? best = null;
        foreach (var slot in container.Inventory)
        {
            if (slot.Empty || MarketKind(slot.Itemstack) != "meal") continue;
            var choice = EvaluateFood(container, slot, eater);
            if (choice == null) continue;
            choice.Score = VarietyScore(choice.Props, citizen) * SpoilPull(slot);
            if (best == null || choice.Score > best.Score) best = choice;
        }
        return best;
    }

    /// <summary>The best food in one container, or null. Raw ingredients only with <paramref name="allowRaw"/>.</summary>
    public FoodChoice? FindFoodIn(BlockEntityContainer container, CitizenRecord citizen, EntityAgent eater, bool allowRaw)
    {
        if (!IsFoodStore(container)) return null;

        // A bowl, pot or crock placed in the world keeps its servings on the block, not in its slots.
        if (container is IBlockEntityMealContainer placed)
        {
            if (placed.QuantityServings <= 0) return null;
            var mealStack = container.Block.OnPickBlock(sapi.World, container.Pos);
            if (mealStack?.Block is not IBlockMealContainer) return null;
            var placedSlot = new DummySlot(mealStack, placed.inventory);
            var placedChoice = EvaluateFood(container, placedSlot, eater);
            if (placedChoice == null) return null;
            placedChoice.Placed = placed;
            placedChoice.Score = VarietyScore(placedChoice.Props, citizen) * SpoilPull(placedSlot);
            return placedChoice;
        }

        // From a fire pit only what has finished cooking, such as a pot of stew left in it.
        IEnumerable<ItemSlot> slots = container is BlockEntityFirepit firepit ? new[] { firepit.outputSlot } : container.Inventory;
        FoodChoice? best = null;
        foreach (var slot in slots)
        {
            if (slot.Empty) continue;
            bool raw = IsRawIngredient(slot.Itemstack);
            if (raw && !allowRaw) continue;
            var choice = EvaluateFood(container, slot, eater);
            if (choice == null) continue;
            // Food that will spoil soon is eaten first (ColonyManager.Freshness.cs).
            choice.Score = VarietyScore(choice.Props, citizen) * SpoilPull(slot) * (raw ? RawIngredientScoreMul : 1);
            if (best == null || choice.Score > best.Score) best = choice;
        }
        return best;
    }

    /// <summary>
    /// Containers settlers may take food from. Ovens, querns, troughs, barrels and buckets are left
    /// alone so settlers do not eat what is being baked, ground, fed to animals or pickled, and so are
    /// the cooking stations of A Culinary Artillery, which this mod only knows by name. Fire pits are
    /// handled in FindFoodIn.
    /// </summary>
    static bool IsFoodStore(BlockEntityContainer container)
    {
        if (container is BlockEntityOven or BlockEntityQuern or BlockEntityTrough or BlockEntityLiquidContainer) return false;
        string name = container.GetType().Name;
        // Stone Bake Oven baking spaces too: settlers wait for the cook to take the bread out.
        return !(name.Contains("MixingBowl") || name.Contains("Saucepan") || name.Contains("Cauldron") || name.Contains("Kiln") || name.StartsWith("BlockEntityOven"));
    }

    /// <summary>
    /// What eating from this slot would give: one serving of a meal, one slice of a pie, or one item of
    /// plain food. Null for anything that is not food, is still raw dough, gives nothing or would hurt.
    /// </summary>
    public FoodChoice? EvaluateFood(BlockEntityContainer container, ItemSlot slot, EntityAgent eater)
    {
        var stack = slot.Itemstack;
        if (stack == null) return null;
        var world = sapi.World;

        FoodNutritionProperties[]? props;
        try
        {
            if (stack.Block is BlockPie pie)
            {
                if (pie.State == "raw") return null;
                // A whole pie is one serving of four slices; a settler eats one slice.
                props = Scale(pie.GetContentNutritionProperties(world, slot, eater), 0.25f);
            }
            else if (stack.Block is IBlockMealContainer meal)
            {
                float servings = meal.GetQuantityServings(world, stack);
                var contents = meal.GetNonEmptyContents(world, stack);
                if (servings <= 0 || contents == null || contents.Length == 0) return null;
                props = MealServingProps(slot, meal, contents, eater);
                props = Scale(props, Math.Min(1, servings));
            }
            else
            {
                props = PlainFoodProps(slot, eater);
            }
        }
        catch (Exception e)
        {
            // A food mod's meal that the game's nutrition code cannot read should not stop settlers eating.
            sapi.Logger.Debug("[createacolony] Could not read the nutrition of {0}: {1}", stack.Collectible.Code, e.Message);
            return null;
        }

        if (props == null || props.Length == 0) return null;
        var choice = new FoodChoice { Container = container, Slot = slot, Props = props };
        if (choice.Satiety <= 0 || choice.Health < 0) return null;
        return choice;
    }

    /// <summary>Nutrition of one serving of a meal in a bowl, cooking pot or crock.</summary>
    FoodNutritionProperties[]? MealServingProps(ItemSlot slot, IBlockMealContainer meal, ItemStack[] contents, EntityAgent eater)
    {
        if (meal is BlockMeal bowl) return bowl.GetContentNutritionProperties(sapi.World, slot, eater);
        // Pots and crocks hold servings the game measures as if they were in a bowl.
        bool noRecipe = string.IsNullOrEmpty(meal.GetRecipeCode(sapi.World, slot.Itemstack));
        return BlockMeal.GetContentNutritionProperties(sapi.World, slot, contents, eater, noRecipe);
    }

    /// <summary>
    /// Nutrition of one plain food item, less for spoiling food, plus the extra nutrients some
    /// Expanded Foods items carry (A Culinary Artillery stores them on the item as "expandedSats").
    /// </summary>
    FoodNutritionProperties[]? PlainFoodProps(ItemSlot slot, EntityAgent eater)
    {
        var world = sapi.World;
        var stack = slot.Itemstack!;
        var props = stack.Collectible.GetNutritionProperties(world, stack, eater);
        if (props == null) return null;

        var state = stack.Collectible.UpdateAndGetTransitionState(world, slot, EnumTransitionType.Perish);
        float spoil = state?.TransitionLevel ?? 0;
        float satMul = GlobalConstants.FoodSpoilageSatLossMul(spoil, stack, eater);
        float healthMul = GlobalConstants.FoodSpoilageHealthLossMul(spoil, stack, eater);

        var list = new List<FoodNutritionProperties>();
        var main = props.Clone();
        main.Satiety *= satMul;
        main.Health *= healthMul;
        list.Add(main);

        // Layout: [health, fruit, vegetable, protein, grain, dairy], each times the item's "satMult".
        if (stack.Attributes["expandedSats"] is FloatArrayAttribute extra && extra.value?.Length >= 6)
        {
            float mult = stack.Collectible.Attributes?["satMult"].AsFloat(1f) ?? 1f;
            for (int i = 1; i <= 5; i++)
            {
                if (extra.value[i] == 0) continue;
                list.Add(new FoodNutritionProperties { FoodCategory = (EnumFoodCategory)(i - 1), Satiety = extra.value[i] * mult * satMul });
            }
            if (extra.value[0] != 0) main.Health += extra.value[0] * mult * healthMul;
        }
        return list.ToArray();
    }

    static FoodNutritionProperties[]? Scale(FoodNutritionProperties[]? props, float factor)
    {
        if (props == null) return null;
        return props.Where(p => p != null).Select(p =>
        {
            var c = p.Clone();
            c.Satiety *= factor;
            c.Health *= factor;
            return c;
        }).ToArray();
    }

    /// <summary>
    /// Saturation, with each food group counted for less the more of the settler's recent meals had
    /// it. A settler who has eaten only bread lately values a bowl of fish stew well above more bread.
    /// </summary>
    float VarietyScore(FoodNutritionProperties[] props, CitizenRecord citizen)
    {
        float score = 0;
        float fussy = TraitMult(citizen, "variety"); // Iron Stomach minds less, Picky Eater more
        foreach (var p in props)
        {
            int had = citizen.RecentFood.Count(c => c == (int)p.FoodCategory);
            score += p.Satiety / (1 + had * fussy);
        }
        return score;
    }

    /// <summary>
    /// Eats the choice: takes one serving, slice or item out of the container, leaving the empty bowl or
    /// pot behind, and gives the settler its saturation and health. Returns the food's name, or null if
    /// the food is gone or changed since it was picked.
    /// </summary>
    /// <summary>The five food groups shown as nutrition bars (EnumFoodCategory 0-4).</summary>
    public const int FoodGroups = 5;

    /// <summary>Fills the settler's food groups with what they ate, as eating fills a player's nutrition bars.</summary>
    public void AddNutrition(CitizenRecord citizen, IEnumerable<FoodNutritionProperties> props)
    {
        while (citizen.Nutrition.Count < FoodGroups) citizen.Nutrition.Add(0);
        foreach (var p in props)
        {
            int group = (int)p.FoodCategory;
            if (p.Satiety <= 0 || group < 0 || group >= FoodGroups) continue;
            citizen.Nutrition[group] = Math.Min(Config.SaturationMax, citizen.Nutrition[group] + p.Satiety);
        }
    }

    public string? EatFood(Colony colony, CitizenRecord citizen, EntityAgent eater, BlockEntityContainer container) =>
        EatFood(colony, citizen, eater, container, out _);

    /// <summary>As EatFood, also giving a copy of what was eaten (one item, slice or bowl) for the settler to hold.</summary>
    public string? EatFood(Colony colony, CitizenRecord citizen, EntityAgent eater, BlockEntityContainer container, out ItemStack? eaten)
    {
        eaten = null;
        bool atMarket = IsMarketStore(colony, container);
        if (atMarket && !CanBuy(colony, citizen, "meal")) return null;
        var choice = atMarket ? MarketMealIn(container, citizen, eater) : FindFoodIn(container, citizen, eater, EatsRaw(citizen));
        if (choice == null) return null;

        var slot = choice.Slot;
        var copy = slot.Itemstack!.Clone();
        string name = slot.Itemstack!.GetName();
        bool cooked = choice.Placed != null || IsCookedFood(slot.Itemstack);
        if (!(choice.Placed != null ? TakePlacedServing(container, choice.Placed) : TakeServing(container, slot))) return null;
        eaten = copy;
        if (atMarket) PayAtMarket(colony, citizen, "meal", container);

        citizen.Saturation = Math.Min(Config.SaturationMax, citizen.Saturation + choice.Satiety);
        AddNutrition(citizen, choice.Props);
        if (cooked) AddThought(citizen, "meal");
        if (choice.Health > 0)
        {
            eater.ReceiveDamage(new DamageSource { Source = EnumDamageSource.Internal, Type = EnumDamageType.Heal }, choice.Health);
        }

        foreach (var cat in choice.Props.Where(p => p.Satiety > 0).Select(p => (int)p.FoodCategory).Distinct())
        {
            citizen.RecentFood.Add(cat);
        }
        if (citizen.RecentFood.Count > RecentFoodMemory) citizen.RecentFood.RemoveRange(0, citizen.RecentFood.Count - RecentFoodMemory);

        // The block may be gone: a finished placed pie, or a bowl or pot swapped for its empty version.
        if (sapi.World.BlockAccessor.GetBlockEntity(container.Pos) == container)
        {
            if (choice.Placed == null) slot.MarkDirty();
            container.MarkDirty(true);
        }
        SaveColony(colony);
        return name;
    }

    /// <summary>Removes what one bite of EvaluateFood stands for from the slot.</summary>
    bool TakeServing(BlockEntityContainer container, ItemSlot slot)
    {
        var world = sapi.World;
        var stack = slot.Itemstack;
        if (stack == null) return false;

        if (stack.Block is BlockPie)
        {
            // A placed pie removes its own block when the last slice is taken.
            if (container is BlockEntityPie placedPie) return placedPie.TakeSlice() != null;

            var pie = SplitOne(container, slot);
            int slices = pie.Attributes.GetAsInt("pieSize", 4);
            if (slices <= 1)
            {
                slot.Itemstack = null;
                return true;
            }
            pie.Attributes.SetInt("pieSize", slices - 1);
            if (pie.Attributes.HasAttribute("quantityServings"))
            {
                pie.Attributes.SetFloat("quantityServings", Math.Max(0.25f, pie.Attributes.GetFloat("quantityServings") - 0.25f));
            }
            return true;
        }

        if (stack.Block is IBlockMealContainer meal)
        {
            var one = SplitOne(container, slot);
            float left = meal.GetQuantityServings(world, one) - 1;
            one.Attributes.RemoveAttribute("sealed");
            if (left > 0.001f)
            {
                meal.SetQuantityServings(world, one, left);
                return true;
            }

            meal.SetQuantityServings(world, one, 0);
            // The bowl or pot stays behind empty, the way a player leaves it.
            string? emptyCode = one.Block.Attributes?["eatenBlock"].AsString() ?? one.Block.Attributes?["emptiedBlockCode"].AsString();
            var empty = emptyCode == null ? null : world.GetBlock(new AssetLocation(emptyCode));
            if (empty != null) slot.Itemstack = new ItemStack(empty);
            else if (one.Block is BlockMeal) slot.Itemstack = null;
            return true;
        }

        return slot.TakeOut(1) != null;
    }

    /// <summary>Takes one serving from a bowl, cooking pot or crock placed in the world.</summary>
    bool TakePlacedServing(BlockEntityContainer container, IBlockEntityMealContainer placed)
    {
        placed.QuantityServings -= 1;
        if (placed.QuantityServings > 0.001f)
        {
            placed.MarkDirty(true);
            return true;
        }

        var world = sapi.World;
        var block = container.Block;
        string? emptyCode = container switch
        {
            BlockEntityMeal => block.Attributes?["eatenBlock"].AsString(),
            BlockEntityCookedContainer => block.Attributes?["emptiedBlockCode"].AsString() ?? block.CodeWithVariant("type", "fired").ToString(),
            _ => null
        };
        var empty = emptyCode == null ? null : world.GetBlock(new AssetLocation(emptyCode));
        if (empty != null)
        {
            world.BlockAccessor.SetBlock(empty.BlockId, container.Pos);
            return true;
        }

        // A crock (or a food mod's container) stays where it is, empty.
        placed.QuantityServings = 0;
        placed.RecipeCode = "";
        placed.inventory.DiscardAll();
        placed.MarkDirty(true);
        return true;
    }

    /// <summary>
    /// Makes sure the slot holds a single item so a serving can be taken from it, moving the rest of the
    /// stack to another slot of the container, or dropping it beside the container if there is no room.
    /// </summary>
    ItemStack SplitOne(BlockEntityContainer container, ItemSlot slot)
    {
        var stack = slot.Itemstack!;
        if (stack.StackSize <= 1) return stack;

        var rest = stack.Clone();
        rest.StackSize = stack.StackSize - 1;
        stack.StackSize = 1;

        var restSlot = new DummySlot(rest);
        foreach (var other in container.Inventory)
        {
            if (other == slot || restSlot.Empty) continue;
            if (other.Empty || other.Itemstack.Equals(sapi.World, rest, GlobalConstants.IgnoredStackAttributes))
            {
                restSlot.TryPutInto(sapi.World, other, restSlot.StackSize);
                other.MarkDirty();
            }
        }
        if (!restSlot.Empty) sapi.World.SpawnItemEntity(restSlot.Itemstack, container.Pos.ToVec3d().Add(0.5, 1.1, 0.5));
        return stack;
    }
}
