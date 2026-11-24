using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using CreateAColony.Settlers;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using ItemMatch = System.Func<Vintagestory.API.Common.ItemStack, bool>;

namespace CreateAColony.Colonies;

/// <summary>
/// Crocks, bowls and washing up (Aaron, 0.124.0).
/// The cook pours each pot meal it cooks into crocks, the way a player does: a crock holds 4 servings and frees the
/// pot for the next meal. It keeps a few empty crocks on hand (config CookSpareCrocks), and when the colony runs
/// short the Potter's Hut makes more on top of what its Pottery tab keeps.
/// Every adult settler carries a bowl in its food slots (config CarryBowl). After a meal at a crock or pot it fills
/// the bowl with a serving to take along and eats it later as packed food, keeping the empty bowl. Settlers without
/// a bowl take one from home, their hut, the warehouse or a Cook's Hut, and the Potter's Hut makes more when there
/// are none.
/// A bowl, crock or pot of rotten food is washed out in water, as a player does by dropping it in water: settlers
/// wash the bowls they carry and the cook the crocks and pots in its hut. The rot goes with them, for compost.
/// </summary>
public partial class ColonyManager
{
    // ---- What things are ----------------------------------------------------------------------

    public static bool IsCrock(ItemStack s) => s.Block is BlockCrock;

    /// <summary>A crock with nothing in it.</summary>
    public bool IsEmptyCrock(ItemStack s) =>
        s.Block is BlockCrock crock && crock.GetQuantityServings(sapi.World, s) <= 0 && (crock.GetNonEmptyContents(sapi.World, s)?.Length ?? 0) == 0;

    /// <summary>An empty bowl that takes a serving of a meal (the game's clay bowls, and food mods' bowls made the same way).</summary>
    public static bool IsEmptyBowl(ItemStack s) =>
        s.Block is BlockLiquidContainerBase bowl && s.Block is not BlockCrock && s.Block.Attributes?["mealBlockCode"].AsString() != null
        && s.Block.Attributes["servingCapacity"].AsFloat(1) <= 1 && bowl.GetContent(s) == null;

    /// <summary>A bowl, empty or with a meal in it (fresh or rotten).</summary>
    public static bool IsBowl(ItemStack s) => IsEmptyBowl(s) || s.Block is BlockMeal;

    /// <summary>A cooking pot with a cooked meal in it, not a crock.</summary>
    public bool IsCookedPot(ItemStack s) =>
        s.Block is BlockCookedContainer pot && pot.GetQuantityServings(sapi.World, s) > 0 && !IsRottenMeal(s);

    /// <summary>
    /// A bowl, crock or pot whose food has rotted. With update, the food's spoiling is brought up to date first
    /// (for things carried, which nothing else updates).
    /// </summary>
    public bool IsRottenMeal(ItemStack s, bool update = false)
    {
        if (s.Block is not IBlockMealContainer meal) return false;
        try
        {
            if (update) s.Collectible.UpdateAndGetTransitionStates(sapi.World, new DummySlot(s));
            var contents = meal.GetNonEmptyContents(sapi.World, s);
            return contents != null && contents.Any(c => c?.Collectible?.Code?.Path == "rot");
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Washes out a bowl, crock or pot of rotten food, as dropping it in water does: a bowl or pot becomes its empty
    /// self, a crock is emptied. The rot is added to rot. Null when the block has no empty version.
    /// </summary>
    ItemStack? Washed(ItemStack s, List<ItemStack> rot)
    {
        if (s.Block is not IBlockMealContainer meal) return null;
        foreach (var content in meal.GetNonEmptyContents(sapi.World, s) ?? Array.Empty<ItemStack>())
        {
            if (content?.Collectible?.Code?.Path == "rot" && content.StackSize > 0) rot.Add(content.Clone());
        }
        if (s.Block is BlockCrock)
        {
            var crock = s.Clone();
            foreach (var key in new[] { "sealed", "recipeCode", "quantityServings", "contents" }) crock.Attributes.RemoveAttribute(key);
            return crock;
        }
        string? emptyCode = s.Block.Attributes?["eatenBlock"].AsString() ?? s.Block.Attributes?["emptiedBlockCode"].AsString();
        var empty = emptyCode == null ? null : sapi.World.GetBlock(new AssetLocation(emptyCode));
        return empty == null ? null : new ItemStack(empty, s.StackSize);
    }

    /// <summary>Washes every rotten bowl, crock and pot the settler carries, in its inventory and food slots. Returns how many.</summary>
    public int WashCarried(Colony colony, CitizenRecord citizen, Vec3d at)
    {
        var rot = new List<ItemStack>();
        int washed = 0;
        var pack = PackItems(citizen);
        for (int i = 0; i < pack.Count; i++)
        {
            if (!IsRottenMeal(pack[i], update: true) || Washed(pack[i], rot) is not { } clean) continue;
            pack[i] = clean;
            washed++;
        }
        if (washed > 0) SavePack(colony, citizen, pack);
        var lunch = LunchItems(citizen);
        int washedLunch = 0;
        for (int i = 0; i < lunch.Count; i++)
        {
            if (!IsRottenMeal(lunch[i], update: true) || Washed(lunch[i], rot) is not { } clean) continue;
            lunch[i] = clean;
            washedLunch++;
        }
        if (washedLunch > 0) SaveLunch(colony, citizen, lunch);
        washed += washedLunch;
        if (washed == 0) return 0;
        // The rot is kept for compost: carried along and dropped off with the rest, or left by the water.
        foreach (var r in rot)
        {
            AddToPack(colony, citizen, r);
            if (r.StackSize > 0) sapi.World.SpawnItemEntity(r, at);
        }
        sapi.World.PlaySoundAt(new AssetLocation("sounds/environment/smallsplash"), at.X, at.Y, at.Z, null);
        return washed;
    }

    /// <summary>True when the settler carries a rotten bowl, crock or pot to wash.</summary>
    public bool CarriesRotten(CitizenRecord citizen) =>
        PackItems(citizen).Any(s => IsRottenMeal(s, update: true)) || LunchItems(citizen).Any(s => IsRottenMeal(s, update: true));

    /// <summary>Water to wash in: the surface water nearest the spot, within WashWaterRadius.</summary>
    public BlockPos? WashWater(Colony colony, BlockPos near) => FindWaterNear(colony, near, Math.Max(2, Config.WashWaterRadius));

    // ---- The cook's crocks --------------------------------------------------------------------

    /// <summary>
    /// Pours the cooked pots the cook carries into the crocks it carries: empty ones, or ones with the same meal
    /// and room. An emptied pot becomes a clean pot again. Returns how many servings went into crocks.
    /// </summary>
    public float FillCrocks(Colony colony, CitizenRecord cook)
    {
        var items = PackItems(cook);
        float poured = 0;
        for (int p = 0; p < items.Count; p++)
        {
            if (items[p].StackSize != 1 || !IsCookedPot(items[p]) || items[p].Block is not BlockCookedContainerBase pot) continue;
            var potSlot = new DummySlot(items[p]);
            for (int c = 0; c < items.Count && potSlot.Itemstack?.Block is BlockCookedContainer; c++)
            {
                if (c == p || items[c].StackSize != 1 || !CrockTakes(items[c], potSlot.Itemstack)) continue;
                float before = pot.GetQuantityServings(sapi.World, potSlot.Itemstack);
                var crockSlot = new DummySlot(items[c]);
                if (!pot.ServeIntoStack(crockSlot, potSlot, sapi.World) || crockSlot.Itemstack == null) continue;
                items[c] = crockSlot.Itemstack;
                poured += before - (potSlot.Itemstack?.Block is IBlockMealContainer left ? left.GetQuantityServings(sapi.World, potSlot.Itemstack) : 0);
            }
            if (potSlot.Itemstack != null) items[p] = potSlot.Itemstack;
        }
        if (poured > 0) SavePack(colony, cook, items);
        return poured;
    }

    /// <summary>True when the crock can take some of the pot's meal: it is empty, or holds the same meal and has room.</summary>
    bool CrockTakes(ItemStack crock, ItemStack pot)
    {
        if (crock.Block is not BlockCrock block) return false;
        if (IsEmptyCrock(crock)) return true;
        if (IsRottenMeal(crock)) return false;
        float capacity = block.Attributes?["servingCapacity"].AsFloat(4) ?? 4;
        return block.GetRecipeCode(sapi.World, crock) == (pot.Block as IBlockMealContainer)?.GetRecipeCode(sapi.World, pot)
            && block.GetQuantityServings(sapi.World, crock) < capacity;
    }

    /// <summary>A crock the cook could pour into: one it carries or one in the kitchen.</summary>
    public bool CrockAtHand(Colony colony, Building hut, CitizenRecord cook) =>
        PackItems(cook).Any(IsEmptyCrock) || KitchenStores(colony, hut).Any(c => c.Inventory.Any(s => !s.Empty && IsEmptyCrock(s.Itemstack)));

    // ---- Bowls --------------------------------------------------------------------------------

    /// <summary>True for a settler who should carry a bowl and has none.</summary>
    public bool WantsBowl(CitizenRecord citizen) =>
        Config.CarryBowl && Config.CarryFood && citizen.Alive && !citizen.Visitor && !citizen.IsChild && !LunchItems(citizen).Any(IsBowl);

    /// <summary>A chest with an empty bowl for a settler without one: in its home, its hut, the warehouse or a Cook's Hut.</summary>
    public BlockEntityContainer? BowlChest(Colony colony, CitizenRecord citizen)
    {
        if (!WantsBowl(citizen)) return null;
        var lunch = LunchItems(citizen);
        if (lunch.Count >= LunchSlots) return null;
        return BowlSources(colony, citizen).FirstOrDefault(c => c.Inventory.Any(s => !s.Empty && IsEmptyBowl(s.Itemstack)));
    }

    IEnumerable<BlockEntityContainer> BowlSources(Colony colony, CitizenRecord citizen)
    {
        IEnumerable<BlockEntityContainer> chests = Enumerable.Empty<BlockEntityContainer>();
        if (HomeOf(colony, citizen) is { } home) chests = chests.Concat(HomeChests(home));
        if (citizen.WorkplacePos != null && GetBuilding(colony, citizen.WorkplacePos) is { } hut) chests = chests.Concat(HutStorage(colony, hut));
        chests = chests.Concat(WarehouseContainers(colony));
        foreach (var cookHut in colony.Buildings.Where(b => b.Type == BuildingTypes.Cook && Staffable(b))) chests = chests.Concat(KitchenStores(colony, cookHut));
        return chests.Distinct();
    }

    /// <summary>Takes one empty bowl from the chest into the settler's food slots.</summary>
    public bool TakeBowl(Colony colony, CitizenRecord citizen, BlockEntityContainer chest)
    {
        if (!WantsBowl(citizen)) return false;
        var lunch = LunchItems(citizen);
        if (lunch.Count >= LunchSlots) return false;
        var bowl = TakeItems(new[] { chest }, IsEmptyBowl, 1);
        if (bowl == null) return false;
        lunch.Add(bowl);
        SaveLunch(colony, citizen, lunch);
        return true;
    }

    /// <summary>
    /// After a meal at a crock, pot or placed meal: fills the settler's empty bowl with one serving of it to take along.
    /// True when it did.
    /// </summary>
    bool FillBowl(Colony colony, CitizenRecord citizen, BlockEntityContainer container, List<ItemStack> lunch)
    {
        if (!Config.CarryBowl) return false;
        int at = lunch.FindIndex(IsEmptyBowl);
        if (at < 0) return false;
        var bowl = lunch[at];
        if (bowl.StackSize > 1 && lunch.Count >= LunchSlots) return false;
        var mealBlock = bowl.Block.Attributes?["mealBlockCode"].AsString() is { } code ? sapi.World.GetBlock(new AssetLocation(code)) : null;
        if (mealBlock is not IBlockMealContainer bowlMeal) return false;

        string? recipeCode;
        ItemStack[] contents;
        if (container is IBlockEntityMealContainer placed)
        {
            if (placed.QuantityServings < 1 || MealMeshCache.ContentsRotten(placed.inventory)) return false;
            recipeCode = placed.RecipeCode;
            contents = placed.inventory.Where(s => !s.Empty).Select(s => s.Itemstack!.Clone()).ToArray();
            if (contents.Length == 0 || !TakePlacedServing(container, placed)) return false;
        }
        else
        {
            var slot = container.Inventory.FirstOrDefault(s => !s.Empty && s.Itemstack.Block is IBlockMealContainer m and not BlockMeal
                && m.GetQuantityServings(sapi.World, s.Itemstack) >= 1 && !IsRottenMeal(s.Itemstack));
            if (slot == null) return false;
            var meal = (IBlockMealContainer)slot.Itemstack.Block;
            recipeCode = meal.GetRecipeCode(sapi.World, slot.Itemstack);
            contents = (meal.GetNonEmptyContents(sapi.World, slot.Itemstack) ?? Array.Empty<ItemStack>()).Select(s => s.Clone()).ToArray();
            if (contents.Length == 0 || !TakeServing(container, slot)) return false;
            slot.MarkDirty();
        }

        var filled = new ItemStack((Block)bowlMeal);
        bowlMeal.SetContents(recipeCode, filled, contents, 1);
        if (bowl.StackSize > 1)
        {
            bowl.StackSize--;
            lunch.Add(filled);
        }
        else lunch[at] = filled;
        container.MarkDirty(true);
        return true;
    }

    /// <summary>The empty bowl left when the meal in a bowl is eaten.</summary>
    ItemStack? EatenBowl(ItemStack bowlMeal)
    {
        string? code = bowlMeal.Block?.Attributes?["eatenBlock"].AsString();
        var empty = code == null ? null : sapi.World.GetBlock(new AssetLocation(code));
        return empty == null ? null : new ItemStack(empty);
    }

    // ---- Orders for the Potter's Hut ----------------------------------------------------------

    sealed class PotteryOrderBook
    {
        public long At;
        public int Crocks;
        public int Bowls;
    }

    readonly ConditionalWeakTable<Colony, PotteryOrderBook> potteryOrders = new();

    /// <summary>
    /// How many of a kind of pottery the colony is short of for its kitchens and settlers, on top of what the Pottery
    /// tab keeps: empty crocks for the cooks (CookSpareCrocks a Cook's Hut) and bowls for settlers without one.
    /// </summary>
    public int PotteryOrdered(Colony colony, PotteryKind kind)
    {
        bool crock = kind.Sample.Block is BlockCrock;
        bool bowl = !crock && IsEmptyBowl(kind.Sample);
        if (!crock && !bowl) return 0;
        // Only the first kind of each gets the order.
        if (PotteryKinds().FirstOrDefault(k => crock ? k.Sample.Block is BlockCrock : k.Sample.Block is not BlockCrock && IsEmptyBowl(k.Sample)) != kind) return 0;

        long now = sapi.World.ElapsedMilliseconds;
        if (!potteryOrders.TryGetValue(colony, out var book) || now - book.At > 5000)
        {
            book = new PotteryOrderBook { At = now };
            var stores = WarehouseContainers(colony)
                .Concat(colony.Buildings.Where(b => b.Type == BuildingTypes.Cook && Staffable(b)).SelectMany(b => KitchenStores(colony, b)))
                .Distinct().SelectMany(c => c.Inventory).Where(s => !s.Empty).Select(s => s.Itemstack!).ToList();
            var cooks = colony.Citizens.Where(c => c.Alive && c.JobCode == Jobs.Cook).ToList();
            int emptyCrocks = stores.Count(IsEmptyCrock) + cooks.Sum(c => PackItems(c).Count(IsEmptyCrock));
            int kitchens = colony.Buildings.Count(b => b.Type == BuildingTypes.Cook && Staffable(b) && WorkerAt(colony, b, Jobs.Cook) != null);
            book.Crocks = Math.Max(0, kitchens * Config.CookSpareCrocks - emptyCrocks);
            int emptyBowls = stores.Where(IsEmptyBowl).Sum(s => s.StackSize);
            book.Bowls = Config.CarryBowl ? Math.Max(0, colony.Citizens.Count(WantsBowl) - emptyBowls) : 0;
            potteryOrders.Remove(colony);
            potteryOrders.Add(colony, book);
        }
        return crock ? book.Crocks : book.Bowls;
    }

    /// <summary>Pieces of a kind the potter has made but not fired yet: shaped, in its kiln or in its hands.</summary>
    int PotteryUnfired(Building hut, PotteryKind kind, CitizenRecord? potter, Colony colony)
    {
        ItemMatch raw = kind.IsRaw;
        int count = CountItems(HutStorage(colony, hut), raw);
        if (potter != null) count += PackCount(potter, raw);
        count += InKilns(hut, raw);
        if (FormingRecipe(hut) is { } forming && kind.Recipes.Contains(forming)) count += forming.Output!.ResolvedItemstack!.StackSize;
        return count;
    }

    /// <summary>True when the potter should make one more of this kind for an order (crocks for a cook, bowls for settlers).</summary>
    bool PotteryOrderOpen(Colony colony, Building hut, PotteryKind kind, CitizenRecord? potter) =>
        PotteryOrdered(colony, kind) is > 0 and var ordered && ordered > PotteryUnfired(hut, kind, potter, colony);
}
