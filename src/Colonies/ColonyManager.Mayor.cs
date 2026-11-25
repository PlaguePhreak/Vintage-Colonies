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

    /// <summary>The Mayor when they are fit to work: not knocked out and not sulking.</summary>
    public CitizenRecord? MayorOnDuty(Colony colony) =>
        MayorOf(colony) is { IsDowned: false } m && !IsBrokenDown(m) ? m : null;

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
        TrackShortages(colony);
        EnsureBuilderJob(colony, mayor);
        SetMayorIdle(colony, "");
        if (colony.MayorMode == MayorModes.Off) return;

        if (colony.MayorPlan is { } plan)
        {
            // Waiting for a yes, or (approved) for a hut block: try again.
            if (plan.Approved) CarryOut(colony, mayor, plan);
            else if (!PlanStillFits(colony, plan)) colony.MayorPlan = null;
            if (colony.MayorPlan != null) return;
        }

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
        LogEvent(colony, "event-mayor-builder", mayor.Name, pick.Name);
    }

    // ---- Choosing the next step ---------------------------------------------------------------

    /// <summary>Huts of a type, built or on their way (a hut block down, or a plan).</summary>
    int CountOf(Colony colony, string type) =>
        colony.Buildings.Count(b => b.Type == type && !b.Removing) + (colony.MayorPlan is { BuildingId: 0 } p && p.Type == type ? 1 : 0);

    bool Skipped(Colony colony, string type) =>
        colony.MayorSkip.Contains(type) || colony.MayorSkipToday.Any(s => s.StartsWith(type + "|"));

    /// <summary>True while the builders have room for another job (MayorOrdersPerBuilder each).</summary>
    bool BuildersHaveRoom(Colony colony)
    {
        int builders = Math.Max(1, colony.Citizens.Count(c => c.Alive && !c.Visitor && c.JobCode == Jobs.Builder));
        int orders = colony.Buildings.Count(b => b.HasWorkOrder && !b.Upkeep && !b.Repairing);
        return orders <= builders * Math.Max(1, Config.MayorOrdersPerBuilder);
    }

    /// <summary>The Mayor's next step, most urgent first; null when there is nothing to do (the reason goes on the Mayor tab).</summary>
    MayorPlan? NextStep(Colony colony, CitizenRecord mayor)
    {
        var settlers = colony.Citizens.Count(c => c.Alive && !c.Visitor);
        bool room = BuildersHaveRoom(colony);
        string? why = null;

        // 1. Food.
        var foodTypes = new[] { BuildingTypes.Farmer, BuildingTypes.Hunter, BuildingTypes.Fisher };
        int foodHuts = foodTypes.Sum(t => CountOf(colony, t));
        if (settlers > foodHuts * Math.Max(1, Config.MayorSettlersPerFoodHut))
        {
            var type = foodTypes.Where(t => !Skipped(colony, t) && CountOf(colony, t) < Config.MayorMaxPerType + 2)
                .OrderBy(t => CountOf(colony, t) + (t == BuildingTypes.Farmer ? 0 : t == BuildingTypes.Hunter ? 0.5 : 1)).FirstOrDefault();
            if (type != null && ForNeed(colony, mayor, type, "mayor-why-food", new[] { settlers.ToString() }, room, ref why) is { } food) return food;
        }

        // 2. and 4. Things the colony is short of: those holding up a building first.
        double hours = sapi.World.Calendar.TotalHours;
        var shortOf = colony.Requests
            .Where(r => r.Remaining > 0 && r.RequestedBy != MayorTag && colony.MayorShortSince.TryGetValue(r.RequestId, out double since) && hours - since >= Config.MayorShortHours)
            .Where(r => CountItems(WarehouseContainers(colony), Matcher(r.ItemCode)) < r.Remaining)
            .OrderByDescending(r => RequestHoldsUpBuild(colony, r))
            .ThenBy(r => r.RequestId)
            .ToList();
        bool bedsChecked = false;
        foreach (var request in shortOf)
        {
            bool forBuild = RequestHoldsUpBuild(colony, request);
            if (!forBuild && !bedsChecked)
            {
                bedsChecked = true;
                if (BedStep(colony, mayor, room, ref why) is { } beds) return beds;
            }
            var maker = MakerTypeFor(colony, request.ItemCode);
            string thing = ThingName(request.ItemCode);
            if (maker == null)
            {
                why ??= "mayor-idle-nomaker|" + thing;
                continue;
            }
            string who = RequesterName(colony, request);
            var step = ForNeed(colony, mayor, maker, forBuild ? "mayor-why-build-short" : "mayor-why-short", new[] { who, thing }, room, ref why);
            if (step != null) return step;
        }

        // 3. Beds.
        if (!bedsChecked && BedStep(colony, mayor, room, ref why) is { } bed) return bed;

        if (!room)
        {
            SetIdleFrom(colony, why ?? "mayor-idle-busy");
            return null;
        }

        // 5. A second builder.
        if (settlers >= Config.MayorSecondBuilderAt && CountOf(colony, BuildingTypes.Builder) < 2 && !Skipped(colony, BuildingTypes.Builder)
            && NewBuilding(colony, BuildingTypes.Builder, "mayor-why-builder", new[] { settlers.ToString() }) is { } builder) return builder;

        // 6. The build order.
        foreach (var (type, from) in MayorOrder(colony))
        {
            if (settlers < from || Skipped(colony, type) || !CanMayorBuild(colony, type)) continue;
            // A hut block someone put down and never ordered: order it.
            if (colony.Buildings.FirstOrDefault(b => b.Type == type && b.Level == 0 && !b.HasWorkOrder && !b.IsStation && !b.Removing) is { } unbuilt)
            {
                return UpgradePlan(unbuilt, "mayor-why-order", Array.Empty<string>(), 1);
            }
            if (CountOf(colony, type) > 0) continue;
            var step = NewBuilding(colony, type, "mayor-why-order", Array.Empty<string>());
            if (step != null) return step;
            why ??= "mayor-idle-nospot|@building-" + type;
        }

        // 7. Upgrades.
        if (UpgradeStep(colony) is { } upgrade) return upgrade;
        SetIdleFrom(colony, why ?? "mayor-idle-nothing");
        return null;
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

    /// <summary>
    /// What to do about a need a hut type meets: build one if there is none, order a waiting hut block built, find
    /// a worker for an empty hut, or upgrade (or add another) when it can't keep up. Null when nothing can be done now.
    /// </summary>
    MayorPlan? ForNeed(Colony colony, CitizenRecord mayor, string type, string reason, string[] args, bool room, ref string? why)
    {
        if (Skipped(colony, type) || !CanMayorBuild(colony, type)) return null;
        var huts = colony.Buildings.Where(b => b.Type == type && !b.Removing).ToList();
        if (colony.MayorPlan is { } pending && pending.Type == type) return null;
        // On its way: nothing more to do until it is built.
        if (huts.Any(b => b.HasWorkOrder)) return null;
        var waiting = huts.FirstOrDefault(b => b.Level == 0 && !b.IsStation);
        if (waiting != null) return room ? UpgradePlan(waiting, reason, args, 1) : null;

        var empty = huts.FirstOrDefault(b => b.Level >= 1 && JobOf(b.Type) is { } job && FreePlaces(colony, b) > 0 && WorkersAt(colony, b).Count == 0);
        if (empty != null)
        {
            if (!HireFor(colony, mayor, empty)) why ??= "mayor-idle-noworker|" + BuildingName(empty);
            return null;
        }
        if (!room) return null;
        var grow = huts.Where(b => UpgradeTo(colony, b) > 0).OrderBy(b => b.Level).FirstOrDefault();
        if (grow != null) return UpgradePlan(grow, reason, args, UpgradeTo(colony, grow));
        if (huts.Count >= Math.Max(1, Config.MayorMaxPerType)) return null;
        var plan = NewBuilding(colony, type, reason, args);
        if (plan == null) why ??= "mayor-idle-nospot|@building-" + type;
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
        if (type is BuildingTypes.TownHall or BuildingTypes.Warehouse or BuildingTypes.SupplyCamp or BuildingTypes.MineshaftEntrance) return false;
        if (type == BuildingTypes.Archive && !ArchiveAllowed(colony)) return false;
        if (type == BuildingTypes.Mint && !CoinageLoaded) return false;
        if (type == BuildingTypes.CourierStation) return colony.WarehousePos != null;
        if (sapi.World.GetBlock(new AssetLocation(BlueprintLibrary.HutBlockCode(type))) is not BlockColonyHut) return false;
        // Only buildings that have a blueprint (the player's builds).
        var probe = new Building { Type = type, Style = ColonyStyle(colony) };
        probe.Variant = Blueprints.VariantFor(Blueprints.StyleFor(probe), type, "");
        return Blueprints.MaxLevel(probe) >= 1;
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
        foreach (var (dx, dz) in Rings(r, 2))
        {
            int x = th.X + dx, z = th.Z + dz;
            int ground = map.At(x, z);
            if (ground == int.MinValue || refused.Any(f => Math.Abs(f.Item1 - x) <= 4 && Math.Abs(f.Item2 - z) <= 4)) continue;
            var pos = new BlockPos(x, ground + 1, z);
            int facing = FacingTowards(dx, dz);
            int rotation = front is { } f ? GameMath.Mod(f - facing, 4) * 90 : 0;
            if (tried++ > 2500) break;
            var rel = boxes[rotation];
            var box = new Cuboidi(rel.X1 + x, rel.Y1 + pos.Y, rel.Z1 + z, rel.X2 + x, rel.Y2 + pos.Y, rel.Z2 + z);
            if (SpotProblem(colony, map, type, pos, box, front == null ? null : facing, others) != null) continue;

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
    string? SpotProblem(Colony colony, GroundMap map, string type, BlockPos pos, Cuboidi box, int? facing, List<Cuboidi> others)
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
                int bad = 0, cells = 0, level = pos.Y - 1, steep = Math.Max(0, Config.MayorMaxSlope) + 1;
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
        int floor = pos.Y - 1;
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
        return SpotProblem(colony, map, plan.Type, plan.Pos, PlannedBox(plan.Type, plan.Style, plan.Variant, plan.Pos, plan.Rotation), facing, others) == null;
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
        if (world.GetBlock(new AssetLocation(BlueprintLibrary.HutBlockCode(plan.Type))) is not BlockColonyHut block) return null;
        if (!world.BlockAccessor.GetBlock(plan.Pos).IsReplacableBy(block)) return null;
        world.BlockAccessor.SetBlock(block.BlockId, plan.Pos);
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
