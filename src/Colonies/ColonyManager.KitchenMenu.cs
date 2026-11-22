using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.GameContent;

namespace CreateAColony.Colonies;

/// <summary>
/// What the Kitchen tab of a Cook's Hut shows (Aaron, 0.124.0): the food the cook has on hand (its inventory, the
/// hut storage and kitchen shelves, and the warehouse) and the dishes it can make from that right now: fire pit
/// food, pot meals (named from the ingredients it would use, such as "Hearty pork stew") and Stone Bake Oven bakes.
/// Until an officer picks dishes the cook keeps a few of each dish it can make (config KitchenAutoKeep and
/// KitchenAutoMealKeep). The first pick makes the hut's own list from what is shown, and from then on the cook only
/// makes the dishes on that list. Reset gives the choice back to the cook.
/// </summary>
public partial class ColonyManager
{
    /// <summary>One dish on the Kitchen tab.</summary>
    public sealed class MenuDish
    {
        public required string Key;
        public required ItemStack Icon;
        public string? Name;
        /// <summary>What it would be made from, or why it cannot be made now.</summary>
        public string Detail = "";
        public bool CanMake;
        public int Keep;
        public int Have;
        public int NeedsLevel;
        public bool Locked;
    }

    /// <summary>
    /// The dishes for the Kitchen tab: the ones the cook can make from the food on hand, and the ones the hut keeps
    /// that it cannot make now (shown so an officer can see why they are not being made).
    /// </summary>
    public List<MenuDish> KitchenMenu(Colony colony, Building hut, CitizenRecord? cook)
    {
        var dishes = new List<MenuDish>();
        var larder = Larder(colony, hut, cook);
        int cookLevel = cook == null ? -1 : CookLevel(colony, cook);
        string missing = Lang.Get("createacolony:kitchen-missing");

        foreach (var kind in KitchenKinds())
        {
            var raw = larder.Values.Where(a => kind.IsRaw(a.Sample)).Select(a => a.Sample.GetName()).ToList();
            int keep = KitchenKeep(hut, kind);
            if (raw.Count == 0 && (!hut.KitchenChosen || keep <= 0)) continue;
            dishes.Add(new MenuDish
            {
                Key = kind.Key,
                Icon = kind.Sample,
                CanMake = raw.Count > 0,
                Detail = raw.Count > 0 ? Lang.Get("createacolony:kitchen-from", string.Join(", ", raw.Distinct())) : missing,
                Keep = raw.Count > 0 || hut.KitchenChosen ? keep : 0,
                Have = KitchenStock(colony, hut, kind, cook)
            });
        }

        var (slots, maxSlotStack, _) = PotSize(colony, hut, cook);
        if (cook != null && WaterAvailable(colony, hut, cook) && WaterSample() is { } water) larder[water.Collectible.Code] = (water, 1_000_000);
        foreach (var recipe in MealRecipes())
        {
            var plan = slots > 0 ? PlanMeal(recipe, larder, slots, 1, maxSlotStack) : null;
            int keep = MealKeep(hut, recipe);
            if (plan == null && (!hut.KitchenChosen || keep <= 0)) continue;
            if (MealIcon(recipe) is not { } icon) continue;
            int needs = MealLevel(recipe);
            dishes.Add(new MenuDish
            {
                Key = MealKey(recipe),
                Icon = icon,
                Name = plan != null ? MealNameFor(plan) : MealName(recipe),
                CanMake = plan != null,
                Detail = plan != null ? Lang.Get("createacolony:kitchen-from", string.Join(", ", plan.Slots.Select(s => s.Sample.GetName()).Distinct())) : missing,
                Keep = keep,
                Have = MealStock(colony, hut, recipe, cook),
                NeedsLevel = needs,
                Locked = cook != null && needs > cookLevel
            });
        }

        foreach (var bake in OvenKinds())
        {
            var from = BakeFrom(bake, larder);
            int keep = BakeKeep(hut, bake);
            if (from == null && (!hut.KitchenChosen || keep <= 0)) continue;
            int needs = BakeLevel(bake);
            dishes.Add(new MenuDish
            {
                Key = bake.Key,
                Icon = bake.Sample,
                Name = bake.IsPie ? Lang.Get("createacolony:kitchen-pie") : Lang.Get("createacolony:kitchen-bake", bake.Sample.GetName()),
                CanMake = from != null,
                Detail = from != null ? Lang.Get("createacolony:kitchen-from", from) : missing,
                Keep = keep,
                Have = BakeStock(colony, hut, bake, cook),
                NeedsLevel = needs,
                Locked = cook != null && needs > cookLevel
            });
        }
        return dishes;
    }

    /// <summary>What a bake would be made from with the food on hand ("Rye dough, Blueberry"), or null when it cannot be made.</summary>
    string? BakeFrom(OvenKind bake, Dictionary<AssetLocation, (ItemStack Sample, int Count)> larder)
    {
        if (bake.IsPie)
        {
            var dough = larder.Values.Where(a => PiePart(a.Sample) == "crust" && a.Count >= 4).OrderByDescending(a => a.Count).FirstOrDefault();
            if (dough.Sample == null) return null;
            var filling = larder.Values.Where(a => PiePart(a.Sample) == "filling" && a.Count >= 8 && !a.Sample.Collectible.Code.Equals(dough.Sample.Collectible.Code))
                .OrderByDescending(a => a.Count).FirstOrDefault();
            return filling.Sample == null ? null : dough.Sample.GetName() + ", " + filling.Sample.GetName();
        }
        var raw = larder.Values.Where(a => bake.IsRaw(a.Sample)).Select(a => a.Sample.GetName()).Distinct().ToList();
        return raw.Count == 0 ? null : string.Join(", ", raw);
    }

    /// <summary>A pot meal's name from the ingredients the plan uses, as the game names a cooked pot.</summary>
    public string MealNameFor(MealPlan plan)
    {
        try
        {
            var stacks = plan.Slots.Select(s =>
            {
                var stack = s.Sample.Clone();
                stack.StackSize = Math.Max(1, s.Count / Math.Max(1, plan.Servings));
                return stack;
            }).ToArray();
            string name = plan.Recipe.GetOutputName(sapi.World, stacks);
            return string.IsNullOrWhiteSpace(name) || name == Lang.Get("unknown") ? MealName(plan.Recipe) : name;
        }
        catch (Exception)
        {
            return MealName(plan.Recipe);
        }
    }

    /// <summary>The pot the cook would use: how many ingredient slots it has, how many of one item a slot takes, and the most servings it cooks.</summary>
    (int Slots, int MaxSlotStack, int MaxServings) PotSize(Colony colony, Building hut, CitizenRecord? cook)
    {
        var pot = (cook != null ? FindCleanPot(colony, hut, cook) : null) ?? DefaultPot();
        if (pot?.Block is not BlockCookingContainer potBlock) return (0, 0, 0);
        return (pot.ItemAttributes?["cookingContainerSlots"].AsInt(4) ?? 4, pot.ItemAttributes?["maxContainerSlotStackSize"].AsInt(999) ?? 999, potBlock.MaxServingSize);
    }

    /// <summary>The food the cook has to cook with, for the Kitchen tab: raw food, meal ingredients, dough and pie fillings, most first.</summary>
    public List<(ItemStack Sample, int Count)> KitchenOnHand(Colony colony, Building hut, CitizenRecord? cook) =>
        Larder(colony, hut, cook).Values
            .Where(a => IsGrillable(a.Sample) || IsMealIngredient(a.Sample) || IsBakeable(a.Sample) || PiePart(a.Sample) != null)
            .OrderByDescending(a => a.Count)
            .ToList();

    /// <summary>
    /// Before 0.124.0 the first change on the Kitchen tab copied every food the game has into the hut's list, so old
    /// huts kept 8 of everything. Such a list goes back to the cook choosing.
    /// </summary>
    public void UpgradeKitchenList(Colony colony, Building hut)
    {
        if (!hut.KitchenChosen || hut.KitchenKeep.Count < KitchenKinds().Count + MealRecipes().Count) return;
        hut.KitchenKeep.Clear();
        hut.KitchenChosen = false;
        MarkDirty(colony);
    }

    /// <summary>
    /// Sets how many of a dish to keep. The first pick turns the dishes the tab shows into the hut's own list, so
    /// the other dishes shown keep their amounts; dishes it had nothing for stay off. An empty key gives the choice back to the cook.
    /// </summary>
    public void SetKitchenKeep(Colony colony, Building hut, string key, int keep)
    {
        if (key.Length == 0)
        {
            hut.KitchenKeep.Clear();
            hut.KitchenChosen = false;
            MarkDirty(colony);
            return;
        }
        if (!hut.KitchenChosen)
        {
            var cook = WorkerAt(colony, hut, Jobs.Cook);
            hut.KitchenKeep = KitchenMenu(colony, hut, cook).Where(d => d.CanMake && d.Keep > 0).ToDictionary(d => d.Key, d => d.Keep);
            hut.KitchenChosen = true;
        }
        if (keep > 0) hut.KitchenKeep[key] = keep;
        else hut.KitchenKeep.Remove(key);
        MarkDirty(colony);
    }
}
