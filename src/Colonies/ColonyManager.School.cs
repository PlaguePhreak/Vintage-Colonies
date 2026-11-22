using System;
using System.Collections.Generic;
using System.Linq;
using CreateAColony.Settlers;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace CreateAColony.Colonies;

/// <summary>
/// The School (0.193.0). A teacher works there in the morning (LessonFromHour to LessonToHour) and the colony's
/// children walk over and sit in class (AiTaskSettlerChildDay). Each child gets one lesson a day while the teacher
/// is there, in one of the teacher's three best skills (a different one each day). In the afternoon, up to
/// PlayToHour, a child follows a parent at work, unless the parent's job is dangerous or a raid is on, and picks
/// up WatchXp of that job's skill once a day. When a child with SchoolMinDays lessons or more grows up, they get
/// 2 to 4 extra levels in the skills they were taught most, sometimes a flame in the top one, plus what they
/// picked up watching. Children have no mood, so the parents feel it: glad when a child is at school, sorry
/// when a child had nothing to do all day.
/// </summary>
public partial class ColonyManager
{
    /// <summary>When each School last had its teacher at work there (total days).</summary>
    readonly Dictionary<int, double> teachingAt = new();
    /// <summary>Each colony's school places (child id to School id), worked out again every 30 seconds.</summary>
    readonly Dictionary<string, (long Ms, Dictionary<int, int> Places)> schoolPlaces = new();
    /// <summary>The last day TickSchool looked back on, per colony.</summary>
    readonly Dictionary<string, int> schoolDayDone = new();

    public bool LessonTime()
    {
        if (!Config.Schools) return false;
        float hour = sapi.World.Calendar.HourOfDay;
        return hour >= Config.LessonFromHour && hour < Config.LessonToHour;
    }

    public bool AfterSchoolTime()
    {
        if (!Config.Schools) return false;
        float hour = sapi.World.Calendar.HourOfDay;
        return hour >= Config.LessonToHour && hour < Config.PlayToHour;
    }

    public List<Building> Schools(Colony colony) => colony.Buildings.Where(b => b.Type == BuildingTypes.School && b.Level >= 1).ToList();

    /// <summary>Children a School takes at its level.</summary>
    public int SchoolPlaceCount(Building school)
    {
        var table = Config.SchoolPlaces;
        if (table == null || table.Length == 0) return 0;
        return Math.Max(0, table[Math.Clamp(school.Level, 1, table.Length) - 1]);
    }

    CitizenRecord? TeacherOf(Colony colony, Building school) => WorkersAt(colony, school).FirstOrDefault(w => w.Alive && !w.IsDowned);

    Dictionary<int, int> SchoolPlaces(Colony colony)
    {
        long now = sapi.World.ElapsedMilliseconds;
        if (schoolPlaces.TryGetValue(colony.ColonyId, out var cached) && now - cached.Ms < 30_000) return cached.Places;
        var places = new Dictionary<int, int>();
        var open = Schools(colony).Where(s => TeacherOf(colony, s) != null).Select(s => (School: s, Left: SchoolPlaceCount(s))).ToList();
        foreach (var child in colony.Citizens.Where(c => c.Alive && c.IsChild && !c.Visitor).OrderBy(c => c.CitizenId))
        {
            int i = open.FindIndex(o => o.Left > 0);
            if (i < 0) break;
            places[child.CitizenId] = open[i].School.BuildingId;
            open[i] = (open[i].School, open[i].Left - 1);
        }
        schoolPlaces[colony.ColonyId] = (now, places);
        return places;
    }

    /// <summary>The School this child has a place at, or null.</summary>
    public Building? SchoolFor(Colony colony, CitizenRecord child)
    {
        if (!Config.Schools || !child.IsChild) return null;
        return SchoolPlaces(colony).TryGetValue(child.CitizenId, out int id) ? colony.Buildings.Find(b => b.BuildingId == id) : null;
    }

    /// <summary>The children with a place at this School.</summary>
    public List<CitizenRecord> PupilsOf(Colony colony, Building school)
    {
        var places = SchoolPlaces(colony);
        return colony.Citizens.Where(c => places.TryGetValue(c.CitizenId, out int id) && id == school.BuildingId).ToList();
    }

    /// <summary>True while the teacher has been at work at this School in the last hour.</summary>
    bool TeacherPresent(Building school) =>
        teachingAt.TryGetValue(school.BuildingId, out double at) && sapi.World.Calendar.TotalDays - at < 1 / 24.0;

    /// <summary>The skill taught at a School on a day: the teacher's three best skills (but teaching) in turn.</summary>
    public string? LessonSkill(Colony colony, Building school, int day)
    {
        var teacher = TeacherOf(colony, school);
        var all = Skills().Where(s => s.Code != "teaching").ToList();
        if (all.Count == 0) return null;
        var best = teacher == null ? new List<string>()
            : all.Select(s => s.Code).Where(code => SkillXp(teacher, code) > 0).OrderByDescending(code => SkillXp(teacher, code)).Take(3).ToList();
        if (best.Count == 0) return all[Math.Abs(day + school.BuildingId) % all.Count].Code;
        return best[Math.Abs(day) % best.Count];
    }

    /// <summary>What the teacher's task should show: why there is no class, or that it is teaching.</summary>
    public string TeacherStatus(Colony colony, Building school)
    {
        if (!LessonTime()) return "teacher-after-school";
        return PupilsOf(colony, school).Count == 0 ? "teacher-no-children" : "teacher-teaching";
    }

    /// <summary>One piece of the teacher's work: the School counts as open, and the teacher learns from teaching.</summary>
    public void TeacherWork(Colony colony, Building school, CitizenRecord teacher)
    {
        teachingAt[school.BuildingId] = sapi.World.Calendar.TotalDays;
        int pupils = PupilsOf(colony, school).Count(c => c.LastLessonDay == (int)sapi.World.Calendar.TotalDays);
        if (pupils > 0) AddSkillXp(colony, teacher, "teaching", (Skill("teaching")?.XpPerWork ?? 1) * pupils);
    }

    /// <summary>A child sat through class: the day's lesson counts, once a day, if the teacher is there. True when it counted.</summary>
    public bool AttendLesson(Colony colony, CitizenRecord child, Building school)
    {
        int today = (int)sapi.World.Calendar.TotalDays;
        if (!child.IsChild || child.LastLessonDay == today || !TeacherPresent(school)) return false;
        if (LessonSkill(colony, school, today) is not { } skill) return false;
        child.LastLessonDay = today;
        child.SchoolDays++;
        child.LessonSkills[skill] = child.LessonSkills.GetValueOrDefault(skill) + 1;
        foreach (var parent in ParentsOf(colony, child).Where(p => p.Alive)) AddThought(parent, "childatschool");
        MarkDirty(colony);
        return true;
    }

    /// <summary>The parent a child may follow at work now: alive, up, in a safe job, with no raid on. Null when none.</summary>
    public CitizenRecord? ParentToFollow(Colony colony, CitizenRecord child)
    {
        if (!Config.Schools || !child.IsChild || RaidOn(colony) != null) return null;
        var unsafeJobs = Config.ChildNoFollowJobs ?? Array.Empty<string>();
        return ParentsOf(colony, child)
            .Where(p => p.Alive && !p.IsChild && !p.IsDowned && !string.IsNullOrEmpty(p.JobCode) && !unsafeJobs.Contains(p.JobCode) && SkillForJob(p.JobCode) != null)
            .Where(p => sapi.World.GetEntityById(p.EntityId) is { Alive: true })
            .OrderBy(p => p.CitizenId)
            .FirstOrDefault();
    }

    /// <summary>A child watched a parent work long enough: WatchXp of the parent's job skill, once a day.</summary>
    public void WatchParent(Colony colony, CitizenRecord child, CitizenRecord parent)
    {
        int today = (int)sapi.World.Calendar.TotalDays;
        if (!child.IsChild || child.LastWatchDay == today || SkillForJob(parent.JobCode) is not { } skill) return;
        child.LastWatchDay = today;
        child.WatchedXp[skill.Code] = child.WatchedXp.GetValueOrDefault(skill.Code) + Math.Max(0, Config.WatchXp);
        MarkDirty(colony);
    }

    /// <summary>Once a day: parents of a child who had neither a lesson nor a parent to watch yesterday feel sorry for them.</summary>
    void TickSchool(Colony colony)
    {
        if (!Config.Schools) return;
        int today = (int)sapi.World.Calendar.TotalDays;
        if (schoolDayDone.TryGetValue(colony.ColonyId, out int done) && done == today) return;
        bool first = !schoolDayDone.ContainsKey(colony.ColonyId);
        schoolDayDone[colony.ColonyId] = today;
        if (first) return;
        int yesterday = today - 1;
        foreach (var child in colony.Citizens.Where(c => c.Alive && c.IsChild && !c.Visitor && c.JoinedTotalDays <= yesterday))
        {
            if (child.LastLessonDay >= yesterday || child.LastWatchDay >= yesterday) continue;
            foreach (var parent in ParentsOf(colony, child).Where(p => p.Alive)) AddThought(parent, "childbored");
        }
    }

    /// <summary>
    /// When a child grows up (after InheritSkills and InheritPassions): extra levels for their lessons, maybe a flame,
    /// and the XP they picked up watching a parent. Returns the extra levels given.
    /// </summary>
    public int SchoolGrowUp(Colony colony, CitizenRecord child)
    {
        int extra = 0;
        if (Config.Skills)
        {
            if (child.SchoolDays >= Math.Max(1, Config.SchoolMinDays))
            {
                extra = Math.Clamp(1 + child.SchoolDays / Math.Max(1, Config.SchoolDaysPerExtraLevel), Config.SchoolMinLevels, Math.Max(Config.SchoolMinLevels, Config.SchoolMaxLevels));
                var taught = child.LessonSkills.Where(kv => kv.Value > 0 && Skill(kv.Key) != null && !IsIncapable(child, kv.Key))
                    .OrderByDescending(kv => kv.Value).Select(kv => kv.Key).Take(3).ToList();
                if (taught.Count == 0) extra = 0;
                for (int i = 0; i < extra; i++)
                {
                    string code = taught[i % taught.Count];
                    int level = Math.Min(MaxLevel, SkillLevel(child, code) + 1);
                    child.SkillXp[code] = Math.Max(SkillXp(child, code), XpForLevel(level));
                }
                if (taught.Count > 0 && Config.SkillPassions && sapi.World.Rand.NextDouble() < Config.SchoolPassionChance)
                {
                    child.Passions[taught[0]] = Math.Min(2, child.Passions.GetValueOrDefault(taught[0]) + 1);
                }
            }
            foreach (var (code, xp) in child.WatchedXp)
            {
                if (xp <= 0 || Skill(code) == null || IsIncapable(child, code)) continue;
                child.SkillXp[code] = SkillXp(child, code) + xp;
            }
        }
        child.LessonSkills.Clear();
        child.WatchedXp.Clear();
        if (extra > 0) TellMembers(colony, "createacolony:school-graduated", child.Name, extra.ToString());
        return extra;
    }

    /// <summary>The School's lines in its hut window.</summary>
    public List<string> SchoolInfo(Colony colony, Building school)
    {
        var lines = new List<string>
        {
            Lang.Get("createacolony:school-info", PupilsOf(colony, school).Count, SchoolPlaceCount(school), (int)Config.LessonFromHour, (int)Config.LessonToHour)
        };
        if (TeacherOf(colony, school) == null) lines.Add(Lang.Get("createacolony:school-no-teacher"));
        else if (LessonSkill(colony, school, (int)sapi.World.Calendar.TotalDays) is { } skill) lines.Add(Lang.Get("createacolony:school-lesson", Lang.Get("createacolony:skill-" + skill)));
        int waiting = colony.Citizens.Count(c => c.Alive && c.IsChild && !c.Visitor && SchoolFor(colony, c) == null);
        if (waiting > 0) lines.Add(Lang.Get("createacolony:school-no-place", waiting));
        return lines;
    }

    /// <summary>A child's school line on their settler page; empty for adults.</summary>
    public string SchoolText(Colony colony, CitizenRecord child)
    {
        if (!Config.Schools || !child.IsChild) return "";
        var school = SchoolFor(colony, child);
        string where = school != null ? Lang.Get("createacolony:school-child-at", BuildingName(school))
            : Lang.Get(Schools(colony).Count == 0 ? "createacolony:school-child-none" : "createacolony:school-child-no-place");
        string lessons = Lang.Get("createacolony:school-child-lessons", child.SchoolDays);
        var top = child.LessonSkills.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault();
        if (top != null) lessons += " " + Lang.Get("createacolony:school-child-best", Lang.Get("createacolony:skill-" + top));
        return where + "<br>" + lessons;
    }
}
