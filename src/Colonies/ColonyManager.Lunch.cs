using System;
using System.Collections.Generic;
using System.Linq;
using CreateAColony.Settlers;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.GameContent;

namespace CreateAColony.Colonies;

/// <summary>
/// Food and light a settler carries with it.
/// Packed food: a few slots of plain food kept apart from the inventory (so it is never unloaded with the
/// harvest). The settler snacks from it whenever a bite fits in its stomach, so it stays close to full while
/// it works. Only once the packed food is gone does it keep working down to the hunger line (EatBelowFraction,
/// 40%) and then walk off for a meal, packing food for later while it is at the store.
/// Torches: in the dark (at night, in a mine) a settler with a torch holds it in its left hand.
/// </summary>
public partial class ColonyManager
{
    public const int LunchSlots = 3;
    /// <summary>Below this light level (0-32, the game's scale) a settler lights its way with a torch.</summary>
    const int TorchBelowLight = 8;

    public List<ItemStack> LunchItems(CitizenRecord citizen) => Resolve(citizen.Lunch);

    public void SaveLunch(Colony colony, CitizenRecord citizen, IEnumerable<ItemStack> stacks)
    {
        citizen.Lunch = stacks.Where(s => s.StackSize > 0).Select(s => s.ToBytes()).ToList();
        MarkDirty(colony);
    }

    /// <summary>
    /// What one item of plain food gives, as for eating from a chest. Null for meals in bowls or pots and
    /// pies (they stay in the kitchen) and for anything that is not food or would hurt.
    /// </summary>
    FoodNutritionProperties[]? LunchProps(ItemStack stack, EntityAgent eater)
    {
        // A bowl with a meal in it is packed food too (ColonyManager.Crocks.cs); a pot, crock or pie stays in the kitchen.
        if (stack.Block is BlockMeal bowl) return BowlProps(stack, bowl, eater);
        if (stack.Block is IBlockMealContainer or BlockPie) return null;
        FoodNutritionProperties[]? props;
        try
        {
            props = PlainFoodProps(new DummySlot(stack.Clone()), eater);
        }
        catch (Exception)
        {
            return null;
        }
        if (props == null || props.Sum(p => p.Satiety) <= 0 || props.Sum(p => p.Health) < 0) return null;
        return props;
    }

    /// <summary>What a serving of the meal in a carried bowl gives. Null for an empty or rotten bowl.</summary>
    FoodNutritionProperties[]? BowlProps(ItemStack stack, BlockMeal bowl, EntityAgent eater)
    {
        try
        {
            var copy = stack.Clone();
            if (bowl.GetQuantityServings(sapi.World, copy) <= 0 || IsRottenMeal(copy, update: true)) return null;
            var props = MealServingProps(new DummySlot(copy), bowl, bowl.GetNonEmptyContents(sapi.World, copy) ?? Array.Empty<ItemStack>(), eater);
            if (props == null || props.Sum(p => p.Satiety) <= 0 || props.Sum(p => p.Health) < 0) return null;
            return props;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public bool IsLunchFood(ItemStack stack, EntityAgent eater) => LunchProps(stack, eater) != null;

    /// <summary>True when the settler has packed food left that it can eat.</summary>
    public bool HasLunch(CitizenRecord citizen, EntityAgent eater) => Config.CarryFood && LunchItems(citizen).Any(s => LunchProps(s, eater) != null);

    /// <summary>Packed food that went off (rot, or spoiled so far it would hurt) moves to the inventory, to be dropped off with the rest.</summary>
    void ClearSpoiledLunch(Colony colony, CitizenRecord citizen, EntityAgent eater, List<ItemStack> items)
    {
        bool changed = false;
        // Bowls stay in the food slots: an empty one for the next meal, a rotten one to wash out.
        foreach (var stack in items.Where(s => !IsBowl(s) && LunchProps(s, eater) == null).ToList())
        {
            AddToPack(colony, citizen, stack);
            if (stack.StackSize <= 0) changed = true;
        }
        if (changed) SaveLunch(colony, citizen, items);
    }

    /// <summary>
    /// Eats one item of packed food when a whole bite fits in the stomach, or whenever the settler is
    /// hungry. The best one for variety is eaten, food that will spoil soon first. Returns its name, or null when it ate nothing.
    /// </summary>
    public string? SnackFromLunch(Colony colony, CitizenRecord citizen, EntityAgent eater)
    {
        if (!Config.CarryFood || citizen.Lunch.Count == 0 || citizen.Saturation < 0) return null;
        var items = LunchItems(citizen);
        ClearSpoiledLunch(colony, citizen, eater, items);
        bool hungry = citizen.Saturation < Config.SaturationMax * Config.EatBelowFraction;
        ItemStack? best = null;
        FoodNutritionProperties[]? bestProps = null;
        float bestScore = 0;
        foreach (var stack in items)
        {
            var props = LunchProps(stack, eater);
            if (props == null) continue;
            float satiety = props.Sum(p => p.Satiety);
            if (!hungry && citizen.Saturation + satiety > Config.SaturationMax) continue;
            float score = VarietyScore(props, citizen) * SpoilPull(new DummySlot(stack));
            if (best != null && score <= bestScore) continue;
            best = stack;
            bestProps = props;
            bestScore = score;
        }
        if (best == null || bestProps == null) return null;

        string name = best.GetName();
        if (best.Block is BlockMeal)
        {
            // The bowl is kept for the next meal.
            AddThought(citizen, "meal");
            int at = items.IndexOf(best);
            if (EatenBowl(best) is { } empty) items[at] = empty;
            else best.StackSize--;
        }
        else
        {
            if (IsCookedFood(best)) AddThought(citizen, "meal");
            best.StackSize--;
        }
        SaveLunch(colony, citizen, items);
        citizen.Saturation = Math.Min(Config.SaturationMax, citizen.Saturation + bestProps.Sum(p => p.Satiety));
        AddNutrition(citizen, bestProps);
        float health = bestProps.Sum(p => p.Health);
        if (health > 0) eater.ReceiveDamage(new DamageSource { Source = EnumDamageSource.Internal, Type = EnumDamageType.Heal }, health);
        foreach (var cat in bestProps.Where(p => p.Satiety > 0).Select(p => (int)p.FoodCategory).Distinct()) citizen.RecentFood.Add(cat);
        if (citizen.RecentFood.Count > RecentFoodMemory) citizen.RecentFood.RemoveRange(0, citizen.RecentFood.Count - RecentFoodMemory);
        sapi.World.PlaySoundAt(new AssetLocation("sounds/player/eat"), eater);
        return name;
    }

    /// <summary>
    /// At a food store after a meal: packs plain food until the settler carries CarryFoodSaturation worth
    /// (or its food slots are full), taking the kinds best for variety, and soonest to spoil, first. Returns how many items.
    /// </summary>
    public int PackLunch(Colony colony, CitizenRecord citizen, EntityAgent eater, BlockEntityContainer container)
    {
        // Nothing is packed from the Market: its meals are bought one at a time.
        if (!Config.CarryFood || !IsFoodStore(container) || IsMarketStore(colony, container)) return 0;
        var items = LunchItems(citizen);
        // A serving of a crock or pot meal goes along in the settler's bowl (ColonyManager.Crocks.cs).
        bool bowlFilled = FillBowl(colony, citizen, container, items);
        if (bowlFilled) SaveLunch(colony, citizen, items);
        if (container is IBlockEntityMealContainer) return bowlFilled ? 1 : 0;
        float carried = items.Sum(s => (LunchProps(s, eater)?.Sum(p => p.Satiety) ?? 0) * s.StackSize);
        IEnumerable<ItemSlot> slots = container is BlockEntityFirepit firepit ? new[] { firepit.outputSlot } : container.Inventory;
        var choices = slots.Where(s => !s.Empty)
            .Select(s => (Slot: s, Props: LunchProps(s.Itemstack, eater)))
            // Raw grain, meat and vegetables are left for the cooks.
            .Where(c => c.Props != null && !IsRawIngredient(c.Slot.Itemstack))
            .OrderByDescending(c => VarietyScore(c.Props!, citizen) * SpoilPull(c.Slot))
            .ToList();

        int taken = 0;
        foreach (var (slot, props) in choices)
        {
            float each = props!.Sum(p => p.Satiety);
            while (carried < Config.CarryFoodSaturation && !slot.Empty)
            {
                var stack = slot.Itemstack;
                var same = items.FirstOrDefault(s => s.Equals(sapi.World, stack, GlobalConstants.IgnoredStackAttributes) && s.StackSize < s.Collectible.MaxStackSize);
                if (same == null && items.Count >= LunchSlots) break;
                int want = (int)Math.Ceiling((Config.CarryFoodSaturation - carried) / each);
                int room = same == null ? stack.Collectible.MaxStackSize : same.Collectible.MaxStackSize - same.StackSize;
                int n = Math.Min(Math.Min(want, room), stack.StackSize);
                if (n <= 0) break;
                var part = slot.TakeOut(n);
                slot.MarkDirty();
                if (same != null) same.StackSize += part.StackSize;
                else items.Add(part);
                carried += each * part.StackSize;
                taken += part.StackSize;
            }
            if (carried >= Config.CarryFoodSaturation) break;
        }
        if (taken > 0)
        {
            container.MarkDirty(true);
            SaveLunch(colony, citizen, items);
        }
        return taken + (bowlFilled ? 1 : 0);
    }

    /// <summary>A lit torch, the kind that lights up the one who holds it.</summary>
    public static bool IsTorch(ItemStack? stack) => stack?.Block is BlockTorch torch && torch.LightHsv[2] > 0;

    /// <summary>True when it is dark where the settler stands: night outdoors, or a dark cave or house.</summary>
    public bool IsDark(EntityAgent settler) =>
        sapi.World.BlockAccessor.GetLightLevel(settler.Pos.AsBlockPos.Up(), EnumLightLevelType.MaxTimeOfDayLight) < TorchBelowLight;
}
