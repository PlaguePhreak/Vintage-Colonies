using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace CreateAColony.Colonies;

/// <summary>
/// Auto hiring and auto housing (0.185.0), the Town Hall's Settings switches, both off until an officer turns them on.
/// Auto hiring: each morning, every jobless grown settler takes an open place, the best fit first (skill, passion,
/// trait, the way the Bailiff hands out jobs). Food comes first: Farmer, Cook and Bakery places fill before the rest,
/// then huts nobody works at, then the others. Huts with Take auto hires off, the Church and the Bailiff's post are left
/// to the player. Auto housing: every few hours, settlers with no home (no bed in a Residence or Boarding House) move
/// into the nearest home with free beds for their whole household, Residences first. Nobody who has a home is moved.
/// </summary>
public partial class ColonyManager
{
    readonly Dictionary<string, double> lastAutoHousing = new();

    void TickAutoTown(Colony colony, double now)
    {
        if (colony.AutoHiring)
        {
            int day = (int)now;
            double hour = (now - day) * 24;
            if (day > colony.LastAutoHireDay && hour >= Config.AutoHireHour)
            {
                colony.LastAutoHireDay = day;
                AutoHire(colony);
                MarkDirty(colony);
            }
        }
        if (colony.AutoHousing)
        {
            double hours = now * sapi.World.Calendar.HoursPerDay;
            if (!lastAutoHousing.TryGetValue(colony.ColonyId, out double last) || hours - last >= Config.AutoHousingEveryHours)
            {
                lastAutoHousing[colony.ColonyId] = hours;
                AutoHouse(colony);
            }
        }
    }

    /// <summary>Hut types whose places auto hiring fills first.</summary>
    static bool IsFoodHut(string type) => type is BuildingTypes.Farmer or BuildingTypes.Cook or BuildingTypes.Bakery;

    /// <summary>Gives every jobless settler an open place it fits, food huts first. Returns how many got a job.</summary>
    public int AutoHire(Colony colony)
    {
        var free = Jobless(colony).ToList();
        if (free.Count == 0) return 0;
        var huts = colony.Buildings
            .Where(b => Staffable(b) && b.TakesAutoHires && !b.IsStation && JobOf(b.Type) is { } job && job != Jobs.Priest && job != Jobs.Bailiff)
            .OrderBy(b => IsGuardPost(b.Type)) // other huts first, guards after (0.198.0)
            .ThenByDescending(b => IsFoodHut(b.Type))
            .ThenBy(b => WorkersAt(colony, b).Count > 0)
            .ThenBy(b => b.Level < 1)
            .ThenBy(b => b.BuildingId)
            .ToList();
        int given = 0;
        foreach (var hut in huts)
        {
            string job = JobOf(hut.Type)!;
            while (free.Count > 0 && FreePlaces(colony, hut) > 0)
            {
                var pick = free.Where(c => IncapableOfJob(c, job) == null).OrderByDescending(c => JobScore(c, job)).ThenBy(c => c.CitizenId).FirstOrDefault();
                if (pick == null) break;
                free.Remove(pick);
                pick.JobCode = job;
                pick.WorkplacePos = hut.HutPos.Copy();
                given++;
                LogEvent(colony, "event-autohire", pick.Name, "@job-" + job, BuildingName(hut));
            }
            if (free.Count == 0) break;
        }
        if (given > 0) TellMembers(colony, "createacolony:autohire-told", given);
        return given;
    }

    /// <summary>Moves settlers with no home into homes with room for their household. Returns how many moved.</summary>
    public int AutoHouse(Colony colony)
    {
        var boxes = ResidenceBoxes(colony);
        var homes = boxes.Where(h => h.B.Level >= 1 && !h.B.Removing && h.B.TakesAutoResidents).ToList();
        if (homes.Count == 0) return 0;
        var beds = BedList(colony);
        var homeless = colony.Citizens
            .Where(c => c.Alive && !c.Visitor && !c.IsChild && !c.WaitingForRoom && (c.HomeBedPos == null || ResidenceAt(colony, c.HomeBedPos, boxes) == null))
            .OrderByDescending(c => MovingHousehold(colony, c).Count)
            .ThenBy(c => c.CitizenId)
            .ToList();
        var done = new HashSet<int>();
        int moved = 0;
        var th = colony.TownHallPos;
        foreach (var settler in homeless)
        {
            if (done.Contains(settler.CitizenId)) continue;
            var people = MovingHousehold(colony, settler);
            foreach (var p in people) done.Add(p.CitizenId);
            // Only people with no home move: a household where someone has a home stays as it is.
            if (people.Any(p => p.HomeBedPos != null && ResidenceAt(colony, p.HomeBedPos, boxes) != null)) continue;
            var from = settler.HomeBedPos ?? th;
            foreach (var (home, min, max) in homes
                .OrderBy(h => h.B.Type == BuildingTypes.BoardingHouse)
                .ThenBy(h => h.B.HutPos.DistanceSqTo(from.X, from.Y, from.Z)))
            {
                var mine = beds.Where(kv => kv.Key.X >= min.X && kv.Key.X <= max.X && kv.Key.Y >= min.Y && kv.Key.Y <= max.Y && kv.Key.Z >= min.Z && kv.Key.Z <= max.Z)
                    .ToDictionary(kv => kv.Key, kv => kv.Value);
                if (mine.Count == 0 || !Rehouse(colony, people, mine)) continue;
                moved += people.Count;
                LogEvent(colony, "event-autohouse", HouseholdName(people), BuildingName(home));
                foreach (var p in people) AddThought(p, "newhome", Config.NewHomeMood);
                break;
            }
        }
        if (moved > 0) SaveColony(colony);
        return moved;
    }
}
