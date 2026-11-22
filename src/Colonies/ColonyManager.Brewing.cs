using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using ItemMatch = System.Func<Vintagestory.API.Common.ItemStack, bool>;

namespace CreateAColony.Colonies;

/// <summary>
/// The Cook brews drinks for the Tavern (economy step 2; Aaron: "the cook does it for now", a Brewer job comes later).
/// It uses the game's own barrel recipes whose output is a drink (config TavernDrinks), the way the Tanner tans: in
/// empty barrels within CookBrewRadius of the Cook's Hut it pours the liquid the recipe takes (juice, honey) from jugs
/// and buckets in the hut storage or the warehouse, adds the solid the recipe takes if any, and seals the barrel once
/// the game finds the recipe. The Tavern Keeper fills jugs from the barrel when the brew is done. Pressing fruit into
/// juice stays the player's job. Only while the colony has a Tavern and config CookBrews is on.
/// </summary>
public partial class ColonyManager
{
    /// <summary>A barrel recipe that makes a drink: the liquid it takes and the solid, if any.</summary>
    public sealed class BrewRecipe
    {
        public BarrelRecipe Recipe = null!;
        public BarrelRecipeIngredient Liquid = null!;
        public BarrelRecipeIngredient? Solid;
    }

    List<BrewRecipe>? brewRecipes;

    /// <summary>The game's barrel recipes that make a drink, worked out once.</summary>
    public List<BrewRecipe> BrewRecipes()
    {
        if (brewRecipes != null) return brewRecipes;
        var list = new List<BrewRecipe>();
        foreach (var r in sapi.GetBarrelRecipes())
        {
            if (r.Output?.ResolvedItemstack is not { } output || !IsDrink(output) || r.Ingredients == null) continue;
            var liquids = r.Ingredients.Where(IsLiquidIngredient).ToList();
            var solids = r.Ingredients.Where(i => !IsLiquidIngredient(i)).ToList();
            if (liquids.Count != 1 || solids.Count > 1) continue;
            list.Add(new BrewRecipe { Recipe = r, Liquid = liquids[0], Solid = solids.FirstOrDefault() });
        }
        return brewRecipes = list;
    }

    /// <summary>True for a liquid some brew takes (juice, honey).</summary>
    bool IsBrewLiquid(ItemStack? liquid) => liquid != null && BrewRecipes().Any(b => b.Liquid.SatisfiesAsIngredient(liquid, false));

    /// <summary>True for a jug or bucket holding a liquid some brew takes.</summary>
    public bool HoldsBrewLiquid(ItemStack? container) => IsBrewLiquid(ContentOf(container));

    /// <summary>True for a solid some brew takes.</summary>
    public bool IsBrewSolid(ItemStack s) => BrewRecipes().Any(b => b.Solid?.SatisfiesAsIngredient(s, false) == true);

    /// <summary>True when the Cook brews: config CookBrews, the colony has a Tavern and there are drink recipes.</summary>
    public bool CookBrews(Colony colony) => Config.CookBrews && colony.Buildings.Any(b => b.Type == BuildingTypes.Tavern && b.Level >= 1) && BrewRecipes().Count > 0;

    /// <summary>
    /// The barrels near the Cook's Hut the cook may brew in: not sealed, and empty or holding only a brew's liquid and
    /// solid. Barrels with a finished drink are the Tavern Keeper's; anything else is left alone.
    /// </summary>
    public List<BlockEntityBarrel> BrewBarrels(Building cookHut) =>
        AllNear<BlockEntityBarrel>(cookHut.HutPos, Math.Max(1, Config.CookBrewRadius))
            .Where(b => !b.Sealed
                && (b.Inventory[1].Empty || IsBrewLiquid(b.Inventory[1].Itemstack))
                && (b.Inventory[0].Empty || IsBrewSolid(b.Inventory[0].Itemstack)))
            .ToList();

    /// <summary>The brew a barrel has started (its liquid), or null for an empty one.</summary>
    BrewRecipe? BrewIn(BlockEntityBarrel barrel)
    {
        var liquid = barrel.Inventory[1].Itemstack;
        return liquid == null ? null : BrewRecipes().FirstOrDefault(b => b.Liquid.SatisfiesAsIngredient(liquid, false));
    }

    /// <summary>A brew barrel this jug's liquid can go in: one already holding that liquid and not full, else an empty one.</summary>
    public BlockEntityBarrel? BarrelForLiquid(List<BlockEntityBarrel> barrels, ItemStack liquid)
    {
        string code = liquid.Collectible.Code.ToString();
        return barrels.FirstOrDefault(b => b.Inventory[1].Itemstack?.Collectible.Code.ToString() == code && BarrelLitres(b) < b.CapacityLitres - 0.5f)
            ?? barrels.FirstOrDefault(b => b.Inventory[1].Empty && b.Inventory[0].Empty);
    }

    /// <summary>A barrel holding a brew's liquid whose recipe takes a solid it does not have yet, and that solid.</summary>
    public (BlockEntityBarrel Barrel, BarrelRecipeIngredient Solid)? BarrelWantingSolid(List<BlockEntityBarrel> barrels)
    {
        foreach (var b in barrels)
        {
            if (b.CurrentRecipe != null || !b.Inventory[0].Empty || BrewIn(b) is not { Solid: { } solid }) continue;
            return (b, solid);
        }
        return null;
    }

    /// <summary>
    /// At a brew barrel: pours in what the cook carries of the barrel's liquid (or any brew liquid for an empty one),
    /// adds the solid its recipe takes, and seals it once the game finds the recipe. Returns true when it did anything.
    /// </summary>
    public bool TendBrew(Colony colony, CitizenRecord cook, Building hut, BlockEntityBarrel barrel)
    {
        if (barrel.Sealed) return false;
        bool did = false;
        var items = PackItems(cook);
        var liquidSlot = barrel.Inventory[1];
        foreach (var jug in items.ToList())
        {
            if (jug.Collectible is not BlockLiquidContainerBase box || box.GetContent(jug) is not { } liquid || !IsBrewLiquid(liquid)) continue;
            if (!liquidSlot.Empty && liquidSlot.Itemstack.Collectible.Code.ToString() != liquid.Collectible.Code.ToString()) continue;
            float room = barrel.CapacityLitres - BarrelLitres(barrel);
            if (room <= 0.01f) break;
            float perJug = LitresOf(liquid);
            int jugs = jug.StackSize;
            for (int i = 0; i < jugs && room > 0.01f; i++)
            {
                var one = jug.Clone();
                one.StackSize = 1;
                var content = box.GetContent(one);
                if (content == null) break;
                float pour = Math.Min(perJug, room);
                var taken = box.TryTakeLiquid(one, pour);
                if (taken == null || taken.StackSize <= 0) break;
                if (liquidSlot.Empty) liquidSlot.Itemstack = taken;
                else liquidSlot.Itemstack.StackSize += taken.StackSize;
                room -= LitresOf(taken);
                jug.StackSize--;
                items.Add(one);
                did = true;
            }
        }
        items.RemoveAll(s => s.StackSize <= 0);
        if (did)
        {
            liquidSlot.MarkDirty();
            sapi.World.PlaySoundAt(new AssetLocation("sounds/block/water"), barrel.Pos.X + 0.5, barrel.Pos.Y + 0.5, barrel.Pos.Z + 0.5, null);
        }

        // The solid the recipe takes.
        if (barrel.Inventory[0].Empty && BrewIn(barrel) is { Solid: { } solid })
        {
            var piece = items.FirstOrDefault(s => solid.SatisfiesAsIngredient(s, false));
            if (piece != null)
            {
                barrel.Inventory[0].Itemstack = piece.Clone();
                barrel.Inventory[0].MarkDirty();
                items.Remove(piece);
                did = true;
            }
        }
        if (!did) return false;
        SavePack(colony, cook, items);
        barrel.MarkDirty(true);
        // The barrel finds the recipe itself; once it has, the brew is sealed (as a player seals it).
        if (barrel.CurrentRecipe != null && IsDrink(barrel.CurrentRecipe.Output?.ResolvedItemstack))
        {
            barrel.SealBarrel();
            barrel.MarkDirty(true);
            AddStat(hut, "brews");
        }
        return true;
    }

    /// <summary>Takes back a solid left over in a barrel whose brew is done, so the barrel can be used again.</summary>
    public void TakeBrewLeftovers(Colony colony, CitizenRecord cook, BlockEntityBarrel barrel)
    {
        var slot = barrel.Inventory[0];
        if (slot.Empty) return;
        var stack = slot.Itemstack;
        AddToPack(colony, cook, stack);
        slot.Itemstack = stack.StackSize > 0 ? stack : null;
        slot.MarkDirty();
        barrel.MarkDirty(true);
    }

    /// <summary>A chest (the Cook's Hut storage first, then the warehouse) with something matching, or null.</summary>
    public BlockEntityContainer? BrewSupplyChest(Colony colony, Building hut, ItemMatch match, System.Func<BlockPos, bool> skip) =>
        HutStorage(colony, hut).Concat(WarehouseContainers(colony))
            .FirstOrDefault(c => !skip(c.Pos) && c.Inventory.Any(s => !s.Empty && match(s.Itemstack)));
}
