using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using ItemMatch = System.Func<Vintagestory.API.Common.ItemStack, bool>;
using Vintagestory.GameContent;

namespace CreateAColony.Colonies;

/// <summary>
/// Warehouse, drop-offs and delivery requests. The warehouse is not one big block: it is every chest
/// (or trunk, or storage vessel) standing near the warehouse marker, so players build it from whatever
/// containers they like and can open it like any other chest.
/// </summary>
public partial class ColonyManager
{
    /// <summary>How far from the warehouse marker a container still counts as part of the warehouse.</summary>
    public const int WarehouseRadius = 5;
    public const int WarehouseHeight = 3;
    /// <summary>A courier who has not finished a request in this many hours lets other couriers take it.</summary>
    const double RequestLeaseHours = 2;

    // ---- Containers -----------------------------------------------------------------------

    public IEnumerable<BlockEntityContainer> WarehouseContainers(Colony colony)
    {
        if (colony.WarehousePos == null) return Enumerable.Empty<BlockEntityContainer>();
        var w = colony.WarehousePos;
        // Drop-off and worker hut chests are never warehouse storage, or couriers would shuffle items in circles.
        var excluded = new HashSet<BlockPos>(DropOffContainers(colony).Select(c => c.Pos));
        foreach (var hut in colony.Buildings.Where(b => HasOwnStorage(b.Type)))
        {
            excluded.UnionWith(HutStorage(colony, hut).Select(c => c.Pos));
        }
        // Nor is a work station's own small storage (the Courier's Station stands in the Warehouse).
        excluded.UnionWith(colony.Buildings.Where(b => b.IsStation).Select(b => b.HutPos));
        // The storage within WarehouseRadius of the Warehouse block, and anywhere inside the Warehouse's own building.
        int x1 = w.X - WarehouseRadius, y1 = w.Y - WarehouseHeight, z1 = w.Z - WarehouseRadius;
        int x2 = w.X + WarehouseRadius, y2 = w.Y + WarehouseHeight, z2 = w.Z + WarehouseRadius;
        if (GetBuilding(colony, w) is { } warehouse && Blueprints.Get(warehouse, Math.Max(1, warehouse.Level)) is { Steps.Count: > 0 })
        {
            var (min, max) = BuildingBox(warehouse);
            x1 = Math.Min(x1, min.X); y1 = Math.Min(y1, min.Y); z1 = Math.Min(z1, min.Z);
            x2 = Math.Max(x2, max.X); y2 = Math.Max(y2, max.Y); z2 = Math.Max(z2, max.Z);
        }
        return FindBlockEntities<BlockEntityContainer>(new Cuboidi(x1, y1, z1, x2, y2, z2))
            .Where(c => !excluded.Contains(c.Pos) && IsStorageVessel(c))
            .OrderBy(c => c.Pos.DistanceSqTo(w.X, w.Y, w.Z))
            .ToList();
    }

    public IEnumerable<BlockEntityContainer> DropOffContainers(Colony colony)
    {
        var ba = sapi.World.BlockAccessor;
        var seen = new HashSet<BlockPos>();
        foreach (var marker in colony.DropOffs)
        {
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dz = -1; dz <= 1; dz++)
            {
                var pos = marker.AddCopy(dx, dy, dz);
                if (ba.GetBlockEntity(pos) is BlockEntityContainer c && IsStorageVessel(c) && seen.Add(c.Pos)) yield return c;
            }
        }
    }

    public int CountItems(IEnumerable<BlockEntityContainer> containers, ItemMatch match)
    {
        return containers.Sum(c => c.Inventory.Where(s => !s.Empty && match(s.Itemstack)).Sum(s => s.StackSize));
    }

    /// <summary>
    /// Takes up to <paramref name="max"/> items of one kind from the containers: the first matching
    /// stack found decides the kind, and further slots of the same kind top it up to a full stack.
    /// Food that can spoil is taken soonest-to-spoil first (ColonyManager.Freshness.cs).
    /// </summary>
    public ItemStack? TakeItems(IEnumerable<BlockEntityContainer> containers, ItemMatch match, int max)
    {
        containers = containers.ToList();
        foreach (var c in containers) SplitOversized(c);
        var found = containers.SelectMany(c => c.Inventory.Where(s => !s.Empty && match(s.Itemstack)).Select(s => (Container: c, Slot: s))).ToList();
        if (found.Any(f => CanSpoil(f.Slot.Itemstack))) found = found.OrderBy(f => HoursUntilSpoiled(f.Slot)).ToList();

        ItemStack? taken = null;
        var changed = new HashSet<BlockEntityContainer>();
        foreach (var (container, slot) in found)
        {
            if (slot.Empty) continue;
            if (taken != null && !taken.Equals(sapi.World, slot.Itemstack, GlobalConstants.IgnoredStackAttributes)) continue;

            int limit = Math.Min(max, slot.Itemstack.Collectible.MaxStackSize) - (taken?.StackSize ?? 0);
            if (limit <= 0) break;
            var part = slot.TakeOut(Math.Min(limit, slot.StackSize));
            slot.MarkDirty();
            changed.Add(container);
            if (taken == null) taken = part;
            else taken.StackSize += part.StackSize;
            if (taken.StackSize >= Math.Min(max, taken.Collectible.MaxStackSize)) break;
        }
        foreach (var container in changed) container.MarkDirty(true);
        return taken;
    }

    /// <summary>
    /// Stacks bigger than the game allows (items spawned with a command or in creative) are split into normal ones,
    /// as far as the container has empty slots (0.200.0). In the overnight test such stacks in a trunk and in a hut
    /// stopped the couriers until Aaron split them by hand.
    /// </summary>
    public void SplitOversized(BlockEntityContainer container)
    {
        bool changed = false;
        foreach (var slot in container.Inventory)
        {
            if (slot.Empty || slot.Itemstack.Collectible == null) continue;
            int max = Math.Max(1, slot.Itemstack.Collectible.MaxStackSize);
            while (slot.StackSize > max)
            {
                var free = container.Inventory.FirstOrDefault(s => s.Empty && s.CanHold(slot));
                if (free == null) break;
                var part = slot.Itemstack.Clone();
                part.StackSize = Math.Min(max, slot.StackSize - max);
                slot.Itemstack.StackSize -= part.StackSize;
                free.Itemstack = part;
                free.MarkDirty();
                slot.MarkDirty();
                changed = true;
            }
        }
        if (changed) container.MarkDirty(true);
    }

    /// <summary>Puts as much of the stack as fits into the container. Returns how many items moved.</summary>
    public int PutItems(BlockEntityContainer container, ItemStack stack)
    {
        // A Food Shelves shelf is stocked segment by segment, as a player stocks it.
        if (IsFoodShelf(container)) return Shelve(container, stack);
        var source = new DummySlot(stack);
        int moved = 0;
        var skip = new List<ItemSlot>();
        while (!source.Empty)
        {
            var target = container.Inventory.GetBestSuitedSlot(source, null, skip)?.slot;
            if (target == null) break;
            int n = source.TryPutInto(sapi.World, target, source.StackSize);
            if (n <= 0)
            {
                skip.Add(target);
                continue;
            }
            moved += n;
            target.MarkDirty();
        }
        stack.StackSize = source.Empty ? 0 : source.StackSize;
        if (moved > 0) container.MarkDirty(true);
        return moved;
    }

    /// <summary>Stores the stack in the warehouse, preferring chests that already hold the same item. Food that can spoil goes in the coolest chests first.</summary>
    public int PutInWarehouse(Colony colony, ItemStack stack)
    {
        int moved = 0;
        var containers = CoolestFirst(WarehouseContainers(colony)
            .OrderByDescending(c => c.Inventory.Any(s => !s.Empty && s.Itemstack.Collectible == stack.Collectible)), stack);
        foreach (var container in containers)
        {
            if (stack.StackSize <= 0) break;
            moved += PutItems(container, stack);
        }
        return moved;
    }

    // ---- Requests -------------------------------------------------------------------------

    public static ItemMatch Matcher(string code)
    {
        // "tool:shovel:1" asks for any shovel of at least that tier, whatever it is made of.
        if (ParseToolCode(code) is { } tool) return stack => ToolFits(stack, tool.Type, tool.Tier);
        // "game:flaxfibers|game:cattailtops" asks for any one of those, and "...{wood=oak}" only in that wood (see WithTypes).
        var (plain, types) = SplitTypes(code);
        var patterns = plain.Split('|').Select(c => new AssetLocation(c)).ToArray();
        return stack => patterns.Any(p => WildcardUtil.Match(p, stack.Collectible.Code)) && TypesMatch(stack, types);
    }

    public DeliveryRequest AddRequest(Colony colony, string itemCode, int quantity, BlockPos dest, string requestedBy)
    {
        int extra = SpareFor(itemCode, quantity, requestedBy);
        var request = new DeliveryRequest
        {
            RequestId = colony.NextRequestId++,
            ItemCode = itemCode,
            Quantity = quantity + extra,
            Extra = extra,
            DestPos = dest.Copy(),
            RequestedBy = requestedBy
        };
        colony.Requests.Add(request);
        MarkDirty(colony);
        return request;
    }

    /// <summary>
    /// A few spare on top of what a hut asks for (Aaron, 0.142.0): a potter who needs 4 firewood asks for 5, in case.
    /// Only for huts' own requests of 2 or more things; tools, Minimum stock and players' requests are left as asked.
    /// </summary>
    int SpareFor(string itemCode, int quantity, string requestedBy)
    {
        if (Config.RequestSparePercent <= 0 || quantity < 2 || !requestedBy.StartsWith("building:") || ParseToolCode(itemCode) != null) return 0;
        int extra = (int)Math.Ceiling(quantity * Config.RequestSparePercent / 100.0);
        return Math.Clamp(extra, 1, Math.Max(1, Config.RequestSpareMax));
    }

    /// <summary>The first of the containers' stacks that matches, the best by rank (lowest first); null when none matches.</summary>
    public ItemStack? BestStack(IEnumerable<BlockEntityContainer> containers, ItemMatch match, System.Func<ItemStack, int> rank) => containers
        .SelectMany(c => c.Inventory).Where(s => !s.Empty && match(s.Itemstack)).Select(s => s.Itemstack!)
        .OrderBy(rank).FirstOrDefault();

    /// <summary>
    /// Gives the courier the oldest request the warehouse (or another hut's spare things) can serve, keeping one
    /// the courier already holds. Returns null when there is nothing in stock for any of them.
    /// </summary>
    public DeliveryRequest? LeaseRequest(Colony colony, CitizenRecord courier)
    {
        double now = sapi.World.Calendar.TotalHours;
        var own = colony.Requests.Find(r => r.LeasedByCitizenId == courier.CitizenId);
        if (own != null) return own;

        var warehouse = WarehouseContainers(colony).Cast<BlockEntityContainer>().ToList();

        foreach (var request in colony.Requests.OrderBy(r => r.RequestId))
        {
            if (request.LeasedByCitizenId != 0 && request.LeaseUntilTotalHours > now) continue;
            if (RequestHeld(request) != null) continue;
            if (CountItems(warehouse, Matcher(request.ItemCode)) <= 0 && SpareForRequest(colony, request) == null) continue;

            request.LeasedByCitizenId = courier.CitizenId;
            request.LeaseUntilTotalHours = now + RequestLeaseHours;
            MarkDirty(colony);
            return request;
        }
        return null;
    }

    /// <summary>Gives a courier another request it brings along on the same trip (same chest to deliver to).</summary>
    public void LeaseAlong(Colony colony, DeliveryRequest request, CitizenRecord courier)
    {
        request.LeasedByCitizenId = courier.CitizenId;
        request.LeaseUntilTotalHours = sapi.World.Calendar.TotalHours + RequestLeaseHours;
        MarkDirty(colony);
    }

    /// <summary>
    /// How many a courier takes for a request (Aaron, 0.160.0): for a worker's own hut it fills up to a full stack
    /// when the warehouse has that many, so a crafter asking for 1 at a time gets a load it can work through. Builders'
    /// work orders, Minimum stock, tools, food that spoils and players' requests get what they asked for.
    /// </summary>
    public int CourierLoad(Colony colony, DeliveryRequest request, ItemStack sample)
    {
        int wanted = request.Remaining;
        if (!Config.CouriersCarryFullLoads || ParseToolCode(request.ItemCode) != null || !request.RequestedBy.StartsWith("building:")
            || !int.TryParse(request.RequestedBy.Substring(9), out int id)) return wanted;
        var hut = colony.Buildings.Find(b => b.BuildingId == id);
        if (hut == null || hut.Type == BuildingTypes.Builder || !IsWorkerHut(hut.Type) || !HutStorage(colony, hut).Any(c => c.Pos.Equals(request.DestPos))) return wanted;
        // Food that spoils is only brought as asked, so it stays in the cooler warehouse.
        if (CanSpoil(sample)) return wanted;
        // Nor anything the hut would send back to the warehouse as not needed.
        if (WorkerHutJunk(colony, hut)(sample)) return wanted;
        return Math.Max(wanted, sample.Collectible.MaxStackSize);
    }

    /// <summary>
    /// Requests a courier could not deliver, until when (game hours) no courier takes them again, and why ("reach",
    /// "full"). Kept in memory only (0.200.0).
    /// </summary>
    readonly Dictionary<int, (double UntilHours, string Why)> heldRequests = new();
    /// <summary>Game hours a request rests after a courier gave up on it, so the same load is not carried back and forth.</summary>
    const double RequestHoldHours = 3;

    /// <summary>A courier gives up on a request for now: it is set free and rests for a few hours.</summary>
    public void HoldRequest(Colony colony, DeliveryRequest request, string why)
    {
        heldRequests[request.RequestId] = (sapi.World.Calendar.TotalHours + RequestHoldHours, why);
        ReleaseRequest(colony, request);
    }

    /// <summary>Why no courier takes this request right now ("reach": no way to the hut, "full": its storage is full), or null.</summary>
    public string? RequestHeld(DeliveryRequest request)
    {
        if (!heldRequests.TryGetValue(request.RequestId, out var held)) return null;
        if (held.UntilHours > sapi.World.Calendar.TotalHours) return held.Why;
        heldRequests.Remove(request.RequestId);
        return null;
    }

    /// <summary>
    /// The hut whose storage a request goes to: the one whose block or chest it names. A builder's requests are
    /// tagged with the building being built, but go to the Builder's Hut. Null for a player's request to a loose chest.
    /// </summary>
    public Building? RequestHut(Colony colony, DeliveryRequest request)
    {
        if (GetBuilding(colony, request.DestPos) is { } named) return named;
        return colony.Buildings.FirstOrDefault(b => !b.IsStation && HasOwnStorage(b.Type) && HutStorage(colony, b).Any(c => c.Pos.Equals(request.DestPos)));
    }

    /// <summary>
    /// Counts things a player put into a hut's storage toward the hut's open requests for them (0.200.0), oldest
    /// first, so couriers stop bringing what is already there.
    /// </summary>
    public void CountTowardRequests(Colony colony, Building hut, string itemCode, int amount)
    {
        foreach (var request in RequestsOf(colony, hut).Where(r => r.ItemCode == itemCode).ToList())
        {
            if (amount <= 0) break;
            int part = Math.Min(amount, request.Remaining);
            RecordDelivery(colony, request, part);
            amount -= part;
        }
    }

    public void ReleaseRequest(Colony colony, DeliveryRequest request)
    {
        request.LeasedByCitizenId = 0;
        MarkDirty(colony);
    }

    /// <summary>Records a delivery, removing the request once it is complete.</summary>
    public void RecordDelivery(Colony colony, DeliveryRequest request, int amount)
    {
        // The Bailiff's wait starts again with every delivery to a hut (0.201.0).
        if (amount > 0 && RequestHut(colony, request) is { } gotIt) lastDelivery[(colony.ColonyId, gotIt.BuildingId)] = sapi.World.Calendar.TotalDays;
        request.Delivered += amount;
        request.LeasedByCitizenId = 0;
        // Once what the hut needs is there, the spare only comes along on the same trip, so nobody waits for it.
        bool done = request.Remaining <= 0 || amount > 0 && request.Remaining <= request.Extra;
        if (done) colony.Requests.Remove(request);
        if (PathDebug) Mod.Logger.Notification("[requests] {0}: {1} x{2} for {3}, {4} more arrived ({5} of {6}){7}", request.RequestId, request.ItemCode,
            request.Quantity, request.RequestedBy, amount, request.Delivered, request.Quantity, done ? ", closed" : "");
        MarkDirty(colony);
    }

    public void CancelRequest(Colony colony, DeliveryRequest request, string why = "cancelled")
    {
        colony.Requests.Remove(request);
        if (PathDebug) Mod.Logger.Notification("[requests] {0}: {1} x{2} for {3} dropped at {4} of {5}: {6}", request.RequestId, request.ItemCode,
            request.Quantity, request.RequestedBy, request.Delivered, request.Quantity, why);
        MarkDirty(colony);
    }

    /// <summary>When (total days) something asked for last reached each hut. Not saved.</summary>
    readonly Dictionary<(string Colony, int Building), double> lastDelivery = new();

    readonly Dictionary<int, double> requestSeen = new();

    /// <summary>
    /// A request for the worker's own hut that has waited WorkerFetchesAfterHours while the warehouse holds the
    /// thing and no courier is bringing it (0.200.0); null when there is none. The worker then goes for it himself.
    /// </summary>
    public DeliveryRequest? OverdueOwnRequest(Colony colony, CitizenRecord worker)
    {
        if (Config.WorkerFetchesAfterHours <= 0 || worker.WorkplacePos == null || colony.WarehousePos == null) return null;
        if (GetBuilding(colony, worker.WorkplacePos) is not { } hut) return null;
        double now = sapi.World.Calendar.TotalHours;
        List<BlockEntityContainer>? warehouse = null;
        foreach (var request in colony.Requests.OrderBy(r => r.RequestId))
        {
            if (request.Remaining <= 0 || !requestSeen.TryGetValue(request.RequestId, out double since) || now - since < Config.WorkerFetchesAfterHours) continue;
            if (request.LeasedByCitizenId != 0 && request.LeasedByCitizenId != worker.CitizenId && request.LeaseUntilTotalHours > now) continue;
            if (RequestHeld(request) == "full" || RequestHut(colony, request) != hut) continue;
            warehouse ??= WarehouseContainers(colony).Cast<BlockEntityContainer>().ToList();
            if (CountItems(warehouse, Matcher(request.ItemCode)) > 0) return request;
        }
        return null;
    }

    /// <summary>
    /// Tidies tool requests (0.200.0): one is dropped when the hut that asked already has such a tool in its storage,
    /// or its only worker carries one. Tools given by hand or picked up left the request open, and a second tool came.
    /// </summary>
    void TidyToolRequests(Colony colony)
    {
        // When each request was first seen, for workers who fetch for themselves after a wait (OverdueOwnRequest).
        double hours = sapi.World.Calendar.TotalHours;
        foreach (var r in colony.Requests) requestSeen.TryAdd(r.RequestId, hours);
        foreach (var request in colony.Requests.Where(r => r.RequestedBy.StartsWith("building:") && r.LeasedByCitizenId == 0).ToList())
        {
            if (ParseToolCode(request.ItemCode) is not { } tool || RequestHut(colony, request) is not { } hut) continue;
            bool fits(ItemStack s) => ToolFits(s, tool.Type, tool.Tier);
            var workers = colony.Citizens.Where(c => c.Alive && c.WorkplacePos != null && c.WorkplacePos.Equals(hut.HutPos)).ToList();
            bool inStorage = CountItems(HutStorage(colony, hut).ToList(), fits) > 0;
            bool carried = workers.Count == 1 && (PackCount(workers[0], fits) > 0
                || fits((sapi.World.GetEntityById(workers[0].EntityId) as EntityAgent)?.RightHandItemSlot?.Itemstack!));
            if (inStorage || carried) CancelRequest(colony, request, inStorage ? "the hut has this tool in its storage" : workers[0].Name + " already carries this tool");
        }
    }
}
