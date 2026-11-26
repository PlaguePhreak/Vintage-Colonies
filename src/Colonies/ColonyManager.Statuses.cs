using System;
using System.Linq;
using CreateAColony.Settlers;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.GameContent;

namespace CreateAColony.Colonies;

/// <summary>
/// Why a settler stands about (Aaron, 0.203.0): "Taking a break" alone hid a courier with a load nobody would take, a
/// Mayor waiting to plan and a worker whose hut waits for clay. The settler window, the couriers list and the report
/// add the reason: "Taking a break: needs clay".
/// </summary>
public partial class ColonyManager
{
    /// <summary>The reason a settler with no task of its own is idle, or null when there is nothing to say.</summary>
    public string? IdleReason(Colony colony, CitizenRecord c, EntityAgent? entity)
    {
        if (!c.Alive || c.Visitor || c.IsChild) return null;
        if (IsBrokenDown(c)) return Lang.Get("createacolony:idle-why-breakdown");
        // A load it could not deliver: the courier task keeps the reason while it rests between tries.
        var tasks = entity?.GetBehavior<EntityBehaviorTaskAI>()?.TaskManager;
        if (tasks?.GetTask<AiTaskSettlerCourier>()?.TroubleText(colony, c) is { } trouble) return trouble;
        if (c.JobCode == Jobs.Mayor) return MayorIdleReason(colony);
        if (c.JobCode == Jobs.Bailiff) return null;
        if (string.IsNullOrEmpty(c.JobCode) && c.HomeJob == null && PackEmpty(c) && NoJobWhy(colony, c) is { } why) return why;
        if (string.IsNullOrEmpty(c.JobCode))
        {
            return Lang.Get(c.HomeJob != null ? "createacolony:idle-why-lent" : PackEmpty(c) ? "createacolony:idle-why-nojob" : "createacolony:idle-why-nojob-carrying", PackItems(c).Count);
        }
        if (c.JobCode == Jobs.Courier)
        {
            return PackEmpty(c) ? Lang.Get(colony.Requests.Any(r => r.Remaining > 0) ? "createacolony:idle-why-courier-nothing-to-fetch" : "createacolony:idle-why-courier-no-requests")
                : Lang.Get("createacolony:idle-why-courier-carrying", PackItems(c).Count);
        }
        if (c.WorkplacePos == null && c.JobCode != Jobs.Guard) return Lang.Get("createacolony:idle-why-nohut", Lang.Get("createacolony:job-" + c.JobCode));
        if (WorkerStatus.TryGetValue(c.CitizenId, out var status) && status.Length > 0 && !PassingStatuses.Contains(status)
            && Lang.HasTranslation("createacolony:workstatus-" + status))
        {
            return Lang.Get("createacolony:workstatus-" + status);
        }
        return null;
    }

    /// <summary>Why a grown settler has no job yet (0.205.0, note 110), or null when the Bailiff should give one soon.</summary>
    public string? NoJobWhy(Colony colony, CitizenRecord c)
    {
        if (c.WaitingForRoom) return Lang.Get("createacolony:idle-why-nojob-home");
        if (BailiffOf(colony) == null) return Lang.Get("createacolony:idle-why-nojob-nobailiff");
        if (!colony.BailiffGivesJobs) return Lang.Get("createacolony:idle-why-nojob-off");
        bool anyPlace = colony.Buildings.Any(b => AutoStaffable(b) && JobOf(b.Type) is { } job && FreePlaces(colony, b) > 0 && IncapableOfJob(c, job) == null);
        return anyPlace ? null : Lang.Get("createacolony:idle-why-nojob-noplace");
    }

    /// <summary>What the Mayor waits for, and when it looks again.</summary>
    string MayorIdleReason(Colony colony)
    {
        if (colony.MayorMode == MayorModes.Off) return Lang.Get("createacolony:idle-why-mayor-off");
        double hours = sapi.World.Calendar.TotalDays * sapi.World.Calendar.HoursPerDay;
        double left = Math.Max(0, Math.Max(0.25, Config.MayorEveryHours) - (hours - colony.MayorLastLookHours));
        string next = left >= 1 ? Lang.Get("createacolony:idle-why-hours", Math.Round(left, 1)) : Lang.Get("createacolony:idle-why-minutes", (int)Math.Ceiling(left * 60));
        if (colony.MayorPlan is { } plan)
        {
            return Lang.Get(plan.Approved ? "createacolony:idle-why-mayor-doing" : "createacolony:idle-why-mayor-asks", PlanName(plan), next);
        }
        string idle = MayorIdleText(colony);
        return idle.Length > 0 ? Lang.Get("createacolony:idle-why-mayor-waits", idle, next) : Lang.Get("createacolony:idle-why-mayor-next", next);
    }

    /// <summary>What the settler is doing, with why it is idle and its note (what it lacks) when there is one.</summary>
    public string ActivityText(Colony colony, CitizenRecord citizen, EntityAgent? entity, string activity)
    {
        if (activity == Lang.Get("createacolony:activity-idle") && IdleReason(colony, citizen, entity) is { Length: > 0 } why)
        {
            activity = Lang.Get("createacolony:activity-with-reason", activity, why);
        }
        return WithNote(citizen, activity);
    }
}
