using System.Linq;
using System.Collections.Generic;
using CreateAColony.Colonies;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace CreateAColony.Settlers;

/// <summary>
/// The courier's working day, one trip at a time: deliver what it is carrying, otherwise fetch the
/// oldest delivery request the warehouse can serve, otherwise empty a drop-off chest into the
/// warehouse, otherwise fill the Food Shelves shelves of a Cook's Hut with food from the warehouse, otherwise
/// move food that can spoil to cooler storage such as a cellar. It carries
/// as much as its inventory holds, and what it carries is saved on its record,
/// so nothing is lost if the trip is interrupted by night, hunger or a storm; the next trip finishes it.
/// Settlers without a job help out the same way when their home needs nothing (JoblessHelpHauling): they take
/// deliveries to huts and builders, empty drop-off chests and clear huts out.
/// </summary>
public class AiTaskSettlerCourier : SettlerTaskBase
{
    const long IdleRetryMs = 5_000;
    const long BlockedRetryMs = 60_000;

    enum Step { Pickup, Deliver, Sweep }

    Step step;
    BlockPos? target;
    int requestId;
    System.Func<ItemStack, bool> pickupFilter = _ => true;
    long nextSearchMs;
    /// <summary>Delivering a settler's own leftovers to its hut rather than the warehouse.</summary>
    bool toHut;
    /// <summary>When a settler without a hut started carrying things, so it saves them up for one trip to the warehouse.</summary>
    long carryingSinceMs = -1;
    /// <summary>The kitchen shelf the food picked up is for, and how much it takes.</summary>
    BlockPos? shelfTarget;
    int shelfWant;
    /// <summary>The cooler storage (a cellar chest) the food picked up is for.</summary>
    BlockPos? coolTarget;
    /// <summary>A torch in a building that went out, which the lit torch picked up replaces.</summary>
    BlockPos? torchTarget;
    /// <summary>Torches other couriers are already bringing a new one to.</summary>
    static readonly System.Collections.Generic.HashSet<BlockPos> torchesTaken = new();
    /// <summary>The request's things come from another hut's storage, which only gives what it does not need (pickupFilter).</summary>
    bool fromHut;
    /// <summary>Where the load being carried was picked up, for the Courier's Station.</summary>
    BlockPos? pickedFrom;
    /// <summary>
    /// Deliveries to one place that went wrong in a row, and why ("reach" or "full"). After DeliveryTries the load
    /// goes back to the warehouse and the requests are set free (0.200.0): in the overnight test a courier held five
    /// stacks for three game days, trying the same hut every minute, with the status "taking a break".
    /// </summary>
    int deliveryFails;
    BlockPos? failedDest;
    string? trouble;
    const int DeliveryTries = 4;
    /// <summary>Things on the ground this courier walked to and could not reach (0.200.0), and when it may look for more.</summary>
    readonly System.Collections.Generic.HashSet<long> sweepSkip = new();
    long sweepItemId;
    long nextSweepMs;
    const long SweepEveryMs = 15_000;
    /// <summary>A worker fetching for his own hut because no courier came (0.200.0), and when he may check again.</summary>
    bool selfFetch;
    long nextSelfFetchMs;
    const long SelfFetchEveryMs = 20_000;

    public AiTaskSettlerCourier(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig) : base(entity, taskConfig, aiConfig) { }

    public override bool ShouldExecute()
    {
        if (manager == null || entity.World.ElapsedMilliseconds < nextSearchMs) return false;
        var citizen = Citizen;
        var colony = Colony;
        // A settler whose courier job was taken away still puts its last load back first.
        // Builders, foresters and farmers carry their own tools and supplies, so their loads are left to them.
        if (citizen == null || colony == null || manager.IsBrokenDown(citizen)) return false;
        if (Jobs.CarriesOwnLoad(citizen.JobCode) && !manager.WinterFarmerHelps(colony, citizen)) return TrySelfFetch(colony, citizen);
        if (citizen.JobCode != Jobs.Courier && !Helps(citizen) && manager.PackEmpty(citizen))
        {
            carryingSinceMs = -1;
            return false;
        }
        if (SavingUp(colony, citizen))
        {
            nextSearchMs = entity.World.ElapsedMilliseconds + IdleRetryMs;
            return false;
        }

        nextSearchMs = entity.World.ElapsedMilliseconds + IdleRetryMs;
        return Plan(colony, citizen);
    }

    /// <summary>
    /// A settler without a job hut does not walk to the warehouse for every item it picks up: it waits until its
    /// pack is full or it has carried things for LooseItemsDepositSeconds. With no warehouse it keeps them.
    /// </summary>
    bool SavingUp(Colony colony, CitizenRecord citizen)
    {
        if (citizen.JobCode == Jobs.Courier || OwnHutChest(colony, citizen) != null) return false;
        if (manager!.PackEmpty(citizen))
        {
            carryingSinceMs = -1; // a helper with empty hands goes straight to hauling
            return false;
        }
        if (colony.Requests.Exists(r => r.LeasedByCitizenId == citizen.CitizenId)) return false;
        long now = entity.World.ElapsedMilliseconds;
        if (carryingSinceMs < 0) carryingSinceMs = now;
        if (colony.WarehousePos == null) return true;
        return !manager.PackFull(citizen) && now - carryingSinceMs < manager.Config.LooseItemsDepositSeconds * 1000;
    }

    /// <summary>A settler without a job who helps the couriers.</summary>
    bool Helps(CitizenRecord citizen) => manager!.Config.JoblessHelpHauling && ColonyManager.IsJobless(citizen)
        || Colony is { } colony && manager.WinterFarmerHelps(colony, citizen);

    BlockPos? OwnHutChest(Colony colony, CitizenRecord citizen) =>
        citizen.WorkplacePos != null && manager!.GetBuilding(colony, citizen.WorkplacePos) is { } hut && ColonyManager.HasOwnStorage(hut.Type)
            ? manager.HutStorage(colony, hut).FirstOrDefault()?.Pos : null;

    /// <summary>
    /// A worker with his own hut (0.200.0): when the hut has waited a game hour for something the warehouse holds and
    /// no courier is bringing it, he walks to the warehouse and carries it to his hut himself. Only what the request
    /// asks for is touched; his tools and supplies stay in his pack.
    /// </summary>
    bool TrySelfFetch(Colony colony, CitizenRecord citizen)
    {
        long nowMs = entity.World.ElapsedMilliseconds;
        if (nowMs < nextSelfFetchMs) return false;
        nextSelfFetchMs = nowMs + SelfFetchEveryMs;
        requestId = 0;
        toHut = false;
        shelfTarget = null;
        coolTarget = null;
        fromHut = false;
        pickedFrom = null;
        selfFetch = false;
        ReleaseTorch();
        // A fetch that was cut short (night, a meal): carry on with it.
        var own = colony.Requests.Find(r => r.LeasedByCitizenId == citizen.CitizenId);
        if (own != null)
        {
            if (manager!.PackCount(citizen, ColonyManager.Matcher(own.ItemCode)) > 0)
            {
                requestId = own.RequestId;
                selfFetch = true;
                return Go(Step.Deliver, own.DestPos);
            }
            manager.ReleaseRequest(colony, own);
        }
        if (manager!.OverdueOwnRequest(colony, citizen) is not { } mine) return false;
        var source = manager.SoonestToSpoil(manager.WarehouseContainers(colony), ColonyManager.Matcher(mine.ItemCode));
        if (source == null) return false;
        manager.LeaseAlong(colony, mine, citizen);
        requestId = mine.RequestId;
        selfFetch = true;
        pickupFilter = _ => true;
        if (manager.PathDebug) manager.Mod.Logger.Notification("[requests] {0}: {1} waited over {2:0.#} h for {3}, the warehouse has it and no courier came, so goes for it",
            mine.RequestId, citizen.Name, manager.Config.WorkerFetchesAfterHours, mine.ItemCode);
        return Go(Step.Pickup, source.Pos);
    }

    bool Plan(Colony colony, CitizenRecord citizen)
    {
        requestId = 0;
        selfFetch = false;
        toHut = false;
        shelfTarget = null;
        coolTarget = null;
        fromHut = false;
        pickedFrom = null;
        ReleaseTorch();
        if (!manager!.PackEmpty(citizen))
        {
            // Finish the delivery we were on, or bring the load back to the warehouse.
            var own = colony.Requests.Find(r => r.LeasedByCitizenId == citizen.CitizenId);
            if (own != null && manager.PackCount(citizen, ColonyManager.Matcher(own.ItemCode)) > 0)
            {
                requestId = own.RequestId;
                return Go(Step.Deliver, own.DestPos);
            }
            // Not a courier: what it carries goes to its own hut's storage when it has one.
            if (citizen.JobCode != Jobs.Courier && OwnHutChest(colony, citizen) is { } hutChest)
            {
                toHut = true;
                return Go(Step.Deliver, hutChest);
            }
            return Go(Step.Deliver, manager.WarehouseContainers(colony).FirstOrDefault()?.Pos);
        }
        if (citizen.JobCode != Jobs.Courier && !Helps(citizen)) return false;

        var request = manager.LeaseRequest(colony, citizen);
        if (request != null)
        {
            var match = ColonyManager.Matcher(request.ItemCode);
            // The food that will spoil soonest goes first; anything else from the nearest chest that has it.
            var source = manager.SoonestToSpoil(manager.WarehouseContainers(colony), match);
            // Not in the warehouse: straight from the hut that made it (ColonyManager.Orders.cs).
            if (source == null && manager.SpareForRequest(colony, request) is { } spare)
            {
                source = spare.Chest;
                pickupFilter = spare.Spare;
                fromHut = true;
            }
            if (source == null)
            {
                manager.ReleaseRequest(colony, request);
                return false;
            }
            requestId = request.RequestId;
            return Go(Step.Pickup, source.Pos);
        }

        if (colony.WarehousePos == null) return false;
        pickupFilter = _ => true;
        var dropOff = manager.DropOffContainers(colony).FirstOrDefault(c => c.Inventory.Any(s => !s.Empty));
        if (dropOff != null) return Go(Step.Pickup, dropOff.Pos);

        // Clear out builders' chests: rubble from site clearing and materials no longer needed.
        if (manager.FindHutJunk(colony) is { } junk)
        {
            pickupFilter = junk.IsJunk;
            return Go(Step.Pickup, junk.Chest.Pos);
        }

        // Stock the kitchen shelves with food from the warehouse (ColonyManager.FoodShelves.cs).
        if (manager.FindShelfRestock(colony) is { } restock)
        {
            pickupFilter = restock.Match;
            shelfTarget = restock.Shelf.Pos.Copy();
            shelfWant = restock.Amount;
            return Go(Step.Pickup, restock.Source.Pos);
        }

        // Move food that can spoil from warm storage to a cooler one, such as a cellar (ColonyManager.Freshness.cs).
        if (manager.FindCoolingMove(colony) is { } cool)
        {
            pickupFilter = cool.Match;
            coolTarget = cool.To.Pos.Copy();
            shelfWant = cool.Amount;
            return Go(Step.Pickup, cool.From.Pos);
        }

        // Put a lit torch up where one in a building went out (ColonyManager.Torches.cs).
        if (manager.FindTorchToRelight(colony, torchesTaken) is { } relight)
        {
            pickupFilter = ColonyManager.IsTorch;
            torchTarget = relight.Torch.Copy();
            torchesTaken.Add(torchTarget);
            shelfWant = 1;
            return Go(Step.Pickup, relight.Source.Pos);
        }

        // Nothing to carry: fetch what lies on the ground inside the colony (0.200.0).
        long nowMs = entity.World.ElapsedMilliseconds;
        if (citizen.JobCode == Jobs.Courier && nowMs >= nextSweepMs)
        {
            nextSweepMs = nowMs + SweepEveryMs;
            if (manager.LooseItemInColony(colony, citizen, entity, manager.Config.CourierSweepRange, sweepSkip) is { } loose)
            {
                sweepItemId = loose.EntityId;
                return Go(Step.Sweep, loose.Pos.AsBlockPos);
            }
        }
        return false;
    }

    void ReleaseTorch()
    {
        if (torchTarget != null) torchesTaken.Remove(torchTarget);
        torchTarget = null;
    }

    bool Go(Step next, BlockPos? pos)
    {
        if (pos == null) return false;
        step = next;
        target = pos.Copy();
        return true;
    }

    public override void StartExecute()
    {
        base.StartExecute();
        if (step == Step.Sweep) WalkTo(target!.ToVec3d().Add(0.5, 0, 0.5), 1.5f);
        else WalkTo(StandBy(target!), 1.5f);
    }

    /// <summary>
    /// Where to walk to for a chest or hut block: a free spot right beside it, not the block itself (0.200.0),
    /// so a hut block against a wall is walked to from inside its room.
    /// </summary>
    Vec3d StandBy(BlockPos chest) => manager!.FreeSpotNear(entity, chest, 1) ?? chest.ToVec3d().Add(0.5, 0, 0.5);

    /// <summary>A delivery went wrong. True once it has gone wrong often enough: the load goes back and the requests are set free.</summary>
    bool DeliveryFailed(Colony colony, CitizenRecord citizen, BlockPos dest, string why)
    {
        if (failedDest == null || !failedDest.Equals(dest)) deliveryFails = 0;
        failedDest = dest.Copy();
        deliveryFails++;
        trouble = why;
        if (deliveryFails < DeliveryTries) return false;
        deliveryFails = 0;
        foreach (var r in colony.Requests.Where(r => r.LeasedByCitizenId == citizen.CitizenId && r.DestPos.Equals(dest)).ToList())
        {
            manager!.HoldRequest(colony, r, why);
        }
        manager!.NoteWalk(citizen, entity, "Courier", why == "full" ? "storage full, load taken back" : "no way in, load taken back", dest.ToVec3d());
        if (manager.PathDebug) manager.Mod.Logger.Notification("[courier] {0} takes its load back: {1} at {2}", citizen.Name, why == "full" ? "the storage is full" : "no way to the hut", dest);
        requestId = 0;
        return true;
    }

    public override bool ContinueExecute(float dt)
    {
        if (PathFailed && step == Step.Sweep)
        {
            // No way to that thing: leave it, look for another.
            sweepSkip.Add(sweepItemId);
            nextSearchMs = entity.World.ElapsedMilliseconds + IdleRetryMs;
            return false;
        }
        if (PathFailed)
        {
            nextSearchMs = entity.World.ElapsedMilliseconds + BlockedRetryMs;
            if (step == Step.Deliver && Colony is { } c && Citizen is { } who && !manager!.PackEmpty(who)
                && c.Requests.Find(r => r.RequestId == requestId) is { } stuck && DeliveryFailed(c, who, stuck.DestPos, "reach"))
            {
                nextSearchMs = 0; // straight back to the warehouse with it
            }
            return false;
        }
        if (!Arrived)
        {
            KeepWalking();
            return true;
        }
        StopWalking();

        var colony = Colony;
        var citizen = Citizen;
        if (colony == null || citizen == null) return false;

        if (step == Step.Sweep)
        {
            if (manager!.PickUpNearby(colony, citizen, entity, 3f) == 0) sweepSkip.Add(sweepItemId);
            // More lying close by comes along on the same round.
            if (manager.LooseItemInColony(colony, citizen, entity, 16, sweepSkip) is { } more)
            {
                sweepItemId = more.EntityId;
                target = more.Pos.AsBlockPos;
                WalkTo(target.ToVec3d().Add(0.5, 0, 0.5), 1.5f);
                return true;
            }
            nextSearchMs = 0; // to the warehouse with it
            return false;
        }

        if (step == Step.Pickup)
        {
            if (!Pickup(colony, citizen)) return false;
            step = Step.Deliver;
            // A torch may hang high on a wall: near enough to reach up to it.
            if (torchTarget != null) WalkTo(target!.ToVec3d().Add(0.5, 0, 0.5), 2.5f);
            else WalkTo(StandBy(target!), 1.5f);
            return true;
        }

        Deliver(colony, citizen);
        return false;
    }

    /// <summary>Fills the inventory from the container at the target, then points the target at its destination.</summary>
    bool Pickup(Colony colony, CitizenRecord citizen)
    {
        if (entity.World.BlockAccessor.GetBlockEntity(target!) is not BlockEntityContainer container) return false;
        var request = colony.Requests.Find(r => r.RequestId == requestId);

        var match = request != null && !fromHut ? ColonyManager.Matcher(request.ItemCode) : pickupFilter;
        int wanted = request?.Remaining ?? (shelfTarget != null || coolTarget != null || torchTarget != null ? shelfWant : int.MaxValue);
        // For a request from the warehouse, every warehouse chest counts, not only the one walked to (Aaron, 0.160.0).
        bool fullLoads = manager!.Config.CouriersCarryFullLoads && request != null && !fromHut;
        var warehouse = fullLoads ? manager.WarehouseContainers(colony).ToList() : null;
        var sources = warehouse != null ? new[] { container }.Concat(warehouse.Where(c => c != container)).ToList() : new List<BlockEntityContainer> { container };
        if (fullLoads && sources.SelectMany(c => c.Inventory).FirstOrDefault(s => !s.Empty && match(s.Itemstack))?.Itemstack is { } sample)
        {
            wanted = manager.CourierLoad(colony, request!, sample);
        }
        int taken = TakeInto(colony, citizen, sources, match, wanted);
        // Other requests for the same chest come along on the same trip, as far as the pack holds them.
        if (fullLoads && taken > 0)
        {
            double now = entity.World.Calendar.TotalHours;
            foreach (var other in colony.Requests.Where(r => r != request && r.Remaining > 0 && r.DestPos.Equals(request!.DestPos)
                && (r.LeasedByCitizenId == 0 || r.LeasedByCitizenId == citizen.CitizenId || r.LeaseUntilTotalHours <= now)).OrderBy(r => r.RequestId).ToList())
            {
                if (manager.PackFull(citizen)) break;
                var otherMatch = ColonyManager.Matcher(other.ItemCode);
                var otherSample = warehouse!.SelectMany(c => c.Inventory).FirstOrDefault(s => !s.Empty && otherMatch(s.Itemstack))?.Itemstack;
                if (otherSample == null) continue;
                if (TakeInto(colony, citizen, warehouse!, otherMatch, manager.CourierLoad(colony, other, otherSample)) > 0) manager.LeaseAlong(colony, other, citizen);
            }
        }

        if (taken == 0)
        {
            if (request != null) manager!.ReleaseRequest(colony, request);
            return false;
        }
        pickedFrom = target!.Copy();
        trouble = null;
        var destination = request != null ? request.DestPos : shelfTarget ?? coolTarget ?? torchTarget ?? manager!.WarehouseContainers(colony).FirstOrDefault()?.Pos;
        if (destination == null) return false; // warehouse removed mid-trip; the load waits until one exists
        target = destination.Copy();
        return true;
    }

    /// <summary>Moves up to wanted matching things from the containers into the pack, one stack at a time; what does not fit goes back.</summary>
    int TakeInto(Colony colony, CitizenRecord citizen, List<BlockEntityContainer> sources, System.Func<ItemStack, bool> match, int wanted)
    {
        int taken = 0;
        while (taken < wanted)
        {
            var part = manager!.TakeItems(sources, match, wanted - taken);
            if (part == null) break;
            int moved = manager.AddToPack(colony, citizen, part, toolBar: false); // a load to deliver, not its own tools
            taken += moved;
            if (part.StackSize > 0)
            {
                // Back where the stack came from: the first chest that takes it.
                foreach (var c in sources)
                {
                    if (part.StackSize <= 0) break;
                    manager.PutItems(c, part);
                }
                break;
            }
        }
        return taken;
    }

    void Deliver(Colony colony, CitizenRecord citizen)
    {
        if (manager!.PackEmpty(citizen)) return;

        var request = colony.Requests.Find(r => r.RequestId == requestId);
        bool blocked;
        int stacksBefore = manager.PackItems(citizen).Count;
        bool wasFull = manager.PackFull(citizen);
        if (request != null)
        {
            if (entity.World.BlockAccessor.GetBlockEntity(request.DestPos) is not BlockEntityContainer dest)
            {
                // The chest that asked for this is gone: drop the request, the load goes back to the warehouse.
                manager.CancelRequest(colony, request, "the chest it was for is gone");
                nextSearchMs = 0;
                return;
            }
            blocked = false;
            // The chest that asked first, then the rest of that hut's storage (0.200.0): a full hut block no
            // longer stops a delivery while the chests beside it have room.
            var into = new List<BlockEntityContainer> { dest };
            if (manager.RequestHut(colony, request) is { } destHut) into.AddRange(manager.HutStorage(colony, destHut).Where(c => !c.Pos.Equals(dest.Pos)));
            // This request and any others for the same chest the courier brought along.
            var along = colony.Requests.Where(r => r != request && r.LeasedByCitizenId == citizen.CitizenId && r.DestPos.Equals(request.DestPos)).ToList();
            var destPos = request.DestPos.Copy();
            foreach (var r in along.Prepend(request))
            {
                var match = ColonyManager.Matcher(r.ItemCode);
                int before = manager.PackCount(citizen, match);
                manager.UnloadPack(colony, citizen, into, match);
                int moved = before - manager.PackCount(citizen, match);
                bool left = manager.PackCount(citizen, match) > 0;
                // Nothing fitted: the request stays this courier's, so the next try (or the way back) knows whose load it is.
                if (moved > 0 || !left) manager.RecordDelivery(colony, r, moved);
                blocked |= left;
            }
            if (!blocked)
            {
                deliveryFails = 0;
                trouble = null;
            }
            else if (DeliveryFailed(colony, citizen, destPos, "full"))
            {
                // Often enough: back to the warehouse with what is left.
                manager.GainHaulXp(colony, citizen, stacksBefore - manager.PackItems(citizen).Count, wasFull);
                nextSearchMs = 0;
                return;
            }
        }
        else if (shelfTarget != null)
        {
            // Whatever the shelf does not take goes back to the warehouse on the next trip.
            if (entity.World.BlockAccessor.GetBlockEntity(shelfTarget) is BlockEntityContainer shelf && ColonyManager.IsFoodShelf(shelf))
            {
                manager.ShelveFromPack(colony, citizen, shelf, pickupFilter);
            }
            shelfTarget = null;
            blocked = false;
        }
        else if (torchTarget != null)
        {
            // A torch that is not needed after all goes back to the warehouse on the next trip.
            manager.RelightTorch(colony, citizen, torchTarget);
            ReleaseTorch();
            blocked = false;
        }
        else if (coolTarget != null)
        {
            // Whatever the cool store does not take goes back to the warehouse on the next trip.
            if (entity.World.BlockAccessor.GetBlockEntity(coolTarget) is BlockEntityContainer cool)
            {
                manager.UnloadPack(colony, citizen, new[] { cool }, pickupFilter);
            }
            coolTarget = null;
            blocked = false;
        }
        else if (toHut && citizen.WorkplacePos != null && manager.GetBuilding(colony, citizen.WorkplacePos) is { } hut)
        {
            manager.UnloadPack(colony, citizen, manager.HutStorage(colony, hut));
            blocked = !manager.PackEmpty(citizen);
            toHut = false;
        }
        else
        {
            var items = manager.PackItems(citizen);
            foreach (var stack in items) manager.PutInWarehouse(colony, stack);
            manager.SavePack(colony, citizen, items);
            blocked = !manager.PackEmpty(citizen);
        }

        // Hauling XP for every stack set down, Strength for a full load (ColonyManager.Skills.cs).
        manager.GainHaulXp(colony, citizen, stacksBefore - manager.PackItems(citizen).Count, wasFull);

        // Straight on to the next job (anything left over goes to the warehouse), unless the destination was full.
        nextSearchMs = blocked ? entity.World.ElapsedMilliseconds + BlockedRetryMs : 0;
    }

    public override void FinishExecute(bool cancelled)
    {
        // Stopped before picking anything up: let another courier take the request.
        var colony = Colony;
        var citizen = Citizen;
        if (step == Step.Pickup) ReleaseTorch();
        if (step == Step.Pickup && colony != null && citizen != null && manager!.PackEmpty(citizen))
        {
            var request = colony.Requests.Find(r => r.RequestId == requestId && r.LeasedByCitizenId == citizen.CitizenId);
            if (request != null) manager!.ReleaseRequest(colony, request);
        }
        base.FinishExecute(cancelled);
    }

    /// <summary>
    /// Where the courier takes things from and to right now, for the Courier's Station. From is null when it is
    /// carrying something it already had; To is null when the things go to the warehouse.
    /// </summary>
    public (BlockPos? From, BlockPos? To) Route(Colony colony)
    {
        var request = colony.Requests.Find(r => r.RequestId == requestId);
        var to = request?.DestPos ?? shelfTarget ?? coolTarget ?? torchTarget;
        if (step == Step.Pickup) return (target, to);
        return (pickedFrom, to ?? (toHut ? target : null));
    }

    /// <summary>
    /// A load it could not deliver, in words: how many stacks, for which hut, and why ("can't get in", "its storage is
    /// full"). Null when there is no such trouble. Also read while the task rests between tries (ColonyManager.IdleReason).
    /// </summary>
    public string? TroubleText(Colony colony, CitizenRecord citizen)
    {
        if (trouble == null || failedDest == null || manager == null || manager.PackEmpty(citizen)) return null;
        string hutName = manager.GetBuilding(colony, failedDest) is { } hut ? ColonyManager.BuildingName(hut)
            : colony.Buildings.FirstOrDefault(b => !b.IsStation && manager.HutStorage(colony, b).Any(c => c.Pos.Equals(failedDest))) is { } owner ? ColonyManager.BuildingName(owner)
            : Vintagestory.API.Config.Lang.Get("createacolony:activity-courier-a-chest");
        int stacks = manager.PackItems(citizen).Count;
        if (!colony.Requests.Exists(r => r.RequestId == requestId)) return Vintagestory.API.Config.Lang.Get("createacolony:activity-courier-back", stacks, hutName);
        return Vintagestory.API.Config.Lang.Get(trouble == "full" ? "createacolony:activity-courier-full" : "createacolony:activity-courier-no-way", stacks, hutName);
    }

    public override string Describe(Colony colony, CitizenRecord citizen)
    {
        var request = colony.Requests.Find(r => r.RequestId == requestId);
        string what = request != null ? request.ItemCode : "";
        if (request != null && ColonyManager.ParseToolCode(request.ItemCode) is { } tool)
        {
            what = Vintagestory.API.Config.Lang.Get("createacolony:tool-" + tool.Type.ToString().ToLowerInvariant());
        }
        else if (request != null)
        {
            var sample = manager!.PackItems(citizen).FirstOrDefault(ColonyManager.Matcher(request.ItemCode));
            if (sample != null) what = sample.GetName();
        }
        if (TroubleText(colony, citizen) is { } stuck) return stuck;
        string key = (request != null, step) switch
        {
            (true, Step.Pickup) when selfFetch => "activity-self-fetch",
            (true, Step.Deliver) when selfFetch => "activity-self-deliver",
            (true, Step.Pickup) => "activity-courier-fetch",
            (true, Step.Deliver) => "activity-courier-deliver",
            (false, _) when shelfTarget != null => "activity-courier-shelves",
            (false, _) when coolTarget != null => "activity-courier-cooling",
            (false, Step.Sweep) => "activity-courier-sweep",
            (false, Step.Pickup) => "activity-courier-collect",
            _ => "activity-courier-store"
        };
        return Vintagestory.API.Config.Lang.Get("createacolony:" + key, request?.Remaining ?? 0, what);
    }
}
