using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using ItemMatch = System.Func<Vintagestory.API.Common.ItemStack, bool>;

namespace CreateAColony.Colonies;

/// <summary>
/// Field trips (0.180.0). Once Field Notes is researched, the Archive offers up to FieldTripsOffered trips, picked
/// from the colony's landmark markers (ruins, translocators, peaks and the game's story places) nearest the Town
/// Hall first. An explorer takes a parchment, walks there, studies the spot FieldTripWorks times and writes a
/// Field notes book, then carries it to the Archive, where it is handed in like a book (points, and a Knowledge
/// Token for ruins, translocators and story places). Each spot counts once.
/// </summary>
public partial class ColonyManager
{
    public static readonly ItemMatch IsParchment = s => s?.Collectible?.Code is { } c && c.Domain == "game" && c.Path == "paper-parchment";

    public bool FieldTripsOpen(Colony colony) => Researched(colony, "fieldnotes");

    static string TripKey(double x, double z) => $"{(int)Math.Floor(x)},{(int)Math.Floor(z)}";

    static string TripNameKey(string kind) => kind switch
    {
        "landmark-ruins" => "trip-ruins",
        "landmark-translocator" => "trip-translocator",
        "landmark-mountain" => "trip-mountain",
        _ => "trip-story"
    };

    /// <summary>What a trip is called: its kind, and the marker's own title when it has one.</summary>
    public static string TripTitle(FieldTrip trip) =>
        Lang.Get("createacolony:" + TripNameKey(trip.Kind)) + (trip.Title.Length > 0 ? " (" + trip.Title + ")" : "");

    /// <summary>Fills the offered trips up from the colony's landmark markers, and drops trips whose explorer is gone.</summary>
    void RefreshFieldTrips(Colony colony)
    {
        if (!FieldTripsOpen(colony)) return;
        foreach (var trip in colony.FieldTrips)
        {
            if (trip.CitizenId != 0 && colony.Citizens.Find(c => c.CitizenId == trip.CitizenId) is not { Alive: true, JobCode: Jobs.Explorer })
                trip.CitizenId = 0;
        }
        int want = Math.Max(0, Config.FieldTripsOffered) - colony.FieldTrips.Count;
        if (want <= 0) return;
        var home = colony.TownHallPos;
        var offered = colony.FieldTrips.Select(t => TripKey(t.X, t.Z)).ToHashSet();
        var picks = colony.Markers
            .Where(m => Config.FieldTripPoints.ContainsKey(m.Kind))
            .Where(m => !colony.FieldTripsDone.Contains(TripKey(m.X, m.Z)) && !offered.Contains(TripKey(m.X, m.Z)))
            .OrderBy(m => (m.X - home.X) * (m.X - home.X) + (m.Z - home.Z) * (m.Z - home.Z))
            .Take(want).ToList();
        foreach (var m in picks)
        {
            colony.FieldTrips.Add(new FieldTrip
            {
                TripId = colony.NextFieldTripId++, Kind = m.Kind, X = m.X, Y = m.Y, Z = m.Z, Title = m.Title,
                Points = (int)Math.Round(Config.FieldTripPoints[m.Kind] * ResearchMult(colony, "fieldNotes")), // Expeditions
                Tokens = Config.FieldTripTokenKinds.Contains(m.Kind) ? 1 : 0
            });
        }
        if (picks.Count > 0) MarkDirty(colony);
    }

    /// <summary>The trip this explorer is on, or the first free one it now takes; null when there is none.</summary>
    public FieldTrip? FieldTripFor(Colony colony, CitizenRecord explorer)
    {
        RefreshFieldTrips(colony);
        if (!FieldTripsOpen(colony)) return null;
        var mine = colony.FieldTrips.Find(t => t.CitizenId == explorer.CitizenId);
        if (mine != null) return mine;
        var free = colony.FieldTrips.Find(t => t.CitizenId == 0);
        if (free == null) return null;
        free.CitizenId = explorer.CitizenId;
        MarkDirty(colony);
        return free;
    }

    public static BlockPos TripPos(FieldTrip trip) => new BlockPos((int)Math.Floor(trip.X), (int)Math.Floor(trip.Y), (int)Math.Floor(trip.Z));

    /// <summary>
    /// One piece of study at the spot. The last one uses a parchment from the explorer's pack and writes the Field
    /// notes book into it. True when the notes were written.
    /// </summary>
    public bool StudyFieldTrip(Colony colony, CitizenRecord explorer, FieldTrip trip)
    {
        trip.Works++;
        MarkDirty(colony);
        if (trip.Works < Math.Max(1, Config.FieldTripWorks)) return false;
        if (TakeFromPack(colony, explorer, IsParchment, 1) == null)
        {
            trip.Works = Math.Max(0, Config.FieldTripWorks - 1); // waits for parchment, keeps its study
            return false;
        }
        var item = sapi.World.GetItem(new AssetLocation(CreateAColonyModSystem.ModId, FieldNotesCode));
        if (item != null)
        {
            var notes = new ItemStack(item);
            notes.Attributes.SetInt("fieldPoints", trip.Points);
            notes.Attributes.SetInt("fieldTokens", trip.Tokens);
            notes.Attributes.SetString("fieldTitle", TripTitle(trip));
            AddToPack(colony, explorer, notes);
        }
        colony.FieldTrips.Remove(trip);
        colony.FieldTripsDone.Add(TripKey(trip.X, trip.Z));
        LogEvent(colony, "event-research-fieldnotes", explorer.Name, TripTitle(trip));
        MarkDirty(colony);
        return true;
    }

    /// <summary>The colony's Archive nearest a spot, or null.</summary>
    public Building? NearestArchive(Colony colony, BlockPos from) =>
        Archives(colony).OrderBy(a => a.HutPos.DistanceSqTo(from.X, from.Y, from.Z)).FirstOrDefault();

    /// <summary>An explorer at the Archive hands in its Field notes; what today's cap leaves over goes in the Archive's storage.</summary>
    public void DeliverFieldNotes(Colony colony, CitizenRecord explorer, Building archive)
    {
        while (TakeFromPack(colony, explorer, IsFieldNotes, 1) is { } notes)
        {
            var (taken, _, _) = HandIn(colony, archive, notes);
            if (taken == 0)
            {
                AddToPack(colony, explorer, notes);
                UnloadPack(colony, explorer, HutStorage(colony, archive), IsFieldNotes);
                break;
            }
        }
        MarkDirty(colony);
    }

    /// <summary>The Archive's Field trips list, one line per trip.</summary>
    public List<string> FieldTripLines(Colony colony, Building archive)
    {
        RefreshFieldTrips(colony);
        var lines = new List<string>();
        foreach (var trip in colony.FieldTrips)
        {
            int dist = (int)Math.Sqrt((trip.X - archive.HutPos.X) * (trip.X - archive.HutPos.X) + (trip.Z - archive.HutPos.Z) * (trip.Z - archive.HutPos.Z));
            string line = Lang.Get(trip.Tokens > 0 ? "createacolony:research-trip-line-tokens" : "createacolony:research-trip-line", TripTitle(trip), dist, trip.Points);
            if (trip.CitizenId != 0 && colony.Citizens.Find(c => c.CitizenId == trip.CitizenId) is { } who)
                line += " - " + Lang.Get("createacolony:research-trip-taken", who.Name);
            lines.Add(line);
        }
        return lines;
    }
}
