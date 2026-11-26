using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.GameContent;
using ItemMatch = System.Func<Vintagestory.API.Common.ItemStack, bool>;

namespace CreateAColony.Colonies;

/// <summary>
/// A settler's own inventory, kept on its record so nothing is lost on a save or a trip cut short.
/// Like a player it has ten slots, and up to four bags (backpacks, baskets...) add their slots on top.
/// </summary>
public partial class ColonyManager
{
    /// <summary>The player's hotbar size.</summary>
    public const int PackBaseSlots = 10;
    /// <summary>The player's number of bag slots.</summary>
    public const int MaxBags = 4;

    /// <summary>Slots a bag adds, or 0 if the item is not a bag.</summary>
    public static int BagSlots(ItemStack stack) => stack.ItemAttributes?["backpack"]?["quantitySlots"]?.AsInt(0) ?? 0;

    List<ItemStack> Resolve(List<byte[]> data)
    {
        var stacks = new List<ItemStack>();
        foreach (var bytes in data)
        {
            var stack = new ItemStack(bytes);
            if (stack.ResolveBlockOrItem(sapi.World) && stack.StackSize > 0) stacks.Add(stack);
        }
        return stacks;
    }

    public List<ItemStack> PackItems(CitizenRecord citizen)
    {
        // Settlers saved before they had an inventory carried a single stack.
        if (citizen.CarriedStack != null)
        {
            citizen.Pack.Add(citizen.CarriedStack);
            citizen.CarriedStack = null;
        }
        return Resolve(citizen.Pack);
    }

    public List<ItemStack> PackBags(CitizenRecord citizen) => Resolve(citizen.Bags);

    /// <summary>A reed hand basket (basket, basket-*), never the reed chest or a basket trap.</summary>
    public static bool IsBasket(ItemStack stack) => stack.Collectible.Code is { Domain: "game" } code
        && (code.Path == "basket" || code.Path.StartsWith("basket-")) && BagSlots(stack) > 0;

    /// <summary>The slots a bag adds to a settler: a basket adds config BasketPackSlots (Aaron, 0.202.0), other bags what the game says.</summary>
    public int SettlerBagSlots(ItemStack bag) => IsBasket(bag) && Config.BasketPackSlots > 0 ? Config.BasketPackSlots : BagSlots(bag);

    public int PackSlots(CitizenRecord citizen) => (int)Math.Round((PackBaseSlots + PackBags(citizen).Sum(SettlerBagSlots)) * TraitMult(citizen, "packSlots")) // Pack Mule
        + (ColonyOf(citizen) is { } colony ? (int)Math.Round(SkillPerk(colony, citizen, "packSlots")) : 0); // Hauling and Strength perks

    public bool PackEmpty(CitizenRecord citizen) => citizen.Pack.Count == 0 && citizen.CarriedStack == null;

    /// <summary>True when every slot holds something, so the next new kind of item has nowhere to go.</summary>
    public bool PackFull(CitizenRecord citizen) => PackItems(citizen).Count >= PackSlots(citizen);

    /// <summary>How many matching items the settler carries, in its inventory and on its tool bar.</summary>
    public int PackCount(CitizenRecord citizen, ItemMatch match) => CarriedItems(citizen).Where(match).Sum(s => s.StackSize);

    /// <summary>The inventory and the tool bar together, for jobs looking for a tool or weapon they carry.</summary>
    public List<ItemStack> CarriedItems(CitizenRecord citizen) => PackItems(citizen).Concat(ToolBarItems(citizen).OfType<ItemStack>()).ToList();

    /// <summary>Stores the inventory back after stacks were changed in place; empty stacks are dropped.</summary>
    public void SavePack(Colony colony, CitizenRecord citizen, IEnumerable<ItemStack> stacks)
    {
        citizen.Pack = stacks.Where(s => s.StackSize > 0).Select(s => s.ToBytes()).ToList();
        MarkDirty(colony);
    }

    /// <summary>
    /// Puts as much of the stack as fits into the settler's inventory. A tool, weapon or shield goes to its
    /// free tool bar slot first, unless toolBar is false (a courier's load). The stack keeps what did not fit.
    /// </summary>
    public int AddToPack(Colony colony, CitizenRecord citizen, ItemStack stack, bool toolBar = true)
    {
        int moved = 0;
        if (toolBar && stack.StackSize > 0 && PutOnToolBar(colony, citizen, stack))
        {
            moved = 1;
            if (stack.StackSize <= 0) return moved;
        }
        var items = PackItems(citizen);
        int slots = PackSlots(citizen);
        foreach (var slot in items)
        {
            if (stack.StackSize <= 0) break;
            if (!slot.Equals(sapi.World, stack, GlobalConstants.IgnoredStackAttributes)) continue;
            int n = Math.Min(stack.StackSize, slot.Collectible.MaxStackSize - slot.StackSize);
            if (n <= 0) continue;
            slot.StackSize += n;
            stack.StackSize -= n;
            moved += n;
        }
        while (stack.StackSize > 0 && items.Count < slots)
        {
            var part = stack.Clone();
            part.StackSize = Math.Min(stack.StackSize, Math.Max(1, stack.Collectible.MaxStackSize));
            stack.StackSize -= part.StackSize;
            moved += part.StackSize;
            items.Add(part);
        }
        if (moved > 0) SavePack(colony, citizen, items);
        return moved;
    }

    /// <summary>
    /// Takes up to max items of one kind (the first match decides the kind) out of the settler's inventory,
    /// or a matching tool off its tool bar when the inventory has none.
    /// </summary>
    public ItemStack? TakeFromPack(Colony colony, CitizenRecord citizen, ItemMatch match, int max)
    {
        if (!PackItems(citizen).Any(match) && max > 0 && TakeFromToolBar(colony, citizen, match) is { } tool) return tool;
        var items = PackItems(citizen);
        ItemStack? taken = null;
        foreach (var slot in items)
        {
            if (!match(slot)) continue;
            if (taken != null && !taken.Equals(sapi.World, slot, GlobalConstants.IgnoredStackAttributes)) continue;
            int n = Math.Min(max - (taken?.StackSize ?? 0), slot.StackSize);
            if (n <= 0) break;
            if (taken == null)
            {
                taken = slot.Clone();
                taken.StackSize = n;
            }
            else
            {
                taken.StackSize += n;
            }
            slot.StackSize -= n;
        }
        if (taken != null) SavePack(colony, citizen, items);
        return taken;
    }

    /// <summary>Empties the settler's inventory into the containers, as far as they have room. Returns true if it is empty now.</summary>
    public bool UnloadPack(Colony colony, CitizenRecord citizen, IEnumerable<BlockEntityContainer> containers, ItemMatch? which = null)
    {
        var items = PackItems(citizen);
        var targets = containers.ToList();
        foreach (var stack in items)
        {
            if (which != null && !which(stack)) continue;
            // Food that can spoil goes in the coolest of them first, such as a cellar chest (ColonyManager.Freshness.cs).
            foreach (var container in CoolestFirst(targets, stack))
            {
                if (stack.StackSize <= 0) break;
                PutItems(container, stack);
            }
        }
        SavePack(colony, citizen, items);
        return PackEmpty(citizen);
    }

    /// <summary>Everything the settler has, for dropping where it died. Clears the inventory, bags, tool bar and packed food.</summary>
    public List<ItemStack> EmptyPackAndBags(Colony colony, CitizenRecord citizen)
    {
        var all = PackItems(citizen).Concat(PackBags(citizen)).Concat(ToolBarItems(citizen).OfType<ItemStack>()).Concat(LunchItems(citizen)).ToList();
        if (FirstAidOf(citizen) is { } aid) all.Add(aid);
        citizen.FirstAid = null;
        citizen.Pack.Clear();
        citizen.Bags.Clear();
        citizen.ToolBar.Clear();
        citizen.Lunch.Clear();
        MarkDirty(colony);
        return all;
    }

    /// <summary>Gives the settler a bag. False if it is not a bag or all bag slots are taken.</summary>
    public bool AddBag(Colony colony, CitizenRecord citizen, ItemStack bag)
    {
        if (BagSlots(bag) <= 0 || citizen.Bags.Count >= MaxBags) return false;
        var one = bag.Clone();
        one.StackSize = 1;
        citizen.Bags.Add(one.ToBytes());
        MarkDirty(colony);
        return true;
    }

    /// <summary>Takes back the last bag. Items that no longer fit come out with it.</summary>
    public (ItemStack Bag, List<ItemStack> Overflow)? RemoveBag(Colony colony, CitizenRecord citizen)
    {
        var bags = PackBags(citizen);
        if (bags.Count == 0) return null;
        var bag = bags[^1];
        citizen.Bags = bags.Take(bags.Count - 1).Select(b => b.ToBytes()).ToList();

        var items = PackItems(citizen);
        int slots = PackSlots(citizen);
        var overflow = items.Skip(slots).ToList();
        SavePack(colony, citizen, items.Take(slots));
        return (bag, overflow);
    }
}
