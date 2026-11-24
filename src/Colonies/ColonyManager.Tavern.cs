using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;
using ItemMatch = System.Func<Vintagestory.API.Common.ItemStack, bool>;

namespace CreateAColony.Colonies;

/// <summary>
/// The Tavern, step 2 of the economy update (Aaron, 2026-11-22, design doc "Colony economy design", Step 2 in detail).
///
/// Drinks are the game's own alcohol (config TavernDrinks: cider, mead, spirits, and mods' ale or wine). The Tavern's
/// stock is the drink in liquid containers in its storage (jugs, buckets) and in barrels, jugs and buckets placed within
/// TavernDrinkRadius of it. The Tavern Keeper keeps TavernStockLitres there: it brings containers of drink from the
/// warehouse, and fills empty ones at finished barrels near the Cook's Hut, where the Cook brews with the game's barrel
/// recipes (ColonyManager.Brewing.cs).
///
/// In the evening (TavernOpenHour to bedtime) most settlers go to the Tavern once and sit for a while
/// (AiTaskSettlerTavern). One with spare gears buys a drink, DrinkPrice into the treasury, and may have another; with
/// none it chats. HangoverDrinks in one evening gives a hangover the next day: unhappy and slower at work. The Tavern
/// only serves while a Tavern Keeper works there and the economy has started.
/// </summary>
public partial class ColonyManager
{
    /// <summary>The in-game day each settler last went to the Tavern, and how many drinks it had that evening. Not saved.</summary>
    readonly Dictionary<int, (int Day, int Drinks)> tavernVisits = new();

    /// <summary>True for a liquid that counts as a drink (config TavernDrinks).</summary>
    public bool IsDrink(ItemStack? liquid)
    {
        if (liquid?.Collectible?.Code is not { } code) return false;
        foreach (var pattern in Config.TavernDrinks)
        {
            if (WildcardUtil.Match(new AssetLocation(pattern), code)) return true;
        }
        return false;
    }

    /// <summary>What a liquid container item holds, or null (also null for anything that is not one).</summary>
    public static ItemStack? ContentOf(ItemStack? container) =>
        container?.Collectible is BlockLiquidContainerBase box ? box.GetContent(container) : null;

    /// <summary>True for a jug, bucket or other liquid container item that holds a drink.</summary>
    public bool HoldsDrink(ItemStack? container) => IsDrink(ContentOf(container));

    /// <summary>True for an empty liquid container item the Tavern Keeper can fill (a jug, a bucket); not a barrel.</summary>
    public static bool IsEmptyDrinkContainer(ItemStack? s) =>
        s?.Collectible is BlockLiquidContainerBase box && box is not BlockBarrel && box.CapacityLitres >= 1 && box.GetContent(s) == null;

    static float LitresOf(ItemStack liquid) => liquid.StackSize / (BlockLiquidContainerBase.GetContainableProps(liquid)?.ItemsPerLitre ?? 100f);

    /// <summary>The built Taverns.</summary>
    public IEnumerable<Building> Taverns(Colony colony) => colony.Buildings.Where(b => b.Type == BuildingTypes.Tavern && b.Level >= 1 && !b.Removing);

    /// <summary>True when this Tavern serves: the economy has started and a Tavern Keeper works there.</summary>
    public bool TavernServes(Colony colony, Building tavern) => EconomyOpen(colony) && WorkersAt(colony, tavern).Any(w => !w.IsDowned);

    /// <summary>Barrels, jugs and buckets placed near the Tavern that hold a drink (barrels not sealed).</summary>
    List<BlockEntityLiquidContainer> PlacedDrinks(Building tavern) =>
        AllNear<BlockEntityLiquidContainer>(tavern.HutPos, Math.Max(1, Config.TavernDrinkRadius))
            .Where(b => !(b is BlockEntityBarrel { Sealed: true }) && IsDrink(b.GetContent())).ToList();

    /// <summary>Litres of drink the Tavern has: in its storage and placed near it.</summary>
    public float TavernLitres(Colony colony, Building tavern)
    {
        float litres = PlacedDrinks(tavern).Sum(b => LitresOf(b.GetContent()));
        foreach (var chest in HutStorage(colony, tavern))
        {
            foreach (var slot in chest.Inventory)
            {
                if (slot.Empty || ContentOf(slot.Itemstack) is not { } liquid || !IsDrink(liquid)) continue;
                litres += LitresOf(liquid) * slot.Itemstack.StackSize;
            }
        }
        return litres;
    }

    /// <summary>
    /// Pours one drink (DrinkLitres) out of the Tavern's stock: barrels and jugs placed near it first, then containers in
    /// its storage. Returns the drink's name, or null when there is none.
    /// </summary>
    public string? PourDrink(Colony colony, Building tavern)
    {
        foreach (var placed in PlacedDrinks(tavern))
        {
            if (placed.Block is not BlockLiquidContainerBase box || box.GetContent(placed.Pos) is not { } liquid) continue;
            int items = ItemsFor(liquid, Config.DrinkLitres);
            var taken = box.TryTakeContent(placed.Pos, items);
            if (taken == null || taken.StackSize <= 0) continue;
            placed.MarkDirty(true);
            return taken.GetName();
        }
        foreach (var chest in HutStorage(colony, tavern))
        {
            foreach (var slot in chest.Inventory)
            {
                if (slot.Empty || slot.Itemstack.Collectible is not BlockLiquidContainerBase box || box.GetContent(slot.Itemstack) is not { } liquid || !IsDrink(liquid)) continue;
                // A stack of jugs: pour from one of them.
                var one = slot.Itemstack.StackSize > 1 ? slot.TakeOut(1) : slot.Itemstack;
                var taken = box.TryTakeContent(one, ItemsFor(liquid, Config.DrinkLitres));
                if (one != slot.Itemstack)
                {
                    PutItems(chest, one);
                    if (one.StackSize > 0) sapi.World.SpawnItemEntity(one, chest.Pos.ToVec3d().Add(0.5, 1, 0.5));
                }
                slot.MarkDirty();
                chest.MarkDirty(true);
                if (taken != null && taken.StackSize > 0) return taken.GetName();
            }
        }
        return null;
    }

    static int ItemsFor(ItemStack liquid, float litres) =>
        Math.Max(1, (int)Math.Round(litres * (BlockLiquidContainerBase.GetContainableProps(liquid)?.ItemsPerLitre ?? 100f)));

    /// <summary>True during the Tavern's hours: TavernOpenHour until bedtime.</summary>
    public bool TavernHours(float hour) => hour >= Config.TavernOpenHour && hour < Math.Max(Config.TavernOpenHour, Config.SleepStartHour);

    /// <summary>
    /// The Tavern this settler goes to tonight, or null: during the Tavern's hours, once a day, by TavernVisitChance.
    /// Grown members only; not guards, the Bailiff, the Tavern Keeper (it is at work there), or anyone asleep or downed.
    /// </summary>
    public Building? TavernFor(Colony colony, CitizenRecord c)
    {
        if (!c.Alive || c.Visitor || c.IsChild || c.WaitingForRoom || c.IsDowned) return null;
        if (c.JobCode is Jobs.Guard or Jobs.Bailiff or Jobs.Mayor or Jobs.TavernKeeper) return null;
        var cal = sapi.World.Calendar;
        if (!TavernHours(cal.HourOfDay)) return null;
        int today = (int)cal.TotalDays;
        if (tavernVisits.TryGetValue(c.CitizenId, out var v) && v.Day == today) return null;
        var tavern = Taverns(colony).Where(t => TavernServes(colony, t)).OrderBy(t => t.HutPos.DistanceSqTo(c.HomeBedPos?.X ?? 0, c.HomeBedPos?.Y ?? 0, c.HomeBedPos?.Z ?? 0)).FirstOrDefault();
        if (tavern == null) return null;
        // Whether it goes or not, tonight is decided.
        tavernVisits[c.CitizenId] = (today, 0);
        return sapi.World.Rand.NextDouble() < Config.TavernVisitChance ? tavern : null;
    }

    /// <summary>
    /// At the Tavern: has a drink if it can pay and there is one (and maybe another), else a chat. Marks tonight's visit.
    /// Returns true when it had a drink.
    /// </summary>
    public bool AtTavern(Colony colony, CitizenRecord c, Building tavern)
    {
        int today = (int)sapi.World.Calendar.TotalDays;
        int drinks = tavernVisits.TryGetValue(c.CitizenId, out var v) && v.Day == today ? v.Drinks : 0;
        bool drank = false;
        while (drinks < Math.Max(1, Config.DrinksMax) && SpareGears(c) >= Config.DrinkPrice && TavernServes(colony, tavern))
        {
            if (drinks > 0 && sapi.World.Rand.NextDouble() >= Config.AnotherDrinkChance) break;
            if (PourDrink(colony, tavern) == null) break;
            int price = Math.Max(0, Config.DrinkPrice);
            c.Purse -= price;
            int stored = price > 0 ? StoreCoins(colony, price) : 0;
            if (stored < price) PutCoins(colony, HutStorage(colony, tavern).ToList(), price - stored, false);
            AddStat(tavern, "drinks");
            if (price > 0) AddStat(tavern, "takings", price);
            drinks++;
            drank = true;
        }
        tavernVisits[c.CitizenId] = (today, Math.Max(drinks, 0));
        if (drank)
        {
            AddThought(c, "drink");
            if (drinks >= Math.Max(1, Config.HangoverDrinks)) hangoverDue[c.CitizenId] = today + 1;
        }
        else AddThought(c, "chat");
        MarkDirty(colony);
        return drank;
    }

    /// <summary>Settlers who had too much: the day their hangover starts (when they wake). Not saved.</summary>
    readonly Dictionary<int, int> hangoverDue = new();

    /// <summary>
    /// True while the settler has a hangover. It starts the morning after too many drinks (the thought is added then)
    /// and lasts as long as the "hangover" thought.
    /// </summary>
    public bool HasHangover(CitizenRecord c)
    {
        double now = sapi.World.Calendar.TotalDays;
        if (hangoverDue.TryGetValue(c.CitizenId, out int day) && now >= day && sapi.World.Calendar.HourOfDay >= Config.SleepEndHour)
        {
            hangoverDue.Remove(c.CitizenId);
            AddThought(c, "hangover");
        }
        return c.Thoughts.Any(t => t.Code == "hangover" && t.UntilTotalDays > now);
    }

    /// <summary>Lines about drinks for the Tavern window.</summary>
    List<string> TavernDrinkInfo(Colony colony, Building tavern)
    {
        var lines = new List<string>();
        if (!EconomyOpen(colony)) lines.Add(Lang.Get("createacolony:tavern-drinks-needs-townhall", Config.EconomyTownHallLevel));
        else if (!WorkersAt(colony, tavern).Any()) lines.Add(Lang.Get("createacolony:tavern-drinks-no-keeper"));
        else lines.Add(ColonyMoney.Get(sapi, "createacolony:tavern-drinks", TavernLitres(colony, tavern).ToString("0.#"), Config.DrinkPrice));
        return lines;
    }

    // ---- The Tavern Keeper's fetching --------------------------------------------------------------------

    /// <summary>A warehouse chest with a jug or bucket of drink in it, or null.</summary>
    public BlockEntityContainer? WarehouseDrinkChest(Colony colony, System.Func<BlockPos, bool> skip) =>
        WarehouseContainers(colony).FirstOrDefault(c => !skip(c.Pos) && c.Inventory.Any(s => !s.Empty && HoldsDrink(s.Itemstack)));

    /// <summary>A chest with an empty jug or bucket: the Tavern's storage first, then the warehouse. Null when there is none.</summary>
    public BlockEntityContainer? EmptyContainerChest(Colony colony, Building tavern, System.Func<BlockPos, bool> skip) =>
        HutStorage(colony, tavern).Concat(WarehouseContainers(colony))
            .FirstOrDefault(c => !skip(c.Pos) && c.Inventory.Any(s => !s.Empty && IsEmptyDrinkContainer(s.Itemstack)));

    /// <summary>A barrel near a Cook's Hut whose brew is done and is a drink, or null (ColonyManager.Brewing.cs).</summary>
    public BlockEntityBarrel? FinishedDrinkBarrel(Colony colony, System.Func<BlockPos, bool> skip) =>
        colony.Buildings.Where(b => b.Type == BuildingTypes.Cook && !b.Removing)
            .SelectMany(cook => AllNear<BlockEntityBarrel>(cook.HutPos, Math.Max(1, Config.CookBrewRadius)))
            .FirstOrDefault(b => !skip(b.Pos) && b.CurrentRecipe == null && IsDrink(b.GetContent()));

    /// <summary>
    /// Fills the empty jugs and buckets the keeper carries from a finished drink barrel; opens the barrel first if the
    /// brew left it sealed. Returns how many it filled.
    /// </summary>
    public int FillFromBarrel(Colony colony, CitizenRecord keeper, BlockEntityBarrel barrel)
    {
        if (barrel.Block is not BlockLiquidContainerBase barrelBlock || barrelBlock.GetContent(barrel.Pos) is not { } liquid || !IsDrink(liquid)) return 0;
        if (barrel.Sealed)
        {
            barrel.Sealed = false;
            barrel.MarkDirty(true);
        }
        var items = PackItems(keeper);
        int filled = 0;
        foreach (var jug in items.ToList())
        {
            if (!IsEmptyDrinkContainer(jug) || jug.Collectible is not BlockLiquidContainerBase box) continue;
            if (barrelBlock.GetContent(barrel.Pos) is not { } left || left.StackSize <= 0) break;
            // One container at a time from a stack of empties.
            int n = jug.StackSize;
            for (int i = 0; i < n; i++)
            {
                var one = jug.Clone();
                one.StackSize = 1;
                var content = barrelBlock.GetContent(barrel.Pos);
                if (content == null) break;
                int moved = box.TryPutLiquid(one, content, box.CapacityLitres);
                if (moved <= 0) break;
                barrelBlock.TryTakeContent(barrel.Pos, moved);
                jug.StackSize--;
                items.Add(one);
                filled++;
            }
        }
        if (filled == 0) return 0;
        items.RemoveAll(s => s.StackSize <= 0);
        SavePack(colony, keeper, items);
        barrel.MarkDirty(true);
        return filled;
    }
}
