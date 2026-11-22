using System.Text;
using CreateAColony.Gui;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;

namespace CreateAColony.Blocks;

/// <summary>
/// A grave marker an undertaker put up over a grave (ColonyManager.Graveyard.cs). It remembers who lies there,
/// their job, when they died and what killed them, and shows it to anyone who looks at it.
/// </summary>
public class BlockEntityGraveMarker : BlockEntity
{
    public string Name = "";
    public string Job = "";
    public bool Child;
    public double DiedTotalDays;
    public string Cause = "";
    public string ColonyId = "";
    public int CitizenId;

    public void Set(string colonyId, int citizenId, string name, string job, bool child, double diedTotalDays, string cause)
    {
        ColonyId = colonyId;
        CitizenId = citizenId;
        Name = name;
        Job = job;
        Child = child;
        DiedTotalDays = diedTotalDays;
        Cause = cause;
        MarkDirty(true);
    }

    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        base.ToTreeAttributes(tree);
        tree.SetString("graveName", Name);
        tree.SetString("graveJob", Job);
        tree.SetBool("graveChild", Child);
        tree.SetDouble("graveDied", DiedTotalDays);
        tree.SetString("graveCause", Cause);
        tree.SetString("graveColony", ColonyId);
        tree.SetInt("graveCitizen", CitizenId);
    }

    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolving)
    {
        base.FromTreeAttributes(tree, worldForResolving);
        Name = tree.GetString("graveName", "");
        Job = tree.GetString("graveJob", "");
        Child = tree.GetBool("graveChild");
        DiedTotalDays = tree.GetDouble("graveDied");
        Cause = tree.GetString("graveCause", "");
        ColonyId = tree.GetString("graveColony", "");
        CitizenId = tree.GetInt("graveCitizen");
    }

    public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
    {
        base.GetBlockInfo(forPlayer, dsc);
        if (Name.Length == 0)
        {
            dsc.AppendLine(Lang.Get("createacolony:grave-nobody"));
            return;
        }
        string job = Child ? Lang.Get("createacolony:citizens-child") : Lang.Get("createacolony:job-" + (Job.Length == 0 ? "none" : Job));
        dsc.AppendLine(Lang.Get("createacolony:grave-here-lies", Name, job));
        if (DiedTotalDays > 0) dsc.AppendLine(Lang.Get("createacolony:grave-died", DateText(Api.World.Calendar, DiedTotalDays)));
        if (Cause.Length > 0) dsc.AppendLine(Lang.Get("createacolony:grave-cause", GuiDialogTownHall.DeathCauseName(Cause)));
    }

    /// <summary>"Year 2, month 3, day 5", as the Church's chronicles write it.</summary>
    public static string DateText(IGameCalendar calendar, double totalDays)
    {
        int perMonth = System.Math.Max(1, calendar.DaysPerMonth);
        int day = (int)totalDays;
        int month = day / perMonth;
        return Lang.Get("createacolony:chronicle-date", month / 12 + 1, month % 12 + 1, day % perMonth + 1);
    }
}
