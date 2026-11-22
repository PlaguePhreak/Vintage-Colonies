using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Util;
using Vintagestory.GameContent;
using ItemMatch = System.Func<Vintagestory.API.Common.ItemStack, bool>;

namespace CreateAColony.Colonies;

/// <summary>
/// The colony's money (0.177.0): vanilla rusty gears, or the Coinage mod's coins when that mod is installed
/// (ColonyMoney). The treasury is the Town Hall's chests, the Royal Mint's storage and the warehouse. Once a month,
/// on payday, the bailiff collects TaxPercent of every settler's purse into the treasury, then pays each settler
/// the wage of their job (config Wages) from it. Settlers keep their money in their purse; what they spend it on
/// comes later. Taxes make settlers a little unhappy, more the higher they are, and so does a wage that was not paid.
/// Gears cannot be made, so the players fill the treasury. With Coinage a colony can pick one coin design
/// (Bailiff tab, "Use the coin in my hand"); the Royal Mint, only placeable with Coinage, strikes copies of it
/// from ingots of its metal. The mod's old copper coin is hidden and swapped for gears 1 for 1 (TickMoney).
/// </summary>
public partial class ColonyManager
{
    /// <summary>Each colony's picked Coinage coin (Colony.Currency) as a stack, by colony id; null when it could not be loaded.</summary>
    readonly Dictionary<string, ItemStack?> currencies = new();
    ItemStack? gearSample;
    ItemStack? defaultCoinageCoin;
    bool coinPatternsSet;

    /// <summary>True when the Coinage mod is installed: the colony's money is then its coins, not rusty gears.</summary>
    public bool CoinageLoaded
    {
        get
        {
            if (!coinPatternsSet)
            {
                coinPatternsSet = true;
                if (Config.OtherCoins is { Length: > 0 } patterns) ColonyMoney.CoinagePatterns = patterns;
            }
            return ColonyMoney.CoinageLoaded(sapi);
        }
    }

    /// <summary>True for any money: rusty gears, the old copper coin, or a Coinage coin.</summary>
    public bool IsCoin(ItemStack s)
    {
        var code = s.Collectible.Code;
        if (code.Domain == "createacolony") return FirstPart(code) == "coin";
        if (code.ToString() == ColonyMoney.GearCode) return true;
        _ = CoinageLoaded;
        return ColonyMoney.IsCoinageCoin(s);
    }

    /// <summary>True for a Coinage coin, the only kind a colony can pick on the Bailiff tab.</summary>
    public bool IsCoinageCoin(ItemStack s)
    {
        _ = CoinageLoaded;
        return ColonyMoney.IsCoinageCoin(s);
    }

    /// <summary>
    /// The colony's money: rusty gears; with Coinage the coin picked on the Bailiff tab, else the first Coinage coin.
    /// </summary>
    public ItemStack? CurrencySample(Colony colony)
    {
        if (!CoinageLoaded)
        {
            if (gearSample == null && sapi.World.GetItem(new AssetLocation(ColonyMoney.GearCode)) is { } gear) gearSample = new ItemStack(gear);
            return gearSample;
        }
        if (colony.Currency is { Length: > 0 } bytes)
        {
            if (!currencies.TryGetValue(colony.ColonyId, out var stack))
            {
                try
                {
                    using var reader = new System.IO.BinaryReader(new System.IO.MemoryStream(bytes));
                    stack = new ItemStack(reader, sapi.World);
                    if (stack.Collectible == null || !ColonyMoney.IsCoinageCoin(stack)) stack = null;
                }
                catch (Exception)
                {
                    stack = null;
                }
                currencies[colony.ColonyId] = stack;
            }
            if (stack != null) return stack;
        }
        return defaultCoinageCoin ??= ColonyMoney.FirstCoinageCoin(sapi.World);
    }

    /// <summary>True when the colony's money is Coinage coins (the mod is installed), false for rusty gears.</summary>
    public bool HasOwnCoin(Colony colony) => CoinageLoaded;

    /// <summary>True when a colony with Coinage has picked one coin design on the Bailiff tab.</summary>
    bool PickedCoin(Colony colony) => CoinageLoaded && colony.Currency is { Length: > 0 } && CurrencySample(colony) is { } s && !ReferenceEquals(s, defaultCoinageCoin);

    /// <summary>Matches the colony's money: rusty gears; the picked Coinage coin (same design only); or any Coinage coin.</summary>
    public ItemMatch CurrencyMatch(Colony colony)
    {
        var sample = CurrencySample(colony);
        if (sample == null) return _ => false;
        if (!CoinageLoaded) return s => s.Collectible == sample.Collectible;
        if (!PickedCoin(colony)) return s => ColonyMoney.IsCoinageCoin(s);
        return s => s.Collectible == sample.Collectible && sample.Equals(sapi.World, s, GlobalConstants.IgnoredStackAttributes);
    }

    /// <summary>A stack of the colony's money; null when there is none to make.</summary>
    public ItemStack? NewCoins(Colony colony, int count)
    {
        if (CurrencySample(colony) is not { } sample) return null;
        var stack = sample.Clone();
        stack.StackSize = count;
        return stack;
    }

    /// <summary>The money's name, for the Bailiff tab.</summary>
    public string CurrencyName(Colony colony) => CurrencySample(colony)?.GetName() ?? "";

    /// <summary>Makes this Coinage coin the colony's money (null: any Coinage coin).</summary>
    public void SetCurrency(Colony colony, ItemStack? coin)
    {
        if (coin != null)
        {
            var one = coin.Clone();
            one.StackSize = 1;
            colony.Currency = one.ToBytes();
        }
        else colony.Currency = null;
        currencies.Remove(colony.ColonyId);
        LogEvent(colony, "event-currency", CurrencyName(colony));
        MarkDirty(colony);
    }

    /// <summary>When TickMoney last ran for each colony (ms).</summary>
    readonly Dictionary<string, long> lastMoneyTick = new();
    const long MoneyTickMs = 60_000;

    /// <summary>
    /// Part of the colony tick, once a minute: old copper coins in the treasury and the Church become rusty gears,
    /// 1 for 1, and without Coinage the Royal Mint's minters stop (there is nothing to strike) and are free for other work.
    /// </summary>
    void TickMoney(Colony colony)
    {
        long ms = sapi.World.ElapsedMilliseconds;
        if (lastMoneyTick.TryGetValue(colony.ColonyId, out long last) && ms - last < MoneyTickMs) return;
        lastMoneyTick[colony.ColonyId] = ms;
        SwapOldCoins(colony);
        GiveStartingGears(colony);
        if (CoinageLoaded) return;
        foreach (var mint in colony.Buildings.Where(b => b.Type == BuildingTypes.Mint).ToList())
            foreach (var minter in WorkersAt(colony, mint).ToList())
            {
                UnassignWorker(colony, mint, minter);
                LogEvent(colony, "event-minter-freed", minter.Name);
            }
    }

    /// <summary>Swaps the old copper coins in the treasury and the Churches' storage for rusty gears, 1 for 1.</summary>
    void SwapOldCoins(Colony colony)
    {
        if (sapi.World.GetItem(new AssetLocation(ColonyMoney.GearCode)) is not { } gear) return;
        var chests = TreasuryContainers(colony)
            .Concat(colony.Buildings.Where(b => b.Type == BuildingTypes.Church).SelectMany(b => HutStorage(colony, b)))
            .Distinct().ToList();
        int swapped = 0, extra = 0;
        int max = Math.Max(1, gear.MaxStackSize);
        foreach (var chest in chests)
        {
            foreach (var slot in chest.Inventory)
            {
                if (slot.Empty || slot.Itemstack.Collectible.Code.ToString() != ColonyMoney.OldCoinCode) continue;
                int count = slot.Itemstack.StackSize;
                slot.Itemstack = new ItemStack(gear, Math.Min(count, max));
                extra += count - slot.Itemstack.StackSize;
                swapped += count;
                slot.MarkDirty();
            }
        }
        // Gears stack smaller than the old coins did: the rest go in with the others.
        while (extra > 0)
        {
            var rest = new ItemStack(gear, Math.Min(extra, max));
            int want = rest.StackSize;
            foreach (var chest in chests) if (rest.StackSize > 0) PutItems(chest, rest);
            if (rest.StackSize > 0) PutInWarehouse(colony, rest);
            if (rest.StackSize > 0) sapi.World.SpawnItemEntity(rest, colony.TownHallPos.ToVec3d().Add(0.5, 1.2, 0.5));
            extra -= want;
        }
        if (swapped > 0) LogEvent(colony, "event-oldcoins-swapped", swapped.ToString());
    }

    /// <summary>True once the colony has a built Royal Mint.</summary>
    public bool HasMint(Colony colony) => colony.Buildings.Any(b => b.Type == BuildingTypes.Mint && b.Level >= 1 && !b.Removing);

    /// <summary>Where the colony's coins are kept: the Town Hall's chests, the Royal Mint's storage, then the warehouse.</summary>
    List<BlockEntityContainer> TreasuryContainers(Colony colony) =>
        colony.Buildings.Where(b => b.Type == BuildingTypes.TownHall)
            .Concat(colony.Buildings.Where(b => b.Type == BuildingTypes.Mint && b.Level >= 1 && !b.Removing))
            .SelectMany(b => HutStorage(colony, b))
            .Concat(WarehouseContainers(colony))
            .Distinct().ToList();

    /// <summary>How many coins the treasury holds.</summary>
    public int TreasuryCoins(Colony colony) => CountItems(TreasuryContainers(colony), CurrencyMatch(colony));

    /// <summary>A job's monthly wage in coins; 0 for no job.</summary>
    public int WageOf(string? job) => job == null ? 0 : Config.Wages.TryGetValue(job, out int w) ? w : Config.WageDefault;

    /// <summary>The in-game month a moment falls in, counted from the start of the world.</summary>
    int MonthOf(double totalDays) => (int)(totalDays / Math.Max(1, sapi.World.Calendar.DaysPerMonth));

    /// <summary>The in-game day of the next payday: the first day of next month.</summary>
    public int NextPayday() => (MonthOf(sapi.World.Calendar.TotalDays) + 1) * Math.Max(1, sapi.World.Calendar.DaysPerMonth);

    /// <summary>True once the Town Hall is level EconomyTownHallLevel: before that there are no taxes or wages, and no moods about them.</summary>
    public bool EconomyOpen(Colony colony) => TownHallLevel(colony) >= Config.EconomyTownHallLevel;

    /// <summary>The standing thoughts about money, for ThoughtsOf: taxes, and a wage that was not paid.</summary>
    void MoneyThoughts(Colony colony, CitizenRecord c, Action<string, int> standing)
    {
        if (!EconomyOpen(colony)) return;
        if (c.Unpaid) standing("unpaid", Config.UnpaidMood);
        if (colony.BailiffCollectsRent && colony.TaxPercent > 0 && (HasMint(colony) || !HasOwnCoin(colony)) && BailiffOnDuty(colony) != null)
            standing("taxes", -(int)Math.Round(colony.TaxPercent * Config.TaxMoodPerPercent));
    }

    /// <summary>Settlers who pay taxes and earn a wage: grown, living members of the colony.</summary>
    static IEnumerable<CitizenRecord> Earners(Colony colony) => colony.Citizens.Where(c => c.Alive && !c.Visitor && !c.IsChild);

    /// <summary>
    /// Part of the bailiff's tick: on the first day of each month, taxes then wages. Nothing happens until the
    /// colony has money in its treasury (or a Royal Mint), and the Town Hall is level EconomyTownHallLevel.
    /// </summary>
    void Payday(Colony colony, CitizenRecord bailiff, double now)
    {
        int month = MonthOf(now);
        if (colony.LastPayMonth < 0 || colony.LastPayMonth > month)
        {
            colony.LastPayMonth = month;
            MarkDirty(colony);
            return;
        }
        if (month <= colony.LastPayMonth) return;
        colony.LastPayMonth = month;
        MarkDirty(colony);
        if (!EconomyOpen(colony))
        {
            foreach (var c in Earners(colony)) c.Unpaid = false;
            return;
        }
        if (!HasMint(colony) && TreasuryCoins(colony) == 0) return;

        var earners = Earners(colony).ToList();
        var day = Today(colony);
        if (colony.BailiffCollectsRent && colony.TaxPercent > 0)
        {
            int taxed = 0, payers = 0;
            foreach (var c in earners)
            {
                int tax = c.Purse * colony.TaxPercent / 100;
                if (tax <= 0) continue;
                int stored = StoreCoins(colony, tax);
                c.Purse -= stored;
                taxed += stored;
                payers++;
                if (stored < tax) break; // the treasury is full
            }
            day.Rent += taxed;
            day.Payers += payers;
            if (taxed > 0) LogEvent(colony, "event-bailiff-taxes", bailiff.Name, taxed.ToString(), payers.ToString());
        }

        if (!colony.BailiffPaysWages)
        {
            foreach (var c in earners) c.Unpaid = false;
            return;
        }
        var treasury = TreasuryContainers(colony);
        var money = CurrencyMatch(colony);
        int paid = 0, unpaid = 0, paidCount = 0;
        // The bailiff pays themselves last.
        foreach (var c in earners.OrderBy(c => c.JobCode == Jobs.Bailiff))
        {
            int wage = WageOf(c.JobCode);
            if (wage <= 0)
            {
                c.Unpaid = false;
                continue;
            }
            int got = TakeCoins(treasury, money, wage);
            c.Purse += got;
            paid += got;
            if (got > 0) OnWagePaid(colony, c, got);
            c.Unpaid = got < wage;
            if (c.Unpaid) unpaid++;
            else
            {
                paidCount++;
                AddThought(c, "payday");
            }
        }
        day.Wages += paid;
        day.Unpaid += unpaid;
        if (paid > 0) LogEvent(colony, "event-bailiff-wages", bailiff.Name, paid.ToString(), paidCount.ToString());
        if (unpaid > 0) LogEvent(colony, "event-bailiff-unpaid", unpaid.ToString());
    }

    /// <summary>Called after a settler is paid a wage (already in their purse), for other systems to take a share of it.
    /// Left unimplemented here; a partial method with no body compiles away.</summary>
    partial void OnWagePaid(Colony colony, CitizenRecord c, int wage);

    /// <summary>Takes up to this many coins out of the treasury; returns how many it found.</summary>
    int TakeCoins(List<BlockEntityContainer> treasury, ItemMatch money, int count)
    {
        int got = 0;
        while (got < count)
        {
            var stack = TakeItems(treasury, money, count - got);
            if (stack == null || stack.StackSize <= 0) break;
            got += stack.StackSize;
        }
        return got;
    }

    /// <summary>Puts this many coins in the treasury, the Town Hall's chests first; returns how many found room.</summary>
    int StoreCoins(Colony colony, int count)
    {
        var chests = colony.Buildings.Where(b => b.Type == BuildingTypes.TownHall).SelectMany(b => HutStorage(colony, b)).ToList();
        return PutCoins(colony, chests, count, true);
    }

    /// <summary>Puts this many of the colony's coins into these chests, then the warehouse if asked; returns how many found room.</summary>
    public int PutCoins(Colony colony, List<BlockEntityContainer> chests, int count, bool warehouseToo)
    {
        if (CurrencySample(colony) is not { } sample) return 0;
        int stored = 0;
        while (stored < count)
        {
            var stack = NewCoins(colony, Math.Min(count - stored, Math.Max(1, sample.Collectible.MaxStackSize)))!;
            int want = stack.StackSize;
            foreach (var chest in chests)
            {
                if (stack.StackSize <= 0) break;
                PutItems(chest, stack);
            }
            if (stack.StackSize > 0 && warehouseToo) PutInWarehouse(colony, stack);
            int moved = want - stack.StackSize;
            stored += moved;
            if (moved < want) break; // everything is full
        }
        return stored;
    }

    // ---- The Royal Mint -----------------------------------------------------------------------

    /// <summary>What the Royal Mint does not keep: everything but its hammer, coins, the colony's coin metal and what its recipes take.</summary>
    ItemMatch MintHutJunk(Colony colony, Building hut)
    {
        var recipeInputs = HutRecipeInputs(colony, hut);
        var tools = Config.HutRecipeTools.GetValueOrDefault(BuildingTypes.Mint) ?? Array.Empty<string>();
        var metal = HasOwnCoin(colony) ? CoinMetal(colony) : null;
        return s => !IsCoin(s) && !tools.Contains(FirstPart(s.Collectible.Code)) && !recipeInputs(s) && (metal == null || !s.Collectible.Code.Equals(metal));
    }

    /// <summary>The ingot the colony's own coin is struck from: of the coin's metal (its "metal" variant or last code part), else copper.</summary>
    public AssetLocation CoinMetal(Colony colony)
    {
        var coin = CurrencySample(colony)?.Collectible;
        string? metal = coin?.Variant?["metal"] ?? coin?.Variant?["material"] ?? coin?.LastCodePart();
        var ingot = new AssetLocation("game:ingot-" + metal);
        return metal != null && sapi.World.GetItem(ingot) != null ? ingot : new AssetLocation("game:ingot-copper");
    }

    /// <summary>
    /// The minter's work while the colony has its own coin: one ingot of its metal from the Mint's storage makes
    /// MintCoinsPerIngot copies of the coin. False when there is no ingot there.
    /// </summary>
    public bool StrikeOwnCoins(Colony colony, Building hut)
    {
        var storage = HutStorage(colony, hut).ToList();
        var metal = CoinMetal(colony);
        var ingot = TakeItems(storage, s => s.Collectible.Code.Equals(metal), 1);
        if (ingot == null || ingot.StackSize <= 0) return false;
        int made = PutCoins(colony, storage, Config.MintCoinsPerIngot, true);
        if (made <= 0)
        {
            // Nowhere to put them: the ingot goes back.
            foreach (var chest in storage) if (ingot.StackSize > 0) PutItems(chest, ingot);
            return false;
        }
        return true;
    }

    /// <summary>A line about the treasury, for the hut window.</summary>
    public string MintInfo(Colony colony) => ColonyMoney.Get(sapi, "createacolony:mint-treasury", TreasuryCoins(colony));
}
