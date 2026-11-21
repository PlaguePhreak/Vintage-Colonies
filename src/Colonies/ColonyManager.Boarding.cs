using System;
using System.Collections.Generic;
using System.Linq;

namespace CreateAColony.Colonies;

/// <summary>
/// The Boarding House: a home with many beds and at most two levels. It is cheap room for a growing colony, but
/// nobody likes living there: boarders have a standing unhappy thought, have no children, and move out by
/// themselves as soon as a Residence has room for their whole household.
/// </summary>
public partial class ColonyManager
{
    /// <summary>In-game hour of the last check per colony, so boarders look for a home a few times a day.</summary>
    readonly Dictionary<string, double> lastBoardingCheck = new();

    /// <summary>Buildings settlers live in: their beds make a home, with a Residents tab.</summary>
    public static bool IsHome(string type) => type is BuildingTypes.Residence or BuildingTypes.BoardingHouse;

    /// <summary>How good a bed's building is for someone looking for a bed: a Residence, a Boarding House, then no home at all.</summary>
    static int HomeRank(Building? home) => home == null ? 2 : home.Type == BuildingTypes.BoardingHouse ? 1 : 0;

    /// <summary>True for a settler whose bed is in a Boarding House.</summary>
    public bool IsBoarder(Colony colony, CitizenRecord c) =>
        c.HomeBedPos != null && ResidenceAt(colony, c.HomeBedPos)?.Type == BuildingTypes.BoardingHouse;

    /// <summary>The standing mood of living in this Boarding House: a little less bad at level 2.</summary>
    int BoardingMood(Building house) => Math.Min(0, Config.BoardingMood + Math.Max(0, house.Level - 1) * Config.BoardingMoodPerLevel);

    /// <summary>
    /// Boarders move into a Residence once one has free beds for their whole household. Couples and families go
    /// first, then whoever has boarded longest (lowest citizen id).
    /// </summary>
    void TickBoarders(Colony colony, double now)
    {
        if (!Config.BoardersMoveOut) return;
        double hours = now * sapi.World.Calendar.HoursPerDay;
        if (lastBoardingCheck.TryGetValue(colony.ColonyId, out double last) && hours - last < Config.BoardersLookEveryHours) return;
        lastBoardingCheck[colony.ColonyId] = hours;
        if (!colony.Buildings.Any(b => b.Type == BuildingTypes.BoardingHouse)) return;

        var boxes = ResidenceBoxes(colony);
        var homes = BedList(colony)
            .Where(kv => ResidenceAt(colony, kv.Key, boxes) is { Type: BuildingTypes.Residence, Level: >= 1 })
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        if (homes.Count == 0) return;

        var done = new HashSet<int>();
        var boarders = colony.Citizens
            .Where(c => c.Alive && !c.Visitor && !c.IsChild && c.HomeBedPos != null
                && ResidenceAt(colony, c.HomeBedPos, boxes)?.Type == BuildingTypes.BoardingHouse)
            .OrderByDescending(c => MovingHousehold(colony, c).Count)
            .ThenBy(c => c.CitizenId)
            .ToList();
        bool moved = false;
        foreach (var boarder in boarders)
        {
            if (done.Contains(boarder.CitizenId)) continue;
            var people = MovingHousehold(colony, boarder);
            foreach (var p in people) done.Add(p.CitizenId);
            if (!Rehouse(colony, people, homes)) continue;
            moved = true;
            var home = ResidenceAt(colony, boarder.HomeBedPos!, boxes);
            LogEvent(colony, "event-boarder-moved", HouseholdName(people), home == null ? "" : BuildingName(home));
            foreach (var p in people) AddThought(p, "newhome", Config.NewHomeMood);
        }
        if (moved) SaveColony(colony);
    }

    /// <summary>"Anna Smith" for one settler, "the Smith family" for a household.</summary>
    static string HouseholdName(List<CitizenRecord> people) =>
        people.Count == 1 ? people[0].Name : Vintagestory.API.Config.Lang.Get("createacolony:boarding-family", people[0].Surname);
}
