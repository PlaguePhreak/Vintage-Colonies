using System;
using System.Collections.Generic;
using System.Linq;
using CreateAColony.Settlers;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace CreateAColony.Colonies;

/// <summary>
/// One list of everything in a colony that is not getting on (0.200.0), shared by /colony stuck and the "Trouble
/// right now" part of /colony report: stuck and lost settlers, goals given up, couriers that can't deliver, requests
/// nobody carries, and builds that wait. In the overnight test the report showed the state but not the reason, and
/// /colony stuck said "Nobody is stuck" while a courier had held five stacks for three game days.
/// </summary>
public partial class ColonyManager
{
    /// <summary>A request older than this many game hours with nobody carrying it counts as trouble.</summary>
    const double RequestTroubleHours = 3;

    public List<string> TroubleLines(Colony colony)
    {
        var lines = new List<string>();
        var ba = sapi.World.BlockAccessor;
        double days = sapi.World.Calendar.TotalDays, hours = sapi.World.Calendar.TotalHours;
        string At(BlockPos p) => p.ToLocalPosition(sapi).ToString();

        foreach (var c in colony.Citizens)
        {
            if (!c.Alive || c.Visitor) continue;
            if (sapi.World.GetEntityById(c.EntityId) is not EntitySettler entity)
            {
                lines.Add($"{c.Name}: not loaded" + (c.LastSeenPos is { } seen ? $", last seen at {At(seen.AsBlockPos)}" : ""));
                continue;
            }
            var will = entity.GetBehavior<EntityBehaviorColonyCitizen>()?.Willpower;
            if (will != null && will.Value > 0)
            {
                lines.Add($"{c.Name} at {At(entity.Pos.AsBlockPos)}: stuck ({will.Reason}{(will.Trapped ? ", trapped" : "")}), Willpower {(int)(will.Fraction * 100)}%"
                    + (will.LastGoal != null ? $", was going to {At(will.LastGoal.AsBlockPos)}" : ""));
            }
            if (entity.Swimming) lines.Add($"{c.Name} at {At(entity.Pos.AsBlockPos)}: in the water");
            foreach (var (goal, until) in GivenUpGoals(c.CitizenId))
            {
                lines.Add($"{c.Name}: gave up walking to {At(goal)} ({PlaceLabel(colony, goal)}) until day {until:0.0}, no way there");
            }
            if (OffLimitsZone(c.CitizenId) is { } zone)
            {
                lines.Add($"{c.Name}: keeps away from {At(zone.At)} ({PlaceLabel(colony, zone.At)}) until day {zone.UntilDays:0.0}, was lost there and carried home");
            }
            var task = entity.GetBehavior<EntityBehaviorTaskAI>()?.TaskManager.ActiveTasksBySlot.OfType<SettlerTaskBase>().FirstOrDefault();
            var leased = colony.Requests.Where(r => r.LeasedByCitizenId == c.CitizenId).ToList();
            if (leased.Count > 0 && leased.Any(r => r.LeaseUntilTotalHours < hours) && !PackEmpty(c))
            {
                lines.Add($"{c.Name} at {At(entity.Pos.AsBlockPos)}: carries {PackItems(c).Count} stacks for request {string.Join(", ", leased.Select(r => r.RequestId))} past the hold time"
                    + (task != null ? $" (doing: {SafeDescribe(task, colony, c)})" : " (no task running)"));
            }
            if (IsBrokenDown(c)) lines.Add($"{c.Name}: too unhappy to work (happiness {c.Happiness:0})");
        }

        var warehouse = WarehouseContainers(colony).Cast<BlockEntityContainer>().ToList();
        foreach (var r in colony.Requests.OrderBy(r => r.RequestId))
        {
            string what = $"Request {r.RequestId} ({r.Quantity}x {r.ItemCode} for {RequestLabel(colony, r)})";
            if (RequestHeld(r) is { } held)
            {
                lines.Add(what + (held == "full" ? ": that hut's storage is full, couriers try again later" : ": a courier found no way in, couriers try again later"));
                continue;
            }
            bool carried = r.LeasedByCitizenId != 0 && r.LeaseUntilTotalHours > hours;
            if (carried || !requestSeen.TryGetValue(r.RequestId, out double since) || hours - since < RequestTroubleHours) continue;
            int stock = CountItems(warehouse, Matcher(r.ItemCode));
            bool haulers = colony.Citizens.Any(c => c.Alive && (c.JobCode == Jobs.Courier || Config.JoblessHelpHauling && IsJobless(c)));
            string why = stock > 0 ? (haulers ? $"the warehouse has {stock} but nobody has taken it" : $"the warehouse has {stock} but the colony has no courier")
                : MakerName(colony, r.ItemCode, r.RequestedBy) is { } maker ? $"none in the warehouse, {maker} makes it"
                : "none in the warehouse and no hut makes it";
            lines.Add($"{what}: open for {hours - since:0.#} h, {why}");
        }

        foreach (var b in colony.Buildings)
        {
            if (!b.HasWorkOrder && !b.Removing) continue;
            string status = BuildStatus.GetValueOrDefault(b.BuildingId, "queued");
            string name = $"{BuildingName(b)} (id {b.BuildingId}) at {At(b.HutPos)}";
            if (StuckStep.TryGetValue(b.BuildingId, out var stuckAt))
            {
                lines.Add($"Build {name}: stuck at {At(stuckAt)}, the block there is {ba.GetBlock(stuckAt).Code}, status {status}");
            }
            else if (status is not ("building" or "queued" or "removing" or "clearing")) lines.Add($"Build {name}: {status}");
        }
        return lines;
    }

    /// <summary>Who a request is for, in words: the hut its things go to and, for a builder, the building being built.</summary>
    public string RequestLabel(Colony colony, DeliveryRequest r)
    {
        string dest = RequestHut(colony, r) is { } hut ? $"{BuildingName(hut)} (id {hut.BuildingId})" : "a chest at " + r.DestPos.ToLocalPosition(sapi);
        if (r.RequestedBy.StartsWith("building:") && int.TryParse(r.RequestedBy.Substring(9), out int id)
            && colony.Buildings.Find(b => b.BuildingId == id) is { } order && RequestHut(colony, r) != order)
        {
            return dest + ", to build " + BuildingName(order);
        }
        return r.RequestedBy.StartsWith("minstock:") ? dest + ", minimum stock" : dest;
    }

    /// <summary>What stands at a spot, in words: a building's name, or "open ground".</summary>
    public string PlaceLabel(Colony colony, BlockPos pos) =>
        BuildingAround(colony, pos.ToVec3d()) is { } b ? BuildingName(b) : "open ground";

    /// <summary>The same for a window: the building's name, or "a spot at x, y, z".</summary>
    public string PlaceLabelLang(Colony colony, BlockPos pos) =>
        BuildingAround(colony, pos.ToVec3d()) is { } b ? BuildingName(b) : Lang.Get("createacolony:place-spot-at", pos.ToLocalPosition(sapi).ToString());

    static string SafeDescribe(SettlerTaskBase task, Colony colony, CitizenRecord citizen)
    {
        try { return task.Describe(colony, citizen); }
        catch (Exception e) { return task.GetType().Name + " (" + e.Message + ")"; }
    }

    /// <summary>"64x Dry grass, 3x Crude door": a short list of stacks by name, the biggest first.</summary>
    public static string StackList(IEnumerable<ItemStack> stacks, int max = 12)
    {
        var groups = stacks.Where(s => s?.Collectible != null).GroupBy(s => s.GetName())
            .Select(g => (Name: g.Key, Count: g.Sum(s => s.StackSize), Stacks: g.Count(), Biggest: g.Max(s => s.StackSize), Max: g.First().Collectible.MaxStackSize))
            .OrderByDescending(g => g.Count).ToList();
        if (groups.Count == 0) return "empty";
        var parts = groups.Take(max).Select(g => $"{g.Count}x {g.Name}" + (g.Biggest > g.Max ? $" (OVERSIZED stack of {g.Biggest}, normal is {g.Max})" : ""));
        return string.Join(", ", parts) + (groups.Count > max ? $", and {groups.Count - max} more kinds" : "");
    }
}
