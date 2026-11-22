using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Config;

namespace CreateAColony.Colonies;

/// <summary>One line of a settler's mood for the windows: the thought, its points and hours left (-1 while it lasts).</summary>
public readonly record struct ThoughtInfo(string Code, int Mood, int HoursLeft);

/// <summary>
/// Happiness, RimWorld style: every settler has a mood from 0 to 100. Thoughts add to or take from 50:
/// some come from how things stand (a bed, a partner, hunger), others last a while after something
/// happened (a good meal, working at a passion, a death in the family). Mood drifts towards that sum by
/// HappinessPerHour, so one bad meal does not swing it but a bad week does. A Joyful settler works and
/// learns faster, a Miserable one slower, and one at HappinessBreakAt or below downs tools for a few hours.
/// </summary>
public partial class ColonyManager
{
    public const float MoodNeutral = 50f;

    /// <summary>Thoughts that last a while: mood points and in-game hours. "passion1/2" are refreshed while the settler works.</summary>
    static readonly Dictionary<string, (int Mood, double Hours)> TimedThoughts = new()
    {
        ["passion1"] = (8, 4),
        ["passion2"] = (14, 4),
        ["meal"] = (4, 12),
        ["skillup"] = (3, 24),
        ["raidwon"] = (5, 48),
        ["parent"] = (12, 72),
        ["knockedout"] = (-12, 48),
        ["death"] = (-6, 72),
        ["familydeath"] = (-20, 144),
        ["catharsis"] = (20, 24),
        ["decorated"] = (5, 48),
        ["payday"] = (3, 24),
        ["newhome"] = (8, 48),
        // The church (ColonyManager.Church.cs): a service (more when the bishop leads it), a missed one, a funeral.
        ["service"] = (8, 24),
        ["missedservice"] = (-4, 48),
        ["funeral"] = (4, 24),
        ["alms"] = (5, 24),
        // Safety, health (ColonyManager.Wellbeing.cs).
        ["attacked"] = (-6, 24),
        ["sawattack"] = (-3, 12),
        ["sick"] = (-8, 24),
        ["monsterkill"] = (-8, 96),
        ["monsterslain"] = (3, 24),
        ["treated"] = (4, 24),
        // Research (ColonyManager.Research.cs): a failed experiment, a lucky find.
        ["researchfail"] = (-2, 24),
        ["researchlucky"] = (4, 24),
        // The Graveyard (ColonyManager.Graveyard.cs): a body seen, bodies left unburied, the undertaker's small funeral.
        ["sawbody"] = (-3, 6),
        ["unburied"] = (-3, 6),
        ["unburiedfamily"] = (-8, 6),
        ["smallfuneral"] = (2, 24),
        // Spending (ColonyManager.Market.cs): bought something at the Market, or wanted to and could not afford it.
        ["bought"] = (2, 24),
        ["cantafford"] = (-1, 12),
        // The Tavern (ColonyManager.Tavern.cs): a drink with friends, a chat with no drink, a hangover.
        ["drink"] = (3, 12),
        ["chat"] = (1, 6),
        ["hangover"] = (-2, 12)
    };

    /// <summary>Mood bands from the top: the lowest happiness each starts at, and the word for it.</summary>
    static readonly (float From, string Code)[] MoodBands =
    {
        (85, "joyful"), (70, "happy"), (45, "content"), (30, "unhappy"), (15, "miserable"), (0, "broken")
    };

    /// <summary>A settler's happiness, 50 until it has been set.</summary>
    public float HappinessOf(CitizenRecord c) => c.Happiness < 0 ? MoodNeutral : c.Happiness;

    /// <summary>The mood word for a happiness: "joyful", "content", "broken"...</summary>
    public string MoodOf(float happiness)
    {
        foreach (var (from, code) in MoodBands) if (happiness >= from) return code;
        return "broken";
    }

    bool HasMood(CitizenRecord c) => Config.Happiness && c.Alive && !c.IsChild && !c.Visitor;

    /// <summary>Multiply work speed by this: 10% faster when Joyful, 5% when Happy, 5% slower Unhappy, 15% Miserable.</summary>
    public float MoodWorkFactor(CitizenRecord c)
    {
        if (!HasMood(c)) return 1f;
        return MoodOf(HappinessOf(c)) switch
        {
            "joyful" => 1.10f,
            "happy" => 1.05f,
            "unhappy" => 0.95f,
            "miserable" or "broken" => 0.85f,
            _ => 1f
        };
    }

    /// <summary>Multiply skill XP by this: 20% more when Joyful, 10% Happy, 25% less Miserable.</summary>
    public float MoodLearnFactor(CitizenRecord c)
    {
        if (!HasMood(c)) return 1f;
        return MoodOf(HappinessOf(c)) switch
        {
            "joyful" => 1.2f,
            "happy" => 1.1f,
            "miserable" or "broken" => 0.75f,
            _ => 1f
        };
    }

    /// <summary>True while the settler is too unhappy to work and sulks instead.</summary>
    public bool IsBrokenDown(CitizenRecord c) => Config.Happiness && c.BreakdownUntilTotalDays > sapi.World.Calendar.TotalDays;

    /// <summary>
    /// Gives a settler a lasting thought, replacing one of the same code. Mood 0 takes the thought's usual
    /// points from TimedThoughts; picky eaters feel meals more, Iron Stomachs less (trait "variety").
    /// </summary>
    public void AddThought(CitizenRecord c, string code, int mood = 0)
    {
        if (!HasMood(c) || !TimedThoughts.TryGetValue(code, out var def)) return;
        if (mood == 0) mood = def.Mood;
        if (code == "meal") mood = (int)Math.Round(mood * TraitMult(c, "variety"));
        c.Thoughts.RemoveAll(t => t.Code == code);
        c.Thoughts.Add(new ThoughtRecord { Code = code, Mood = mood, UntilTotalDays = sapi.World.Calendar.TotalDays + def.Hours / 24.0 });
    }

    /// <summary>Every thought a settler has now: lasting ones, then how things stand. Hours left is -1 for the standing ones.</summary>
    public List<ThoughtInfo> ThoughtsOf(Colony colony, CitizenRecord c)
    {
        var list = new List<ThoughtInfo>();
        if (!HasMood(c)) return list;
        double now = sapi.World.Calendar.TotalDays;
        foreach (var t in c.Thoughts.Where(t => t.UntilTotalDays > now))
        {
            list.Add(new ThoughtInfo(t.Code, t.Mood, (int)Math.Ceiling((t.UntilTotalDays - now) * sapi.World.Calendar.HoursPerDay)));
        }

        float fussy = TraitMult(c, "variety");
        void Standing(string code, int mood) { if (mood != 0) list.Add(new ThoughtInfo(code, mood, -1)); }

        float full = c.Saturation < 0 ? 1f : c.Saturation / Math.Max(1f, Config.SaturationMax);
        if (full <= 0) Standing("starving", -20);
        else if (full < 0.25f) Standing("hungry", (int)Math.Round(-8 * fussy));
        // A varied diet: three or more food groups among the last few meals.
        if (c.RecentFood.Skip(Math.Max(0, c.RecentFood.Count - 6)).Distinct().Count() >= 3) Standing("variety", (int)Math.Round(6 * fussy));

        if (c.HomeBedPos == null) Standing("nobed", -10);
        else if (ResidenceAt(colony, c.HomeBedPos) is { } home)
        {
            if (home.Type == BuildingTypes.BoardingHouse) Standing("boarding", BoardingMood(home));
            else Standing("bed", 3 + Math.Max(0, home.Level - 1));
        }
        if (c.PartnerId != 0 && colony.Citizens.Find(p => p.CitizenId == c.PartnerId) is { Alive: true }) Standing("partner", 6);
        Standing("townhall", TownHallLevel(colony));
        if (c.JobCode == null) Standing("nojob", -4);
        Standing("bailiff", BailiffMoodFor(colony, c));
        // Taxes and unpaid wages (ColonyManager.Economy.cs).
        MoneyThoughts(colony, c, Standing);
        ChurchThoughts(colony, c, Standing);
        // In rags, cold, wearing jewellery (ColonyManager.Clothing.cs).
        ClothesThoughts(c, Standing);
        // Safety, health and crowding (ColonyManager.Wellbeing.cs).
        SafetyThoughts(colony, Standing);
        HealthThoughts(c, Standing);
        CrowdingThoughts(colony, c, Standing);
        // Sanguine and Melancholic.
        Standing("temperament", (int)Math.Round(TraitAdd(c, "mood")));
        return list;
    }

    /// <summary>The happiness a settler drifts towards: 50 plus every thought, between 0 and 100.</summary>
    public float MoodTarget(Colony colony, CitizenRecord c) => Math.Clamp(MoodNeutral + ThoughtsOf(colony, c).Sum(t => t.Mood), 0f, 100f);

    /// <summary>
    /// Part of the colony tick: moods move towards their targets for the hours since the last tick, old
    /// thoughts fade, and a settler at HappinessBreakAt or below breaks down for a few hours (then feels
    /// relief, the catharsis thought, so it does not happen again at once).
    /// </summary>
    void TickHappiness(Colony colony)
    {
        if (!Config.Happiness) return;
        double nowHours = sapi.World.Calendar.TotalHours;
        double now = sapi.World.Calendar.TotalDays;
        if (colony.LastMoodTotalHours <= 0 || nowHours < colony.LastMoodTotalHours)
        {
            colony.LastMoodTotalHours = nowHours;
            return;
        }
        // While the colony was unloaded nothing happened to them; catch up a day at most.
        double hours = Math.Min(24, nowHours - colony.LastMoodTotalHours);
        if (hours < 0.1) return;
        colony.LastMoodTotalHours = nowHours;
        WearClothes(colony, hours);
        CheckDanger(colony);

        foreach (var c in colony.Citizens)
        {
            if (!HasMood(c)) continue;
            c.Thoughts.RemoveAll(t => t.UntilTotalDays <= now);
            if (c.IsDowned) continue;
            float mood = HappinessOf(c);
            float target = MoodTarget(colony, c);
            float step = (float)(Math.Max(0, Config.HappinessPerHour) * hours);
            c.Happiness = mood < target ? Math.Min(target, mood + step) : Math.Max(target, mood - step);

            if (c.Happiness <= Config.HappinessBreakAt && c.BreakdownUntilTotalDays <= now)
            {
                double min = Math.Max(0, Config.HappinessBreakHoursMin);
                double max = Math.Max(min, Config.HappinessBreakHoursMax);
                c.BreakdownUntilTotalDays = now + (min + sapi.World.Rand.NextDouble() * (max - min)) / 24.0;
                AddThought(c, "catharsis");
                LogEvent(colony, "event-breakdown", c.Name);
                TellMembers(colony, "createacolony:mood-breakdown", c.Name);
            }
        }
        MarkDirty(colony);
    }

    /// <summary>A settler died: everyone is sad, family members far more.</summary>
    public void MournDeath(Colony colony, CitizenRecord dead)
    {
        foreach (var c in colony.Citizens)
        {
            if (c == dead || !HasMood(c)) continue;
            bool family = c.PartnerId == dead.CitizenId || dead.PartnerId == c.CitizenId
                || c.ParentIds.Contains(dead.CitizenId) || dead.ParentIds.Contains(c.CitizenId)
                || (c.ParentIds.Count > 0 && c.ParentIds.Intersect(dead.ParentIds).Any());
            AddThought(c, family ? "familydeath" : "death");
        }
        MarkDirty(colony);
    }

    /// <summary>True for food that was cooked: pot meals, pies, bread and anything roasted or cooked.</summary>
    public static bool IsCookedFood(Vintagestory.API.Common.ItemStack? stack)
    {
        if (stack == null) return false;
        if (stack.Block is Vintagestory.GameContent.IBlockMealContainer or Vintagestory.GameContent.BlockPie) return true;
        string path = stack.Collectible.Code?.Path ?? "";
        if (path.Contains("charred") || path.Contains("burnt")) return false;
        return path.Contains("cooked") || path.Contains("bread") || path.Contains("roasted") || path.Contains("baked");
    }

    /// <summary>The colony's average happiness, for the Town Hall; -1 when nobody has a mood.</summary>
    public int AverageHappiness(Colony colony)
    {
        var moods = colony.Citizens.Where(HasMood).Select(HappinessOf).ToList();
        return moods.Count == 0 ? -1 : (int)Math.Round(moods.Average());
    }

    /// <summary>"Hungry -8" style hover lines for the settler window.</summary>
    public string ThoughtText(ThoughtInfo t)
    {
        string points = (t.Mood >= 0 ? "+" : "") + t.Mood;
        string name = Lang.Get("createacolony:thought-" + t.Code);
        return t.HoursLeft >= 0 ? Lang.Get("createacolony:thought-line-timed", name, points, t.HoursLeft) : Lang.Get("createacolony:thought-line", name, points);
    }
}
