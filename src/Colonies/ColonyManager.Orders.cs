using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Util;
using Vintagestory.GameContent;
using ItemMatch = System.Func<Vintagestory.API.Common.ItemStack, bool>;

namespace CreateAColony.Colonies;

/// <summary>
/// Huts make what other huts are waiting for (Aaron, 0.109.0). When a delivery request (a Builder's or Miner's
/// Required resources, a crafter's inputs, Minimum stock) cannot be served from the warehouse, a hut that can make
/// the thing makes the missing amount on top of what its own tab keeps: the Sawmill saws planks for the mine, a
/// Forester's Hut with firewood on its Crafting recipes tab splits firewood for the smelter. Huts that gather a thing
/// by themselves (the Forester's logs) are named on the Required resources tab too. Couriers bring the things
/// straight from the hut that made them to the hut that asked, or through the warehouse.
/// </summary>
public partial class ColonyManager
{
    /// <summary>What each hut makes for others right now, worked out at most every few seconds.</summary>
    sealed class OrderBook
    {
        public long At;
        /// <summary>How many of a thing (by its key on the hut's tab) a hut makes for others.</summary>
        public readonly Dictionary<(int BuildingId, string Key), int> Orders = new();
        public readonly HashSet<int> Makers = new();
    }

    readonly ConditionalWeakTable<Colony, OrderBook> orderBooks = new();
    const long OrderBookMs = 5000;
    readonly Dictionary<(string Code, string Pattern), bool> gatherOverlap = new();

    /// <summary>One thing a hut can make: its key on the hut's tab, a sample, and the parts of its recipes (null when unknown).</summary>
    sealed record MakerKind(string Key, ItemStack Sample, List<List<CraftingRecipeIngredient>>? Inputs);

    /// <summary>Everything a hut can make for another hut: its crafter tab's things, then its Crafting recipes tab's.</summary>
    IEnumerable<MakerKind> MakerKinds(Colony colony, Building hut) => (hut.Type switch
    {
        BuildingTypes.Sawmill => SawKinds().Select(k => new MakerKind(k.Key, k.Sample, k.Recipes.Select(r => r.Inputs.Select(i => i.Part).ToList()).ToList())),
        BuildingTypes.Blacksmith or BuildingTypes.Armourer or BuildingTypes.Weaponsmith => SmithKindsFor(colony, hut.Type).Select(k => new MakerKind(k.Key, k.Sample, null)),
        BuildingTypes.Craftsman => KnapKinds().Select(k => new MakerKind(k.Key, k.Sample, null)),
        BuildingTypes.Smelter => SmeltKinds().Select(k => new MakerKind(k.Key, k.Sample, null)),
        _ => Enumerable.Empty<MakerKind>()
    }).Concat(HasHutRecipes(hut.Type)
        ? HutRecipeKinds(colony, hut.Type).Select(k => new MakerKind(k.Key, k.Sample, k.Recipes.Select(r => r.Inputs.Select(i => i.Part).ToList()).ToList()))
        : Enumerable.Empty<MakerKind>());

    /// <summary>Huts that are built and have their worker, so they can make or gather something.</summary>
    IEnumerable<Building> WorkingHuts(Colony colony) => colony.Buildings
        .Where(b => Staffable(b) && JobOf(b.Type) is { } job && !IsGuardPost(b.Type) && WorkerAt(colony, b, job) != null)
        .OrderBy(b => b.BuildingId);

    OrderBook Orders(Colony colony)
    {
        long now = sapi.World.ElapsedMilliseconds;
        if (orderBooks.TryGetValue(colony, out var book) && now - book.At < OrderBookMs) return book;
        book = new OrderBook { At = now };
        orderBooks.AddOrUpdate(colony, book);
        if (!Config.HutsMakeForOthers || colony.Requests.Count == 0) return book;

        var makers = WorkingHuts(colony).Select(h => (Hut: h, Kinds: MakerKinds(colony, h).ToList())).Where(m => m.Kinds.Count > 0).ToList();
        if (makers.Count == 0) return book;
        var warehouse = WarehouseContainers(colony).ToList();
        var stacks = WarehouseStacks(warehouse);
        foreach (var request in colony.Requests.OrderBy(r => r.RequestId))
        {
            if (request.Remaining <= 0) continue;
            var match = Matcher(request.ItemCode);
            int shortBy = request.Remaining - CountItems(warehouse, match);
            if (shortBy <= 0) continue;
            if (FindMaker(makers, match, request.RequestedBy, stacks, book) is not { } found) continue;
            var key = (found.Hut.BuildingId, found.Key);
            book.Orders[key] = book.Orders.GetValueOrDefault(key) + shortBy;
            book.Makers.Add(found.Hut.BuildingId);
        }
        return book;
    }

    /// <summary>One stack of each kind of thing in the warehouse, to know what the colony has.</summary>
    static List<ItemStack> WarehouseStacks(IEnumerable<BlockEntityContainer> warehouse) => warehouse
        .SelectMany(c => c.Inventory).Where(s => !s.Empty).Select(s => s.Itemstack!)
        .GroupBy(s => s.Collectible.Code).Select(g => g.First()).ToList();

    /// <summary>
    /// The hut that makes a thing for the hut that asked (never the asking hut itself), and which of its things it
    /// is. When any wood will do, a kind the hut already keeps comes first, then one the warehouse has the wood for.
    /// </summary>
    (Building Hut, string Key)? FindMaker(List<(Building Hut, List<MakerKind> Kinds)> makers, ItemMatch match, string requestedBy,
        List<ItemStack> stacks, OrderBook? book)
    {
        foreach (var (hut, kinds) in makers)
        {
            if (RequestTag(hut) == requestedBy) continue;
            var fits = kinds.Where(k => match(k.Sample)).ToList();
            if (fits.Count == 0) continue;
            var pick = fits.FirstOrDefault(k => book != null && book.Orders.ContainsKey((hut.BuildingId, k.Key)))
                ?? fits.FirstOrDefault(k => KeepOf(hut, k.Key) > 0)
                ?? fits.FirstOrDefault(k => k.Inputs != null && k.Inputs.Any(parts => parts.All(p => stacks.Any(s => p.SatisfiesAsIngredient(s, false)))))
                ?? fits[0];
            return (hut, pick.Key);
        }
        return null;
    }

    int KeepOf(Building hut, string key) => (CraftTabOf(hut.Type) != null ? CraftKeep(hut, key) : 0) + RecipeKeep(hut, key);

    /// <summary>How many of a thing a hut makes for other huts right now.</summary>
    public int OrderedFor(Colony colony, Building hut, string key) => Orders(colony).Orders.GetValueOrDefault((hut.BuildingId, key));

    /// <summary>True when the hut makes something for another hut right now.</summary>
    public bool HasOrders(Colony colony, Building hut) => Orders(colony).Makers.Contains(hut.BuildingId);

    /// <summary>How many of a thing a crafter hut makes: what its tab keeps, plus what other huts are waiting for.</summary>
    public int CraftWant(Colony colony, Building hut, string key) => CraftKeep(hut, key) + OrderedFor(colony, hut, key);

    /// <summary>
    /// How many of a thing a hut's Crafting recipes tab makes: what it keeps, plus what other huts are waiting for.
    /// At a crafter hut, orders for something its crafter tab makes too are made there, not counted twice.
    /// </summary>
    public int RecipeWant(Colony colony, Building hut, string key) => WinterRecipeKeep(hut, key, RecipeKeep(hut, key))
        + (CraftTabOf(hut.Type) != null && CraftKeys(hut).Contains(key) ? 0 : OrderedFor(colony, hut, key));

    /// <summary>
    /// The name of the hut that makes or gathers a thing the hut asking for it is short of, for its Required resources
    /// tab; null when no hut in the colony does.
    /// </summary>
    public string? MakerName(Colony colony, string code, string requestedBy)
    {
        if (!Config.HutsMakeForOthers) return null;
        var match = Matcher(code);
        var huts = WorkingHuts(colony).ToList();
        var makers = huts.Select(h => (Hut: h, Kinds: MakerKinds(colony, h).ToList())).Where(m => m.Kinds.Count > 0).ToList();
        var found = FindMaker(makers, match, requestedBy, new List<ItemStack>(), Orders(colony))?.Hut
            ?? huts.FirstOrDefault(h => RequestTag(h) != requestedBy && Gathers(h.Type, code));
        if (found == null) return null;
        return found.CustomName.Length > 0 ? found.CustomName : Lang.Get("createacolony:building-" + found.Type);
    }

    /// <summary>True when huts of this type gather the thing by themselves (config HutGathers), as a Forester's Hut does logs.</summary>
    bool Gathers(string type, string code)
    {
        if (!Config.HutGathers.TryGetValue(type, out var patterns)) return false;
        foreach (var pattern in patterns)
        {
            if (gatherOverlap.TryGetValue((code, pattern), out bool known))
            {
                if (known) return true;
                continue;
            }
            // Something that fits both: the request and what the hut gathers.
            var asked = Matcher(code);
            var gathered = new AssetLocation(pattern);
            bool overlap = sapi.World.Collectibles.Any(c => c?.Code != null && WildcardUtil.Match(gathered, c.Code) && asked(new ItemStack(c)));
            gatherOverlap[(code, pattern)] = overlap;
            if (overlap) return true;
        }
        return false;
    }

    /// <summary>
    /// A chest at another hut with spare things a request asks for (what that hut made and does not need itself),
    /// so a courier can take them straight there; null when there is none.
    /// </summary>
    public (BlockEntityContainer Chest, ItemMatch Spare)? SpareForRequest(Colony colony, DeliveryRequest request)
    {
        if (!Config.HutsMakeForOthers) return null;
        var match = Matcher(request.ItemCode);
        foreach (var hut in colony.Buildings.Where(b => IsWorkerHut(b.Type) && HasOwnStorage(b.Type) && !b.IsStation && RequestTag(b) != request.RequestedBy))
        {
            var storage = HutStorage(colony, hut).ToList();
            if (storage.Any(c => c.Pos.Equals(request.DestPos))) continue;
            // Only work out what the hut needs when it has something that fits.
            var withIt = storage.Where(c => c.Inventory.Any(s => !s.Empty && match(s.Itemstack))).ToList();
            if (withIt.Count == 0) continue;
            var spare = SpareAt(colony, hut);
            ItemMatch both = s => match(s) && spare(s);
            var chest = withIt.FirstOrDefault(c => c.Inventory.Any(s => !s.Empty && both(s.Itemstack)));
            if (chest != null) return (chest, both);
        }
        return null;
    }
}
