using System;
using System.Collections.Generic;
using System.Linq;
using CreateAColony.Settlers;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace CreateAColony.Colonies;

/// <summary>One skill from config/skills.json.</summary>
public class SkillDef
{
    public string Code = "";
    /// <summary>The job whose work trains this skill and goes faster with it; empty for a skill everyone trains (strength, fitness).</summary>
    public string Job = "";
    /// <summary>Other jobs this skill counts for (the quarrier shares Mining with the miner).</summary>
    public string[] AlsoJobs = Array.Empty<string>();
    /// <summary>XP for one piece of work: a block built, a tree felled, a hit landed.</summary>
    public int XpPerWork = 1;
    /// <summary>Strength XP that the same piece of work gives, for heavy work.</summary>
    public int StrengthPerWork;
    /// <summary>Each usable level makes the job's work this much faster (0.04 = 4%).</summary>
    public float WorkSpeedPerLevel = 0.04f;
    /// <summary>
    /// Milestone perks: effect key to its values at the levels in config SkillPerkLevels (5, 10, 15, 20).
    /// "yield" and "portion" are chances of one more item, "packSlots" adds slots, the rest multiply.
    /// </summary>
    public Dictionary<string, float[]> Perks = new();

    public bool IsPassive => Job.Length == 0;
    public bool Covers(string? job) => job != null && job.Length > 0 && (job == Job || AlsoJobs.Contains(job));
}

/// <summary>What the settler window and the hiring lists show about one skill.</summary>
public readonly record struct SkillInfo(string Code, int Level, int Usable, int Xp, int LevelXp, int NextXp, string CapNote, int Passion, string Incapable, bool Decaying, int XpToday);

/// <summary>
/// Skills, RimWorld style: settlers learn by doing and get better at their job, from level 0 to 20.
/// Strength and Fitness grow for everyone. How much of a skill a settler can use is limited by the level
/// of the building they work at (or sleep in, for Strength and Fitness): a master at a level 1 hut works
/// like a level 4 one until the hut is upgraded, because skill is no use without the tools. XP keeps
/// coming past the cap, so nothing is lost. Passions (one or two flames) make a skill grow faster, some
/// traits rule a skill out, skills above 10 fade a little every day, and each skill gains full XP only
/// up to a daily cap. Numbers live in config/skills.json and ColonyConfig.
/// </summary>
public partial class ColonyManager
{
    const string SkillsAsset = "config/skills.json";
    public const string Strength = "strength";
    public const string Fitness = "fitness";
    /// <summary>Strength XP for carrying a full load somewhere.</summary>
    const int StrengthPerFullLoad = 1;
    /// <summary>Fitness XP per this many blocks walked.</summary>
    public const float FitnessBlocksPerXp = 50f;

    List<SkillDef>? skills;

    /// <summary>All skills, in the order of skills.json.</summary>
    public List<SkillDef> Skills()
    {
        if (skills != null) return skills;
        var loaded = sapi.Assets.TryGet(new AssetLocation(CreateAColonyModSystem.ModId, SkillsAsset))?.ToObject<SkillsFile>()?.Skills;
        skills = loaded?.Where(s => s.Code.Length > 0).ToList() ?? new List<SkillDef>();
        if (skills.Count == 0) Mod.Logger.Warning("No skills loaded from {0}; settlers have no skills", SkillsAsset);
        return skills;
    }

    class SkillsFile { public List<SkillDef> Skills = new(); }

    public SkillDef? Skill(string code) => Skills().Find(s => s.Code == code);

    /// <summary>The skill a job trains, or null for a job without one.</summary>
    public SkillDef? SkillForJob(string? job) => job == null ? null : Skills().Find(s => s.Covers(job));

    // ---- Levels and XP --------------------------------------------------------------------------

    public int MaxLevel => Math.Max(1, Config.SkillLevelMax);

    /// <summary>
    /// Total XP needed to reach a level. Level n costs SkillXpPerLevel x n on top of the one below up to
    /// SkillSteepFrom (75, 150 ... 4,125 for level 10), then steps grow SkillSteepFactor times as fast
    /// (900, 1,050 ... 19,875 for level 20), the shape of RimWorld's curve.
    /// </summary>
    public int XpForLevel(int level)
    {
        int per = Math.Max(0, Config.SkillXpPerLevel), from = Math.Max(0, Config.SkillSteepFrom), factor = Math.Max(1, Config.SkillSteepFactor);
        int total = 0;
        for (int n = 1; n <= level; n++) total += per * (n <= from ? n : from + factor * (n - from));
        return total;
    }

    /// <summary>The level this much XP has earned, at most MaxLevel.</summary>
    public int LevelOf(int xp)
    {
        int level = 0;
        while (level < MaxLevel && xp >= XpForLevel(level + 1)) level++;
        return level;
    }

    public int SkillXp(CitizenRecord c, string code) => c.SkillXp.GetValueOrDefault(code);

    /// <summary>The settler's real level in a skill, whatever their building allows.</summary>
    public int SkillLevel(CitizenRecord c, string code) => LevelOf(SkillXp(c, code));

    /// <summary>
    /// Gives a settler XP in a skill. Children, the dead and the incapable learn nothing. The amount is
    /// multiplied by passion, learning rate (Fast/Slow Learner), mood and the config multiplier, then
    /// counts in full only up to the daily cap. Logs levels 5, 10, 15 and the top level.
    /// </summary>
    public void AddSkillXp(Colony colony, CitizenRecord c, string code, int amount)
    {
        if (!Config.Skills || amount <= 0 || !c.Alive || c.IsChild || Skill(code) == null || IsIncapable(c, code)) return;
        double scaled = amount * Math.Max(0, Config.SkillXpMultiplier) * PassionXpFactor(c, code) * TraitMult(c, "learning") * MoodLearnFactor(c) * ChurchLearnFactor(c);
        // The daily soft cap: past SkillDailyCap a skill only gets SkillOverCapFactor of the XP.
        int today = (int)sapi.World.Calendar.TotalDays;
        if (c.SkillXpDay != today)
        {
            c.SkillXpDay = today;
            c.SkillXpToday.Clear();
        }
        int cap = Config.SkillDailyCap;
        int got = c.SkillXpToday.GetValueOrDefault(code);
        if (cap > 0)
        {
            double full = Math.Clamp(cap - got, 0, scaled);
            scaled = full + (scaled - full) * Math.Clamp(Config.SkillOverCapFactor, 0, 1);
        }
        // Whole XP only: the fraction is a chance of one more, so small amounts still add up on average.
        int gain = (int)Math.Floor(scaled);
        if (sapi.World.Rand.NextDouble() < scaled - gain) gain++;
        if (gain <= 0) return;
        c.SkillXpToday[code] = got + gain;
        int before = SkillLevel(c, code);
        c.SkillXp[code] = Math.Min(SkillXp(c, code) + gain, XpForLevel(MaxLevel));
        int after = SkillLevel(c, code);
        if (after > before)
        {
            if (after % 5 == 0 || after == MaxLevel) LogEvent(colony, "event-skill-up", c.Name, "@skill-" + code, after.ToString());
            AddThought(c, "skillup", 0);
        }
        MarkDirty(colony);
    }

    /// <summary>One piece of a job's work done: XP for its skill, plus Strength for heavy work.</summary>
    public void GainWorkXp(Colony colony, CitizenRecord c, string? job, int times = 1)
    {
        var skill = SkillForJob(job);
        if (skill == null || times <= 0) return;
        // A job trait (Green Thumb for a farmer) now gives a passion for its skill, which is what speeds it up.
        AddSkillXp(colony, c, skill.Code, skill.XpPerWork * times);
        if (skill.StrengthPerWork > 0) AddSkillXp(colony, c, Strength, skill.StrengthPerWork * times);
        // Doing work they love lifts their mood for a while (RimWorld's +8 / +14).
        if (Passion(c, skill.Code) is int flames and > 0) AddThought(c, "passion" + flames, 0);
    }

    /// <summary>A courier set down a load: Hauling for each stack, Strength when the pack was full.</summary>
    public void GainHaulXp(Colony colony, CitizenRecord c, int stacks, bool wasFull)
    {
        GainWorkXp(colony, c, Jobs.Courier, stacks);
        if (wasFull) AddSkillXp(colony, c, Strength, StrengthPerFullLoad);
    }

    // ---- Caps from building level ---------------------------------------------------------------

    /// <summary>The highest level a building of this level lets a settler use, from SkillCapByBuildingLevel (level 0 counts as 1).</summary>
    public int CapForBuildingLevel(int buildingLevel)
    {
        var caps = Config.SkillCapByBuildingLevel;
        if (caps == null || caps.Length == 0) return MaxLevel;
        int i = Math.Clamp(buildingLevel - 1, 0, caps.Length - 1);
        return Math.Clamp(caps[i], 0, MaxLevel);
    }

    /// <summary>The building that limits a settler's skill: their hut for a job skill, their Residence for Strength and Fitness.</summary>
    public Building? CapBuilding(Colony colony, CitizenRecord c, SkillDef skill)
    {
        if (skill.IsPassive) return c.HomeBedPos == null ? null : ResidenceAt(colony, c.HomeBedPos);
        if (skill.Covers(c.JobCode) && c.WorkplacePos != null) return GetBuilding(colony, c.WorkplacePos);
        // Not working at it: the best hut of that kind in the colony says what they could use there.
        return colony.Buildings.Where(b => skill.Covers(JobOf(b.Type))).OrderByDescending(b => b.Level).FirstOrDefault();
    }

    /// <summary>The highest usable level a building allows: by its level, or everything at the top level its style has.</summary>
    public int CapForBuilding(Building? b)
    {
        if (b == null) return CapForBuildingLevel(0);
        int top = Blueprints.MaxLevel(b);
        if (top > 0 && b.Level >= top) return MaxLevel;
        return CapForBuildingLevel(b.Level);
    }

    /// <summary>How much of a skill the settler can use where they work or live now.</summary>
    public int SkillCap(Colony colony, CitizenRecord c, SkillDef skill) => CapForBuilding(CapBuilding(colony, c, skill));

    /// <summary>The level that counts: the lower of the real level and what the building allows.</summary>
    public int UsableLevel(Colony colony, CitizenRecord c, string code)
    {
        if (!Config.Skills || c.IsChild || IsIncapable(c, code)) return 0;
        var skill = Skill(code);
        if (skill == null) return 0;
        return Math.Min(SkillLevel(c, code), SkillCap(colony, c, skill));
    }

    // ---- Effects --------------------------------------------------------------------------------

    /// <summary>Clamps a multiplier so stacked skills can't go past SkillEffectMax (or below its inverse).</summary>
    float ClampEffect(float f) => Math.Clamp(f, 1f / Math.Max(1.01f, Config.SkillEffectMax), Math.Max(1.01f, Config.SkillEffectMax));

    /// <summary>Multiply a job's work time by this: 1 at level 0, smaller as the job's skill grows.</summary>
    public float WorkTimeFactor(Colony colony, CitizenRecord c)
    {
        var skill = SkillForJob(Jobs.WorkJob(c));
        float speed = Config.Skills && skill != null ? 1f + skill.WorkSpeedPerLevel * UsableLevel(colony, c, skill.Code) : 1f;
        speed *= TraitMult(c, "workSpeed"); // Hard Worker, Lazy, the job traits, Shaken
        speed *= SkillPerk(colony, c, "workSpeed"); // Prospecting's faster readings
        speed *= MoodWorkFactor(c); // Joyful works faster, Miserable slower
        speed *= BailiffWorkFactor(colony, c); // a bailiff on duty keeps everyone at it
        if (Jobs.IsCrafter(c.JobCode)) speed *= ResearchMult(colony, "crafterSpeed"); // Specialized Craftsmen
        return ClampEffect(1f / speed);
    }

    /// <summary>Divide a guard's time between swings by this: Veteran swings faster, Night Owl at night.</summary>
    public float SwingFactor(Colony colony, CitizenRecord c)
    {
        float f = TraitMult(c, "swingSpeed") * SkillPerk(colony, c, "swingSpeed");
        float hour = sapi.World.Calendar.HourOfDay;
        if (!InHours(hour, Config.SleepEndHour, Config.SleepStartHour)) f *= TraitMult(c, "nightSwing");
        return ClampEffect(f);
    }

    /// <summary>Multiply a guard's damage by this: Fighting and Strength each add 3% a level.</summary>
    public float DamageFactor(Colony colony, CitizenRecord c) =>
        ClampEffect((1f + 0.03f * UsableLevel(colony, c, "fighting") + 0.03f * UsableLevel(colony, c, Strength))
            * (c.JobCode == Jobs.Guard ? ResearchMult(colony, "guardDamage") : 1f)); // Guard Training Regimen

    /// <summary>Multiply a settler's walking speed by this: Fitness adds 1.5% a level, Hauling 4% a level while a courier carries a load.</summary>
    public float WalkFactor(Colony colony, CitizenRecord c, bool carrying)
    {
        float f = 1f + 0.015f * UsableLevel(colony, c, Fitness);
        if (carrying && c.JobCode == Jobs.Courier) f += 0.04f * UsableLevel(colony, c, "hauling");
        if (carrying && c.JobCode == Jobs.Courier) f *= ResearchMult(colony, "courierWalk"); // Organized Storage
        if (c.JobCode == Jobs.Explorer) f *= ResearchMult(colony, "explorerWalk"); // Cartographer's Guild
        f *= TraitMult(c, "walkSpeed"); // Fleet-footed, Sluggish
        return ClampEffect(f);
    }

    /// <summary>Extra max health from Fitness: one point for every two usable levels.</summary>
    public float HealthBonus(Colony colony, CitizenRecord c) => UsableLevel(colony, c, Fitness) / 2 + (float)Math.Round(TraitAdd(c, "maxHealth")) // plus Tough, minus Frail
        + (c.JobCode == Jobs.Guard ? ResearchAdd(colony, "guardHealth") : 0f); // Basic Fortifications

    /// <summary>Sets a settler's max health from their Fitness; the game's health behavior adds it to the base.</summary>
    public void ApplyHealthBonus(Colony colony, CitizenRecord c, Entity entity) =>
        entity.GetBehavior<EntityBehaviorHealth>()?.SetMaxHealthModifiers("createacolony-fitness", HealthBonus(colony, c));

    // ---- Starting skills and inheritance --------------------------------------------------------

    /// <summary>
    /// Starting skills for someone from outside: 0 to 2 in everything and one background skill at 2 to 4
    /// ("was a farmer before"). The dictionary goes on a CitizenRecord or a Tavern offer.
    /// </summary>
    public Dictionary<string, int> RollStartingSkills()
    {
        var result = new Dictionary<string, int>();
        var all = Skills();
        if (all.Count == 0) return result;
        var rand = sapi.World.Rand;
        foreach (var s in all)
        {
            int level = rand.Next(3);
            if (level > 0) result[s.Code] = XpForLevel(level);
        }
        var background = all[rand.Next(all.Count)];
        result[background.Code] = XpForLevel(rand.Next(2, 5));
        return result;
    }

    /// <summary>A grown-up child starts each skill at a third of their parents' average level.</summary>
    public void InheritSkills(Colony colony, CitizenRecord child)
    {
        var parents = ParentsOf(colony, child).ToList();
        if (parents.Count == 0) return;
        foreach (var s in Skills())
        {
            int average = (int)Math.Round(parents.Average(p => SkillLevel(p, s.Code)));
            int level = average / 3;
            if (level > 0) child.SkillXp[s.Code] = Math.Max(SkillXp(child, s.Code), XpForLevel(level));
        }
    }

    // ---- Passions ------------------------------------------------------------------------------

    /// <summary>A settler's passion for a skill: 0 none, 1 Interested (one flame), 2 Burning (two flames).</summary>
    public int Passion(CitizenRecord c, string code) => Config.SkillPassions ? Math.Clamp(c.Passions.GetValueOrDefault(code), 0, 2) : 0;

    /// <summary>How much faster a skill grows for the settler's passion: SkillPassionXp by flames.</summary>
    public float PassionXpFactor(CitizenRecord c, string code)
    {
        var table = Config.SkillPassionXp;
        if (table == null || table.Length == 0) return 1f;
        return Math.Max(0, table[Math.Min(Passion(c, code), table.Length - 1)]);
    }

    /// <summary>
    /// Passions for someone from outside: 0 to 3 rolled ones (a quarter Burning), more likely on their best
    /// (background) skill, never on a skill a trait rules out. A job trait gives its skill a flame, or turns
    /// one flame into two.
    /// </summary>
    public Dictionary<string, int> RollPassions(Dictionary<string, int> skillXp, List<TraitRecord> traits)
    {
        var result = new Dictionary<string, int>();
        var all = Skills().Where(s => !IncapableBy(traits, s.Code).Any()).ToList();
        if (!Config.SkillPassions || all.Count == 0) return result;
        var rand = sapi.World.Rand;
        var weights = Config.SkillPassionCountWeights is { Length: > 0 } w ? w : new[] { 1 };
        int count = Weighted(rand, Enumerable.Range(0, weights.Length).ToList(), i => weights[i]);
        // The best skill they came with counts three times in the draw: what they did before, they liked.
        string best = skillXp.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault() ?? "";
        for (int i = 0; i < count; i++)
        {
            var left = all.Where(s => !result.ContainsKey(s.Code)).ToList();
            if (left.Count == 0) break;
            var pick = Weighted(rand, left, s => s.Code == best ? 3 : 1);
            result[pick.Code] = rand.NextDouble() < Config.SkillPassionBurningChance ? 2 : 1;
        }
        ApplyTraitPassions(result, traits);
        return result;
    }

    /// <summary>A job trait means they love that work: at least one flame, or two if they already had one.</summary>
    public void ApplyTraitPassions(Dictionary<string, int> passions, List<TraitRecord> traits)
    {
        foreach (var record in traits)
        {
            if (Trait(record.Code) is not { IsJobTrait: true } def || SkillForJob(def.Job) is not { } skill) continue;
            passions[skill.Code] = Math.Min(2, passions.GetValueOrDefault(skill.Code) + 1);
        }
    }

    /// <summary>A grown-up child takes each parent's passion SkillPassionInherit of the time, plus one rolled passion and its job trait's.</summary>
    public void InheritPassions(Colony colony, CitizenRecord child)
    {
        if (!Config.SkillPassions) return;
        var rand = sapi.World.Rand;
        var result = new Dictionary<string, int>();
        foreach (var parent in ParentsOf(colony, child))
        {
            foreach (var (code, flames) in parent.Passions)
            {
                if (IncapableBy(child.Traits, code).Any() || rand.NextDouble() >= Config.SkillPassionInherit) continue;
                result[code] = Math.Max(result.GetValueOrDefault(code), flames);
            }
        }
        var free = Skills().Where(s => !result.ContainsKey(s.Code) && !IncapableBy(child.Traits, s.Code).Any()).ToList();
        if (free.Count > 0) result[free[rand.Next(free.Count)].Code] = rand.NextDouble() < Config.SkillPassionBurningChance ? 2 : 1;
        ApplyTraitPassions(result, child.Traits);
        child.Passions = result;
        child.PassionsRolled = true;
    }

    /// <summary>Settlers from saves before passions roll theirs once; children get theirs when they grow up.</summary>
    void EnsurePassions(Colony colony)
    {
        if (!Config.SkillPassions) return;
        bool changed = false;
        foreach (var c in colony.Citizens)
        {
            if (c.PassionsRolled || c.IsChild) continue;
            c.PassionsRolled = true;
            if (c.Alive && c.Passions.Count == 0) c.Passions = RollPassions(c.SkillXp, c.Traits);
            changed = true;
        }
        if (changed) MarkDirty(colony);
    }

    // ---- Incapable ------------------------------------------------------------------------------

    /// <summary>The traits in this list that rule a skill out (trait effect "incapable-&lt;skill&gt;").</summary>
    public IEnumerable<string> IncapableBy(List<TraitRecord> traits, string code)
    {
        if (!Config.SkillIncapable || !Config.Traits) yield break;
        foreach (var record in traits)
        {
            if (Trait(record.Code) is { } def && def.Effects.ContainsKey("incapable-" + code)) yield return def.Code;
        }
    }

    public bool IsIncapable(CitizenRecord c, string code) => IncapableBy(c.Traits, code).Any();

    /// <summary>The trait that keeps a settler from a job, or null when they can do it.</summary>
    public string? IncapableOfJob(CitizenRecord c, string? job) =>
        SkillForJob(job) is { } skill ? IncapableBy(c.Traits, skill.Code).FirstOrDefault() : null;

    // ---- Decay ----------------------------------------------------------------------------------

    /// <summary>XP a skill at this level loses a day: nothing up to SkillSteepFrom, then SkillDecayPerDay.</summary>
    public int DecayPerDay(int level)
    {
        int from = Math.Max(0, Config.SkillSteepFrom);
        var table = Config.SkillDecayPerDay;
        if (!Config.SkillDecay || level <= from || table == null || table.Length == 0) return 0;
        return Math.Max(0, table[Math.Min(level - from - 1, table.Length - 1)]);
    }

    /// <summary>
    /// Part of the colony tick, once per in-game day: skills above SkillSteepFrom lose their day's XP
    /// (halved by Good Memory), never dropping below SkillSteepFrom.
    /// </summary>
    void TickSkills(Colony colony, double now)
    {
        if (!Config.Skills) return;
        int today = (int)now;
        if (colony.LastSkillDay == today) return;
        bool first = colony.LastSkillDay < 0;
        colony.LastSkillDay = today;
        MarkDirty(colony);
        if (first || !Config.SkillDecay) return;
        int floor = XpForLevel(Math.Min(MaxLevel, Math.Max(0, Config.SkillSteepFrom)));
        foreach (var c in colony.Citizens)
        {
            if (!c.Alive || c.Visitor || c.IsChild) continue;
            foreach (var code in c.SkillXp.Keys.ToList())
            {
                int loss = (int)Math.Round(DecayPerDay(SkillLevel(c, code)) * TraitMult(c, "decay"));
                if (loss > 0) c.SkillXp[code] = Math.Max(floor, c.SkillXp[code] - loss);
            }
        }
    }

    // ---- Milestone perks ------------------------------------------------------------------------

    static readonly HashSet<string> AddingPerks = new() { "yield", "portion", "packSlots" };

    /// <summary>
    /// A milestone perk from every skill the settler uses: their job's skill and Strength/Fitness. A skill
    /// gives the value of the highest SkillPerkLevels milestone its usable level has reached. Adding keys
    /// sum (0 when none), the rest multiply (1 when none).
    /// </summary>
    public float SkillPerk(Colony colony, CitizenRecord c, string key)
    {
        bool adds = AddingPerks.Contains(key);
        float result = adds ? 0f : 1f;
        if (!Config.Skills || c.IsChild) return result;
        var levels = Config.SkillPerkLevels ?? Array.Empty<int>();
        foreach (var skill in Skills())
        {
            if (!skill.Perks.TryGetValue(key, out var values) || values.Length == 0) continue;
            if (!skill.IsPassive && !skill.Covers(c.JobCode)) continue;
            int usable = UsableLevel(colony, c, skill.Code);
            int reached = levels.Count(l => usable >= l);
            if (reached == 0) continue;
            float v = values[Math.Min(reached, values.Length) - 1];
            result = adds ? result + v : result * v;
        }
        return result;
    }

    /// <summary>The colony a settler belongs to, for callers that only have the record.</summary>
    public Colony? ColonyOf(CitizenRecord c) => colonies.Values.FirstOrDefault(col => col.Citizens.Contains(c));

    /// <summary>Multiply hunger by this: the Fitness perk.</summary>
    public float HungerPerkFor(string? colonyId, CitizenRecord c) => GetById(colonyId ?? "") is { } colony ? SkillPerk(colony, c, "hunger") : 1f;

    /// <summary>The next milestone level above a usable level, or 0 past the last.</summary>
    public int NextPerkLevel(int usable) => (Config.SkillPerkLevels ?? Array.Empty<int>()).Where(l => l > usable).DefaultIfEmpty(0).Min();

    /// <summary>A chance perk ("yield", "portion") rolled once: true when the settler gets one more.</summary>
    public bool RollPerk(Colony colony, CitizenRecord c, string key)
    {
        float chance = SkillPerk(colony, c, key);
        return chance > 0 && sapi.World.Rand.NextDouble() < chance;
    }

    /// <summary>Settlers from saves before skills existed roll their starting skills once.</summary>
    void EnsureSkills(Colony colony)
    {
        if (!Config.Skills) return;
        bool changed = false;
        foreach (var c in colony.Citizens)
        {
            if (c.SkillsRolled) continue;
            c.SkillsRolled = true;
            if (c.Alive && !c.IsChild && c.SkillXp.Count == 0) c.SkillXp = RollStartingSkills();
            changed = true;
        }
        if (changed) MarkDirty(colony);
    }

    // ---- For the windows ------------------------------------------------------------------------

    /// <summary>Every skill of a settler with level, usable level and what limits it, the job's own skill first.</summary>
    public List<SkillInfo> SkillInfos(Colony colony, CitizenRecord c)
    {
        var list = new List<SkillInfo>();
        foreach (var s in Skills())
        {
            int xp = SkillXp(c, s.Code);
            int level = LevelOf(xp);
            int cap = SkillCap(colony, c, s);
            int usable = c.IsChild ? 0 : Math.Min(level, cap);
            string note = "";
            if (level > usable && CapBuilding(colony, c, s) is { } limit) note = BuildingName(limit);
            else if (level > usable) note = s.IsPassive ? "@skill-cap-no-home" : "@skill-cap-no-hut";
            int next = level >= MaxLevel ? 0 : XpForLevel(level + 1);
            string incapable = IncapableBy(c.Traits, s.Code).FirstOrDefault() ?? "";
            if (incapable.Length > 0) usable = 0;
            int today = c.SkillXpDay == (int)sapi.World.Calendar.TotalDays ? c.SkillXpToday.GetValueOrDefault(s.Code) : 0;
            list.Add(new SkillInfo(s.Code, level, usable, xp, XpForLevel(level), next, incapable.Length > 0 ? "" : note, Passion(c, s.Code), incapable, DecayPerDay(level) > 0, today));
        }
        var own = SkillForJob(Jobs.WorkJob(c));
        return own == null ? list : list.OrderByDescending(i => i.Code == own.Code).ToList();
    }

    /// <summary>The two best skills someone has, for the hiring lists: "Farming 3, Strength 2", passions first among equals.</summary>
    public List<(string Code, int Level, int Passion)> BestSkills(Dictionary<string, int> xp, Dictionary<string, int>? passions = null, int count = 2) =>
        xp.Select(kv => (kv.Key, LevelOf(kv.Value), Config.SkillPassions ? passions?.GetValueOrDefault(kv.Key) ?? 0 : 0)).Where(x => x.Item2 > 0)
            .OrderByDescending(x => x.Item2).ThenByDescending(x => x.Item3).ThenBy(x => x.Key).Take(count).ToList();

    /// <summary>The mark after a skill for its passion in lists: lang passion-mark-1 / -2, empty for none.</summary>
    public string PassionMark(int passion) => passion > 0 && Config.SkillPassions ? " " + Lang.Get("createacolony:passion-mark-" + Math.Min(2, passion)) : "";

    /// <summary>The level name: "Journeyman", "Legend of the realm".</summary>
    public static string LevelName(int level) => Lang.Get("createacolony:skill-rank-" + Math.Clamp(level, 0, 20));

    /// <summary>"Next: extra yield 10% at level 10" for a skill's hover, or empty past its last perk.</summary>
    public string NextPerkText(string code, int usable)
    {
        if (Skill(code) is not { } skill || skill.Perks.Count == 0) return "";
        var levels = Config.SkillPerkLevels ?? Array.Empty<int>();
        int next = NextPerkLevel(usable);
        int index = Array.IndexOf(levels, next);
        if (next <= 0 || index < 0) return "";
        var parts = new List<string>();
        foreach (var (key, values) in skill.Perks)
        {
            if (values.Length == 0) continue;
            float v = values[Math.Min(index, values.Length - 1)];
            if (AddingPerks.Contains(key) && v <= 0) continue;
            string amount = key == "packSlots" ? "+" + (int)Math.Round(v)
                : AddingPerks.Contains(key) ? (int)Math.Round(v * 100) + "%"
                : (v >= 1 ? "+" : "") + (int)Math.Round((v - 1) * 100) + "%";
            parts.Add(Lang.Get("createacolony:perk-" + key, amount));
        }
        return parts.Count == 0 ? "" : Lang.Get("createacolony:perk-next", string.Join(", ", parts), next);
    }
}
