using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;

namespace CreateAColony.Colonies;

/// <summary>
/// What the Town Hall window needs beyond the colony's own fields: the event log on the Information
/// tab, Recall on the Citizens tab, and the colony-wide counts on the Statistics tab.
/// </summary>
public partial class ColonyManager
{
    /// <summary>How many lines the event log keeps; older ones drop off the top.</summary>
    public const int MaxEvents = 100;

    /// <summary>Adds a line to the colony's event log. Arguments starting with "@" are lang keys (without "createacolony:").</summary>
    public void LogEvent(Colony colony, string key, params string[] args)
    {
        colony.Events.Add(new ColonyEvent { TotalDays = sapi.World.Calendar.TotalDays, Key = key, Args = args.ToList() });
        if (colony.Events.Count > MaxEvents) colony.Events.RemoveRange(0, colony.Events.Count - MaxEvents);
        MarkDirty(colony);
    }

    /// <summary>Brings a stuck settler to the Town Hall at once, as MineColonies' Recall does. Returns a lang key on failure.</summary>
    public string? RecallCitizen(Colony colony, CitizenRecord citizen)
    {
        if (!citizen.Alive || citizen.IsDowned) return "createacolony:bad-citizen";
        if (sapi.World.GetEntityById(citizen.EntityId) is not EntityAgent { Alive: true } entity) return "createacolony:recall-not-loaded";
        var spot = FreeSpotNear(entity, colony.TownHallPos.UpCopy(), 3) ?? Beside(colony.TownHallPos);
        entity.TeleportToDouble(spot.X, spot.Y, spot.Z);
        return null;
    }

    /// <summary>The Statistics counts of every building added together, by key, for the same five periods as a hut's.</summary>
    public List<(string Key, int[] Counts)> ColonyStatSummary(Colony colony)
    {
        var totals = new Dictionary<string, int[]>();
        foreach (var b in colony.Buildings)
        {
            foreach (var (key, counts) in StatSummary(b))
            {
                if (!totals.TryGetValue(key, out var sum)) totals[key] = sum = new int[counts.Length];
                for (int i = 0; i < counts.Length; i++) sum[i] += counts[i];
            }
        }
        return totals.OrderBy(t => t.Key).Select(t => (t.Key, t.Value)).ToList();
    }

    /// <summary>
    /// For each job: how many settlers have it, and how many places the colony's huts offer (-1 for the
    /// unemployed, couriers, and guards while there is no Guard Tower or Barracks, since none of them need a hut).
    /// </summary>
    public List<(string Job, int Workers, int Places)> JobCounts(Colony colony)
    {
        var settlers = colony.Citizens.Where(c => c.Alive && !c.Visitor).ToList();
        var rows = new List<(string, int, int)>();
        foreach (string job in Jobs.All)
        {
            int workers = settlers.Count(c => (c.JobCode ?? "") == job);
            var huts = colony.Buildings.Where(b => JobOf(b.Type) == job).ToList();
            int places = job is Jobs.Bailiff or Jobs.Mayor ? 1
                : job.Length == 0 || job == Jobs.Courier || job == Jobs.Guard && huts.Count == 0 ? -1 : huts.Sum(WorkerSlots);
            rows.Add((job, workers, places));
        }
        return rows;
    }
}
