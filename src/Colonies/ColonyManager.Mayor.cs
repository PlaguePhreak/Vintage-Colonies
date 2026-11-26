using System;
using System.Collections.Generic;
using System.Linq;
using CreateAColony.Blocks;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace CreateAColony.Colonies;

/// <summary>
/// The Mayor (0.199.0, Aaron's design doc "Mayor and self-building settlement"). One settler with the Mayor job works
/// from the Town Hall and grows the settlement: every few hours (and at dawn) they look at what the colony needs, pick
/// the next building, find a spot for it, put the hut down and order it built; later they order upgrades.
/// What comes first:
/// 1. food (more Farmers and Hunters while there are many settlers for each),
/// 2. whatever a waiting building is short of (a request nobody filled for MayorShortHours: logs, so a Forester),
/// 3. beds (a Residence when none are free),
/// 4. anything else the colony is short of,
/// 5. a second Builder's Hut at MayorSecondBuilderAt settlers,
/// 6. the build order on the Mayor tab (stages by settler count),
/// 7. upgrades of busy, full huts up to the Town Hall's level, then the Town Hall itself (always asked).
/// With "Ask first" the plan waits on the Mayor tab for Approve, Other spot or Skip this one. Hut blocks come from the
/// Town Hall's and the warehouse's storage; when there is none the Mayor asks for one on the Requests tab (the Sawmill
/// can make them), unless the test switch "Mayor gets hut blocks free" is on. The Mayor only uses blueprints that exist
/// (Aaron's builds): it never makes up a building.
/// </summary>
public partial class ColonyManager
{
    /// <summary>Requests the Mayor makes for hut blocks are tagged with this.</summary>
    public const string MayorTag = "mayor";

    /// <summary>The colony's Mayor, or null.</summary>
    public CitizenRecord? MayorOf(Colony colony) =>
        colony.Citizens.Find(c => c.Alive && !c.Visitor && !c.IsChild && c.JobCode == Jobs.Mayor);

    /// <summary>
    /// The Mayor whose planning runs. Since 0.201.0 (Aaron) that is any living Mayor: planning carries on while they are
    /// too unhappy to work, knocked out or stuck somewhere, and only "Off" on the Mayor tab stops it.
    /// </summary>
    public CitizenRecord? MayorOnDuty(Colony colony) => MayorOf(colony);

    // ---- What the Mayor thought on the last look (0.201.0), for the report and the server log ----

    readonly Dictionary<string, List<string>> mayorNotes = new();
    readonly Dictionary<string, string> mayorLastLogged = new();

    /// <summary>The Mayor's notes from the last look: what was passed over and why.</summary>
    public IReadOnlyList<string> MayorNotes(Colony colony) =>
        mayorNotes.TryGetValue(colony.ColonyId, out var notes) ? notes : (IReadOnlyList<string>)Array.Empty<string>();

    void MayorNote(Colony colony, string text)
    {
        if (!mayorNotes.TryGetValue(colony.ColonyId, out var notes)) mayorNotes[colony.ColonyId] = notes = new List<string>();
        if (notes.Count < 40 && !notes.Contains(text)) notes.Add(text);
    }

    static string TypeName(string type) => Lang.Get("createacolony:building-" + type);

    /// <summary>The build order: the colony's own (Mayor tab) or config MayorBuildOrder, with each type's settler count.</summary>
    public List<(string Type, int From)> MayorOrder(Colony colony)
    {
        var stages = new Dictionary<string, int>();
        var defaults = new List<string>();
        foreach (var entry in Config.MayorBuildOrder ?? Array.Empty<string>())
        {
            var parts = entry.Split(':');
            string type = parts[^1].Trim();
            if (type.Length == 0 || stages.ContainsKey(type)) continue;
            stages[type] = parts.Length > 1 && int.TryParse(parts[0], out int from) ? from : 0;
            defaults.Add(type);
        }
        var order = colony.MayorOrder.Count > 0 ? colony.MayorOrder.Where(stages.ContainsKey).Distinct().ToList() : new List<string>();
        // Types added to the config later go at the end of a colony's own order.
        foreach (var type in defaults) if (!order.Contains(type)) order.Add(type);
        return order.Select(t => (t, stages[t])).ToList();
    }

    // ---- The tick ---------------------------------------------------------------------------

    void TickMayor(Colony colony, double now)
    {
        var mayor = MayorOnDuty(colony);
        if (mayor == null) return;
        double hours = now * sapi.World.Calendar.HoursPerDay;
        double hourOfDay = sapi.World.Calendar.HourOfDay;
        bool dawn = hourOfDay >= Config.AutoHireHour && (int)(colony.MayorLastLookHours / sapi.World.Calendar.HoursPerDay) < (int)now;
        if (!dawn && hours - colony.MayorLastLookHours < Math.Max(0.25, Config.MayorEveryHours)) return;
        colony.MayorLastLookHours = hours;
        MayorLook(colony, mayor);
        MarkDirty(colony);
    }

    /// <summary>The Mayor looks at the colony: makes sure there is a builder, tracks shortages, and plans or carries on with the next step.</summary>
    public void MayorLook(Colony colony, CitizenRecord mayor)
    {
        int day = (int)sapi.World.Calendar.TotalDays;
        colony.MayorSkipToday.RemoveAll(s => !s.EndsWith("|" + day));
        mayorNotes.Remove(colony.ColonyId);
        TrackShortages(colony);
        EnsureBuilderJob(colony, mayor);
        SetMayorIdle(colony, "");
        if (colony.MayorMode == MayorModes.Off) return;
        MayorThinks(colony, mayor);
        LogMayorLook(colony, mayor);
    }

    /// <summary>One line in the server log each time the Mayor's plan or reason for waiting changes (0.201.0).</summary>
    void LogMayorLook(Colony colony, CitizenRecord mayor)
    {
        string now = colony.MayorPlan is { } p
            ? (p.Approved ? "carrying out " : "asks for ") + PlanName(p) + ". " + ReasonText(p)
            : "waits: " + (MayorIdleText(colony) is { Length: > 0 } idle ? idle : "nothing to do");
        if (mayorLastLogged.GetValueOrDefault(colony.ColonyId) == now) return;
        mayorLastLogged[colony.ColonyId] = now;
        Mod.Logger.Notification("[mayor] {0}: {1} {2}", colony.Name, mayor.Name, now);
        foreach (var note in MayorNotes(colony)) Mod.Logger.Notification("[mayor] {0}:   {1}", colony.Name, note);
    }

    void MayorThinks(Colony colony, CitizenRecord mayor)
    {
        if (colony.MayorPlan is { } plan)
        {
            // Waiting for a yes, or (approved) for a hut block: try again.
            if (plan.Approved) CarryOut(colony, mayor, plan);
            else if (!PlanStillFits(colony, plan)) colony.MayorPlan = null;
            if (colony.MayorPlan != null) return;
        }

        // Up to three steps in one look (0.201.0), so each builder gets a build in work and one queued without
        // waiting for the next look.
        for (int n = 0; n < 3; n++)
        {
            var next = NextStep(colony, mayor);
            if (next == null) return;
            bool ask = colony.MayorMode == MayorModes.Ask || next.Type == BuildingTypes.TownHall;
            if (ask)
            {
                colony.MayorPlan = next;
                TellMembers(colony, "createacolony:mayor-asks", mayor.Name, PlanName(next), ReasonText(next));
                return;
            }
            next.Approved = true;
            colony.MayorPlan = next;
            CarryOut(colony, mayor, next);
            if (colony.MayorPlan != null) return;
        }
    }

    void SetMayorIdle(Colony colony, string key, params string[] args)
    {
        colony.MayorIdle = key;
        colony.MayorIdleArgs = args.ToList();
    }

    /// <summary>Remembers since when each request has been open, and forgets those that are gone.</summary>
    void TrackShortages(Colony colony)
    {
        double hours = sapi.World.Calendar.TotalHours;
        var open = colony.Requests.Where(r => r.Remaining > 0 && r.RequestedBy != MayorTag).Select(r => r.RequestId).ToHashSet();
        foreach (var id in colony.MayorShortSince.Keys.ToList()) if (!open.Contains(id)) colony.MayorShortSince.Remove(id);
        foreach (var id in open) if (!colony.MayorShortSince.ContainsKey(id)) colony.MayorShortSince[id] = hours;
    }

    /// <summary>With no builder at all nothing gets built: the Mayor makes the best jobless settler the builder.</summary>
    void EnsureBuilderJob(Colony colony, CitizenRecord mayor)
    {
        if (colony.Citizens.Any(c => c.Alive && !c.Visitor && c.JobCode == Jobs.Builder)) return;
        var pick = Jobless(colony).Where(c => IncapableOfJob(c, Jobs.Builder) == null).OrderByDescending(c => JobScore(c, Jobs.Builder)).ThenBy(c => c.CitizenId).FirstOrDefault();
        if (pick == null) return;
        pick.JobCode = Jobs.Builder;
        pick.WorkplacePos = null;
        pick.JobSetByPlayer = false;
        pick.LastMovedTotalDays = sapi.World.Calendar.TotalDays;
        LogEvent(colony, "event-mayor-builder", mayor.Name, pick.Name);
    }

    // ---- Choosing the next step ---------------------------------------------------------------

    /// <summary>Huts of a type, built or on their way (a hut block down, or a plan).</summary>
    int CountOf(Colony colony, string type) =>
        colony.Buildings.Count(b => b.Type == type && !b.Removing) + (colony.MayorPlan is { BuildingId: 0 } p && p.Type == type ? 1 : 0);

    bool Skipped(Colony colony, string type) =>
        colony.MayorSkip.Contains(type) || colony.MayorSkipToday.Any(s => s.StartsWith(type + "|"));

    /// <summary>True while the builders have room for another job: each has one build in work and one queued (MayorOrdersPerBuilder).</summary>
    bool BuildersHaveRoom(Colony colony)
    {
        int builders = Math.Max(1, colony.Citizens.Count(c => c.Alive && !c.Visitor && c.JobCode == Jobs.Builder));
        return OpenBuilds(colony).Count < builders * Math.Max(1, Config.MayorOrdersPerBuilder);
    }

    List<Building> OpenBuilds(Colony colony) => colony.Buildings.Where(b => b.HasWorkOrder && !b.Upkeep && !b.Repairing).OrderBy(b => b.BuildingId).ToList();

    /// <summary>True when a hut of this type is built and has a worker (or needs none).</summary>
    bool HutWorking(Colony colony, string type) =>
        colony.Buildings.Any(b => b.Type == type && !b.Removing && (b.Level >= 1
            ? JobOf(type) == null || WorkersAt(colony, b).Count > 0
            // Huts work at level 0 (0.205.0, Aaron): a hut block with its worker counts.
            : Config.MayorHutsStartUnbuilt && !b.IsStation && Staffable(b) && JobOf(type) != null && WorkersAt(colony, b).Count > 0));

    /// <summary>A hut type the Mayor may put down as a bare hut block with a worker (0.205.0): one with a job, not a home or a guard post.</summary>
    bool StartsUnbuilt(string type) => Config.MayorHutsStartUnbuilt && Config.WorkAtUnbuiltHuts && JobOf(type) != null && !IsHome(type) && !IsGuardPost(type)
        && type is not (BuildingTypes.Builder or BuildingTypes.TownHall or BuildingTypes.Warehouse);

    readonly Dictionary<string, (long Ms, double Days)> foodDays = new();

    /// <summary>
    /// Days of food (0.205.0): everything settlers could eat in the colony's chests, plus what is in their bellies, in
    /// days for everyone. Worked out again every minute. A large number when nobody can be asked (no settler loaded).
    /// </summary>
    public double FoodDays(Colony colony)
    {
        long now = sapi.World.ElapsedMilliseconds;
        if (foodDays.TryGetValue(colony.ColonyId, out var cached) && now - cached.Ms < 60_000) return cached.Days;
        var people = colony.Citizens.Where(c => c.Alive && !c.Visitor).ToList();
        var eater = people.Select(c => sapi.World.GetEntityById(c.EntityId) as Vintagestory.API.Common.EntityAgent).FirstOrDefault(e => e != null);
        if (people.Count == 0 || eater == null) return 99;
        double perDay = people.Count * Math.Max(1f, Config.HungerPerDay);
        double have = people.Sum(c => Math.Max(0, (double)c.Saturation));
        foreach (var container in FindBlockEntities<BlockEntityContainer>(colony))
        {
            foreach (var slot in container.Inventory)
            {
                if (slot.Empty || EvaluateFood(container, slot, eater) is not { } food) continue;
                int portions = slot.Itemstack.Block is IBlockMealContainer meal
                    ? Math.Max(1, (int)meal.GetQuantityServings(sapi.World, slot.Itemstack))
                    : slot.Itemstack.StackSize;
                have += food.Satiety * portions;
            }
            if (have > perDay * 30) break;
        }
        double days = have / perDay;
        foodDays[colony.ColonyId] = (now, days);
        return days;
    }

    /// <summary>
    /// Families (0.205.0, Aaron: one house per family): a couple with their children, or a grown-up on their own.
    /// Children go with a living parent.
    /// </summary>
    public int Families(Colony colony)
    {
        var people = colony.Citizens.Where(c => c.Alive && !c.Visitor).ToList();
        var alive = people.ToDictionary(c => c.CitizenId);
        var keys = new HashSet<int>();
        foreach (var c in people)
        {
            if (c.IsChild && c.ParentIds.FirstOrDefault(alive.ContainsKey) is var parent && parent != 0)
            {
                var p = alive[parent];
                keys.Add(p.PartnerId != 0 && alive.ContainsKey(p.PartnerId) ? Math.Min(p.CitizenId, p.PartnerId) : p.CitizenId);
                continue;
            }
            keys.Add(c.PartnerId != 0 && alive.ContainsKey(c.PartnerId) ? Math.Min(c.CitizenId, c.PartnerId) : c.CitizenId);
        }
        return keys.Count;
    }

    /// <summary>Homes the colony still needs: one per family, less the Residences it has or has on the way.</summary>
    public int HomesNeeded(Colony colony) =>
        Math.Max(0, Families(colony) - colony.Buildings.Count(b => b.Type == BuildingTypes.Residence && !b.Removing)
            - (colony.MayorPlan is { BuildingId: 0 } p && p.Type == BuildingTypes.Residence ? 1 : 0));

    /// <summary>Every chest the colony keeps things in: the warehouse and each hut's storage.</summary>
    IEnumerable<BlockEntityContainer> ColonyStores(Colony colony) =>
        WarehouseContainers(colony).Concat(colony.Buildings.Where(b => !b.Removing).SelectMany(b => HutStorage(colony, b))).Distinct();

    /// <summary>True when the colony has seeds to sow: in a chest or with a settler. A Farmer's Hut waits for them (0.201.0).</summary>
    bool ColonyHasSeeds(Colony colony)
    {
        static bool Seed(ItemStack s) => s.Collectible.Code.Path.StartsWith("seeds-");
        if (CountItems(ColonyStores(colony), Seed) > 0) return true;
        return colony.Citizens.Any(c => c.Alive && !c.Visitor && PackCount(c, Seed) > 0);
    }

    /// <summary>
    /// The start of a settlement (0.201.0, Aaron): Gatherer's Hut, Builder's Hut, Primitive Craftsman and Warehouse, in
    /// that order, before beds and everything else. Null once all four are down (or can't be).
    /// </summary>
    MayorPlan? StartStep(Colony colony, bool room, ref string? why)
    {
        foreach (var type in Config.MayorStartOrder ?? Array.Empty<string>())
        {
            if (Skipped(colony, type)) continue;
            if (!CanMayorBuild(colony, type))
            {
                if (CountOf(colony, type) == 0) MayorNote(colony, $"Start: no {TypeName(type)} design in this colony's style, so it is left out.");
                continue;
            }
            if (colony.MayorPlan is { } pending && pending.Type == type) continue;
            var unbuilt = colony.Buildings.FirstOrDefault(b => b.Type == type && b.Level == 0 && !b.HasWorkOrder && !b.IsStation && !b.Removing);
            if (unbuilt != null)
            {
                if (room) return UpgradePlan(unbuilt, "mayor-why-start", Array.Empty<string>(), 1);
                continue;
            }
            if (CountOf(colony, type) > 0) continue;
            if (!room)
            {
                MayorNote(colony, $"Start: the {TypeName(type)} is next, once a builder is free.");
                return null;
            }
            var plan = NewBuilding(colony, type, "mayor-why-start", Array.Empty<string>());
            if (plan != null) return plan;
            why ??= "mayor-idle-nospot|@building-" + type;
            MayorNote(colony, $"Start: no spot found for the {TypeName(type)}.");
        }
        return null;
    }

    /// <summary>
    /// The hut chain (0.201.0, config MayorHutNeeds): true when every hut this type depends on is built and has a
    /// worker. When one is missing, first is what to do about it now (build it, order it, find it a worker), or null
    /// when that has to wait.
    /// </summary>
    bool ChainReady(Colony colony, CitizenRecord mayor, string type, bool room, ref string? why, out MayorPlan? first, int depth = 0)
    {
        first = null;
        if (depth > 6 || Config.MayorHutNeeds == null || !Config.MayorHutNeeds.TryGetValue(type, out var needs)) return true;
        foreach (var dep in needs)
        {
            if (dep == type || HutWorking(colony, dep)) continue;
            // A hut the colony can never have (no design for it) does not hold the others up.
            if (!CanMayorBuild(colony, dep) && !colony.Buildings.Any(b => b.Type == dep && !b.Removing)) continue;
            why ??= "mayor-idle-chain|@building-" + type + "|@building-" + dep;
            MayorNote(colony, $"Chain: the {TypeName(type)} waits for a working {TypeName(dep)}.");
            first = ForNeed(colony, mayor, dep, "mayor-why-chain", new[] { "@building-" + type }, room, ref why, depth + 1);
            return false;
        }
        return true;
    }

    // ---- The materials check (0.201.0) ---------------------------------------------------------

    readonly Dictionary<(string Colony, string Type), (long Ms, Dictionary<string, (ItemStack Sample, int Count)> Costs)> designCosts = new();

    /// <summary>What level 1 of a design costs, counted on empty ground. Kept for ten minutes.</summary>
    Dictionary<string, (ItemStack Sample, int Count)> DesignCosts(Colony colony, string type)
    {
        long ms = sapi.World.ElapsedMilliseconds;
        if (designCosts.TryGetValue((colony.ColonyId, type), out var known) && ms - known.Ms < 600_000) return known.Costs;
        var costs = new Dictionary<string, (ItemStack, int)>();
        string style = ColonyStyle(colony);
        var probe = new Building { Type = type, Style = style };
        probe.Variant = Blueprints.VariantFor(Blueprints.StyleFor(probe), type, "");
        // Counted high in the air over the Town Hall, where nothing stands, so the whole design counts.
        probe.HutPos = new BlockPos(colony.TownHallPos.X, Math.Max(colony.TownHallPos.Y, sapi.World.BlockAccessor.MapSizeY - 80), colony.TownHallPos.Z);
        if (Blueprints.Get(probe, 1) is { } blueprint)
        {
            foreach (var step in blueprint.Steps)
            {
                if (step.BlockId == 0) continue;
                foreach (var cost in RemainingCost(colony, step, StepPos(probe, step)))
                {
                    costs[cost.Code] = costs.TryGetValue(cost.Code, out var n) ? (n.Item1, n.Item2 + cost.Count) : (cost.Sample, cost.Count);
                }
            }
        }
        designCosts[(colony.ColonyId, type)] = (ms, costs);
        return costs;
    }

    /// <summary>
    /// The materials check: what in a design the colony neither stores nor can make. Maker is the hut type that makes it
    /// and is not working yet (build that first), or null when no hut makes it at all (someone has to supply it).
    /// Things the start huts bring (logs, clay, dry grass, stone tools) never count: the Mayor builds those huts when
    /// a build runs short. Null when the design is fine.
    /// </summary>
    (string Thing, string? Maker)? MaterialsProblem(Colony colony, string type)
    {
        if (!Config.MayorChecksMaterials || !Config.BuildersNeedMaterials) return null;
        if ((Config.MayorStartOrder ?? Array.Empty<string>()).Contains(type)) return null;
        var stores = ColonyStores(colony).ToList();
        (string, string?)? supply = null;
        var (saw, later) = LaterCodes(colony);
        foreach (var (code, (sample, count)) in DesignCosts(colony, type))
        {
            if (CountItems(stores, Matcher(code)) >= count) continue;
            // Left for later (0.205.0): boards before a saw, and what the colony already gave up on.
            if (Config.BuildersFillInLater && (later.Contains(code) || !saw && NeedsSaw(code))) continue;
            var maker = MakerTypeFor(colony, code);
            if (maker != null && HutWorking(colony, maker)) continue;
            if (Config.MayorMakersFirst)
            {
                // Maker huts first (0.205.0, Aaron): a hut that makes it, and is not working yet, comes first. What no
                // hut makes is left for later (or brought by the builders: soil, sand, stones).
                if (maker == null) continue;
                string what = sample?.GetName() ?? ThingName(code);
                if (maker == type)
                {
                    supply ??= (what, null);
                    continue;
                }
                return (what, maker);
            }
            bool chained = maker != null && (Config.MayorMaterialMakers ?? Array.Empty<string>()).Contains(maker);
            // Made by a simple hut (Forester, Gatherer, Sawmill, Quarry), or by nobody: builders and players bring it.
            if (!chained) continue;
            string thing = sample?.GetName() ?? ThingName(code);
            // The design needs what only its own kind makes (a Blacksmith's anvil): someone has to supply it.
            if (maker == type)
            {
                supply ??= (thing, null);
                continue;
            }
            return (thing, maker);
        }
        return supply;
    }

    /// <summary>The Mayor's next step, most urgent first; null when there is nothing to do (the reason goes on the Mayor tab).</summary>
    MayorPlan? NextStep(Colony colony, CitizenRecord mayor)
    {
        var settlers = colony.Citizens.Count(c => c.Alive && !c.Visitor);
        bool room = BuildersHaveRoom(colony);
        // why: what really holds the next building up. aside: true, but not what the Mayor waits for (0.201.0).
        string? why = null, aside = null;

        // 0. The start of a settlement: Gatherer's Hut, Builder's Hut, Primitive Craftsman, Warehouse.
        if (StartStep(colony, room, ref why) is { } start) return start;

        // 1. Food. The Gatherer's Hut is the first food hut; a Farmer's Hut waits until the colony has seeds.
        var foodTypes = new[] { BuildingTypes.Gatherer, BuildingTypes.Farmer, BuildingTypes.Hunter, BuildingTypes.Fisher };
        int foodHuts = foodTypes.Sum(t => CountOf(colony, t));
        bool seeds = ColonyHasSeeds(colony);
        // Food first, by real food (0.205.0): below MayorFoodDaysLow days of food, a food hut comes before anything else.
        double foodLeft = FoodDays(colony);
        bool hungry = foodLeft < Config.MayorFoodDaysLow;
        if (hungry) MayorNote(colony, $"Food: {foodLeft:0.#} days of food left for {settlers} settlers.");
        if (hungry || settlers > foodHuts * Math.Max(1, Config.MayorSettlersPerFoodHut))
        {
            if (!seeds) MayorNote(colony, "Food: no Farmer's Hut yet, the colony has no seeds.");
            var type = foodTypes.Where(t => !Skipped(colony, t) && (t != BuildingTypes.Farmer || seeds))
                .Where(t => CountOf(colony, t) < (t == BuildingTypes.Gatherer ? Math.Max(1, Config.MayorMaxPerType) : Config.MayorMaxPerType + 2))
                .OrderBy(t => CountOf(colony, t) + (t == BuildingTypes.Gatherer ? 0 : t == BuildingTypes.Farmer ? 0.25 : t == BuildingTypes.Hunter ? 0.5 : 1)).FirstOrDefault();
            if (type != null && ForNeed(colony, mayor, type, "mayor-why-food", new[] { settlers.ToString() }, room, ref why) is { } food) return food;
        }

        // Homes second (0.205.0, Aaron): one house per family.
        if (Config.MayorHomePerFamily && HomeStep(colony, mayor, room, ref why) is { } homeStep) return homeStep;

        // Blocks left for later that the colony can now get (0.205.0).
        if (FillInStep(colony, room) is { } fillIn) return fillIn;

        // 2. and 4. Things the colony is short of: tools nobody can make yet first (so the Primitive Craftsman comes
        // before anything else), then what holds up a building.
        double hours = sapi.World.Calendar.TotalHours;
        bool craftsmanWorks = HutWorking(colony, BuildingTypes.Craftsman);
        bool ToolNobodyMakes(DeliveryRequest r) => !craftsmanWorks && ParseToolCode(r.ItemCode) != null && MakerTypeFor(colony, r.ItemCode) == BuildingTypes.Craftsman;
        var shortOf = colony.Requests
            .Where(r => r.Remaining > 0 && r.RequestedBy != MayorTag)
            .Where(r => ToolNobodyMakes(r) || colony.MayorShortSince.TryGetValue(r.RequestId, out double since) && hours - since >= Config.MayorShortHours)
            .Where(r => CountItems(WarehouseContainers(colony), Matcher(r.ItemCode)) < r.Remaining)
            .OrderByDescending(ToolNobodyMakes)
            .ThenByDescending(r => RequestHoldsUpBuild(colony, r))
            .ThenBy(r => r.RequestId)
            .ToList();
        bool bedsChecked = false;
        foreach (var request in shortOf)
        {
            bool forBuild = RequestHoldsUpBuild(colony, request);
            bool tool = ToolNobodyMakes(request);
            if (!forBuild && !tool && !bedsChecked)
            {
                bedsChecked = true;
                if (BedStep(colony, mayor, room, ref why) is { } beds) return beds;
            }
            var maker = MakerTypeFor(colony, request.ItemCode);
            string thing = ThingName(request.ItemCode);
            if (maker == null)
            {
                aside ??= "mayor-idle-nomaker|" + thing;
                MayorNote(colony, $"Short of {thing} ({RequesterName(colony, request)}), and no hut can make it.");
                continue;
            }
            string who = RequesterName(colony, request);
            var step = ForNeed(colony, mayor, maker, tool ? "mayor-why-tool" : forBuild ? "mayor-why-build-short" : "mayor-why-short", new[] { who, thing }, room, ref why);
            if (step != null) return step;
        }

        // 3. Beds.
        if (!bedsChecked && BedStep(colony, mayor, room, ref why) is { } bed) return bed;

        if (!room)
        {
            // The real reason (0.201.0): the builders have their hands full. Say with what, and what it waits for.
            var builds = OpenBuilds(colony);
            var slow = builds.FirstOrDefault(b => IsBuildStarved(colony, b)) ?? builds.FirstOrDefault(IsBuildStuck) ?? builds.FirstOrDefault();
            if (slow != null) SetMayorIdle(colony, "mayor-idle-building", BuildingName(slow), "@buildstatus-" + BuildStatusText(colony, slow));
            else SetMayorIdle(colony, "mayor-idle-busy");
            if (why != null) MayorNote(colony, "Also: " + IdleText(why));
            if (aside != null) MayorNote(colony, "Also: " + IdleText(aside));
            return null;
        }

        // 5. A second builder.
        if (settlers >= Config.MayorSecondBuilderAt && CountOf(colony, BuildingTypes.Builder) < 2 && !Skipped(colony, BuildingTypes.Builder)
            && NewBuilding(colony, BuildingTypes.Builder, "mayor-why-builder", new[] { settlers.ToString() }) is { } builder) return builder;

        // Huts working from a bare hut block (0.205.0) get their building once the colony can supply it.
        foreach (var bare in colony.Buildings.Where(b => b.Level == 0 && !b.HasWorkOrder && !b.IsStation && !b.Removing && StartsUnbuilt(b.Type)
                     && WorkersAt(colony, b).Count > 0 && !Skipped(colony, b.Type) && Blueprints.Get(b, 1) != null).OrderBy(b => b.BuildingId))
        {
            if (Config.MayorMakersFirst && MaterialsProblem(colony, bare.Type) is { } bareProblem)
            {
                if (bareProblem.Maker != null && ForNeed(colony, mayor, bareProblem.Maker, "mayor-why-material", new[] { "@building-" + bare.Type, bareProblem.Thing }, room, ref why) is { } bareMaker) return bareMaker;
                continue;
            }
            return UpgradePlan(bare, "mayor-why-order", Array.Empty<string>(), 1);
        }

        // 6. The build order.
        (string Type, string Thing)? lastResort = null;
        foreach (var (type, from) in MayorOrder(colony))
        {
            if (settlers < from || Skipped(colony, type) || !CanMayorBuild(colony, type)) continue;
            // A hut block someone put down and never ordered: order it.
            if (colony.Buildings.FirstOrDefault(b => b.Type == type && b.Level == 0 && !b.HasWorkOrder && !b.IsStation && !b.Removing) is { } unbuilt)
            {
                return UpgradePlan(unbuilt, "mayor-why-order", Array.Empty<string>(), 1);
            }
            if (CountOf(colony, type) > 0) continue;
            if (type == BuildingTypes.Farmer && !seeds)
            {
                why ??= "mayor-idle-noseeds";
                MayorNote(colony, "Build order: the Farmer's Hut waits for seeds.");
                continue;
            }
            // The hut chain: what this hut depends on comes first.
            if (!ChainReady(colony, mayor, type, room, ref why, out var first))
            {
                if (first != null) return first;
                continue;
            }
            // The materials check: a design the colony can't supply waits, and its maker comes first.
            if (MaterialsProblem(colony, type) is { } problem)
            {
                if (problem.Maker == null)
                {
                    lastResort ??= (type, problem.Thing);
                    MayorNote(colony, $"Build order: the {TypeName(type)} needs {problem.Thing}, which no hut here makes. It goes last.");
                    continue;
                }
                why ??= "mayor-idle-materials|@building-" + type + "|" + problem.Thing + "|@building-" + problem.Maker;
                MayorNote(colony, $"Build order: the {TypeName(type)} needs {problem.Thing}, so a {TypeName(problem.Maker)} comes first.");
                var makerStep = ForNeed(colony, mayor, problem.Maker, "mayor-why-material", new[] { "@building-" + type, problem.Thing }, room, ref why);
                if (makerStep != null) return makerStep;
                continue;
            }
            var step = NewBuilding(colony, type, "mayor-why-order", Array.Empty<string>());
            if (step != null) return step;
            why ??= "mayor-idle-nospot|@building-" + type;
            MayorNote(colony, $"Build order: no spot found for the {TypeName(type)}.");
        }

        // 7. Upgrades.
        if (UpgradeStep(colony) is { } upgrade) return upgrade;

        // Last: a design the colony can't fully supply. It is ordered anyway, and says what someone has to bring.
        if (!Config.MayorMakersFirst && lastResort is { } last && NewBuilding(colony, last.Type, "mayor-why-order-supply", new[] { last.Thing }) is { } supplied) return supplied;

        SetIdleFrom(colony, why ?? aside ?? "mayor-idle-nothing");
        return null;
    }

    /// <summary>An idle reason ("key|arg|arg") in words.</summary>
    string IdleText(string why)
    {
        var parts = why.Split('|');
        var args = parts.Skip(1).Select(a => a.StartsWith('@') ? Lang.Get("createacolony:" + a[1..]) : a).Cast<object>().ToArray();
        return Lang.Get("createacolony:" + parts[0], args);
    }

    void SetIdleFrom(Colony colony, string why)
    {
        var parts = why.Split('|');
        SetMayorIdle(colony, parts[0], parts.Skip(1).ToArray());
    }

    /// <summary>A Residence (or a bigger one) when no bed is free.</summary>
    MayorPlan? BedStep(Colony colony, CitizenRecord mayor, bool room, ref string? why)
    {
        if (FreeBeds(colony) > 0) return null;
        // A home on its way already.
        if (colony.Buildings.Any(b => IsHome(b.Type) && !b.Removing && (b.Level == 0 || b.HasWorkOrder))) return null;
        if (colony.MayorPlan is { } p && IsHome(p.Type)) return null;
        if (!room) return null;
        var home = colony.Buildings.Where(b => b.Type == BuildingTypes.Residence && UpgradeTo(colony, b) > 0).OrderBy(b => b.Level).FirstOrDefault();
        if (home != null) return UpgradePlan(home, "mayor-why-beds", Array.Empty<string>(), UpgradeTo(colony, home));
        if (Skipped(colony, BuildingTypes.Residence)) return null;
        var plan = NewBuilding(colony, BuildingTypes.Residence, "mayor-why-beds", Array.Empty<string>());
        if (plan == null) why ??= "mayor-idle-nospot|@building-" + BuildingTypes.Residence;
        return plan;
    }

    /// <summary>A new Residence while there are more families than homes (0.205.0, Aaron: one house per family).</summary>
    MayorPlan? HomeStep(Colony colony, CitizenRecord mayor, bool room, ref string? why)
    {
        int needed = HomesNeeded(colony);
        if (needed <= 0) return null;
        MayorNote(colony, $"Homes: {needed} more famil{(needed == 1 ? "y needs a home" : "ies need homes")}.");
        // A home on its way already: one at a time.
        var waitingHome = colony.Buildings.FirstOrDefault(b => b.Type == BuildingTypes.Residence && !b.Removing && b.Level == 0 && !b.HasWorkOrder);
        if (waitingHome != null) return room ? UpgradePlan(waitingHome, "mayor-why-homes", new[] { needed.ToString() }, 1) : null;
        if (colony.Buildings.Any(b => b.Type == BuildingTypes.Residence && !b.Removing && b.Level == 0 && b.HasWorkOrder)) return null;
        if (!room || Skipped(colony, BuildingTypes.Residence) || !CanMayorBuild(colony, BuildingTypes.Residence)) return null;
        var plan = NewBuilding(colony, BuildingTypes.Residence, "mayor-why-homes", new[] { needed.ToString() });
        if (plan == null) why ??= "mayor-idle-nospot|@building-" + BuildingTypes.Residence;
        return plan;
    }

    /// <summary>
    /// What to do about a need a hut type meets: build one if there is none, order a waiting hut block built, find
    /// a worker for an empty hut, or upgrade (or add another) when it can't keep up. Null when nothing can be done now.
    /// </summary>
    MayorPlan? ForNeed(Colony colony, CitizenRecord mayor, string type, string reason, string[] args, bool room, ref string? why, int depth = 0)
    {
        if (Skipped(colony, type) || !CanMayorBuild(colony, type)) return null;
        var huts = colony.Buildings.Where(b => b.Type == type && !b.Removing).ToList();
        if (colony.MayorPlan is { } pending && pending.Type == type) return null;
        // The hut chain (0.201.0): no Blacksmith before a working Smelter. What it depends on comes first.
        if (huts.Count == 0 && !ChainReady(colony, mayor, type, room, ref why, out var first, depth)) return first;
        // On its way: nothing more to do until it is built.
        if (huts.Any(b => b.HasWorkOrder)) return null;
        var waiting = huts.FirstOrDefault(b => b.Level == 0 && !b.IsStation);
        if (waiting != null && StartsUnbuilt(type))
        {
            // Huts work at level 0 (0.205.0): a hut block with a worker meets the need; its building comes later.
            if (WorkersAt(colony, waiting).Count > 0) return null;
            if (!HireFor(colony, mayor, waiting))
            {
                why ??= "mayor-idle-noworker|" + BuildingName(waiting);
                MayorNote(colony, $"The {BuildingName(waiting)} has no worker, and nobody is free.");
            }
            return null;
        }
        if (waiting != null) return room ? UpgradePlan(waiting, reason, args, 1) : null;

        var empty = huts.FirstOrDefault(b => b.Level >= 1 && JobOf(b.Type) is { } job && FreePlaces(colony, b) > 0 && WorkersAt(colony, b).Count == 0);
        if (empty != null)
        {
            if (!HireFor(colony, mayor, empty))
            {
                why ??= "mayor-idle-noworker|" + BuildingName(empty);
                MayorNote(colony, $"The {BuildingName(empty)} has no worker, and nobody is free.");
            }
            return null;
        }
        bool unbuilt = StartsUnbuilt(type);
        if (!room && !unbuilt) return null;
        var grow = room ? huts.Where(b => UpgradeTo(colony, b) > 0).OrderBy(b => b.Level).FirstOrDefault() : null;
        if (grow != null) return UpgradePlan(grow, reason, args, UpgradeTo(colony, grow));
        if (huts.Count >= Math.Max(1, Config.MayorMaxPerType)) return null;
        var plan = NewBuilding(colony, type, reason, args);
        if (plan == null) why ??= "mayor-idle-nospot|@building-" + type;
        else plan.Unbuilt = unbuilt;
        return plan;
    }

    /// <summary>Gives the best jobless settler a place at the hut. False when nobody is free.</summary>
    bool HireFor(Colony colony, CitizenRecord mayor, Building hut)
    {
        if (JobOf(hut.Type) is not { } job || !hut.TakesAutoHires) return false;
        var pick = Jobless(colony).Where(c => IncapableOfJob(c, job) == null).OrderByDescending(c => JobScore(c, job)).ThenBy(c => c.CitizenId).FirstOrDefault();
        if (pick == null) return false;
        pick.JobCode = job;
        pick.WorkplacePos = hut.HutPos.Copy();
        pick.JobSetByPlayer = false;
        pick.LastMovedTotalDays = sapi.World.Calendar.TotalDays;
        LogEvent(colony, "event-mayor-hired", mayor.Name, pick.Name, "@job-" + job, BuildingName(hut));
        return true;
    }

    /// <summary>The next level the Mayor may take a building to (0 for none): one with a blueprint, up to the Town Hall's level.</summary>
    int UpgradeTo(Colony colony, Building b)
    {
        if (b.Level < 1 || b.IsStation || b.Removing || b.HasWorkOrder || b.Type is BuildingTypes.TownHall or BuildingTypes.MineshaftEntrance) return 0;
        int next = b.Level + 1;
        if (next > Blueprints.MaxLevel(b) || Blueprints.Get(b, next) == null) return 0;
        if (Config.MayorHutsUpToTownHall && next > TownHallLevel(colony)) return 0;
        return next;
    }

    /// <summary>A busy, full hut to upgrade; when every hut has caught up with the Town Hall, the Town Hall itself.</summary>
    MayorPlan? UpgradeStep(Colony colony)
    {
        var full = colony.Buildings
            .Where(b => UpgradeTo(colony, b) > 0 && !Skipped(colony, b.Type))
            .Where(b => JobOf(b.Type) is { } && WorkersAt(colony, b).Count > 0 && FreePlaces(colony, b) <= 0)
            .OrderBy(b => b.Level).ThenBy(b => b.BuildingId).FirstOrDefault();
        if (full != null) return UpgradePlan(full, "mayor-why-busy", new[] { BuildingName(full) }, UpgradeTo(colony, full));

        var townHall = colony.Buildings.Find(b => b.Type == BuildingTypes.TownHall);
        if (townHall == null || townHall.HasWorkOrder || townHall.Level < 1) return null;
        int next = townHall.Level + 1;
        if (next > Blueprints.MaxLevel(townHall) || Blueprints.Get(townHall, next) == null) return null;
        bool caughtUp = colony.Buildings.Where(b => b.Level >= 1 && !b.IsStation && !b.Removing && b.Type != BuildingTypes.TownHall && JobOf(b.Type) != null)
            .All(b => b.Level >= townHall.Level || Blueprints.MaxLevel(b) <= b.Level);
        return caughtUp ? UpgradePlan(townHall, "mayor-why-townhall", Array.Empty<string>(), next) : null;
    }

    static MayorPlan UpgradePlan(Building b, string reason, string[] args, int level) => new()
    {
        Type = b.Type, Pos = b.HutPos.Copy(), Rotation = b.Rotation, Style = b.Style, Variant = b.Variant, BuildingId = b.BuildingId,
        Reason = reason, ReasonArgs = args.ToList(), Level = level
    };

    /// <summary>Whether the Mayor may put a hut of this type down at all (as the wand would allow).</summary>
    bool CanMayorBuild(Colony colony, string type)
    {
        if (type is BuildingTypes.TownHall or BuildingTypes.SupplyCamp or BuildingTypes.MineshaftEntrance) return false;
        // One Warehouse for a colony (0.201.0: the Mayor builds it as part of the start).
        if (type == BuildingTypes.Warehouse && colony.WarehousePos != null) return false;
        if (type == BuildingTypes.Archive && !ArchiveAllowed(colony)) return false;
        if (type == BuildingTypes.Mint && !CoinageLoaded) return false;
        if (type == BuildingTypes.CourierStation) return colony.WarehousePos != null;
        var hutBlock = sapi.World.GetBlock(new AssetLocation(BlueprintLibrary.HutBlockCode(type)));
        if (hutBlock is not BlockColonyHut && !(type == BuildingTypes.Warehouse && hutBlock is BlockWarehouse)) return false;
        // Only buildings that have a blueprint (the player's builds).
        var probe = new Building { Type = type, Style = ColonyStyle(colony) };
        probe.Variant = Blueprints.VariantFor(Blueprints.StyleFor(probe), type, "");
        // A hut that works from its bare hut block (0.205.0) needs no design yet: it gets one when Aaron makes it.
        return Blueprints.MaxLevel(probe) >= 1 || StartsUnbuilt(type);
    }

    /// <summary>The asset pack the colony builds in: the Town Hall's, else the one most of its huts use.</summary>
    string ColonyStyle(Colony colony)
    {
        var townHall = colony.Buildings.Find(b => b.Type == BuildingTypes.TownHall);
        if (townHall != null && townHall.Style.Length > 0) return townHall.Style;
        return colony.Buildings.Where(b => b.Style.Length > 0).GroupBy(b => b.Style).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault()
            ?? BlueprintStyles.Default;
    }

    /// <summary>True when the request is for a building being built or upgraded.</summary>
    static bool RequestHoldsUpBuild(Colony colony, DeliveryRequest r) =>
        r.RequestedBy.StartsWith("building:") && int.TryParse(r.RequestedBy["building:".Length..], out int id)
        && colony.Buildings.Find(b => b.BuildingId == id) is { HasWorkOrder: true };

    string RequesterName(Colony colony, DeliveryRequest r)
    {
        if (r.RequestedBy.StartsWith("building:") && int.TryParse(r.RequestedBy["building:".Length..], out int id)
            && colony.Buildings.Find(b => b.BuildingId == id) is { } b) return BuildingName(b);
        return Lang.Get("createacolony:mayor-someone");
    }

    /// <summary>A short name for what a request asks for: the first thing it fits.</summary>
    string ThingName(string code)
    {
        if (ParseToolCode(code) is { } tool) return Lang.Get("createacolony:mayor-tool", tool.Type.ToString().ToLowerInvariant());
        var match = Matcher(code);
        var first = code.Split('|')[0];
        var loc = new AssetLocation(first.Split('{')[0]);
        if (!first.Contains('*') && ((CollectibleObject?)sapi.World.GetItem(loc) ?? sapi.World.GetBlock(loc)) is { Id: > 0 } exact) return new ItemStack(exact).GetName();
        var any = sapi.World.Collectibles.FirstOrDefault(c => c?.Code != null && match(new ItemStack(c)));
        return any != null ? new ItemStack(any).GetName() : loc.Path;
    }

    /// <summary>
    /// The hut type that makes or gathers a thing: config MayorMakers first, then what hut types can craft (their
    /// crafter tabs and Crafting recipes) and gather (config HutGathers). Types the Mayor can't build are left out.
    /// </summary>
    string? MakerTypeFor(Colony colony, string code)
    {
        var match = Matcher(code);
        // A tool a stone one will do for is the Primitive Craftsman's work (0.201.0): the list below reaches the
        // Blacksmith first, and a stone age colony was told to build one for a shovel.
        if (ParseToolCode(code) != null && (CanMayorBuild(colony, BuildingTypes.Craftsman) || colony.Buildings.Any(b => b.Type == BuildingTypes.Craftsman))
            && MakerKinds(colony, new Building { Type = BuildingTypes.Craftsman }).Any(k => match(k.Sample))) return BuildingTypes.Craftsman;
        foreach (var (type, patterns) in Config.MayorMakers)
        {
            if (!CanMayorBuild(colony, type) && !colony.Buildings.Any(b => b.Type == type)) continue;
            foreach (var pattern in patterns)
            {
                var loc = new AssetLocation(pattern);
                if (MakerMatches(code, loc, match)) return type;
            }
        }
        foreach (var type in MayorOrder(colony).Select(o => o.Type))
        {
            if (!CanMayorBuild(colony, type)) continue;
            if (Gathers(type, code)) return type;
            if (MakerKinds(colony, new Building { Type = type }).Any(k => match(k.Sample))) return type;
        }
        return null;
    }

    readonly Dictionary<(string Code, string Pattern), bool> makerOverlap = new();

    /// <summary>True when something fits both the request and a MayorMakers pattern.</summary>
    bool MakerMatches(string code, AssetLocation pattern, System.Func<ItemStack, bool> match)
    {
        if (makerOverlap.TryGetValue((code, pattern.ToString()), out bool known)) return known;
        bool overlap = sapi.World.Collectibles.Any(c => c?.Code != null && WildcardUtil.Match(pattern, c.Code) && match(new ItemStack(c)));
        makerOverlap[(code, pattern.ToString())] = overlap;
        return overlap;
    }

    // ---- Finding a spot ---------------------------------------------------------------------

    /// <summary>A plan for a new building of this type at the best spot, or null when none fits.</summary>
    MayorPlan? NewBuilding(Colony colony, string type, string reason, string[] args)
    {
        if (!CanMayorBuild(colony, type)) return null;
        if (type == BuildingTypes.CourierStation)
        {
            var at = StationSpot(colony);
            return at == null ? null : new MayorPlan { Type = type, Pos = at, Station = true, Reason = reason, ReasonArgs = args.ToList(), Level = 1 };
        }
        string style = ColonyStyle(colony);
        var probe = new Building { Type = type, Style = style };
        string variant = Blueprints.VariantFor(Blueprints.StyleFor(probe), type, "");
        var spot = FindSpot(colony, type, style, variant);
        if (spot == null) return null;
        return new MayorPlan
        {
            Type = type, Pos = spot.Value.Pos, Rotation = spot.Value.Rotation, Style = style, Variant = variant,
            Reason = reason, ReasonArgs = args.ToList(), Level = 1
        };
    }

    /// <summary>The ground height of each column around the Town Hall, or int.MinValue where it can't be built on.</summary>
    sealed class GroundMap
    {
        public int X0, Z0, Size;
        public int[] Height = Array.Empty<int>();
        /// <summary>Water at the top, for the Fisher; trees and stone near, for the Forester, the Quarry and the Miner.</summary>
        public bool[] Water = Array.Empty<bool>(), Tree = Array.Empty<bool>(), Stone = Array.Empty<bool>();
        public int At(int x, int z)
        {
            int i = x - X0, j = z - Z0;
            return i < 0 || j < 0 || i >= Size || j >= Size ? int.MinValue : Height[i * Size + j];
        }
        public bool Flag(bool[] flags, int x, int z)
        {
            int i = x - X0, j = z - Z0;
            return i >= 0 && j >= 0 && i < Size && j < Size && flags[i * Size + j];
        }
    }

    /// <summary>Reads the land around the Town Hall once for a look: where the ground is, and what is on it.</summary>
    GroundMap ReadGround(Colony colony)
    {
        long ms = sapi.World.ElapsedMilliseconds;
        if (groundCache is { } known && known.Colony == colony.ColonyId && ms - known.Ms < 2000) return known.Map;
        var fresh = ReadGroundNow(colony);
        groundCache = (colony.ColonyId, ms, fresh);
        return fresh;
    }

    (string Colony, long Ms, GroundMap Map)? groundCache;

    GroundMap ReadGroundNow(Colony colony)
    {
        int r = Math.Max(16, Config.MayorSearchRadius + colony.ClaimRadius);
        var map = new GroundMap { X0 = colony.TownHallPos.X - r, Z0 = colony.TownHallPos.Z - r, Size = 2 * r + 1 };
        int n = map.Size * map.Size;
        map.Height = new int[n];
        map.Water = new bool[n];
        map.Tree = new bool[n];
        map.Stone = new bool[n];
        var ba = sapi.World.BlockAccessor;
        var pos = new BlockPos(0, 0, 0);
        for (int i = 0; i < map.Size; i++)
        for (int j = 0; j < map.Size; j++)
        {
            int x = map.X0 + i, z = map.Z0 + j, k = i * map.Size + j;
            map.Height[k] = int.MinValue;
            pos.Set(x, colony.TownHallPos.Y, z);
            if (ba.GetChunkAtBlockPos(pos) == null) continue;
            int top = ba.GetRainMapHeightAt(x, z);
            for (int y = top; y > top - 48 && y > 1; y--)
            {
                pos.Y = y;
                var block = ba.GetBlock(pos);
                if (block.Id == 0) continue;
                if (block.IsLiquid())
                {
                    map.Water[k] = true;
                    break;
                }
                string path = block.Code?.Path ?? "";
                if (block.Code?.Domain == "game" && (path.StartsWith("log-grown") || path.StartsWith("leaves") || path.StartsWith("logsection-grown")))
                {
                    map.Tree[k] = true;
                    continue;
                }
                // Plants, snow and loose stones on the ground are cleared by the builder.
                if (block.BlockMaterial is EnumBlockMaterial.Plant or EnumBlockMaterial.Leaves or EnumBlockMaterial.Snow || block.Replaceable >= 5000
                    || path.StartsWith("loosestones") || path.StartsWith("looseflints") || path.StartsWith("looseboulders"))
                {
                    continue;
                }
                if (BlueprintLibrary.IsNaturalGround(block))
                {
                    map.Height[k] = y;
                    if (path.StartsWith("rock-")) map.Stone[k] = true;
                }
                // Anything else (a path, a wall, a chest) is someone's: the Mayor never builds over it.
                break;
            }
        }
        return map;
    }

    /// <summary>The space a building would take at pos (its biggest level), as for buildings that stand.</summary>
    Cuboidi PlannedBox(string type, string style, string variant, BlockPos pos, int rotation) =>
        Footprint(new Building { Type = type, HutPos = pos.Copy(), Rotation = rotation, Style = style, Variant = variant });

    /// <summary>
    /// The best spot for a new building: inside the border, flat enough, on natural ground, away from other buildings
    /// (MayorGap), with room in front (MayorFrontClear), its front facing the Town Hall. Close to the Town Hall is
    /// better; some types like their own land (trees for the Forester, water for the Fisher, stone for the Quarry).
    /// </summary>
    (BlockPos Pos, int Rotation)? FindSpot(Colony colony, string type, string style, string variant)
    {
        var map = ReadGround(colony);
        var th = colony.TownHallPos;
        int? front = Blueprints.Get(style, type, 1, 0, variant)?.Front;
        // A build whose floor is a step above its yard has its hut block that much above the land (0.201.0).
        int rise = Blueprints.Get(style, type, 1, 0, variant)?.GroundRise ?? 0;
        int gap = Math.Max(0, Config.MayorGap);
        var others = colony.Buildings.Where(b => !b.IsStation && !b.Removing).Select(b => Grow(Footprint(b), gap)).ToList();
        if (colony.MayorPlan is { BuildingId: 0, Station: false } pending)
        {
            others.Add(Grow(PlannedBox(pending.Type, pending.Style, pending.Variant, pending.Pos, pending.Rotation), gap));
        }
        var refused = colony.MayorRefused.Where(s => s.StartsWith(type + "|")).Select(s => s.Split('|')).Where(p => p.Length == 3)
            .Select(p => (int.Parse(p[1]), int.Parse(p[2]))).ToHashSet();

        (BlockPos, int)? best = null;
        double bestScore = double.MaxValue;
        int r = (map.Size - 1) / 2;
        int tried = 0;
        // The building's space for each turn, around its hut block at 0,0,0.
        var boxes = new Dictionary<int, Cuboidi>();
        foreach (int turn in new[] { 0, 90, 180, 270 }) boxes[turn] = PlannedBox(type, style, variant, new BlockPos(0, 0, 0), turn);
        // Close to the Town Hall first (0.201.0): the nearest ring that has a spot wins, and the search only widens when
        // a ring has none. Before, trees far out pulled a Gatherer's Hut 53 blocks from the Town Hall.
        var nearRings = (Config.MayorNearRings ?? Array.Empty<int>()).Where(n => n > 0).OrderBy(n => n).ToArray();
        int ring = 0;
        foreach (var (dx, dz) in Rings(r, 2))
        {
            double out2 = dx * dx + dz * dz;
            bool done = false;
            while (ring < nearRings.Length && out2 > (double)nearRings[ring] * nearRings[ring])
            {
                if (best != null) done = true;
                ring++;
            }
            if (done) break;
            int x = th.X + dx, z = th.Z + dz;
            int ground = map.At(x, z);
            if (ground == int.MinValue || refused.Any(f => Math.Abs(f.Item1 - x) <= 4 && Math.Abs(f.Item2 - z) <= 4)) continue;
            var pos = new BlockPos(x, ground + 1 + rise, z);
            int facing = FacingTowards(dx, dz);
            int rotation = front is { } f ? GameMath.Mod(f - facing, 4) * 90 : 0;
            if (tried++ > 2500) break;
            var rel = boxes[rotation];
            var box = new Cuboidi(rel.X1 + x, rel.Y1 + pos.Y, rel.Z1 + z, rel.X2 + x, rel.Y2 + pos.Y, rel.Z2 + z);
            if (SpotProblem(colony, map, type, pos, box, front == null ? null : facing, others, rise) != null) continue;

            double score = Math.Sqrt(dx * dx + dz * dz) + LandScore(map, type, x, z, Math.Sqrt(dx * dx + dz * dz), colony.ClaimRadius);
            if (double.IsPositiveInfinity(score)) continue;
            if (score < bestScore)
            {
                bestScore = score;
                best = (pos, rotation);
            }
            // Types that don't care about their land take the nearest good spot.
            if (!CaresAboutLand(type)) break;
        }
        return best;
    }

    static bool CaresAboutLand(string type) => type is BuildingTypes.Forester or BuildingTypes.Fisher or BuildingTypes.Quarry or BuildingTypes.Miner
        or BuildingTypes.GuardTower or BuildingTypes.Farmer or BuildingTypes.Gatherer;

    /// <summary>Lower is better: how well the land around fits the type (added to the distance from the Town Hall).</summary>
    static double LandScore(GroundMap map, string type, int x, int z, double distance, int border)
    {
        int Count(bool[] flags, int radius)
        {
            int n = 0;
            for (int i = -radius; i <= radius; i += 2)
            for (int j = -radius; j <= radius; j += 2)
            {
                if (map.Flag(flags, x + i, z + j)) n++;
            }
            return n;
        }
        return type switch
        {
            BuildingTypes.Forester or BuildingTypes.Gatherer => -2.0 * Count(map.Tree, 14),
            BuildingTypes.Fisher => Count(map.Water, 10) == 0 ? double.PositiveInfinity : -3.0 * Count(map.Water, 10),
            BuildingTypes.Quarry or BuildingTypes.Miner => -3.0 * Count(map.Stone, 10),
            BuildingTypes.Farmer => 2.0 * Count(map.Tree, 8) + 2.0 * Count(map.Water, 6),
            // Guard towers stand out towards the border.
            BuildingTypes.GuardTower => -2.0 * Math.Min(distance, Math.Max(8, border)),
            _ => 0
        };
    }

    /// <summary>Offsets around the centre, nearest first, every step blocks.</summary>
    static IEnumerable<(int, int)> Rings(int radius, int step)
    {
        var all = new List<(int, int, int)>();
        for (int dx = -radius; dx <= radius; dx += step)
        for (int dz = -radius; dz <= radius; dz += step)
        {
            if (dx == 0 && dz == 0) continue;
            all.Add((dx, dz, dx * dx + dz * dz));
        }
        return all.OrderBy(a => a.Item3).ThenBy(a => a.Item1).ThenBy(a => a.Item2).Select(a => (a.Item1, a.Item2));
    }

    /// <summary>The side (BlockFacing HorizontalAngleIndex) that looks from a spot back towards the Town Hall, dx/dz away.</summary>
    static int FacingTowards(int dx, int dz)
    {
        if (Math.Abs(dx) >= Math.Abs(dz)) return dx > 0 ? BlockFacing.WEST.HorizontalAngleIndex : BlockFacing.EAST.HorizontalAngleIndex;
        return dz > 0 ? BlockFacing.NORTH.HorizontalAngleIndex : BlockFacing.SOUTH.HorizontalAngleIndex;
    }

    static Cuboidi Grow(Cuboidi box, int by) => new(box.X1 - by, box.Y1, box.Z1 - by, box.X2 + by, box.Y2, box.Z2 + by);

    static bool Overlaps2D(Cuboidi a, Cuboidi b) => a.X1 <= b.X2 && b.X1 <= a.X2 && a.Z1 <= b.Z2 && b.Z1 <= a.Z2;

    /// <summary>Why a building can't stand at pos (a lang key), or null when it can.</summary>
    string? SpotProblem(Colony colony, GroundMap map, string type, BlockPos pos, Cuboidi box, int? facing, List<Cuboidi> others, int rise = 0)
    {
        if (others.Any(o => Overlaps2D(o, box))) return "mayor-spot-taken";
        // Room in front of the door.
        if (facing is { } side)
        {
            int c = Math.Max(0, Config.MayorFrontClear);
            var face = BlockFacing.HORIZONTALS_ANGLEORDER[side];
            var ahead = new Cuboidi(
                face == BlockFacing.WEST ? box.X1 - c : face == BlockFacing.EAST ? box.X2 + 1 : box.X1, box.Y1,
                face == BlockFacing.NORTH ? box.Z1 - c : face == BlockFacing.SOUTH ? box.Z2 + 1 : box.Z1,
                face == BlockFacing.WEST ? box.X1 - 1 : face == BlockFacing.EAST ? box.X2 + c : box.X2, box.Y2,
                face == BlockFacing.NORTH ? box.Z1 - 1 : face == BlockFacing.SOUTH ? box.Z2 + c : box.Z2);
            if (others.Any(o => Overlaps2D(o, ahead))) return "mayor-spot-front";
            // And ground to walk on there (0.200.0): not water, not a cliff, or nobody gets to the door.
            if (c > 0)
            {
                int bad = 0, cells = 0, level = pos.Y - 1 - rise, steep = Math.Max(0, Config.MayorMaxSlope) + 1;
                for (int x = ahead.X1; x <= ahead.X2; x++)
                for (int z = ahead.Z1; z <= ahead.Z2; z++)
                {
                    cells++;
                    int h = map.At(x, z);
                    if (h == int.MinValue || Math.Abs(h - level) > steep) bad++;
                }
                if (bad * 2 > cells) return "mayor-spot-front";
            }
        }
        // Inside the border, corners and hut block.
        foreach (var (x, z) in new[] { (box.X1, box.Z1), (box.X2, box.Z1), (box.X1, box.Z2), (box.X2, box.Z2), (pos.X, pos.Z) })
        {
            if (GetAt(new BlockPos(x, pos.Y, z)) != colony) return "mayor-spot-border";
        }
        // Flat, dry, natural ground under all of it.
        int slope = Math.Max(0, Config.MayorMaxSlope);
        int floor = pos.Y - 1 - rise;
        for (int x = box.X1; x <= box.X2; x++)
        for (int z = box.Z1; z <= box.Z2; z++)
        {
            int h = map.At(x, z);
            if (h == int.MinValue) return map.Flag(map.Water, x, z) ? "mayor-spot-water" : "mayor-spot-ground";
            if (Math.Abs(h - floor) > slope) return "mayor-spot-slope";
        }
        if (!sapi.World.BlockAccessor.GetBlock(pos).IsReplacableBy(sapi.World.GetBlock(new AssetLocation(BlueprintLibrary.HutBlockCode(type))))) return "mayor-spot-taken";
        return null;
    }

    /// <summary>Whether a plan waiting for a yes can still go where it is (nothing was built there since).</summary>
    bool PlanStillFits(Colony colony, MayorPlan plan)
    {
        if (plan.BuildingId != 0)
        {
            var b = colony.Buildings.Find(x => x.BuildingId == plan.BuildingId);
            return b != null && !b.Removing && !b.HasWorkOrder && b.Level == plan.Level - 1;
        }
        if (plan.Station) return colony.WarehousePos != null && sapi.World.BlockAccessor.GetBlock(plan.Pos).Replaceable >= 6000;
        var map = ReadGround(colony);
        int gap = Math.Max(0, Config.MayorGap);
        var others = colony.Buildings.Where(b => !b.IsStation && !b.Removing).Select(b => Grow(Footprint(b), gap)).ToList();
        int? front = Blueprints.Get(plan.Style, plan.Type, 1, 0, plan.Variant)?.Front;
        int? facing = front == null ? null : FacingTowards(plan.Pos.X - colony.TownHallPos.X, plan.Pos.Z - colony.TownHallPos.Z);
        int rise = Blueprints.Get(plan.Style, plan.Type, 1, 0, plan.Variant)?.GroundRise ?? 0;
        return SpotProblem(colony, map, plan.Type, plan.Pos, PlannedBox(plan.Type, plan.Style, plan.Variant, plan.Pos, plan.Rotation), facing, others, rise) == null;
    }

    /// <summary>A free spot beside the Warehouse block for a Courier's Station: air with solid ground under it.</summary>
    BlockPos? StationSpot(Colony colony)
    {
        if (colony.WarehousePos is not { } w) return null;
        var ba = sapi.World.BlockAccessor;
        var block = sapi.World.GetBlock(new AssetLocation(BlueprintLibrary.HutBlockCode(BuildingTypes.CourierStation)));
        if (block == null) return null;
        for (int d = 1; d <= 3; d++)
        for (int dy = 0; dy >= -1; dy--)
        foreach (var face in BlockFacing.HORIZONTALS)
        {
            var at = w.AddCopy(face.Normali.X * d, dy, face.Normali.Z * d);
            if (!ba.GetBlock(at).IsReplacableBy(block) || !ba.GetBlock(at.DownCopy()).SideSolid[BlockFacing.UP.Index]) continue;
            if (colony.Buildings.Any(b => b.HutPos.Equals(at))) continue;
            if (StationHostAt(colony, BuildingTypes.CourierStation, at) == null) continue;
            return at;
        }
        return null;
    }

    // ---- Carrying a plan out ----------------------------------------------------------------

    /// <summary>Does what the plan says, as far as it can. The plan is cleared when done or no longer possible.</summary>
    void CarryOut(Colony colony, CitizenRecord mayor, MayorPlan plan)
    {
        if (plan.BuildingId != 0)
        {
            colony.MayorPlan = null;
            var b = colony.Buildings.Find(x => x.BuildingId == plan.BuildingId);
            if (b == null) return;
            // Fill in later (0.205.0): the same level again puts in the blocks the last build left out.
            if (plan.Level == b.Level && b.Level >= 1)
            {
                b.LaterBlocks = false;
                if (StartRepair(colony, b) is { } repairError)
                {
                    LogEvent(colony, "event-mayor-cant", mayor.Name, BuildingName(b), "@" + repairError["createacolony:".Length..]);
                    return;
                }
                LogEvent(colony, "event-mayor-fillin", mayor.Name, BuildingName(b));
                return;
            }
            if (StartWorkOrder(colony, b) is { } error)
            {
                LogEvent(colony, "event-mayor-cant", mayor.Name, BuildingName(b), "@" + error["createacolony:".Length..]);
                return;
            }
            LogEvent(colony, plan.Level <= 1 ? "event-mayor-ordered" : "event-mayor-upgrade", mayor.Name, BuildingName(b), plan.Level.ToString(), ReasonText(plan));
            return;
        }

        if (!PlanStillFits(colony, plan))
        {
            colony.MayorPlan = null;
            return;
        }
        string code = BlueprintLibrary.HutBlockCode(plan.Type);
        if (!colony.MayorFreeHuts && !TakeHutBlock(colony, code))
        {
            AskForHutBlock(colony, code);
            SetMayorIdle(colony, "mayor-idle-hutblock", "@building-" + plan.Type);
            return;
        }
        colony.Requests.RemoveAll(r => r.RequestedBy == MayorTag && r.ItemCode == code);
        colony.MayorPlan = null;
        var placed = plan.Station ? PlaceStation(colony, plan) : PlaceHut(colony, plan);
        if (placed == null) return;
        LogEvent(colony, "event-mayor-placed", mayor.Name, "@building-" + plan.Type, ReasonText(plan));
        if (plan.Unbuilt && !plan.Station)
        {
            // Huts work at level 0 (0.205.0): no build yet, a worker straight away.
            if (!HireFor(colony, mayor, placed)) MayorNote(colony, $"The {BuildingName(placed)} has no worker yet, and nobody is free.");
            return;
        }
        if (!plan.Station && StartWorkOrder(colony, placed) is { } orderError)
        {
            LogEvent(colony, "event-mayor-cant", mayor.Name, BuildingName(placed), "@" + orderError["createacolony:".Length..]);
        }
    }

    /// <summary>Containers the Mayor takes hut blocks from: the Town Hall's, the warehouse, and the huts that make them.</summary>
    IEnumerable<BlockEntityContainer> HutBlockStores(Colony colony)
    {
        var townHall = colony.Buildings.Find(b => b.Type == BuildingTypes.TownHall);
        var stores = new List<BlockEntityContainer>();
        if (townHall != null) stores.AddRange(HutStorage(colony, townHall));
        stores.AddRange(WarehouseContainers(colony));
        foreach (var hut in colony.Buildings.Where(b => b.Type is BuildingTypes.Sawmill or BuildingTypes.Builder && b.Level >= 1)) stores.AddRange(HutStorage(colony, hut));
        return stores.Distinct();
    }

    /// <summary>Takes one hut block of this code from the colony's storage. False when there is none.</summary>
    bool TakeHutBlock(Colony colony, string code)
    {
        var loc = new AssetLocation(code);
        foreach (var container in HutBlockStores(colony))
        {
            foreach (var slot in container.Inventory)
            {
                if (slot.Empty || slot.Itemstack.Collectible.Code != loc) continue;
                slot.TakeOut(1);
                slot.MarkDirty();
                container.MarkDirty(true);
                return true;
            }
        }
        return false;
    }

    /// <summary>Asks for a hut block on the Requests tab (once), to the Town Hall's storage or the warehouse.</summary>
    void AskForHutBlock(Colony colony, string code)
    {
        if (colony.Requests.Any(r => r.RequestedBy == MayorTag && r.ItemCode == code)) return;
        var townHall = colony.Buildings.Find(b => b.Type == BuildingTypes.TownHall);
        var dest = (townHall == null ? null : HutStorage(colony, townHall).FirstOrDefault()) ?? WarehouseContainers(colony).FirstOrDefault();
        if (dest == null) return;
        AddRequest(colony, code, 1, dest.Pos, MayorTag);
    }

    /// <summary>Puts a hut block down for the colony, as the Builder's Wand does for a player. Null when it can't.</summary>
    Building? PlaceHut(Colony colony, MayorPlan plan)
    {
        var world = sapi.World;
        var block = world.GetBlock(new AssetLocation(BlueprintLibrary.HutBlockCode(plan.Type)));
        bool warehouse = plan.Type == BuildingTypes.Warehouse && block is BlockWarehouse && colony.WarehousePos == null;
        if (block is not BlockColonyHut && !warehouse) return null;
        if (!world.BlockAccessor.GetBlock(plan.Pos).IsReplacableBy(block)) return null;
        world.BlockAccessor.SetBlock(block!.BlockId, plan.Pos);
        if (warehouse) colony.WarehousePos = plan.Pos.Copy();
        var building = AddBuilding(colony, plan.Type, plan.Pos, plan.Rotation);
        building.Style = plan.Style;
        building.Variant = plan.Variant;
        if (world.BlockAccessor.GetBlockEntity(plan.Pos) is BlockEntityColonyHut hut) hut.SetRotation(plan.Rotation);
        AfterHutPlaced(colony, building);
        MarkDirty(colony);
        return building;
    }

    /// <summary>Puts a Courier's Station down beside the Warehouse block.</summary>
    Building? PlaceStation(Colony colony, MayorPlan plan)
    {
        var world = sapi.World;
        var block = world.GetBlock(new AssetLocation(BlueprintLibrary.HutBlockCode(plan.Type)));
        if (block == null || !world.BlockAccessor.GetBlock(plan.Pos).IsReplacableBy(block)) return null;
        var host = StationHostAt(colony, plan.Type, plan.Pos);
        if (host == null) return null;
        world.BlockAccessor.SetBlock(block.BlockId, plan.Pos);
        if (world.BlockAccessor.GetBlockEntity(plan.Pos) is BlockEntityColonyHut hut) hut.SetRotation(0);
        return AddStation(colony, plan.Type, plan.Pos, host);
    }

    // ---- Answers from the Mayor tab -------------------------------------------------------------

    /// <summary>
    /// The Mayor tab's buttons (officers): Text "mode|n", "approve", "other", "skip", "up|type", "down|type",
    /// "toggle|type", "reset" (the default build order) or "look" (look again now). Returns a lang key on failure.
    /// </summary>
    public string? MayorAction(Colony colony, string text)
    {
        var parts = (text ?? "").Split('|');
        string arg = parts.Length > 1 ? parts[1] : "";
        var mayor = MayorOnDuty(colony);
        int day = (int)sapi.World.Calendar.TotalDays;
        switch (parts[0])
        {
            case "mode":
                if (!int.TryParse(arg, out int mode) || mode < MayorModes.Off || mode > MayorModes.OnOwn) return "createacolony:bad-request";
                colony.MayorMode = mode;
                if (mode == MayorModes.Off) colony.MayorPlan = null;
                return null;
            case "approve":
                if (colony.MayorPlan is not { } plan) return "createacolony:mayor-no-plan";
                if (mayor == null) return "createacolony:mayor-not-on-duty";
                plan.Approved = true;
                CarryOut(colony, mayor, plan);
                return null;
            case "other":
            case "skip":
                if (colony.MayorPlan is not { } refused) return "createacolony:mayor-no-plan";
                colony.MayorPlan = null;
                if (refused.BuildingId == 0) colony.MayorRefused.Add($"{refused.Type}|{refused.Pos.X}|{refused.Pos.Z}");
                if (colony.MayorRefused.Count > 200) colony.MayorRefused.RemoveRange(0, colony.MayorRefused.Count - 200);
                if (parts[0] == "skip") colony.MayorSkipToday.Add(refused.Type + "|" + day);
                if (mayor != null) MayorLook(colony, mayor);
                return null;
            case "look":
                if (mayor == null) return "createacolony:mayor-not-on-duty";
                colony.MayorLastLookHours = sapi.World.Calendar.TotalHours;
                MayorLook(colony, mayor);
                return null;
            case "up":
            case "down":
            {
                var order = MayorOrder(colony).Select(o => o.Type).ToList();
                int i = order.IndexOf(arg);
                if (i < 0) return "createacolony:bad-request";
                int j = parts[0] == "up" ? i - 1 : i + 1;
                if (j < 0 || j >= order.Count) return null;
                (order[i], order[j]) = (order[j], order[i]);
                colony.MayorOrder = order;
                return null;
            }
            case "toggle":
                if (!MayorOrder(colony).Any(o => o.Type == arg)) return "createacolony:bad-request";
                if (!colony.MayorSkip.Remove(arg)) colony.MayorSkip.Add(arg);
                return null;
            case "reset":
                colony.MayorOrder = new List<string>();
                colony.MayorSkip = new List<string>();
                return null;
            case "free":
                colony.MayorFreeHuts = arg == "1";
                return null;
        }
        return "createacolony:bad-request";
    }

    // ---- Text -------------------------------------------------------------------------------

    /// <summary>"a Forester's Hut" or "level 2 of the Residence".</summary>
    public string PlanName(MayorPlan plan) => plan.BuildingId == 0
        ? Lang.Get("createacolony:building-" + plan.Type)
        : Lang.Get("createacolony:mayor-plan-level", plan.Level, Lang.Get("createacolony:building-" + plan.Type));

    /// <summary>Why the Mayor wants it, in words.</summary>
    public string ReasonText(MayorPlan plan)
    {
        if (plan.Reason.Length == 0) return "";
        var args = plan.ReasonArgs.Select(a => a.StartsWith('@') ? Lang.Get("createacolony:" + a[1..]) : a).Cast<object>().ToArray();
        return Lang.Get("createacolony:" + plan.Reason, args);
    }

    /// <summary>What the Mayor is waiting on, in words, for the Mayor tab; "" when nothing.</summary>
    public string MayorIdleText(Colony colony)
    {
        if (colony.MayorIdle.Length == 0) return "";
        var args = colony.MayorIdleArgs.Select(a => a.StartsWith('@') ? Lang.Get("createacolony:" + a[1..]) : a).Cast<object>().ToArray();
        return Lang.Get("createacolony:" + colony.MayorIdle, args);
    }
}
