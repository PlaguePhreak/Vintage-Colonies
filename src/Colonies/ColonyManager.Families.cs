using System;
using System.Collections.Generic;
using System.Linq;
using CreateAColony.Settlers;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace CreateAColony.Colonies;

/// <summary>
/// Families, MineColonies style. Every settler has a family name. Two single adults who have lived in
/// the colony a while pair up and move in together, best in a Residence. A couple with food in their
/// bellies and a free bed in the colony may have a child: a small settler who plays near home, eats
/// and sleeps, and after a few days grows up into an adult who can be given a job. No free bed, no
/// child, so housing sets how fast the colony grows.
/// </summary>
public partial class ColonyManager
{
    const string SurnamesAsset = "config/settlersurnames.json";

    string[]? surnames;

    // ---- Names ----------------------------------------------------------------------------

    /// <summary>A family name from config/settlersurnames.json no family in the colony has yet, if there is one.</summary>
    public string RandomSurname(Colony colony)
    {
        surnames ??= sapi.Assets.TryGet(new AssetLocation(CreateAColonyModSystem.ModId, SurnamesAsset))?.ToObject<Dictionary<string, string[]>>()
            ?.GetValueOrDefault("surnames") ?? Array.Empty<string>();
        if (surnames.Length == 0) return "";
        var used = new HashSet<string>(colony.Citizens.Where(c => c.Alive).Select(c => c.Surname));
        foreach (var b in colony.Buildings) used.UnionWith(b.Recruits.Select(r => SurnameOf(r.Name)));
        var free = surnames.Where(n => !used.Contains(n)).ToArray();
        var pick = free.Length > 0 ? free : surnames;
        return pick[sapi.World.Rand.Next(pick.Length)];
    }

    /// <summary>The family name in a full name: its last word, when it has more than one.</summary>
    public static string SurnameOf(string name)
    {
        int space = name.Trim().LastIndexOf(' ');
        return space < 0 ? "" : name.Trim()[(space + 1)..];
    }

    /// <summary>Settlers from before families came get a family name, once.</summary>
    void EnsureSurnames(Colony colony)
    {
        bool changed = false;
        foreach (var c in colony.Citizens.Where(c => c.Surname.Length == 0))
        {
            string surname = RandomSurname(colony);
            if (surname.Length == 0) return;
            c.Surname = surname;
            c.Name = $"{c.Name} {surname}";
            if (sapi.World.GetEntityById(c.EntityId) is { } entity) entity.GetBehavior<Vintagestory.GameContent.EntityBehaviorNameTag>()?.SetName(c.Name);
            changed = true;
        }
        if (changed) MarkDirty(colony);
    }

    // ---- Relatives ------------------------------------------------------------------------

    public CitizenRecord? PartnerOf(Colony colony, CitizenRecord c) =>
        c.PartnerId == 0 ? null : colony.Citizens.Find(o => o.CitizenId == c.PartnerId);

    public List<CitizenRecord> ParentsOf(Colony colony, CitizenRecord c) =>
        colony.Citizens.Where(o => c.ParentIds.Contains(o.CitizenId)).ToList();

    public List<CitizenRecord> ChildrenOf(Colony colony, CitizenRecord c) =>
        colony.Citizens.Where(o => o.ParentIds.Contains(c.CitizenId)).OrderBy(o => o.CitizenId).ToList();

    /// <summary>Brothers and sisters: anyone else with a parent in common.</summary>
    public List<CitizenRecord> SiblingsOf(Colony colony, CitizenRecord c) => c.ParentIds.Count == 0
        ? new List<CitizenRecord>()
        : colony.Citizens.Where(o => o != c && o.ParentIds.Intersect(c.ParentIds).Any()).OrderBy(o => o.CitizenId).ToList();

    /// <summary>Single: never paired, or the partner has died or left the colony.</summary>
    bool IsSingle(Colony colony, CitizenRecord c) => PartnerOf(colony, c) is not { Alive: true };

    /// <summary>A settler who can pair up today.</summary>
    bool CanPair(Colony colony, CitizenRecord c, double now) =>
        c.Alive && !c.Visitor && !c.IsChild && !c.IsDowned && IsSingle(colony, c) && !TraitFlag(c, "noPair") // Loner
        && now - c.JoinedTotalDays >= Config.FamilyCoupleAfterDays * TraitMult(c, "pairAfter"); // Charming pairs sooner

    // ---- The daily family tick ------------------------------------------------------------

    /// <summary>
    /// Part of the colony tick: children who are old enough grow up at once; once per in-game day
    /// singles pair up and couples may have a child.
    /// </summary>
    void TickFamilies(Colony colony, double now)
    {
        EnsureSurnames(colony);
        foreach (var child in colony.Citizens.Where(c => c.Alive && c.IsChild && now >= c.GrowsUpTotalDays).ToList()) GrowUp(colony, child);
        TickBoarders(colony, now);

        if (!Config.Families) return;
        int today = (int)now;
        if (colony.LastFamilyDay == today) return;
        // The first time round only marks the day, so a colony loaded at night does not get a burst at once.
        bool first = colony.LastFamilyDay < 0;
        colony.LastFamilyDay = today;
        MarkDirty(colony);
        if (first) return;

        PairUp(colony, now);
        Births(colony, now);
        foreach (var child in colony.Citizens.Where(c => c.Alive && c.IsChild)) KeepChildNearHome(colony, child);
    }

    void PairUp(Colony colony, double now)
    {
        for (int made = 0; made < Math.Max(0, Config.FamilyCouplesPerDay); made++)
        {
            // Charming settlers are picked first.
            var singles = colony.Citizens.Where(c => CanPair(colony, c, now)).OrderBy(_ => sapi.World.Rand.Next()).OrderByDescending(c => TraitFlag(c, "pairFirst")).ToList();
            CitizenRecord? a = null, b = null;
            foreach (var first in singles)
            {
                // Never brothers and sisters, nor a parent and child.
                b = singles.FirstOrDefault(o => o != first && (Config.FamilyCouplesAnySex || o.Sex != first.Sex)
                    && !first.ParentIds.Contains(o.CitizenId) && !o.ParentIds.Contains(first.CitizenId) && !o.ParentIds.Intersect(first.ParentIds).Any());
                if (b != null) { a = first; break; }
            }
            if (a == null || b == null) return;
            MakeCouple(colony, a, b);
        }
    }

    /// <summary>Pairs two settlers and moves them in together. Also used by /colony couple for testing.</summary>
    public void MakeCouple(Colony colony, CitizenRecord a, CitizenRecord b)
    {
        a.PartnerId = b.CitizenId;
        b.PartnerId = a.CitizenId;
        // A new couple waits a little before their first child.
        a.LastChildTotalDays = b.LastChildTotalDays = sapi.World.Calendar.TotalDays;
        MoveInTogether(colony, a, b);
        MarkDirty(colony);
        TellMembers(colony, "createacolony:family-couple", a.Name, b.Name);
        LogEvent(colony, "event-couple", a.Name, b.Name);
    }

    /// <summary>
    /// Gives a couple one home: a furniture double bed both fit in, else beds side by side in the
    /// Residence one of them lives in. If there is no room they keep the beds they have.
    /// </summary>
    void MoveInTogether(Colony colony, CitizenRecord a, CitizenRecord b)
    {
        var beds = BedList(colony);
        var taken = BedsTaken(colony);
        var boxes = ResidenceBoxes(colony);
        int Free(BlockPos pos) => beds.TryGetValue(pos, out int places) ? places - taken.GetValueOrDefault(pos) : 0;

        // One of them already sleeps in a bed with room for the other.
        foreach (var (stay, move) in new[] { (a, b), (b, a) })
        {
            if (stay.HomeBedPos != null && Free(stay.HomeBedPos) > 0)
            {
                move.HomeBedPos = stay.HomeBedPos.Copy();
                return;
            }
        }
        // A double bed nobody sleeps in.
        var dbl = beds.Where(kv => kv.Value >= 2 && taken.GetValueOrDefault(kv.Key) == 0).Select(kv => kv.Key)
            .OrderByDescending(p => ResidenceAt(colony, p, boxes) != null)
            .ThenBy(p => p.DistanceSqTo((a.HomeBedPos ?? colony.TownHallPos).X, (a.HomeBedPos ?? colony.TownHallPos).Y, (a.HomeBedPos ?? colony.TownHallPos).Z))
            .FirstOrDefault();
        if (dbl != null)
        {
            a.HomeBedPos = dbl.Copy();
            b.HomeBedPos = dbl.Copy();
            return;
        }
        // A free bed in the Residence the other one lives in, as close to their bed as can be.
        foreach (var (stay, move) in new[] { (a, b), (b, a) })
        {
            if (stay.HomeBedPos == null || ResidenceAt(colony, stay.HomeBedPos, boxes) is not { } home) continue;
            var near = beds.Keys.Where(p => Free(p) > 0 && ResidenceAt(colony, p, boxes) == home)
                .OrderBy(p => p.DistanceSqTo(stay.HomeBedPos.X, stay.HomeBedPos.Y, stay.HomeBedPos.Z)).FirstOrDefault();
            if (near == null) continue;
            move.HomeBedPos = near.Copy();
            return;
        }
    }

    /// <summary>Why a couple cannot have a child right now (a lang key), or null when they can.</summary>
    public string? ChildBlocker(Colony colony, CitizenRecord a, CitizenRecord b, double now)
    {
        if (!Config.Families) return "createacolony:family-off";
        if (a.Sex == b.Sex) return "createacolony:family-same-sex";
        if (!a.Alive || !b.Alive || a.IsDowned || b.IsDowned) return "createacolony:family-not-well";
        float hungry = Config.SaturationMax * Config.EatBelowFraction;
        if ((a.Saturation >= 0 && a.Saturation < hungry) || (b.Saturation >= 0 && b.Saturation < hungry)) return "createacolony:family-hungry";
        int children = ChildrenOf(colony, a).Count(c => c.ParentIds.Contains(b.CitizenId));
        if (children >= Config.FamilyMaxChildren) return "createacolony:family-enough-children";
        if (now - Math.Max(a.LastChildTotalDays, b.LastChildTotalDays) < Config.FamilyChildCooldownDays) return "createacolony:family-waiting";
        if (Config.FamilyMaxColonySize > 0 && colony.Citizens.Count(c => c.Alive && !c.Visitor) >= Config.FamilyMaxColonySize) return "createacolony:family-colony-full";
        if (IsFull(colony)) return "createacolony:family-colony-full";
        if (Config.FamilyChildNeedsFreeBed && FreeBeds(colony) == 0) return "createacolony:family-no-bed";
        if (!Config.BoardersHaveChildren && (IsBoarder(colony, a) || IsBoarder(colony, b))) return "createacolony:family-boarding";
        return null;
    }

    void Births(Colony colony, double now)
    {
        var couples = colony.Citizens.Where(c => c.Alive && !c.Visitor && c.Sex == "female" && PartnerOf(colony, c) is { Alive: true, Sex: "male" }).ToList();
        foreach (var mother in couples)
        {
            var father = PartnerOf(colony, mother)!;
            if (ChildBlocker(colony, mother, father, now) != null) continue;
            double chance = Config.FamilyChildChancePerDay + TraitAdd(mother, "childChance") + TraitAdd(father, "childChance"); // Fertile
            if (sapi.World.Rand.NextDouble() >= chance) continue;
            HaveChild(colony, mother, father);
        }
    }

    /// <summary>A couple's new child, beside the mother (or their home). Returns null when it could not be placed.</summary>
    public CitizenRecord? HaveChild(Colony colony, CitizenRecord mother, CitizenRecord father)
    {
        string sex = RandomSex();
        string surname = sapi.World.Rand.Next(2) == 0 ? father.Surname : mother.Surname;
        if (surname.Length == 0) surname = father.Surname.Length > 0 ? father.Surname : mother.Surname;
        string first = ChildFirstName(colony, sex, mother, father);
        string name = surname.Length > 0 ? $"{first} {surname}" : first;

        Vec3d at = sapi.World.GetEntityById(mother.EntityId) is { Alive: true } m ? m.Pos.XYZ.Clone()
            : mother.HomeBedPos != null ? Beside(mother.HomeBedPos) : Beside(colony.TownHallPos);
        var child = SpawnSettler(colony, name, sex, at, child: true);
        if (child == null) return null;
        child.Surname = surname;
        child.ParentIds = new List<int> { mother.CitizenId, father.CitizenId };
        InheritTraits(colony, child);
        mother.LastChildTotalDays = father.LastChildTotalDays = sapi.World.Calendar.TotalDays;
        AddThought(mother, "parent");
        AddThought(father, "parent");
        EnsureBed(colony, child);
        KeepChildNearHome(colony, child);
        MarkDirty(colony);
        TellMembers(colony, "createacolony:family-born", mother.Name, father.Name, child.Name);
        LogEvent(colony, "event-born", child.Name, mother.Name, father.Name);
        return child;
    }

    /// <summary>A first name for a child that none of its brothers and sisters, nor its parents, have.</summary>
    string ChildFirstName(Colony colony, string sex, CitizenRecord mother, CitizenRecord father)
    {
        var family = new HashSet<string>(ChildrenOf(colony, mother).Concat(ChildrenOf(colony, father)).Append(mother).Append(father).Select(FirstNameOf));
        for (int attempt = 0; attempt < 20; attempt++)
        {
            string first = RandomFirstName(colony, sex);
            if (!family.Contains(first)) return first;
        }
        return RandomFirstName(colony, sex);
    }

    static string FirstNameOf(CitizenRecord c) => c.Surname.Length > 0 && c.Name.EndsWith(" " + c.Surname) ? c.Name[..^(c.Surname.Length + 1)] : c.Name;

    static string FirstNameOf(string name)
    {
        int space = name.Trim().LastIndexOf(' ');
        return space < 0 ? name.Trim() : name.Trim()[..space];
    }

    /// <summary>A child is grown up: the small settler is swapped for an adult one in the same spot, ready to be given a job.</summary>
    public void GrowUp(Colony colony, CitizenRecord child)
    {
        child.GrowsUpTotalDays = 0;
        child.JoinedTotalDays = sapi.World.Calendar.TotalDays;
        InheritSkills(colony, child);
        InheritPassions(colony, child);
        SchoolGrowUp(colony, child);
        if (sapi.World.GetEntityById(child.EntityId) is { Alive: true } entity)
        {
            var at = entity.Pos.XYZ.Clone();
            entity.Die(EnumDespawnReason.Removed);
            SpawnEntityFor(colony, child, at);
        }
        MarkDirty(colony);
        TellMembers(colony, "createacolony:family-grown-up", child.Name);
        LogEvent(colony, "event-grown-up", child.Name);
    }

    /// <summary>Children wander near their bed (or a parent's) instead of where they were born.</summary>
    void KeepChildNearHome(Colony colony, CitizenRecord child)
    {
        var home = child.HomeBedPos ?? ParentsOf(colony, child).Select(p => p.HomeBedPos).FirstOrDefault(p => p != null);
        if (home == null || sapi.World.GetEntityById(child.EntityId) is not EntitySettler { Alive: true } entity) return;
        SetWanderHome(entity, home.ToVec3d().Add(0.5, 1, 0.5));
    }

    /// <summary>In-game hours until a child grows up, 0 for adults.</summary>
    public int HoursToGrowUp(CitizenRecord c) =>
        c.IsChild ? (int)Math.Ceiling(Math.Max(0, c.GrowsUpTotalDays - sapi.World.Calendar.TotalDays) * sapi.World.Calendar.HoursPerDay) : 0;

    // ---- Residences -----------------------------------------------------------------------

    /// <summary>The Residence whose building a block lies in, or null. Pass ResidenceBoxes when looking up many blocks.</summary>
    public Building? ResidenceAt(Colony colony, BlockPos pos, List<(Building B, BlockPos Min, BlockPos Max)>? boxes = null)
    {
        foreach (var (b, min, max) in boxes ?? ResidenceBoxes(colony))
        {
            if (pos.X >= min.X && pos.X <= max.X && pos.Y >= min.Y && pos.Y <= max.Y && pos.Z >= min.Z && pos.Z <= max.Z) return b;
        }
        return null;
    }

    /// <summary>The blocks each Residence covers.</summary>
    public List<(Building B, BlockPos Min, BlockPos Max)> ResidenceBoxes(Colony colony) => colony.Buildings
        .Where(b => IsHome(b.Type))
        .Select(b => { var (min, max) = BuildingBox(b); return (b, min, max); })
        .ToList();

    /// <summary>Every bed on the colony's land and how many sleep in it.</summary>
    Dictionary<BlockPos, int> BedList(Colony colony) =>
        FindBlockEntities<BlockEntity>(colony).Where(b => BedPlaces(b) > 0).ToDictionary(b => b.Pos.Copy(), BedPlaces);

    /// <summary>The settlers who sleep in a Residence, and how many beds it has and how many are free.</summary>
    public (List<CitizenRecord> Residents, int Beds, int Free) ResidenceInfo(Colony colony, Building residence)
    {
        var boxes = ResidenceBoxes(colony);
        var beds = BedList(colony).Where(kv => ResidenceAt(colony, kv.Key, boxes) == residence).ToList();
        var taken = BedsTaken(colony);
        var residents = colony.Citizens.Where(c => c.Alive && c.HomeBedPos != null && ResidenceAt(colony, c.HomeBedPos, boxes) == residence).ToList();
        int places = beds.Sum(kv => kv.Value);
        return (residents, places, Math.Max(0, places - beds.Sum(kv => Math.Min(kv.Value, taken.GetValueOrDefault(kv.Key)))));
    }

    /// <summary>A settler, their partner and their children: the people who move house together.</summary>
    List<CitizenRecord> MovingHousehold(Colony colony, CitizenRecord c)
    {
        var head = c.IsChild && ParentsOf(colony, c).FirstOrDefault(p => p.Alive) is { } parent ? parent : c;
        var people = new List<CitizenRecord> { head };
        if (PartnerOf(colony, head) is { Alive: true } partner) people.Add(partner);
        people.AddRange(colony.Citizens.Where(k => k.Alive && k.IsChild && !people.Contains(k) && k.ParentIds.Any(id => people.Any(p => p.CitizenId == id))));
        return people;
    }

    /// <summary>
    /// Moves a settler and their household into free beds of a Residence, chosen by a player on its
    /// Residents tab. Returns a lang key when there is not room for all of them.
    /// </summary>
    public string? MoveIntoResidence(Colony colony, Building residence, CitizenRecord citizen)
    {
        var boxes = ResidenceBoxes(colony);
        var beds = BedList(colony).Where(kv => ResidenceAt(colony, kv.Key, boxes) == residence).ToDictionary(kv => kv.Key, kv => kv.Value);
        return Rehouse(colony, MovingHousehold(colony, citizen), beds) ? null : "createacolony:residence-no-room";
    }

    /// <summary>
    /// Moves a settler and their household out of a Residence: into another Residence with room if there
    /// is one, else any free bed on the colony's land. They keep their beds when there is nowhere to go.
    /// </summary>
    public string? MoveOutOfResidence(Colony colony, Building residence, CitizenRecord citizen)
    {
        var boxes = ResidenceBoxes(colony);
        var elsewhere = BedList(colony).Where(kv => ResidenceAt(colony, kv.Key, boxes) != residence).ToList();
        var people = MovingHousehold(colony, citizen);
        var homes = elsewhere.Where(kv => ResidenceAt(colony, kv.Key, boxes) != null).ToDictionary(kv => kv.Key, kv => kv.Value);
        if (Rehouse(colony, people, homes) || Rehouse(colony, people, elsewhere.ToDictionary(kv => kv.Key, kv => kv.Value))) return null;
        return "createacolony:residence-nowhere";
    }

    /// <summary>
    /// Gives a household beds from a set, if the set has a free place for each of them: a couple a
    /// double bed when there is one, then everyone as close as can be to the first bed taken.
    /// </summary>
    bool Rehouse(Colony colony, List<CitizenRecord> people, Dictionary<BlockPos, int> beds)
    {
        var taken = colony.Citizens.Where(c => c.Alive && c.HomeBedPos != null && !people.Contains(c))
            .GroupBy(c => c.HomeBedPos!).ToDictionary(g => g.Key, g => g.Count());
        var free = beds.ToDictionary(kv => kv.Key, kv => kv.Value - taken.GetValueOrDefault(kv.Key));
        if (free.Values.Where(v => v > 0).Sum() < people.Count) return false;

        bool couple = people.Count(p => !p.IsChild) > 1;
        BlockPos? first = null;
        foreach (var c in people)
        {
            var anchor = first;
            var bed = free.Where(kv => kv.Value > 0).Select(kv => kv.Key)
                .OrderByDescending(p => anchor == null && couple && free[p] >= 2)
                .ThenBy(p => anchor == null ? 0.0 : p.DistanceSqTo(anchor.X, anchor.Y, anchor.Z))
                .First();
            free[bed]--;
            c.HomeBedPos = bed.Copy();
            first ??= bed;
        }
        return true;
    }

    /// <summary>
    /// The households in a list of settlers: each couple with their children, and anyone else on their
    /// own. Partners first, then children in birth order.
    /// </summary>
    public List<List<CitizenRecord>> Households(Colony colony, List<CitizenRecord> people)
    {
        var left = people.OrderBy(c => c.IsChild).ThenBy(c => c.CitizenId).ToList();
        var result = new List<List<CitizenRecord>>();
        while (left.Count > 0)
        {
            var head = left[0];
            var house = new List<CitizenRecord> { head };
            if (PartnerOf(colony, head) is { } partner && left.Contains(partner)) house.Add(partner);
            house.AddRange(left.Where(c => c.IsChild && c.ParentIds.Any(id => house.Any(h => h.CitizenId == id)) && !house.Contains(c)));
            // A child whose parents live elsewhere still forms its own line.
            result.Add(house);
            left.RemoveAll(house.Contains);
        }
        return result;
    }

    /// <summary>A short line about one relative for the family lists: name, and "child" or "died" when it applies.</summary>
    public string RelativeLine(CitizenRecord c)
    {
        string text = c.Name;
        if (!c.Alive) text += " " + Lang.Get("createacolony:family-deceased");
        else if (c.IsChild) text += " " + Lang.Get("createacolony:family-child-tag");
        return text;
    }
}
