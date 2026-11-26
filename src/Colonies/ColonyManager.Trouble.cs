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
    /// <summary>
    /// The Mayor and the Bailiff for the report (0.201.0): who, what the Mayor plans or waits for and why, what was
    /// passed over on the last look, and who the Bailiff has lent out or is leaving to wait.
    /// </summary>
    public List<string> OfficeLines(Colony colony)
    {
        var lines = new List<string>();
        var cal = sapi.World.Calendar;
        var mayor = MayorOf(colony);
        if (mayor == null) lines.Add("Mayor: none.");
        else
        {
            string mode = colony.MayorMode == MayorModes.Off ? "Off" : colony.MayorMode == MayorModes.Ask ? "Ask me first" : "Build on their own";
            double next = Math.Max(0, colony.MayorLastLookHours + Math.Max(0.25, Config.MayorEveryHours) - cal.TotalDays * cal.HoursPerDay);
            lines.Add($"Mayor: {mayor.Name}, buildings \"{mode}\", next look in {next:0.#} game hours (and at dawn).");
            if (colony.MayorPlan is { } plan) lines.Add($"  Plan: {PlanName(plan)}{(plan.Approved ? " (approved, waiting to start)" : " (waiting for your answer)")}. {ReasonText(plan)}");
            else if (MayorIdleText(colony) is { Length: > 0 } idle) lines.Add("  Waiting: " + idle);
            foreach (var note in MayorNotes(colony)) lines.Add("  " + note);
            int builders = colony.Citizens.Count(c => c.Alive && !c.Visitor && c.JobCode == Jobs.Builder);
            var builds = OpenBuilds(colony);
            lines.Add($"  Builders: {builders}, builds open: {builds.Count} of {Math.Max(1, builders) * Math.Max(1, Config.MayorOrdersPerBuilder)}.");
            foreach (var b in builds)
            {
                string who = colony.Citizens.Find(c => c.Alive && c.CitizenId == b.BuilderCitizenId && c.JobCode == Jobs.Builder)?.Name ?? "no builder";
                string state = IsBuildStuck(b) ? ", stuck at a block" : IsBuildStarved(colony, b) ? ", waited hours for materials: its builder may work elsewhere" : "";
                lines.Add($"    {BuildingName(b)}: {Lang.Get("createacolony:buildstatus-" + BuildStatusText(colony, b))} ({who}{state})");
            }
        }
        var bailiff = BailiffOf(colony);
        if (bailiff == null) lines.Add("Bailiff: none.");
        else
        {
            lines.Add($"Bailiff: {bailiff.Name}{(BailiffOnDuty(colony) == null ? " (not on duty)" : "")}. Gives jobs: {(colony.BailiffGivesJobs ? "yes" : "no")}, moves stuck workers: {(colony.BailiffLendsWorkers ? "yes" : "no")}, waits {BailiffWaitDaysOf(colony)} days for a delivery.");
            double now = cal.TotalDays;
            foreach (var c in colony.Citizens.Where(c => c.Alive && !c.Visitor))
            {
                if (c.HomeJob != null)
                {
                    var home = c.HomeWorkplacePos == null ? null : GetBuilding(colony, c.HomeWorkplacePos);
                    lines.Add($"  {c.Name}: lent from {(home == null ? c.HomeJob : BuildingName(home))}, back in {Math.Max(0, (c.LentUntilTotalDays - now) * cal.HoursPerDay):0.#} game hours or when that hut can work again.");
                    continue;
                }
                if (c.WorkplacePos == null || !stuckSince.TryGetValue((colony.ColonyId, c.CitizenId), out double since)) continue;
                var hut = GetBuilding(colony, c.WorkplacePos);
                string status = WorkerStatus.GetValueOrDefault(c.CitizenId, "");
                bool waiting = hut != null && WaitsForDelivery(colony, hut, status);
                double limit = waiting ? BailiffWaitDaysOf(colony) : Math.Max(0, Config.BailiffStuckHours) / 24.0;
                string why = c.JobSetByPlayer ? "job set by a player, stays" : waiting ? "waits for a delivery" : "can't work";
                lines.Add($"  {c.Name}: {why} ({status}), {(now - since) * cal.HoursPerDay:0.#} of {limit * cal.HoursPerDay:0.#} game hours before a move.");
            }
            foreach (var c in colony.Citizens.Where(c => c.Alive && !c.Visitor && c.JobSetByPlayer && c.JobCode != null))
                lines.Add($"  {c.Name}: {c.JobCode} set by a player. The Bailiff leaves it.");
        }
        return lines;
    }

    /// <summary>
    /// This colony's builds that lack a Front of build or a Ground floor marker (0.201.0), one line per building type.
    /// Without the Ground floor marker the hut block's height is taken as the ground.
    /// </summary>
    public List<string> MarkerlessBuilds(Colony colony)
    {
        var lines = new List<string>();
        string style = ColonyStyle(colony);
        foreach (var type in BuildingCategories.All.SelectMany(c => c.Types).Distinct())
        {
            string variant = Blueprints.VariantFor(Blueprints.StyleFor(style, type), type, "");
            int max = Blueprints.MaxLevel(style, type, variant);
            var noFront = new List<int>();
            var noGround = new List<int>();
            for (int level = 1; level <= max; level++)
            {
                if (Blueprints.Get(style, type, level, 0, variant) is not { } bp) continue;
                if (!bp.HasFrontMarker) noFront.Add(level);
                if (!bp.HasGroundMarker) noGround.Add(level);
            }
            if (noFront.Count == 0 && noGround.Count == 0) continue;
            var parts = new List<string>();
            if (noFront.Count > 0) parts.Add("no Front of build marker at level " + string.Join(", ", noFront));
            if (noGround.Count > 0) parts.Add("no Ground floor marker at level " + string.Join(", ", noGround));
            lines.Add($"{Lang.Get("createacolony:building-" + type)}: {string.Join("; ", parts)}");
        }
        return lines;
    }

    /// <summary>A request older than this many game hours with nobody carrying it counts as trouble.</summary>
    const double RequestTroubleHours = 3;

    public List<string> TroubleLines(Colony colony)
    {
        var lines = new List<string>();
        var ba = sapi.World.BlockAccessor;
        double days = sapi.World.Calendar.TotalDays, hours = sapi.World.Calendar.TotalHours;
        string At(BlockPos p) => p.ToLocalPosition(sapi).ToString();

        // The two needs that come first (0.205.0, Aaron): food, then homes.
        double food = FoodDays(colony);
        int starving = colony.Citizens.Count(c => c.Alive && !c.Visitor && c.Saturation <= 0);
        if (starving > 0) lines.Add($"STARVING: {starving} settlers have nothing in their bellies. Food left: {food:0.#} days.");
        else if (food < Config.MayorFoodDaysLow) lines.Add($"Food is low: {food:0.#} days left for everyone.");
        int homes = HomesNeeded(colony);
        if (homes > 0) lines.Add($"Homes: {homes} of {Families(colony)} families have no home of their own.");
        int later = BuildingsWithLaterBlocks(colony);
        if (later > 0) lines.Add($"{later} buildings have blocks left for later (boards before a saw, things no hut makes).");

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
            string status = BuildStatusText(colony, b);
            string name = $"{BuildingName(b)} (id {b.BuildingId}) at {At(b.HutPos)}";
            if (StuckText(b) is { } stuck) lines.Add($"Build {name}: stuck {stuck}");
            else if (status == "queued") lines.Add($"Build {name}: waiting for a builder");
            else if (status is not ("building" or "removing" or "clearing")) lines.Add($"Build {name}: {status}");
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
