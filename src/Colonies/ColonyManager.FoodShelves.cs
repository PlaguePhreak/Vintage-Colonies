using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using ItemMatch = System.Func<Vintagestory.API.Common.ItemStack, bool>;

namespace CreateAColony.Colonies;

/// <summary>
/// Food Shelves (SONZ-INA) as kitchen storage. Its shelves, baskets and cabinets are containers whose slots
/// only take the foods that shelf is made for (bread on a bread shelf, pies on a pie shelf), in rows and
/// segments that hold one kind each. Settlers put things on them the way a player does, segment by segment,
/// so the shelf looks as if a player had stocked it. The shelves in a Cook's Hut are its kitchen shelves: the
/// cook puts what it cooks on them and takes ingredients from them, and couriers fill them with food from the
/// warehouse. Settlers eat from any shelf, as from any food store. The mod is optional and only known by name.
/// </summary>
public partial class ColonyManager
{
    /// <summary>How the mod's shelves are laid out, read from its base block entity class once per type.</summary>
    sealed class ShelfLayout
    {
        public required PropertyInfo ItemsPerSegment;
        public required PropertyInfo SlotCount;
        public required PropertyInfo AdditionalSlots;
        public required MethodInfo SegmentLimit;
        /// <summary>The mod's own check that two items may share a segment; null if it was not found.</summary>
        public MethodInfo? CanShareSegment;
    }

    static readonly Dictionary<Type, ShelfLayout?> shelfLayouts = new();
    static FieldInfo? bulkField;
    /// <summary>Kitchen shelf positions per Cook's Hut, looked up again after ShelfCacheMs.</summary>
    readonly Dictionary<int, (long Until, List<BlockPos> Shelves)> kitchenShelfCache = new();
    const long ShelfCacheMs = 10_000;
    /// <summary>When a courier last found no kitchen shelf to fill, per colony, so the search does not run every few seconds.</summary>
    readonly Dictionary<string, long> noShelfRestockUntil = new();
    const long ShelfRestockRetryMs = 30_000;

    static ShelfLayout? LayoutOf(Type type)
    {
        lock (shelfLayouts)
        {
            if (shelfLayouts.TryGetValue(type, out var known)) return known;
            ShelfLayout? layout = null;
            var baseType = type;
            while (baseType != null && !(baseType.Name == "BEBaseFSContainer" && baseType.Namespace == "FoodShelves")) baseType = baseType.BaseType;
            if (baseType != null)
            {
                const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var per = baseType.GetProperty("ItemsPerSegment", all);
                var count = baseType.GetProperty("SlotCount", all);
                var extra = baseType.GetProperty("AdditionalSlots", all);
                var limit = baseType.GetMethod("GetSegmentLimit", all, null, new[] { typeof(ItemStack) }, null);
                if (per != null && count != null && extra != null && limit != null)
                {
                    layout = new ShelfLayout
                    {
                        ItemsPerSegment = per,
                        SlotCount = count,
                        AdditionalSlots = extra,
                        SegmentLimit = limit,
                        CanShareSegment = baseType.Assembly.GetType("FoodShelves.CheckExtensions")?
                            .GetMethod("CanInsertIntoSegment", BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(ItemStack), typeof(ItemStack) }, null)
                    };
                }
            }
            shelfLayouts[type] = layout;
            return layout;
        }
    }

    /// <summary>True for a Food Shelves shelf, basket or cabinet that holds items.</summary>
    public static bool IsFoodShelf(BlockEntity? be) => be is BlockEntityContainer && LayoutOf(be.GetType()) != null;

    static bool IsBulk(ItemSlot slot)
    {
        if (slot.GetType().Name != "ItemSlotFSUniversal") return false;
        bulkField ??= slot.GetType().GetField("isBulk", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return bulkField?.GetValue(slot) is true;
    }

    /// <summary>
    /// Puts as much of the stack on the shelf as it takes, segment by segment as a player would, and returns
    /// how many items went (or, with <paramref name="simulate"/>, would go) on it. The stack shrinks by that much
    /// unless simulating.
    /// </summary>
    int Shelve(BlockEntityContainer shelf, ItemStack stack, bool simulate = false)
    {
        if (stack.StackSize <= 0 || LayoutOf(shelf.GetType()) is not { } layout) return 0;
        int per, count;
        try
        {
            per = (int)layout.ItemsPerSegment.GetValue(shelf)!;
            count = (int)layout.SlotCount.GetValue(shelf)! - (int)layout.AdditionalSlots.GetValue(shelf)!;
        }
        catch (Exception)
        {
            return 0;
        }
        var inv = shelf.Inventory;
        count = Math.Min(count, inv.Count);
        if (per <= 0) return 0;

        var source = new DummySlot(simulate ? stack.Clone() : stack);
        int moved = 0;
        for (int start = 0; start + per <= count && !source.Empty; start += per)
        {
            if (!CanShareSegment(layout, inv[start].Itemstack, source.Itemstack)) continue;
            bool bulk = IsBulk(inv[start]);
            int limit = bulk ? per : SegmentLimit(layout, shelf, source.Itemstack, per);
            int filled = bulk ? 0 : Enumerable.Range(start, per).Count(i => !inv[i].Empty);
            for (int i = 0; i < per && !source.Empty; i++)
            {
                var slot = inv[start + i];
                if (!slot.Empty && !slot.Itemstack.Equals(sapi.World, source.Itemstack, GlobalConstants.IgnoredStackAttributes)) continue;
                if (!bulk && slot.Empty && filled >= limit) continue;
                if (!slot.CanHold(source)) continue;
                int room = Math.Min(slot.GetRemainingSlotSpace(source.Itemstack), (bulk ? slot.MaxSlotStackSize : 1) - slot.StackSize);
                if (room <= 0) continue;
                bool wasEmpty = slot.Empty;
                int n;
                if (simulate)
                {
                    n = Math.Min(room, source.StackSize);
                    source.Itemstack.StackSize -= n;
                    if (source.Itemstack.StackSize <= 0) source.Itemstack = null;
                }
                else
                {
                    n = source.TryPutInto(sapi.World, slot, Math.Min(room, source.StackSize));
                    if (n > 0) slot.MarkDirty();
                }
                if (n > 0 && wasEmpty && !bulk) filled++;
                moved += n;
            }
        }
        if (!simulate)
        {
            stack.StackSize = source.Empty ? 0 : source.StackSize;
            if (moved > 0) shelf.MarkDirty(true);
        }
        return moved;
    }

    /// <summary>How many of the stack the shelf would take now.</summary>
    public int ShelfRoom(BlockEntityContainer shelf, ItemStack stack) => Shelve(shelf, stack, simulate: true);

    static bool CanShareSegment(ShelfLayout layout, ItemStack? first, ItemStack? adding)
    {
        if (first == null) return true;
        if (adding == null) return false;
        if (layout.CanShareSegment == null) return first.Collectible == adding.Collectible;
        try
        {
            return layout.CanShareSegment.Invoke(null, new object?[] { first, adding }) is true;
        }
        catch (Exception)
        {
            return first.Collectible == adding.Collectible;
        }
    }

    static int SegmentLimit(ShelfLayout layout, BlockEntityContainer shelf, ItemStack? stack, int fallback)
    {
        try
        {
            return layout.SegmentLimit.Invoke(shelf, new object?[] { stack }) is int n ? n : fallback;
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    // ---- Kitchen shelves ------------------------------------------------------------------

    /// <summary>The Food Shelves shelves, baskets and cabinets inside a Cook's Hut, nearest the hut block first.</summary>
    public List<BlockEntityContainer> KitchenShelves(Colony colony, Building hut)
    {
        if (hut.Type != BuildingTypes.Cook) return new List<BlockEntityContainer>();
        long now = sapi.World.ElapsedMilliseconds;
        if (!kitchenShelfCache.TryGetValue(hut.BuildingId, out var known) || known.Until < now)
        {
            var (min, max) = BuildingBox(hut);
            var h = hut.HutPos;
            var found = FindBlockEntities<BlockEntityContainer>(new Cuboidi(min.X, min.Y, min.Z, max.X, max.Y, max.Z))
                .Where(IsFoodShelf)
                .OrderBy(c => c.Pos.DistanceSqTo(h.X, h.Y, h.Z))
                .Select(c => c.Pos.Copy())
                .ToList();
            known = (now + ShelfCacheMs, found);
            kitchenShelfCache[hut.BuildingId] = known;
        }
        var ba = sapi.World.BlockAccessor;
        return known.Shelves.Select(p => ba.GetBlockEntity(p) as BlockEntityContainer).Where(IsFoodShelf).Cast<BlockEntityContainer>().ToList();
    }

    /// <summary>Where a Cook's Hut keeps food: its storage chests, then its kitchen shelves.</summary>
    public IEnumerable<BlockEntityContainer> KitchenStores(Colony colony, Building hut) =>
        HutStorage(colony, hut).Cast<BlockEntityContainer>().Concat(KitchenShelves(colony, hut));

    /// <summary>
    /// Something the kitchen made that belongs on a shelf: a cooked food from the Kitchen tab, a meal in a pot
    /// or bowl, or a baked pie.
    /// </summary>
    public bool IsDish(ItemStack s)
    {
        if (s.Block is BlockPie pie) return pie.State != "raw";
        // A meal gone rotten is washed out, not shelved (ColonyManager.Crocks.cs).
        if (s.Block is IBlockMealContainer meal) return meal.GetQuantityServings(sapi.World, s) > 0 && !IsRottenMeal(s);
        return KitchenKinds().Any(k => k.IsMine(s)) || OvenKinds().Any(k => k.IsMine(s));
    }

    /// <summary>Food ready to eat that couriers bring to kitchen shelves: dishes and plain food, never raw food for the fire pit or rot.</summary>
    public bool IsShelfFood(ItemStack s)
    {
        if (s.Collectible.Code.Path == "rot") return false;
        if (IsDish(s)) return true;
        return s.Collectible.NutritionProps != null && !IsGrillable(s) && !IsBakeable(s) && !IsLiquid(s);
    }

    /// <summary>The kitchen shelf with room for something the settler carries that the match says yes to, the coolest first for food that can spoil.</summary>
    public BlockEntityContainer? ShelfFor(Colony colony, Building hut, CitizenRecord citizen, ItemMatch match, System.Func<BlockPos, bool>? skip = null)
    {
        var carried = PackItems(citizen).Where(match).ToList();
        if (carried.Count == 0) return null;
        var shelves = KitchenShelves(colony, hut).Where(shelf => skip == null || !skip(shelf.Pos)).ToList();
        foreach (var stack in carried)
        {
            var shelf = CoolestFirst(shelves, stack).FirstOrDefault(sh => ShelfRoom(sh, stack) > 0);
            if (shelf != null) return shelf;
        }
        return null;
    }

    /// <summary>Puts what the settler carries that the match says yes to on the shelf. Returns how many items went on it.</summary>
    public int ShelveFromPack(Colony colony, CitizenRecord citizen, BlockEntityContainer shelf, ItemMatch match)
    {
        var items = PackItems(citizen);
        int moved = 0;
        foreach (var stack in items.Where(match)) moved += Shelve(shelf, stack);
        if (moved > 0) SavePack(colony, citizen, items);
        return moved;
    }

    /// <summary>A courier trip that fills a kitchen shelf: where to pick the food up, what, how much and where it goes.</summary>
    public sealed record ShelfRestock(BlockEntityContainer Source, BlockEntityContainer Shelf, ItemMatch Match, int Amount);

    /// <summary>Food in the warehouse that a kitchen shelf in one of the colony's Cook's Huts has room for, or null.</summary>
    public ShelfRestock? FindShelfRestock(Colony colony)
    {
        long now = sapi.World.ElapsedMilliseconds;
        if (noShelfRestockUntil.TryGetValue(colony.ColonyId, out long until) && now < until) return null;
        var shelves = colony.Buildings.Where(b => b.Type == BuildingTypes.Cook && Staffable(b)).SelectMany(b => KitchenShelves(colony, b)).ToList();
        if (shelves.Count > 0)
        {
            var food = WarehouseContainers(colony)
                .SelectMany(c => c.Inventory.Where(s => !s.Empty && IsShelfFood(s.Itemstack)).Select(s => (Chest: c, Stack: s.Itemstack!)))
                .ToList();
            // Each food to the coolest shelf that takes it, so a cellar's shelves fill first (sorted once per food group).
            var byGroup = new Dictionary<(int, bool), List<BlockEntityContainer>>();
            foreach (var (chest, stack) in food)
            {
                var key = ((int?)stack.Collectible.NutritionProps?.FoodCategory ?? -1, CanSpoil(stack));
                if (!byGroup.TryGetValue(key, out var ordered)) byGroup[key] = ordered = CoolestFirst(shelves, stack);
                foreach (var shelf in ordered)
                {
                    int room = ShelfRoom(shelf, stack);
                    if (room <= 0) continue;
                    var kind = stack.Collectible;
                    return new ShelfRestock(chest, shelf, s => s.Collectible == kind && IsShelfFood(s), room);
                }
            }
        }
        noShelfRestockUntil[colony.ColonyId] = now + ShelfRestockRetryMs;
        return null;
    }
}
