using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Util;
using Vintagestory.GameContent;
using ItemMatch = System.Func<Vintagestory.API.Common.ItemStack, bool>;

namespace CreateAColony.Colonies;

/// <summary>
/// Iron (0.169.0): the smelter fills the game's bloomery by the Smelter's Hut with iron nuggets and charcoal in the
/// game's share (the same the bloomery lets a player put in), lights it, and when the game has burnt it (half a day)
/// takes the iron bloom out. A player breaks the bloomery to get the bloom; the smelter takes it out of the
/// bloomery's slot instead, so the bloomery stays. The bloomery needs its chimney on top, as for a player.
/// The blacksmith then heats the bloom in the forge and hammers it into an iron ingot (ColonyManager.Smithing.cs).
/// </summary>
public partial class ColonyManager
{
    static readonly FieldInfo? BloomeryInvField = typeof(BlockEntityBloomery).GetField("bloomeryInv", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    /// <summary>The bloomery's fuel, ore and bloom slots; null if the game changed them.</summary>
    static (ItemSlot Fuel, ItemSlot Ore, ItemSlot Out)? BloomerySlots(BlockEntityBloomery bloomery)
    {
        if (BloomeryInvField?.GetValue(bloomery) is not InventoryBase inv || inv.Count < 3) return null;
        return (inv[0], inv[1], inv[2]);
    }

    /// <summary>Everything the game makes in a bloomery that the config lets the smelter make (iron bloom), with what melts into it.</summary>
    List<SmeltKind> BloomeryKinds()
    {
        var kinds = new Dictionary<string, SmeltKind>();
        foreach (var obj in sapi.World.Collectibles)
        {
            var props = obj?.CombustibleProps;
            if (obj?.Code == null || props?.SmeltedStack == null || props.MeltingPoint < 1000 || props.MeltingPoint >= 1500) continue;
            var output = props.SmeltedStack.ResolvedItemstack;
            if (output == null || output.Collectible.Code.Equals(obj.Code)) continue;
            string key = output.Collectible.Code.ToString();
            if (!Config.BloomeryOutputs.Any(p => WildcardUtil.Match(p, key))) continue;
            int ratio = Math.Max(1, props.SmeltedRatio);
            if (!kinds.TryGetValue(key, out var kind))
            {
                var sample = output.Clone();
                sample.StackSize = 1;
                kinds[key] = kind = new SmeltKind { Key = key, Sample = sample, Bloomery = true };
            }
            // Pieces per bloom; a bloom counts as 100 units of metal.
            kind.BloomRatio[obj.Code] = Math.Max(1, (int)Math.Round(ratio / (double)Math.Max(1, props.SmeltedStack.StackSize)));
            kind.UnitsPer[obj.Code] = Math.Max(1, 100 / kind.BloomRatio[obj.Code]);
            kind.MeltingPoint[obj.Code] = props.MeltingPoint;
        }
        return kinds.Values.ToList();
    }

    /// <summary>The bloomery the smelter uses: the nearest one within reach of the Smelter's Hut.</summary>
    public BlockEntityBloomery? SmelterBloomery(Building hut) => NearHut<BlockEntityBloomery>(hut, Config.SmelterRadius);

    /// <summary>True when the bloomery has its chimney on top, so it can be lit.</summary>
    public bool HasChimney(BlockEntityBloomery bloomery) =>
        sapi.World.BlockAccessor.GetBlock(bloomery.Pos.UpCopy())?.Code?.Path.Contains("bloomerychimney") == true;

    /// <summary>A burnt bloomery with a bloom in it to take out.</summary>
    public static bool BloomReady(BlockEntityBloomery bloomery) =>
        !bloomery.IsBurning && BloomerySlots(bloomery) is { } slots && !slots.Out.Empty;

    /// <summary>True when the bloomery is cold and empty of anything a player put in, so the smelter can fill it.</summary>
    public bool BloomeryFree(BlockEntityBloomery bloomery, SmeltKind kind) =>
        !bloomery.IsBurning && BloomerySlots(bloomery) is { } slots && slots.Out.Empty
        && (slots.Ore.Empty || kind.IsOre(slots.Ore.Itemstack));

    /// <summary>Pieces of ore already in the bloomery (put in but not lit yet, or burning).</summary>
    public static int BloomeryOre(BlockEntityBloomery bloomery) => BloomerySlots(bloomery)?.Ore.StackSize ?? 0;

    /// <summary>Fuel a bloomery takes: charcoal or coal (hot and long burning).</summary>
    public static bool IsBloomeryFuel(ItemStack s) =>
        s.Collectible.CombustibleProps is { BurnTemperature: >= 1200, BurnDuration: > 30 } && s.Block == null && !IsSmeltableStatic(s);

    /// <summary>Pieces of ore in the bloomery per piece of fuel, as the game counts it.</summary>
    static int OreToFuel(ItemStack ore)
    {
        int ratio = ore.Collectible.CombustibleProps?.SmeltedRatio ?? 1;
        return Math.Max(1, ore.ItemAttributes?["bloomeryFuelRatio"].AsInt(ratio) ?? ratio);
    }

    /// <summary>Blooms waiting in the bloomery or still burning there, for the Smelting tab's count.</summary>
    int BloomsInBloomery(Building hut, SmeltKind kind)
    {
        if (SmelterBloomery(hut) is not { } bloomery || BloomerySlots(bloomery) is not { } slots) return 0;
        int count = 0;
        if (slots.Out.Itemstack is { } done && kind.IsMine(done)) count += done.StackSize;
        if (bloomery.IsBurning && slots.Ore.Itemstack is { } ore && kind.IsOre(ore)) count += ore.StackSize / PiecesPerIngot(kind, ore.Collectible.Code);
        return count;
    }

    /// <summary>
    /// Puts ore of one kind and enough fuel for it into the bloomery, for whole blooms, up to what the bloomery
    /// holds, and lights it. False when the bloomery cannot take it or is not ready.
    /// </summary>
    public bool LoadBloomery(Colony colony, CitizenRecord smelter, BlockEntityBloomery bloomery, SmeltKind kind, int blooms)
    {
        if (!BloomeryFree(bloomery, kind) || BloomerySlots(bloomery) is not { } slots || !HasChimney(bloomery)) return false;
        var pack = PackItems(smelter);
        var ore = slots.Ore.Itemstack is { } inside
            ? pack.Where(s => s.Collectible.Code.Equals(inside.Collectible.Code)).GroupBy(s => s.Collectible.Code).FirstOrDefault()
            : pack.Where(kind.IsOre).GroupBy(s => s.Collectible.Code).OrderByDescending(g => g.Sum(s => s.StackSize)).FirstOrDefault();
        int already = slots.Ore.StackSize;
        if (ore == null && already == 0) return false;
        var sample = ore?.First() ?? slots.Ore.Itemstack!;
        var code = sample.Collectible.Code;
        int per = PiecesPerIngot(kind, code);
        int fuelRatio = OreToFuel(sample);
        int capacity = fuelRatio * 6;
        int haveOre = already + (ore?.Sum(s => s.StackSize) ?? 0);
        int total = Math.Min(Math.Min(haveOre, capacity) / per, Math.Max(1, blooms)) * per;
        if (total <= 0 || total < already) total = already;
        if (total <= 0) return false;

        int addOre = total - already;
        if (addOre > 0 && TakeFromPack(colony, smelter, s => s.Collectible.Code.Equals(code), addOre) is { } part)
        {
            if (slots.Ore.Empty) slots.Ore.Itemstack = part;
            else slots.Ore.Itemstack.StackSize += part.StackSize;
            slots.Ore.MarkDirty();
        }
        if (slots.Ore.Empty) return false;

        int fuelNeeded = (int)Math.Ceiling(slots.Ore.StackSize / (double)fuelRatio);
        int addFuel = fuelNeeded - slots.Fuel.StackSize;
        if (addFuel > 0)
        {
            ItemMatch fits = s => IsBloomeryFuel(s) && (slots.Fuel.Empty || s.Collectible.Code.Equals(slots.Fuel.Itemstack.Collectible.Code));
            if (TakeFromPack(colony, smelter, fits, addFuel) is { } fuel)
            {
                if (slots.Fuel.Empty) slots.Fuel.Itemstack = fuel;
                else slots.Fuel.Itemstack.StackSize += fuel.StackSize;
                slots.Fuel.MarkDirty();
            }
        }
        bloomery.MarkDirty(true);
        sapi.World.PlaySoundAt(bloomery.OreSoundLocation, bloomery.Pos, 0);
        if (!bloomery.CanIgnite()) return false;
        bool lit = bloomery.TryIgnite();
        if (lit) bloomery.MarkDirty(true);
        return lit;
    }

    /// <summary>Lights a filled bloomery (fuel and ore in, chimney on top).</summary>
    public bool LightBloomery(BlockEntityBloomery bloomery)
    {
        if (!HasChimney(bloomery) || !bloomery.CanIgnite()) return false;
        bool lit = bloomery.TryIgnite();
        if (lit) bloomery.MarkDirty(true);
        return lit;
    }

    /// <summary>Takes the bloom out of a burnt bloomery into the smelter's inventory (what does not fit drops beside it).</summary>
    public int TakeBloom(Colony colony, Building hut, CitizenRecord smelter, BlockEntityBloomery bloomery)
    {
        if (!BloomReady(bloomery) || BloomerySlots(bloomery) is not { } slots) return 0;
        var bloom = slots.Out.Itemstack!;
        slots.Out.Itemstack = null;
        slots.Out.MarkDirty();
        int count = bloom.StackSize;
        AddToPack(colony, smelter, bloom);
        if (bloom.StackSize > 0) sapi.World.SpawnItemEntity(bloom, bloomery.Pos.ToVec3d().Add(0.5, 1.2, 0.5));
        bloomery.MarkDirty(true);
        sapi.World.PlaySoundAt(new AssetLocation("sounds/block/loosestone"), bloomery.Pos, 0);
        AddStat(hut, "blooms-made", count);
        return count;
    }

    /// <summary>A line about the bloomery for the hut window; null when there is none.</summary>
    public string? BloomeryInfo(Building hut)
    {
        if (SmelterBloomery(hut) is not { } bloomery) return null;
        if (!HasChimney(bloomery)) return Lang.Get("createacolony:smelter-bloomery-nochimney");
        if (bloomery.IsBurning) return Lang.Get("createacolony:smelter-bloomery-burning");
        if (BloomReady(bloomery)) return Lang.Get("createacolony:smelter-bloomery-done");
        return Lang.Get("createacolony:smelter-bloomery-ready");
    }

    // The blacksmith's side: hammering blooms into ingots.

    /// <summary>True for an iron bloom (or another bloom the anvil works), whole or part worked.</summary>
    public static bool IsBloom(ItemStack s) => s.Collectible is IAnvilWorkable && s.Collectible.FirstCodePart() != "ingot" && s.Collectible.Code.Path.Contains("bloom");

    /// <summary>
    /// What the anvil makes from a bloom: the game's smithing recipe from the bloom into an ingot (iron bloom to iron
    /// ingot), or the config's ingot for it when the game has no such recipe.
    /// </summary>
    IEnumerable<SmithKind> BloomSmithKinds()
    {
        foreach (var item in sapi.World.Items)
        {
            if (item?.Code == null || item is not IAnvilWorkable workable) continue;
            var bloom = new ItemStack(item);
            if (!IsBloom(bloom)) continue;
            SmithingRecipe? recipe = null;
            int tier, voxels;
            try
            {
                recipe = workable.GetMatchingRecipes(bloom).FirstOrDefault(r => r.Output?.ResolvedItemstack?.Collectible.FirstCodePart() == "ingot");
                tier = workable.GetRequiredAnvilTier(bloom);
                voxels = Math.Max(1, workable.VoxelCountForHandbook(bloom));
            }
            catch (Exception)
            {
                continue;
            }
            ItemStack? ingot = recipe?.Output?.ResolvedItemstack?.Clone();
            if (ingot == null && Config.BloomIngots.TryGetValue(item.Code.ToString(), out var code) && sapi.World.GetItem(new AssetLocation(code)) is { } ingotItem)
            {
                ingot = new ItemStack(ingotItem);
            }
            if (ingot == null) continue;
            var sample = ingot.Clone();
            sample.StackSize = Math.Max(1, ingot.StackSize);
            yield return new SmithKind
            {
                Key = ingot.Collectible.Code.ToString(),
                Sample = sample,
                Recipe = recipe,
                Ingot = item.Code,
                Ingots = 1,
                Strikes = Math.Max(1, (int)Math.Ceiling(voxels / (double)Math.Max(1, Config.BlacksmithVoxelsPerStrike)) + Config.BloomExtraStrikes),
                Tier = tier,
                Piece = ingot.Clone(),
                FromBloom = true
            };
        }
    }
}
