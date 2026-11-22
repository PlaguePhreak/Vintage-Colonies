using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.GameContent;
using ItemMatch = System.Func<Vintagestory.API.Common.ItemStack, bool>;

namespace CreateAColony.Colonies;

/// <summary>
/// The Bakery (0.185.0). Its baker keeps the colony in bread: it grinds grain into flour at a quern by the Bakery (the
/// game's grinding: what the quern would make of it), mixes flour into dough as the game's crafting recipe does (water
/// comes from the Bakery's own water), and bakes the dough on the fire pit by the Bakery, the way the Cook does. While a
/// Bakery has its baker, the Cook leaves bread to it and only cooks meals. The bread goes to the warehouse, and the
/// Market Trader sells it to settlers with gears.
/// </summary>
public partial class ColonyManager
{
    /// <summary>The fire pit foods made from dough: the bread kinds.</summary>
    public List<KitchenKind> BreadKinds() => KitchenKinds().Where(k => k.Raw.All(r => r.Path.StartsWith("dough"))).ToList();

    public static bool IsDough(ItemStack s) => s.Collectible.Code.Path.StartsWith("dough");
    public static bool IsFlour(ItemStack s) => s.Collectible.Code.Path.StartsWith("flour");

    /// <summary>Grain (or anything) a quern grinds into flour.</summary>
    public static bool IsGrindableToFlour(ItemStack s) =>
        s.Collectible.GrindingProps?.GroundStack?.ResolvedItemstack is { } ground && IsFlour(ground);

    /// <summary>True when a Bakery has a living baker: the Cook then leaves the bread to them.</summary>
    public bool BakerWorks(Colony colony) =>
        colony.Buildings.Any(b => b.Type == BuildingTypes.Bakery && b.Level >= Config.BakeryWorksFromLevel && !b.Removing && WorkerAt(colony, b, Jobs.Baker) is { IsDowned: false });

    /// <summary>How much bread there is: the warehouse, the Bakery and the baker, and what is on its fire pit.</summary>
    public int BreadStock(Colony colony, Building hut, CitizenRecord? baker)
    {
        var kinds = BreadKinds();
        ItemMatch bread = s => kinds.Any(k => k.IsMine(s));
        int count = CountItems(WarehouseContainers(colony), bread) + CountItems(HutStorage(colony, hut), bread);
        if (baker != null) count += PackCount(baker, bread);
        if (CookFirepit(colony, hut) is { } pit)
        {
            if (!pit.outputSlot.Empty && bread(pit.outputSlot.Itemstack)) count += pit.outputSlot.StackSize;
            if (!pit.inputSlot.Empty && IsDough(pit.inputSlot.Itemstack)) count += pit.inputSlot.StackSize;
        }
        return count;
    }

    /// <summary>The quern the baker grinds at: the nearest one by the Bakery.</summary>
    public BlockEntityQuern? BakeryQuern(Building hut) => NearHut<BlockEntityQuern>(hut, Config.BakeryWorkRadius);

    /// <summary>The fire pit kind that bakes this dough, or null.</summary>
    public KitchenKind? BreadFor(ItemStack dough) => BreadKinds().FirstOrDefault(k => k.IsRaw(dough));

    /// <summary>Grinds what the baker carries into flour, as much as GrindBatch, the way the quern would. Returns how much flour it made.</summary>
    public int GrindAtQuern(Colony colony, Building hut, CitizenRecord baker)
    {
        var grain = TakeFromPack(colony, baker, IsGrindableToFlour, Config.BakerGrindBatch);
        if (grain?.Collectible.GrindingProps?.GroundStack?.ResolvedItemstack is not { } ground) return 0;
        var flour = ground.Clone();
        flour.StackSize = Math.Max(1, ground.StackSize) * grain.StackSize;
        int made = flour.StackSize;
        StoreHarvest(colony, hut, baker, new[] { flour }, hut.HutPos);
        AddStat(hut, "flour", made);
        return made;
    }

    /// <summary>
    /// The game's recipe that mixes this flour into dough: how much flour it takes and the dough it makes. The water it
    /// also takes comes from the Bakery. Null when no recipe makes dough of this flour.
    /// </summary>
    public (int Flour, ItemStack Dough)? DoughRecipe(ItemStack flour)
    {
        foreach (var recipe in sapi.World.GridRecipes)
        {
            var output = recipe?.Output?.ResolvedItemStack;
            if (recipe == null || output == null || !recipe.Enabled || recipe.ResolvedIngredients == null || !IsDough(output)) continue;
            var parts = recipe.ResolvedIngredients.Where(i => i != null).ToList();
            var flourParts = parts.Where(i => i!.SatisfiesAsIngredient(flour, false)).ToList();
            if (flourParts.Count == 0) continue;
            int quantity = flourParts.Sum(i => Math.Max(1, i!.Quantity));
            return (quantity, output.Clone());
        }
        return null;
    }

    /// <summary>Mixes one batch of dough from the baker's flour. False when it has too little flour for any recipe.</summary>
    public bool MixDough(Colony colony, Building hut, CitizenRecord baker)
    {
        var flour = PackItems(baker).FirstOrDefault(IsFlour);
        if (flour == null || DoughRecipe(flour) is not { } recipe) return false;
        int batches = Math.Max(1, Math.Min(Config.BakerDoughBatch, PackCount(baker, s => SameItem(s, flour)) / recipe.Flour));
        if (PackCount(baker, s => SameItem(s, flour)) < recipe.Flour) return false;
        var kind = flour.Collectible;
        TakeFromPack(colony, baker, s => s.Collectible == kind, recipe.Flour * batches);
        var dough = recipe.Dough.Clone();
        dough.StackSize = Math.Max(1, recipe.Dough.StackSize) * batches;
        StoreHarvest(colony, hut, baker, new[] { dough }, hut.HutPos);
        AddStat(hut, "dough", dough.StackSize);
        return true;
    }

    /// <summary>At a Bakery, grain, flour, dough and firewood stay; the bread goes to the warehouse.</summary>
    ItemMatch BakeryHutJunk() => s => !IsFirewood(s) && !IsDough(s) && !IsFlour(s) && !IsGrindableToFlour(s);

    /// <summary>The hut window's lines: bread on hand, the quern and the fire pit.</summary>
    public List<string> BakeryInfo(Colony colony, Building hut)
    {
        var lines = new List<string>
        {
            Lang.Get("createacolony:bakery-bread", BreadStock(colony, hut, WorkerAt(colony, hut, Jobs.Baker)), Config.BakerBreadKeep)
        };
        if (BakeryQuern(hut) == null) lines.Add(Lang.Get("createacolony:bakery-no-quern"));
        if (CookFirepit(colony, hut) == null) lines.Add(Lang.Get("createacolony:bakery-no-firepit"));
        return lines;
    }
}
