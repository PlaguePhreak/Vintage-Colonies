using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace CreateAColony.Colonies;

/// <summary>
/// More home duties for settlers without a job (AiTaskSettlerChores). They skin dead animals lying on the
/// colony's land (and ones the colony's hunter left) with a knife, the way a player does, and take the meat, hide and the rest home. Raw food at
/// home they cook on a fire pit in or right by the home, and the cooked food goes in a chest there, where
/// hungry settlers find it.
/// </summary>
public partial class ColonyManager
{
    /// <summary>How long a settler that set out to skin a carcass keeps it for itself, so two do not go for the same one.</summary>
    const long CarcassClaimMs = 120_000;

    /// <summary>Carcass entity id to the settler entity going to skin it, and until when.</summary>
    readonly Dictionary<long, (long By, long Until)> carcassClaims = new();

    static readonly FieldInfo? HarvestDropsField = typeof(EntityBehaviorHarvestable).GetField("jsonDrops", BindingFlags.Instance | BindingFlags.NonPublic);
    static readonly PropertyInfo? HarvestMultiplierProp = typeof(EntityBehaviorHarvestable).GetProperty("dropQuantityMultiplier", BindingFlags.Instance | BindingFlags.NonPublic);

    public static bool IsKnife(ItemStack? s) => s?.Collectible.Tool == EnumTool.Knife;

    /// <summary>A dead animal that still has something on it: not skinned yet, or skinned with things left on it.</summary>
    public static bool Skinnable(Entity e)
    {
        if (e.Alive || e is EntityPlayer || e is Settlers.EntitySettler) return false;
        if (e.GetBehavior<EntityBehaviorHarvestable>() is not { } h) return false;
        return !h.IsHarvested || !h.Inventory.Empty;
    }

    /// <summary>The nearest dead animal on the colony's land that no other settler is on its way to skin.</summary>
    public Entity? CarcassToSkin(Colony colony, EntityAgent settler, System.Func<BlockPos, bool> skip)
    {
        long now = sapi.World.ElapsedMilliseconds;
        int r = Math.Max(8, Config.JoblessSkinRadius);
        var found = sapi.World.GetEntitiesAround(settler.Pos.XYZ, r, Math.Min(r, 24), e => Skinnable(e));
        // With a butcher, animals nobody has started on go to the Butcher's Hut whole (ColonyManager.Butchery.cs).
        bool butcher = ButcherWorks(colony);
        return found
            .Where(e => !butcher || e.GetBehavior<EntityBehaviorHarvestable>()?.IsHarvested == true)
            .Where(e => !skip(e.Pos.AsBlockPos) && (InClaim(colony, e.Pos.AsBlockPos) || ZoneAt(colony, e.Pos.AsBlockPos) != null
                || e.WatchedAttributes.GetString(HuntedByAttr) == colony.ColonyId))
            .Where(e => !carcassClaims.TryGetValue(e.EntityId, out var c) || c.By == settler.EntityId || c.Until < now)
            .OrderBy(e => e.Pos.SquareDistanceTo(settler.Pos.XYZ))
            .FirstOrDefault();
    }

    /// <summary>Keeps a carcass for this settler for a while.</summary>
    public void ClaimCarcass(Entity carcass, EntityAgent settler)
    {
        long now = sapi.World.ElapsedMilliseconds;
        foreach (var id in carcassClaims.Where(c => c.Value.Until < now).Select(c => c.Key).ToList()) carcassClaims.Remove(id);
        carcassClaims[carcass.EntityId] = (settler.EntityId, now + CarcassClaimMs);
    }

    /// <summary>Seconds skinning a carcass takes with this knife, as for a player.</summary>
    public static float SkinSeconds(Entity carcass, ItemSlot knife, EntityAgent settler)
    {
        if (carcass.GetBehavior<EntityBehaviorHarvestable>() is not { } h || h.IsHarvested) return 1f;
        float speed = knife.Itemstack?.Collectible is ItemKnife k ? k.GetKnifeHarvestingSpeed(knife) : 1f;
        return Math.Max(1f, speed * h.GetHarvestDuration(knife, settler));
    }

    /// <summary>
    /// Skins a carcass with the knife in the settler's hand (the knife wears as a player's does) and takes what
    /// is on it. What does not fit stays on the carcass for later; a carcass with nothing left on it rots away.
    /// Returns the codes of what was taken.
    /// </summary>
    public List<string> Skin(Colony colony, CitizenRecord citizen, EntityAgent settler, Entity carcass, ItemSlot knife)
    {
        var taken = new List<string>();
        if (carcass.GetBehavior<EntityBehaviorHarvestable>() is not { } h || h.Inventory is not InventoryGeneric inv) return taken;
        if (!h.IsHarvested)
        {
            if (!h.DropsGenerated) FillCarcass(carcass, h, inv, settler);
            carcass.WatchedAttributes.SetBool("harvested", true);
            carcass.WatchedAttributes.MarkPathDirty("harvested");
            knife.Itemstack?.Collectible.DamageItem(sapi.World, settler, knife, 3);
            sapi.World.PlaySoundAt(new AssetLocation("sounds/player/scrape"), carcass, null, true, 16);
        }
        foreach (var slot in inv)
        {
            if (slot.Empty) continue;
            var stack = slot.Itemstack;
            string code = stack.Collectible.Code.ToString();
            if (AddToPack(colony, citizen, stack, false) > 0) taken.Add(code);
            slot.Itemstack = stack.StackSize > 0 ? stack : null;
            slot.MarkDirty();
        }
        var tree = new TreeAttribute();
        inv.ToTreeAttributes(tree);
        carcass.WatchedAttributes["harvestableInv"] = tree;
        carcass.WatchedAttributes.MarkPathDirty("harvestableInv");
        carcassClaims.Remove(carcass.EntityId);
        if (inv.Empty) carcass.GetBehavior<EntityBehaviorDeadDecay>()?.DecayNow();
        return taken;
    }

    /// <summary>What the game would put on the carcass for a player skinning it; the game's own code needs a player.</summary>
    void FillCarcass(Entity carcass, EntityBehaviorHarvestable h, InventoryGeneric inv, EntityAgent settler)
    {
        h.DropsGenerated = true;
        var drops = HarvestDropsField?.GetValue(h) as BlockDropItemStack[] ?? Array.Empty<BlockDropItemStack>();
        float deathMult = HarvestMultiplierProp?.GetValue(h) is float f ? f : 1f;
        float statMult = carcass.Properties.Attributes?["isMechanical"].AsBool() == true ? 1f : settler.Stats.GetBlended("animalLootDropRate");
        var todrop = new List<ItemStack>();
        foreach (var dstack in drops)
        {
            if (dstack.Tool != null && dstack.Tool != EnumTool.Knife) continue;
            dstack.Resolve(sapi.World, "Vintage Colonies skinning ", carcass.Code);
            if (dstack.ResolvedItemstack == null) continue;
            float extra = dstack.DropModbyStat != null ? settler.Stats.GetBlended(dstack.DropModbyStat) : 1f;
            var obj = dstack.ResolvedItemstack.Collectible;
            var smelted = obj.GetCombustibleProperties(sapi.World, dstack.ResolvedItemstack, null)?.SmeltedStack?.ResolvedItemstack;
            // Meat comes off a thin animal in smaller amounts, as for a player.
            if (obj.GetNutritionProperties(sapi.World, dstack.ResolvedItemstack, settler) != null || smelted?.Collectible.GetNutritionProperties(sapi.World, smelted, settler) != null)
            {
                extra *= h.AnimalWeight;
            }
            var stack = dstack.GetNextItemStack(deathMult * statMult * extra);
            if (stack == null || stack.StackSize <= 0) continue;
            if (stack.Collectible is IResolvableCollectible irc)
            {
                var dummy = new DummySlot(stack);
                irc.Resolve(dummy, sapi.World);
                stack = dummy.Itemstack;
            }
            if (stack == null) continue;
            while (stack.StackSize > stack.Collectible.MaxStackSize)
            {
                var overflow = stack.GetEmptyClone();
                overflow.StackSize = stack.Collectible.MaxStackSize;
                stack.StackSize -= overflow.StackSize;
                todrop.Add(overflow);
            }
            todrop.Add(stack);
            if (dstack.LastDrop) break;
        }
        // Mods that add to what a carcass gives (the game's own code passes the player, so some may need one).
        foreach (var extraDrops in carcass.GetInterfaces<IHarvestableDrops>() ?? new List<IHarvestableDrops>())
        {
            try
            {
                var more = extraDrops.GetHarvestableDrops(sapi.World, carcass.Pos.AsBlockPos, null!);
                if (more != null) todrop.AddRange(more.Where(s => s != null));
            }
            catch (Exception e)
            {
                Mod.Logger.Warning("Skinning {0}: extra drops failed: {1}", carcass.Code, e.Message);
            }
        }
        if (todrop.Count > inv.Count) inv.AddSlots(todrop.Count - inv.Count);
        for (int i = 0; i < todrop.Count; i++) inv[i].Itemstack = todrop[i];
    }

    /// <summary>
    /// The fire pit a household cooks on: one inside the home or a few blocks from it, not the Cook's Hut's or an oven's.
    /// </summary>
    public BlockEntityFirepit? HomeFirepit(Colony colony, Building home)
    {
        var (min, max) = ZoneOf(home);
        int r = Math.Max(0, Config.JoblessFirepitReach);
        var area = new Cuboidi(min.X - r, min.Y - 2, min.Z - r, max.X + r, max.Y + 2, max.Z + r);
        var cookPits = colony.Buildings.Where(b => b.Type == BuildingTypes.Cook && b != home)
            .Select(b => CookFirepit(colony, b)?.Pos).OfType<BlockPos>().ToHashSet();
        var h = home.HutPos;
        return FindBlockEntities<BlockEntityFirepit>(area)
            .Where(p => !p.Block.Code.Path.Contains("construct") && !IsOvenFire(p) && !cookPits.Contains(p.Pos))
            .OrderBy(p => p.Pos.DistanceSqTo(h.X, h.Y, h.Z))
            .FirstOrDefault();
    }

    /// <summary>True when the fire pit holds cooked food to take off.</summary>
    public static bool FireHasFood(BlockEntityFirepit pit) =>
        !pit.outputSlot.Empty && pit.outputSlot.Itemstack.Collectible.NutritionProps != null;

    /// <summary>True when the fire pit can be fed or already burns, with what the settler carries or the home holds.</summary>
    public bool CanFuel(Colony colony, Building home, CitizenRecord citizen, BlockEntityFirepit pit) =>
        pit.IsBurning || !pit.fuelSlot.Empty || PackCount(citizen, IsFirewood) > 0 || HomeSupply(colony, home, IsFirewood) != null;
}
