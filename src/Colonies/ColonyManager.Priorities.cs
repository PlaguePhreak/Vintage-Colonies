using System.Collections.Generic;
using System.Linq;

namespace CreateAColony.Colonies;

/// <summary>
/// The Priorities tab (Aaron, TRADERS_DRAFT.md): every hut that can do more than one thing lists what it was
/// asked to do, in the order it does it, and officers move things up and down. Builders work down the colony's
/// work order queue (building orders and trader houses together, Colony.OrderQueue). Crafting huts make what is
/// short in their own order (Building.Priorities); until someone changes it, the most wanted comes first.
/// </summary>
public partial class ColonyManager
{
    public const int MoveUp = -1;
    public const int MoveDown = 1;
    public const int MoveTop = -1000;

    public static string OrderKey(Building b) => "b:" + b.BuildingId;
    public static string OrderKey(TeardownOrder t) => "t:" + t.Id;

    /// <summary>Where a key sits in a priority list; things nobody placed come after the rest.</summary>
    static int PriorityIndex(List<string> order, string key)
    {
        int i = order.IndexOf(key);
        return i < 0 ? int.MaxValue : i;
    }

    // ---- Builders ---------------------------------------------------------------------------

    /// <summary>The work orders and trader houses, in the order builders take them: the queue, then new ones oldest first.</summary>
    public List<(Building? Building, TeardownOrder? Teardown)> BuilderQueue(Colony colony)
    {
        var all = colony.Buildings.Where(b => b.HasWorkOrder || b.Removing).OrderBy(b => b.BuildingId)
            .Select(b => (Key: OrderKey(b), Item: ((Building?)b, (TeardownOrder?)null)))
            .Concat(colony.Teardowns.OrderBy(t => t.Id).Select(t => (Key: OrderKey(t), Item: ((Building?)null, (TeardownOrder?)t))))
            .ToList();
        return all.Select((e, n) => (e, n)).OrderBy(x => PriorityIndex(colony.OrderQueue, x.e.Key)).ThenBy(x => x.n).Select(x => x.e.Item).ToList();
    }

    /// <summary>
    /// The work a builder does now: the first in the queue nobody else is on. A builder that was on something
    /// lower down lets it go, so "Do next" takes effect at once.
    /// </summary>
    public (Building? Building, TeardownOrder? Teardown) NextBuilderJob(Colony colony, CitizenRecord builder)
    {
        bool Busy(int citizenId) => citizenId != builder.CitizenId && colony.Citizens.Find(c => c.CitizenId == citizenId) is { Alive: true, JobCode: Jobs.Builder };
        // A build stuck at a block it failed twice over goes after the others, so one bad block cannot hold up the town.
        var queue = BuilderQueue(colony);
        queue = queue.Where(e => e.Building == null || !IsBuildStuck(e.Building)).Concat(queue.Where(e => e.Building != null && IsBuildStuck(e.Building))).ToList();
        foreach (var (b, t) in queue)
        {
            if (Busy(b?.BuilderCitizenId ?? t!.BuilderCitizenId)) continue;
            foreach (var other in colony.Buildings.Where(o => o != b && o.BuilderCitizenId == builder.CitizenId)) other.BuilderCitizenId = 0;
            foreach (var other in colony.Teardowns.Where(o => o != t && o.BuilderCitizenId == builder.CitizenId)) other.BuilderCitizenId = 0;
            if (b != null) b.BuilderCitizenId = builder.CitizenId;
            if (t != null) t.BuilderCitizenId = builder.CitizenId;
            MarkDirty(colony);
            return (b, t);
        }
        return (null, null);
    }

    /// <summary>Moves a work order or trader house up, down or to the top of the builders' queue. Returns a lang key on failure.</summary>
    public string? MoveBuilderOrder(Colony colony, string key, int move)
    {
        var keys = BuilderQueue(colony).Select(e => e.Building != null ? OrderKey(e.Building) : OrderKey(e.Teardown!)).ToList();
        if (!Move(keys, key, move)) return "createacolony:bad-building";
        colony.OrderQueue = keys;
        MarkDirty(colony);
        return null;
    }

    static bool Move(List<string> keys, string key, int move)
    {
        int i = keys.IndexOf(key);
        if (i < 0) return false;
        int to = move == MoveTop ? 0 : System.Math.Clamp(i + move, 0, keys.Count - 1);
        keys.RemoveAt(i);
        keys.Insert(to, key);
        return true;
    }

    // ---- Crafting huts ----------------------------------------------------------------------

    /// <summary>
    /// What is short, in the hut's priority order. Before anyone set an order, the most wanted (the furthest
    /// below what the hut keeps) comes first.
    /// </summary>
    public IEnumerable<T> ByPriority<T>(Building hut, IEnumerable<(T Kind, string Key, int Short)> wanted) =>
        hut.Priorities.Count == 0
            ? wanted.OrderByDescending(w => w.Short).Select(w => w.Kind)
            : wanted.OrderBy(w => PriorityIndex(hut.Priorities, w.Key)).ThenByDescending(w => w.Short).Select(w => w.Kind);

    /// <summary>Huts with a Priorities tab: those that make more than one kind of thing.</summary>
    public bool HasPriorities(string type) =>
        CraftTabOf(type) != null || HasHutRecipes(type) || type is BuildingTypes.Cook or BuildingTypes.Potter or BuildingTypes.Gatherer;

    /// <summary>
    /// Moves one thing a crafting hut keeps up, down or to the top. The list shown (keys, in the order shown) is
    /// saved, so what the worker does matches the tab from then on. Returns a lang key on failure.
    /// </summary>
    public string? MoveHutPriority(Colony colony, Building hut, List<string> shown, string key, int move)
    {
        var keys = shown.Distinct().ToList();
        if (!Move(keys, key, move)) return "createacolony:bad-building";
        // Things kept before but not shown now (nothing wanted of them today) keep their place after the rest.
        keys.AddRange(hut.Priorities.Where(k => !keys.Contains(k)));
        hut.Priorities = keys;
        MarkDirty(colony);
        return null;
    }
}
