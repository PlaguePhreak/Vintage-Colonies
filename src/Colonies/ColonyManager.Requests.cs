using System;
using System.Collections.Generic;
using System.Linq;
using CreateAColony.Quests;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace CreateAColony.Colonies;

/// <summary>
/// Settler requests: small jobs the colony posts in the quest book's Requests chapter. The chapter holds a
/// pool of quests marked Request; only the ones on the colony's board are open. Every RequestEveryDays the
/// colony posts one more (up to MaxOpenRequests), picked first from what its stores are short of (the quest's
/// Need), and a request nobody did leaves after RequestLastsDays with no penalty. Each one names a settler,
/// who is happier for a while when it is done. Tasks that count something the colony already has (buildings,
/// settlers, events) and kills count from the moment the request was posted.
/// </summary>
public partial class ColonyManager
{
    public const int MaxOpenRequests = 3;
    const double RequestEveryDays = 2;
    public const double RequestLastsDays = 5;

    static readonly HashSet<string> CountedFromPosting = new()
    {
        ObjectiveTypes.Buildings, ObjectiveTypes.Settlers, ObjectiveTypes.Event, ObjectiveTypes.Kill
    };

    public OpenRequest? OpenRequestOf(Colony? colony, QuestDef q) => colony?.QuestRequests.Find(r => r.Code == q.Code);

    /// <summary>How far a request's task is, counted from when it was posted (kills: from when this player first saw it).</summary>
    int RequestProgress(IServerPlayer player, Colony colony, OpenRequest open, QuestObjectiveDef o, int index)
    {
        int current = Progress(player, colony, o);
        string key = o.Type == ObjectiveTypes.Kill ? player.PlayerUID + "#" + index : "#" + index;
        if (!open.Baseline.TryGetValue(key, out int start))
        {
            open.Baseline[key] = start = current;
            MarkDirty(colony);
        }
        return Math.Max(0, current - start);
    }

    void TickRequests(Colony colony, double now)
    {
        int gone = colony.QuestRequests.RemoveAll(r => now - r.PostedTotalDays > RequestLastsDays || Quests.Get(r.Code)?.Request != true);
        if (gone > 0) MarkDirty(colony);
        if (colony.QuestRequests.Count >= MaxOpenRequests || now - colony.LastRequestTotalDays < RequestEveryDays) return;

        var askers = colony.Citizens.Where(c => c.Alive && !c.Visitor && !c.IsChild).ToList();
        var pool = Quests.All.Where(q => q.Request && !q.Soon && q.PerColony && OpenRequestOf(colony, q) == null).ToList();
        if (askers.Count == 0 || pool.Count == 0) return;

        var storage = ColonyStorage(colony).ToList();
        var shortOf = pool.Where(q => IsShortOf(storage, q.Need)).ToList();
        var pick = shortOf.Count > 0 ? shortOf : pool.Where(q => q.Need.Length == 0).ToList();
        if (pick.Count == 0) pick = pool;
        var quest = pick[sapi.World.Rand.Next(pick.Count)];
        var asker = askers[sapi.World.Rand.Next(askers.Count)];

        var open = new OpenRequest { Code = quest.Code, PostedTotalDays = now, CitizenId = asker.CitizenId };
        for (int i = 0; i < quest.Objectives.Count; i++)
        {
            var o = quest.Objectives[i];
            if (CountedFromPosting.Contains(o.Type) && o.Type != ObjectiveTypes.Kill) open.Baseline["#" + i] = Progress(null!, colony, o);
        }
        colony.QuestRequests.Add(open);
        colony.LastRequestTotalDays = now;
        // A request that was done before starts clean: told again when it is ready.
        if (colony.Quests.TryGetValue(quest.Code, out var record)) record.Notified = record.Checked = record.Accepted = false;
        MarkDirty(colony);
        LogEvent(colony, "event-request-posted", asker.Name, "@quest-" + quest.Code);
    }

    /// <summary>Need is "pattern&lt;count": true while the colony's stores hold fewer than count of the item.</summary>
    bool IsShortOf(IEnumerable<Vintagestory.GameContent.BlockEntityContainer> storage, string need)
    {
        int at = need.LastIndexOf('<');
        if (at <= 0 || !int.TryParse(need[(at + 1)..], out int count)) return false;
        return CountItems(storage, Matcher(need[..at])) < count;
    }

    /// <summary>A request was claimed: it leaves the board and the settler who asked is pleased.</summary>
    void FinishRequest(Colony colony, QuestDef q)
    {
        var open = OpenRequestOf(colony, q);
        if (open == null) return;
        colony.QuestRequests.Remove(open);
        if (colony.Citizens.Find(c => c.CitizenId == open.CitizenId && c.Alive) is { } asker) AddThought(asker, "requestdone");
        MarkDirty(colony);
    }
}
