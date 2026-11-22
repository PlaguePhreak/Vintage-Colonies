using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;
using ItemMatch = System.Func<Vintagestory.API.Common.ItemStack, bool>;

namespace CreateAColony.Colonies;

/// <summary>
/// The cook of a Cook's Hut cooks with the game's own fire pit. It puts raw food (meat, fish, dough,
/// anything the game cooks in a fire pit into something edible) in the fire pit by the hut, adds
/// firewood, lights it, and takes the cooked food out once the game has cooked it. Cooked food goes on
/// the hut's Food Shelves shelves when it has any (ColonyManager.FoodShelves.cs), else stays in the hut's
/// storage, where hungry settlers find it. The Kitchen tab says how much of each to keep (ColonyManager.KitchenMenu.cs).
/// </summary>
public partial class ColonyManager
{
    /// <summary>One cooked food and the raw foods a fire pit cooks into it. Its key is the cooked food's code.</summary>
    public sealed class KitchenKind
    {
        public required string Key;
        /// <summary>The cooked food, for the hut window.</summary>
        public required ItemStack Sample;
        public readonly HashSet<AssetLocation> Raw = new();

        public bool IsRaw(ItemStack s) => Raw.Contains(s.Collectible.Code);
        public bool IsMine(ItemStack s) => s.Collectible.Code.ToString() == Key;
    }

    List<KitchenKind>? kitchenKinds;

    /// <summary>Everything the game cooks in a fire pit, without a pot, into food.</summary>
    public List<KitchenKind> KitchenKinds()
    {
        if (kitchenKinds != null) return kitchenKinds;
        var kinds = new Dictionary<string, KitchenKind>();
        foreach (var obj in sapi.World.Collectibles)
        {
            if (obj?.Code == null || CookedFrom(obj) is not { } cooked) continue;
            string key = cooked.Collectible.Code.ToString();
            if (!kinds.TryGetValue(key, out var kind)) kinds[key] = kind = new KitchenKind { Key = key, Sample = cooked.Clone() };
            kind.Raw.Add(obj.Code);
        }
        // Food that is already cooked is never cooked again (bread into charred bread, say).
        var cookedCodes = kinds.Values.Select(k => k.Sample.Collectible.Code).ToHashSet();
        foreach (var kind in kinds.Values) kind.Raw.RemoveWhere(cookedCodes.Contains);
        return kitchenKinds = kinds.Values.Where(k => k.Raw.Count > 0).OrderBy(k => k.Key).ToList();
    }

    /// <summary>What a fire pit cooks this into, if that is food and needs no cooking pot; else null.</summary>
    ItemStack? CookedFrom(CollectibleObject obj)
    {
        var props = obj.CombustibleProps;
        if (props == null || props.RequiresContainer || props.SmeltingType is not (EnumSmeltType.Cook or EnumSmeltType.Bake)) return null;
        if (props.MeltingPoint > Config.CookMaxTemperature) return null;
        var cooked = props.SmeltedStack?.ResolvedItemstack;
        if (cooked == null || cooked.Collectible.Code.Equals(obj.Code)) return null;
        return cooked.Collectible.NutritionProps != null ? cooked : null;
    }

    public KitchenKind? KitchenKindOf(ItemStack stack) => KitchenKinds().FirstOrDefault(k => k.IsRaw(stack));

    /// <summary>True for raw food a cook can cook in a fire pit.</summary>
    public bool IsGrillable(ItemStack stack) => KitchenKindOf(stack) != null;

    public static bool IsFirewood(ItemStack stack) => stack.Collectible.Code.Path == "firewood";

    /// <summary>How many of a cooked food the hut keeps in stock.</summary>
    public int KitchenKeep(Building hut, KitchenKind kind) =>
        hut.KitchenChosen ? hut.KitchenKeep.GetValueOrDefault(kind.Key) : Config.KitchenAutoKeep;

    /// <summary>How much of a cooked food is ready: at the hut (and on its kitchen shelves), in the warehouse, with the cook and in the fire pit.</summary>
    public int KitchenStock(Colony colony, Building hut, KitchenKind kind, CitizenRecord? cook) =>
        KitchenStock(colony, hut, kind, cook, CookFirepit(colony, hut));

    int KitchenStock(Colony colony, Building hut, KitchenKind kind, CitizenRecord? cook, BlockEntityFirepit? pit)
    {
        ItemMatch mine = kind.IsMine;
        int count = CountItems(WarehouseContainers(colony), mine) + CountItems(KitchenStores(colony, hut), mine);
        if (cook != null) count += PackCount(cook, mine);
        if (pit != null)
        {
            if (!pit.outputSlot.Empty && mine(pit.outputSlot.Itemstack)) count += pit.outputSlot.StackSize;
            // What is cooking now counts too, so the cook does not start a second batch of it.
            if (!pit.inputSlot.Empty && kind.IsRaw(pit.inputSlot.Itemstack)) count += pit.inputSlot.StackSize;
        }
        return count;
    }

    /// <summary>The cooked foods there are too few of, most wanted first.</summary>
    public List<KitchenKind> WantedFood(Colony colony, Building hut, CitizenRecord? cook)
    {
        var pit = CookFirepit(colony, hut);
        // With a Stone Bake Oven, dough goes in the oven rather than the fire pit.
        bool oven = CookOven(colony, hut) != null;
        // With a Bakery and its baker, bread is the baker's (ColonyManager.Baking.cs).
        bool baker = hut.Type != BuildingTypes.Bakery && BakerWorks(colony);
        return ByPriority(hut, KitchenKinds()
            .Where(k => KitchenKeep(hut, k) > 0 && !(oven && k.Raw.All(IsBakeableCode)) && !(baker && k.Raw.All(r => r.Path.StartsWith("dough"))))
            .Select(k => (Kind: k, k.Key, Short: KitchenKeep(hut, k) - KitchenStock(colony, hut, k, cook, pit)))
            .Where(k => k.Short > 0))
            .ToList();
    }

    /// <summary>
    /// The fire pit the cook uses: the nearest finished fire pit within reach of the hut. The Cook's Hut
    /// blueprint has one; a fire pit placed nearby by hand works as well.
    /// </summary>
    public BlockEntityFirepit? CookFirepit(Colony colony, Building hut)
    {
        int r = Config.CookFirepitRadius;
        var h = hut.HutPos;
        var area = new Cuboidi(h.X - r, h.Y - 4, h.Z - r, h.X + r, h.Y + 4, h.Z + r);
        return FindBlockEntities<BlockEntityFirepit>(area)
            .Where(p => !p.Block.Code.Path.Contains("construct") && !IsOvenFire(p))
            .OrderBy(p => p.Pos.DistanceSqTo(h.X, h.Y, h.Z))
            .FirstOrDefault();
    }

    /// <summary>Puts raw food in the fire pit, with firewood, and lights it.</summary>
    public void LoadFirepit(Colony colony, CitizenRecord cook, BlockEntityFirepit pit, KitchenKind kind)
    {
        if (!pit.inputSlot.Empty) return;
        var raw = TakeFromPack(colony, cook, kind.IsRaw, Config.CookBatch);
        if (raw == null) return;
        pit.inputSlot.Itemstack = raw;
        pit.inputSlot.MarkDirty();
        FuelFirepit(colony, cook, pit);
    }

    /// <summary>Adds firewood to the fire pit if it has none, and lights it if it is not burning.</summary>
    public void FuelFirepit(Colony colony, CitizenRecord cook, BlockEntityFirepit pit)
    {
        if (pit.fuelSlot.Empty && TakeFromPack(colony, cook, IsFirewood, Config.CookFirewood) is { } wood)
        {
            pit.fuelSlot.Itemstack = wood;
            pit.fuelSlot.MarkDirty();
        }
        if (!pit.IsBurning && !pit.fuelSlot.Empty)
        {
            pit.igniteFuel();
            // The fire pit lights the next piece of firewood by itself while there is something to cook.
            pit.canIgniteFuel = true;
        }
        pit.MarkDirty(true);
    }

    /// <summary>Takes the cooked food out of the fire pit into the cook's inventory.</summary>
    public void EmptyFirepit(Colony colony, CitizenRecord cook, BlockEntityFirepit pit)
    {
        var cooked = pit.outputSlot.Itemstack;
        if (cooked == null) return;
        // A skilled cook sometimes gets one more portion: a serving more in a pot, one more piece grilled.
        if (RollPerk(colony, cook, "portion"))
        {
            if (cooked.Block is IBlockMealContainer meal) meal.SetQuantityServings(sapi.World, cooked, meal.GetQuantityServings(sapi.World, cooked) + 1);
            else cooked.StackSize++;
        }
        AddToPack(colony, cook, cooked);
        pit.outputSlot.Itemstack = cooked.StackSize > 0 ? cooked : null;
        pit.outputSlot.MarkDirty();
        pit.MarkDirty(true);
    }

    /// <summary>
    /// At a Cook's Hut everything that is not food, firewood or a cooking pot goes to the warehouse. Cooked
    /// food and pot meals stay, so hungry settlers eat at the hut.
    /// </summary>
    System.Func<ItemStack, bool> CookHutJunk() => s => !IsFirewood(s) && s.Collectible.NutritionProps == null && !IsGrillable(s) && !IsBakeable(s) && PiePart(s) == null
        && s.Block is not (IBlockMealContainer or BlockCookingContainer) && !IsMealIngredient(s);

    /// <summary>True for anything a meal recipe can use (grain, vegetables, eggs and so on).</summary>
    public bool IsMealIngredient(ItemStack s) =>
        !IsLiquid(s) && MealRecipes().Any(r => r.Ingredients!.Any(i => i.GetMatchingStack(s) != null));

    /// <summary>Asks couriers to bring something to the Cook's Hut, once at a time.</summary>
    public void EnsureCookRequest(Colony colony, Building hut, string code, int quantity)
    {
        var chest = HutStorage(colony, hut).FirstOrDefault();
        if (chest == null) return;
        string tag = RequestTag(hut);
        if (!colony.Requests.Any(r => r.RequestedBy == tag && r.ItemCode == code)) AddRequest(colony, code, quantity, chest.Pos, tag);
    }

    /// <summary>The raw food in the warehouse for a cooked food, so the cook can ask couriers for it by its code.</summary>
    public ItemStack? RawInWarehouse(Colony colony, KitchenKind kind)
    {
        foreach (var chest in WarehouseContainers(colony))
        {
            foreach (var slot in chest.Inventory)
            {
                if (!slot.Empty && kind.IsRaw(slot.Itemstack)) return slot.Itemstack;
            }
        }
        return null;
    }

    /// <summary>A line about the fire pit for the hut window.</summary>
    public string FirepitInfo(Colony colony, Building hut)
    {
        var pit = CookFirepit(colony, hut);
        if (pit == null) return Lang.Get("createacolony:firepit-none", Config.CookFirepitRadius);
        if (!pit.inputSlot.Empty) return Lang.Get(pit.IsBurning ? "createacolony:firepit-cooking" : "createacolony:firepit-waiting", pit.inputSlot.Itemstack.GetName());
        return Lang.Get("createacolony:firepit-ready");
    }
}
