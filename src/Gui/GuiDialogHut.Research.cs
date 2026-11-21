using System;
using System.Linq;
using CreateAColony.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace CreateAColony.Gui;

/// <summary>
/// The Archive's Research tab (ColonyManager.Research.cs): how fast research goes here and why, what is running,
/// then one branch of the tree at a time with Start buttons for officers.
/// </summary>
public partial class GuiDialogHut
{
    const int ResearchTab = 30;
    static readonly string[] ResearchBranches = { "settlement", "production", "population", "defence", "exploration" };
    string researchBranch = "settlement";

    void ComposeResearch(GuiComposer composer)
    {
        double y = 30;
        int id = BuildingId;
        y = composer.Row(Lang.Get("createacolony:research-tier"), snapshot.ArchiveTier.ToString(), 0, y, Width);
        y = composer.Row(Lang.Get("createacolony:research-slots"), Lang.Get("createacolony:research-slots-value", snapshot.ResearchRunning, snapshot.ResearchSlots), 0, y, Width);
        y = composer.Row(Lang.Get("createacolony:research-speed"), snapshot.ResearchSpeed + "%", 0, y, Width);
        y = composer.Row(Lang.Get("createacolony:research-tokens"), snapshot.KnowledgeTokens.ToString(), 0, y, Width);
        foreach (var note in snapshot.ResearchNotes) y = composer.Text(note, GuiKit.Detail().WithColor(GuiKit.WarnColor), 0, y, Width, 2);

        // Running and paused research first.
        var started = snapshot.Research.Where(r => r.State is 2 or 3).ToList();
        y = composer.Section(Lang.Get("createacolony:research-running-section"), y + 4, Width);
        if (started.Count == 0) y = composer.Text(Lang.Get("createacolony:research-running-none"), GuiKit.Muted(), 0, y, Width);
        foreach (var r in started)
        {
            string name = Lang.Get("createacolony:research-" + r.Code);
            int percent = r.PointsNeeded <= 0 ? 0 : (int)Math.Round(100.0 * r.Points / r.PointsNeeded);
            string state = r.State == 2 ? Lang.Get("createacolony:research-paused") : r.Elsewhere ? Lang.Get("createacolony:research-elsewhere") : "";
            composer.AddStaticText(name, GuiKit.Body(), ElementBounds.Fixed(0, y + 4, Width - 120, 22));
            if (Officer && !r.Elsewhere)
            {
                string code = r.Code;
                composer.AddSmallButton(Lang.Get(r.State == 2 ? "createacolony:research-resume" : "createacolony:research-pause"),
                    () => SendText(r.State == 2 ? ColonyAction.StartResearch : ColonyAction.PauseResearch, id, code), ElementBounds.Fixed(Width - 110, y, 110, 28));
            }
            y += 30;
            string line = Lang.Get("createacolony:research-progress", r.Points, r.PointsNeeded, percent) + (state.Length > 0 ? " - " + state : "");
            y = composer.Text(line, GuiKit.Detail(), 10, y, Width - 10, 2);
            if (r.Note.Length > 0) y = composer.Text(r.Note, GuiKit.Detail().WithColor(GuiKit.WarnColor), 10, y, Width - 10, 2);
            y += 4;
        }

        // One branch of the tree.
        y = composer.Section(Lang.Get("createacolony:research-tree-section"), y + 4, Width);
        double x = 0;
        double bw = (Width - 4 * 6) / ResearchBranches.Length;
        foreach (var branch in ResearchBranches)
        {
            string b = branch;
            string label = (b == researchBranch ? "> " : "") + Lang.Get("createacolony:research-branch-" + b);
            composer.AddSmallButton(label, () => { researchBranch = b; Compose(); return true; }, ElementBounds.Fixed(x, y, bw, 28));
            x += bw + 6;
        }
        y += 36;
        var nodes = snapshot.Research.Where(r => r.Branch == researchBranch && r.State is not (2 or 3)).OrderBy(r => r.Tier).ToList();
        if (nodes.Count == 0) y = composer.Text(Lang.Get("createacolony:research-branch-empty"), GuiKit.Muted(), 0, y, Width);
        foreach (var r in nodes)
        {
            string title = Lang.Get("createacolony:research-" + r.Code) + "  " + Lang.Get("createacolony:research-tier-short", r.Tier);
            var titleFont = r.State == 4 ? GuiKit.Body().WithColor(GuiKit.GoodColor) : r.State == 0 ? GuiKit.Body().WithColor(GuiKit.MutedColor) : GuiKit.Body();
            composer.AddStaticText(title, titleFont, ElementBounds.Fixed(0, y + 4, Width - 120, 22));
            if (r.State == 4) composer.AddStaticText(Lang.Get("createacolony:research-done"), GuiKit.Body().WithColor(GuiKit.GoodColor).WithOrientation(EnumTextOrientation.Right), ElementBounds.Fixed(Width - 110, y + 4, 110, 22));
            else if (r.State == 1 && Officer)
            {
                string code = r.Code;
                composer.AddSmallButton(Lang.Get("createacolony:research-start"), () => SendText(ColonyAction.StartResearch, id, code), ElementBounds.Fixed(Width - 110, y, 110, 28));
            }
            y += 30;
            y = composer.Text(Lang.Get("createacolony:research-" + r.Code + "-desc"), GuiKit.Detail(), 10, y, Width - 10, 2);
            if (r.State != 4)
            {
                y = composer.Text(Lang.Get("createacolony:research-cost", r.PointsNeeded, r.Cost), GuiKit.Muted(), 10, y, Width - 10, 2);
                if (r.State == 0 && r.Note.Length > 0) y = composer.Text(Lang.Get("createacolony:research-locked-note", r.Note), GuiKit.Detail().WithColor(GuiKit.WarnColor), 10, y, Width - 10, 2);
            }
            y += 6;
        }
    }
}
