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
/// Spending, step 1 of the economy update (Aaron, 2026-11-22, design doc "Colony economy design").
///
/// The economy starts at Town Hall level EconomyTownHallLevel (2), and then the colony is given StartingGears (60)
/// once, in its treasury. Settlers keep SavingsKeep gears and spend what is above it at the Market:
///
/// - The Market Trader keeps the Market's storage stocked from the warehouse with the kinds on its For sale tab:
///   cooked meals (not plain bread) and clothes in good condition, up to MarketStockEach of each. It never takes
///   more than half of what the warehouse has, so the basics stay free for settlers with no gears.
/// - A hungry settler who can afford a meal prefers one from the Market; it pays when it eats. One who cannot
///   afford it eats free food elsewhere as before, and is a little put out ("cantafford").
/// - An idle settler who can afford it buys new clothes at the Market when what it wears is worn, and leaves the
///   old piece there; couriers take that to the warehouse for the Tailor.
/// - What a settler pays goes into the treasury (Town Hall chests, then the warehouse). Buying makes it happier.
///
/// The Market only sells while a Market Trader works there and the economy has started.
/// </summary>
public partial class ColonyManager
{
    /// <summary>The kinds a Market sells, in For sale tab order.</summary>
    public static readonly string[] MarketKinds = { "meal", "clothing" };

    /// <summary>The Markets that are built and not being taken down.</summary>
    public IEnumerable<Building> Markets(Colony colony) =>
        colony.Buildings.Where(b => b.Type == BuildingTypes.Market && b.Level >= 1 && !b.Removing);

    /// <summary>True when a built Market has a Market Trader and the economy has started: settlers can shop.</summary>
    public bool MarketOpen(Colony colony) => EconomyOpen(colony) && Markets(colony).Any(m => WorkersAt(colony, m).Any(w => !w.IsDowned));

    /// <summary>The kind of thing the Market would sell this as ("meal", "clothing"), or null.</summary>
    public string? MarketKind(ItemStack? stack)
    {
        if (stack == null) return null;
        if (IsCookedFood(stack) && !(stack.Collectible.Code?.Path ?? "").Contains("bread")) return "meal";
        if (WearsDown(stack) && Condition(stack) >= Math.Min(1f, Config.MarketClothesBelow + 0.2f)) return "clothing";
        return null;
    }

    /// <summary>True when the Market sells this kind (its For sale tab has it on).</summary>
    public bool MarketSells(Colony colony, string kind) => !colony.MarketOff.Contains(kind);

    /// <summary>What the Market charges for one of this kind.</summary>
    public int MarketPrice(Colony colony, string kind) =>
        Math.Max(0, colony.MarketPrices.TryGetValue(kind, out int p) ? p : Config.MarketPrices.TryGetValue(kind, out int d) ? d : 1);

    /// <summary>Gears a settler may spend: what its purse holds above SavingsKeep.</summary>
    public int SpareGears(CitizenRecord c) => Math.Max(0, c.Purse - Math.Max(0, Config.SavingsKeep));

    /// <summary>True when this settler may buy one of this kind at the Market now.</summary>
    public bool CanBuy(Colony colony, CitizenRecord c, string kind) =>
        c.Alive && !c.Visitor && !c.IsChild && !c.WaitingForRoom && MarketSells(colony, kind) && MarketOpen(colony) && SpareGears(c) >= MarketPrice(colony, kind);

    /// <summary>Where the Markets' storage stands.</summary>
    HashSet<BlockPos> MarketChests(Colony colony) =>
        colony.Buildings.Where(b => b.Type == BuildingTypes.Market).SelectMany(b => HutStorage(colony, b)).Select(c => c.Pos).ToHashSet();

    /// <summary>True when this container is part of a Market's storage: what is in it is for sale, not free.</summary>
    public bool IsMarketStore(Colony colony, BlockEntityContainer container) =>
        colony.Buildings.Any(b => b.Type == BuildingTypes.Market) && MarketChests(colony).Contains(container.Pos);

    /// <summary>The Market whose storage holds this container, or null.</summary>
    Building? MarketOf(Colony colony, BlockEntityContainer container) =>
        colony.Buildings.FirstOrDefault(b => b.Type == BuildingTypes.Market && HutStorage(colony, b).Any(c => c.Pos.Equals(container.Pos)));

    /// <summary>
    /// The settler pays for one of this kind: out of its purse into the treasury, and it is a little happier.
    /// Gears that find no room in the treasury go into the Market's own storage.
    /// </summary>
    public void PayAtMarket(Colony colony, CitizenRecord c, string kind, BlockEntityContainer at)
    {
        int price = Math.Min(MarketPrice(colony, kind), c.Purse);
        c.Purse -= price;
        int stored = price > 0 ? StoreCoins(colony, price) : 0;
        if (stored < price) PutCoins(colony, new List<BlockEntityContainer> { at }, price - stored, false);
        AddThought(c, "bought");
        if (MarketOf(colony, at) is { } market)
        {
            AddStat(market, "sold-" + kind);
            if (price > 0) AddStat(market, "takings", price);
        }
        MarkDirty(colony);
    }

    /// <summary>A settler wanted something from the Market and could not pay for it.</summary>
    public void CantAfford(CitizenRecord c)
    {
        if (c.Thoughts.Any(t => t.Code == "cantafford" && t.UntilTotalDays > sapi.World.Calendar.TotalDays)) return;
        AddThought(c, "cantafford");
    }

    /// <summary>
    /// At a Market, couriers take away what it does not sell: the old clothes settlers leave there, things turned
    /// off on the For sale tab. Gears stay (they are the Market's till when the treasury is full).
    /// </summary>
    ItemMatch MarketHutJunk(Colony colony)
    {
        var money = CurrencyMatch(colony);
        return s => !money(s) && (MarketKind(s) is not { } kind || !MarketSells(colony, kind));
    }

    /// <summary>How many of this kind the Market's storage holds.</summary>
    public int MarketStock(Colony colony, Building market, string kind) => CountItems(HutStorage(colony, market), s => MarketKind(s) == kind);

    /// <summary>Lines for the Market's Main tab.</summary>
    List<string> MarketInfo(Colony colony, Building market)
    {
        var lines = new List<string>();
        if (!EconomyOpen(colony)) lines.Add(Lang.Get("createacolony:market-needs-townhall", Config.EconomyTownHallLevel));
        foreach (var kind in MarketKinds)
        {
            lines.Add(MarketSells(colony, kind)
                ? ColonyMoney.Get(sapi, "createacolony:market-line", Lang.Get("createacolony:market-kind-" + kind), MarketStock(colony, market, kind), MarketPrice(colony, kind))
                : Lang.Get("createacolony:market-line-off", Lang.Get("createacolony:market-kind-" + kind)));
        }
        return lines;
    }

    /// <summary>For sale tab (officers): "kind|on", "kind|off" or "kind|price".</summary>
    public string? SetMarketRule(Colony colony, string text)
    {
        var parts = text.Split('|');
        if (parts.Length != 2 || !MarketKinds.Contains(parts[0])) return null;
        string kind = parts[0];
        if (parts[1] == "on") colony.MarketOff.Remove(kind);
        else if (parts[1] == "off") { if (!colony.MarketOff.Contains(kind)) colony.MarketOff.Add(kind); }
        else if (int.TryParse(parts[1], out int price)) colony.MarketPrices[kind] = Math.Clamp(price, 0, 99);
        MarkDirty(colony);
        return null;
    }

    // ---- Starting gears --------------------------------------------------------------------------

    /// <summary>
    /// Part of the money tick: when the economy starts (Town Hall level EconomyTownHallLevel), the treasury is given
    /// StartingGears once, as many as fit now and the rest as room is made.
    /// </summary>
    void GiveStartingGears(Colony colony)
    {
        if (!EconomyOpen(colony)) return;
        if (colony.StartingGearsLeft < 0)
        {
            colony.StartingGearsLeft = Math.Max(0, Config.StartingGears);
            MarkDirty(colony);
        }
        if (colony.StartingGearsLeft <= 0) return;
        int given = StoreCoins(colony, colony.StartingGearsLeft);
        if (given <= 0) return;
        colony.StartingGearsLeft -= given;
        LogEvent(colony, "event-starting-gears", given.ToString());
        MarkDirty(colony);
    }

    // ---- Clothes from the Market -----------------------------------------------------------------

    /// <summary>
    /// A Market chest with clothes this settler would buy: it can afford them, and what it wears in that slot is worn
    /// (below MarketClothesBelow), ragged, or too cold. Null when there is none.
    /// </summary>
    BlockEntityContainer? MarketClothesChest(Colony colony, CitizenRecord c, ItemStack?[] worn, bool cold)
    {
        if (!Config.SettlersChangeClothes || !CanBuy(colony, c, "clothing")) return null;
        foreach (var market in Markets(colony))
        {
            foreach (var chest in HutStorage(colony, market))
            {
                if (chest.Inventory.Any(s => !s.Empty && MarketKind(s.Itemstack) == "clothing" && BoughtPiece(worn, s.Itemstack, cold) >= 0)) return chest;
            }
        }
        // Worn clothes, nothing to buy: only a pity if a better piece would have been there.
        return null;
    }

    /// <summary>The clothes slot a bought piece would go in, or -1: worn or ragged there, or warmer when cold.</summary>
    int BoughtPiece(ItemStack?[] worn, ItemStack piece, bool cold)
    {
        int slot = ClothesSlotOf(piece);
        if (slot < 0 || slot >= worn.Length) return -1;
        var have = worn[slot];
        if (have != null && (IsJewellery(have) || IsArmor(have))) return -1;
        if (have == null) return cold ? slot : -1;
        if (Condition(have) < Config.MarketClothesBelow && Condition(piece) > Condition(have) + 0.2f) return slot;
        if (cold && WarmthOf(piece) > WarmthOf(have) + 0.5f) return slot;
        return -1;
    }

    /// <summary>True when the settler wears something worn enough to want new clothes from the Market.</summary>
    bool WantsNewClothes(ItemStack?[] worn) => worn.Any(s => s != null && WearsDown(s) && Condition(s) < Config.MarketClothesBelow);

    /// <summary>
    /// At a Market chest: buys the pieces it wants while it can pay, one per slot, and leaves what it wore there.
    /// True when it bought anything.
    /// </summary>
    bool BuyClothesAt(Colony colony, CitizenRecord c, BlockEntityContainer chest)
    {
        var worn = WornClothes(c);
        bool cold = IsCold(c);
        bool changed = false;
        foreach (var slot in chest.Inventory)
        {
            if (slot.Empty || MarketKind(slot.Itemstack) != "clothing") continue;
            if (!CanBuy(colony, c, "clothing")) break;
            int to = BoughtPiece(worn, slot.Itemstack, cold);
            if (to < 0) continue;
            var piece = slot.TakeOut(1);
            slot.MarkDirty();
            var old = worn[to];
            worn[to] = piece;
            changed = true;
            PayAtMarket(colony, c, "clothing", chest);
            if (old == null) continue;
            PutItems(chest, old);
            if (old.StackSize > 0) AddToPack(colony, c, old, false);
            if (old.StackSize > 0) sapi.World.SpawnItemEntity(old, chest.Pos.ToVec3d().Add(0.5, 1, 0.5));
        }
        if (!changed) return false;
        chest.MarkDirty(true);
        SaveClothes(colony, c, worn);
        coldCache.Remove(c.CitizenId);
        return true;
    }
}
