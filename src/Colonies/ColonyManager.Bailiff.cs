using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace CreateAColony.Colonies;

/// <summary>
/// The Bailiff, the lord's manager of the estate while the players are away. One settler with the Bailiff job
/// works from the Town Hall. While on duty (not knocked out or sulking) they:
/// - answer wanderers at the Town Hall as the Bailiff tab says: welcome them, welcome them while a bed is free,
///   turn them away, or leave it to the players;
/// - give jobless settlers a job at a hut with a free place, the one they are best at;
/// - lend a worker who can't work at their own hut (no tool, nothing left to pick, everything in stock) to
///   another hut with a free place, or to hauling, for BailiffLendDays, then send them back;
/// - oversee the fields: farmers work faster (BailiffFarmBoost), everyone else a little faster (BailiffWorkBoost);
/// - on the first of each month collect taxes from settlers' purses and pay their wages in the Royal Mint's coins
///   (ColonyManager.Economy.cs), and keep a ledger of it;
/// - are not liked: every other settler has a small mood penalty (BailiffMood) while one watches over them.
/// The Bailiff tab also shows an estate report: every hut, who works there and what they are waiting on.
/// </summary>
public partial class ColonyManager
{
    /// <summary>How many days the bailiff's ledger keeps.</summary>
    public const int LedgerDays = 30;
    /// <summary>How often the bailiff looks for jobless settlers to put to work, in real milliseconds.</summary>
    const long BailiffJobCheckMs = 10_000;

    readonly Dictionary<string, long> lastBailiffJobCheck = new();
    readonly Dictionary<string, long> lastBailiffLoanCheck = new();
    /// <summary>Since when (total days) each worker has been stuck, by colony and citizen id. Not saved.</summary>
    readonly Dictionary<(string, int), double> stuckSince = new();

    /// <summary>Hut statuses that mean the worker can't do its job there now: nothing left, everything in stock, or nothing to work with.</summary>
    static readonly HashSet<string> StuckStatuses = new()
    {
        "no-hut-chest", "hut-chest-full", "no-trees", "no-fields", "growing", "gathered-all", "explored-all", "prospected-all",
        "hunter-nothing", "mine-no-readings", "mine-no-site", "mine-no-site-ore", "mine-blocked", "mine-shaft-blocked", "mine-shaft-done",
        "mine-no-shaft-spot", "mineshaft-done", "quarry-done", "quarry-no-room", "no-kiln-spot", "no-form-spot", "no-firepit",
        "firepit-busy", "oven-nofire", "smith-no-forge", "smith-no-anvil", "smith-forge-busy", "smelter-firepit-busy",
        "smelter-no-mold-spot", "tailor-nothing-to-mend", "jeweller-nothing-set"
    };

    /// <summary>Statuses that come and go during work and say nothing either way (walking about, fetching tools).</summary>
    static readonly HashSet<string> PassingStatuses = new() { "starting", "unreachable", "foraging-look", "fetching-tools", "searching", "mine-travelling" };

    static bool IsStuckStatus(string status) =>
        StuckStatuses.Contains(status) || status.StartsWith("needs-") || status.Contains("-needs-") || status.EndsWith("-stocked");

    /// <summary>Jobs the bailiff never moves anyone out of: guards keep watch and healers wait for the hurt.</summary>
    static bool KeepsPost(string? job) => job is null or Jobs.Bailiff or Jobs.Guard or Jobs.Healer or Jobs.Builder or Jobs.Courier or Jobs.Priest;

    /// <summary>A lent worker goes back to normal: they keep whatever job they have now.</summary>
    public static void EndLoan(CitizenRecord c)
    {
        c.HomeJob = null;
        c.HomeWorkplacePos = null;
        c.LentUntilTotalDays = 0;
    }

    /// <summary>Free places at a hut, counting workers lent away from it as still there.</summary>
    int FreePlaces(Colony colony, Building hut)
    {
        string? job = JobOf(hut.Type);
        if (job == null) return 0;
        int away = colony.Citizens.Count(c => c.Alive && c.HomeJob == job && c.HomeWorkplacePos != null && c.HomeWorkplacePos.Equals(hut.HutPos));
        return WorkerSlots(hut) - WorkersAt(colony, hut).Count - away;
    }

    /// <summary>
    /// True when a hut can have workers now: built, or (0.198.0, Config.WorkAtUnbuiltHuts) just its hut block placed,
    /// so workers start before the builder gets to it. A hut being taken down never.
    /// </summary>
    public bool Staffable(Building hut) => !hut.Removing && (hut.Level >= 1 || Config.WorkAtUnbuiltHuts);

    /// <summary>The colony's bailiff, or null.</summary>
    public CitizenRecord? BailiffOf(Colony colony) =>
        colony.Citizens.Find(c => c.Alive && !c.Visitor && !c.IsChild && c.JobCode == Jobs.Bailiff);

    /// <summary>The bailiff when they are fit to work: not knocked out and not sulking.</summary>
    public CitizenRecord? BailiffOnDuty(Colony colony) =>
        BailiffOf(colony) is { IsDowned: false } b && !IsBrokenDown(b) ? b : null;

    /// <summary>Multiply work speed by this: farmers work faster with a bailiff overseeing the fields, the others a little.</summary>
    public float BailiffWorkFactor(Colony colony, CitizenRecord c)
    {
        if (c.JobCode == null || c.JobCode == Jobs.Bailiff || BailiffOnDuty(colony) == null) return 1f;
        return Math.Max(0.1f, c.JobCode == Jobs.Farmer ? Config.BailiffFarmBoost : Config.BailiffWorkBoost);
    }

    /// <summary>The mood points a settler has from the bailiff watching over them; 0 for the bailiff and when there is none.</summary>
    int BailiffMoodFor(Colony colony, CitizenRecord c) =>
        c.JobCode != Jobs.Bailiff && BailiffOnDuty(colony) != null ? Config.BailiffMood : 0;

    /// <summary>Part of the colony tick: the bailiff answers wanderers, hands out jobs and lends workers, and on payday collects taxes and pays wages.</summary>
    void TickBailiff(Colony colony, double now)
    {
        var bailiff = BailiffOnDuty(colony);
        if (bailiff == null) return;
        AnswerWanderers(colony, bailiff, now);

        long ms = sapi.World.ElapsedMilliseconds;
        if (colony.BailiffGivesJobs && (!lastBailiffJobCheck.TryGetValue(colony.ColonyId, out long last) || ms - last >= BailiffJobCheckMs))
        {
            lastBailiffJobCheck[colony.ColonyId] = ms;
            GiveOutJobs(colony, bailiff);
        }
        if (!lastBailiffLoanCheck.TryGetValue(colony.ColonyId, out long lastLoan) || ms - lastLoan >= BailiffJobCheckMs)
        {
            lastBailiffLoanCheck[colony.ColonyId] = ms;
            SendLentWorkersBack(colony, bailiff, now);
            if (colony.BailiffLendsWorkers) LendStuckWorkers(colony, bailiff, now);
        }
        // Taxes and wages on the first of each month (ColonyManager.Economy.cs).
        Payday(colony, bailiff, now);
    }

    /// <summary>Wanderers who have waited BailiffAnswerHours get the bailiff's answer, as the Bailiff tab says.</summary>
    void AnswerWanderers(Colony colony, CitizenRecord bailiff, double now)
    {
        if (colony.WandererRule == BailiffWanderers.Ask) return;
        double wait = Math.Max(0, Config.BailiffAnswerHours) / 24.0;
        foreach (var visitor in colony.Citizens.Where(c => c.Visitor && c.Alive).ToList())
        {
            double arrived = visitor.VisitorUntilTotalDays - Config.WandererWaitDays;
            if (now < arrived + wait) continue;
            bool welcome = colony.WandererRule switch
            {
                BailiffWanderers.Welcome => true,
                BailiffWanderers.IfBed => FreeBeds(colony) > 0,
                _ => false
            };
            // A full colony has no room for them whatever the rule says.
            if (welcome && Welcome(colony, visitor) == null)
            {
                Today(colony).Welcomed++;
                LogEvent(colony, "event-bailiff-welcomed", bailiff.Name, visitor.Name);
            }
            else
            {
                TurnAway(colony, visitor);
                Today(colony).TurnedAway++;
                LogEvent(colony, "event-bailiff-turned-away", bailiff.Name, visitor.Name);
            }
        }
    }

    /// <summary>Settlers the bailiff may put to work: grown, at home in the colony and without a job.</summary>
    IEnumerable<CitizenRecord> Jobless(Colony colony) =>
        colony.Citizens.Where(c => c.Alive && !c.Visitor && !c.IsChild && !c.WaitingForRoom && !c.IsDowned && c.JobCode == null && c.HomeJob == null);

    /// <summary>
    /// Gives each hut with a free place the jobless settler best at its job: the highest skill level, passions
    /// counting extra. Settlers whose traits rule the job out are passed over. A settler given a job on the
    /// Citizens tab who has not picked their hut yet still counts as taking a place of that job.
    /// </summary>
    void GiveOutJobs(Colony colony, CitizenRecord bailiff)
    {
        var free = Jobless(colony).ToList();
        if (free.Count == 0) return;
        // Every hut that takes workers, built or not (0.198.0, Aaron: work from the hut block). Guard posts come last,
        // so a free place at any other hut is filled first; built huts before unbuilt ones.
        var huts = colony.Buildings.Where(b => Staffable(b) && JobOf(b.Type) != null)
            .OrderBy(b => IsGuardPost(b.Type)).ThenBy(b => b.Level < 1).ThenBy(b => b.BuildingId).ToList();
        foreach (var job in huts.Select(b => JobOf(b.Type)!).Distinct().ToList())
        {
            if (free.Count == 0) break;
            var ofJob = huts.Where(b => JobOf(b.Type) == job).ToList();
            int open = ofJob.Sum(WorkerSlots) - colony.Citizens.Count(c => c.Alive && (c.JobCode == job || c.HomeJob == job));
            while (open > 0 && free.Count > 0)
            {
                var hut = ofJob.FirstOrDefault(b => FreePlaces(colony, b) > 0);
                if (hut == null) break;
                var pick = free.Where(c => IncapableOfJob(c, job) == null).OrderByDescending(c => JobScore(c, job)).ThenBy(c => c.CitizenId).FirstOrDefault();
                if (pick == null) break;
                free.Remove(pick);
                open--;
                pick.JobCode = job;
                pick.WorkplacePos = hut.HutPos.Copy();
                Today(colony).JobsGiven++;
                LogEvent(colony, "event-bailiff-job", bailiff.Name, pick.Name, "@job-" + job, BuildingName(hut));
                TellMembers(colony, "createacolony:bailiff-gave-job", bailiff.Name, pick.Name, Lang.Get("createacolony:job-" + job));
                MarkDirty(colony);
            }
        }
    }

    /// <summary>
    /// Watches each worker's hut status. One that has been stuck for BailiffStuckHours (no tool, nothing left to
    /// pick, everything in stock) is lent to the hut with a free place they are best at, or to hauling when there
    /// is none, for BailiffLendDays. Their own place stays theirs meanwhile.
    /// </summary>
    void LendStuckWorkers(Colony colony, CitizenRecord bailiff, double now)
    {
        double need = Math.Max(0, Config.BailiffStuckHours) / 24.0;
        foreach (var c in colony.Citizens.Where(c => c.Alive && !c.Visitor && !c.IsChild && !c.IsDowned && c.HomeJob == null
            && !KeepsPost(c.JobCode) && c.WorkplacePos != null).ToList())
        {
            var key = (colony.ColonyId, c.CitizenId);
            string status = WorkerStatus.GetValueOrDefault(c.CitizenId, "");
            if (!IsStuckStatus(status))
            {
                if (!PassingStatuses.Contains(status)) stuckSince.Remove(key);
                continue;
            }
            if (!stuckSince.TryGetValue(key, out double since))
            {
                stuckSince[key] = now;
                continue;
            }
            if (now - since < need) continue;
            stuckSince.Remove(key);
            if (GetBuilding(colony, c.WorkplacePos!) is not { } home) continue;
            Lend(colony, bailiff, c, home, status, now);
        }
    }

    void Lend(Colony colony, CitizenRecord bailiff, CitizenRecord c, Building home, string status, double now)
    {
        var target = colony.Buildings
            .Where(b => Staffable(b) && JobOf(b.Type) is { } job && job != c.JobCode && !KeepsPost(job)
                && IncapableOfJob(c, job) == null && FreePlaces(colony, b) > 0)
            .OrderByDescending(b => JobScore(c, JobOf(b.Type)!)).ThenBy(b => b.BuildingId).FirstOrDefault();
        c.HomeJob = c.JobCode;
        c.HomeWorkplacePos = c.WorkplacePos!.Copy();
        c.LentUntilTotalDays = now + Math.Max(0.05, Config.BailiffLendDays);
        // No hut has room: they help with hauling, as jobless settlers do.
        c.JobCode = target != null ? JobOf(target.Type) : null;
        c.WorkplacePos = target?.HutPos.Copy();
        Today(colony).Lent++;
        if (target != null) LogEvent(colony, "event-bailiff-lent", bailiff.Name, c.Name, BuildingName(home), BuildingName(target), "@workstatus-" + status);
        else LogEvent(colony, "event-bailiff-lent-hauling", bailiff.Name, c.Name, BuildingName(home), "@workstatus-" + status);
        MarkDirty(colony);
    }

    /// <summary>Lent workers whose time is up go back to their own hut, if their place there is still free; else they stay.</summary>
    void SendLentWorkersBack(Colony colony, CitizenRecord bailiff, double now)
    {
        foreach (var c in colony.Citizens.Where(c => c.HomeJob != null && now >= c.LentUntilTotalDays).ToList())
        {
            var home = c.HomeWorkplacePos == null ? null : GetBuilding(colony, c.HomeWorkplacePos);
            string homeJob = c.HomeJob!;
            EndLoan(c);
            MarkDirty(colony);
            if (!c.Alive || home == null || home.Removing || JobOf(home.Type) != homeJob || FreePlaces(colony, home) <= 0) continue;
            c.JobCode = homeJob;
            c.WorkplacePos = home.HutPos.Copy();
            LogEvent(colony, "event-bailiff-back", bailiff.Name, c.Name, BuildingName(home));
        }
    }

    /// <summary>How good a settler would be at a job: its skill level, and a passion for it counts as more.</summary>
    int JobScore(CitizenRecord c, string job)
    {
        if (SkillForJob(job) is not { } skill) return 0;
        return SkillLevel(c, skill.Code) * 10 + c.Passions.GetValueOrDefault(skill.Code) * 15;
    }

    /// <summary>Today's line of the ledger, added if needed; old days drop off.</summary>
    LedgerDay Today(Colony colony)
    {
        int today = (int)sapi.World.Calendar.TotalDays;
        var day = colony.Ledger.Count > 0 && colony.Ledger[^1].Day == today ? colony.Ledger[^1] : null;
        if (day == null)
        {
            day = new LedgerDay { Day = today };
            colony.Ledger.Add(day);
            if (colony.Ledger.Count > LedgerDays) colony.Ledger.RemoveRange(0, colony.Ledger.Count - LedgerDays);
        }
        MarkDirty(colony);
        return day;
    }

    /// <summary>
    /// The estate report for the Bailiff tab: one line per hut with who works there and what they are doing or
    /// waiting on, huts nobody works at, then the fields and the jobless.
    /// </summary>
    public List<string> EstateReport(Colony colony)
    {
        var lines = new List<string>();
        foreach (var hut in colony.Buildings.Where(b => JobOf(b.Type) != null && !b.Removing).OrderBy(b => b.Level < 1).ThenBy(b => b.BuildingId))
        {
            string name = BuildingName(hut);
            if (hut.Level < 1)
            {
                lines.Add(Lang.Get("createacolony:estate-not-built", name));
                continue;
            }
            var workers = WorkersAt(colony, hut);
            double now = sapi.World.Calendar.TotalDays;
            foreach (var away in colony.Citizens.Where(c => c.Alive && c.HomeJob != null && c.HomeWorkplacePos != null && c.HomeWorkplacePos.Equals(hut.HutPos)))
            {
                var at = away.WorkplacePos == null ? null : GetBuilding(colony, away.WorkplacePos);
                int hours = (int)Math.Ceiling(Math.Max(0, away.LentUntilTotalDays - now) * sapi.World.Calendar.HoursPerDay);
                lines.Add(at != null ? Lang.Get("createacolony:estate-lent", name, away.Name, BuildingName(at), hours)
                    : Lang.Get("createacolony:estate-lent-hauling", name, away.Name, hours));
            }
            if (workers.Count == 0 && colony.Citizens.Any(c => c.HomeWorkplacePos != null && c.HomeWorkplacePos.Equals(hut.HutPos))) continue;
            if (workers.Count == 0)
            {
                lines.Add(Lang.Get("createacolony:estate-no-worker", name));
                continue;
            }
            foreach (var w in workers)
            {
                string status = WorkerStatus.TryGetValue(w.CitizenId, out var ws) ? ws : WorkStatus.TryGetValue(hut.BuildingId, out var s) ? s : "";
                string what = IsBrokenDown(w) ? Lang.Get("createacolony:estate-sulking")
                    : w.IsDowned ? Lang.Get("createacolony:estate-downed")
                    : status.Length > 0 ? Lang.Get("createacolony:workstatus-" + status) : Lang.Get("createacolony:estate-working");
                lines.Add(Lang.Get("createacolony:estate-worker", name, w.Name, what));
            }
        }
        int farmers = colony.Citizens.Count(c => c.Alive && c.JobCode == Jobs.Farmer);
        lines.Add(Lang.Get("createacolony:estate-fields", colony.Fields.Count, farmers));
        var idle = Jobless(colony).Select(c => c.Name).ToList();
        if (idle.Count > 0) lines.Add(Lang.Get("createacolony:estate-jobless", string.Join(", ", idle)));
        return lines;
    }
}
