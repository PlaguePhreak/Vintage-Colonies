using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.GameContent;
using ItemMatch = System.Func<Vintagestory.API.Common.ItemStack, bool>;

namespace CreateAColony.Colonies;

/// <summary>
/// Keeping food fresh, by the game's own spoilage rules. The game spoils food more slowly where it is cool: in a
/// cellar (a small closed room with soil or stone walls), underground, or in winter. Settlers use that:
/// - They eat, pack and cook with the food that will spoil soonest first (SpoilPull, and TakeItems takes the
///   oldest first).
/// - Food that can spoil is put in the coolest storage there is room in, and couriers (and the cook, when it has
///   nothing to cook) move it from warm storage down to a cellar: from the warehouse to a cooler warehouse chest
///   or a Cook's Hut cellar, inside a Cook's Hut to its basement, and inside a home to its basement. Kitchen shelves are filled but never emptied
///   this way, so food is not carried back and forth.
/// </summary>
public partial class ColonyManager
{
    /// <summary>When a search found no food to move somewhere cooler, per colony or hut, so it does not run every few seconds.</summary>
    readonly Dictionary<string, long> noCoolingUntil = new();
    const long CoolingRetryMs = 30_000;
    /// <summary>Storage at most this much cooler (as a share of normal spoil speed) is not worth the walk.</summary>
    const float CoolingMinGain = 0.05f;

    /// <summary>True for something that rots: food, or a pot, bowl or pie with food in it.</summary>
    public bool CanSpoil(ItemStack? stack)
    {
        if (stack == null || stack.Collectible.Code.Path == "rot") return false;
        if (HasPerish(stack)) return true;
        return stack.Block is BlockContainer container && container.GetNonEmptyContents(sapi.World, stack).Any(HasPerish);
    }

    bool HasPerish(ItemStack stack) =>
        stack.Collectible.GetTransitionableProperties(sapi.World, stack, null)?.Any(p => p.Type == EnumTransitionType.Perish) == true;

    /// <summary>How fast food spoils in this container compared to normal (1): about 0.25 in a good cellar, more in a warm room.</summary>
    public float PerishRate(BlockEntityContainer container, ItemStack stack)
    {
        try
        {
            return container.Inventory.GetTransitionSpeedMul(EnumTransitionType.Perish, stack) * GlobalConstants.PerishSpeedModifier;
        }
        catch (Exception)
        {
            return 1;
        }
    }

    /// <summary>
    /// In-game hours until the food in the slot is rot, at the speed it spoils where it is now. For a pot, bowl or
    /// crock, its first ingredient to rot decides. double.MaxValue for anything that never spoils.
    /// </summary>
    public double HoursUntilSpoiled(ItemSlot slot)
    {
        var stack = slot.Itemstack;
        if (!CanSpoil(stack)) return double.MaxValue;
        var world = sapi.World;
        // A pie spoils as a whole; a meal by its ingredients.
        var parts = stack!.Block is BlockContainer container && stack.Block is not BlockPie && !HasPerish(stack)
            ? container.GetNonEmptyContents(world, stack)
            : new[] { stack };
        double left = double.MaxValue;
        foreach (var part in parts)
        {
            TransitionState? state;
            try
            {
                // On a copy, so reading it changes nothing in the chest.
                var copy = part.Clone();
                state = copy.Collectible.UpdateAndGetTransitionState(world, new DummySlot(copy), EnumTransitionType.Perish);
            }
            catch (Exception)
            {
                continue;
            }
            if (state == null) continue;
            left = Math.Min(left, Math.Max(0, state.FreshHours + state.TransitionHours - state.TransitionedHours));
        }
        if (left == double.MaxValue) return left;
        float rate = slot.Inventory == null ? GlobalConstants.PerishSpeedModifier : SafeRate(slot.Inventory, stack);
        return rate <= 0.001f ? double.MaxValue : left / rate;
    }

    static float SafeRate(InventoryBase inventory, ItemStack stack)
    {
        try
        {
            return inventory.GetTransitionSpeedMul(EnumTransitionType.Perish, stack) * GlobalConstants.PerishSpeedModifier;
        }
        catch (Exception)
        {
            return 1;
        }
    }

    /// <summary>How much more a settler wants food that will spoil soon: up to 1 + EatSpoilingFirst, and 1 for food that keeps.</summary>
    public float SpoilPull(double hoursLeft)
    {
        if (Config.EatSpoilingFirst <= 0 || hoursLeft == double.MaxValue) return 1;
        return 1 + Config.EatSpoilingFirst * (float)Math.Exp(-hoursLeft / Math.Max(1, Config.SpoilingSoonHours));
    }

    public float SpoilPull(ItemSlot slot) => SpoilPull(HoursUntilSpoiled(slot));

    /// <summary>The container (of those given) holding the matching item that will spoil soonest, else the first holding any.</summary>
    public BlockEntityContainer? SoonestToSpoil(IEnumerable<BlockEntityContainer> containers, ItemMatch match)
    {
        BlockEntityContainer? first = null, best = null;
        double bestHours = double.MaxValue;
        foreach (var container in containers)
        {
            foreach (var slot in container.Inventory)
            {
                if (slot.Empty || !match(slot.Itemstack)) continue;
                first ??= container;
                double hours = HoursUntilSpoiled(slot);
                if (hours >= bestHours) continue;
                best = container;
                bestHours = hours;
            }
        }
        return best ?? first;
    }

    /// <summary>The containers ordered coolest first for this stack when it can spoil (in steps, so near-equal ones keep their order).</summary>
    public List<BlockEntityContainer> CoolestFirst(IEnumerable<BlockEntityContainer> containers, ItemStack stack)
    {
        var list = containers.ToList();
        if (!Config.KeepFoodCool || list.Count < 2 || !CanSpoil(stack)) return list;
        return list.OrderBy(c => Math.Round(PerishRate(c, stack) / CoolingMinGain)).ToList();
    }

    /// <summary>How many of the stack the container has room for.</summary>
    int RoomFor(BlockEntityContainer container, ItemStack stack)
    {
        if (IsFoodShelf(container)) return ShelfRoom(container, stack);
        var probe = new DummySlot(stack.Clone());
        int room = 0;
        foreach (var slot in container.Inventory)
        {
            try
            {
                if (slot.Empty) room += slot.CanHold(probe) ? Math.Min(slot.MaxSlotStackSize, stack.Collectible.MaxStackSize) : 0;
                else if (slot.Itemstack.Equals(sapi.World, stack, GlobalConstants.IgnoredStackAttributes)) room += Math.Max(0, slot.GetRemainingSlotSpace(stack));
            }
            catch (Exception)
            {
                // A modded slot that cannot say: count it as full.
            }
        }
        return room;
    }

    /// <summary>Food to carry somewhere cooler: from which container, what, how much and where to.</summary>
    public sealed record CoolingMove(BlockEntityContainer From, BlockEntityContainer To, ItemMatch Match, int Amount);

    /// <summary>
    /// Food that can spoil, in warm storage, that a clearly cooler store has room for. With a hut, only from that
    /// Cook's Hut's storage to its storage and kitchen shelves (for its cook); without, across the warehouse, the Cook's Huts and homes (for
    /// couriers): warehouse food may go to the warehouse or a Cook's Hut, food in a Cook's Hut or home stays there.
    /// Food <paramref name="shelvesOnly"/> says yes to only goes on Food Shelves shelves.
    /// </summary>
    public CoolingMove? FindCoolingMove(Colony colony, Building? hut = null, ItemMatch? shelvesOnly = null)
    {
        if (!Config.KeepFoodCool) return null;
        string key = hut == null ? colony.ColonyId : colony.ColonyId + "/" + hut.BuildingId;
        long now = sapi.World.ElapsedMilliseconds;
        if (noCoolingUntil.TryGetValue(key, out long until) && now < until) return null;

        // Sources, each with the stores its food may go to.
        var groups = new List<(List<BlockEntityContainer> From, List<BlockEntityContainer> To)>();
        if (hut != null)
        {
            groups.Add((HutStorage(colony, hut).ToList(), KitchenStores(colony, hut).ToList()));
        }
        else
        {
            var kitchens = colony.Buildings.Where(b => b.Type == BuildingTypes.Cook && b.Level >= 1).ToList();
            var warehouse = WarehouseContainers(colony).ToList();
            var all = warehouse.Concat(kitchens.SelectMany(k => KitchenStores(colony, k))).ToList();
            groups.Add((warehouse, all));
            foreach (var kitchen in kitchens) groups.Add((HutStorage(colony, kitchen).ToList(), KitchenStores(colony, kitchen).ToList()));
            // A home's food stays in that home, in its cellar if it has one.
            foreach (var home in colony.Buildings.Where(b => IsHome(b.Type) && b.Level >= 1))
            {
                var chests = HomeChests(home);
                groups.Add((chests, chests));
            }
        }

        // Spoil speeds per container and food group, worked out once per search.
        var rates = new Dictionary<(BlockEntityContainer, int), float>();
        float Rate(BlockEntityContainer c, ItemStack s)
        {
            int group = (int?)s.Collectible.NutritionProps?.FoodCategory ?? -1;
            if (!rates.TryGetValue((c, group), out float r)) rates[(c, group)] = r = PerishRate(c, s);
            return r;
        }

        foreach (var (from, to) in groups)
        {
            if (to.Count < 2) continue;
            foreach (var source in from)
            {
                foreach (var slot in source.Inventory)
                {
                    if (slot.Empty || !CanSpoil(slot.Itemstack)) continue;
                    var stack = slot.Itemstack;
                    float here = Rate(source, stack);
                    bool onlyShelves = shelvesOnly?.Invoke(stack) == true;
                    var cooler = to
                        .Where(c => c != source && !c.Pos.Equals(source.Pos) && (!onlyShelves || IsFoodShelf(c)))
                        .Select(c => (Store: c, Rate: Rate(c, stack)))
                        .Where(t => t.Rate <= here * Config.CoolStoreMaxRateShare && here - t.Rate >= CoolingMinGain)
                        .OrderBy(t => t.Rate)
                        .Select(t => (t.Store, Room: RoomFor(t.Store, stack)))
                        .FirstOrDefault(t => t.Room > 0);
                    if (cooler.Store == null) continue;
                    var kind = stack.Collectible;
                    // No more than fits, so nothing is left over to carry back.
                    return new CoolingMove(source, cooler.Store, s => s.Collectible == kind && CanSpoil(s), Math.Min(stack.StackSize, cooler.Room));
                }
            }
        }
        noCoolingUntil[key] = now + CoolingRetryMs;
        return null;
    }

    /// <summary>A line for the Cook's Hut window: how fast food spoils in its coolest storage.</summary>
    public string? CoolStoreInfo(Colony colony, Building hut)
    {
        if (!Config.KeepFoodCool) return null;
        var sample = KitchenKinds().FirstOrDefault()?.Sample;
        var stores = KitchenStores(colony, hut).ToList();
        if (sample == null || stores.Count == 0) return null;
        float rate = stores.Min(c => PerishRate(c, sample));
        return Lang.Get("createacolony:hut-cool-store", (int)Math.Round(rate * 100));
    }
}
