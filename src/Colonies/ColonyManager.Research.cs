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

/// <summary>One research node from config/research.json.</summary>
public class ResearchDef
{
    public string Code = "";
    /// <summary>settlement, production, population, defence, exploration (or temporal).</summary>
    public string Branch = "";
    public int Tier = 1;
    /// <summary>Research codes that must be finished first.</summary>
    public string[] Needs = Array.Empty<string>();
    /// <summary>The Town Hall level it needs, and how many settlers.</summary>
    public int TownHall;
    public int Settlers;
    /// <summary>A building type the colony must have, at least at BuildingLevel (0: placed is enough).</summary>
    public string Building = "";
    public int BuildingLevel;
    /// <summary>Research Points it takes.</summary>
    public int Points = 100;
    /// <summary>Knowledge Tokens it costs, taken when it is started.</summary>
    public int Tokens;
    /// <summary>Items it takes, brought to the Archive by couriers before the work starts.</summary>
    public List<ResearchCost> Cost = new();
    /// <summary>What it does once done: effect key to value (see ColonyManager.ResearchMult and ResearchAdd).</summary>
    public Dictionary<string, float> Effects = new();
    /// <summary>Knowledge Tokens the colony gets when it is done.</summary>
    public int GivesTokens;
}

public class ResearchCost
{
    /// <summary>An item or block code, wildcards allowed: "game:seeds-*".</summary>
    public string Code = "";
    public int Quantity = 1;
    /// <summary>A lang key for its name; the code's own name when empty.</summary>
    public string Name = "";
}

/// <summary>
/// The colony's research (Aaron's design, 0.166.0). An Archive (needs the Town Hall at config ArchiveTownHallLevel)
/// holds researchers. Officers start research nodes on the Archive's Research tab; each node is gated by
/// finished research, the Town Hall level, the number of settlers and a building, and costs items (couriers bring
/// them to the Archive) and Research Points. Researchers make the points by studying at the Archive: every piece
/// of work (config ResearchSecondsPerWork, shortened by skill and mood) gives ResearchPointsPerWork, times the
/// Archive's tier, its room (a table, a shelf with books and a light nearby) and its quiet (no forges or saws
/// close by). Up to ResearchMaxPerNode researchers work on one node; the Archive runs one node per tier at once.
/// Finished research belongs to the colony and stays even if the Archive is removed. Its effects are read with
/// ResearchMult and ResearchAdd where each system needs them. Nothing that existed before research is locked.
/// </summary>
public partial class ColonyManager
{
    const string ResearchAsset = "config/research.json";
    /// <summary>The Archive's top tier: tiers above it count as this one.</summary>
    public const int ArchiveMaxTier = 3;
    /// <summary>Researchers an Archive takes per tier.</summary>
    public const int ResearchersPerTier = 2;

    List<ResearchDef>? researchDefs;

    class ResearchFile { public List<ResearchDef> Research = new(); }

    /// <summary>All research nodes, in the order of research.json.</summary>
    public List<ResearchDef> ResearchDefs()
    {
        if (researchDefs != null) return researchDefs;
        var loaded = sapi.Assets.TryGet(new AssetLocation(CreateAColonyModSystem.ModId, ResearchAsset))?.ToObject<ResearchFile>()?.Research;
        researchDefs = loaded?.Where(r => r.Code.Length > 0).ToList() ?? new List<ResearchDef>();
        if (researchDefs.Count == 0) Mod.Logger.Warning("No research loaded from {0}", ResearchAsset);
        return researchDefs;
    }

    public ResearchDef? ResearchDef(string code) => ResearchDefs().Find(r => r.Code == code);

    public bool Researched(Colony colony, string code) => colony.ResearchDone.Contains(code);

    /// <summary>The product of a multiplying effect over all finished research (1 when none has it).</summary>
    public float ResearchMult(Colony? colony, string key)
    {
        if (colony == null || colony.ResearchDone.Count == 0) return 1f;
        float f = 1f;
        foreach (var code in colony.ResearchDone)
        {
            if (ResearchDef(code) is { } def && def.Effects.TryGetValue(key, out float v)) f *= v;
        }
        return f;
    }

    /// <summary>The sum of an adding effect over all finished research (0 when none has it).</summary>
    public float ResearchAdd(Colony? colony, string key)
    {
        if (colony == null || colony.ResearchDone.Count == 0) return 0f;
        float sum = 0f;
        foreach (var code in colony.ResearchDone)
        {
            if (ResearchDef(code) is { } def && def.Effects.TryGetValue(key, out float v)) sum += v;
        }
        return sum;
    }

    /// <summary>The colony a building belongs to, for effects asked about by building alone.</summary>
    public Colony? ColonyOfBuilding(Building building) => colonies.Values.FirstOrDefault(c => c.Buildings.Contains(building));

    // ---- The Archive ----------------------------------------------------------------------------

    /// <summary>True when the Town Hall is high enough to place an Archive.</summary>
    public bool ArchiveAllowed(Colony colony) => TownHallLevel(colony) >= Config.ArchiveTownHallLevel;

    /// <summary>The Archive's tier, 1 to 3. An Archive with no blueprints in its pack works as tier 1.</summary>
    public int ArchiveTier(Building archive) =>
        Math.Clamp(Blueprints.MaxLevel(archive) == 0 ? 1 : archive.Level, 0, ArchiveMaxTier);

    /// <summary>Researchers an Archive takes: two per tier (two before it is built, so they can be hired).</summary>
    public static int ArchiveSlots(Building archive) => ResearchersPerTier * Math.Clamp(archive.Level, 1, ArchiveMaxTier);

    /// <summary>How many research nodes an Archive runs at once: one per tier.</summary>
    public int ResearchSlots(Building archive) => ArchiveTier(archive);

    IEnumerable<Building> Archives(Colony colony) => colony.Buildings.Where(b => b.Type == BuildingTypes.Archive && !b.Removing);

    /// <summary>What is around the Archive: a table, a shelf or books, a light. Checked at most every 30 seconds.</summary>
    public record struct ArchiveRoom(bool Table, bool Shelf, bool Light, int Noisy);

    readonly Dictionary<int, (long Ms, ArchiveRoom Room)> archiveRooms = new();

    public ArchiveRoom RoomOf(Colony colony, Building archive)
    {
        long now = sapi.World.ElapsedMilliseconds;
        if (archiveRooms.TryGetValue(archive.BuildingId, out var known) && now - known.Ms < 30_000) return known.Room;
        bool table = false, shelf = false, light = false;
        int r = Math.Max(1, Config.ArchiveRoomRadius);
        var h = archive.HutPos;
        var ba = sapi.World.BlockAccessor;
        ba.WalkBlocks(new BlockPos(h.X - r, h.Y - 1, h.Z - r), new BlockPos(h.X + r, h.Y + 4, h.Z + r), (block, x, y, z) =>
        {
            if (block.Id == 0 || block.Code == null) return;
            string path = block.Code.Path;
            if (path.StartsWith("table")) table = true;
            if (path.Contains("shelf") || path.Contains("book") || path.Contains("scroll")) shelf = true;
            if (block.LightHsv[2] > 0) light = true;
        }, true);
        int nr = Math.Max(0, Config.ArchiveNoiseRadius);
        int noisy = colony.Buildings.Count(b => b != archive && !b.Removing && Config.ArchiveNoisyHuts.Contains(b.Type)
            && Math.Abs(b.HutPos.X - h.X) <= nr && Math.Abs(b.HutPos.Z - h.Z) <= nr && Math.Abs(b.HutPos.Y - h.Y) <= nr);
        var room = new ArchiveRoom(table, shelf, light, noisy);
        archiveRooms[archive.BuildingId] = (now, room);
        return room;
    }

    /// <summary>How fast research goes at this Archive: tier, room, quiet and finished research, 1 = normal.</summary>
    public float ArchiveSpeed(Colony colony, Building archive)
    {
        var room = RoomOf(colony, archive);
        float f = 1f + Config.ArchiveSpeedPerTier * Math.Max(0, ArchiveTier(archive) - 1);
        int missing = (room.Table ? 0 : 1) + (room.Shelf ? 0 : 1) + (room.Light ? 0 : 1);
        f *= Math.Max(0.1f, 1f - Config.ArchiveMissingPenalty * missing);
        f *= Math.Max(0.1f, 1f - Config.ArchiveNoisePenalty * room.Noisy);
        f *= ResearchMult(colony, "researchSpeed");
        return f;
    }

    // ---- Gates and starting --------------------------------------------------------------------

    /// <summary>What a node still waits for, in words; empty when it can be started.</summary>
    public List<string> ResearchBlockers(Colony colony, ResearchDef def)
    {
        var list = new List<string>();
        foreach (var need in def.Needs)
        {
            if (!Researched(colony, need)) list.Add(Lang.Get("createacolony:research-needs-research", Lang.Get("createacolony:research-" + need)));
        }
        if (TownHallLevel(colony) < def.TownHall) list.Add(Lang.Get("createacolony:research-needs-townhall", def.TownHall));
        if (def.Settlers > 0 && Population(colony) < def.Settlers) list.Add(Lang.Get("createacolony:research-needs-settlers", def.Settlers));
        if (def.Building.Length > 0 && !colony.Buildings.Any(b => b.Type == def.Building && !b.Removing && b.Level >= def.BuildingLevel))
        {
            string name = Lang.Get("createacolony:building-" + def.Building);
            list.Add(def.BuildingLevel > 0 ? Lang.Get("createacolony:research-needs-building-level", name, def.BuildingLevel) : Lang.Get("createacolony:research-needs-building", name));
        }
        if (def.Tokens > colony.KnowledgeTokens && !colony.Research.Any(p => p.Code == def.Code))
            list.Add(Lang.Get("createacolony:research-needs-tokens", def.Tokens));
        return list;
    }

    /// <summary>Starts (or resumes) a node at an Archive. Returns an error lang key, or null.</summary>
    public string? StartResearch(Colony colony, Building archive, string code)
    {
        if (archive.Type != BuildingTypes.Archive) return "createacolony:bad-building";
        if (ResearchDef(code) is not { } def) return "createacolony:research-unknown";
        if (Researched(colony, code)) return "createacolony:research-already-done";
        if (ArchiveTier(archive) < 1) return "createacolony:research-build-first";
        if (Running(colony, archive).Count >= ResearchSlots(archive)) return "createacolony:research-no-slot";
        var progress = colony.Research.Find(p => p.Code == code);
        if (progress != null && RunsSomewhere(colony, progress))
            return progress.ArchiveId == archive.BuildingId ? null : "createacolony:research-elsewhere-error";
        if (progress == null)
        {
            if (ResearchBlockers(colony, def).Count > 0) return "createacolony:research-locked";
            colony.KnowledgeTokens -= def.Tokens;
            progress = new ResearchProgress { Code = code, Paid = def.Cost.Count == 0 };
            colony.Research.Add(progress);
        }
        progress.ArchiveId = archive.BuildingId;
        progress.Running = true;
        LogEvent(colony, "event-research-started", "@research-" + code);
        MarkDirty(colony);
        return null;
    }

    /// <summary>Pauses a node: it keeps its points and the items already paid, and frees the slot.</summary>
    public void PauseResearch(Colony colony, string code)
    {
        var progress = colony.Research.Find(p => p.Code == code);
        if (progress == null || !progress.Running) return;
        progress.Running = false;
        CancelResearchRequests(colony, progress);
        MarkDirty(colony);
    }

    /// <summary>True while a node runs at an Archive that still stands; a removed Archive's nodes count as paused.</summary>
    public bool RunsSomewhere(Colony colony, ResearchProgress progress) =>
        progress.Running && colony.Buildings.Any(b => b.BuildingId == progress.ArchiveId && b.Type == BuildingTypes.Archive && !b.Removing);

    /// <summary>The nodes running at this Archive, oldest first.</summary>
    public List<ResearchProgress> Running(Colony colony, Building archive) =>
        colony.Research.Where(p => p.Running && p.ArchiveId == archive.BuildingId).ToList();

    void CancelResearchRequests(Colony colony, ResearchProgress progress)
    {
        if (colony.Buildings.Find(b => b.BuildingId == progress.ArchiveId) is not { } archive) return;
        string tag = RequestTag(archive);
        // Only when nothing else running there still wants items.
        if (Running(colony, archive).Any(p => p != progress && !p.Paid)) return;
        colony.Requests.RemoveAll(r => r.RequestedBy == tag && r.LeasedByCitizenId == 0);
    }

    // ---- Items ----------------------------------------------------------------------------------

    static ItemMatch CostMatch(ResearchCost cost)
    {
        var pattern = new AssetLocation(cost.Code);
        return s => s?.Collectible?.Code != null && WildcardUtil.Match(pattern, s.Collectible.Code);
    }

    /// <summary>
    /// Takes a node's items from the Archive's storage once all of them are there. When some are missing it asks
    /// couriers for what the warehouse has. True when the node is paid.
    /// </summary>
    public bool PayResearch(Colony colony, Building archive, ResearchProgress progress, out string status)
    {
        status = "";
        if (progress.Paid) return true;
        if (ResearchDef(progress.Code) is not { } def) return false;
        var storage = HutStorage(colony, archive).ToList();
        bool all = true;
        foreach (var cost in def.Cost)
        {
            var match = CostMatch(cost);
            int have = CountItems(storage, match);
            if (have >= cost.Quantity) continue;
            all = false;
            if (CodeInWarehouse(colony, match) is { } code) EnsureCraftRequest(colony, archive, code, cost.Quantity - have);
        }
        if (!all)
        {
            status = colony.Requests.Any(r => r.RequestedBy == RequestTag(archive)) ? "research-waiting-items" : "research-needs-items";
            return false;
        }
        foreach (var cost in def.Cost)
        {
            var match = CostMatch(cost);
            int left = cost.Quantity;
            while (left > 0 && TakeItems(storage, match, left) is { } taken) left -= taken.StackSize;
        }
        progress.Paid = true;
        MarkDirty(colony);
        return true;
    }

    /// <summary>What the items of a node are, in words: "8 Parchment, 32 seeds".</summary>
    public string CostText(ResearchDef def)
    {
        var parts = new List<string>();
        foreach (var cost in def.Cost) parts.Add(cost.Quantity + " " + CostName(cost));
        if (def.Tokens > 0) parts.Add(Lang.Get("createacolony:research-cost-tokens", def.Tokens));
        return parts.Count == 0 ? Lang.Get("createacolony:research-cost-none") : string.Join(", ", parts);
    }

    string CostName(ResearchCost cost)
    {
        if (cost.Name.Length > 0) return Lang.Get(cost.Name);
        if (!cost.Code.Contains('*'))
        {
            var loc = new AssetLocation(cost.Code);
            CollectibleObject? c = sapi.World.GetItem(loc);
            c ??= sapi.World.GetBlock(loc);
            if (c != null) return new ItemStack(c).GetName();
        }
        return cost.Code;
    }

    /// <summary>What a node's items still missing at the Archive are, in words; empty when all are there.</summary>
    public string MissingText(Colony colony, Building archive, ResearchDef def)
    {
        var storage = HutStorage(colony, archive).ToList();
        var parts = new List<string>();
        foreach (var cost in def.Cost)
        {
            int have = CountItems(storage, CostMatch(cost));
            if (have < cost.Quantity) parts.Add((cost.Quantity - have) + " " + CostName(cost));
        }
        return string.Join(", ", parts);
    }

    /// <summary>The Archive keeps what its unpaid research needs, and paper and books; couriers take the rest.</summary>
    ItemMatch ArchiveJunk(Colony colony, Building archive)
    {
        var wanted = Running(colony, archive).Where(p => !p.Paid).Select(p => ResearchDef(p.Code)).Where(d => d != null)
            .SelectMany(d => d!.Cost).Select(CostMatch).ToList();
        return s => !wanted.Any(m => m(s)) && !(s.Collectible.Code.Path.StartsWith("paper") || s.Collectible.Code.Path.StartsWith("book"));
    }

    // ---- The work -------------------------------------------------------------------------------

    /// <summary>
    /// The node this researcher works on: the running nodes at its Archive are shared out in turn among its
    /// researchers, at most ResearchMaxPerNode each. Null when there is none, with the reason in status.
    /// </summary>
    public ResearchProgress? ResearchFor(Colony colony, Building archive, CitizenRecord researcher, out string status, bool pay = true)
    {
        status = "";
        var running = Running(colony, archive);
        if (running.Count == 0)
        {
            status = "research-nothing";
            return null;
        }
        // Pay what can be paid; the unpaid ones wait for their items.
        if (pay) foreach (var p in running) PayResearch(colony, archive, p, out _);
        var ready = running.Where(p => p.Paid).ToList();
        if (ready.Count == 0)
        {
            if (pay) PayResearch(colony, archive, running[0], out status);
            if (status.Length == 0) status = "research-needs-items";
            return null;
        }
        var workers = WorkersAt(colony, archive);
        int index = Math.Max(0, workers.IndexOf(researcher));
        if (index >= ready.Count * Math.Max(1, Config.ResearchMaxPerNode))
        {
            status = "research-full";
            return null;
        }
        status = "research-working";
        return ready[index % ready.Count];
    }

    /// <summary>One piece of a researcher's work: Research Points for its node, and the node done when it has enough.</summary>
    public void AddResearchWork(Colony colony, Building archive, CitizenRecord researcher)
    {
        var progress = ResearchFor(colony, archive, researcher, out _);
        if (progress == null || ResearchDef(progress.Code) is not { } def) return;
        progress.Points += Math.Max(0f, Config.ResearchPointsPerWork) * ArchiveSpeed(colony, archive);
        if (progress.Points >= def.Points) FinishResearch(colony, progress, def);
        MarkDirty(colony);
    }

    void FinishResearch(Colony colony, ResearchProgress progress, ResearchDef def)
    {
        colony.Research.Remove(progress);
        if (!colony.ResearchDone.Contains(def.Code)) colony.ResearchDone.Add(def.Code);
        colony.KnowledgeTokens += def.GivesTokens;
        LogEvent(colony, "event-research-done", "@research-" + def.Code);
        TellMembers(colony, "createacolony:research-done-chat", Lang.Get("createacolony:research-" + def.Code), Lang.Get("createacolony:research-" + def.Code + "-desc"));
        // Effects that are set once rather than read live.
        if (def.Effects.ContainsKey("claimGrow")) RefreshClaim(colony);
    }

    /// <summary>The Archive's line on the Main tab.</summary>
    public string ArchiveInfo(Colony colony, Building archive) =>
        Lang.Get("createacolony:archive-info", Running(colony, archive).Count, ResearchSlots(archive), colony.ResearchDone.Count, colony.KnowledgeTokens);
}
