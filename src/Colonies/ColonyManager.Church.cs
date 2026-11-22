using System;
using System.Collections.Generic;
using System.Linq;
using CreateAColony.Blocks;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using ItemMatch = System.Func<Vintagestory.API.Common.ItemStack, bool>;

namespace CreateAColony.Colonies;

/// <summary>
/// The Church and religion (Aaron, 0.137.0). Every settler has a Faith from 0 to 100. A Church's clergy (job
/// Priest; one of them may be the colony's Bishop once a Church is level ChurchBishopLevel) hold a service every
/// ChurchServiceEveryDays days: settlers with enough Faith walk to the Church (AiTaskSettlerWorship), and
/// afterwards they are happier, a little more faithful and learn faster for a day, with a small chance of a
/// level in a random skill. Devout settlers who miss it are unhappy. The clergy hold funerals for the newly dead
/// (grief fades faster), write the colony's events into chronicle books and hang the church's paintings
/// (AiTaskSettlerPriest, AiTaskSettlerDecorate).
/// Settlers give the church a tithe of their wages (Colony.TithePercent, from ColonyManager.Economy.cs's
/// OnWagePaid) as coins in its storage, and the church spends them on charity: food for the hungry when the
/// colony has none, bandages for its Healer's Station, care for orphans, and beds for the homeless (the
/// poorhouse: beds inside a Church are given out last).
/// </summary>
public partial class ColonyManager
{
    /// <summary>A Church of this level takes a bishop (one per colony), and of the next level a second priest.</summary>
    public const int ChurchBishopLevel = 3;
    public const int ChurchSecondPriestLevel = 4;
    /// <summary>Days of the church's ledger kept.</summary>
    public const int ChurchLedgerDays = 30;
    public const string Theology = "theology";

    /// <summary>Who came to today's service, by colony: settlers and clergy (citizen ids). Not saved: a reload mid-service starts it over.</summary>
    readonly Dictionary<string, (int Day, HashSet<int> Came, HashSet<int> Clergy)> serviceAttendance = new();
    /// <summary>When each colony's church next looks for hungry settlers and an empty Healer's Station (elapsed ms).</summary>
    readonly Dictionary<string, long> nextCharityMs = new();
    /// <summary>The dead whose funeral a priest is on its way to, so two priests do not both go.</summary>
    readonly HashSet<int> funeralsUnderway = new();
    const long CharityCheckMs = 60_000;

    // ---- Churches and clergy ----------------------------------------------------------------

    /// <summary>How many clergy a Church takes: a priest, a bishop from level 3, a second priest from level 4.</summary>
    public static int ChurchSlots(Building church) =>
        1 + (church.Level >= ChurchBishopLevel ? 1 : 0) + (church.Level >= ChurchSecondPriestLevel ? 1 : 0);

    IEnumerable<Building> Churches(Colony colony) => colony.Buildings.Where(b => b.Type == BuildingTypes.Church && !b.Removing);

    /// <summary>
    /// The colony has a Church. Without one there is no religion at all (Aaron, 0.154.0): no services or missed
    /// services, no tithe, no charity, no church thoughts or learning boost, and Faith is not shown.
    /// </summary>
    public bool HasChurch(Colony colony) => Churches(colony).Any();

    /// <summary>Thoughts that only a Church gives.</summary>
    static readonly string[] ChurchThoughtCodes = { "service", "missedservice", "funeral", "alms" };

    /// <summary>With no Church, takes away what one left behind: its thoughts, the learning boost and today's service.</summary>
    void ForgetChurch(Colony colony)
    {
        serviceAttendance.Remove(colony.ColonyId);
        foreach (var c in colony.Citizens)
        {
            caredFor.Remove(c.CitizenId);
            if (c.Thoughts.RemoveAll(t => ChurchThoughtCodes.Contains(t.Code)) > 0 || c.LearnBoostUntilTotalDays > 0)
            {
                c.LearnBoostUntilTotalDays = 0;
                MarkDirty(colony);
            }
        }
    }

    public static bool IsBishop(CitizenRecord c) => c.JobCode == Jobs.Priest && c.WorkRole == ChurchRoles.Bishop;

    /// <summary>The job name to show for a settler: "bishop" for the bishop, else its job.</summary>
    public static string JobLabel(CitizenRecord c) => IsBishop(c) ? "bishop" : c.JobCode ?? "";

    /// <summary>Clergy who can work now: alive, awake, not sulking, at a Church that stands.</summary>
    public List<CitizenRecord> ActiveClergy(Colony colony) => colony.Citizens
        .Where(c => c.Alive && !c.IsDowned && c.JobCode == Jobs.Priest && !IsBrokenDown(c) && ChurchOf(colony, c) != null).ToList();

    /// <summary>The Church a member of the clergy works at, or null.</summary>
    public Building? ChurchOf(Colony colony, CitizenRecord clergy) =>
        clergy.WorkplacePos != null && GetBuilding(colony, clergy.WorkplacePos) is { Type: BuildingTypes.Church, Removing: false } church ? church : null;

    /// <summary>A Church that has clergy, the one with most first; null when no Church has any.</summary>
    public Building? ServingChurch(Colony colony) => Churches(colony)
        .Select(b => (B: b, N: WorkersAt(colony, b).Count(c => !c.IsDowned))).Where(x => x.N > 0)
        .OrderByDescending(x => x.N).ThenBy(x => x.B.BuildingId).Select(x => x.B).FirstOrDefault();

    /// <summary>Makes a member of a Church's clergy its priest or the colony's bishop. Returns a lang key on failure.</summary>
    public string? SetChurchRole(Colony colony, Building church, CitizenRecord worker, int role)
    {
        if (role is not (ChurchRoles.Priest or ChurchRoles.Bishop)) return "createacolony:bad-building";
        if (!WorkersAt(colony, church).Contains(worker)) return "createacolony:bad-citizen";
        if (worker.WorkRole == role) return null;
        if (role == ChurchRoles.Bishop)
        {
            if (church.Level < ChurchBishopLevel) return "createacolony:church-bishop-level";
            if (colony.Citizens.Any(c => c != worker && c.Alive && IsBishop(c))) return "createacolony:church-one-bishop";
        }
        worker.WorkRole = role;
        LogEvent(colony, role == ChurchRoles.Bishop ? "event-church-bishop" : "event-church-priest", worker.Name);
        MarkDirty(colony);
        return null;
    }

    /// <summary>A bishop whose Church is too small, or a second one, goes back to being a priest; so does anyone else with a stray work role.</summary>
    void FixChurchRoles(Colony colony)
    {
        bool bishopSeen = false;
        foreach (var c in colony.Citizens.Where(c => c.Alive && c.JobCode == Jobs.Priest).OrderBy(c => c.CitizenId))
        {
            if (c.WorkRole == ChurchRoles.Priest) continue;
            bool ok = c.WorkRole == ChurchRoles.Bishop && !bishopSeen && ChurchOf(colony, c) is { Level: >= ChurchBishopLevel };
            if (ok)
            {
                bishopSeen = true;
                continue;
            }
            c.WorkRole = ChurchRoles.Priest;
            MarkDirty(colony);
        }
    }

    /// <summary>
    /// What stays in a Church's storage: coins, books and writing tools, paintings and bookshelves, and for its
    /// stations their healing things, tools and what their recipes take. The rest goes to the warehouse.
    /// </summary>
    ItemMatch ChurchJunk(Colony colony, Building church)
    {
        var stations = StationsIn(colony, church).ToList();
        var inputs = stations.Select(s => HutRecipeInputs(colony, s)).ToList();
        var tools = stations.SelectMany(s => Config.HutRecipeTools.GetValueOrDefault(s.Type) ?? Array.Empty<string>()).ToHashSet();
        return s => !IsCoin(s) && !IsChurchThing(s) && !IsHealingItem(s) && !tools.Contains(FirstPart(s.Collectible.Code)) && !inputs.Any(i => i(s));
    }

    static bool IsChurchThing(ItemStack s) =>
        s.Collectible is ItemBook || IsWritingTool(s) || s.Block is BlockPainting || IsDecorMount(DecorKind.Books, s);

    /// <summary>Where a settler puts up decorations: the clergy their Church, everyone else their home.</summary>
    public Building? DecorPlaceOf(Colony colony, CitizenRecord c) =>
        c.JobCode == Jobs.Priest && ChurchOf(colony, c) is { } church ? church : HomeOf(colony, c);

    // ---- Faith ------------------------------------------------------------------------------

    /// <summary>A settler's Faith, 50 until it has been rolled.</summary>
    public static float FaithOf(CitizenRecord c) => c.Faith < 0 ? 50f : c.Faith;

    /// <summary>The word for a Faith: devout, faithful, doubting or faithless.</summary>
    public string FaithWord(float faith) =>
        faith >= Config.FaithDevoutAt ? "devout" : faith >= 40 ? "faithful" : faith >= Config.FaithAttendsAt ? "doubting" : "faithless";

    /// <summary>Everyone gets a Faith the first time it is needed: most in the middle, a few very devout or faithless.</summary>
    void EnsureFaith(Colony colony)
    {
        var rand = sapi.World.Rand;
        foreach (var c in colony.Citizens.Where(c => c.Alive && c.Faith < 0))
        {
            c.Faith = (float)Math.Round((rand.NextDouble() + rand.NextDouble() + rand.NextDouble()) / 3 * 100);
            MarkDirty(colony);
        }
    }

    /// <summary>The colony's average Faith, for the Church window; -1 with nobody to count.</summary>
    public int AverageFaith(Colony colony)
    {
        var all = colony.Citizens.Where(c => c.Alive && !c.Visitor).Select(FaithOf).ToList();
        return all.Count == 0 ? -1 : (int)Math.Round(all.Average());
    }

    /// <summary>Multiply skill XP by this: more for a day after a service.</summary>
    public float ChurchLearnFactor(CitizenRecord c) =>
        c.LearnBoostUntilTotalDays > sapi.World.Calendar.TotalDays ? 1 + Math.Max(0, Config.ServiceLearnBoost) : 1f;

    /// <summary>The standing thoughts about the church, for ThoughtsOf: a high tithe for the faithless, the church's care for orphans and the sheltered.</summary>
    void ChurchThoughts(Colony colony, CitizenRecord c, Action<string, int> standing)
    {
        if (!HasChurch(colony)) return;
        if (colony.TithePercent >= Config.TitheGrumbleAtPercent && FaithOf(c) < 40 && WageOf(c.JobCode) > 0 && HasMint(colony) && ServingChurch(colony) != null)
            standing("tithe", Config.TitheGrumbleMood);
        if (colony.LastAlmsDay >= 0 && (int)sapi.World.Calendar.TotalDays - colony.LastAlmsDay <= Math.Max(1, Config.ChurchServiceEveryDays))
        {
            if (IsOrphan(colony, c) && caredFor.Contains(c.CitizenId)) standing("orphancare", Config.OrphanCareMood);
            if (InPoorhouse(colony, c) && caredFor.Contains(c.CitizenId)) standing("poorhouse", Config.PoorhouseMood);
        }
    }

    // ---- Services ---------------------------------------------------------------------------

    public bool IsServiceDay(int day) => Config.ChurchServiceEveryDays > 0 && day % Config.ChurchServiceEveryDays == 0;

    /// <summary>True during the hours of a service on a service day; day is today.</summary>
    public bool IsServiceTime(out int day)
    {
        var cal = sapi.World.Calendar;
        day = (int)cal.TotalDays;
        float hour = cal.HourOfDay;
        return IsServiceDay(day) && hour >= Config.ChurchServiceHour && hour < Config.ChurchServiceHour + Config.ChurchServiceHours;
    }

    /// <summary>The next service day from today (today itself while its service has not ended).</summary>
    public int NextServiceDay()
    {
        int every = Math.Max(1, Config.ChurchServiceEveryDays);
        var cal = sapi.World.Calendar;
        int today = (int)cal.TotalDays;
        int next = (today + every - 1) / every * every;
        if (next == today && cal.HourOfDay >= Config.ChurchServiceHour + Config.ChurchServiceHours) next += every;
        return next;
    }

    /// <summary>
    /// True when a settler goes to today's service now: it is time, a Church has clergy, and the settler has Faith
    /// enough, is not a guard on watch, the clergy or a visitor, and has not been already.
    /// </summary>
    public Building? ServiceFor(Colony colony, CitizenRecord c)
    {
        if (!IsServiceTime(out int day) || !c.Alive || c.Visitor || c.IsDowned || c.WaitingForRoom || c.LastServiceDay == day) return null;
        if (c.JobCode is Jobs.Guard or Jobs.Priest || IsBrokenDown(c) || FaithOf(c) < Config.FaithAttendsAt) return null;
        if (serviceAttendance.TryGetValue(colony.ColonyId, out var a) && a.Day == day && a.Came.Contains(c.CitizenId)) return null;
        return ServingChurch(colony);
    }

    /// <summary>A settler (or one of the clergy) is at the Church for the service.</summary>
    public void AtService(Colony colony, CitizenRecord c, bool clergy)
    {
        if (!IsServiceTime(out int day)) return;
        if (!serviceAttendance.TryGetValue(colony.ColonyId, out var a) || a.Day != day)
        {
            a = (day, new HashSet<int>(), new HashSet<int>());
            serviceAttendance[colony.ColonyId] = a;
        }
        (clergy ? a.Clergy : a.Came).Add(c.CitizenId);
    }

    /// <summary>True while a member of the clergy is at the Church leading today's service.</summary>
    public bool ServiceUnderway(Colony colony) =>
        IsServiceTime(out int day) && serviceAttendance.TryGetValue(colony.ColonyId, out var a) && a.Day == day && a.Clergy.Count > 0;

    /// <summary>
    /// Once a service's hours are over: those who came feel better, grow in Faith and learn faster for a while,
    /// with a small chance of a level in a random skill; devout settlers who stayed away are unhappy. Without
    /// clergy there, there was no service.
    /// </summary>
    void SettleService(Colony colony)
    {
        var cal = sapi.World.Calendar;
        int today = (int)cal.TotalDays;
        if (!IsServiceDay(today) || cal.HourOfDay < Config.ChurchServiceHour + Config.ChurchServiceHours) return;
        if (colony.LastServiceDay >= today) return;
        colony.LastServiceDay = today;
        MarkDirty(colony);
        if (!serviceAttendance.TryGetValue(colony.ColonyId, out var a) || a.Day != today) return;
        serviceAttendance.Remove(colony.ColonyId);
        if (a.Clergy.Count == 0)
        {
            if (a.Came.Count > 0) LogEvent(colony, "event-church-no-service", a.Came.Count.ToString());
            return;
        }

        var clergy = a.Clergy.Select(id => colony.Citizens.Find(c => c.CitizenId == id)).OfType<CitizenRecord>().ToList();
        var leader = clergy.OrderByDescending(IsBishop).ThenByDescending(c => SkillLevel(c, Theology)).First();
        bool bishop = IsBishop(leader);
        int theology = clergy.Max(c => SkillLevel(c, Theology));
        float gain = Config.ServiceFaithGain * (1 + 0.5f * theology / MaxLevel) * (bishop ? Config.BishopServiceFactor : 1f);
        int mood = (int)Math.Round(8 * (bishop ? Config.BishopServiceFactor : 1f));
        double now = cal.TotalDays;
        var rand = sapi.World.Rand;
        int came = 0;
        foreach (var c in a.Came.Select(id => colony.Citizens.Find(c => c.CitizenId == id)).OfType<CitizenRecord>().Where(c => c.Alive))
        {
            came++;
            c.LastServiceDay = today;
            c.Faith = Math.Min(100, FaithOf(c) + gain);
            AddThought(c, "service", mood);
            c.LearnBoostUntilTotalDays = now + Math.Max(0, Config.ServiceLearnDays);
            double chance = Config.ServiceLevelChanceMin + (Config.ServiceLevelChanceMax - Config.ServiceLevelChanceMin) * FaithOf(c) / 100.0;
            if (rand.NextDouble() < chance) LearnAtChurch(colony, c);
        }
        foreach (var c in colony.Citizens.Where(c => c.Alive && !c.Visitor && !c.IsChild && !c.IsDowned && c.JobCode is not (Jobs.Priest or Jobs.Guard)
            && !a.Came.Contains(c.CitizenId) && FaithOf(c) >= Config.FaithDevoutAt))
        {
            AddThought(c, "missedservice");
            c.Faith = Math.Max(0, FaithOf(c) - Config.MissedServiceFaithLoss);
        }
        foreach (var c in clergy) GainWorkXp(colony, c, Jobs.Priest, Math.Clamp(came, 1, 10));
        ChurchToday(colony).Attended += came;
        PayAlms(colony, today);
        LogEvent(colony, bishop ? "event-church-service-bishop" : "event-church-service", leader.Name, came.ToString());
    }

    /// <summary>A level in a random skill the settler can still learn, from what it heard at church.</summary>
    void LearnAtChurch(Colony colony, CitizenRecord c)
    {
        if (!Config.Skills || c.IsChild) return;
        var open = Skills().Where(s => !IsIncapable(c, s.Code) && SkillLevel(c, s.Code) < MaxLevel).ToList();
        if (open.Count == 0) return;
        var skill = open[sapi.World.Rand.Next(open.Count)];
        int level = SkillLevel(c, skill.Code) + 1;
        c.SkillXp[skill.Code] = Math.Max(SkillXp(c, skill.Code), XpForLevel(level));
        LogEvent(colony, "event-church-learned", c.Name, "@skill-" + skill.Code, level.ToString());
        AddThought(c, "skillup", 0);
        MarkDirty(colony);
    }

    // ---- Funerals ---------------------------------------------------------------------------

    /// <summary>The next settler to bury: died within FuneralWithinDays, not buried yet, nobody on the way to it; the longest dead first.</summary>
    public CitizenRecord? NextFuneral(Colony colony)
    {
        double now = sapi.World.Calendar.TotalDays;
        return colony.Citizens
            .Where(c => !c.Alive && !c.Buried && !c.Visitor && c.DiedTotalDays > 0 && now - c.DiedTotalDays <= Config.FuneralWithinDays && !funeralsUnderway.Contains(c.CitizenId))
            .OrderBy(c => c.DiedTotalDays).FirstOrDefault();
    }

    /// <summary>Settlers who died lately and have not had their funeral, for the Church window.</summary>
    public int FuneralsWaiting(Colony colony)
    {
        double now = sapi.World.Calendar.TotalDays;
        return colony.Citizens.Count(c => !c.Alive && !c.Buried && !c.Visitor && c.DiedTotalDays > 0 && now - c.DiedTotalDays <= Config.FuneralWithinDays);
    }

    public void ClaimFuneral(CitizenRecord dead, bool claim)
    {
        if (claim) funeralsUnderway.Add(dead.CitizenId);
        else funeralsUnderway.Remove(dead.CitizenId);
    }

    /// <summary>Where the funeral is held: at the settler's grave marker, else at the Church.</summary>
    public BlockPos FuneralSpot(Colony colony, CitizenRecord dead, Building church)
    {
        var grave = colony.Markers.Find(m => m.Kind == "grave" && m.CitizenId == dead.CitizenId);
        return grave != null ? new BlockPos((int)Math.Floor(grave.X), (int)Math.Floor(grave.Y), (int)Math.Floor(grave.Z)) : church.HutPos.Copy();
    }

    /// <summary>The funeral is held: the settler is buried, and everyone's grief over a death fades faster, with some comfort.</summary>
    public void HoldFuneral(Colony colony, CitizenRecord priest, CitizenRecord dead)
    {
        funeralsUnderway.Remove(dead.CitizenId);
        if (dead.Buried) return;
        dead.Buried = true;
        double now = sapi.World.Calendar.TotalDays;
        float factor = Math.Clamp(Config.FuneralGriefFactor, 0, 1);
        foreach (var c in colony.Citizens.Where(c => c.Alive && c != priest))
        {
            bool grieving = false;
            foreach (var t in c.Thoughts.Where(t => t.Code is "death" or "familydeath" && t.UntilTotalDays > now))
            {
                t.UntilTotalDays = now + (t.UntilTotalDays - now) * factor;
                grieving = true;
            }
            if (grieving) AddThought(c, "funeral");
        }
        GainWorkXp(colony, priest, Jobs.Priest, 3);
        ChurchToday(colony).Funerals++;
        LogEvent(colony, "event-church-funeral", priest.Name, dead.Name);
        MarkDirty(colony);
    }

    // ---- Tithes -----------------------------------------------------------------------------

    /// <summary>
    /// After a settler is paid (ColonyManager.Economy.cs): it gives the church TithePercent of the wage, all of
    /// it when faithful, less with little Faith. The coins go into the Church's storage. Whole coins only: the
    /// fraction is a chance of one more, so small wages still give on average.
    /// </summary>
    partial void OnWagePaid(Colony colony, CitizenRecord c, int wage)
    {
        if (colony.TithePercent <= 0 || c.JobCode == Jobs.Priest || ServingChurch(colony) is not { } church) return;
        float faith = FaithOf(c);
        float share = faith >= 40 ? Config.TitheShareFaithful : faith >= Config.FaithAttendsAt ? Config.TitheShareDoubting : Config.TitheShareFaithless;
        double owed = wage * colony.TithePercent / 100.0 * Math.Max(0, share);
        int tithe = (int)Math.Floor(owed);
        if (sapi.World.Rand.NextDouble() < owed - tithe) tithe++;
        tithe = Math.Min(tithe, c.Purse);
        if (tithe <= 0) return;
        int stored = PutChurchCoins(colony, church, tithe);
        c.Purse -= stored;
        if (stored > 0) ChurchToday(colony).Tithes += stored;
    }

    /// <summary>Coins in the Church's storage.</summary>
    public int ChurchCoins(Colony colony, Building church) => CountItems(HutStorage(colony, church), CurrencyMatch(colony));

    /// <summary>Puts coins into the Church's storage; returns how many found room.</summary>
    int PutChurchCoins(Colony colony, Building church, int count) => PutCoins(colony, HutStorage(colony, church).ToList(), count, false);

    /// <summary>Spends coins from the Church's storage. False (and nothing taken) when it has too few.</summary>
    bool SpendChurchCoins(Colony colony, Building church, int count)
    {
        if (count <= 0) return true;
        var storage = HutStorage(colony, church).ToList();
        var money = CurrencyMatch(colony);
        if (CountItems(storage, money) < count) return false;
        int got = 0;
        while (got < count)
        {
            var stack = TakeItems(storage, money, count - got);
            if (stack == null || stack.StackSize <= 0) break;
            got += stack.StackSize;
        }
        return true;
    }

    public void SetTithe(Colony colony, int percent)
    {
        colony.TithePercent = percent is 0 or 5 or 10 ? percent : 10;
        MarkDirty(colony);
    }

    ChurchDay ChurchToday(Colony colony)
    {
        int today = (int)sapi.World.Calendar.TotalDays;
        var day = colony.ChurchLedger.Count > 0 && colony.ChurchLedger[^1].Day == today ? colony.ChurchLedger[^1] : null;
        if (day == null)
        {
            day = new ChurchDay { Day = today };
            colony.ChurchLedger.Add(day);
            if (colony.ChurchLedger.Count > ChurchLedgerDays) colony.ChurchLedger.RemoveRange(0, colony.ChurchLedger.Count - ChurchLedgerDays);
        }
        MarkDirty(colony);
        return day;
    }

    // ---- Charity ----------------------------------------------------------------------------

    /// <summary>Orphans and the sheltered the church paid for at the last alms. Not saved: alms come round again each week.</summary>
    readonly HashSet<int> caredFor = new();

    /// <summary>A child whose parents have all died.</summary>
    public bool IsOrphan(Colony colony, CitizenRecord c) =>
        c.Alive && c.IsChild && c.ParentIds.Count > 0 && ParentsOf(colony, c).All(p => !p.Alive);

    /// <summary>A settler sleeping in one of a Church's beds.</summary>
    public bool InPoorhouse(Colony colony, CitizenRecord c) =>
        c.Alive && c.HomeBedPos != null && ZoneAt(colony, c.HomeBedPos) is { Type: BuildingTypes.Church };

    /// <summary>True for a bed inside a Church: those go to settlers last (ColonyManager.EnsureBed).</summary>
    public bool IsChurchBed(Colony colony, BlockPos bed) =>
        Churches(colony).Any(ch => { var (min, max) = BuildingBox(ch); return InBox(bed, min, max); });

    /// <summary>
    /// Once a week, after the service: the church pays CharityCarePerWeek for each orphan and each settler in its
    /// beds; those it can pay for get the orphancare and poorhouse thoughts until the next.
    /// </summary>
    void PayAlms(Colony colony, int today)
    {
        if (ServingChurch(colony) is not { } church) return;
        colony.LastAlmsDay = today;
        foreach (var c in colony.Citizens.Where(c => caredFor.Contains(c.CitizenId))) caredFor.Remove(c.CitizenId);
        // Those in the poorhouse move out to a free bed elsewhere once there is one.
        foreach (var c in colony.Citizens.Where(c => InPoorhouse(colony, c)).ToList())
        {
            var was = c.HomeBedPos;
            c.HomeBedPos = null;
            if (EnsureBed(colony, c) == null) c.HomeBedPos = was;
        }
        foreach (var c in colony.Citizens.Where(c => IsOrphan(colony, c) || InPoorhouse(colony, c)))
        {
            if (!SpendChurchCoins(colony, church, Config.CharityCarePerWeek)) break;
            caredFor.Add(c.CitizenId);
            ChurchToday(colony).CareCoins += Config.CharityCarePerWeek;
            AddThought(c, "alms");
        }
    }

    /// <summary>
    /// Every minute or so: when settlers are hungry and the colony has no food at all, the church buys bread and
    /// puts it in its storage, where they find it; when its Healer's Station has no bandages, it buys some.
    /// </summary>
    void TickCharity(Colony colony)
    {
        long nowMs = sapi.World.ElapsedMilliseconds;
        if (nextCharityMs.TryGetValue(colony.ColonyId, out long at) && nowMs < at) return;
        nextCharityMs[colony.ColonyId] = nowMs + CharityCheckMs;
        if (ServingChurch(colony) is not { } church) return;

        var hungry = colony.Citizens.Where(c => c.Alive && !c.IsDowned && c.Saturation >= 0 && c.Saturation < Config.SaturationMax * 0.25f).ToList();
        if (hungry.Count > 0 && !ColonyHasFood(colony) && CharityFood() is { } food)
        {
            int meals = 0;
            var storage = HutStorage(colony, church).ToList();
            while (meals < hungry.Count && SpendChurchCoins(colony, church, Config.CharityMealCost))
            {
                var bread = new ItemStack(food, 2);
                foreach (var chest in storage)
                {
                    if (bread.StackSize <= 0) break;
                    PutItems(chest, bread);
                }
                if (bread.StackSize > 0) PutInWarehouse(colony, bread);
                if (bread.StackSize > 0) sapi.World.SpawnItemEntity(bread, church.HutPos.ToVec3d().Add(0.5, 1.2, 0.5));
                meals++;
            }
            if (meals > 0)
            {
                var day = ChurchToday(colony);
                day.Meals += meals;
                day.FoodCoins += meals * Config.CharityMealCost;
                LogEvent(colony, "event-church-fed", meals.ToString());
            }
        }

        foreach (var station in StationsIn(colony, church).Where(s => s.Type == BuildingTypes.Healer))
        {
            var storage = HutStorage(colony, station).ToList();
            if (CountItems(storage, s => IsHealingItem(s)) > 0) continue;
            var bandage = HutRecipeKinds(BuildingTypes.Healer).FirstOrDefault(k => FirstPart(k.Sample.Collectible.Code) == "bandage")?.Sample;
            if (bandage == null) break;
            int bought = 0;
            while (bought < Config.CharityBandages && SpendChurchCoins(colony, church, Config.CharityBandageCost))
            {
                var one = bandage.Clone();
                one.StackSize = 1;
                foreach (var chest in storage)
                {
                    if (one.StackSize <= 0) break;
                    PutItems(chest, one);
                }
                if (one.StackSize > 0) sapi.World.SpawnItemEntity(one, station.HutPos.ToVec3d().Add(0.5, 1.2, 0.5));
                bought++;
            }
            if (bought > 0)
            {
                var day = ChurchToday(colony);
                day.Bandages += bought;
                day.BandageCoins += bought * Config.CharityBandageCost;
                LogEvent(colony, "event-church-bandages", bought.ToString());
            }
            break;
        }
    }

    /// <summary>The food the church hands out: the first of CharityFoods the game has.</summary>
    CollectibleObject? CharityFood()
    {
        foreach (var code in Config.CharityFoods ?? Array.Empty<string>())
        {
            var loc = new AssetLocation(code);
            if (sapi.World.GetItem(loc) is { } item) return item;
            if (sapi.World.GetBlock(loc) is { Id: > 0 } block) return block;
        }
        return null;
    }

    /// <summary>True when any container in the colony holds something to eat.</summary>
    bool ColonyHasFood(Colony colony) => FindBlockEntities<BlockEntityContainer>(colony)
        .Any(c => c.Inventory.Any(s => !s.Empty && s.Itemstack.Collectible.GetNutritionProperties(sapi.World, s.Itemstack, null) is { Satiety: > 0 }));

    // ---- Records ----------------------------------------------------------------------------

    /// <summary>A book that can still be written in: one the game lets players write, with no text yet.</summary>
    public static bool IsBlankBook(ItemStack? s) =>
        s?.Collectible is ItemBook && s.Collectible.Attributes?["editable"].AsBool() == true && !s.Attributes.HasAttribute("text") && !s.Attributes.HasAttribute("textCodes");

    /// <summary>Something to write with: the game's ink and quill, or anything a mod says writes.</summary>
    public static bool IsWritingTool(ItemStack? s) => s?.Collectible.Attributes?["writingTool"].AsBool() == true;

    /// <summary>The colony's events since the last chronicle.</summary>
    List<ColonyEvent> UnrecordedEvents(Colony colony) => colony.Events.Where(e => e.TotalDays > colony.LastChronicleTotalDays).ToList();

    /// <summary>True when there is enough to write about for a new chronicle.</summary>
    public int UnrecordedCount(Colony colony) => UnrecordedEvents(colony).Count;

    public bool ChronicleDue(Colony colony) => Config.ChronicleMinEvents > 0 && UnrecordedEvents(colony).Count >= Config.ChronicleMinEvents;

    /// <summary>
    /// Writes the colony's events since the last chronicle into a blank book the priest carries, signed by the
    /// priest, and puts it in the Church's storage. False when it has no blank book.
    /// </summary>
    public bool WriteChronicle(Colony colony, Building church, CitizenRecord priest)
    {
        var book = TakeFromPack(colony, priest, IsBlankBook, 1);
        if (book == null) return false;
        var events = UnrecordedEvents(colony);
        var lines = events.Select(e => Lang.Get("createacolony:chronicle-line", DateText(e.TotalDays), EventText(e)));
        double from = events.Count > 0 ? events[0].TotalDays : sapi.World.Calendar.TotalDays;
        book.Attributes.SetString("title", Lang.Get("createacolony:chronicle-title", colony.Name, DateText(from)));
        book.Attributes.SetString("text", Lang.Get("createacolony:chronicle-heading", colony.Name, priest.Name) + "\n\n" + string.Join("\n", lines));
        book.Attributes.SetString("signedby", priest.Name);
        colony.LastChronicleTotalDays = sapi.World.Calendar.TotalDays;
        foreach (var chest in HutStorage(colony, church))
        {
            if (book.StackSize <= 0) break;
            PutItems(chest, book);
        }
        if (book.StackSize > 0) AddToPack(colony, priest, book);
        if (book.StackSize > 0) sapi.World.SpawnItemEntity(book, church.HutPos.ToVec3d().Add(0.5, 1.2, 0.5));
        GainWorkXp(colony, priest, Jobs.Priest, 3);
        ChurchToday(colony).Books++;
        LogEvent(colony, "event-church-chronicle", priest.Name);
        return true;
    }

    /// <summary>An event log line in words, as the Town Hall shows it.</summary>
    static string EventText(ColonyEvent e)
    {
        var args = e.Args.Select(a => a.StartsWith('@') ? Lang.Get("createacolony:" + a[1..]) : a).Cast<object>().ToArray();
        return Lang.Get("createacolony:" + e.Key, args);
    }

    /// <summary>"Year 2, month 3, day 5" for a moment in total days.</summary>
    string DateText(double totalDays)
    {
        int perMonth = Math.Max(1, sapi.World.Calendar.DaysPerMonth);
        int day = (int)totalDays;
        int month = day / perMonth;
        return Lang.Get("createacolony:chronicle-date", month / 12 + 1, month % 12 + 1, day % perMonth + 1);
    }

    // ---- The colony tick and the window -------------------------------------------------------

    /// <summary>Part of the colony tick: Faith for newcomers, roles, stations, the service's end, and charity.</summary>
    void TickChurch(Colony colony)
    {
        EnsureFaith(colony);
        FixChurchRoles(colony);
        TickStations(colony);
        if (!HasChurch(colony))
        {
            ForgetChurch(colony);
            return;
        }
        SettleService(colony);
        TickCharity(colony);
    }

    /// <summary>Lines about the Church for its window: the next service and the church's coins.</summary>
    public IEnumerable<string> ChurchInfo(Colony colony, Building church)
    {
        int next = NextServiceDay();
        int today = (int)sapi.World.Calendar.TotalDays;
        yield return ServiceUnderway(colony) ? Lang.Get("createacolony:church-service-now")
            : next == today ? Lang.Get("createacolony:church-service-today", (int)Config.ChurchServiceHour)
            : Lang.Get("createacolony:church-service-next", next - today);
        yield return ColonyMoney.Get(sapi, "createacolony:church-coins", ChurchCoins(colony, church), colony.TithePercent);
    }
}
