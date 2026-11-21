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
/// The smelter of a Smelter's Hut melts metal the way a player does in the copper age: it fills a fired
/// crucible with nuggets (or metal bits), puts it on the fire pit by the hut with charcoal, lets the game
/// melt it, and pours the metal into fired ingot molds it sets out beside the fire pit. Once the ingots
/// have cooled it takes them out. Ingots stay in the hut storage until couriers take them to the
/// warehouse. The Smelting tab says how many ingots of each metal to keep. Alloys such as bronze are
/// mixed in the crucible from the nuggets of each metal in the right shares (ColonyManager.Alloys.cs), and
/// iron is made in the game's bloomery (ColonyManager.Bloomery.cs).
/// </summary>
public partial class ColonyManager
{
    /// <summary>One metal: the ingot (its code is the key) and what melts into it in a crucible, with the units of metal each piece gives.</summary>
    public sealed class SmeltKind
    {
        public required string Key;
        public required ItemStack Sample;
        public readonly Dictionary<AssetLocation, int> UnitsPer = new();
        public readonly Dictionary<AssetLocation, int> MeltingPoint = new();
        /// <summary>For an alloy: the game's alloy recipe and the metals in it, each with the ores that melt into it.</summary>
        public AlloyRecipe? Alloy;
        public readonly List<AlloyPart> Parts = new();
        /// <summary>Made in the bloomery, not the crucible (iron bloom): pieces of each ore per bloom.</summary>
        public bool Bloomery;
        public readonly Dictionary<AssetLocation, int> BloomRatio = new();

        public bool IsOre(ItemStack s) => UnitsPer.ContainsKey(s.Collectible.Code);
        public bool IsMine(ItemStack s) => s.Collectible.Code.ToString() == Key;
    }

    List<SmeltKind>? smeltKinds;

    /// <summary>Every metal the game melts in a crucible into ingots, with what melts into it.</summary>
    public List<SmeltKind> SmeltKinds()
    {
        if (smeltKinds != null) return smeltKinds;
        var kinds = new Dictionary<string, SmeltKind>();
        foreach (var obj in sapi.World.Collectibles)
        {
            var props = obj?.CombustibleProps;
            // A bloom is hammered on the anvil, never melted (ColonyManager.Bloomery.cs).
            if (obj is IAnvilWorkable && obj.Code?.Path.Contains("bloom") == true) continue;
            if (obj?.Code == null || props?.SmeltedStack == null || !props.RequiresContainer || props.MeltingPoint <= 0 || props.SmeltingType != EnumSmeltType.Smelt) continue;
            var ingot = props.SmeltedStack.ResolvedItemstack;
            if (ingot == null || ingot.Collectible.FirstCodePart() != "ingot" || ingot.Collectible.Code.Equals(obj.Code)) continue;
            int units = (int)Math.Round(100.0 * props.SmeltedStack.StackSize / Math.Max(1, props.SmeltedRatio));
            if (units <= 0) continue;
            string key = ingot.Collectible.Code.ToString();
            if (!kinds.TryGetValue(key, out var kind))
            {
                var sample = ingot.Clone();
                sample.StackSize = 1;
                kinds[key] = kind = new SmeltKind { Key = key, Sample = sample };
            }
            kind.UnitsPer[obj.Code] = units;
            kind.MeltingPoint[obj.Code] = props.MeltingPoint;
        }
        var all = kinds.Values.ToList();
        all.AddRange(AlloyKinds(kinds));
        all.AddRange(BloomeryKinds());
        return smeltKinds = all.OrderBy(k => k.Alloy != null ? 1 : k.Bloomery ? 2 : 0).ThenBy(k => k.Key).ToList();
    }

    public SmeltKind? SmeltKindOf(ItemStack stack) => SmeltKinds().FirstOrDefault(k => k.IsOre(stack));

    /// <summary>True for nuggets, metal bits and anything else a crucible melts into ingots.</summary>
    public bool IsSmeltable(ItemStack stack) => SmeltKindOf(stack) != null;

    /// <summary>An empty fired crucible.</summary>
    public static bool IsEmptyCrucible(ItemStack s) => s.Block is BlockSmeltingContainer;

    /// <summary>A crucible with molten (or cooled) metal in it, ready to pour.</summary>
    public static bool IsFullCrucible(ItemStack s) => s.Block is BlockSmeltedContainer;

    /// <summary>A fired ingot mold, not yet set out.</summary>
    public static bool IsIngotMold(ItemStack s) => s.Block is BlockIngotMold && (s.Block.Variant["type"] == "fired" || s.Block.Code.Path.Contains("burned"));

    /// <summary>Fuel that burns hot enough to melt this; charcoal or coal for copper, firewood is enough for tin and lead.</summary>
    public static bool IsSmeltFuel(ItemStack s, int temperature) =>
        s.Collectible.CombustibleProps is { BurnDuration: > 0 } props && props.BurnTemperature >= temperature && s.Block == null
        && !IsSmeltableStatic(s);

    static bool IsSmeltableStatic(ItemStack s) => s.Collectible.CombustibleProps?.SmeltedStack != null && s.Collectible.CombustibleProps.RequiresContainer;

    /// <summary>The temperature a fire must reach to melt this kind's ore (the hottest of what it has on hand).</summary>
    public static int MeltTemperature(SmeltKind kind, IEnumerable<ItemStack> ore) =>
        ore.Where(kind.IsOre).Select(s => kind.MeltingPoint[s.Collectible.Code]).DefaultIfEmpty(kind.MeltingPoint.Values.Max()).Max();

    /// <summary>Ingots of a metal ready: at the hut, in the warehouse, with the smelter and cooling in the molds.</summary>
    public int SmeltStock(Colony colony, Building hut, SmeltKind kind, CitizenRecord? smelter)
    {
        ItemMatch mine = kind.IsMine;
        int count = CountItems(WarehouseContainers(colony), mine) + CountItems(HutStorage(colony, hut), mine);
        if (smelter != null) count += PackCount(smelter, mine);
        foreach (var mold in SmelterMolds(hut))
        {
            if (mold.ContentsLeft != null && mine(mold.ContentsLeft)) count++;
            if (mold.ContentsRight != null && mine(mold.ContentsRight)) count++;
        }
        if (kind.Bloomery) return count + BloomsInBloomery(hut, kind);
        // Metal in the crucible counts too, so a second batch of it is not started.
        var pit = SmelterFirepit(hut);
        foreach (var slot in pit == null ? Array.Empty<ItemSlot>() : new[] { pit.inputSlot, pit.outputSlot })
        {
            if (slot.Itemstack?.Block is BlockSmeltedContainer full && full.GetContents(sapi.World, slot.Itemstack) is { Key: { } metal } contents && mine(metal)) count += contents.Value / 100;
        }
        if (pit != null && CrucibleMakes(pit) is { } making && making.Code == kind.Key) count += making.Units / 100;
        return count;
    }

    /// <summary>The metals there are too few ingots of, most wanted first.</summary>
    public List<SmeltKind> WantedIngots(Colony colony, Building hut, CitizenRecord? smelter) => ByPriority(hut, SmeltKinds()
        .Select(k => (Kind: k, Want: CraftWant(colony, hut, k.Key)))
        .Select(k => (k.Kind, k.Kind.Key, Short: k.Want - (k.Want > 0 ? SmeltStock(colony, hut, k.Kind, smelter) : 0)))
        .Where(k => k.Short > 0))
        .ToList();

    /// <summary>The fire pit the smelter uses: the nearest finished one within reach of the hut.</summary>
    public BlockEntityFirepit? SmelterFirepit(Building hut) =>
        NearHut<BlockEntityFirepit>(hut, Config.SmelterRadius, p => !p.Block.Code.Path.Contains("construct") && !IsOvenFire(p));

    /// <summary>Ingot molds set out within reach of the smelter's fire pit (or of the hut when it has none).</summary>
    public List<BlockEntityIngotMold> SmelterMolds(Building hut)
    {
        var at = SmelterFirepit(hut)?.Pos ?? hut.HutPos;
        return AllNear<BlockEntityIngotMold>(at, Config.SmelterMoldRadius);
    }

    /// <summary>The sides of a mold (false left, true right) that take molten metal right now.</summary>
    static IEnumerable<bool> FreeSides(BlockEntityIngotMold mold)
    {
        if (!mold.ShatteredLeft && mold.MoldLeft != null && mold.ContentsLeft == null) yield return false;
        if (mold.QuantityMolds > 1 && !mold.ShatteredRight && mold.MoldRight != null && mold.ContentsRight == null) yield return true;
    }

    /// <summary>How many ingots the molds set out can take right now.</summary>
    public int FreeMoldSides(Building hut) => SmelterMolds(hut).Sum(m => FreeSides(m).Count());

    /// <summary>A mold with a cooled, full ingot in it to take out.</summary>
    public BlockEntityIngotMold? MoldWithIngot(Building hut) => SmelterMolds(hut)
        .FirstOrDefault(m => (m.ContentsLeft != null && m.IsHardenedLeft) || (m.ContentsRight != null && m.IsHardenedRight));

    /// <summary>A spot next to the fire pit to set an ingot mold down: free, on solid ground, not the fire pit's own.</summary>
    public BlockPos? FindMoldSpot(Colony colony, Building hut, Block moldBlock)
    {
        var pit = SmelterFirepit(hut);
        if (pit == null) return null;
        var ba = sapi.World.BlockAccessor;
        int r = Math.Max(1, Config.SmelterMoldRadius);
        for (int ring = 1; ring <= r; ring++)
        {
            for (int dx = -ring; dx <= ring; dx++)
            for (int dz = -ring; dz <= ring; dz++)
            {
                if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != ring) continue;
                for (int dy = 0; dy >= -1; dy--)
                {
                    var pos = pit.Pos.AddCopy(dx, dy, dz);
                    if (pos.Equals(hut.HutPos) || !ba.GetBlock(pos).IsReplacableBy(moldBlock) || ba.GetBlock(pos, BlockLayersAccess.Fluid).Id != 0) continue;
                    var below = pos.DownCopy();
                    if (!ba.GetBlock(below).CanAttachBlockAt(ba, moldBlock, below, BlockFacing.UP)) continue;
                    // Never in a doorway.
                    if (ba.GetBlock(pos.UpCopy()) is BlockDoor || ba.GetBlock(pos) is BlockDoor) continue;
                    if (GetAt(pos) != colony || !CanWorkAt(colony, pos)) continue;
                    return pos;
                }
            }
        }
        return null;
    }

    /// <summary>Sets a fired ingot mold down at the spot, as a player does.</summary>
    public bool PlaceMold(Colony colony, CitizenRecord smelter, BlockPos pos)
    {
        var mold = TakeFromPack(colony, smelter, IsIngotMold, 1);
        if (mold == null) return false;
        var ba = sapi.World.BlockAccessor;
        if (!ba.GetBlock(pos).IsReplacableBy(mold.Block))
        {
            AddToPack(colony, smelter, mold);
            return false;
        }
        ba.SetBlock(mold.Block.BlockId, pos);
        if (ba.GetBlockEntity(pos) is BlockEntityIngotMold be)
        {
            be.MoldLeft = mold.Clone();
            be.MoldLeft.StackSize = 1;
            be.MarkDirty(true);
        }
        if (mold.Block.Sounds?.Place != null) sapi.World.PlaySoundAt(mold.Block.Sounds.Place, pos, -0.5);
        return true;
    }

    /// <summary>
    /// Fills an empty crucible with ore of one kind and puts it on the fire pit with fuel, then lights it.
    /// Takes as much ore as makes whole ingots, up to what the crucible and the free molds hold.
    /// </summary>
    public bool LoadCrucible(Colony colony, CitizenRecord smelter, BlockEntityFirepit pit, SmeltKind kind, int ingots)
    {
        if (!pit.inputSlot.Empty) return false;
        var pack = PackItems(smelter);
        var ore = pack.Where(kind.IsOre).GroupBy(s => s.Collectible.Code).OrderByDescending(g => g.Sum(s => s.StackSize)).FirstOrDefault();
        if (ore == null) return false;
        var code = ore.Key;
        int perIngot = PiecesPerIngot(kind, code);
        int pieces = Math.Min(ore.Sum(s => s.StackSize) / perIngot, ingots) * perIngot;
        if (pieces <= 0) return false;

        var crucible = TakeFromPack(colony, smelter, IsEmptyCrucible, 1);
        if (crucible == null) return false;
        pit.inputSlot.Itemstack = crucible;
        pit.inputSlot.MarkDirty(); // the fire pit opens the crucible's slots
        var slots = pit.otherCookingSlots;
        int left = pieces;
        foreach (var slot in slots)
        {
            if (left <= 0) break;
            while (left > 0 && TakeFromPack(colony, smelter, s => s.Collectible.Code.Equals(code), left) is { } part)
            {
                var source = new DummySlot(part);
                int moved = source.TryPutInto(sapi.World, slot, part.StackSize);
                left -= moved;
                if (!source.Empty)
                {
                    AddToPack(colony, smelter, source.Itemstack);
                    break;
                }
            }
            slot.MarkDirty();
        }

        // Whole ingots only: what did not fit in the crucible's slots comes back out.
        int inCrucible = slots.Where(s => !s.Empty).Sum(s => s.StackSize);
        int extra = inCrucible % perIngot;
        for (int i = slots.Length - 1; i >= 0 && extra > 0; i--)
        {
            if (slots[i].Empty) continue;
            int take = Math.Min(extra, slots[i].StackSize);
            var back = slots[i].TakeOut(take);
            AddToPack(colony, smelter, back);
            extra -= take;
            slots[i].MarkDirty();
        }

        if (!CrucibleCanSmelt(pit))
        {
            UnloadCrucible(colony, smelter, pit);
            return false;
        }
        FuelSmelterFire(colony, smelter, pit, MeltTemperature(kind, slots.Where(s => !s.Empty).Select(s => s.Itemstack!)));
        return true;
    }

    /// <summary>True when the game will melt what is in the crucible on the fire pit.</summary>
    bool CrucibleCanSmelt(BlockEntityFirepit pit) =>
        pit.Inventory is ISlotProvider cooking && pit.inputSlot.Itemstack is { Block: BlockSmeltingContainer bsc } crucible
        && pit.otherCookingSlots.Any(s => !s.Empty) && bsc.CanSmelt(sapi.World, cooking, crucible, null);

    /// <summary>Takes the ore and then the empty crucible off the fire pit, back to the smelter.</summary>
    void UnloadCrucible(Colony colony, CitizenRecord smelter, BlockEntityFirepit pit)
    {
        // Take the ore out before the crucible: a fire pit drops whatever is in a crucible taken off it.
        foreach (var slot in pit.otherCookingSlots)
        {
            if (slot.Empty) continue;
            AddToPack(colony, smelter, slot.Itemstack);
            slot.Itemstack = null;
            slot.MarkDirty();
        }
        if (pit.inputSlot.Itemstack is { } crucible) AddToPack(colony, smelter, crucible);
        pit.inputSlot.Itemstack = null;
        pit.inputSlot.MarkDirty();
        pit.MarkDirty(true);
    }

    /// <summary>How many pieces of this ore make one ingot (20 nuggets, say).</summary>
    public static int PiecesPerIngot(SmeltKind kind, AssetLocation code) => kind.Bloomery
        ? Math.Max(1, kind.BloomRatio[code])
        : Math.Max(1, (int)Math.Ceiling(100.0 / kind.UnitsPer[code]));

    /// <summary>The temperature the fire pit must reach for what is on it now.</summary>
    public int SmelterHeatNeeded(BlockEntityFirepit pit)
    {
        var stack = pit.inputSlot.Itemstack;
        if (stack == null) return 0;
        try
        {
            if (stack.Block is BlockSmeltedContainer full && full.GetContents(sapi.World, stack).Key is { } metal)
            {
                return (int)metal.Collectible.GetMeltingPoint(sapi.World, null, new DummySlot(metal));
            }
            return (int)stack.Collectible.GetMeltingPoint(sapi.World, pit.Inventory as ISlotProvider, pit.inputSlot);
        }
        catch (Exception)
        {
            return 1100;
        }
    }

    /// <summary>Adds fuel hot enough for this if the fire pit has none, and lights it.</summary>
    public void FuelSmelterFire(Colony colony, CitizenRecord smelter, BlockEntityFirepit pit, int temperature)
    {
        if ((pit.fuelSlot.Empty || !IsSmeltFuel(pit.fuelSlot.Itemstack, temperature))
            && TakeFromPack(colony, smelter, s => IsSmeltFuel(s, temperature), Config.SmelterFuel) is { } fuel)
        {
            if (!pit.fuelSlot.Empty) AddToPack(colony, smelter, pit.fuelSlot.Itemstack);
            pit.fuelSlot.Itemstack = fuel;
            pit.fuelSlot.MarkDirty();
        }
        if (!pit.IsBurning && !pit.fuelSlot.Empty)
        {
            pit.igniteFuel();
            pit.canIgniteFuel = true;
        }
        pit.MarkDirty(true);
    }

    /// <summary>A crucible of metal on the fire pit (the smelted one in the output, or one put back to melt again in the input).</summary>
    public static ItemSlot? FullCrucibleSlot(BlockEntityFirepit pit) =>
        pit.outputSlot.Itemstack is { } o && IsFullCrucible(o) ? pit.outputSlot
        : pit.inputSlot.Itemstack is { } i && IsFullCrucible(i) ? pit.inputSlot
        : null;

    /// <summary>True when the metal in the crucible is still runny enough to pour.</summary>
    public bool IsMolten(ItemStack crucible) =>
        crucible.Block is BlockSmeltedContainer full && full.GetContents(sapi.World, crucible) is { Key: { } metal }
        && !full.HasSolidifed(crucible, metal, sapi.World);

    /// <summary>
    /// Pours the crucible on the fire pit into every free mold within reach. An empty crucible goes back to
    /// the smelter; one with metal left in it goes back on the fire to melt again.
    /// </summary>
    public int PourCrucible(Colony colony, Building hut, CitizenRecord smelter, BlockEntityFirepit pit)
    {
        var slot = FullCrucibleSlot(pit);
        if (slot?.Itemstack?.Block is not BlockSmeltedContainer full) return 0;
        var crucible = slot.Itemstack;
        var (metal, units) = full.GetContents(sapi.World, crucible);
        if (metal == null || units <= 0) return 0;
        float temp = crucible.Collectible.GetTemperature(sapi.World, crucible);
        int poured = 0;
        foreach (var mold in SmelterMolds(hut))
        {
            foreach (bool right in FreeSides(mold).ToList())
            {
                if (units <= 0) break;
                mold.IsRightSideSelected = right;
                int amount = Math.Min(units, mold.RequiredUnits);
                int before = amount;
                mold.ReceiveLiquidMetal(metal, ref amount, temp);
                units -= before - amount;
                if (before - amount > 0) poured++;
                mold.MarkDirty(true);
            }
            if (units <= 0) break;
        }

        if (units <= 0)
        {
            // Empty: back to a plain crucible, as when a player pours out the last of it.
            string emptied = full.Attributes?["emptiedBlockCode"].AsString() ?? "";
            var empty = emptied.Length > 0 ? sapi.World.GetBlock(AssetLocation.Create(emptied, full.Code.Domain)) : null;
            slot.Itemstack = null;
            slot.MarkDirty();
            if (empty != null) AddToPack(colony, smelter, new ItemStack(empty));
        }
        else
        {
            crucible.Attributes.SetInt("units", units);
            // Metal left over goes back on the fire to stay runny until there are molds for it.
            if (slot == pit.outputSlot && pit.inputSlot.Empty)
            {
                pit.inputSlot.Itemstack = crucible;
                pit.outputSlot.Itemstack = null;
                pit.inputSlot.MarkDirty();
            }
            slot.MarkDirty();
        }
        pit.MarkDirty(true);
        if (poured > 0) AddStat(hut, "ingots-poured", poured);
        return poured;
    }

    /// <summary>Puts a crucible whose metal has gone hard back on the fire, with fuel, to melt it again.</summary>
    public void ReheatCrucible(Colony colony, CitizenRecord smelter, BlockEntityFirepit pit)
    {
        if (!pit.inputSlot.Empty || pit.outputSlot.Itemstack is not { } crucible || !IsFullCrucible(crucible)) return;
        pit.inputSlot.Itemstack = crucible;
        pit.outputSlot.Itemstack = null;
        pit.inputSlot.MarkDirty();
        pit.outputSlot.MarkDirty();
        FuelSmelterFire(colony, smelter, pit, SmelterHeatNeeded(pit));
    }

    /// <summary>Takes the cooled ingots out of a mold into the smelter's inventory.</summary>
    public int TakeIngots(Colony colony, CitizenRecord smelter, BlockEntityIngotMold mold)
    {
        int taken = 0;
        foreach (bool right in new[] { false, true })
        {
            mold.IsRightSideSelected = right;
            if (mold.SelectedContents == null || !mold.SelectedIsHardened) continue;
            var drop = mold.GetSelectedStateAwareContents();
            if (drop != null)
            {
                AddToPack(colony, smelter, drop);
                if (drop.StackSize > 0) sapi.World.SpawnItemEntity(drop, mold.Pos.ToVec3d().Add(0.5, 0.2, 0.5));
                taken++;
            }
            // A part-filled ingot is lost like a player's would be chiselled out; it is rare as the smelter loads whole ingots.
            mold.SelectedContents = null;
            mold.SelectedFillLevel = 0;
        }
        mold.IsRightSideSelected = false;
        mold.MarkDirty(true);
        if (taken > 0) sapi.World.PlaySoundAt(new AssetLocation("sounds/block/ingot"), mold.Pos, -0.5);
        return taken;
    }

    /// <summary>At a Smelter's Hut ore, fuel, crucibles and molds stay; ingots and everything else go to the warehouse.</summary>
    ItemMatch SmelterHutJunk() => s => !IsSmeltable(s) && !IsEmptyCrucible(s) && !IsFullCrucible(s) && !IsIngotMold(s)
        && !IsSmeltFuel(s, Config.SmelterKeepFuelTemperature) && !IsFirewood(s);

    /// <summary>A line about the fire pit and molds for the hut window.</summary>
    public string SmelterInfo(Colony colony, Building hut)
    {
        var pit = SmelterFirepit(hut);
        string bloomery = BloomeryInfo(hut) is { } line ? "<br>" + line : "";
        if (pit == null) return Lang.Get("createacolony:smelter-no-firepit", Config.SmelterRadius) + bloomery;
        int molds = SmelterMolds(hut).Sum(m => (m.MoldLeft != null && !m.ShatteredLeft ? 1 : 0) + (m.QuantityMolds > 1 && m.MoldRight != null && !m.ShatteredRight ? 1 : 0));
        string fire = FullCrucibleSlot(pit) != null ? Lang.Get("createacolony:smelter-fire-poured")
            : pit.inputSlot.Itemstack is { } input && IsEmptyCrucible(input) ? Lang.Get(pit.IsBurning ? "createacolony:smelter-fire-melting" : "createacolony:smelter-fire-waiting")
            : Lang.Get("createacolony:smelter-fire-ready");
        return fire + "<br>" + Lang.Get("createacolony:smelter-molds", molds, FreeMoldSides(hut)) + bloomery;
    }
}
